using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Services;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Integration;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Modules.Finance.Tests.QuickBooks;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Modules.Reports.Models;
using SMS.Modules.Reports.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;
using Xunit;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// SAP alignment (docs/finance/SAP-ALIGNMENT-PLAN.md) on the sales invoice: the line's tax code copied
/// from the order (S-3), the exchange-rate snapshot taken at issue (S-5), and cancelling an issued,
/// unpaid invoice by posting the opposite entries (S-7) — with the real invoice, payment, customer-ledger
/// and product-ledger services over one in-memory database, every call in a fresh scope as an HTTP
/// request would be, and QuickBooks behind a recording gateway.
/// </summary>
public class SalesInvoiceSapAlignmentTests
{
    private const int User = 42;
    private static readonly Guid PkrId = Guid.NewGuid();
    private static readonly Guid UsdId = Guid.NewGuid();

    // ── The world ────────────────────────────────────────────────────────────

    private sealed class Rates : IExchangeRateProvider
    {
        public Dictionary<(string, string), ExchangeRateQuote> Quotes { get; } = [];
        public List<(string From, string To, DateTime AsOf)> Calls { get; } = [];
        public bool Throw { get; set; }

        public void Add(string from, string to, decimal rate, bool inverted = false) =>
            Quotes[(from, to)] = new ExchangeRateQuote(from, to, rate, new DateTime(2026, 9, 1), inverted);

        public Task<ExchangeRateQuote?> GetRateAsync(string from, string to, DateTime asOf, CancellationToken ct = default)
        {
            Calls.Add((from, to, asOf));
            if (Throw) throw new InvalidOperationException("the rate table is unreachable");
            return Task.FromResult(Quotes.GetValueOrDefault((from, to)));
        }
    }

    private sealed class Codes : ICurrencyCodeLookup
    {
        public Dictionary<Guid, string> Known { get; } = [];
        public Task<string?> GetCodeAsync(Guid currencyId, CancellationToken ct = default) =>
            Task.FromResult<string?>(Known.GetValueOrDefault(currencyId));
    }

    private sealed class World
    {
        public string       DbName   { get; } = Guid.NewGuid().ToString();
        public Guid         Org      { get; } = Guid.NewGuid();
        public TestClock    Clock    { get; } = new();
        public FakeVariants Variants { get; } = new();
        public RecordingQuickBooksGateway Gateway { get; } = new();
        public Rates        Rates    { get; } = new();
        public List<Job>    Jobs     { get; } = [];
        public Dictionary<Guid, DeliveryForInvoicing> Deliveries { get; } = [];

        /// <summary>What the organization's base currency is; null for one that never set it.</summary>
        public Guid?  BaseCurrency     { get; set; } = PkrId;
        /// <summary>False builds the invoice service without any of the S-5 services, as an older harness does.</summary>
        public bool   CurrencyServices { get; set; } = true;
        public Codes? CurrencyCodes    { get; set; }

        public int Orders;
        public int DeliveriesMade;

        public Scope Open(IInterceptor? finance = null, IInterceptor? demand = null, Guid? org = null, bool superAdmin = false) =>
            new(this, org ?? Org, finance, demand, superAdmin);
    }

    private sealed class Scope : IAsyncDisposable
    {
        public Scope(World w, Guid org, IInterceptor? financeInterceptor, IInterceptor? demandInterceptor, bool superAdmin = false)
        {
            var tenant = new StaticTenantContext { OrganizationId = org, IsSuperAdmin = superAdmin };

            var financeOptions = new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(w.DbName);
            if (financeInterceptor is not null) financeOptions.AddInterceptors(financeInterceptor);
            var demandOptions = new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(w.DbName);
            if (demandInterceptor is not null) demandOptions.AddInterceptors(demandInterceptor);

            Db     = new FinanceDbContext(financeOptions.Options, tenant);
            Demand = new DemandDbContext(demandOptions.Options, tenant);

            var reader = new Mock<IDeliveryFulfillmentReader>();
            reader.Setup(r => r.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync((Guid id, CancellationToken _) => w.Deliveries.GetValueOrDefault(id));

            var lookups = new Mock<ILookupsService>();
            lookups.Setup(l => l.GetCurrencies()).Returns(
            [
                new CurrencyModel { Id = PkrId, Name = "Pakistani Rupee", Code = "PKR" },
                new CurrencyModel { Id = UsdId, Name = "US Dollar", Code = "USD" }
            ]);

            var jobs = new Mock<IBackgroundJobClient>();
            jobs.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()))
                .Callback<Job, IState>((job, _) => w.Jobs.Add(job)).Returns("fake-job-id");

            var orgCurrency = new Mock<IOrganizationCurrencyService>();
            orgCurrency.Setup(o => o.GetBaseCurrencyIdAsync(org)).ReturnsAsync(w.BaseCurrency);

            var publisher = new SalesInvoiceQuickBooksPublisher(
                new SalesInvoiceQuickBooksSource(Db, w.Gateway, NullLogger<SalesInvoiceQuickBooksSource>.Instance),
                NullLogger<SalesInvoiceQuickBooksPublisher>.Instance);

            Ledger        = new CustomerLedgerService(Db, w.Clock);
            ProductLedger = new ProductLedgerService(Db, w.Variants, w.Clock);
            Invoices      = new SalesInvoiceService(
                Db, Demand, reader.Object, Ledger, ProductLedger, Receivables.Names().Object, lookups.Object, jobs.Object,
                NullLogger<SalesInvoiceService>.Instance, w.Clock, publisher,
                exchangeRates: w.CurrencyServices ? w.Rates : null,
                orgCurrency:   w.CurrencyServices ? orgCurrency.Object : null,
                currencyCodes: w.CurrencyServices ? w.CurrencyCodes : null);
            Payments      = new CustomerPaymentService(Db, Ledger, Receivables.Names().Object, lookups.Object, w.Clock);
        }

        public FinanceDbContext       Db            { get; }
        public DemandDbContext        Demand        { get; }
        public CustomerLedgerService  Ledger        { get; }
        public ProductLedgerService   ProductLedger { get; }
        public SalesInvoiceService    Invoices      { get; }
        public CustomerPaymentService Payments      { get; }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Demand.DisposeAsync();
        }
    }

    /// <param name="Cost">What the ledger holds each unit of the line's variant at — the line's quantity is stocked at it.</param>
    private sealed record LineSpec(
        decimal Qty, decimal Price, decimal Disc = 0m, decimal Tax = 0m, Guid? TaxCodeUuid = null, string? TaxCode = null, decimal Cost = 10m);

    private sealed record Placed(Guid Uuid, string Number, Guid TraceId, Guid Customer, IReadOnlyList<Guid> LineUuids, IReadOnlyList<Guid> Variants);

    private static async Task<Placed> PlaceAsync(World w, Guid? currency = null, Guid? customer = null, LineSpec[]? lines = null)
    {
        lines ??= [new LineSpec(1m, 10m)];
        await using var s = w.Open();
        var order = new SaleOrder
        {
            SoNumber = $"SO-2026-{++w.Orders:00000}", TraceId = Guid.NewGuid(), PartnerId = customer ?? Guid.NewGuid(),
            OrderDate = new DateTime(2026, 9, 1), CurrencyId = currency ?? PkrId, Status = "FULFILLED", DeliveryMode = "SHIP", CreatedBy = 1
        };
        foreach (var l in lines)
            order.Lines.Add(new SaleOrderLine
            {
                VariantUuid = Guid.NewGuid(), Quantity = l.Qty, UnitPrice = l.Price, DiscountPercent = l.Disc, TaxPercent = l.Tax,
                TaxCodeUuid = l.TaxCodeUuid, TaxCode = l.TaxCode,
                LineTotal = SalesInvoiceTotals.LineTotal(l.Qty, l.Price, l.Disc, l.Tax),
                FulfilledQty = l.Qty, FulfillmentMode = "IN_STOCK", Status = "FULFILLED"
            });
        s.Demand.SaleOrders.Add(order);
        await s.Demand.SaveChangesAsync();

        var saved = order.Lines.OrderBy(l => l.Id).ToList();
        for (var i = 0; i < lines.Length; i++)
            await StockAsync(w, saved[i].VariantUuid, lines[i].Qty, lines[i].Cost);

        return new Placed(order.UUID, order.SoNumber, order.TraceId, order.PartnerId,
            [.. saved.Select(l => l.UUID)], [.. saved.Select(l => l.VariantUuid)]);
    }

    /// <summary>Goods received into the product ledger at a cost, as a GRN does.</summary>
    private static async Task StockAsync(World w, Guid variant, decimal qty, decimal cost)
    {
        if (!w.Variants.Products.ContainsKey(variant)) w.Variants.Products[variant] = Guid.NewGuid();
        await using var s = w.Open();
        await s.ProductLedger.AppendEntryAsync(new ProductLedgerPosting(
            variant, "PURCHASE", "IN", qty, cost, "GRN", Guid.NewGuid(), "GRN-STOCK", User, ProductUuid: w.Variants.Products[variant]));
    }

    /// <summary>A delivered delivery of (line, quantity) pairs; every line in full when none are given.</summary>
    private static Guid Deliver(World w, Placed order, params (int Line, decimal Qty)[] lines)
    {
        var uuid = Guid.NewGuid();
        IEnumerable<(int Line, decimal Qty)> which = lines.Length > 0 ? lines : FullLines(w, order);
        w.Deliveries[uuid] = new DeliveryForInvoicing(uuid, $"DLV-2026-{++w.DeliveriesMade:00000}", "DELIVERED", order.Uuid,
            [.. which.Select((l, i) => new DeliveredLineForInvoicing(
                Guid.NewGuid(), i + 1, order.LineUuids[l.Line], order.Variants[l.Line], $"Item {l.Line + 1}", l.Qty))]);
        return uuid;
    }

    private static IEnumerable<(int, decimal)> FullLines(World w, Placed order)
    {
        using var demand = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(w.DbName).Options,
            new StaticTenantContext { OrganizationId = w.Org });
        var quantities = demand.SaleOrderLines.AsNoTracking().Where(l => order.LineUuids.Contains(l.UUID))
            .ToDictionary(l => l.UUID, l => l.Quantity);
        return order.LineUuids.Select((id, i) => (i, quantities[id])).ToList();
    }

    private static async Task<Guid> RaiseAsync(World w, Guid delivery)
    {
        await using var s = w.Open();
        return (await s.Invoices.CreateFromFulfillmentAsync(delivery, User)).InvoiceUuid;
    }

    private static async Task<Guid> IssueAsync(World w, Guid delivery)
    {
        var id = await RaiseAsync(w, delivery);
        await using var s = w.Open();
        await s.Invoices.IssueAsync(id, User);
        return id;
    }

    private static async Task<SalesInvoiceDetailModel> CancelAsync(World w, Guid invoice, string? reason = "Billed in error", IInterceptor? finance = null, IInterceptor? demand = null)
    {
        await using var s = w.Open(finance, demand);
        return await s.Invoices.CancelAsync(invoice, reason, User);
    }

    private static async Task<SalesInvoice> InvoiceAsync(World w, Guid uuid)
    {
        await using var db = Receivables.Db(w.Org, w.DbName);
        return await db.SalesInvoices.AsNoTracking().Include(i => i.Lines).SingleAsync(i => i.UUID == uuid);
    }

    private static async Task<SalesInvoiceDetailModel> DetailAsync(World w, Guid uuid)
    {
        await using var s = w.Open();
        return (await s.Invoices.GetAsync(uuid))!;
    }

    private static async Task<List<CustomerLedgerEntry>> LedgerAsync(World w, Guid customer)
    {
        await using var db = Receivables.Db(w.Org, w.DbName);
        return await db.CustomerLedgerEntries.AsNoTracking().Where(e => e.PartnerId == customer).OrderBy(e => e.SequenceNo).ToListAsync();
    }

    private static async Task<List<ProductLedgerEntry>> ProductLedgerAsync(World w, Guid variant)
    {
        await using var db = Receivables.Db(w.Org, w.DbName);
        return await db.ProductLedgerEntries.AsNoTracking().Where(e => e.VariantUuid == variant).OrderBy(e => e.SequenceNo).ToListAsync();
    }

    private static async Task<List<SaleOrderLine>> SoLinesAsync(World w, Placed order)
    {
        await using var demand = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(w.DbName).Options,
            new StaticTenantContext { OrganizationId = w.Org });
        return await demand.SaleOrderLines.AsNoTracking().Where(l => order.LineUuids.Contains(l.UUID)).OrderBy(l => l.Id).ToListAsync();
    }

    private static async Task<int> ProductEntryCountAsync(World w)
    {
        await using var db = Receivables.Db(w.Org, w.DbName);
        return await db.ProductLedgerEntries.CountAsync();
    }

    // ── S-3: the tax code travels from the order line to the invoice line ────

    [Fact]
    public async Task Raising_an_invoice_copies_each_lines_tax_code_with_its_rate_and_leaves_a_line_without_one_alone()
    {
        var w   = new World();
        var gst = Guid.NewGuid();
        var order = await PlaceAsync(w, lines:
        [
            new LineSpec(10m, 40m, Disc: 10m, Tax: 17m, TaxCodeUuid: gst, TaxCode: "GST17"),
            new LineSpec(3m, 15.5m, Tax: 5m)
        ]);

        var invoice = await RaiseAsync(w, Deliver(w, order));

        var lines = (await InvoiceAsync(w, invoice)).Lines.OrderBy(l => l.LineNo).ToList();
        (lines[0].TaxCodeUuid, lines[0].TaxCode, lines[0].TaxPercent).Should().Be((gst, "GST17", 17m));
        (lines[1].TaxCodeUuid, lines[1].TaxCode, lines[1].TaxPercent).Should().Be(((Guid?)null, (string?)null, 5m));

        var detail = await DetailAsync(w, invoice);
        detail.Lines[0].TaxCodeUuid.Should().Be(gst);
        detail.Lines[0].TaxCode.Should().Be("GST17");
        detail.Lines[1].TaxCode.Should().BeNull();
        detail.GrandTotal.Should().Be(SalesInvoiceTotals.Header([(10m, 40m, 10m, 17m), (3m, 15.5m, 0m, 5m)]).Grand,
            "the code changes nothing about the arithmetic");
    }

    // ── S-5: the exchange-rate snapshot at issue ─────────────────────────────

    [Fact]
    public async Task Issuing_in_the_base_currency_snapshots_a_rate_of_exactly_one_and_asks_for_no_rate()
    {
        var w = new World();
        var order = await PlaceAsync(w, lines: [new LineSpec(3m, 12.37m, Tax: 17m)]);

        var invoice = await IssueAsync(w, Deliver(w, order));

        var stored = await InvoiceAsync(w, invoice);
        (stored.ExchangeRate, stored.BaseCurrencyCode, stored.BaseGrandTotal).Should().Be((1m, "PKR", stored.GrandTotal));
        w.Rates.Calls.Should().BeEmpty();

        var detail = await DetailAsync(w, invoice);
        (detail.ExchangeRate, detail.BaseCurrencyCode, detail.BaseGrandTotal).Should().Be((1m, "PKR", stored.GrandTotal));
    }

    [Fact]
    public async Task Issuing_in_a_foreign_currency_snapshots_the_rate_on_the_invoice_date_and_the_base_total()
    {
        var w = new World();
        w.Rates.Add("USD", "PKR", 278.5m);
        var order = await PlaceAsync(w, currency: UsdId, lines: [new LineSpec(3m, 12.37m)]);

        var invoice = await IssueAsync(w, Deliver(w, order));

        var stored = await InvoiceAsync(w, invoice);
        stored.CurrencyCode.Should().Be("USD");
        stored.GrandTotal.Should().Be(37.11m);
        stored.ExchangeRate.Should().Be(278.5m);
        stored.BaseCurrencyCode.Should().Be("PKR");
        stored.BaseGrandTotal.Should().Be(10335.14m, "37.11 × 278.5 = 10335.135, rounded half away from zero");
        w.Rates.Calls.Should().ContainSingle().Which.Should().Be(("USD", "PKR", new DateTime(2026, 9, 20)));
    }

    [Fact]
    public async Task A_rate_with_more_places_than_the_column_keeps_is_stored_to_eight_and_the_base_total_is_worked_from_that()
    {
        var w = new World();
        w.Rates.Add("USD", "PKR", 1m / 0.0036m, inverted: true);   // 277.7777…
        var order = await PlaceAsync(w, currency: UsdId, lines: [new LineSpec(1m, 100m)]);

        var invoice = await IssueAsync(w, Deliver(w, order));

        var stored = await InvoiceAsync(w, invoice);
        stored.ExchangeRate.Should().Be(277.77777778m);
        stored.BaseGrandTotal.Should().Be(27777.78m);
    }

    [Fact]
    public async Task With_no_rate_on_file_the_invoice_is_still_issued_with_no_rate_but_keeps_the_base_currency_it_was_issued_under()
    {
        // Consistency finding: the supplier invoice records its base currency when no rate was on file (so it can
        // say "no USD → PKR rate was on file"); the sales invoice recorded nothing, so it could not say so.
        var w = new World();
        var order = await PlaceAsync(w, currency: UsdId, lines: [new LineSpec(2m, 50m)]);

        var invoice = await IssueAsync(w, Deliver(w, order));

        var stored = await InvoiceAsync(w, invoice);
        stored.Status.Should().Be("ISSUED");
        (stored.ExchangeRate, stored.BaseCurrencyCode, stored.BaseGrandTotal).Should().Be(((decimal?)null, "PKR", (decimal?)null));
        (await LedgerAsync(w, order.Customer)).Should().ContainSingle().Which.DebitAmount.Should().Be(100m);

        var detail = await DetailAsync(w, invoice);
        (detail.ExchangeRate, detail.BaseCurrencyCode, detail.BaseGrandTotal).Should().Be(((decimal?)null, "PKR", (decimal?)null));
    }

    [Fact]
    public async Task With_no_base_currency_configured_the_invoice_is_still_issued_with_no_snapshot()
    {
        var w = new World { BaseCurrency = null };
        var order = await PlaceAsync(w, lines: [new LineSpec(2m, 50m)]);

        var invoice = await IssueAsync(w, Deliver(w, order));

        var stored = await InvoiceAsync(w, invoice);
        stored.Status.Should().Be("ISSUED");
        stored.ExchangeRate.Should().BeNull();
        stored.BaseCurrencyCode.Should().BeNull();
        stored.BaseGrandTotal.Should().BeNull();
    }

    [Fact]
    public async Task A_rate_lookup_that_fails_does_not_stop_the_invoice_being_issued()
    {
        var w = new World();
        w.Rates.Throw = true;
        var order = await PlaceAsync(w, currency: UsdId, lines: [new LineSpec(2m, 50m)]);

        var invoice = await IssueAsync(w, Deliver(w, order));

        var stored = await InvoiceAsync(w, invoice);
        stored.Status.Should().Be("ISSUED");
        (stored.ExchangeRate, stored.BaseCurrencyCode, stored.BaseGrandTotal).Should().Be(((decimal?)null, "PKR", (decimal?)null),
            "a rate that could not be read is a rate not on file; the base currency was known all the same");
        (await LedgerAsync(w, order.Customer)).Should().ContainSingle();
    }

    [Fact]
    public async Task Without_an_exchange_rate_provider_a_foreign_invoice_keeps_its_base_currency_and_no_rate()
    {
        var w = new World();
        var order = await PlaceAsync(w, currency: UsdId, lines: [new LineSpec(2m, 50m)]);
        var draft = await RaiseAsync(w, Deliver(w, order));

        await using (var s = w.Open())
        {
            var orgCurrency = new Mock<IOrganizationCurrencyService>();
            orgCurrency.Setup(o => o.GetBaseCurrencyIdAsync(w.Org)).ReturnsAsync(PkrId);
            var lookups = new Mock<ILookupsService>();
            lookups.Setup(l => l.GetCurrencies()).Returns(
                [new CurrencyModel { Id = PkrId, Code = "PKR" }, new CurrencyModel { Id = UsdId, Code = "USD" }]);
            await new SalesInvoiceService(
                    s.Db, s.Demand, Mock.Of<IDeliveryFulfillmentReader>(), s.Ledger, s.ProductLedger, Receivables.Names().Object,
                    lookups.Object, Mock.Of<IBackgroundJobClient>(), NullLogger<SalesInvoiceService>.Instance, w.Clock,
                    exchangeRates: null, orgCurrency: orgCurrency.Object)
                .IssueAsync(draft, User);
        }

        var stored = await InvoiceAsync(w, draft);
        (stored.ExchangeRate, stored.BaseCurrencyCode, stored.BaseGrandTotal).Should().Be(((decimal?)null, "PKR", (decimal?)null));
    }

    [Fact]
    public async Task Without_the_currency_services_an_invoice_is_issued_as_it_always_was()
    {
        var w = new World { CurrencyServices = false };
        var order = await PlaceAsync(w, lines: [new LineSpec(2m, 50m)]);

        var invoice = await IssueAsync(w, Deliver(w, order));

        var stored = await InvoiceAsync(w, invoice);
        stored.Status.Should().Be("ISSUED");
        (stored.ExchangeRate, stored.BaseCurrencyCode, stored.BaseGrandTotal).Should().Be(((decimal?)null, (string?)null, (decimal?)null));
    }

    [Fact]
    public async Task The_base_currencys_code_comes_from_the_currency_code_lookup_when_there_is_one()
    {
        var eur = Guid.NewGuid();   // not in the Lookups catalog the service also holds
        var w = new World { BaseCurrency = eur, CurrencyCodes = new Codes() };
        w.CurrencyCodes.Known[eur] = " EUR ";
        w.Rates.Add("PKR", "EUR", 0.0031m);
        var order = await PlaceAsync(w, lines: [new LineSpec(1m, 1000m)]);

        var invoice = await IssueAsync(w, Deliver(w, order));

        var stored = await InvoiceAsync(w, invoice);
        (stored.ExchangeRate, stored.BaseCurrencyCode, stored.BaseGrandTotal).Should().Be((0.0031m, "EUR", 3.10m));
    }

    [Fact]
    public async Task A_lost_race_while_issuing_does_not_ask_for_the_rate_twice()
    {
        var w = new World();
        w.Rates.Add("USD", "PKR", 280m);
        var order = await PlaceAsync(w, currency: UsdId, lines: [new LineSpec(1m, 10m)]);
        var invoice = await RaiseAsync(w, Deliver(w, order));

        var race = new LoseTheRaceOnce(db => db.ChangeTracker.Entries<SalesInvoice>().Any(e => e.State == EntityState.Modified), () => Task.CompletedTask);
        await using (var s = w.Open(race))
            await s.Invoices.IssueAsync(invoice, User);

        race.Fired.Should().BeTrue();
        w.Rates.Calls.Should().ContainSingle();
        (await InvoiceAsync(w, invoice)).BaseGrandTotal.Should().Be(2800m);
    }

    // ── S-7: cancelling an issued invoice ────────────────────────────────────

    /// <summary>
    /// Variant A holds 10 at 25 and 5 at 31 (15 worth 405, so 27 a unit); variant B holds 3 at 7. The order
    /// sells 10 of A at 40 less 10% plus GST17 and all 3 of B at 15.50: 467.70 in all.
    /// </summary>
    private static async Task<(World W, Placed Order, Guid Invoice, Guid Delivery)> IssuedAsync()
    {
        var w = new World();
        var order = await PlaceAsync(w, lines:
        [
            new LineSpec(10m, 40m, Disc: 10m, Tax: 17m, TaxCodeUuid: Guid.NewGuid(), TaxCode: "GST17", Cost: 25m),
            new LineSpec(3m, 15.5m, Cost: 7m)
        ]);
        await StockAsync(w, order.Variants[0], 5m, 31m);

        var delivery = Deliver(w, order);
        var invoice  = await IssueAsync(w, delivery);
        return (w, order, invoice, delivery);
    }

    [Fact]
    public async Task Cancelling_an_issued_invoice_reverses_it_on_every_ledger_and_tells_everyone()
    {
        var (w, order, invoice, _) = await IssuedAsync();
        var number = (await InvoiceAsync(w, invoice)).InvoiceNumber;
        (await InvoiceAsync(w, invoice)).GrandTotal.Should().Be(467.70m, "the scenario is what it claims");
        (await SoLinesAsync(w, order)).Select(l => l.InvoicedQty).Should().Equal(10m, 3m);
        w.Jobs.Clear();
        var cancelledAt = TestClock.Start.AddDays(2);
        w.Clock.Value = cancelledAt;

        var result = await CancelAsync(w, invoice, "  Billed to the wrong customer  ");

        // The invoice.
        var stored = await InvoiceAsync(w, invoice);
        stored.Status.Should().Be("CANCELLED");
        stored.BalanceDue.Should().Be(0m);
        stored.AmountPaid.Should().Be(0m);
        stored.GrandTotal.Should().Be(467.70m, "the document still says what it billed");
        stored.CancelledAt.Should().Be(cancelledAt);
        stored.CancelledBy.Should().Be(User);
        stored.CancellationReason.Should().Be("Billed to the wrong customer");
        stored.ModifiedBy.Should().Be(User);
        stored.ModifiedDate.Should().Be(cancelledAt, "the concurrency token moves on every write");

        // What the caller gets back.
        result.Status.Should().Be("CANCELLED");
        result.BalanceDue.Should().Be(0m);
        result.CancelledAt.Should().Be(cancelledAt);
        result.CancelledBy.Should().Be(User);
        result.CancellationReason.Should().Be("Billed to the wrong customer");

        // The customer's ledger: the debit stays, and the opposite entry offsets it.
        var ledger = await LedgerAsync(w, order.Customer);
        ledger.Select(e => (e.SequenceNo, e.EntryType, e.DebitAmount, e.CreditAmount, e.RunningBalance)).Should().Equal(
            (1, "INVOICE", 467.70m, 0m, 467.70m),
            (2, "CREDIT_NOTE", 0m, 467.70m, 0m));
        var credit = ledger[1];
        credit.CurrencyCode.Should().Be("PKR");
        credit.ReferenceType.Should().Be("SalesInvoice");
        credit.ReferenceId.Should().Be(invoice);
        credit.ReferenceNumber.Should().Be(number);
        credit.EntryDate.Should().Be(cancelledAt);
        credit.CreatedBy.Should().Be(User);
        credit.Narration.Should().Contain(number).And.Contain("Billed to the wrong customer");

        // The product ledger: what went out comes back in, at the cost it went out at.
        var a = await ProductLedgerAsync(w, order.Variants[0]);
        a.Select(e => (e.EntryType, e.Direction, e.Quantity, e.UnitCost, e.TotalCost, e.RunningQty, e.RunningValue)).Should().Equal(
            ("PURCHASE", "IN", 10m, 25m, 250m, 10m, 250m),
            ("PURCHASE", "IN", 5m, 31m, 155m, 15m, 405m),
            ("SALE", "OUT", 10m, 27m, 270m, 5m, 135m),
            ("RETURN_IN", "IN", 10m, 27m, 270m, 15m, 405m));
        var back = a[^1];
        (back.ReferenceType, back.ReferenceId, back.ReferenceNumber, back.PartnerId, back.EntryDate)
            .Should().Be(("SalesInvoice", invoice, number, (Guid?)order.Customer, cancelledAt));

        var b = await ProductLedgerAsync(w, order.Variants[1]);
        b.Select(e => (e.EntryType, e.Quantity, e.UnitCost, e.RunningQty, e.RunningValue)).Should().Equal(
            ("PURCHASE", 3m, 7m, 3m, 21m),
            ("SALE", 3m, 7m, 0m, 0m),
            ("RETURN_IN", 3m, 7m, 3m, 21m));

        // The order's counters.
        (await SoLinesAsync(w, order)).Select(l => l.InvoicedQty).Should().Equal(0m, 0m);

        // The order's timeline.
        var job = w.Jobs.Should().ContainSingle(j => j.Method.Name == "AppendAsync").Subject;
        job.Args[0].Should().Be(order.TraceId);
        var evt = (TimelineEvent)job.Args[1];
        evt.EventType.Should().Be(SaleOrderTimelineEventTypes.SoInvoiceCancelled).And.Be("SO_INVOICE_CANCELLED");
        evt.DocumentId.Should().Be(order.Uuid);
        evt.DocumentNumber.Should().Be(order.Number);
        evt.PerformedBy.Should().Be(User);
        evt.Notes.Should().Contain(number).And.Contain("467.70 PKR").And.Contain("Billed to the wrong customer");
        job.Args[4].Should().Be(w.Org);

        // QuickBooks: issued then voided.
        w.Gateway.Voids.Should().Equal(invoice.ToString());
        w.Gateway.Calls[^1].Should().Be($"Void:{invoice}");
    }

    private sealed class SaveRecorder : SaveChangesInterceptor
    {
        public List<(int ModifiedInvoices, int CustomerEntries, int ProductEntries)> Saves { get; } = [];

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            var entries = eventData.Context!.ChangeTracker.Entries().ToList();
            Saves.Add((
                entries.Count(e => e.Entity is SalesInvoice && e.State == EntityState.Modified),
                entries.Count(e => e.Entity is CustomerLedgerEntry && e.State == EntityState.Added),
                entries.Count(e => e.Entity is ProductLedgerEntry && e.State == EntityState.Added)));
            return base.SavingChangesAsync(eventData, result, ct);
        }
    }

    [Fact]
    public async Task The_status_the_credit_and_the_stock_coming_back_are_one_save()
    {
        var (w, _, invoice, _) = await IssuedAsync();
        var recorder = new SaveRecorder();

        await CancelAsync(w, invoice, finance: recorder);

        recorder.Saves.Should().ContainSingle("Finance saves exactly once")
            .Which.Should().Be((ModifiedInvoices: 1, CustomerEntries: 1, ProductEntries: 2));
    }

    [Fact]
    public async Task An_overdue_invoice_can_be_cancelled_and_is_voided_in_quickbooks_too()
    {
        var (w, _, invoice, _) = await IssuedAsync();
        w.Clock.Value = TestClock.Start.AddDays(45);
        await using (var auditor = Receivables.Auditor(w.DbName))
            (await new InvoiceOverdueJob(auditor, NullLogger<InvoiceOverdueJob>.Instance, w.Clock).SweepAsync()).Should().Be(1);
        (await InvoiceAsync(w, invoice)).Status.Should().Be("OVERDUE");

        var result = await CancelAsync(w, invoice);

        result.Status.Should().Be("CANCELLED");
        w.Gateway.Voids.Should().Equal(invoice.ToString());
    }

    [Fact]
    public async Task A_draft_is_not_cancelled_but_deleted()
    {
        var w = new World();
        var order = await PlaceAsync(w, lines: [new LineSpec(1m, 10m)]);
        var draft = await RaiseAsync(w, Deliver(w, order));

        var act = async () => await CancelAsync(w, draft);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*DRAFT*nothing to reverse*Delete the draft*");
        (await InvoiceAsync(w, draft)).Status.Should().Be("DRAFT");
        (await LedgerAsync(w, order.Customer)).Should().BeEmpty();
        w.Gateway.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_invoice_cannot_be_cancelled_twice()
    {
        var (w, order, invoice, _) = await IssuedAsync();
        await CancelAsync(w, invoice);
        var entries = await ProductEntryCountAsync(w);

        var act = async () => await CancelAsync(w, invoice);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*already CANCELLED*");
        (await LedgerAsync(w, order.Customer)).Should().HaveCount(2, "one debit, one credit — not a second credit");
        (await ProductEntryCountAsync(w)).Should().Be(entries);
        (await SoLinesAsync(w, order)).Select(l => l.InvoicedQty).Should().Equal(0m, 0m);
        w.Gateway.Voids.Should().ContainSingle();
    }

    [Theory]
    [InlineData(100, "PARTIALLY_PAID")]
    [InlineData(467.70, "PAID")]
    public async Task An_invoice_with_money_paid_against_it_cannot_be_cancelled(double paid, string status)
    {
        var (w, order, invoice, _) = await IssuedAsync();
        await using (var s = w.Open())
            await s.Payments.RecordPaymentAsync(order.Customer, (decimal)paid, "BANK_TRANSFER", new CustomerPaymentDetails("PKR"), User);
        (await InvoiceAsync(w, invoice)).Status.Should().Be(status);
        var before = await LedgerAsync(w, order.Customer);
        var entries = await ProductEntryCountAsync(w);

        var act = async () => await CancelAsync(w, invoice);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage($"*{status}*has been paid against it*");
        var stored = await InvoiceAsync(w, invoice);
        stored.Status.Should().Be(status);
        stored.CancelledAt.Should().BeNull();
        (await LedgerAsync(w, order.Customer)).Should().HaveCount(before.Count);
        (await ProductEntryCountAsync(w)).Should().Be(entries);
        w.Gateway.Voids.Should().BeEmpty();
    }

    [Fact]
    public async Task An_invoice_whose_cheque_bounced_owes_everything_again_and_can_be_cancelled()
    {
        var (w, order, invoice, _) = await IssuedAsync();
        CustomerPaymentRecorded cheque;
        await using (var s = w.Open())
            cheque = await s.Payments.RecordPaymentAsync(order.Customer, 467.70m, "CHEQUE", new CustomerPaymentDetails("PKR", ChequeNumber: "CHQ-1"), User);
        await using (var s = w.Open())
            await s.Payments.BounceAsync(cheque.PaymentUuid, "Insufficient funds", User);
        (await InvoiceAsync(w, invoice)).Should().Match<SalesInvoice>(i => i.Status == "ISSUED" && i.AmountPaid == 0m);

        var result = await CancelAsync(w, invoice);

        result.Status.Should().Be("CANCELLED");
        result.Payments.Should().ContainSingle("the bounced cheque stays in the invoice's history").Which.PaymentStatus.Should().Be("BOUNCED");
        (await LedgerAsync(w, order.Customer)).Select(e => e.EntryType).Should().Equal("INVOICE", "PAYMENT", "PAYMENT", "CREDIT_NOTE");
        (await LedgerAsync(w, order.Customer))[^1].RunningBalance.Should().Be(0m);
    }

    [Fact]
    public async Task A_payment_still_applied_to_the_invoice_stops_the_cancellation_even_if_the_balance_says_otherwise()
    {
        // Belt and braces: the allocation row of a payment that still stands is checked, not only AmountPaid.
        var w = new World();
        var customer = Guid.NewGuid();
        var invoice  = Receivables.Invoice(w.Org, customer, "SINV-20260920-0009", new DateTime(2026, 9, 20), 100m);
        var payment  = new CustomerPayment
        {
            UUID = Guid.NewGuid(), OrganizationId = w.Org, PartnerId = customer, PartnerName = "Acme", PaymentNumber = "CPAY-1",
            PaymentDate = new DateTime(2026, 9, 20), Amount = 50m, PaymentMethod = "CASH", CreatedBy = 1, CreatedDate = new DateTime(2026, 9, 20)
        };
        payment.Allocations.Add(new PaymentAllocation
        {
            UUID = Guid.NewGuid(), OrganizationId = w.Org, SalesInvoice = invoice, AllocatedAmount = 50m, AllocatedAt = new DateTime(2026, 9, 20), AllocatedBy = 1
        });
        await Receivables.Seed(Receivables.Db(w.Org, w.DbName), invoice, payment);

        var act = async () => await CancelAsync(w, invoice.UUID);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*customer payment applied*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_reason_is_required(string? reason)
    {
        var (w, _, invoice, _) = await IssuedAsync();

        var act = async () => await CancelAsync(w, invoice, reason);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*why the invoice is being cancelled*");
        (await InvoiceAsync(w, invoice)).Status.Should().Be("ISSUED");
    }

    [Fact]
    public async Task A_reason_may_be_500_characters_and_no_more()
    {
        var (w, _, invoice, _) = await IssuedAsync();

        var tooLong = async () => await CancelAsync(w, invoice, new string('x', 501));
        (await tooLong.Should().ThrowAsync<BadRequestException>()).WithMessage("*longer than 500*");
        (await InvoiceAsync(w, invoice)).Status.Should().Be("ISSUED");

        var result = await CancelAsync(w, invoice, new string('x', 500));
        result.CancellationReason.Should().HaveLength(500);
    }

    [Fact]
    public async Task An_unknown_a_deleted_or_another_organizations_invoice_is_not_found()
    {
        var (w, _, invoice, _) = await IssuedAsync();

        var unknown = async () => await CancelAsync(w, Guid.NewGuid());
        await unknown.Should().ThrowAsync<NotFoundException>();

        await using (var other = w.Open(org: Guid.NewGuid()))
        {
            var foreign = async () => await other.Invoices.CancelAsync(invoice, "mine now", User);
            await foreign.Should().ThrowAsync<NotFoundException>();
        }
        (await InvoiceAsync(w, invoice)).Status.Should().Be("ISSUED");

        var draftWorld = new World();
        var order = await PlaceAsync(draftWorld, lines: [new LineSpec(1m, 10m)]);
        var draft = await RaiseAsync(draftWorld, Deliver(draftWorld, order));
        await using (var s = draftWorld.Open())
            await s.Invoices.DeleteAsync(draft, User);
        var deleted = async () => await CancelAsync(draftWorld, draft);
        await deleted.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task A_payment_that_lands_while_the_invoice_is_being_cancelled_wins_and_the_cancellation_is_refused()
    {
        var (w, order, invoice, _) = await IssuedAsync();

        // Another request applies a payment between the cancellation's read and its save; that save then
        // finds the invoice's token moved on (as SQL Server would report it), and the retry re-reads.
        var race = new LoseTheRaceOnce(
            db => db.ChangeTracker.Entries<SalesInvoice>().Any(e => e.State == EntityState.Modified && e.Entity.Status == "CANCELLED"),
            async () =>
            {
                await using var s = w.Open();
                await s.Payments.RecordPaymentAsync(order.Customer, 100m, "CASH", new CustomerPaymentDetails("PKR"), User);
            },
            asConcurrencyConflict: true);

        var act = async () => await CancelAsync(w, invoice, finance: race);

        race.Fired.Should().BeFalse("not yet");
        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*PARTIALLY_PAID*");
        race.Fired.Should().BeTrue();
        (await InvoiceAsync(w, invoice)).Status.Should().Be("PARTIALLY_PAID");
        (await LedgerAsync(w, order.Customer)).Select(e => e.EntryType).Should().Equal("INVOICE", "PAYMENT");
        (await ProductLedgerAsync(w, order.Variants[0])).Should().NotContain(e => e.EntryType == "RETURN_IN");
        w.Gateway.Voids.Should().BeEmpty();
    }

    [Fact]
    public async Task A_write_made_from_a_read_taken_before_the_cancellation_fails_rather_than_overwrite_it()
    {
        var (w, _, invoice, _) = await IssuedAsync();
        await using var stale = Receivables.Db(w.Org, w.DbName);
        var before = await stale.SalesInvoices.SingleAsync(i => i.UUID == invoice);

        w.Clock.Value = TestClock.Start.AddDays(1);
        await CancelAsync(w, invoice);

        before.Status = "PARTIALLY_PAID";
        before.ModifiedDate = TestClock.Start.AddDays(2);
        var act = async () => await stale.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        (await InvoiceAsync(w, invoice)).Status.Should().Be("CANCELLED");
    }

    [Fact]
    public async Task A_failure_bringing_the_orders_counters_down_does_not_undo_the_cancellation()
    {
        var (w, order, invoice, _) = await IssuedAsync();

        var result = await CancelAsync(w, invoice, demand: new AlwaysFail());

        result.Status.Should().Be("CANCELLED");
        (await LedgerAsync(w, order.Customer)).Should().HaveCount(2);
        (await SoLinesAsync(w, order)).Select(l => l.InvoicedQty).Should().Equal(new[] { 10m, 3m }, "logged for someone to reconcile");
        w.Gateway.Voids.Should().ContainSingle("the void still goes out");
    }

    [Fact]
    public async Task An_invoice_issued_before_the_product_ledger_existed_is_cancelled_with_nothing_to_take_back()
    {
        var w = new World();
        var customer = Guid.NewGuid();
        var invoice  = Receivables.Invoice(w.Org, customer, "SINV-20260801-0001", new DateTime(2026, 8, 1), 250m);
        invoice.Lines.Add(new SalesInvoiceLine { UUID = Guid.NewGuid(), LineNo = 1, VariantUuid = Guid.NewGuid(), SoLineUuid = Guid.NewGuid(), Description = "Old", Quantity = 1m, UnitPrice = 250m, LineTotal = 250m });
        await Receivables.Seed(Receivables.Db(w.Org, w.DbName), invoice);

        var result = await CancelAsync(w, invoice.UUID);

        result.Status.Should().Be("CANCELLED");
        (await ProductEntryCountAsync(w)).Should().Be(0);
        (await LedgerAsync(w, customer)).Should().ContainSingle().Which.Should().Match<CustomerLedgerEntry>(e =>
            e.EntryType == "CREDIT_NOTE" && e.CreditAmount == 250m && e.RunningBalance == -250m);
    }

    [Fact]
    public async Task A_super_admin_signed_in_to_another_organization_cannot_cancel_an_invoice_into_its_own_books()
    {
        // Reviewer finding (raised by the security audit): a super admin bypasses the tenant filter, found the
        // invoice by uuid and cancelled it — and the credit note and the stock coming back were stamped with the
        // super admin's own organization, leaving the invoice's customer owing a cancelled invoice.
        var (w, order, invoice, _) = await IssuedAsync();
        var entries = await ProductEntryCountAsync(w);

        await using (var admin = w.Open(org: Guid.NewGuid(), superAdmin: true))
        {
            var act = async () => await admin.Invoices.CancelAsync(invoice, "Cancelled from another organization", User);
            await act.Should().ThrowAsync<NotFoundException>("to every action that books something, another organization's invoice is not found");
        }

        (await InvoiceAsync(w, invoice)).Status.Should().Be("ISSUED");
        await using (var auditor = Receivables.Auditor(w.DbName))
        {
            (await auditor.CustomerLedgerEntries.CountAsync()).Should().Be(1, "only the issue's debit, in the invoice's own organization");
            (await auditor.ProductLedgerEntries.CountAsync()).Should().Be(entries);
        }
        w.Gateway.Voids.Should().BeEmpty();

        // In its own organization the same super admin cancels it like anyone else.
        await using (var admin = w.Open(superAdmin: true))
            (await admin.Invoices.CancelAsync(invoice, "Billed in error", User)).Status.Should().Be("CANCELLED");
        (await LedgerAsync(w, order.Customer)).Select(e => e.EntryType).Should().Equal("INVOICE", "CREDIT_NOTE");
    }

    [Fact]
    public async Task A_super_admin_signed_in_to_another_organization_cannot_issue_an_invoice_into_its_own_books()
    {
        var w = new World();
        var order = await PlaceAsync(w, lines: [new LineSpec(2m, 50m)]);
        var draft = await RaiseAsync(w, Deliver(w, order));

        await using (var admin = w.Open(org: Guid.NewGuid(), superAdmin: true))
        {
            var act = async () => await admin.Invoices.IssueAsync(draft, User);
            await act.Should().ThrowAsync<NotFoundException>();
        }

        (await InvoiceAsync(w, draft)).Status.Should().Be("DRAFT");
        await using var auditor = Receivables.Auditor(w.DbName);
        (await auditor.CustomerLedgerEntries.CountAsync()).Should().Be(0);
        (await auditor.ProductLedgerEntries.CountAsync(e => e.EntryType == "SALE")).Should().Be(0);
    }

    // ── After a cancellation ─────────────────────────────────────────────────

    [Fact]
    public async Task The_delivery_can_be_invoiced_again_in_full_and_the_new_invoice_issued()
    {
        var (w, order, invoice, delivery) = await IssuedAsync();
        var first = await InvoiceAsync(w, invoice);
        await CancelAsync(w, invoice);

        Guid again;
        await using (var s = w.Open())
        {
            var created = await s.Invoices.CreateFromFulfillmentAsync(delivery, User);
            created.AlreadyExisted.Should().BeFalse("the cancelled invoice no longer holds the delivery");
            created.InvoiceUuid.Should().NotBe(invoice);
            created.InvoiceNumber.Should().NotBe(first.InvoiceNumber, "a cancelled invoice keeps its number");
            created.GrandTotal.Should().Be(467.70m);
            again = created.InvoiceUuid;
        }

        (await InvoiceAsync(w, again)).Lines.Select(l => l.Quantity).Should().BeEquivalentTo(new[] { 10m, 3m }, "the full quantity is billable again");

        await using (var s = w.Open())
            (await s.Invoices.IssueAsync(again, User)).Status.Should().Be("ISSUED");

        (await SoLinesAsync(w, order)).Select(l => l.InvoicedQty).Should().Equal(10m, 3m);
        (await LedgerAsync(w, order.Customer)).Select(e => (e.EntryType, e.RunningBalance)).Should().Equal(
            ("INVOICE", 467.70m), ("CREDIT_NOTE", 0m), ("INVOICE", 467.70m));
        (await ProductLedgerAsync(w, order.Variants[0]))[^1].Should().Match<ProductLedgerEntry>(e =>
            e.EntryType == "SALE" && e.TotalCost == 270m && e.RunningQty == 5m, "the stock that came back is sold again at the same cost");
    }

    [Fact]
    public async Task A_cancelled_invoice_is_not_a_receivable_to_aging_to_a_payment_or_to_the_overdue_sweep_and_the_statement_nets_it_out()
    {
        var w = new World();
        var customer = Guid.NewGuid();
        var order = await PlaceAsync(w, customer: customer, lines: [new LineSpec(10m, 100m)]);
        var kept      = await IssueAsync(w, Deliver(w, order, (0, 4m)));     // 400
        var cancelled = await IssueAsync(w, Deliver(w, order, (0, 6m)));     // 600
        await CancelAsync(w, cancelled);

        // R3 aging: only the invoice that stands.
        var reports = Reports(w);
        var aging = await reports.GetAgingReceivablesForExportAsync(new AgingReceivablesFilter { AsOf = new DateTime(2026, 9, 20) });
        aging.Invoices.Select(i => i.InvoiceUuid).Should().Equal(kept);
        aging.Totals.Should().ContainSingle().Which.Total.Should().Be(400m);

        // R2 statement: invoice, invoice, credit note — closing at what is really owed.
        var statement = await reports.GetCustomerLedgerForExportAsync(new CustomerLedgerReportFilter { PartnerId = customer });
        statement.Entries.Select(e => (e.EntryType, e.Balance)).Should().Equal(("INVOICE", 400m), ("INVOICE", 1000m), ("CREDIT_NOTE", 400m));
        statement.Summaries.Should().ContainSingle().Which.ClosingBalance.Should().Be(400m);

        // A payment FIFO-applies to the invoice that stands and never to the cancelled one.
        CustomerPaymentRecorded paid;
        await using (var s = w.Open())
            paid = await s.Payments.RecordPaymentAsync(customer, 1000m, "BANK_TRANSFER", new CustomerPaymentDetails("PKR"), User);
        paid.Allocations.Should().ContainSingle().Which.InvoiceUuid.Should().Be(kept);
        paid.UnallocatedAmount.Should().Be(600m, "held on account");
        (await InvoiceAsync(w, cancelled)).Should().Match<SalesInvoice>(i => i.Status == "CANCELLED" && i.AmountPaid == 0m && i.BalanceDue == 0m);

        // The overdue sweep, long after the due date, leaves it alone.
        w.Clock.Value = TestClock.Start.AddDays(90);
        await using (var auditor = Receivables.Auditor(w.DbName))
            await new InvoiceOverdueJob(auditor, NullLogger<InvoiceOverdueJob>.Instance, w.Clock).SweepAsync();
        (await InvoiceAsync(w, cancelled)).Status.Should().Be("CANCELLED");

        // And the customer's books hold together the way every receivables test holds them to.
        await using var db = Receivables.Db(w.Org, w.DbName);
        var books = new Books(customer,
            await db.SalesInvoices.AsNoTracking().Where(i => i.PartnerId == customer && !i.IsDelete).OrderBy(i => i.Id).ToListAsync(),
            await db.CustomerPayments.AsNoTracking().Include(p => p.Allocations).Where(p => p.PartnerId == customer).OrderBy(p => p.Id).ToListAsync(),
            await db.CustomerLedgerEntries.AsNoTracking().Where(e => e.PartnerId == customer).OrderBy(e => e.SequenceNo).ToListAsync());
        ReceivablesInvariants.Violations(books, w.Clock.Value.Date, afterOverdueSweep: true).Should().BeEmpty();
        books.Ledger[^1].RunningBalance.Should().Be(-600m, "the 600 the customer paid for the cancelled invoice's goods is on account");
    }

    [Fact]
    public async Task A_cancelled_sale_is_not_profit_and_the_variant_is_worth_what_it_was_before()
    {
        var (w, order, invoice, _) = await IssuedAsync();
        await CancelAsync(w, invoice);

        await using var db = Receivables.Db(w.Org, w.DbName);
        var query = new ProductLedgerQueryService(db, w.Variants);

        (await query.GetProfitabilityRowsAsync(new ProductProfitabilityFilter())).Should().BeEmpty();
        var summary = await query.GetSummaryAsync(order.Variants[0]);
        (summary.CurrentQuantity, summary.StockValue, summary.WeightedAverageCost).Should().Be((15m, 405m, 27m));
    }

    [Fact]
    public async Task A_cancelled_sale_is_neither_sold_quantity_nor_cost_of_goods_sold_in_the_variants_summary()
    {
        // Reviewer finding: the summary added up every SALE entry, so a sale its invoice's cancellation had
        // taken back still counted as sold — 10 sold and 100 of cost of sales for goods of which only 4 left.
        var w = new World();
        var order   = await PlaceAsync(w, lines: [new LineSpec(10m, 25m, Cost: 10m)]);
        var variant = order.Variants[0];
        await IssueAsync(w, Deliver(w, order, (0, 4m)));
        var cancelled = await IssueAsync(w, Deliver(w, order, (0, 6m)));
        await CancelAsync(w, cancelled);

        // A genuine customer return — another document — is stock coming back, not a sale undone: sold stays as it was.
        await using (var s = w.Open())
            await s.ProductLedger.AppendEntryAsync(new ProductLedgerPosting(
                variant, "RETURN_IN", "IN", 1m, 10m, "SalesReturn", Guid.NewGuid(), "SRET-1", User));

        await using var db = Receivables.Db(w.Org, w.DbName);
        var summary = await new ProductLedgerQueryService(db, w.Variants).GetSummaryAsync(variant);

        (summary.SoldQuantity, summary.CostOfGoodsSold).Should().Be((4m, 40m));
        (summary.PurchasedQuantity, summary.PurchasedCost).Should().Be((10m, 100m));
        (summary.CurrentQuantity, summary.StockValue).Should().Be((7m, 70m));
        summary.EntryCount.Should().Be(5, "the ledger itself keeps every entry, the sale and its reversal included");

        // The profitability report says the same of the same sales.
        (await new ProductLedgerQueryService(db, w.Variants).GetProfitabilityRowsAsync(new ProductProfitabilityFilter()))
            .Should().ContainSingle().Which.Should().Match<ProductProfitabilityItemModel>(r => r.QuantitySold == 4m && r.CostOfGoodsSold == 40m);
    }

    [Fact]
    public async Task A_sale_re_invoiced_after_its_cancellation_is_sold_once_not_twice_nor_never()
    {
        var (w, order, invoice, delivery) = await IssuedAsync();
        await CancelAsync(w, invoice);
        await using (var s = w.Open())
        {
            var again = await s.Invoices.CreateFromFulfillmentAsync(delivery, User);
            await using var t = w.Open();
            await t.Invoices.IssueAsync(again.InvoiceUuid, User);
        }

        await using var db = Receivables.Db(w.Org, w.DbName);
        var summary = await new ProductLedgerQueryService(db, w.Variants).GetSummaryAsync(order.Variants[0]);

        (summary.SoldQuantity, summary.CostOfGoodsSold, summary.CurrentQuantity, summary.StockValue).Should().Be((10m, 270m, 5m, 135m));
    }

    private static ReceivablesReportService Reports(World w)
    {
        var templates = new Mock<IPoDocumentTemplateService>();
        templates.Setup(t => t.GetActiveAsync()).ReturnsAsync((PoDocumentTemplateModel?)null);
        return new ReceivablesReportService(Receivables.Db(w.Org, w.DbName), Receivables.Names().Object, templates.Object, w.Clock);
    }

    // ── S-7: the order asks Finance before it is cancelled ───────────────────

    [Fact]
    public async Task The_lookup_lists_an_orders_drafts_and_issued_invoices_but_not_cancelled_deleted_or_other_orders()
    {
        var w = new World();
        var order = await PlaceAsync(w, lines: [new LineSpec(10m, 10m)]);
        var other = await PlaceAsync(w, lines: [new LineSpec(1m, 10m)]);
        var issued    = await IssueAsync(w, Deliver(w, order, (0, 2m)));
        var draft     = await RaiseAsync(w, Deliver(w, order, (0, 2m)));
        var cancelled = await IssueAsync(w, Deliver(w, order, (0, 2m)));
        await CancelAsync(w, cancelled);
        var deleted   = await RaiseAsync(w, Deliver(w, order, (0, 2m)));
        await using (var s = w.Open()) await s.Invoices.DeleteAsync(deleted, User);
        await IssueAsync(w, Deliver(w, other));

        await using var db = Receivables.Db(w.Org, w.DbName);
        var live = await new SaleOrderInvoiceLookup(db).GetLiveInvoicesAsync(order.Uuid);

        live.Select(i => (i.InvoiceUuid, i.Status)).Should().Equal((issued, "ISSUED"), (draft, "DRAFT"));
        live.Should().OnlyContain(i => i.InvoiceNumber.StartsWith("SINV-"));

        await using var foreign = Receivables.Db(Guid.NewGuid(), w.DbName);
        (await new SaleOrderInvoiceLookup(foreign).GetLiveInvoicesAsync(order.Uuid)).Should().BeEmpty("another organization sees none of them");
    }

    [Fact]
    public async Task A_sale_order_with_an_issued_invoice_cannot_be_cancelled_until_the_invoice_is()
    {
        var (w, order, invoice, _) = await IssuedAsync();

        SaleOrderService Orders(DemandDbContext demand, FinanceDbContext finance) => new(
            demand, new StaticTenantContext { OrganizationId = w.Org }, Mock.Of<IOrganizationCurrencyService>(),
            Mock.Of<IDocumentNumberGenerator>(), Mock.Of<IPricingService>(), Mock.Of<IStockReservationService>(),
            Mock.Of<ITimelineService>(), Mock.Of<IBackgroundJobClient>(), Mock.Of<IAvailabilityCheckService>(),
            Mock.Of<IPurchaseOrderService>(), Mock.Of<ISaleOrderEmailService>(),
            invoices: new SaleOrderInvoiceLookup(finance));

        await using (var s = w.Open())
        {
            var act = async () => await Orders(s.Demand, s.Db).CancelAsync(order.Uuid, User, "No longer wanted");
            (await act.Should().ThrowAsync<ConflictException>())
                .WithMessage($"*{order.Number}*{(await InvoiceAsync(w, invoice)).InvoiceNumber} (ISSUED)*Cancel its invoices first*");
        }

        await CancelAsync(w, invoice);

        await using (var s = w.Open())
            (await Orders(s.Demand, s.Db).CancelAsync(order.Uuid, User, "No longer wanted")).Should().BeTrue();
        await using (var s = w.Open())
            (await s.Demand.SaleOrders.AsNoTracking().SingleAsync(o => o.UUID == order.Uuid)).Status.Should().Be("CANCELLED");
    }

    // ── Wiring ───────────────────────────────────────────────────────────────

    [Fact]
    public void The_module_registers_the_lookup_and_gives_the_invoice_service_its_rate_services()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Data:mainOrg"] = "Server=none;Database=none;Trusted_Connection=True;" })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(new StaticTenantContext());
        services.AddFinanceModule(configuration);
        services.AddDbContext<DemandDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddSingleton(Mock.Of<IDeliveryFulfillmentReader>());
        services.AddSingleton(Mock.Of<ISupplierNameLookupService>());
        services.AddSingleton(Mock.Of<ILookupsService>());
        services.AddSingleton(Mock.Of<IBackgroundJobClient>());
        services.AddSingleton(Mock.Of<IProductVariantResolver>());
        // What Tenancy and Lookups register in the real host.
        services.AddSingleton(Mock.Of<IOrganizationCurrencyService>());
        services.AddSingleton(Mock.Of<ICurrencyCodeLookup>());

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ISaleOrderInvoiceLookup>().Should().BeOfType<SaleOrderInvoiceLookup>();

        var invoices = scope.ServiceProvider.GetRequiredService<ISalesInvoiceService>();
        object? Field(string name) => invoices.GetType()
            .GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(invoices);

        Field("_exchangeRates").Should().NotBeNull("Finance registers the exchange-rate provider");
        Field("_orgCurrency").Should().NotBeNull();
        Field("_currencyCodes").Should().NotBeNull();
    }
}

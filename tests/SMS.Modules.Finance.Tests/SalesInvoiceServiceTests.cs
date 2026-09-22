using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Services;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Models;
using Xunit;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// A29-P7-04 §9.5/§13.3 — raising a DRAFT invoice from a delivered delivery, and issuing it: the
/// customer ledger DEBIT in the same transaction, invoiced quantities held to what was delivered,
/// several invoices per order, and SO_INVOICED on the order's own trace (TC-14).
/// </summary>
public class SalesInvoiceServiceTests
{
    private const int User = 42;
    private static readonly Guid CurrencyId = Guid.NewGuid();
    private static readonly DateTime Today  = new(2026, 9, 20, 10, 30, 0, DateTimeKind.Utc);

    private sealed class FakeClock : TimeProvider
    {
        public DateTime Now { get; set; } = Today;
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Now, DateTimeKind.Utc));
    }

    /// <summary>Records what each save contained, to prove two things went in one unit of work.</summary>
    private sealed class SaveRecorder : SaveChangesInterceptor
    {
        public List<(int ModifiedInvoices, int AddedLedgerEntries)> Saves { get; } = [];

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            var entries = eventData.Context!.ChangeTracker.Entries().ToList();
            Saves.Add((
                entries.Count(e => e.Entity is SalesInvoice && e.State == EntityState.Modified),
                entries.Count(e => e.Entity is CustomerLedgerEntry && e.State == EntityState.Added)));
            return base.SavingChangesAsync(eventData, result, ct);
        }
    }

    private sealed class Harness
    {
        public required FinanceDbContext Db;
        public required DemandDbContext Demand;
        public required SalesInvoiceService Service;
        public required Mock<IDeliveryFulfillmentReader> Reader;
        public required Mock<ISupplierNameLookupService> Names;
        public required Mock<ILookupsService> Lookups;
        public required List<Job> Jobs;
        public required FakeClock Clock;
        public required Guid OrgId;
        public required string DbName;
    }

    private static Harness NewHarness(
        Guid? orgId = null, string? dbName = null, IInterceptor? financeInterceptor = null,
        IInterceptor? demandInterceptor = null, FakeClock? clock = null)
    {
        orgId  ??= Guid.NewGuid();
        dbName ??= Guid.NewGuid().ToString();
        clock  ??= new FakeClock();
        var tenant = new StaticTenantContext { OrganizationId = orgId.Value };

        var financeOptions = new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(dbName);
        if (financeInterceptor is not null) financeOptions.AddInterceptors(financeInterceptor);
        var demandOptions = new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(dbName);
        if (demandInterceptor is not null) demandOptions.AddInterceptors(demandInterceptor);

        var db     = new FinanceDbContext(financeOptions.Options, tenant);
        var demand = new DemandDbContext(demandOptions.Options, tenant);

        var reader = new Mock<IDeliveryFulfillmentReader>();
        var names  = new Mock<ISupplierNameLookupService>();
        names.Setup(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()))
             .ReturnsAsync((IReadOnlyList<Guid> ids) => ids.ToDictionary(id => id, _ => "Acme Ltd"));
        var lookups = new Mock<ILookupsService>();
        lookups.Setup(l => l.GetCurrencies()).Returns([new CurrencyModel { Id = CurrencyId, Name = "Pakistani Rupee", Code = " PKR " }]);

        var jobs = new List<Job>();
        var jobClient = new Mock<IBackgroundJobClient>();
        jobClient.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()))
                 .Callback<Job, IState>((job, _) => jobs.Add(job)).Returns("fake-job-id");

        var service = new SalesInvoiceService(
            db, demand, reader.Object, new CustomerLedgerService(db), new NoProductLedger(), names.Object, lookups.Object,
            jobClient.Object, NullLogger<SalesInvoiceService>.Instance, clock);

        return new Harness
        {
            Db = db, Demand = demand, Service = service, Reader = reader, Names = names, Lookups = lookups,
            Jobs = jobs, Clock = clock, OrgId = orgId.Value, DbName = dbName
        };
    }

    // ── Seeding ──────────────────────────────────────────────────────────────

    private sealed record SoLineSpec(decimal Qty, decimal Price = 40m, decimal Discount = 0m, decimal Tax = 0m, decimal? Fulfilled = null, string Status = "FULFILLED");

    private static async Task<SaleOrder> SeedOrder(Harness h, string status = "FULFILLED", Guid? partner = null, params SoLineSpec[] lines)
    {
        var order = new SaleOrder
        {
            SoNumber = "SO-2026-00042", TraceId = Guid.NewGuid(), PartnerId = partner ?? Guid.NewGuid(),
            OrderDate = new DateTime(2026, 9, 1), CurrencyId = CurrencyId, Status = status,
            DeliveryMode = "SHIP", CreatedBy = 1
        };
        foreach (var l in lines.Length > 0 ? lines : [new SoLineSpec(100m)])
            order.Lines.Add(new SaleOrderLine
            {
                VariantUuid = Guid.NewGuid(), Quantity = l.Qty, UnitPrice = l.Price,
                DiscountPercent = l.Discount, TaxPercent = l.Tax,
                LineTotal = SalesInvoiceTotals.LineTotal(l.Qty, l.Price, l.Discount, l.Tax),
                FulfilledQty = l.Fulfilled ?? l.Qty, FulfillmentMode = "IN_STOCK", Status = l.Status
            });
        h.Demand.SaleOrders.Add(order);
        await h.Demand.SaveChangesAsync();
        h.Demand.ChangeTracker.Clear();
        return order;
    }

    /// <summary>Tells the fake Logistics reader about a delivery: (sale order line index, delivered qty) pairs.</summary>
    private static Guid SeedDelivery(
        Harness h, SaleOrder? order, string status = "DELIVERED", string number = "DLV-2026-00001",
        params (int SoLineIndex, decimal Delivered)[] lines)
    {
        var uuid = Guid.NewGuid();
        var soLines = order?.Lines.OrderBy(l => l.Id).ToList() ?? [];

        var delivered = (lines.Length > 0 ? lines : [(0, 100m)])
            .Select((l, i) => new DeliveredLineForInvoicing(
                Guid.NewGuid(), i + 1, order is null ? null : soLines[l.SoLineIndex].UUID, order is null ? null : soLines[l.SoLineIndex].VariantUuid,
                $"Item {l.SoLineIndex + 1}", l.Delivered))
            .ToList();

        h.Reader.Setup(r => r.GetAsync(uuid, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeliveryForInvoicing(uuid, number, status, order?.UUID, delivered));
        return uuid;
    }

    private static Task<SalesInvoice> Load(Harness h, Guid invoiceUuid) =>
        h.Db.SalesInvoices.AsNoTracking().Include(i => i.Lines).SingleAsync(i => i.UUID == invoiceUuid);

    private static Task<List<CustomerLedgerEntry>> Ledger(Harness h) =>
        h.Db.CustomerLedgerEntries.AsNoTracking().OrderBy(e => e.PartnerId).ThenBy(e => e.SequenceNo).ToListAsync();

    // ── Create: what an invoice is ───────────────────────────────────────────

    [Fact]
    public async Task A_delivered_delivery_becomes_a_draft_invoice_priced_from_the_order()
    {
        var h        = NewHarness();
        var partner  = Guid.NewGuid();
        var order    = await SeedOrder(h, "FULFILLED", partner, new SoLineSpec(100m, 40m, Discount: 10m, Tax: 5m));
        var delivery = SeedDelivery(h, order, "DELIVERED", "DLV-2026-00007", (0, 100m));

        var created = await h.Service.CreateFromFulfillmentAsync(delivery, User);

        created.InvoiceNumber.Should().Be("SINV-20260920-0001");
        created.AlreadyExisted.Should().BeFalse();
        created.CurrencyCode.Should().Be("PKR", "trimmed from the catalog's code");
        created.GrandTotal.Should().Be(3780m, "100 × 40, less 10%, plus 5% tax");

        var invoice = await Load(h, created.InvoiceUuid);
        invoice.Status.Should().Be("DRAFT");
        invoice.TraceId.Should().Be(order.TraceId, "§9.1: copied from the sale order");
        invoice.SaleOrderUuid.Should().Be(order.UUID);
        invoice.SaleOrderNumber.Should().Be("SO-2026-00042");
        invoice.DeliveryUuid.Should().Be(delivery);
        invoice.DeliveryNumber.Should().Be("DLV-2026-00007");
        invoice.PartnerId.Should().Be(partner);
        invoice.PartnerName.Should().Be("Acme Ltd");
        invoice.InvoiceDate.Should().Be(new DateTime(2026, 9, 20));
        invoice.DueDate.Should().Be(new DateTime(2026, 10, 20), "thirty days on");
        invoice.Subtotal.Should().Be(4000m);
        invoice.DiscountAmount.Should().Be(400m);
        invoice.TaxAmount.Should().Be(180m);
        invoice.GrandTotal.Should().Be(3780m);
        invoice.AmountPaid.Should().Be(0m);
        invoice.BalanceDue.Should().Be(3780m);
        invoice.CreatedBy.Should().Be(User);
        invoice.OrganizationId.Should().Be(h.OrgId);

        var line = invoice.Lines.Should().ContainSingle().Subject;
        line.SoLineUuid.Should().Be(order.Lines.Single().UUID);
        line.VariantUuid.Should().Be(order.Lines.Single().VariantUuid);
        line.Description.Should().Be("Item 1");
        line.Quantity.Should().Be(100m);
        line.UnitPrice.Should().Be(40m);
        line.DiscountPercent.Should().Be(10m);
        line.TaxPercent.Should().Be(5m);
        line.LineTotal.Should().Be(3780m);
    }

    [Fact]
    public async Task Creating_an_invoice_books_nothing_and_says_nothing_yet()
    {
        // A draft is a proposal: no receivable, no timeline event, and the order's counters are as they were.
        var h        = NewHarness();
        var order    = await SeedOrder(h);
        var delivery = SeedDelivery(h, order);

        await h.Service.CreateFromFulfillmentAsync(delivery, User);

        (await Ledger(h)).Should().BeEmpty();
        h.Jobs.Should().BeEmpty();
        (await h.Demand.SaleOrderLines.AsNoTracking().SingleAsync()).InvoicedQty.Should().Be(0m);
    }

    [Theory]
    [InlineData("DELIVERED")]
    [InlineData("CLOSED")]
    public async Task Goods_that_reached_the_customer_can_be_invoiced(string status)
    {
        var h        = NewHarness();
        var delivery = SeedDelivery(h, await SeedOrder(h), status);

        var act = async () => await h.Service.CreateFromFulfillmentAsync(delivery, User);

        await act.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("RELEASED")]
    [InlineData("PICKING")]
    [InlineData("PICKED")]
    [InlineData("PACKED")]
    [InlineData("STAGED")]
    [InlineData("GOODS_ISSUED")]
    [InlineData("IN_TRANSIT")]
    [InlineData("PARTIALLY_DELIVERED")]
    [InlineData("ON_HOLD")]
    [InlineData("SHORT_CLOSED")]
    [InlineData("CANCELLED")]
    public async Task A_delivery_that_has_not_reached_the_customer_cannot_be_invoiced(string status)
    {
        var h        = NewHarness();
        var delivery = SeedDelivery(h, await SeedOrder(h), status);

        var act = async () => await h.Service.CreateFromFulfillmentAsync(delivery, User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage($"*{status}*")
            .WithMessage("*DELIVERED*");
        (await h.Db.SalesInvoices.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_delivery_that_is_not_for_a_sale_order_has_no_customer_to_bill()
    {
        var h        = NewHarness();
        var delivery = SeedDelivery(h, order: null, "DELIVERED", "DLV-2026-00003", (0, 10m));

        var act = async () => await h.Service.CreateFromFulfillmentAsync(delivery, User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*not for a sale order*");
    }

    [Fact]
    public async Task An_unknown_delivery_is_not_found()
    {
        var h = NewHarness();
        h.Reader.Setup(r => r.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((DeliveryForInvoicing?)null);

        var act = async () => await h.Service.CreateFromFulfillmentAsync(Guid.NewGuid(), User);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("CANCELLED")]
    public async Task An_order_that_is_not_live_cannot_be_billed(string orderStatus)
    {
        var h        = NewHarness();
        var delivery = SeedDelivery(h, await SeedOrder(h, orderStatus));

        var act = async () => await h.Service.CreateFromFulfillmentAsync(delivery, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage($"*{orderStatus}*");
    }

    [Theory]
    [InlineData("CONFIRMED")]
    [InlineData("PARTIALLY_FULFILLED")]
    [InlineData("FULFILLED")]
    public async Task An_order_still_being_or_already_fulfilled_can_be_billed(string orderStatus)
    {
        var h        = NewHarness();
        var delivery = SeedDelivery(h, await SeedOrder(h, orderStatus));

        var act = async () => await h.Service.CreateFromFulfillmentAsync(delivery, User);

        await act.Should().NotThrowAsync();
    }

    // ── Create: partial delivery, several invoices per order ─────────────────

    [Fact]
    public async Task Only_what_was_delivered_is_billed_and_lines_delivered_nothing_are_left_off()
    {
        var h     = NewHarness();
        var order = await SeedOrder(h, "PARTIALLY_FULFILLED", null,
            new SoLineSpec(100m, 40m, Fulfilled: 40m), new SoLineSpec(10m, 5m, Fulfilled: 0m));
        var delivery = SeedDelivery(h, order, "DELIVERED", "DLV-2026-00001", (0, 40m), (1, 0m));

        var created = await h.Service.CreateFromFulfillmentAsync(delivery, User);

        var invoice = await Load(h, created.InvoiceUuid);
        invoice.Lines.Should().ContainSingle().Which.Quantity.Should().Be(40m);
        invoice.GrandTotal.Should().Be(1600m, "40 units, not the 100 ordered");
    }

    [Fact]
    public async Task A_delivery_with_nothing_delivered_has_nothing_to_invoice()
    {
        var h        = NewHarness();
        var delivery = SeedDelivery(h, await SeedOrder(h), "DELIVERED", "DLV-2026-00001", (0, 0m));

        var act = async () => await h.Service.CreateFromFulfillmentAsync(delivery, User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*Nothing was delivered*");
    }

    [Fact]
    public async Task One_order_delivered_in_three_goes_out_as_three_invoices_that_add_up_to_the_order()
    {
        // TC-09/§9.5: 40 + 30 + 30 — multiple invoices per SO, each delivery billed once.
        var h     = NewHarness();
        var order = await SeedOrder(h, "FULFILLED", null, new SoLineSpec(100m, 40m, Discount: 10m, Tax: 5m));

        var first  = await h.Service.CreateFromFulfillmentAsync(SeedDelivery(h, order, "DELIVERED", "DLV-2026-00001", (0, 40m)), User);
        var second = await h.Service.CreateFromFulfillmentAsync(SeedDelivery(h, order, "DELIVERED", "DLV-2026-00002", (0, 30m)), User);
        var third  = await h.Service.CreateFromFulfillmentAsync(SeedDelivery(h, order, "DELIVERED", "DLV-2026-00003", (0, 30m)), User);

        new[] { first.InvoiceNumber, second.InvoiceNumber, third.InvoiceNumber }
            .Should().Equal("SINV-20260920-0001", "SINV-20260920-0002", "SINV-20260920-0003");

        var invoices = await h.Db.SalesInvoices.AsNoTracking().Include(i => i.Lines).ToListAsync();
        invoices.Should().OnlyContain(i => i.SaleOrderUuid == order.UUID && i.TraceId == order.TraceId,
            "one order, one trace, however many invoices");
        invoices.SelectMany(i => i.Lines).Sum(l => l.Quantity).Should().Be(100m);
        invoices.Sum(i => i.GrandTotal).Should().Be(3780m, "the three invoices together bill exactly the order");
    }

    // ── §9.5: invoiced qty ≤ delivered qty ───────────────────────────────────

    [Fact]
    public async Task An_invoice_cannot_bill_more_than_the_order_has_delivered_in_all()
    {
        // The order has delivered 100. 60 is already invoiced; a second delivery claims 50 more.
        var h     = NewHarness();
        var order = await SeedOrder(h, "FULFILLED", null, new SoLineSpec(100m, Fulfilled: 100m));

        await h.Service.CreateFromFulfillmentAsync(SeedDelivery(h, order, "DELIVERED", "DLV-2026-00001", (0, 60m)), User);
        var act = async () => await h.Service.CreateFromFulfillmentAsync(SeedDelivery(h, order, "DELIVERED", "DLV-2026-00002", (0, 50m)), User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*110*")
            .WithMessage("*100 delivered*")
            .WithMessage("*cannot bill more than has been delivered*");
        (await h.Db.SalesInvoices.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_delivery_the_order_never_credited_cannot_be_billed()
    {
        // The delivery says 30 were delivered but the order's own fulfilled quantity is only 20 — the
        // two disagree, and billing the larger one would invoice goods the order does not know left.
        var h        = NewHarness();
        var order    = await SeedOrder(h, "PARTIALLY_FULFILLED", null, new SoLineSpec(100m, Fulfilled: 20m));
        var delivery = SeedDelivery(h, order, "DELIVERED", "DLV-2026-00001", (0, 30m));

        var act = async () => await h.Service.CreateFromFulfillmentAsync(delivery, User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*above the 20 delivered*");
    }

    [Theory]
    [InlineData("CANCELLED")]
    [InlineData("CREDIT_NOTE")]
    public async Task A_cancelled_invoice_or_a_credit_note_is_not_a_billing_and_frees_the_quantity(string status)
    {
        var h     = NewHarness();
        var order = await SeedOrder(h, "FULFILLED", null, new SoLineSpec(100m, Fulfilled: 100m));
        var first = await h.Service.CreateFromFulfillmentAsync(SeedDelivery(h, order, "DELIVERED", "DLV-2026-00001", (0, 100m)), User);

        var stored = await h.Db.SalesInvoices.SingleAsync(i => i.UUID == first.InvoiceUuid);
        stored.Status = status;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var second = await h.Service.CreateFromFulfillmentAsync(SeedDelivery(h, order, "DELIVERED", "DLV-2026-00002", (0, 100m)), User);

        second.AlreadyExisted.Should().BeFalse();
        (await Load(h, second.InvoiceUuid)).Lines.Single().Quantity.Should().Be(100m);
    }

    [Fact]
    public async Task A_delivered_line_that_does_not_name_its_order_line_is_refused()
    {
        var h     = NewHarness();
        var order = await SeedOrder(h);
        var uuid  = Guid.NewGuid();
        h.Reader.Setup(r => r.GetAsync(uuid, It.IsAny<CancellationToken>())).ReturnsAsync(new DeliveryForInvoicing(
            uuid, "DLV-2026-00009", "DELIVERED", order.UUID,
            [new DeliveredLineForInvoicing(Guid.NewGuid(), 1, null, Guid.NewGuid(), "Mystery", 5m)]));

        var act = async () => await h.Service.CreateFromFulfillmentAsync(uuid, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*does not name the sale order line*");
    }

    [Fact]
    public async Task A_delivered_line_pointing_at_another_orders_line_is_refused()
    {
        var h     = NewHarness();
        var order = await SeedOrder(h);
        var uuid  = Guid.NewGuid();
        h.Reader.Setup(r => r.GetAsync(uuid, It.IsAny<CancellationToken>())).ReturnsAsync(new DeliveryForInvoicing(
            uuid, "DLV-2026-00009", "DELIVERED", order.UUID,
            [new DeliveredLineForInvoicing(Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(), "Stray", 5m)]));

        var act = async () => await h.Service.CreateFromFulfillmentAsync(uuid, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*not on sale order*");
    }

    [Fact]
    public async Task An_invoice_for_nothing_is_refused()
    {
        var h        = NewHarness();
        var order    = await SeedOrder(h, "FULFILLED", null, new SoLineSpec(10m, Price: 0m));
        var delivery = SeedDelivery(h, order, "DELIVERED", "DLV-2026-00001", (0, 10m));

        var act = async () => await h.Service.CreateFromFulfillmentAsync(delivery, User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*no receivable to book*");
    }

    // ── Create: idempotence ──────────────────────────────────────────────────

    [Fact]
    public async Task Invoicing_the_same_delivery_twice_returns_the_first_invoice()
    {
        var h        = NewHarness();
        var delivery = SeedDelivery(h, await SeedOrder(h));

        var first  = await h.Service.CreateFromFulfillmentAsync(delivery, User);
        var second = await h.Service.CreateFromFulfillmentAsync(delivery, User);

        second.AlreadyExisted.Should().BeTrue();
        second.InvoiceUuid.Should().Be(first.InvoiceUuid);
        second.InvoiceNumber.Should().Be(first.InvoiceNumber);
        (await h.Db.SalesInvoices.CountAsync()).Should().Be(1, "billing the same goods twice looks exactly as legitimate as once");
    }

    [Fact]
    public async Task A_cancelled_invoice_frees_its_delivery_to_be_invoiced_again()
    {
        var h        = NewHarness();
        var delivery = SeedDelivery(h, await SeedOrder(h));
        var first    = await h.Service.CreateFromFulfillmentAsync(delivery, User);

        var stored = await h.Db.SalesInvoices.SingleAsync(i => i.UUID == first.InvoiceUuid);
        stored.Status = "CANCELLED";
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var again = await h.Service.CreateFromFulfillmentAsync(delivery, User);

        again.AlreadyExisted.Should().BeFalse();
        again.InvoiceUuid.Should().NotBe(first.InvoiceUuid);
        again.InvoiceNumber.Should().Be("SINV-20260920-0002", "the cancelled invoice keeps its number");
    }

    // ── Create: numbering ────────────────────────────────────────────────────

    [Fact]
    public async Task Numbers_continue_from_the_highest_used_that_day_and_never_reuse_a_deleted_one()
    {
        var h     = NewHarness();
        var order = await SeedOrder(h, "FULFILLED", null, new SoLineSpec(1000m, Fulfilled: 1000m));

        var first = await h.Service.CreateFromFulfillmentAsync(SeedDelivery(h, order, "DELIVERED", "DLV-2026-00001", (0, 10m)), User);
        var stored = await h.Db.SalesInvoices.SingleAsync(i => i.UUID == first.InvoiceUuid);
        stored.IsDelete = true; // soft-deleted: its number stays spoken for
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var second = await h.Service.CreateFromFulfillmentAsync(SeedDelivery(h, order, "DELIVERED", "DLV-2026-00002", (0, 10m)), User);

        second.InvoiceNumber.Should().Be("SINV-20260920-0002");
    }

    [Fact]
    public async Task Each_day_restarts_the_count_and_each_organization_counts_alone()
    {
        var clock = new FakeClock();
        var a     = NewHarness(clock: clock);
        var order = await SeedOrder(a, "FULFILLED", null, new SoLineSpec(100m, Fulfilled: 100m));
        await a.Service.CreateFromFulfillmentAsync(SeedDelivery(a, order, "DELIVERED", "DLV-2026-00001", (0, 10m)), User);

        clock.Now = Today.AddDays(1);
        var nextDay = await a.Service.CreateFromFulfillmentAsync(SeedDelivery(a, order, "DELIVERED", "DLV-2026-00002", (0, 10m)), User);
        nextDay.InvoiceNumber.Should().Be("SINV-20260921-0001");

        var b      = NewHarness(dbName: a.DbName);
        var orderB = await SeedOrder(b, "FULFILLED", null, new SoLineSpec(100m, Fulfilled: 100m));
        var other  = await b.Service.CreateFromFulfillmentAsync(SeedDelivery(b, orderB, "DELIVERED", "DLV-2026-00009", (0, 10m)), User);
        other.InvoiceNumber.Should().Be("SINV-20260920-0001", "another organization's counter is its own");
    }

    [Fact]
    public async Task Losing_the_race_for_an_invoice_number_takes_the_next_one()
    {
        // Another writer commits SINV-…-0001 between this one reading the maximum and saving.
        var dbName = Guid.NewGuid().ToString();
        var orgId  = Guid.NewGuid();
        var rival  = NewHarness(orgId, dbName);
        var rivalOrder = await SeedOrder(rival, "FULFILLED", null, new SoLineSpec(100m, Fulfilled: 100m));

        var race = new LoseTheRaceOnce(db => db.ChangeTracker.Entries<SalesInvoice>().Any(e => e.State == EntityState.Added), async () =>
            await rival.Service.CreateFromFulfillmentAsync(SeedDelivery(rival, rivalOrder, "DELIVERED", "DLV-2026-00099", (0, 5m)), User));

        var h     = NewHarness(orgId, dbName, race);
        var order = await SeedOrder(h, "FULFILLED", null, new SoLineSpec(100m, Fulfilled: 100m));
        var created = await h.Service.CreateFromFulfillmentAsync(SeedDelivery(h, order, "DELIVERED", "DLV-2026-00001", (0, 10m)), User);

        race.Fired.Should().BeTrue();
        created.InvoiceNumber.Should().Be("SINV-20260920-0002", "the winner took 0001; the loser re-read the maximum");
        (await h.Db.SalesInvoices.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Losing_the_race_to_invoice_the_same_delivery_returns_the_winners_invoice()
    {
        var dbName = Guid.NewGuid().ToString();
        var orgId  = Guid.NewGuid();
        var rival  = NewHarness(orgId, dbName);
        var rivalOrder = await SeedOrder(rival);
        var rivalDelivery = SeedDelivery(rival, rivalOrder);

        var race = new LoseTheRaceOnce(db => db.ChangeTracker.Entries<SalesInvoice>().Any(e => e.State == EntityState.Added), async () =>
            await rival.Service.CreateFromFulfillmentAsync(rivalDelivery, User));

        var h = NewHarness(orgId, dbName, race);
        h.Reader.Setup(r => r.GetAsync(rivalDelivery, It.IsAny<CancellationToken>()))
                .Returns((Guid _, CancellationToken _) => rival.Reader.Object.GetAsync(rivalDelivery));

        var created = await h.Service.CreateFromFulfillmentAsync(rivalDelivery, User);

        created.AlreadyExisted.Should().BeTrue();
        (await h.Db.SalesInvoices.CountAsync()).Should().Be(1, "the loser did not raise a second invoice");
    }

    [Fact]
    public async Task A_currency_with_no_iso_code_is_refused_rather_than_guessed()
    {
        var h = NewHarness();
        h.Lookups.Setup(l => l.GetCurrencies()).Returns([new CurrencyModel { Id = CurrencyId, Name = "Mystery", Code = null }]);
        var delivery = SeedDelivery(h, await SeedOrder(h));

        var act = async () => await h.Service.CreateFromFulfillmentAsync(delivery, User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*no ISO code*");
        (await h.Db.SalesInvoices.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_unresolvable_customer_name_falls_back_to_a_placeholder_not_a_failure()
    {
        var h = NewHarness();
        h.Names.Setup(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>())).ReturnsAsync(new Dictionary<Guid, string>());
        var delivery = SeedDelivery(h, await SeedOrder(h));

        var created = await h.Service.CreateFromFulfillmentAsync(delivery, User);

        (await Load(h, created.InvoiceUuid)).PartnerName.Should().Be("(unknown customer)");
    }

    [Fact]
    public async Task Deleting_a_draft_frees_its_delivery_and_its_quantities_to_be_invoiced_afresh_under_a_new_number()
    {
        var h        = NewHarness();
        var order    = await SeedOrder(h, "FULFILLED", null, new SoLineSpec(100m, 40m, Fulfilled: 100m));
        var delivery = SeedDelivery(h, order);

        var first = await h.Service.CreateFromFulfillmentAsync(delivery, User);
        await h.Service.DeleteAsync(first.InvoiceUuid, User);
        var second = await h.Service.CreateFromFulfillmentAsync(delivery, User);

        second.AlreadyExisted.Should().BeFalse("the deleted draft no longer counts against its delivery");
        second.InvoiceUuid.Should().NotBe(first.InvoiceUuid);
        second.InvoiceNumber.Should().Be("SINV-20260920-0002", "the deleted invoice keeps its number");
        (await Load(h, second.InvoiceUuid)).Lines.Single().Quantity.Should().Be(100m, "the full quantity is billable again");
    }

    [Fact]
    public async Task A_draft_edited_before_it_is_issued_keeps_its_due_date_through_issue_and_the_ledger_is_unaffected_by_the_edit()
    {
        var h       = NewHarness();
        var partner = Guid.NewGuid();
        var order   = await SeedOrder(h, "FULFILLED", partner, new SoLineSpec(100m, 40m, Fulfilled: 100m));
        var invoice = await Draft(h, order);

        await h.Service.UpdateAsync(invoice, new UpdateSalesInvoiceRequest { DueDate = new DateTime(2026, 11, 15), Notes = "Net 55" }, User);
        (await Ledger(h)).Should().BeEmpty("a draft books nothing, edited or not");

        var issued = await h.Service.IssueAsync(invoice, User);

        issued.Status.Should().Be("ISSUED");
        var stored = await Load(h, invoice);
        (stored.DueDate, stored.Notes, stored.Status).Should().Be((new DateTime(2026, 11, 15), "Net 55", "ISSUED"));
        (await Ledger(h)).Should().ContainSingle().Which.DebitAmount.Should().Be(4000m);
    }

    // ── Issue: the receivable ────────────────────────────────────────────────

    private static async Task<Guid> Draft(Harness h, SaleOrder order, string deliveryNumber = "DLV-2026-00001", decimal qty = 100m, int line = 0) =>
        (await h.Service.CreateFromFulfillmentAsync(SeedDelivery(h, order, "DELIVERED", deliveryNumber, (line, qty)), User)).InvoiceUuid;

    [Fact]
    public async Task Issuing_moves_the_invoice_to_issued_and_debits_the_customers_ledger()
    {
        var h       = NewHarness();
        var partner = Guid.NewGuid();
        var order   = await SeedOrder(h, "FULFILLED", partner, new SoLineSpec(100m, 40m, Discount: 10m, Tax: 5m));
        var invoice = await Draft(h, order);

        var result = await h.Service.IssueAsync(invoice, User);

        result.Status.Should().Be("ISSUED");
        result.GrandTotal.Should().Be(3780m);
        result.PartnerBalance.Should().Be(3780m);

        (await Load(h, invoice)).Status.Should().Be("ISSUED");
        (await Load(h, invoice)).ModifiedBy.Should().Be(User);

        var entry = (await Ledger(h)).Should().ContainSingle().Subject;
        entry.PartnerId.Should().Be(partner);
        entry.EntryType.Should().Be("INVOICE");
        entry.DebitAmount.Should().Be(3780m, "an issued invoice increases what the customer owes");
        entry.CreditAmount.Should().Be(0m);
        entry.RunningBalance.Should().Be(3780m);
        entry.SequenceNo.Should().Be(1);
        entry.ReferenceType.Should().Be("SalesInvoice");
        entry.ReferenceId.Should().Be(invoice);
        entry.ReferenceNumber.Should().Be("SINV-20260920-0001");
        entry.CurrencyCode.Should().Be("PKR");
        entry.Narration.Should().Contain("SINV-20260920-0001").And.Contain("SO-2026-00042").And.Contain("DLV-2026-00001");
        entry.CreatedBy.Should().Be(User);
        entry.OrganizationId.Should().Be(h.OrgId);
    }

    [Fact]
    public async Task The_status_change_and_the_ledger_entry_are_one_unit_of_work()
    {
        // §9.5: "in the same transaction". One SaveChanges carries both, so they commit together or
        // not at all — there is no window in which the invoice is issued and the receivable is not booked.
        var recorder = new SaveRecorder();
        var h        = NewHarness(financeInterceptor: recorder);
        var invoice  = await Draft(h, await SeedOrder(h));
        recorder.Saves.Clear();

        await h.Service.IssueAsync(invoice, User);

        recorder.Saves.Should().ContainSingle("issuing saves exactly once")
                .Which.Should().Be((ModifiedInvoices: 1, AddedLedgerEntries: 1));
    }

    [Fact]
    public async Task If_the_save_fails_neither_the_status_nor_the_ledger_changes()
    {
        var h = NewHarness();
        var invoice = await Draft(h, await SeedOrder(h));

        var refusing = new AlwaysFail();
        var failing  = NewHarness(h.OrgId, h.DbName, refusing);

        var act = async () => await failing.Service.IssueAsync(invoice, User);

        (await act.Should().ThrowAsync<DbUpdateException>()).WithMessage("*refusing writes*");
        refusing.Attempts.Should().Be(5, "a lost race is retried, then the real error is surfaced rather than swallowed");
        (await Load(h, invoice)).Status.Should().Be("DRAFT", "the failed unit of work left the invoice as it was");
        (await Ledger(h)).Should().BeEmpty("and booked no receivable");
        h.Jobs.Should().BeEmpty();
        failing.Jobs.Should().BeEmpty("no SO_INVOICED for an invoice that was never issued");
        failing.Db.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Should().BeEmpty("nothing is left on the context for a later save to commit half of");
    }

    [Fact]
    public async Task A_persistent_failure_creating_an_invoice_is_surfaced_and_leaves_nothing_tracked()
    {
        var seed  = NewHarness();
        var order = await SeedOrder(seed);
        var refusing = new AlwaysFail();
        var h = NewHarness(seed.OrgId, seed.DbName, refusing);
        var delivery = SeedDelivery(h, order);

        var act = async () => await h.Service.CreateFromFulfillmentAsync(delivery, User);

        (await act.Should().ThrowAsync<DbUpdateException>()).WithMessage("*refusing writes*");
        refusing.Attempts.Should().Be(5);
        h.Db.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).Should().BeEmpty();
        (await seed.Db.SalesInvoices.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_customers_balance_runs_across_invoices_and_each_customer_has_their_own_ledger()
    {
        var h       = NewHarness();
        var partner = Guid.NewGuid();
        var order   = await SeedOrder(h, "FULFILLED", partner, new SoLineSpec(100m, 40m, Fulfilled: 100m));
        var other   = await SeedOrder(h, "FULFILLED", Guid.NewGuid(), new SoLineSpec(10m, 5m, Fulfilled: 10m));

        var first  = await h.Service.IssueAsync(await Draft(h, order, "DLV-2026-00001", 60m), User);   // 2400
        var second = await h.Service.IssueAsync(await Draft(h, order, "DLV-2026-00002", 40m), User);   // 1600
        var theirs = await h.Service.IssueAsync(await Draft(h, other, "DLV-2026-00003", 10m), User);   // 50

        first.PartnerBalance.Should().Be(2400m);
        second.PartnerBalance.Should().Be(4000m, "previous + debit");
        theirs.PartnerBalance.Should().Be(50m, "another customer's account starts from nothing");

        var ledger = await Ledger(h);
        ledger.Where(e => e.PartnerId == partner).Select(e => (e.SequenceNo, e.RunningBalance))
              .Should().Equal((1, 2400m), (2, 4000m));
        ledger.Single(e => e.PartnerId != partner).SequenceNo.Should().Be(1);
    }

    [Fact]
    public async Task Losing_the_race_for_the_customers_next_sequence_retries_against_the_fresh_last_row()
    {
        // Another writer posts this customer's entry 1 (a 500 debit) between the read and the save.
        var dbName  = Guid.NewGuid().ToString();
        var orgId   = Guid.NewGuid();
        var partner = Guid.NewGuid();
        var rival   = NewHarness(orgId, dbName);

        var race = new LoseTheRaceOnce(db => db.ChangeTracker.Entries<CustomerLedgerEntry>().Any(e => e.State == EntityState.Added), async () =>
        {
            await new CustomerLedgerService(rival.Db).TrackEntryAsync(new CustomerLedgerPosting(
                partner, "INVOICE", "SalesInvoice", Guid.NewGuid(), "SINV-RIVAL", 500m, 0m, "PKR", null, Today, 7));
            await rival.Db.SaveChangesAsync();
        });

        var h       = NewHarness(orgId, dbName, race);
        var order   = await SeedOrder(h, "FULFILLED", partner, new SoLineSpec(100m, 40m, Fulfilled: 100m));
        var invoice = await Draft(h, order, "DLV-2026-00001", 100m);

        var result = await h.Service.IssueAsync(invoice, User);

        race.Fired.Should().BeTrue();
        result.PartnerBalance.Should().Be(4500m, "the rival's 500 plus this invoice's 4000 — the balance did not fork");

        var ledger = await Ledger(h);
        ledger.Select(e => (e.SequenceNo, e.DebitAmount, e.RunningBalance)).Should().Equal((1, 500m, 500m), (2, 4000m, 4500m));
        (await Load(h, invoice)).Status.Should().Be("ISSUED");
    }

    [Fact]
    public async Task Only_a_draft_can_be_issued_and_a_second_issue_books_nothing_more()
    {
        var h       = NewHarness();
        var invoice = await Draft(h, await SeedOrder(h));
        await h.Service.IssueAsync(invoice, User);

        var again = async () => await h.Service.IssueAsync(invoice, User);

        (await again.Should().ThrowAsync<ConflictException>()).WithMessage("*ISSUED*").WithMessage("*Only a DRAFT*");
        (await Ledger(h)).Should().ContainSingle("the receivable was booked once");
    }

    [Theory]
    [InlineData("PARTIALLY_PAID")]
    [InlineData("PAID")]
    [InlineData("OVERDUE")]
    [InlineData("CANCELLED")]
    [InlineData("CREDIT_NOTE")]
    public async Task No_other_status_can_be_issued(string status)
    {
        var h       = NewHarness();
        var invoice = await Draft(h, await SeedOrder(h));
        var stored  = await h.Db.SalesInvoices.SingleAsync(i => i.UUID == invoice);
        stored.Status = status;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var act = async () => await h.Service.IssueAsync(invoice, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage($"*{status}*");
        (await Ledger(h)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_or_deleted_invoice_is_not_found()
    {
        var h       = NewHarness();
        var invoice = await Draft(h, await SeedOrder(h));

        var unknown = async () => await h.Service.IssueAsync(Guid.NewGuid(), User);
        await unknown.Should().ThrowAsync<NotFoundException>();

        var stored = await h.Db.SalesInvoices.SingleAsync(i => i.UUID == invoice);
        stored.IsDelete = true;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var deleted = async () => await h.Service.IssueAsync(invoice, User);
        await deleted.Should().ThrowAsync<NotFoundException>();
    }

    // ── Issue: the order and the timeline ────────────────────────────────────

    [Fact]
    public async Task Issuing_brings_the_orders_invoiced_quantities_up_to_date_line_by_line()
    {
        var h     = NewHarness();
        var order = await SeedOrder(h, "FULFILLED", null,
            new SoLineSpec(100m, 40m, Fulfilled: 100m), new SoLineSpec(10m, 5m, Fulfilled: 10m));

        await h.Service.IssueAsync(await Draft(h, order, "DLV-2026-00001", 60m, 0), User);
        var soLines = await h.Demand.SaleOrderLines.AsNoTracking().OrderBy(l => l.Id).ToListAsync();
        soLines.Select(l => l.InvoicedQty).Should().Equal(60m, 0m);

        await h.Service.IssueAsync(await Draft(h, order, "DLV-2026-00002", 40m, 0), User);
        await h.Service.IssueAsync(await Draft(h, order, "DLV-2026-00003", 10m, 1), User);
        soLines = await h.Demand.SaleOrderLines.AsNoTracking().OrderBy(l => l.Id).ToListAsync();
        soLines.Select(l => l.InvoicedQty).Should().Equal(100m, 10m);
        soLines.Should().OnlyContain(l => l.InvoicedQty == l.FulfilledQty, "invoiced never exceeds fulfilled");
    }

    [Fact]
    public async Task Issuing_records_SO_INVOICED_on_the_orders_own_trace_with_the_organization_named()
    {
        var h       = NewHarness();
        var order   = await SeedOrder(h, "FULFILLED", null, new SoLineSpec(100m, 40m, Fulfilled: 100m));
        var invoice = await Draft(h, order, "DLV-2026-00007", 100m);
        h.Jobs.Clear();

        await h.Service.IssueAsync(invoice, User);

        var job = h.Jobs.Should().ContainSingle(j => j.Method.Name == "AppendAsync").Subject;
        job.Args.Should().HaveCount(5, "the overload that carries the organization explicitly (§13.7)");
        job.Args[0].Should().Be(order.TraceId, "§13.3: on the sale order's trace, the one the invoice copied");

        var evt = ((TimelineEvent)job.Args[1]);
        evt.EventType.Should().Be(SaleOrderTimelineEventTypes.SoInvoiced).And.Be("SO_INVOICED");
        evt.InterfaceCode.Should().Be("SO");
        evt.DocumentId.Should().Be(order.UUID);
        evt.DocumentNumber.Should().Be("SO-2026-00042");
        evt.PerformedBy.Should().Be(User);
        evt.Notes.Should().Contain("SINV-20260920-0001").And.Contain("4000.00 PKR").And.Contain("100 unit(s)").And.Contain("DLV-2026-00007");

        job.Args[2].Should().Be("SO");
        job.Args[3].Should().Be("SO-2026-00042");
        job.Args[4].Should().Be(h.OrgId);
    }

    [Fact]
    public async Task Each_invoice_of_an_order_adds_its_own_event_to_the_same_trace()
    {
        var h     = NewHarness();
        var order = await SeedOrder(h, "FULFILLED", null, new SoLineSpec(100m, 40m, Fulfilled: 100m));
        var a = await Draft(h, order, "DLV-2026-00001", 60m);
        var b = await Draft(h, order, "DLV-2026-00002", 40m);
        h.Jobs.Clear();

        await h.Service.IssueAsync(a, User);
        await h.Service.IssueAsync(b, User);

        var events = h.Jobs.Where(j => j.Method.Name == "AppendAsync").ToList();
        events.Should().HaveCount(2);
        events.Select(j => j.Args[0]).Should().OnlyContain(t => (Guid)t == order.TraceId);
        events.Select(j => ((TimelineEvent)j.Args[1]).Notes).Should().Contain(n => n!.Contains("SINV-20260920-0001"))
              .And.Contain(n => n!.Contains("SINV-20260920-0002"));
    }

    [Fact]
    public async Task A_failure_updating_the_order_does_not_undo_the_issued_invoice()
    {
        // The receivable is booked whatever the order's counter does next.
        var dbName = Guid.NewGuid().ToString();
        var orgId  = Guid.NewGuid();
        var seed   = NewHarness(orgId, dbName);
        var order  = await SeedOrder(seed);
        var delivery = SeedDelivery(seed, order);
        var invoice  = (await seed.Service.CreateFromFulfillmentAsync(delivery, User)).InvoiceUuid;

        var h = NewHarness(orgId, dbName, demandInterceptor: new AlwaysFail());

        var result = await h.Service.IssueAsync(invoice, User);

        result.Status.Should().Be("ISSUED");
        (await Ledger(h)).Should().ContainSingle();
        h.Jobs.Should().Contain(j => j.Method.Name == "AppendAsync", "the event is still recorded");
        (await h.Demand.SaleOrderLines.AsNoTracking().SingleAsync()).InvoicedQty.Should().Be(0m, "logged for reconciliation");
    }

    // ── Issued, then paid (P7-04 + P7-05 together) ───────────────────────────

    private static CustomerPaymentService PaymentsOn(Harness h) =>
        new(h.Db, new CustomerLedgerService(h.Db), h.Names.Object, h.Lookups.Object, h.Clock);

    [Fact]
    public async Task An_invoice_issued_here_is_paid_down_to_nothing_by_a_payment_and_the_ledger_nets_to_zero()
    {
        var h       = NewHarness();
        var partner = Guid.NewGuid();
        var order   = await SeedOrder(h, "FULFILLED", partner, new SoLineSpec(100m, 40m, Discount: 10m, Tax: 5m));
        var invoice = await Draft(h, order);
        await h.Service.IssueAsync(invoice, User);

        var paid = await PaymentsOn(h).RecordPaymentAsync(partner, 3780m, "BANK_TRANSFER",
            new CustomerPaymentDetails("PKR", BankReference: "TRX-1"), User);

        paid.Allocations.Should().ContainSingle().Which.Should().Be(
            new AppliedPaymentAllocation(invoice, "SINV-20260920-0001", 3780m, 0m, "PAID"));
        paid.PartnerBalance.Should().Be(0m);

        var stored = await Load(h, invoice);
        (stored.Status, stored.AmountPaid, stored.BalanceDue).Should().Be(("PAID", 3780m, 0m));

        (await Ledger(h)).Select(e => (e.SequenceNo, e.EntryType, e.DebitAmount, e.CreditAmount, e.RunningBalance)).Should().Equal(
            (1, "INVOICE", 3780m, 0m, 3780m),
            (2, "PAYMENT", 0m, 3780m, 0m));
    }

    [Fact]
    public async Task A_draft_invoice_has_no_receivable_so_a_payment_cannot_pay_it_until_it_is_issued()
    {
        var h       = NewHarness();
        var partner = Guid.NewGuid();
        var order   = await SeedOrder(h, "FULFILLED", partner, new SoLineSpec(100m, 40m, Fulfilled: 100m));
        var invoice = await Draft(h, order);

        var beforeIssue = await PaymentsOn(h).RecordPaymentAsync(partner, 1000m, "CASH", new CustomerPaymentDetails("PKR"), User);
        beforeIssue.Allocations.Should().BeEmpty("nothing to pay down yet — the money is held on account");
        (await Load(h, invoice)).AmountPaid.Should().Be(0m);

        await h.Service.IssueAsync(invoice, User);

        var afterIssue = await PaymentsOn(h).RecordPaymentAsync(partner, 1000m, "CASH", new CustomerPaymentDetails("PKR"), User);
        afterIssue.Allocations.Should().ContainSingle().Which.InvoiceStatus.Should().Be("PARTIALLY_PAID");
    }

    // ── Tenancy ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Another_organization_cannot_issue_or_see_an_invoice()
    {
        var a       = NewHarness();
        var invoice = await Draft(a, await SeedOrder(a));

        var b = NewHarness(Guid.NewGuid(), a.DbName);

        var act = async () => await b.Service.IssueAsync(invoice, User);

        await act.Should().ThrowAsync<NotFoundException>();
        (await a.Db.CustomerLedgerEntries.CountAsync()).Should().Be(0);
        (await Load(a, invoice)).Status.Should().Be("DRAFT");
    }

    [Fact]
    public async Task Another_organizations_order_cannot_be_billed()
    {
        var a     = NewHarness();
        var order = await SeedOrder(a);

        var b = NewHarness(Guid.NewGuid(), a.DbName);
        var delivery = SeedDelivery(b, order); // B's reader claims a delivery for A's order

        var act = async () => await b.Service.CreateFromFulfillmentAsync(delivery, User);

        await act.Should().ThrowAsync<NotFoundException>("the tenant filter makes A's order simply not exist for B");
        (await b.Db.SalesInvoices.CountAsync()).Should().Be(0);
    }
}

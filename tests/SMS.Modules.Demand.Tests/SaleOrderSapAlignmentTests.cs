using FluentAssertions;
using FluentValidation.TestHelper;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Services;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>Finance's tax codes, as SMS.Shared lets Demand see them. Counts every lookup.</summary>
internal sealed class FakeTaxCodes : ITaxCodeLookup
{
    public Dictionary<Guid, TaxCodeInfo> Codes { get; } = [];
    public int GetCalls { get; private set; }

    public TaxCodeInfo Add(string code, decimal rate, string usage = TaxCodeUsage.Sales, bool active = true, bool isDefault = false)
    {
        var info = new TaxCodeInfo(Guid.NewGuid(), code, $"{code} name", rate, usage, isDefault, active);
        Codes[info.Uuid] = info;
        return info;
    }

    public void Replace(TaxCodeInfo info) => Codes[info.Uuid] = info;

    public Task<TaxCodeInfo?> GetAsync(Guid uuid, CancellationToken ct = default)
    {
        GetCalls++;
        return Task.FromResult(Codes.GetValueOrDefault(uuid));
    }

    public Task<IReadOnlyList<TaxCodeInfo>> ListActiveAsync(string side, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TaxCodeInfo>>(
            [.. Codes.Values.Where(c => c.IsActive && TaxCodeUsage.Allows(c.Usage, side))]);

    public Task<TaxCodeInfo?> GetDefaultAsync(string side, CancellationToken ct = default) =>
        Task.FromResult(Codes.Values.FirstOrDefault(c => c.IsActive && c.IsDefault && TaxCodeUsage.Allows(c.Usage, side)));
}

/// <summary>Finance's exchange rates: answers only the pairs it is given, and records every question.</summary>
internal sealed class FakeExchangeRates : IExchangeRateProvider
{
    public Dictionary<(string From, string To), ExchangeRateQuote> Quotes { get; } = [];
    public List<(string From, string To, DateTime AsOf)> Calls { get; } = [];

    public void Add(string from, string to, decimal rate, bool inverted = false) =>
        Quotes[(from, to)] = new ExchangeRateQuote(from, to, rate, new DateTime(2026, 1, 1), inverted);

    public Task<ExchangeRateQuote?> GetRateAsync(string fromCurrencyCode, string toCurrencyCode, DateTime asOf, CancellationToken ct = default)
    {
        Calls.Add((fromCurrencyCode, toCurrencyCode, asOf));
        return Task.FromResult(Quotes.GetValueOrDefault((fromCurrencyCode, toCurrencyCode)));
    }
}

internal sealed class FakeCurrencyCodes : ICurrencyCodeLookup
{
    public Dictionary<Guid, string?> Codes { get; } = [];
    public Task<string?> GetCodeAsync(Guid currencyId, CancellationToken ct = default) =>
        Task.FromResult(Codes.GetValueOrDefault(currencyId));
}

/// <summary>
/// SAP alignment (docs/finance/SAP-ALIGNMENT-PLAN.md) on the sale order: a tax code per line with its rate
/// snapshotted (S-3), the 0–100 cap on a typed percentage, a price rule quoted in another currency converted
/// into the order's, and refusing to cancel an order that still has invoices standing (S-7).
/// </summary>
public class SaleOrderSapAlignmentTests
{
    private const int User = 7;
    private static readonly Guid Pkr = Guid.NewGuid();
    private static readonly Guid Usd = Guid.NewGuid();
    private static readonly Guid Eur = Guid.NewGuid();
    private static readonly Guid NoCode = Guid.NewGuid();
    private static readonly DateTime OrderDate = new(2026, 9, 15);

    private sealed class Harness
    {
        public required DemandDbContext Db;
        public required SaleOrderService Service;
        public required Mock<IPricingService> Pricing;
        public required FakeTaxCodes TaxCodes;
        public required FakeExchangeRates Rates;
        public required Mock<ISaleOrderInvoiceLookup> Invoices;
        public required Mock<IStockReservationService> Stock;
        public required List<Job> Jobs;
    }

    private static Harness NewHarness(bool withTaxCodes = true, bool withRates = true, bool withInvoices = true, bool withBaseCurrency = true)
    {
        var tenant = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var db = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);

        var orgCurrency = new Mock<IOrganizationCurrencyService>();
        orgCurrency.Setup(c => c.GetBaseCurrencyIdAsync(It.IsAny<Guid>())).ReturnsAsync(withBaseCurrency ? Pkr : null);
        var numbers = new Mock<IDocumentNumberGenerator>();
        numbers.Setup(n => n.NextAsync("SO", It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("SO-2026-00001");

        var jobs = new List<Job>();
        var jobClient = new Mock<IBackgroundJobClient>();
        jobClient.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()))
                 .Callback<Job, IState>((job, _) => jobs.Add(job)).Returns("fake-job-id");

        var availabilityCheck = new Mock<IAvailabilityCheckService>();
        availabilityCheck.Setup(a => a.CheckAndReserveAsync(It.IsAny<Guid>(), It.IsAny<int>()))
            .ReturnsAsync((IReadOnlyList<LineReservation>)new List<LineReservation>());

        var codes = new FakeCurrencyCodes();
        codes.Codes[Pkr] = "PKR";
        codes.Codes[Usd] = " USD ";   // as typed into the catalog — trimmed before it is used
        codes.Codes[Eur] = "EUR";
        codes.Codes[NoCode] = null;

        var pricing  = new Mock<IPricingService>();
        var taxCodes = new FakeTaxCodes();
        var rates    = new FakeExchangeRates();
        var invoices = new Mock<ISaleOrderInvoiceLookup>();
        invoices.Setup(i => i.GetLiveInvoicesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<SaleOrderInvoiceRef>)[]);
        var stock = new Mock<IStockReservationService>();

        var service = new SaleOrderService(
            db, tenant, orgCurrency.Object, numbers.Object, pricing.Object, stock.Object,
            Mock.Of<ITimelineService>(), jobClient.Object, availabilityCheck.Object,
            Mock.Of<IPurchaseOrderService>(), Mock.Of<ISaleOrderEmailService>(),
            variants: null, availability: null,
            taxCodes: withTaxCodes ? taxCodes : null,
            exchangeRates: withRates ? rates : null,
            currencyCodes: withRates ? codes : null,
            invoices: withInvoices ? invoices.Object : null);

        return new Harness
        {
            Db = db, Service = service, Pricing = pricing, TaxCodes = taxCodes, Rates = rates,
            Invoices = invoices, Stock = stock, Jobs = jobs
        };
    }

    private static void Price(Harness h, Guid variant, decimal price, Guid? currency) =>
        h.Pricing.Setup(p => p.ResolveSalePriceAsync(variant, It.IsAny<Guid?>(), It.IsAny<decimal>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new SalePriceResolution(true, price, currency, PriceResolutionTier.Contract, Guid.NewGuid()));

    private static CreateSaleOrderRequest Order(Guid currency, params CreateSaleOrderLineRequest[] lines) => new()
    {
        PartnerId = Guid.NewGuid(), CurrencyId = currency, OrderDate = OrderDate, DeliveryMode = "SELF_PICKUP", Lines = [.. lines]
    };

    private static CreateSaleOrderLineRequest Line(Guid variant, decimal qty = 1m, decimal discount = 0m, decimal tax = 0m, Guid? code = null) =>
        new() { VariantUuid = variant, Quantity = qty, DiscountPercent = discount, TaxPercent = tax, TaxCodeUuid = code };

    private static Task<SaleOrder> Stored(Harness h, Guid uuid) =>
        h.Db.SaleOrders.AsNoTracking().Include(o => o.Lines).SingleAsync(o => o.UUID == uuid);

    // ── S-3: a tax code per line ─────────────────────────────────────────────

    [Fact]
    public async Task A_sales_tax_code_is_kept_on_the_line_with_its_rate_and_the_typed_percentage_is_ignored()
    {
        var h = NewHarness();
        var gst = h.TaxCodes.Add("GST17", 17m);
        var variant = Guid.NewGuid();
        Price(h, variant, 100m, Pkr);

        // 99% typed alongside the code: the code decides.
        var uuid = await h.Service.CreateAsync(Order(Pkr, Line(variant, 10m, discount: 10m, tax: 99m, code: gst.Uuid)), User);

        var line = (await Stored(h, uuid)).Lines.Single();
        line.TaxCodeUuid.Should().Be(gst.Uuid);
        line.TaxCode.Should().Be("GST17");
        line.TaxPercent.Should().Be(17m);
        line.LineTotal.Should().Be(1053m, "10 × 100, less 10%, plus 17%");

        var model = (await h.Service.GetByIdAsync(uuid))!;
        model.Lines.Single().TaxCodeUuid.Should().Be(gst.Uuid);
        model.Lines.Single().TaxCode.Should().Be("GST17");
        model.TaxAmount.Should().Be(153m);
        model.GrandTotal.Should().Be(1053m);
    }

    [Fact]
    public async Task A_code_for_both_sides_can_be_used_on_a_sale()
    {
        var h = NewHarness();
        var both = h.TaxCodes.Add("STD", 18m, TaxCodeUsage.Both);
        var variant = Guid.NewGuid();
        Price(h, variant, 50m, Pkr);

        var uuid = await h.Service.CreateAsync(Order(Pkr, Line(variant, 2m, code: both.Uuid)), User);

        (await Stored(h, uuid)).Lines.Single().Should().Match<SaleOrderLine>(l => l.TaxCode == "STD" && l.TaxPercent == 18m);
    }

    [Fact]
    public async Task An_inactive_code_is_refused_by_name()
    {
        var h = NewHarness();
        var old = h.TaxCodes.Add("GST16", 16m, active: false);
        var variant = Guid.NewGuid();
        Price(h, variant, 50m, Pkr);

        var act = () => h.Service.CreateAsync(Order(Pkr, Line(variant, code: old.Uuid)), User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*GST16*inactive*");
        (await h.Db.SaleOrders.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_purchase_only_code_cannot_tax_a_sale()
    {
        var h = NewHarness();
        var input = h.TaxCodes.Add("INPUT17", 17m, TaxCodeUsage.Purchase);
        var variant = Guid.NewGuid();
        Price(h, variant, 50m, Pkr);

        var act = () => h.Service.CreateAsync(Order(Pkr, Line(variant, code: input.Uuid)), User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*INPUT17*purchase only*SALES or BOTH*");
        (await h.Db.SaleOrders.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_unknown_code_or_another_organizations_is_refused()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        Price(h, variant, 50m, Pkr);
        var unknown = Guid.NewGuid();

        var act = () => h.Service.CreateAsync(Order(Pkr, Line(variant, code: unknown)), User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage($"*{unknown}*does not exist*Tax Codes*");
    }

    [Fact]
    public async Task Naming_a_code_where_codes_cannot_be_checked_is_refused_rather_than_taken_on_trust()
    {
        var h = NewHarness(withTaxCodes: false);
        var variant = Guid.NewGuid();
        Price(h, variant, 50m, Pkr);

        var act = () => h.Service.CreateAsync(Order(Pkr, Line(variant, code: Guid.NewGuid())), User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*cannot be checked*tax percentage*");
    }

    [Fact]
    public async Task One_code_on_many_lines_is_looked_up_once()
    {
        var h = NewHarness();
        var gst = h.TaxCodes.Add("GST17", 17m);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        Price(h, a, 10m, Pkr);
        Price(h, b, 20m, Pkr);

        await h.Service.CreateAsync(Order(Pkr, Line(a, code: gst.Uuid), Line(b, code: gst.Uuid), Line(a, 2m, code: gst.Uuid)), User);

        h.TaxCodes.GetCalls.Should().Be(1);
    }

    [Fact]
    public async Task A_line_without_a_code_keeps_the_typed_percentage_as_before()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        Price(h, variant, 100m, Pkr);

        var uuid = await h.Service.CreateAsync(Order(Pkr, Line(variant, 10m, discount: 10m, tax: 5m)), User);

        var line = (await Stored(h, uuid)).Lines.Single();
        line.TaxCodeUuid.Should().BeNull();
        line.TaxCode.Should().BeNull();
        line.TaxPercent.Should().Be(5m);
        line.LineTotal.Should().Be(945m);
        h.TaxCodes.GetCalls.Should().Be(0, "no code was named, so Finance is not asked");
    }

    [Fact]
    public async Task An_empty_guid_is_no_code_at_all()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        Price(h, variant, 100m, Pkr);

        var uuid = await h.Service.CreateAsync(Order(Pkr, Line(variant, tax: 8m, code: Guid.Empty)), User);

        (await Stored(h, uuid)).Lines.Single().Should().Match<SaleOrderLine>(l => l.TaxCodeUuid == null && l.TaxPercent == 8m);
    }

    [Theory]
    [InlineData(3, 33.33, 7.5)]
    [InlineData(7, 0.333, 0)]
    [InlineData(1, 1.005, 2.5)]
    [InlineData(12.5, 19.99, 12.25)]
    public async Task A_code_gives_exactly_the_same_cents_as_typing_its_rate(decimal qty, decimal price, decimal discount)
    {
        var withCode = NewHarness();
        var typed    = NewHarness();
        var gst      = withCode.TaxCodes.Add("GST17", 17m);
        var other    = withCode.TaxCodes.Add("GST5", 5m);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        foreach (var h in new[] { withCode, typed })
        {
            Price(h, a, price, Pkr);
            Price(h, b, price * 2, Pkr);
        }

        var coded = await withCode.Service.CreateAsync(Order(Pkr,
            Line(a, qty, discount, code: gst.Uuid), Line(b, qty, discount, code: other.Uuid)), User);
        var plain = await typed.Service.CreateAsync(Order(Pkr,
            Line(a, qty, discount, tax: 17m), Line(b, qty, discount, tax: 5m)), User);

        var x = await Stored(withCode, coded);
        var y = await Stored(typed, plain);
        (x.Subtotal, x.DiscountAmount, x.TaxAmount, x.GrandTotal).Should().Be((y.Subtotal, y.DiscountAmount, y.TaxAmount, y.GrandTotal));
        x.Lines.OrderBy(l => l.Id).Select(l => l.LineTotal).Should().Equal(y.Lines.OrderBy(l => l.Id).Select(l => l.LineTotal));
    }

    [Theory]
    [InlineData(100.01)]
    [InlineData(1000)]
    [InlineData(-0.01)]
    public async Task A_typed_percentage_outside_0_to_100_is_refused(decimal tax)
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        Price(h, variant, 100m, Pkr);

        var act = () => h.Service.CreateAsync(Order(Pkr, Line(variant, tax: tax)), User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*between 0 and 100*");
    }

    [Theory]
    [InlineData(17.125)]
    [InlineData(0.001)]
    public async Task A_typed_percentage_with_more_than_two_decimals_is_refused_rather_than_rounded_by_the_column(decimal tax)
    {
        // Reviewer finding: the line's total was worked at 17.125% while its decimal(5,2) column kept 17.13%, so the
        // order said one amount and every invoice raised from it — which works from the stored percentage — another.
        var h = NewHarness();
        var variant = Guid.NewGuid();
        Price(h, variant, 100m, Pkr);

        var act = () => h.Service.CreateAsync(Order(Pkr, Line(variant, 1000m, tax: tax)), User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*tax percentage*two decimal*");
        (await h.Db.SaleOrders.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(100.01)]
    [InlineData(150)]
    [InlineData(12.345)]
    public async Task A_discount_outside_0_to_100_or_with_more_than_two_decimals_is_refused(decimal discount)
    {
        // Over 100% made a negative line (and a negative order); a third decimal is lost in the decimal(5,2) column.
        var h = NewHarness();
        var variant = Guid.NewGuid();
        Price(h, variant, 100m, Pkr);

        var act = () => h.Service.CreateAsync(Order(Pkr, Line(variant, 10m, discount: discount)), User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*discount*");
        (await h.Db.SaleOrders.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(12.5)]
    [InlineData(100)]
    public async Task A_discount_from_0_to_100_with_up_to_two_decimals_is_taken(decimal discount)
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        Price(h, variant, 100m, Pkr);

        var uuid = await h.Service.CreateAsync(Order(Pkr, Line(variant, 10m, discount: discount)), User);

        (await Stored(h, uuid)).Lines.Single().DiscountPercent.Should().Be(discount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public async Task The_ends_of_the_range_are_allowed(decimal tax)
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        Price(h, variant, 100m, Pkr);

        var uuid = await h.Service.CreateAsync(Order(Pkr, Line(variant, tax: tax)), User);

        (await Stored(h, uuid)).Lines.Single().TaxPercent.Should().Be(tax);
    }

    [Fact]
    public async Task A_rate_changed_later_never_changes_an_order_already_taken()
    {
        var h = NewHarness();
        var gst = h.TaxCodes.Add("GST17", 17m);
        var variant = Guid.NewGuid();
        Price(h, variant, 100m, Pkr);
        var uuid = await h.Service.CreateAsync(Order(Pkr, Line(variant, 1m, code: gst.Uuid)), User);
        await h.Service.ConfirmAsync(uuid, User);

        h.TaxCodes.Replace(gst with { RatePercent = 18m });

        var line = (await h.Service.GetByIdAsync(uuid))!.Lines.Single();
        line.TaxPercent.Should().Be(17m, "S-3: the document keeps the rate it was taken at");
        line.LineTotal.Should().Be(117m);
    }

    // ── S-3 on a draft that is edited ────────────────────────────────────────

    [Fact]
    public async Task Editing_a_draft_that_sends_its_code_back_keeps_the_code()
    {
        var h = NewHarness();
        var gst = h.TaxCodes.Add("GST17", 17m);
        var variant = Guid.NewGuid();
        Price(h, variant, 100m, Pkr);
        var uuid = await h.Service.CreateAsync(Order(Pkr, Line(variant, 1m, code: gst.Uuid)), User);

        await h.Service.UpdateAsync(uuid, new UpdateSaleOrderRequest
        {
            CurrencyId = Pkr, DeliveryMode = "SELF_PICKUP", Lines = [Line(variant, 3m, tax: 17m, code: gst.Uuid)]
        }, User);

        var line = (await Stored(h, uuid)).Lines.Single();
        (line.TaxCodeUuid, line.TaxCode, line.TaxPercent, line.Quantity, line.LineTotal)
            .Should().Be((gst.Uuid, "GST17", 17m, 3m, 351m));
    }

    [Fact]
    public async Task Editing_a_draft_takes_the_codes_rate_as_it_is_now_like_the_price()
    {
        // A draft is re-priced on every edit (its lines are rebuilt), so its tax rate is re-read too.
        var h = NewHarness();
        var gst = h.TaxCodes.Add("GST17", 17m);
        var variant = Guid.NewGuid();
        Price(h, variant, 100m, Pkr);
        var uuid = await h.Service.CreateAsync(Order(Pkr, Line(variant, 1m, code: gst.Uuid)), User);
        h.TaxCodes.Replace(gst with { RatePercent = 18m });

        await h.Service.UpdateAsync(uuid, new UpdateSaleOrderRequest
        {
            CurrencyId = Pkr, DeliveryMode = "SELF_PICKUP", Lines = [Line(variant, 1m, code: gst.Uuid)]
        }, User);

        (await Stored(h, uuid)).Lines.Single().TaxPercent.Should().Be(18m);
    }

    [Fact]
    public async Task Editing_a_draft_without_the_code_leaves_the_line_without_one()
    {
        var h = NewHarness();
        var gst = h.TaxCodes.Add("GST17", 17m);
        var variant = Guid.NewGuid();
        Price(h, variant, 100m, Pkr);
        var uuid = await h.Service.CreateAsync(Order(Pkr, Line(variant, 1m, code: gst.Uuid)), User);

        await h.Service.UpdateAsync(uuid, new UpdateSaleOrderRequest
        {
            CurrencyId = Pkr, DeliveryMode = "SELF_PICKUP", Lines = [Line(variant, 1m, tax: 5m)]
        }, User);

        (await Stored(h, uuid)).Lines.Single().Should().Match<SaleOrderLine>(l => l.TaxCodeUuid == null && l.TaxCode == null && l.TaxPercent == 5m);
    }

    [Fact]
    public async Task An_edit_refused_for_a_bad_code_leaves_the_draft_as_it_was()
    {
        var h = NewHarness();
        var gst = h.TaxCodes.Add("GST17", 17m);
        var retired = h.TaxCodes.Add("OLD", 10m, active: false);
        var variant = Guid.NewGuid();
        Price(h, variant, 100m, Pkr);
        var uuid = await h.Service.CreateAsync(Order(Pkr, Line(variant, 1m, code: gst.Uuid)), User);

        var act = () => h.Service.UpdateAsync(uuid, new UpdateSaleOrderRequest
        {
            CurrencyId = Pkr, DeliveryMode = "SELF_PICKUP", Lines = [Line(variant, 4m, code: retired.Uuid)]
        }, User);

        await act.Should().ThrowAsync<BadRequestException>();
        var line = (await Stored(h, uuid)).Lines.Single();
        (line.TaxCode, line.Quantity).Should().Be(("GST17", 1m));
    }

    // ── The validator ────────────────────────────────────────────────────────

    private static SaleOrderLineModel ValidLine() => new()
    {
        VariantUuid = Guid.NewGuid(), Quantity = 1, UnitPrice = 100m, DiscountPercent = 0, TaxPercent = 100m,
        LineTotal = 200m, Status = "OPEN"
    };

    [Fact]
    public void The_validator_allows_a_tax_of_exactly_100_percent()
    {
        new SaleOrderLineValidator().TestValidate(ValidLine()).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(100.01)]
    [InlineData(1000)]
    [InlineData(-1)]
    public void The_validator_caps_the_tax_percentage_at_0_to_100(decimal tax)
    {
        var line = ValidLine();
        line.TaxPercent = tax;
        line.LineTotal = Math.Round(100m * (1 + tax / 100m), 2);

        new SaleOrderLineValidator().TestValidate(line).ShouldHaveValidationErrorFor(x => x.TaxPercent);
    }

    [Fact]
    public void The_validator_wants_a_coded_line_to_carry_the_codes_text_of_at_most_20_characters()
    {
        var line = ValidLine();
        line.TaxCodeUuid = Guid.NewGuid();
        new SaleOrderLineValidator().TestValidate(line).ShouldHaveValidationErrorFor(x => x.TaxCode);

        line.TaxCode = new string('X', 21);
        new SaleOrderLineValidator().TestValidate(line).ShouldHaveValidationErrorFor(x => x.TaxCode);

        line.TaxCode = "GST17";
        new SaleOrderLineValidator().TestValidate(line).ShouldNotHaveAnyValidationErrors();
    }

    // ── A price quoted in another currency ───────────────────────────────────

    [Fact]
    public async Task A_price_in_the_orders_own_currency_is_taken_as_it_is_and_no_rate_is_asked_for()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        Price(h, variant, 123.4567m, Pkr);

        var uuid = await h.Service.CreateAsync(Order(Pkr, Line(variant)), User);

        (await Stored(h, uuid)).Lines.Single().UnitPrice.Should().Be(123.4567m);
        h.Rates.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_list_price_with_no_currency_on_an_order_in_the_base_currency_is_taken_as_it_is()
    {
        // The variant's own selling price carries no currency: IPricingService says that means the base currency.
        var h = NewHarness();
        var variant = Guid.NewGuid();
        Price(h, variant, 80m, currency: null);

        var uuid = await h.Service.CreateAsync(Order(Pkr, Line(variant)), User);

        (await Stored(h, uuid)).Lines.Single().UnitPrice.Should().Be(80m);
        h.Rates.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_list_price_with_no_currency_is_in_the_base_currency_and_is_converted_into_a_foreign_order()
    {
        // Reviewer finding: a PKR list price on a USD order was taken as 28000 USD — a price 280 times too high.
        var h = NewHarness();
        h.Rates.Add("PKR", "USD", 1m / 280m, inverted: true);
        var variant = Guid.NewGuid();
        Price(h, variant, 28000m, currency: null);

        var uuid = await h.Service.CreateAsync(Order(Usd, Line(variant, 2m)), User);

        var order = await Stored(h, uuid);
        order.Lines.Single().UnitPrice.Should().Be(100m);
        order.GrandTotal.Should().Be(200m);
        h.Rates.Calls.Should().ContainSingle().Which.Should().Be(("PKR", "USD", OrderDate));
    }

    [Fact]
    public async Task A_list_price_on_a_foreign_order_with_no_rate_on_file_is_refused_like_a_rule_price()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        Price(h, variant, 80m, currency: null);

        var act = () => h.Service.CreateAsync(Order(Usd, Line(variant)), User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*PKR*USD*no PKR → USD exchange rate*Settings → Exchange Rates*");
        (await h.Db.SaleOrders.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Without_a_base_currency_a_list_price_is_taken_as_it_is_since_its_currency_is_unknown()
    {
        var h = NewHarness(withBaseCurrency: false);
        var variant = Guid.NewGuid();
        Price(h, variant, 80m, currency: null);

        var uuid = await h.Service.CreateAsync(Order(Usd, Line(variant)), User);

        (await Stored(h, uuid)).Lines.Single().UnitPrice.Should().Be(80m);
        h.Rates.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Where_rates_cannot_be_read_a_list_price_is_taken_as_it_is_as_before()
    {
        var h = NewHarness(withRates: false);
        var variant = Guid.NewGuid();
        Price(h, variant, 80m, currency: null);

        var uuid = await h.Service.CreateAsync(Order(Usd, Line(variant)), User);

        (await Stored(h, uuid)).Lines.Single().UnitPrice.Should().Be(80m);
    }

    [Fact]
    public async Task A_usd_contract_on_a_pkr_order_is_converted_at_the_rate_for_the_order_date()
    {
        var h = NewHarness();
        h.Rates.Add("USD", "PKR", 278.55m);
        var variant = Guid.NewGuid();
        Price(h, variant, 12.37m, Usd);

        var uuid = await h.Service.CreateAsync(Order(Pkr, Line(variant, 3m, tax: 10m)), User);

        var order = await Stored(h, uuid);
        order.Lines.Single().UnitPrice.Should().Be(3445.66m, "12.37 × 278.55 = 3445.6635, to the cent");
        order.Subtotal.Should().Be(10336.98m, "the totals are built from the converted price");
        order.GrandTotal.Should().Be(11370.68m);
        h.Rates.Calls.Should().ContainSingle().Which.Should().Be(("USD", "PKR", OrderDate));
    }

    [Fact]
    public async Task A_rate_found_only_as_the_reciprocal_of_the_opposite_pair_is_used_as_quoted()
    {
        // The provider turns a PKR→USD rate into USD→PKR (S-4); the order just uses the rate it is given.
        var h = NewHarness();
        h.Rates.Add("PKR", "USD", 1m / 280m, inverted: true);
        var variant = Guid.NewGuid();
        Price(h, variant, 28000m, Pkr);

        var uuid = await h.Service.CreateAsync(Order(Usd, Line(variant)), User);

        (await Stored(h, uuid)).Lines.Single().UnitPrice.Should().Be(100m);
        h.Rates.Calls.Single().Should().Be(("PKR", "USD", OrderDate), "the codes are trimmed before they are asked about");
    }

    [Fact]
    public async Task With_no_rate_on_file_the_line_is_refused_naming_the_pair_and_where_to_add_one()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        Price(h, variant, 10m, Eur);

        var act = () => h.Service.CreateAsync(Order(Pkr, Line(variant)), User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*EUR*PKR*no EUR → PKR exchange rate*15 Sep 2026*Settings → Exchange Rates*");
        (await h.Db.SaleOrders.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_currency_with_no_iso_code_cannot_be_converted_and_says_so()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        Price(h, variant, 10m, NoCode);

        var act = () => h.Service.CreateAsync(Order(Pkr, Line(variant)), User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*the price's currency has no ISO code*");
    }

    [Fact]
    public async Task Where_rates_cannot_be_read_a_foreign_price_is_taken_as_quoted_as_before()
    {
        var h = NewHarness(withRates: false);
        var variant = Guid.NewGuid();
        Price(h, variant, 10m, Usd);

        var uuid = await h.Service.CreateAsync(Order(Pkr, Line(variant)), User);

        (await Stored(h, uuid)).Lines.Single().UnitPrice.Should().Be(10m);
    }

    [Fact]
    public async Task Editing_a_draft_converts_at_the_rate_for_its_own_order_date()
    {
        var h = NewHarness();
        h.Rates.Add("USD", "PKR", 280m);
        var variant = Guid.NewGuid();
        Price(h, variant, 2m, Usd);
        var uuid = await h.Service.CreateAsync(Order(Pkr, Line(variant)), User);
        h.Rates.Calls.Clear();
        h.Rates.Add("USD", "PKR", 281m);

        await h.Service.UpdateAsync(uuid, new UpdateSaleOrderRequest
        {
            CurrencyId = Pkr, DeliveryMode = "SELF_PICKUP", Lines = [Line(variant, 5m)]
        }, User);

        (await Stored(h, uuid)).Lines.Single().UnitPrice.Should().Be(562m);
        h.Rates.Calls.Should().ContainSingle().Which.AsOf.Should().Be(OrderDate);
    }

    [Fact]
    public async Task Moving_a_draft_into_the_rules_own_currency_takes_the_price_as_quoted()
    {
        var h = NewHarness();
        h.Rates.Add("USD", "PKR", 280m);
        var variant = Guid.NewGuid();
        Price(h, variant, 2m, Usd);
        var uuid = await h.Service.CreateAsync(Order(Pkr, Line(variant)), User);

        await h.Service.UpdateAsync(uuid, new UpdateSaleOrderRequest
        {
            CurrencyId = Usd, DeliveryMode = "SELF_PICKUP", Lines = [Line(variant)]
        }, User);

        var order = await Stored(h, uuid);
        order.CurrencyId.Should().Be(Usd);
        order.Lines.Single().UnitPrice.Should().Be(2m);
    }

    // ── S-7: an order with invoices still standing cannot be cancelled ───────

    [Fact]
    public async Task An_order_with_invoices_still_standing_cannot_be_cancelled_and_nothing_is_released()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        Price(h, variant, 10m, Pkr);
        var uuid = await h.Service.CreateAsync(Order(Pkr, Line(variant)), User);
        await h.Service.ConfirmAsync(uuid, User);
        h.Jobs.Clear();
        h.Invoices.Setup(i => i.GetLiveInvoicesAsync(uuid, It.IsAny<CancellationToken>())).ReturnsAsync(
            (IReadOnlyList<SaleOrderInvoiceRef>)
            [
                new SaleOrderInvoiceRef(Guid.NewGuid(), "SINV-20260920-0001", "ISSUED"),
                new SaleOrderInvoiceRef(Guid.NewGuid(), "SINV-20260921-0002", "DRAFT")
            ]);

        var act = () => h.Service.CancelAsync(uuid, User, "Customer changed their mind");

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*SO-2026-00001*SINV-20260920-0001 (ISSUED), SINV-20260921-0002 (DRAFT)*Cancel its invoices first*");
        (await Stored(h, uuid)).Status.Should().Be("CONFIRMED");
        h.Stock.Verify(s => s.ReleaseBySourceAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        h.Jobs.Should().BeEmpty("no timeline event and no cancellation email for an order that was not cancelled");
    }

    [Fact]
    public async Task An_order_whose_invoices_were_all_cancelled_can_be_cancelled()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        Price(h, variant, 10m, Pkr);
        var uuid = await h.Service.CreateAsync(Order(Pkr, Line(variant)), User);

        (await h.Service.CancelAsync(uuid, User, null)).Should().BeTrue();

        (await Stored(h, uuid)).Status.Should().Be("CANCELLED");
        h.Invoices.Verify(i => i.GetLiveInvoicesAsync(uuid, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task The_status_refusal_comes_before_finance_is_asked()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        Price(h, variant, 10m, Pkr);
        var uuid = await h.Service.CreateAsync(Order(Pkr, Line(variant)), User);
        await h.Service.CancelAsync(uuid, User, null);
        h.Invoices.Invocations.Clear();

        var act = () => h.Service.CancelAsync(uuid, User, null);

        await act.Should().ThrowAsync<BadRequestException>();
        h.Invoices.Verify(i => i.GetLiveInvoicesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Without_finance_an_order_is_cancelled_as_before()
    {
        var h = NewHarness(withInvoices: false);
        var variant = Guid.NewGuid();
        Price(h, variant, 10m, Pkr);
        var uuid = await h.Service.CreateAsync(Order(Pkr, Line(variant)), User);

        (await h.Service.CancelAsync(uuid, User, null)).Should().BeTrue();
    }
}

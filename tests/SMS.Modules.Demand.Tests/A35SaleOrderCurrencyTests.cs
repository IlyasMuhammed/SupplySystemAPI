using FluentAssertions;
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

/// <summary>
/// A35 P3-09 / P3-12 / P3-15 — the sale order's currency: defaulted from the customer (D-14), overridable while DRAFT
/// (T-C4-03), rate locked at CONFIRMED with the base amounts (T-C5-01/02/04/05/07/09), refused without a rate (T-C5-10,
/// D-5), and inherited from a quotation but re-locked at confirm (T-C5-06).
/// </summary>
public class A35SaleOrderCurrencyTests
{
    private const int User = 7;
    private static readonly Guid Pkr = Guid.NewGuid();
    private static readonly Guid Aed = Guid.NewGuid();
    private static readonly Guid Eur = Guid.NewGuid();
    private static readonly Guid Jpy = Guid.NewGuid();
    private static readonly Guid Mxn = Guid.NewGuid();
    private static readonly Guid AedCustomer = Guid.NewGuid();

    private sealed class H
    {
        public required DemandDbContext Db;
        public required SaleOrderService Svc;
        public required Mock<IPricingService> Pricing;
        public required FakeCurrencyService Currency;
        public required Mock<IAvailabilityCheckService> Availability;
        public required StaticTenantContext Tenant;
    }

    private static H NewHarness(bool withFinance = true, FakeOrgCurrencies? orgCurrencies = null)
    {
        var tenant = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var db = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);

        var orgCurrency = new Mock<IOrganizationCurrencyService>();
        orgCurrency.Setup(c => c.GetBaseCurrencyIdAsync(It.IsAny<Guid>())).ReturnsAsync(Pkr);
        orgCurrency.Setup(c => c.GetBaseCurrencyIdAsync(It.IsAny<Guid>(), It.IsAny<TransactionDomain>(), It.IsAny<CancellationToken>())).ReturnsAsync(Pkr);
        var seq = 0;
        var numbers = new Mock<IDocumentNumberGenerator>();
        numbers.Setup(n => n.NextAsync("SO", It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => $"SO-2026-{++seq:D5}");
        var jobClient = new Mock<IBackgroundJobClient>();
        jobClient.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("job");
        var availability = new Mock<IAvailabilityCheckService>();
        availability.Setup(a => a.CheckAndReserveAsync(It.IsAny<Guid>(), It.IsAny<int>()))
            .ReturnsAsync((IReadOnlyList<LineReservation>)new List<LineReservation>());

        var currency = new FakeCurrencyService();
        currency.Bases[TransactionDomain.Sale] = Pkr;
        currency.Bases[TransactionDomain.Purchase] = Pkr;
        currency.Codes[Pkr] = "PKR"; currency.Codes[Aed] = "AED"; currency.Codes[Eur] = "EUR"; currency.Codes[Mxn] = "MXN"; currency.Codes[Jpy] = "JPY";
        currency.Decimals[Jpy] = 0;
        var partners = new FakePartnerCurrencyDefaults(currency.Bases);
        partners.Partners[AedCustomer] = (Aed, null);

        var pricing = new Mock<IPricingService>();
        var svc = new SaleOrderService(
            db, tenant, orgCurrency.Object, numbers.Object, pricing.Object, Mock.Of<IStockReservationService>(),
            Mock.Of<ITimelineService>(), jobClient.Object, availability.Object,
            Mock.Of<IPurchaseOrderService>(), Mock.Of<ISaleOrderEmailService>(),
            currency: withFinance ? currency : null, partnerCurrencies: partners, orgCurrencies: orgCurrencies);

        return new H { Db = db, Svc = svc, Pricing = pricing, Currency = currency, Availability = availability, Tenant = tenant };
    }

    private static void Price(H h, decimal price) =>
        h.Pricing.Setup(p => p.ResolveSalePriceAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<decimal>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new SalePriceResolution(true, price, null, PriceResolutionTier.Contract, Guid.NewGuid()));

    private static CreateSaleOrderRequest Order(Guid? currency, Guid? partner = null, params CreateSaleOrderLineRequest[] lines) => new()
    {
        PartnerId = partner ?? Guid.NewGuid(), CurrencyId = currency, OrderDate = new DateTime(2026, 10, 7), DeliveryMode = "SELF_PICKUP",
        Lines = lines.Length == 0 ? [Line(50m)] : [.. lines]
    };

    private static CreateSaleOrderLineRequest Line(decimal qty, decimal discount = 0m, decimal tax = 0m) =>
        new() { VariantUuid = Guid.NewGuid(), Quantity = qty, DiscountPercent = discount, TaxPercent = tax };

    private static Task<SaleOrder> Stored(H h, Guid uuid) =>
        h.Db.SaleOrders.AsNoTracking().Include(o => o.Lines).SingleAsync(o => o.UUID == uuid);

    // ── defaults and override (D-14, T-C4-03) ────────────────────────────────

    [Fact]
    public async Task Without_a_currency_the_order_takes_the_customers_default_sale_currency()
    {
        var h = NewHarness();
        Price(h, 120m);
        var uuid = await h.Svc.CreateAsync(Order(null, AedCustomer), User);
        (await Stored(h, uuid)).CurrencyId.Should().Be(Aed);
    }

    [Fact]
    public async Task A_customer_without_a_default_gets_the_sale_base()
    {
        var h = NewHarness();
        Price(h, 120m);
        var uuid = await h.Svc.CreateAsync(Order(null), User);
        (await Stored(h, uuid)).CurrencyId.Should().Be(Pkr);
    }

    [Fact]
    public async Task T_C4_03_an_explicit_currency_overrides_the_customers_default()
    {
        var h = NewHarness();
        Price(h, 120m);
        var uuid = await h.Svc.CreateAsync(Order(Eur, AedCustomer), User);
        (await Stored(h, uuid)).CurrencyId.Should().Be(Eur);
    }

    [Fact]
    public async Task An_update_that_names_no_currency_keeps_the_orders_currency()
    {
        var h = NewHarness();
        Price(h, 120m);
        var uuid = await h.Svc.CreateAsync(Order(Eur), User);
        await h.Svc.UpdateAsync(uuid, new UpdateSaleOrderRequest { DeliveryMode = "SELF_PICKUP", Lines = [Line(5m)] }, User);
        (await Stored(h, uuid)).CurrencyId.Should().Be(Eur);
    }

    [Fact]
    public async Task A_picked_currency_that_is_not_an_active_org_currency_is_refused()
    {
        var org = new FakeOrgCurrencies();
        org.Add(Pkr, "PKR");
        org.Add(Eur, "EUR", active: false);
        var h = NewHarness(orgCurrencies: org);
        Price(h, 120m);
        var act = () => h.Svc.CreateAsync(Order(Eur), User);
        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("EUR is not an active currency of this organization.");
    }

    [Fact]
    public async Task The_sale_base_is_always_accepted_even_when_it_is_not_a_configured_org_currency()
    {
        var org = new FakeOrgCurrencies();
        org.Add(Eur, "EUR");                    // configured, but PKR (the base) is not
        var h = NewHarness(orgCurrencies: org);
        Price(h, 120m);
        var uuid = await h.Svc.CreateAsync(Order(Pkr), User);
        (await Stored(h, uuid)).CurrencyId.Should().Be(Pkr);

        var empty = NewHarness(orgCurrencies: new FakeOrgCurrencies()); // nothing configured yet: pre-A35 behaviour
        Price(empty, 120m);
        (await Stored(empty, await empty.Svc.CreateAsync(Order(Aed), User))).CurrencyId.Should().Be(Aed);
    }

    [Fact]
    public async Task A_draft_has_no_rate_and_no_base_amounts()
    {
        var h = NewHarness();
        Price(h, 120m);
        var uuid = await h.Svc.CreateAsync(Order(Aed), User);
        var order = await Stored(h, uuid);
        order.ExchangeRate.Should().BeNull();
        order.GrandTotalBase.Should().BeNull();
        order.Lines.Single().LineTotalBase.Should().BeNull();
    }

    // ── lock at CONFIRMED ────────────────────────────────────────────────────

    [Fact]
    public async Task T_C5_01_confirmed_in_AED_locks_the_rate_and_stores_the_base_amounts()
    {
        var h = NewHarness();
        h.Currency.Rate(Aed, 76.30m);
        Price(h, 120m);
        var uuid = await h.Svc.CreateAsync(Order(Aed, null, Line(50m)), User);

        await h.Svc.ConfirmAsync(uuid, User);

        var order = await Stored(h, uuid);
        order.ExchangeRate.Should().Be(76.30m);
        order.BaseCurrencyId.Should().Be(Pkr);
        order.RateLockedAt.Should().NotBeNull();
        order.Lines.Single().UnitPriceBase.Should().Be(9_156m);
        order.Lines.Single().LineTotalBase.Should().Be(457_800m);
        order.GrandTotalBase.Should().Be(457_800m);
        h.Currency.Locks.Single().Should().Be((h.Tenant.OrganizationId, Aed, DateOnly.FromDateTime(DateTime.UtcNow), TransactionDomain.Sale));

        var model = (await h.Svc.GetByIdAsync(uuid))!;
        model.ExchangeRate.Should().Be(76.30m);
        model.GrandTotalBase.Should().Be(457_800m);
        model.Lines.Single().LineTotalBase.Should().Be(457_800m);
    }

    [Fact]
    public async Task T_C5_02_confirmed_in_the_base_has_rate_1_and_identical_amounts()
    {
        var h = NewHarness();
        Price(h, 9_200m);
        var uuid = await h.Svc.CreateAsync(Order(Pkr, null, Line(25m, discount: 5m, tax: 17m)), User);

        await h.Svc.ConfirmAsync(uuid, User);

        var order = await Stored(h, uuid);
        order.ExchangeRate.Should().Be(1m);
        order.Lines.Single().UnitPriceBase.Should().Be(9_200m);
        order.Lines.Single().LineTotalBase.Should().Be(order.Lines.Single().LineTotal);
        order.GrandTotalBase.Should().Be(order.GrandTotal);
        order.SubtotalBase.Should().Be(order.Subtotal);
        order.TaxAmountBase.Should().Be(order.TaxAmount);
        order.DiscountAmountBase.Should().Be(order.DiscountAmount);
    }

    [Fact]
    public async Task T_C5_10_no_rate_at_confirmation_is_a_400_and_nothing_is_reserved()
    {
        var h = NewHarness();
        Price(h, 10m);
        var uuid = await h.Svc.CreateAsync(Order(Mxn), User);

        var act = () => h.Svc.ConfirmAsync(uuid, User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage($"No exchange rate for MXN on {DateTime.UtcNow:yyyy-MM-dd}. Add one under Settings → Exchange Rates.");
        (await Stored(h, uuid)).Status.Should().Be("DRAFT");
        h.Availability.Verify(a => a.CheckAndReserveAsync(It.IsAny<Guid>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task T_C5_05_a_later_rate_change_does_not_touch_a_confirmed_order()
    {
        var h = NewHarness();
        h.Currency.Rate(Aed, 76.30m);
        Price(h, 120m);
        var uuid = await h.Svc.CreateAsync(Order(Aed, null, Line(50m)), User);
        await h.Svc.ConfirmAsync(uuid, User);

        h.Currency.Rates.Clear();
        h.Currency.Rate(Aed, 80m);
        await h.Svc.GetByIdAsync(uuid);
        await h.Svc.UpdateCustomerPoAsync(uuid, new UpdateSaleOrderCustomerPoRequest { CustomerPoReference = "PO-9" }, User);

        var order = await Stored(h, uuid);
        order.ExchangeRate.Should().Be(76.30m);
        order.GrandTotalBase.Should().Be(457_800m);
        h.Currency.Locks.Should().ContainSingle();
    }

    [Fact]
    public async Task T_C5_07_a_JPY_order_converts_at_the_base_decimals()
    {
        var h = NewHarness();
        h.Currency.Rate(Jpy, 1.86m);
        Price(h, 1_234m);
        var uuid = await h.Svc.CreateAsync(Order(Jpy, null, Line(10m)), User);

        await h.Svc.ConfirmAsync(uuid, User);

        var line = (await Stored(h, uuid)).Lines.Single();
        line.LineTotal.Should().Be(12_340m);
        line.LineTotalBase.Should().Be(22_952.40m);
        line.UnitPriceBase.Should().Be(2_295.24m);
    }

    [Fact]
    public async Task D_13_a_JPY_orders_own_amounts_are_rounded_to_whole_yen()
    {
        var org = new FakeOrgCurrencies();
        org.Add(Jpy, "JPY", decimals: 0);
        org.Add(Pkr, "PKR");
        var h = NewHarness(orgCurrencies: org);
        Price(h, 1_234m);
        var uuid = await h.Svc.CreateAsync(Order(Jpy, null, Line(1m, tax: 17.25m)), User);

        var order = await Stored(h, uuid);
        order.Lines.Single().LineTotal.Should().Be(1_447m, "1,234 × 1.1725 = 1,446.865 → ¥1,447");
        order.TaxAmount.Should().Be(213m);
        order.GrandTotal.Should().Be(1_447m);
    }

    [Fact]
    public async Task T_C5_09_the_header_base_total_is_the_sum_of_the_line_base_totals()
    {
        var h = NewHarness();
        h.Currency.Rate(Aed, 76.3333m);
        Price(h, 33.33m);
        var uuid = await h.Svc.CreateAsync(Order(Aed, null,
            Line(3m, 5m, 17m), Line(7m, 0m, 17m), Line(1.5m, 12.5m, 0m), Line(11m), Line(2m, 3m, 5m)), User);

        await h.Svc.ConfirmAsync(uuid, User);

        var order = await Stored(h, uuid);
        order.GrandTotalBase.Should().Be(order.Lines.Sum(l => l.LineTotalBase));
        (order.SubtotalBase - order.DiscountAmountBase + order.TaxAmountBase).Should().Be(order.GrandTotalBase);
        order.TaxAmountBase.Should().Be(Math.Round(order.TaxAmount * 76.3333m, 2, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public async Task Without_Finance_a_base_currency_order_still_locks_at_1_and_a_foreign_one_stays_unlocked()
    {
        var h = NewHarness(withFinance: false);
        Price(h, 10m);
        var pkr = await h.Svc.CreateAsync(Order(Pkr), User);
        var aed = await h.Svc.CreateAsync(Order(Aed), User);

        await h.Svc.ConfirmAsync(pkr, User);
        await h.Svc.ConfirmAsync(aed, User);

        (await Stored(h, pkr)).ExchangeRate.Should().Be(1m);
        var foreign = await Stored(h, aed);
        foreign.Status.Should().Be("CONFIRMED");
        foreign.ExchangeRate.Should().BeNull();
    }

    // ── from a quotation (T-C5-06) ───────────────────────────────────────────

    [Fact]
    public async Task T_C5_06_an_order_from_a_quotation_inherits_its_currency_and_relocks_at_confirm()
    {
        var h = NewHarness();
        var quotation = new SaleQuotation
        {
            OrganizationId = h.Tenant.OrganizationId, QuotationNumber = "SQ-1", PartnerId = Guid.NewGuid(), CurrencyId = Eur,
            ValidFrom = DateTime.UtcNow.Date, ValidTo = DateTime.UtcNow.Date.AddDays(10), Status = "CONVERTED",
            SentAt = DateTime.UtcNow.AddDays(-3), ExchangeRate = 300m, BaseCurrencyId = Pkr
        };
        h.Db.SaleQuotations.Add(quotation);
        await h.Db.SaveChangesAsync();
        h.Currency.Rate(Eur, 316.48m);

        var uuid = await h.Svc.CreateFromQuotationAsync(new CreateSaleOrderFromQuotationCommand
        {
            SourceQuotationUuid = quotation.UUID, DeliveryMode = "SELF_PICKUP",
            Lines = [new QuotedSaleOrderLine { VariantUuid = Guid.NewGuid(), Quantity = 2m, UnitPrice = 100m }]
        }, User);

        var draft = await Stored(h, uuid);
        draft.CurrencyId.Should().Be(Eur);
        draft.ExchangeRate.Should().BeNull("not inherited from the quotation");

        await h.Svc.ConfirmAsync(uuid, User);
        var order = await Stored(h, uuid);
        order.ExchangeRate.Should().Be(316.48m, "re-locked at the confirm date, not the quotation's 300");
        order.GrandTotalBase.Should().Be(63_296m);
    }
}

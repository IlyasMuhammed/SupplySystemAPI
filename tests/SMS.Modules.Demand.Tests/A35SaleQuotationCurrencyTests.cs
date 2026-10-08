using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>
/// A35 P3-11 / P3-12 — the sale quotation's currency: customer default → inquiry → quotation (D-14), rate locked at SENT
/// with the line base amounts (T-C5-03), refused without a rate (D-5), a counter price accepted after SENT re-based at the
/// locked rate, and a copy that does not carry the rate.
/// </summary>
public class A35SaleQuotationCurrencyTests
{
    private const int User = 7;
    private static readonly Guid Customer    = Guid.NewGuid();
    private static readonly Guid AedCustomer = Guid.NewGuid();
    private static readonly Guid Pkr = Guid.NewGuid();
    private static readonly Guid Aed = Guid.NewGuid();
    private static readonly Guid Eur = Guid.NewGuid();
    private static readonly Guid Mxn = Guid.NewGuid();

    private sealed class H
    {
        public required DemandDbContext Db;
        public required SaleQuotationService Svc;
        public required FakeCurrencyService Currency;
        public required StaticTenantContext Tenant;
    }

    private static H NewHarness(bool withFinance = true)
    {
        var tenant = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var db = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);
        var seq = 0;
        var numbers = new Mock<IDocumentNumberGenerator>();
        numbers.Setup(n => n.NextAsync("SQ", It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(() => $"SQ-2026-{++seq:D5}");
        var partners = new Mock<IPartnerRoleLookup>();
        partners.Setup(p => p.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid id, CancellationToken _) => new PartnerRoleInfo(id, "Acme", true, false, true));
        var orgCurrency = new Mock<IOrganizationCurrencyService>();
        orgCurrency.Setup(c => c.GetBaseCurrencyIdAsync(It.IsAny<Guid>())).ReturnsAsync(Pkr);
        var pricing = new Mock<IPricingService>();
        pricing.Setup(p => p.ResolveSalePriceAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<decimal>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new SalePriceResolution(true, 100m, null, PriceResolutionTier.VariantDefault, null));
        var inquiries = new Mock<ISaleInquiryService>();
        inquiries.Setup(i => i.MarkQuotedAsync(It.IsAny<Guid>(), It.IsAny<int>()))
                 .Returns(async (Guid uuid, int _) => (await db.SaleInquiries.FirstAsync(x => x.UUID == uuid)).Status = "QUOTED");

        var currency = new FakeCurrencyService();
        currency.Bases[TransactionDomain.Sale] = Pkr;
        currency.Bases[TransactionDomain.Purchase] = Pkr;
        currency.Codes[Mxn] = "MXN";
        var partnerDefaults = new FakePartnerCurrencyDefaults(currency.Bases);
        partnerDefaults.Partners[AedCustomer] = (Aed, null);

        var svc = new SaleQuotationService(
            db, tenant, numbers.Object, partners.Object, orgCurrency.Object, pricing.Object,
            inquiries.Object, Mock.Of<ISaleOrderService>(),
            currency: withFinance ? currency : null, partnerCurrencies: partnerDefaults);
        return new H { Db = db, Svc = svc, Currency = currency, Tenant = tenant };
    }

    private static CreateSaleQuotationRequest Header(Guid? currency, Guid? partner = null) => new()
    {
        PartnerId = partner ?? Customer, CurrencyId = currency, ValidTo = DateTime.UtcNow.Date.AddDays(30),
        Lines = [new SaleQuotationLineRequest { VariantUuid = Guid.NewGuid(), Quantity = 50m, UnitPrice = 120m, DiscountPercent = 10m, TaxPercent = 5m }]
    };

    private static Task<SaleQuotation> Stored(H h, Guid uuid) =>
        h.Db.SaleQuotations.AsNoTracking().Include(q => q.Lines).SingleAsync(q => q.UUID == uuid);

    [Fact]
    public async Task Without_a_currency_the_quotation_takes_the_customers_default()
    {
        var h = NewHarness();
        var uuid = await h.Svc.CreateAsync(Header(null, AedCustomer), User);
        (await Stored(h, uuid)).CurrencyId.Should().Be(Aed);
    }

    [Fact]
    public async Task A_quotation_from_an_inquiry_inherits_the_inquiry_currency()
    {
        var h = NewHarness();
        var inquiry = new SaleInquiry
        {
            OrganizationId = h.Tenant.OrganizationId, InquiryNumber = "INQ-1", PartnerId = AedCustomer, CurrencyId = Eur,
            Status = "REVIEW_COMPLETE", ReceivedDate = DateTime.UtcNow.Date,
            Lines = { new SaleInquiryLine { OrganizationId = h.Tenant.OrganizationId, LineNumber = 1, ProductDescription = "x",
                                            VariantUuid = Guid.NewGuid(), RequestedQuantity = 2m, LineStatus = "CAN_SUPPLY" } }
        };
        h.Db.SaleInquiries.Add(inquiry);
        await h.Db.SaveChangesAsync();

        var uuid = await h.Svc.CreateFromInquiryAsync(inquiry.UUID,
            new CreateSaleQuotationFromInquiryRequest { ValidTo = DateTime.UtcNow.Date.AddDays(30) }, User);

        (await Stored(h, uuid)).CurrencyId.Should().Be(Eur, "inquiry → quotation (D-14), not the customer's AED");
    }

    [Fact]
    public async Task T_C5_03_sending_locks_the_rate_of_the_sent_date_and_the_line_base_amounts()
    {
        var h = NewHarness();
        h.Currency.Rate(Aed, 76.30m);
        var uuid = await h.Svc.CreateAsync(Header(Aed), User);
        (await Stored(h, uuid)).ExchangeRate.Should().BeNull("a draft is not locked");

        await h.Svc.SendAsync(uuid, User);

        var q = await Stored(h, uuid);
        q.ExchangeRate.Should().Be(76.30m);
        q.BaseCurrencyId.Should().Be(Pkr);
        q.RateLockedAt.Should().NotBeNull();
        h.Currency.Locks.Single().Date.Should().Be(DateOnly.FromDateTime(q.SentAt!.Value));
        var line = q.Lines.Single();
        line.UnitPriceBase.Should().Be(9_156m);
        line.DiscountAmountBase.Should().Be(45_780m);     // 50 × 120 × 10% = 600 AED
        line.TaxAmountBase.Should().Be(Math.Round(line.TaxAmount * 76.30m, 2));
        line.LineTotalBase.Should().Be(Math.Round(line.LineTotal * 76.30m, 2));

        var model = (await h.Svc.GetByIdAsync(uuid))!;
        model.ExchangeRate.Should().Be(76.30m);
        model.RateLockedAt.Should().NotBeNull();
        model.Lines.Single().LineTotalBase.Should().Be(line.LineTotalBase);
    }

    [Fact]
    public async Task A_quotation_in_the_base_locks_at_1()
    {
        var h = NewHarness();
        var uuid = await h.Svc.CreateAsync(Header(Pkr), User);
        await h.Svc.SendAsync(uuid, User);
        var q = await Stored(h, uuid);
        q.ExchangeRate.Should().Be(1m);
        q.Lines.Single().LineTotalBase.Should().Be(q.Lines.Single().LineTotal);
    }

    [Fact]
    public async Task Sending_without_a_rate_is_a_400_and_the_quotation_stays_a_draft()
    {
        var h = NewHarness();
        var uuid = await h.Svc.CreateAsync(Header(Mxn), User);

        var act = () => h.Svc.SendAsync(uuid, User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("No exchange rate for MXN on *");
        var q = await Stored(h, uuid);
        q.Status.Should().Be("DRAFT");
        q.SentAt.Should().BeNull();
    }

    [Fact]
    public async Task An_accepted_counter_price_is_rebased_at_the_locked_rate()
    {
        var h = NewHarness();
        h.Currency.Rate(Aed, 76.30m);
        var uuid = await h.Svc.CreateAsync(Header(Aed), User);
        await h.Svc.SendAsync(uuid, User);
        var lineUuid = (await Stored(h, uuid)).Lines.Single().UUID;
        h.Currency.Rates.Clear();
        h.Currency.Rate(Aed, 99m); // a later rate must not be used

        await h.Svc.RecordCustomerResponseAsync(uuid, lineUuid, new RecordCustomerResponseRequest { Response = "COUNTER", CounterPrice = 100m }, User);
        await h.Svc.RecordCustomerResponseAsync(uuid, lineUuid, new RecordCustomerResponseRequest { Response = "ACCEPTED", AcceptCounterPrice = true }, User);

        var q = await Stored(h, uuid);
        q.ExchangeRate.Should().Be(76.30m);
        q.Lines.Single().UnitPrice.Should().Be(100m);
        q.Lines.Single().UnitPriceBase.Should().Be(7_630m);
        q.Lines.Single().LineTotalBase.Should().Be(Math.Round(q.Lines.Single().LineTotal * 76.30m, 2));
    }

    [Fact]
    public async Task A_copy_keeps_the_currency_but_not_the_rate()
    {
        var h = NewHarness();
        h.Currency.Rate(Aed, 76.30m);
        var uuid = await h.Svc.CreateAsync(Header(Aed), User);
        await h.Svc.SendAsync(uuid, User);

        var copy = await Stored(h, (await h.Svc.CopyAsync(uuid, User))!.Value);

        copy.CurrencyId.Should().Be(Aed);
        copy.ExchangeRate.Should().BeNull();
        copy.Lines.Single().LineTotalBase.Should().BeNull();
    }
}

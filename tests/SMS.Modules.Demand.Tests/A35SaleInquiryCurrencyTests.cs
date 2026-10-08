using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>A35 P3-04 / P3-12 — the inquiry's currency: T-C4-01 (customer default), T-C4-02 (sale base), override, keep on update.</summary>
public class A35SaleInquiryCurrencyTests
{
    private const int User = 7;
    private static readonly Guid Customer    = Guid.NewGuid();
    private static readonly Guid AedCustomer = Guid.NewGuid();
    private static readonly Guid Pkr = Guid.NewGuid();
    private static readonly Guid Aed = Guid.NewGuid();
    private static readonly Guid Eur = Guid.NewGuid();

    private static (SaleInquiryService Svc, DemandDbContext Db) NewService(FakeOrgCurrencies? orgCurrencies = null)
    {
        var tenant = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var db = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);
        var numbers = new Mock<IDocumentNumberGenerator>();
        numbers.Setup(n => n.NextAsync("INQ", It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => $"INQ-{Guid.NewGuid():N}"[..20]);
        var partners = new Mock<IPartnerRoleLookup>();
        partners.Setup(p => p.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid id, CancellationToken _) => new PartnerRoleInfo(id, "Acme", true, false, true));
        var orgCurrency = new Mock<IOrganizationCurrencyService>();
        orgCurrency.Setup(c => c.GetBaseCurrencyIdAsync(It.IsAny<Guid>())).ReturnsAsync(Pkr);
        var defaults = new FakePartnerCurrencyDefaults(new() { [TransactionDomain.Sale] = Pkr, [TransactionDomain.Purchase] = Pkr });
        defaults.Partners[AedCustomer] = (Aed, null);
        var codes = new FakeCurrencyCodes();
        codes.Codes[Aed] = "AED";
        return (new SaleInquiryService(db, tenant, numbers.Object, partners.Object,
            partnerCurrencies: defaults, orgCurrency: orgCurrency.Object, currencyCodes: codes, orgCurrencies: orgCurrencies), db);
    }

    private static CreateSaleInquiryRequest Inquiry(Guid partner, Guid? currency = null) => new() { PartnerId = partner, CurrencyId = currency };

    [Fact]
    public async Task T_C4_01_a_customer_with_an_AED_default_gets_an_AED_inquiry()
    {
        var (svc, _) = NewService();
        var uuid = await svc.CreateAsync(Inquiry(AedCustomer), User);
        var model = (await svc.GetByIdAsync(uuid))!;
        model.CurrencyId.Should().Be(Aed);
        model.CurrencyCode.Should().Be("AED");
    }

    [Fact]
    public async Task T_C4_02_a_customer_without_a_default_gets_the_sale_base()
    {
        var (svc, _) = NewService();
        var uuid = await svc.CreateAsync(Inquiry(Customer), User);
        (await svc.GetByIdAsync(uuid))!.CurrencyId.Should().Be(Pkr);
    }

    [Fact]
    public async Task An_explicit_currency_wins_and_an_update_without_one_keeps_it()
    {
        var (svc, db) = NewService();
        var uuid = await svc.CreateAsync(Inquiry(AedCustomer, Eur), User);
        await svc.UpdateAsync(uuid, new UpdateSaleInquiryRequest { ReceivedDate = DateTime.UtcNow.Date }, User);
        (await db.SaleInquiries.AsNoTracking().SingleAsync(i => i.UUID == uuid)).CurrencyId.Should().Be(Eur);

        await svc.UpdateAsync(uuid, new UpdateSaleInquiryRequest { ReceivedDate = DateTime.UtcNow.Date, CurrencyId = Pkr }, User);
        (await db.SaleInquiries.AsNoTracking().SingleAsync(i => i.UUID == uuid)).CurrencyId.Should().Be(Pkr);
    }

    [Fact]
    public async Task An_inactive_org_currency_is_refused()
    {
        var org = new FakeOrgCurrencies();
        org.Add(Eur, "EUR", active: false);
        var (svc, _) = NewService(org);
        var act = () => svc.CreateAsync(Inquiry(Customer, Eur), User);
        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("EUR is not an active currency of this organization.");
    }
}

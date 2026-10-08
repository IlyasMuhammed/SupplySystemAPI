using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Domain;
using SMS.Modules.Suppliers.Integration;
using SMS.Modules.Suppliers.Models;
using SMS.Modules.Suppliers.Repositories;
using SMS.Modules.Suppliers.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Suppliers.Tests;

/// <summary>
/// A35 P2-07/08 + P2-10 (T-C4-01/02 resolver half, T-C4-04 resolver half) — D-9: DefaultSaleCurrency is new,
/// PreferredCurrency is the default purchase currency (API alias defaultPurchaseCurrencyId, which wins); BR-C4-01 a
/// default must be an active currency of the caller's organization; IPartnerCurrencyDefaults resolves partner default →
/// organization domain base, never reading another organization's partner.
/// </summary>
public class PartnerCurrencyDefaultsTests
{
    private static readonly Guid Pkr = Guid.NewGuid();
    private static readonly Guid Usd = Guid.NewGuid();
    private static readonly Guid Aed = Guid.NewGuid();
    private static readonly Guid Eur = Guid.NewGuid();
    private static readonly Guid Jpy = Guid.NewGuid();   // configured but inactive
    private static readonly Guid Gbp = Guid.NewGuid();   // service base

    private readonly Guid _org = Guid.NewGuid();
    private readonly Guid _otherOrg = Guid.NewGuid();
    private readonly SuppliersDbContext _db;
    private readonly string _dbName;

    public PartnerCurrencyDefaultsTests()
    {
        (_db, _, _dbName) = SuppliersTestDb.New(_org, isSuperAdmin: true);   // tenant filter off: explicit org filtering must hold
    }

    private BusinessPartner Seed(Guid org, Guid? sale = null, Guid? purchase = null, string code = "C1")
    {
        var p = new BusinessPartner
        {
            UUID = Guid.NewGuid(), OrganizationId = org, SupplierName = "Partner " + code, SupplierCode = code,
            PartnerType = "VENDOR_CUSTOMER", IsVendor = true, IsCustomer = true, Status = "ACTIVE", IsActive = true,
            DefaultSaleCurrency = sale, PreferredCurrency = purchase, CreatedBy = 1, CreatedDate = DateTime.UtcNow
        };
        _db.BusinessPartners.Add(p);
        _db.SaveChanges();
        p.OrganizationId = org;   // the context may stamp the tenant's org on add; force the intended one
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
        return p;
    }

    private static IOrganizationCurrencyService OrgBases(Guid org) => new FakeBases(org);

    private sealed class FakeBases(Guid org) : IOrganizationCurrencyService
    {
        public Task<Guid?> GetBaseCurrencyIdAsync(Guid organizationId) => Task.FromResult<Guid?>(organizationId == org ? Pkr : null);

        public Task<Guid?> GetBaseCurrencyIdAsync(Guid organizationId, TransactionDomain domain, CancellationToken ct = default) =>
            Task.FromResult<Guid?>(organizationId != org ? null : domain == TransactionDomain.Service ? Gbp : Pkr);
    }

    private PartnerCurrencyDefaultsService Defaults() => new(_db, OrgBases(_org));

    // ── IPartnerCurrencyDefaults ────────────────────────────────────────────

    [Fact]
    public async Task T_C4_01_a_customer_with_an_AED_default_resolves_to_AED_for_sale_documents()
    {
        var p = Seed(_org, sale: Aed);

        (await Defaults().ResolveDefaultCurrencyAsync(_org, p.UUID, TransactionDomain.Sale)).Should().Be(Aed);
    }

    [Fact]
    public async Task T_C4_02_a_customer_without_a_default_resolves_to_the_sale_base()
    {
        var p = Seed(_org);

        (await Defaults().ResolveDefaultCurrencyAsync(_org, p.UUID, TransactionDomain.Sale)).Should().Be(Pkr);
    }

    [Fact]
    public async Task T_C4_04_a_supplier_with_a_USD_preferred_currency_resolves_to_USD_for_purchase_documents()
    {
        var p = Seed(_org, sale: Aed, purchase: Usd);

        (await Defaults().ResolveDefaultCurrencyAsync(_org, p.UUID, TransactionDomain.Purchase)).Should().Be(Usd);
        (await Defaults().ResolveDefaultCurrencyAsync(_org, p.UUID, TransactionDomain.Service)).Should().Be(Gbp,
            "service documents have no partner default");
    }

    [Fact]
    public async Task Get_returns_both_defaults_and_another_organizations_partner_is_absent()
    {
        var mine = Seed(_org, sale: Aed, purchase: Usd);
        var theirs = Seed(_otherOrg, sale: Eur, purchase: Eur, code: "C2");

        (await Defaults().GetAsync(_org, mine.UUID)).Should().Be(new PartnerCurrencyDefaults(mine.UUID, Aed, Usd));
        (await Defaults().GetAsync(_org, theirs.UUID)).Should().BeNull();
        (await Defaults().ResolveDefaultCurrencyAsync(_org, theirs.UUID, TransactionDomain.Sale)).Should().Be(Pkr,
            "another organization's partner is unknown here → the domain base");
        (await Defaults().ResolveDefaultCurrencyAsync(_org, Guid.NewGuid(), TransactionDomain.Purchase)).Should().Be(Pkr);
    }

    [Fact]
    public async Task A_deleted_partner_is_absent()
    {
        var p = Seed(_org, sale: Aed);
        var row = _db.BusinessPartners.Single(x => x.UUID == p.UUID);
        row.IsDelete = true;
        _db.SaveChanges();

        (await Defaults().GetAsync(_org, p.UUID)).Should().BeNull();
    }

    // ── BR-C4-01 + alias on the partner surface (api/partners) ──────────────

    private FakeOrgCurrencies Currencies()
    {
        var c = new FakeOrgCurrencies();
        foreach (var (id, code) in new[] { (Pkr, "PKR"), (Usd, "USD"), (Aed, "AED"), (Eur, "EUR") }) c.Add(_org, id, code, true);
        c.Add(_org, Jpy, "JPY", false);
        return c;
    }

    private PartnerCurrencyRules Rules() =>
        new(Currencies(), new StaticTenantContext { OrganizationId = _org }, CodeLookup());

    private static ICurrencyCodeLookup CodeLookup() => new FakeCodes();

    private sealed class FakeCodes : ICurrencyCodeLookup
    {
        private static readonly Dictionary<Guid, string> Codes = new() { [Pkr] = "PKR", [Usd] = "USD", [Aed] = "AED", [Eur] = "EUR", [Jpy] = "JPY" };
        public Task<string?> GetCodeAsync(Guid currencyId, CancellationToken ct = default) =>
            Task.FromResult(Codes.TryGetValue(currencyId, out var c) ? c : null);
    }

    private BusinessPartnerService PartnerService()
    {
        var mapper = new AutoMapper.MapperConfiguration(cfg => cfg.AddProfile<BusinessPartnerMappingProfile>()).CreateMapper();
        var repo = new BusinessPartnerRepository(_db, new OpenAccess(), mapper);
        return new BusinessPartnerService(repo, new BusinessPartnerModelValidator(), Array.Empty<ISupplierReferenceChecker>(),
            currencyRules: Rules());
    }

    private static BusinessPartnerModel Model(string code = "BP1") => new()
    {
        PartnerCode = code, CompanyName = "Acme Trading", IsCustomer = true, IsVendor = true
    };

    [Fact]
    public async Task Create_stores_both_defaults_and_defaultPurchaseCurrencyId_wins_over_the_preferredCurrency_alias()
    {
        var m = Model();
        m.DefaultSaleCurrencyId = Aed;
        m.DefaultPurchaseCurrencyId = Usd;
        m.PreferredCurrency = Eur;

        var uuid = await PartnerService().CreateAsync(m, 1);

        var read = (await PartnerService().GetByIdAsync(uuid))!;
        read.DefaultSaleCurrencyId.Should().Be(Aed);
        read.DefaultSaleCurrencyCode.Should().Be("AED");
        read.DefaultPurchaseCurrencyId.Should().Be(Usd);
        read.DefaultPurchaseCurrencyCode.Should().Be("USD");
        read.PreferredCurrency.Should().Be(Usd, "the old name is the same column");
    }

    [Fact]
    public async Task An_inactive_or_foreign_currency_is_refused_on_create_and_nothing_is_written()
    {
        var m = Model();
        m.DefaultSaleCurrencyId = Jpy;
        var act = () => PartnerService().CreateAsync(m, 1);
        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("JPY is not an active currency of this organization.");

        var foreign = Model("BP2");
        foreign.PreferredCurrency = Guid.NewGuid();
        var act2 = () => PartnerService().CreateAsync(foreign, 1);
        await act2.Should().ThrowAsync<BadRequestException>();

        _db.BusinessPartners.Should().BeEmpty();
    }

    [Fact]
    public async Task An_update_without_the_fields_keeps_the_defaults_and_the_clear_flags_remove_them()
    {
        var m = Model();
        m.DefaultSaleCurrencyId = Aed;
        m.DefaultPurchaseCurrencyId = Usd;
        var uuid = await PartnerService().CreateAsync(m, 1);
        _db.ChangeTracker.Clear();

        await PartnerService().UpdateAsync(uuid, Model(), 1);   // an old client: no currency fields
        _db.ChangeTracker.Clear();
        var kept = (await PartnerService().GetByIdAsync(uuid))!;
        kept.DefaultSaleCurrencyId.Should().Be(Aed);
        kept.DefaultPurchaseCurrencyId.Should().Be(Usd);

        var clear = Model();
        clear.ClearDefaultSaleCurrency = true;
        clear.ClearDefaultPurchaseCurrency = true;
        await PartnerService().UpdateAsync(uuid, clear, 1);
        _db.ChangeTracker.Clear();
        var cleared = (await PartnerService().GetByIdAsync(uuid))!;
        cleared.DefaultSaleCurrencyId.Should().BeNull();
        cleared.DefaultPurchaseCurrencyId.Should().BeNull();
    }

    [Fact]
    public async Task A_value_and_its_clear_flag_together_are_refused()
    {
        var uuid = await PartnerService().CreateAsync(Model(), 1);
        var m = Model();
        m.DefaultSaleCurrencyId = Aed;
        m.ClearDefaultSaleCurrency = true;

        var act = () => PartnerService().UpdateAsync(uuid, m, 1);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Sending_back_an_unchanged_default_that_has_since_been_deactivated_is_not_rechecked()
    {
        var p = Seed(_org, sale: Jpy, code: "BP9");
        var m = Model("BP9");
        m.DefaultSaleCurrencyId = Jpy;   // what the edit form had loaded

        await PartnerService().UpdateAsync(p.UUID, m, 1);
    }

    // ── Legacy supplier surface (api/suppliers) ─────────────────────────────

    private SuppliersService SupplierService() =>
        new(new SuppliersRepository(_db, new NoEncryption(), new OpenAccess()), new DefaultSupplierEventPublisher(Microsoft.Extensions.Logging.Abstractions.NullLogger<DefaultSupplierEventPublisher>.Instance),
            new PhoneNumberValidationService(), Array.Empty<ISupplierReferenceChecker>(), currencyRules: Rules());

    [Fact]
    public async Task The_supplier_api_accepts_and_returns_both_defaults_with_the_alias_and_validates_them()
    {
        var uuid = await SupplierService().CreateSupplierAsync(new CreateSupplierRequest
        {
            SupplierName = "Vendor", SupplierCode = "V1", PreferredCurrency = Usd, DefaultSaleCurrencyId = Aed
        }, 1);
        _db.ChangeTracker.Clear();

        var d = (await SupplierService().GetSupplierByIdAsync(uuid))!;
        d.PreferredCurrency.Should().Be(Usd);
        d.DefaultPurchaseCurrencyId.Should().Be(Usd);
        d.DefaultPurchaseCurrencyCode.Should().Be("USD");
        d.DefaultSaleCurrencyId.Should().Be(Aed);
        d.DefaultSaleCurrencyCode.Should().Be("AED");

        var bad = () => SupplierService().PatchSupplierAsync(uuid, new PatchSupplierRequest { DefaultPurchaseCurrencyId = Jpy }, 1);
        (await bad.Should().ThrowAsync<BadRequestException>()).WithMessage("JPY *");

        await SupplierService().PatchSupplierAsync(uuid, new PatchSupplierRequest { ClearDefaultSaleCurrency = true, DefaultPurchaseCurrencyId = Eur }, 1);
        _db.ChangeTracker.Clear();
        var after = (await SupplierService().GetSupplierByIdAsync(uuid))!;
        after.DefaultSaleCurrencyId.Should().BeNull();
        after.PreferredCurrency.Should().Be(Eur);
    }

    [Fact]
    public async Task Creating_a_supplier_with_an_inactive_sale_default_is_refused()
    {
        var act = () => SupplierService().CreateSupplierAsync(new CreateSupplierRequest
        {
            SupplierName = "Vendor", SupplierCode = "V2", DefaultSaleCurrencyId = Jpy
        }, 1);

        await act.Should().ThrowAsync<BadRequestException>();
        _db.BusinessPartners.Should().BeEmpty();
    }

    // ── DI ──────────────────────────────────────────────────────────────────

    [Fact]
    public void The_module_registers_the_partner_currency_defaults_and_rules()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Data:mainOrg"] = "Server=none;Database=none;Trusted_Connection=True;" })
            .Build();
        var services = new ServiceCollection();
        services.AddSuppliersModule(configuration);

        services.Should().Contain(d => d.ServiceType == typeof(IPartnerCurrencyDefaults)
                                    && d.ImplementationType == typeof(PartnerCurrencyDefaultsService));
        services.Should().Contain(d => d.ServiceType == typeof(PartnerCurrencyRules));
    }

    // ── Fakes ───────────────────────────────────────────────────────────────

    private sealed class OpenAccess : IUserSupplierAccessService
    {
        public Task<bool> IsRestrictedAsync() => Task.FromResult(false);
        public Task<IReadOnlySet<Guid>> GetAllowedSupplierIdsAsync() => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
        public Task<bool> CanAccessSupplierAsync(Guid supplierUuid) => Task.FromResult(true);
    }

    private sealed class NoEncryption : IEncryptionService
    {
        public string Encrypt(string plainText) => plainText;
        public string Decrypt(string cipherText) => cipherText;
    }

    private sealed class FakeOrgCurrencies : IOrgCurrencyLookup
    {
        private readonly List<(Guid Org, OrgCurrencyInfo Info)> _rows = new();

        public void Add(Guid org, Guid id, string code, bool active) =>
            _rows.Add((org, new OrgCurrencyInfo(id, code, code, code, 2, 0.01m, "before", active, _rows.Count)));

        public Task<OrgCurrencyInfo?> GetAsync(Guid organizationId, Guid currencyId, CancellationToken ct = default) =>
            Task.FromResult(_rows.Where(r => r.Org == organizationId && r.Info.CurrencyId == currencyId).Select(r => r.Info).FirstOrDefault());

        public Task<OrgCurrencyInfo?> GetByCodeAsync(Guid organizationId, string code, CancellationToken ct = default) =>
            Task.FromResult(_rows.Where(r => r.Org == organizationId && r.Info.Code == code).Select(r => r.Info).FirstOrDefault());

        public Task<IReadOnlyList<OrgCurrencyInfo>> ListAsync(Guid organizationId, bool activeOnly, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OrgCurrencyInfo>>(_rows.Where(r => r.Org == organizationId).Select(r => r.Info).ToList());

        public Task<int> GetDecimalPlacesAsync(Guid organizationId, Guid currencyId, CancellationToken ct = default) => Task.FromResult(2);
    }
}

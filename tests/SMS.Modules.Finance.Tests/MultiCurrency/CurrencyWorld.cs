using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Services;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.Tests.MultiCurrency;

/// <summary>
/// A35 CUR — one in-memory Finance database, a global currency catalog, and per-organization currency settings
/// (rate currency + three bases). Each call builds fresh contexts, as an HTTP request does.
/// </summary>
internal sealed class CurrencyWorld
{
    internal const int User = 7;

    internal static readonly Guid Pkr = Guid.Parse("00000000-0000-0000-0000-00000000c001");
    internal static readonly Guid Usd = Guid.Parse("00000000-0000-0000-0000-00000000c002");
    internal static readonly Guid Eur = Guid.Parse("00000000-0000-0000-0000-00000000c003");
    internal static readonly Guid Aed = Guid.Parse("00000000-0000-0000-0000-00000000c004");
    internal static readonly Guid Jpy = Guid.Parse("00000000-0000-0000-0000-00000000c005");
    internal static readonly Guid Bhd = Guid.Parse("00000000-0000-0000-0000-00000000c006");
    internal static readonly Guid Mxn = Guid.Parse("00000000-0000-0000-0000-00000000c007");

    public string DbName { get; } = Guid.NewGuid().ToString();
    public TestClock Clock { get; } = new();

    public List<CurrencyModel> Catalog { get; } =
    [
        new() { Id = Pkr, Code = "PKR", Name = "Pakistani Rupee", Symbol = "₨" },
        new() { Id = Usd, Code = "USD", Name = "US Dollar",       Symbol = "$" },
        new() { Id = Eur, Code = "EUR", Name = "Euro",            Symbol = "€" },
        new() { Id = Aed, Code = "AED", Name = "UAE Dirham",      Symbol = "د.إ" },
        new() { Id = Jpy, Code = "JPY", Name = "Japanese Yen",    Symbol = "¥" },
        new() { Id = Bhd, Code = "BHD", Name = "Bahraini Dinar",  Symbol = "BD" },
    ];

    public FakeOrgCurrencySettings Settings { get; } = new();
    public List<ICurrencyUsageChecker> Checkers { get; } = [];

    public FinanceDbContext Db(Guid org, bool superAdmin = false) =>
        new(new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(DbName).Options,
            new StaticTenantContext { OrganizationId = org, IsSuperAdmin = superAdmin });

    public Mock<ILookupsService> Lookups()
    {
        var m = new Mock<ILookupsService>();
        m.Setup(l => l.GetCurrencies()).Returns(() => Catalog.ToList());
        m.Setup(l => l.CreateCurrency(It.IsAny<CreateCurrencyRequest>())).Returns((CreateCurrencyRequest r) =>
        {
            var id = Guid.NewGuid();
            Catalog.Add(new CurrencyModel { Id = id, Code = r.Code, Name = r.Name, Symbol = r.Symbol });
            return id;
        });
        return m;
    }

    public CurrencyService Currency(FinanceDbContext db) => new(db, Settings);
    public CurrencyRateService Rates(FinanceDbContext db) => new(db, Settings, Clock);
    public OrgCurrencyService OrgCurrencies(FinanceDbContext db) => new(db, Lookups().Object, Settings, Checkers, Clock);

    /// <summary>Org with every currency of the catalog configured and the given rate currency's SYSTEM row.</summary>
    public async Task<Guid> NewOrgAsync(Guid? rateCurrency = null, Guid? saleBase = null, Guid? purchaseBase = null)
    {
        var org = Guid.NewGuid();
        var rc  = rateCurrency ?? Pkr;
        Settings.Set(org, saleBase ?? rc, purchaseBase ?? saleBase ?? rc, saleBase ?? rc, rc);

        await using var db = Db(org);
        var order = 1;
        foreach (var c in Catalog)
            db.OrgCurrencies.Add(new OrgCurrency
            {
                OrganizationId = org, CurrencyId = c.Id, Code = c.Code!, Name = c.Name, Symbol = c.Symbol ?? c.Code!,
                DecimalPlaces = c.Code switch { "JPY" => 0, "BHD" => 3, _ => 2 },
                Rounding = c.Code switch { "JPY" => 1m, "BHD" => 0.001m, _ => 0.01m },
                DisplayOrder = order++
            });
        db.CurrencyRates.Add(new CurrencyRate
        {
            OrganizationId = org, CurrencyId = rc, CurrencyCode = Catalog.Single(c => c.Id == rc).Code!, Rate = 1m,
            InverseRate = 1m, EffectiveFrom = CurrencyConventions.SystemStart, EffectiveTo = CurrencyConventions.OpenEnd,
            Source = CurrencyConventions.SourceSystem
        });
        await db.SaveChangesAsync();
        return org;
    }

    /// <summary>A stored rate row, straight into the table.</summary>
    public async Task SeedRateAsync(Guid org, Guid currency, decimal rate, string from, string? to = null)
    {
        await using var db = Db(org);
        db.CurrencyRates.Add(new CurrencyRate
        {
            OrganizationId = org, CurrencyId = currency, CurrencyCode = Catalog.Single(c => c.Id == currency).Code!,
            Rate = rate, InverseRate = CurrencyConventions.RoundRate(1m / rate),
            EffectiveFrom = DateOnly.Parse(from), EffectiveTo = to is null ? CurrencyConventions.OpenEnd : DateOnly.Parse(to),
            Source = CurrencyConventions.SourceManual
        });
        await db.SaveChangesAsync();
    }
}

/// <summary>A hand fake of Tenancy's settings service (the real one is TEN's).</summary>
internal sealed class FakeOrgCurrencySettings : IOrganizationCurrencyService
{
    private readonly Dictionary<Guid, OrgCurrencySettingsSnapshot> _rows = new();

    public void Set(Guid org, Guid sale, Guid purchase, Guid service, Guid rate) =>
        _rows[org] = new OrgCurrencySettingsSnapshot(org, sale, purchase, service, rate, "7110", "7120", "7130", "7140", IsStored: true);

    public Task<Guid?> GetBaseCurrencyIdAsync(Guid organizationId) =>
        Task.FromResult(_rows.TryGetValue(organizationId, out var s) ? s.SaleBaseCurrencyId : (Guid?)null);

    public Task<Guid?> GetBaseCurrencyIdAsync(Guid organizationId, TransactionDomain domain, CancellationToken ct = default) =>
        Task.FromResult(_rows.TryGetValue(organizationId, out var s) ? s.BaseFor(domain) : (Guid?)null);

    public Task<OrgCurrencySettingsSnapshot> GetSettingsAsync(Guid organizationId, CancellationToken ct = default) =>
        Task.FromResult(_rows.TryGetValue(organizationId, out var s)
            ? s
            : new OrgCurrencySettingsSnapshot(organizationId, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, null, null, null, null, false));
}

internal sealed class FakeUsageChecker : ICurrencyUsageChecker
{
    public string? CurrencyUsage { get; set; }
    public Dictionary<TransactionDomain, string> DomainUsage { get; } = new();

    public Task<string?> DescribeCurrencyUsageAsync(Guid organizationId, Guid currencyId, CancellationToken ct = default) =>
        Task.FromResult(CurrencyUsage);

    public Task<string?> DescribeDomainBaseUsageAsync(Guid organizationId, TransactionDomain domain, CancellationToken ct = default) =>
        Task.FromResult(DomainUsage.TryGetValue(domain, out var u) ? u : null);
}

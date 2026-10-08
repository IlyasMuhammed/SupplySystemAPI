using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.Tests.FinanceSetup;

/// <summary>
/// One in-memory database holding Finance's and Demand's tables, seen through a "desk" per organization —
/// each call a desk makes is its own pair of contexts, as an HTTP request is, so nothing is shared
/// through a change tracker by accident.
/// </summary>
internal sealed class SetupWorld
{
    internal const int User = 7;

    internal static readonly Guid PkrId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    internal static readonly Guid UsdId = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    internal static readonly Guid EurId = Guid.Parse("00000000-0000-0000-0000-0000000000a3");
    internal static readonly Guid AedId = Guid.Parse("00000000-0000-0000-0000-0000000000a4");

    public string    DbName { get; } = Guid.NewGuid().ToString();
    public TestClock Clock  { get; } = new();

    /// <summary>The Lookups currency catalog every desk sees. "aed" is stored in lower case on purpose.</summary>
    public List<CurrencyModel> Currencies { get; } =
    [
        new() { Id = PkrId, Name = "Pakistani Rupee", Code = "PKR" },
        new() { Id = UsdId, Name = "US Dollar",       Code = "USD" },
        new() { Id = EurId, Name = "Euro",            Code = "EUR" },
        new() { Id = AedId, Name = "UAE Dirham",      Code = "aed" },
        new() { Id = Guid.NewGuid(), Name = "No code yet", Code = null }
    ];

    public SetupDesk For(Guid org) => new(this, org);

    /// <summary>Sees every organization — for checking where a write landed.</summary>
    public FinanceDbContext Auditor() =>
        new(new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(DbName).Options,
            new StaticTenantContext { IsSuperAdmin = true });

    internal Mock<ILookupsService> Lookups()
    {
        var lookups = new Mock<ILookupsService>();
        lookups.Setup(l => l.GetCurrencies()).Returns(() => Currencies.ToList());
        return lookups;
    }
}

/// <summary>One organization's view of a <see cref="SetupWorld"/>. <c>superAdmin</c> bypasses the tenant filter, as a platform admin does.</summary>
internal sealed class SetupDesk
{
    private readonly SetupWorld _world;

    internal SetupDesk(SetupWorld world, Guid org)
    {
        _world = world;
        Org    = org;
    }

    public Guid Org { get; }

    public FinanceDbContext Finance(bool superAdmin = false) =>
        new(new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(_world.DbName).Options,
            new StaticTenantContext { OrganizationId = Org, IsSuperAdmin = superAdmin });

    public DemandDbContext Demand(bool superAdmin = false) =>
        new(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(_world.DbName).Options,
            new StaticTenantContext { OrganizationId = Org, IsSuperAdmin = superAdmin });

    // ── Services, each over fresh contexts ───────────────────────────────────

    public async Task<T> TaxCodes<T>(Func<TaxCodeService, Task<T>> act, bool superAdmin = false)
    {
        await using var db     = Finance(superAdmin);
        await using var demand = Demand(superAdmin);
        return await act(new TaxCodeService(db, demand, _world.Clock));
    }

    public async Task<T> Rates<T>(Func<ExchangeRateService, Task<T>> act, bool superAdmin = false)
    {
        await using var db = Finance(superAdmin);
        return await act(new ExchangeRateService(db, _world.Lookups().Object, new ExchangeRateProvider(db), _world.Clock));
    }

    public async Task Rates(Func<ExchangeRateService, Task> act)
    {
        await using var db = Finance();
        await act(new ExchangeRateService(db, _world.Lookups().Object, new ExchangeRateProvider(db), _world.Clock));
    }

    public async Task<ExchangeRateQuote?> Quote(string from, string to, DateTime asOf, bool superAdmin = false)
    {
        await using var db = Finance(superAdmin);
        return await new ExchangeRateProvider(db).GetRateAsync(from, to, asOf);
    }

    public async Task<T> Lookup<T>(Func<TaxCodeLookup, Task<T>> act, bool superAdmin = false)
    {
        await using var db = Finance(superAdmin);
        return await act(new TaxCodeLookup(db));
    }

    // ── Shortcuts ────────────────────────────────────────────────────────────

    public Task<TaxCodeSaved> CreateCode(
        string code, decimal rate, string usage = "BOTH", bool isDefault = false, bool isActive = true, string? name = null) =>
        TaxCodes(s => s.CreateAsync(Code(code, rate, usage, isDefault, isActive, name), SetupWorld.User));

    public Task<TaxCodeSaved> UpdateCode(Guid uuid, SaveTaxCodeRequest req) =>
        TaxCodes(s => s.UpdateAsync(uuid, req, SetupWorld.User));

    public Task<IReadOnlyList<TaxCodeModel>> ListCodes(string? side = null, bool includeInactive = false) =>
        TaxCodes(s => s.ListAsync(side, includeInactive));

    public static SaveTaxCodeRequest Code(
        string code, decimal rate, string usage = "BOTH", bool isDefault = false, bool isActive = true, string? name = null) => new()
    {
        Code = code, Name = name ?? $"{code} name", RatePercent = rate, Usage = usage, IsDefault = isDefault, IsActive = isActive
    };

    public static SaveTaxCodeRequest From(TaxCodeModel m) => new()
    {
        Code = m.Code, Name = m.Name, Description = m.Description, RatePercent = m.RatePercent, Usage = m.Usage,
        IsDefault = m.IsDefault, IsActive = m.IsActive
    };

    public static SaveExchangeRateRequest Rate(string from, string to, decimal rate, string date, string? notes = null) => new()
    {
        FromCurrencyCode = from, ToCurrencyCode = to, Rate = rate, EffectiveDate = date, Notes = notes
    };

    /// <summary>A sale order with one line per tax rate given.</summary>
    public async Task<SaleOrder> PlaceOrder(bool deleted = false, params decimal[] taxRates)
    {
        await using var demand = Demand();
        var order = new SaleOrder
        {
            SoNumber = $"SO-{Guid.NewGuid():N}"[..14], TraceId = Guid.NewGuid(), PartnerId = Guid.NewGuid(),
            OrderDate = new DateTime(2026, 9, 1), CurrencyId = SetupWorld.PkrId, Status = "CONFIRMED",
            DeliveryMode = "SHIP", CreatedBy = 1, IsDeleted = deleted
        };
        foreach (var rate in taxRates)
            order.Lines.Add(new SaleOrderLine
            {
                VariantUuid = Guid.NewGuid(), Quantity = 1m, UnitPrice = 100m, TaxPercent = rate,
                LineTotal = 100m + rate, Status = "OPEN"
            });

        demand.SaleOrders.Add(order);
        await demand.SaveChangesAsync();
        return order;
    }

    /// <summary>Rows straight into the Finance tables (bypassing the services), for states a service would refuse.</summary>
    public async Task Seed(params object[] entities)
    {
        await using var db = Finance();
        db.AddRange(entities);
        await db.SaveChangesAsync();
    }
}

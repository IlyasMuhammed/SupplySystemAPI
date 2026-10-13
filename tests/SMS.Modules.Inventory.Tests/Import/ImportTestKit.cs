using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Services;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Import;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Common;

namespace SMS.Modules.Inventory.Tests.Import;

/// <summary>A40 (TINV) — in-memory Inventory (+ Finance master product ledger) for the import targets, organization explicit.</summary>
internal sealed class ImportHarness
{
    public Guid Org { get; } = Guid.NewGuid();
    public Guid OtherOrg { get; } = Guid.NewGuid();
    public string DbName { get; } = Guid.NewGuid().ToString();
    public InventoryDbContext Db { get; }
    public FinanceDbContext Fin { get; }
    public FakeProductLedger ProductLedger { get; } = new();
    public FakeOpeningDate OpeningDate { get; } = new();
    public FakeCustomers Customers { get; } = new();
    public FakeCurrencies Currencies { get; } = new();
    public IServiceProvider Services { get; }

    public ImportHarness(params string[] units)
    {
        // A super admin's context: every target must scope by the organization it is given, never by the ambient tenant.
        Db  = NewContext();
        Fin = new FinanceDbContext(
            new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new StaticTenantContext { OrganizationId = Org });
        Services = new ServiceCollection()
            .AddSingleton<IImportTarget>(new FakeUnitTarget(units.Length == 0 ? ["PCS", "KG"] : units))
            .BuildServiceProvider();
    }

    public InventoryDbContext NewContext() => new(
        new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(DbName).Options,
        new StaticTenantContext { OrganizationId = Guid.NewGuid(), IsSuperAdmin = true });

    public ProductCategoryImportTarget Categories() => new(Db);
    public ProductImportTarget Products() => new(Db, Services);
    public WarehouseImportTarget Warehouses() => new(Db);
    public WarehouseLocationImportTarget Locations() => new(Db);
    public PricingRuleImportTarget PricingRules(bool withCustomers = true) =>
        new(Db, withCustomers ? Customers : null, Currencies, Currencies);
    public OpeningStockImportTarget OpeningStock(bool withDateProvider = true) =>
        new(Db, new InventoryLedgerService(Db, NullLogger<InventoryLedgerService>.Instance, new MasterProductLedgerService(Fin)),
            withDateProvider ? OpeningDate : null, ProductLedger);

    public static ImportRowInput Row(int n, params (string Key, string? Value)[] values) =>
        new(n, values.ToDictionary(v => v.Key, v => v.Value), null);

    public static ImportApplyRow Create(int n, params (string Key, string? Value)[] values) =>
        new(n, values.ToDictionary(v => v.Key, v => v.Value), "CREATE", null);

    public static ImportApplyRow Update(int n, Guid existing, params (string Key, string? Value)[] values) =>
        new(n, values.ToDictionary(v => v.Key, v => v.Value), "UPDATE", existing);

    public async Task<Domain.Warehouse> WarehouseAsync(string code = "WH1", Guid? org = null)
    {
        var w = new Domain.Warehouse { Uuid = Guid.NewGuid(), OrganizationId = org ?? Org, Code = code, Name = $"Warehouse {code}", IsActive = true };
        Db.Warehouses.Add(w);
        await Db.SaveChangesAsync();
        return w;
    }

    public async Task<ProductVariant> ProductAsync(
        string sku, string? name = null, bool batch = false, bool serial = false, bool stockable = true,
        string type = ProductType.StockItem, Guid? org = null, string? variantSku = null)
    {
        var product = new Product
        {
            Uuid = Guid.NewGuid(), OrganizationId = org ?? Org, Sku = sku, Name = name ?? $"Product {sku}", IsActive = true,
            IsBatchTracked = batch, IsSerialTracked = serial, IsStockable = stockable, ProductType = type
        };
        product.Variants.Add(new ProductVariant
        {
            Uuid = Guid.NewGuid(), OrganizationId = org ?? Org, Sku = variantSku ?? $"{sku}-DEFAULT", VariantName = product.Name,
            IsDefault = true, IsActive = true, PurchasePrice = 1m, SellingPrice = 2m
        });
        Db.Products.Add(product);
        await Db.SaveChangesAsync();
        return product.Variants.First();
    }
}

internal sealed class FakeUnitTarget(IReadOnlyList<string> units) : IImportTarget
{
    public ImportProfileDefinition Describe() => new(ImportEntityTypes.Unit, "Units", null, 1, false, ["code"], []);
    public Task<IReadOnlyList<ImportTemplateList>> GetTemplateListsAsync(Guid organizationId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ImportTemplateList>>([new("units", units)]);
    public Task<IReadOnlyList<ImportRowCheck>> ValidateAsync(Guid organizationId, IReadOnlyList<ImportRowInput> rows, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task<IReadOnlyList<ImportRowOutcome>> ApplyAsync(Guid organizationId, int userId, IReadOnlyList<ImportApplyRow> rows, CancellationToken ct = default) =>
        throw new NotSupportedException();
}

internal sealed class FakeProductLedger : IProductLedgerService
{
    public readonly List<(ProductLedgerPosting Posting, DbTransaction? Transaction)> Calls = [];
    public int ThrowOnCall { get; set; } = -1;

    public Task<ProductLedgerPosted> AppendEntryAsync(ProductLedgerPosting posting, DbTransaction? transaction = null)
    {
        if (Calls.Count == ThrowOnCall) throw new InvalidOperationException("product ledger down");
        Calls.Add((posting, transaction));
        return Task.FromResult(new ProductLedgerPosted(Guid.NewGuid(), Calls.Count, posting.VariantUuid, posting.ProductUuid ?? Guid.Empty,
            posting.Direction, posting.Quantity, posting.UnitCost ?? 0m, posting.Quantity * (posting.UnitCost ?? 0m), posting.Quantity,
            posting.Quantity * (posting.UnitCost ?? 0m), posting.UnitCost ?? 0m));
    }
}

internal sealed class FakeOpeningDate : IOpeningBalanceDateProvider
{
    public DateOnly? Date { get; set; } = new DateOnly(2026, 1, 1);
    public Task<DateOnly?> GetAsync(Guid organizationId, CancellationToken ct = default) => Task.FromResult(Date);
}

internal sealed class FakeCustomers : IPosCustomerSource
{
    public readonly List<PosCustomer> All = [];
    public Guid? Organization { get; set; }

    public PosCustomer Add(string code, bool active = true)
    {
        var c = new PosCustomer(Guid.NewGuid(), code, $"Customer {code}", "COMPANY", null, null, null, 0m, active, false,
            DateTime.UtcNow.AddMinutes(All.Count));
        All.Add(c);
        return c;
    }

    public Task<IReadOnlyList<PosCustomer>> GetChangedAsync(Guid organizationId, DateTime? since, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PosCustomer>>(Organization is { } o && o != organizationId ? []
            : All.Where(c => since is null || c.ModifiedAt > since).OrderBy(c => c.ModifiedAt).Take(limit).ToList());
    public Task<PosCustomer?> GetAsync(Guid organizationId, Guid customerUuid, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<PosCustomer>> GetManyAsync(Guid organizationId, IReadOnlyCollection<Guid> customerUuids, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<PosCustomer> GetWalkInAsync(Guid organizationId, CancellationToken ct = default) => throw new NotSupportedException();
}

internal sealed class FakeCurrencies : IOrgCurrencyLookup, IOrganizationCurrencyService
{
    public Guid Pkr { get; } = Guid.NewGuid();
    public Guid Usd { get; } = Guid.NewGuid();

    private IEnumerable<OrgCurrencyInfo> Rows() =>
    [
        new(Pkr, "PKR", "Pakistani Rupee", "Rs", 2, 0.01m, "BEFORE", true, 1),
        new(Usd, "USD", "US Dollar", "$", 2, 0.01m, "BEFORE", true, 2),
        new(Guid.NewGuid(), "EUR", "Euro", "€", 2, 0.01m, "BEFORE", false, 3)
    ];

    public Task<OrgCurrencyInfo?> GetAsync(Guid organizationId, Guid currencyId, CancellationToken ct = default) =>
        Task.FromResult(Rows().FirstOrDefault(c => c.CurrencyId == currencyId));
    public Task<OrgCurrencyInfo?> GetByCodeAsync(Guid organizationId, string code, CancellationToken ct = default) =>
        Task.FromResult(Rows().FirstOrDefault(c => c.Code == code));
    public Task<IReadOnlyList<OrgCurrencyInfo>> ListAsync(Guid organizationId, bool activeOnly, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OrgCurrencyInfo>>(Rows().Where(c => !activeOnly || c.IsActive).ToList());
    public Task<int> GetDecimalPlacesAsync(Guid organizationId, Guid currencyId, CancellationToken ct = default) => Task.FromResult(2);
    public Task<Guid?> GetBaseCurrencyIdAsync(Guid organizationId) => Task.FromResult<Guid?>(Pkr);
}

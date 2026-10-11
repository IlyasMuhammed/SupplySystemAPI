using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Services;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Common;

namespace SMS.Modules.Inventory.Tests.Pos;

/// <summary>Records every IPosChangeListener call; <see cref="OnCall"/> runs inside the call (e.g. to read what is committed).</summary>
internal sealed class FakePosChangeListener : IPosChangeListener
{
    public readonly List<(Guid Org, IReadOnlyCollection<Guid> Variants)> VariantCalls = [];
    public readonly List<(Guid Org, Guid Variant, decimal? Old, decimal? New)> PriceCalls = [];
    public readonly List<(Guid Org, Guid Warehouse, IReadOnlyCollection<Guid> Variants)> StockCalls = [];
    public Func<Task>? OnCall { get; set; }
    public bool Throw { get; set; }

    public int Total => VariantCalls.Count + PriceCalls.Count + StockCalls.Count;

    public void Clear()
    {
        VariantCalls.Clear();
        PriceCalls.Clear();
        StockCalls.Clear();
    }

    private async Task Run()
    {
        if (OnCall is not null) await OnCall();
        if (Throw) throw new InvalidOperationException("listener down");
    }

    public async Task OnVariantsChangedAsync(Guid organizationId, IReadOnlyCollection<Guid> variantUuids, CancellationToken ct = default)
    {
        VariantCalls.Add((organizationId, variantUuids.ToList()));
        await Run();
    }

    public async Task OnPriceChangedAsync(Guid organizationId, Guid variantUuid, decimal? oldPrice, decimal? newPrice, CancellationToken ct = default)
    {
        PriceCalls.Add((organizationId, variantUuid, oldPrice, newPrice));
        await Run();
    }

    public async Task OnStockChangedAsync(Guid organizationId, Guid warehouseUuid, IReadOnlyCollection<Guid> variantUuids, CancellationToken ct = default)
    {
        StockCalls.Add((organizationId, warehouseUuid, variantUuids.ToList()));
        await Run();
    }

    public Task OnCustomerBalanceChangedAsync(Guid organizationId, Guid customerUuid, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>In-memory Inventory + Finance (master product ledger) for one organization, optionally with a POS listener.</summary>
internal sealed class PosHarness
{
    public Guid Org { get; } = Guid.NewGuid();
    public string InventoryDbName { get; } = Guid.NewGuid().ToString();
    public InventoryDbContext Db { get; }
    public FinanceDbContext Fin { get; }
    public PosStockPoster Poster { get; }
    public PosCatalogSource Catalog { get; }
    public FakePosChangeListener Listener { get; } = new();
    public IServiceProvider? Services { get; }

    public PosHarness(bool withListener = false)
    {
        if (withListener)
            Services = new ServiceCollection().AddLogging().AddSingleton<IPosChangeListener>(Listener).BuildServiceProvider();

        Db  = NewContext();
        Fin = new FinanceDbContext(
            new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new StaticTenantContext { OrganizationId = Org });

        var ledger = new InventoryLedgerService(Db, NullLogger<InventoryLedgerService>.Instance, new MasterProductLedgerService(Fin));
        Poster  = new PosStockPoster(Db, ledger);
        Catalog = new PosCatalogSource(Db);
    }

    /// <summary>Another context on the same in-memory database (what a different scope would see).</summary>
    public InventoryDbContext NewContext(Guid? org = null) => new(
        new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(InventoryDbName).Options,
        new StaticTenantContext { OrganizationId = org ?? Org }, Services);

    public async Task<Domain.Warehouse> WarehouseAsync(string name = "Store 1")
    {
        var w = new Domain.Warehouse { Uuid = Guid.NewGuid(), Name = name, Code = $"W{Guid.NewGuid():N}"[..6], IsActive = true };
        Db.Warehouses.Add(w);
        await Db.SaveChangesAsync();
        return w;
    }

    public async Task<ProductVariant> VariantAsync(
        string name = "Cola", bool batchTracked = false, bool stockable = true, decimal? price = 2.5m, decimal purchasePrice = 1m,
        int? categoryId = null)
    {
        var product = new Product
        {
            Uuid = Guid.NewGuid(), Name = name, Sku = $"P{Guid.NewGuid():N}"[..12], IsActive = true, CategoryId = categoryId,
            IsBatchTracked = batchTracked, IsStockable = stockable, UomCode = "EA"
        };
        Db.Products.Add(product);
        await Db.SaveChangesAsync();

        var variant = new ProductVariant
        {
            Uuid = Guid.NewGuid(), ProductId = product.Id, Sku = $"V{Guid.NewGuid():N}"[..12], VariantName = "Default",
            IsDefault = true, IsActive = true, SellingPrice = price, PurchasePrice = purchasePrice, IsAvailableForPos = true
        };
        Db.ProductVariants.Add(variant);
        await Db.SaveChangesAsync();
        return variant;
    }

    public async Task<InventoryItem> RowAsync(
        ProductVariant v, Domain.Warehouse w, decimal onHand, decimal reserved = 0m, string? batch = null,
        DateTime? expiry = null, decimal? unitCost = 5m)
    {
        var item = new InventoryItem
        {
            Uuid = Guid.NewGuid(), VariantId = v.Id, WarehouseId = w.Id, QtyOnHand = onHand, QtyReserved = reserved,
            BatchNumber = batch, ExpiryDate = expiry, UnitCost = unitCost
        };
        Db.InventoryItems.Add(item);
        await Db.SaveChangesAsync();
        return item;
    }

    public PosStockPosting Sale(Domain.Warehouse w, Guid source, params (ProductVariant V, decimal Qty)[] lines) =>
        new(Org, w.Uuid, source, "POS-S1-20261010-0001", PosStockTransactionType.Sale, true,
            lines.Select(l => new PosStockLine(l.V.Uuid, l.Qty)).ToList(), 7);

    public PosStockPosting Inbound(string type, Domain.Warehouse w, Guid source, params (ProductVariant V, decimal Qty)[] lines) =>
        new(Org, w.Uuid, source, "POS-S1-20261010-0002", type, false,
            lines.Select(l => new PosStockLine(l.V.Uuid, l.Qty)).ToList(), 7);

    public async Task<List<InventoryItem>> RowsAsync(ProductVariant v, Domain.Warehouse w)
    {
        await using var db = NewContext();
        return await db.InventoryItems.AsNoTracking().Where(i => i.VariantId == v.Id && i.WarehouseId == w.Id).OrderBy(i => i.Id).ToListAsync();
    }
}

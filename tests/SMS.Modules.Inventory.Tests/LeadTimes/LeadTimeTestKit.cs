using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Inventory.Tests;

/// <summary>A34 lead-time tests: in-memory contexts, seeding and fakes of the cross-module contracts.</summary>
internal static class LeadTimeKit
{
    internal static InventoryDbContext Db(string name, Guid org, bool superAdmin = false) => new(
        new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(name).Options,
        new StaticTenantContext { OrganizationId = org, IsSuperAdmin = superAdmin });

    /// <summary>One product with one variant under the context's organization; returns the variant.</summary>
    internal static async Task<ProductVariant> VariantAsync(
        InventoryDbContext db, string supplyMethod = SupplyMethod.Purchase, int? productLeadTimeDays = null,
        Action<ProductVariant>? configure = null, Guid? route = null, string name = "Item", int? productionWarehouseId = null)
    {
        await using var _ = db;
        var product = new Product
        {
            Uuid = Guid.NewGuid(), Name = name, Sku = $"SKU{Guid.NewGuid():N}"[..12], IsActive = true,
            SupplyMethod = supplyMethod, IsManufacturable = supplyMethod == SupplyMethod.Manufacture,
            ProductType = supplyMethod == SupplyMethod.Manufacture ? ProductType.FinishedGood : ProductType.StockItem,
            LeadTimeDays = productLeadTimeDays, DefaultProductionWarehouseId = productionWarehouseId
        };
        var variant = new ProductVariant
        {
            Uuid = Guid.NewGuid(), Sku = $"V{Guid.NewGuid():N}"[..12], VariantName = "Std", IsDefault = true, IsActive = true,
            FulfillmentRouteUuid = route
        };
        configure?.Invoke(variant);
        product.Variants.Add(variant);
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return variant;
    }

    internal static async Task RateAsync(
        InventoryDbContext db, int variantId, Guid supplier, int? leadTimeDays, bool preferred = false, bool active = true,
        DateTime? from = null, DateTime? to = null)
    {
        await using var _ = db;
        db.VariantSuppliers.Add(new VariantSupplier
        {
            Uuid = Guid.NewGuid(), VariantId = variantId, SupplierId = supplier, VendorUnitCost = 1m, CurrencyId = Guid.NewGuid(),
            LeadTimeDays = leadTimeDays, IsPreferred = preferred, IsActive = active,
            EffectiveFrom = from ?? DateTime.UtcNow.Date.AddDays(-10), EffectiveTo = to
        });
        await db.SaveChangesAsync();
    }

    internal static async Task DefaultsAsync(InventoryDbContext db, int pickPack, int shipping, int sales, int mfgBuffer, int qc, int transfer)
    {
        await using var _ = db;
        db.LeadTimeDefaults.Add(new LeadTimeDefaults
        {
            PickPackDays = pickPack, ShippingLeadTimeDays = shipping, SalesBufferDays = sales,
            ManufacturingBufferDays = mfgBuffer, QualityInspectionDays = qc, InternalTransferDays = transfer, CreatedBy = 1
        });
        await db.SaveChangesAsync();
    }

    internal static async Task<Domain.Warehouse> WarehouseAsync(InventoryDbContext db, string code = "W")
    {
        await using var _ = db;
        var warehouse = new Domain.Warehouse { Uuid = Guid.NewGuid(), Name = code, Code = $"{code}{Guid.NewGuid():N}"[..6], IsActive = true };
        db.Warehouses.Add(warehouse);
        await db.SaveChangesAsync();
        return warehouse;
    }

    internal static async Task StockAsync(InventoryDbContext db, int variantId, int warehouseId, decimal onHand, decimal reserved = 0m)
    {
        await using var _ = db;
        db.InventoryItems.Add(new InventoryItem
        {
            Uuid = Guid.NewGuid(), VariantId = variantId, WarehouseId = warehouseId, QtyOnHand = onHand, QtyReserved = reserved
        });
        await db.SaveChangesAsync();
    }
}

/// <summary>Stands in for Suppliers' <see cref="ISupplierLeadTimeLookup"/>: BusinessPartner.LeadTimeDays per (org, supplier).</summary>
internal sealed class FakeSupplierLeadTimeLookup : ISupplierLeadTimeLookup
{
    private readonly Dictionary<(Guid Org, Guid Supplier), int> _days = [];
    public int Calls { get; private set; }

    public FakeSupplierLeadTimeLookup Set(Guid org, Guid supplier, int days)
    {
        _days[(org, supplier)] = days;
        return this;
    }

    public Task<IReadOnlyDictionary<Guid, int>> GetAsync(Guid organizationId, IReadOnlyCollection<Guid> supplierUuids, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult<IReadOnlyDictionary<Guid, int>>(supplierUuids
            .Where(s => _days.ContainsKey((organizationId, s)))
            .Distinct()
            .ToDictionary(s => s, s => _days[(organizationId, s)]));
    }
}

/// <summary>Stands in for Material's <see cref="IBomStructureReader"/>: active BOMs per (org, variant); counts calls (T-C4-08).</summary>
internal sealed class FakeBomStructureReader : IBomStructureReader
{
    private readonly Dictionary<(Guid Org, Guid Variant), BomStructure> _boms = [];
    public int Calls { get; private set; }
    public List<IReadOnlyCollection<Guid>> Requests { get; } = [];

    public FakeBomStructureReader Add(Guid org, Guid variant, decimal baseQuantity, params (Guid Variant, decimal Quantity, decimal Scrap)[] inputs)
    {
        _boms[(org, variant)] = new BomStructure(
            Guid.NewGuid(), $"BOM-{_boms.Count + 1:0000}", 1, baseQuantity,
            inputs.Select(i => new BomInput(i.Variant, Guid.NewGuid(), i.Quantity, i.Scrap)).ToList());
        return this;
    }

    public Task<IReadOnlyDictionary<Guid, BomStructure>> GetActiveBomsAsync(
        Guid organizationId, IReadOnlyCollection<Guid> variantUuids, CancellationToken ct = default)
    {
        Calls++;
        Requests.Add(variantUuids.ToList());
        return Task.FromResult<IReadOnlyDictionary<Guid, BomStructure>>(variantUuids.Distinct()
            .Where(v => _boms.ContainsKey((organizationId, v)))
            .ToDictionary(v => v, v => _boms[(organizationId, v)]));
    }
}

/// <summary>Stands in for Suppliers' <see cref="ISupplierNameLookupService"/>.</summary>
internal sealed class FakeSupplierNames : ISupplierNameLookupService
{
    private readonly Dictionary<Guid, string> _names = [];

    public FakeSupplierNames Set(Guid supplier, string name)
    {
        _names[supplier] = name;
        return this;
    }

    public Task<IReadOnlyDictionary<Guid, string>> GetNamesAsync(IReadOnlyList<Guid> supplierIds) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(supplierIds.Where(_names.ContainsKey).Distinct().ToDictionary(s => s, s => _names[s]));
}

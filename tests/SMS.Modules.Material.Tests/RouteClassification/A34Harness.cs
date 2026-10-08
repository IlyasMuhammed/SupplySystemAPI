using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Services;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Services;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Modules.Material.Models;
using SMS.Modules.Material.Repositories;
using SMS.Modules.Material.Services;
using SMS.Shared.Common;

namespace SMS.Modules.Material.Tests.RouteClassification;

/// <summary>
/// A34 MFG — the QualityAndFgrTests harness (real services over two in-memory contexts), plus the A34 readers. The
/// ambient tenant is <see cref="Org"/>; rows of <see cref="OtherOrg"/> are written with that org stamped explicitly.
/// </summary>
internal sealed class A34Harness
{
    internal const int Author   = 7;
    internal const int Operator = 9;
    internal static readonly DateTime Today = DateTime.UtcNow.Date;

    public Guid Org      { get; } = Guid.NewGuid();
    public Guid OtherOrg { get; } = Guid.NewGuid();

    public StaticTenantContext             Tenant    { get; }
    public MaterialDbContext               Material  { get; }
    public InventoryDbContext              Inventory { get; }
    public BomRepository                   Boms      { get; }
    public ServiceProvider                 Provider  { get; }
    public IProductionOrderService         Orders    => Provider.GetRequiredService<IProductionOrderService>();
    public IQualityInspectionService       Qi        => Provider.GetRequiredService<IQualityInspectionService>();
    public IFinishedGoodsReceiptService    Fgr       => Provider.GetRequiredService<IFinishedGoodsReceiptService>();
    public Mock<IManufacturingNotificationService> Notify { get; } = new();
    public Guid Plant { get; }
    public int  PlantId { get; }

    private int _bomSeq, _prodSeq, _srSeq, _pmiSeq, _qiSeq, _fgrSeq;

    public A34Harness(Action<ServiceCollection>? configure = null)
    {
        Tenant    = new StaticTenantContext { OrganizationId = Org };
        Material  = new MaterialDbContext(new DbContextOptionsBuilder<MaterialDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, Tenant);
        Inventory = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, Tenant);

        var numbers = new Mock<IDocumentNumberGenerator>();
        numbers.Setup(n => n.NextAsync(ManufacturingDocumentPrefix.BillOfMaterials, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(() => $"BOM-2026-{++_bomSeq:D5}");
        numbers.Setup(n => n.NextAsync(ManufacturingDocumentPrefix.ProductionOrder, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(() => $"PROD-2026-{++_prodSeq:D5}");
        numbers.Setup(n => n.NextAsync(ManufacturingDocumentPrefix.SupplyRequirement, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(() => $"SR-2026-{++_srSeq:D5}");
        numbers.Setup(n => n.NextAsync(ManufacturingDocumentPrefix.ProductionIssue, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(() => $"PMI-2026-{++_pmiSeq:D5}");
        numbers.Setup(n => n.NextAsync(ManufacturingDocumentPrefix.QualityInspection, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(() => $"QI-2026-{++_qiSeq:D5}");
        numbers.Setup(n => n.NextAsync(ManufacturingDocumentPrefix.FinishedGoodsReceipt, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(() => $"FGR-2026-{++_fgrSeq:D5}");
        Numbers = numbers;

        var plant = new Warehouse { Uuid = Guid.NewGuid(), Code = "PLANT", Name = "Plant", IsActive = true, CreatedBy = 1 };
        Inventory.Warehouses.Add(plant);
        Inventory.SaveChanges();
        Plant   = plant.Uuid;
        PlantId = plant.Id;

        var supplierNames = new Mock<ISupplierNameLookupService>();
        supplierNames.Setup(s => s.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()))
            .ReturnsAsync((IReadOnlyList<Guid> ids) => ids.ToDictionary(id => id, _ => "Acme Supplies"));
        var rates = new Mock<IVariantSupplierResolver>();
        rates.Setup(r => r.GetActiveRateAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateOnly>()))
            .ReturnsAsync((ActiveRateInfo?)null);

        var services = new ServiceCollection();
        services.AddSingleton(Material);
        services.AddSingleton(Inventory);
        services.AddSingleton<ITenantContext>(Tenant);
        services.AddSingleton(numbers.Object);
        services.AddSingleton(new Mock<IPurchaseOrderService>().Object);
        services.AddSingleton(supplierNames.Object);
        services.AddSingleton(rates.Object);
        services.AddSingleton(Notify.Object);
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger<InventoryLedgerService>>(NullLogger<InventoryLedgerService>.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IInventoryLedgerService, InventoryLedgerService>();
        services.AddSingleton<IStockReservationService, StockReservationService>();
        services.AddSingleton<IAllocationRunListener, ProductionReadinessListener>();
        services.AddSingleton<IAllocationReceiptListener, ProductionReadinessListener>();
        services.AddSingleton<IAllocationEngine, AllocationEngine>();
        services.AddSingleton<IProductionOrderRepository, ProductionOrderRepository>();
        services.AddSingleton<ProductionOrderService>();
        services.AddSingleton<IProductionOrderService>(sp => sp.GetRequiredService<ProductionOrderService>());
        services.AddSingleton<IProductionDemandService>(sp => sp.GetRequiredService<ProductionOrderService>());
        services.AddSingleton<ISupplyRequirementEngine, SupplyRequirementEngine>();
        services.AddSingleton<IProductionMaterialIssueService, ProductionMaterialIssueService>();
        services.AddSingleton<IQualityInspectionService, QualityInspectionService>();
        services.AddSingleton<IFinishedGoodsReceiptService, FinishedGoodsReceiptService>();
        services.AddSingleton<IBomStructureReader, BomStructureReader>();
        services.AddSingleton<IManufacturingReadiness, ManufacturingReadiness>();
        services.AddSingleton<IFulfillmentRouteUsage, ProductionOrderRouteUsage>();
        configure?.Invoke(services);
        Provider = services.BuildServiceProvider();

        Boms = new BomRepository(Material, Inventory, numbers.Object);
    }

    public Mock<IDocumentNumberGenerator> Numbers { get; }

    public T Get<T>() where T : notnull => Provider.GetRequiredService<T>();

    /// <summary>A manufactured finished good (with <see cref="Plant"/> as its production warehouse unless told otherwise), or a raw material.</summary>
    public (Guid Product, Guid Variant) Product(string name, bool manufactured, bool productionWarehouse = true, Guid? organizationId = null)
    {
        var product = new Product
        {
            Uuid = Guid.NewGuid(), Sku = $"SKU-{Guid.NewGuid():N}"[..12], Name = name, UomCode = "PCS",
            ProductType  = manufactured ? SMS.Shared.Common.ProductType.FinishedGood : SMS.Shared.Common.ProductType.RawMaterial,
            SupplyMethod = manufactured ? SupplyMethod.Manufacture : SupplyMethod.Purchase,
            IsManufacturable = manufactured,
            DefaultProductionWarehouseId = manufactured && productionWarehouse ? PlantId : null,
            IsActive = true, CreatedBy = 1, OrganizationId = organizationId ?? Guid.Empty
        };
        var variant = new ProductVariant
        {
            Uuid = Guid.NewGuid(), Sku = $"{product.Sku}-1", VariantName = "Default", IsDefault = true, IsActive = true,
            PurchasePrice = 10m, IsAvailableForProduction = true, CreatedBy = 1, OrganizationId = organizationId ?? Guid.Empty
        };
        product.Variants.Add(variant);
        Inventory.Products.Add(product);
        Inventory.SaveChanges();
        return (product.Uuid, variant.Uuid);
    }

    /// <summary>A second, non-default variant of an existing product.</summary>
    public Guid Variant(Guid productUuid, string name)
    {
        var product = Inventory.Products.IgnoreQueryFilters().Single(p => p.Uuid == productUuid);
        var variant = new ProductVariant
        {
            Uuid = Guid.NewGuid(), ProductId = product.Id, Sku = $"{product.Sku}-{name}", VariantName = name, IsActive = true,
            PurchasePrice = 10m, IsAvailableForProduction = true, CreatedBy = 1, OrganizationId = product.OrganizationId
        };
        Inventory.ProductVariants.Add(variant);
        Inventory.SaveChanges();
        return variant.Uuid;
    }

    /// <summary>Gives a product of <see cref="OtherOrg"/> a production warehouse of its own organization.</summary>
    public Guid TheirPlant(Guid productUuid)
    {
        var plant = new Warehouse { Uuid = Guid.NewGuid(), Code = "THEIRS", Name = "Their plant", IsActive = true, CreatedBy = 1, OrganizationId = OtherOrg };
        Inventory.Warehouses.Add(plant);
        Inventory.SaveChanges();
        Inventory.Products.IgnoreQueryFilters().Single(p => p.Uuid == productUuid).DefaultProductionWarehouseId = plant.Id;
        Inventory.SaveChanges();
        return plant.Uuid;
    }

    public void Stock(Guid variantUuid, Guid warehouseUuid, decimal qty)
    {
        var variantId   = Inventory.ProductVariants.IgnoreQueryFilters().First(v => v.Uuid == variantUuid).Id;
        var warehouseId = Inventory.Warehouses.IgnoreQueryFilters().First(w => w.Uuid == warehouseUuid).Id;
        Inventory.InventoryItems.Add(new InventoryItem { Uuid = Guid.NewGuid(), VariantId = variantId, WarehouseId = warehouseId, QtyOnHand = qty, UnitCost = 3m });
        Inventory.SaveChanges();
    }

    /// <summary>Through the real BOM workflow (draft → submit → approve → activate).</summary>
    public async Task<Guid> ActiveBomAsync(Guid product, decimal baseQty, params (Guid Variant, decimal Qty, decimal Scrap)[] lines)
    {
        var uuid = await Boms.CreateAsync(new CreateBomRequest
        {
            ProductUuid = product, BaseQuantity = baseQty,
            Lines = lines.Select(l => new BomLineRequest { MaterialVariantUuid = l.Variant, Quantity = l.Qty, ScrapPercentage = l.Scrap, IsCritical = true }).ToList()
        }, Author);
        await Boms.SubmitAsync(uuid, Author);
        await Boms.ApproveAsync(uuid, Author + 1);
        await Boms.ActivateAsync(uuid, Author + 1);
        return uuid;
    }

    /// <summary>Written straight to the context, for states and versions the workflow would not leave side by side.</summary>
    public BillOfMaterial RawBom(Guid product, Guid? variant, int version, string status = BomStatus.Active,
        DateTime? from = null, DateTime? to = null, Guid? organizationId = null, decimal baseQty = 1m, params (Guid Variant, Guid Product, decimal Qty, decimal Scrap)[] lines)
    {
        var bom = new BillOfMaterial
        {
            BomNumber = $"BOM-RAW-{Guid.NewGuid():N}"[..16], ProductUuid = product, ProductVariantUuid = variant, Version = version,
            Status = status, EffectiveFrom = from, EffectiveTo = to, BaseQuantity = baseQty, BaseUom = "PCS", CreatedBy = Author,
            OrganizationId = organizationId ?? Guid.Empty,
            Lines = lines.Select((l, i) => new BillOfMaterialLine
            {
                Sequence = i + 1, MaterialVariantUuid = l.Variant, MaterialProductUuid = l.Product, Quantity = l.Qty, ScrapPercentage = l.Scrap,
                Uom = "PCS", OrganizationId = organizationId ?? Guid.Empty
            }).ToList()
        };
        Material.BillsOfMaterials.Add(bom);
        Material.SaveChanges();
        return bom;
    }
}

/// <summary>A34 — routes by (org, uuid), category included; other organizations' routes read as absent.</summary>
internal sealed class FakeRouteLookup : IFulfillmentRouteLookup
{
    public readonly Dictionary<(Guid Org, Guid Route), FulfillmentRouteSummary> Routes = [];

    public Guid Add(Guid org, string category, string code = "MFG_PICK_SHIP")
    {
        var uuid = Guid.NewGuid();
        Routes[(org, uuid)] = new FulfillmentRouteSummary(uuid, code, code, true, false, true, false, true,
            ["PICK", "GOODS_ISSUE", "SHIP"]) { Category = category };
        return uuid;
    }

    public Task<IReadOnlyDictionary<Guid, FulfillmentRouteSummary>> GetAsync(Guid organizationId, IReadOnlyCollection<Guid> routeUuids, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, FulfillmentRouteSummary>>(
            Routes.Where(r => r.Key.Org == organizationId && routeUuids.Contains(r.Key.Route)).ToDictionary(r => r.Key.Route, r => r.Value));

    public Task<FulfillmentRouteDefaults> GetOrgDefaultsAsync(Guid organizationId, CancellationToken ct = default) =>
        Task.FromResult(new FulfillmentRouteDefaults(null, null));

    public Task<IReadOnlyList<FulfillmentRouteSummary>> ListActiveAsync(Guid organizationId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<FulfillmentRouteSummary>>(Routes.Where(r => r.Key.Org == organizationId).Select(r => r.Value).ToList());
}

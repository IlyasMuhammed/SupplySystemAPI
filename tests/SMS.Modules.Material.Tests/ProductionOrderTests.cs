using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Models;
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
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Material.Tests;

/// <summary>
/// Production orders end to end (A30 §11–§13, §16; A30-P3-01..18). T-PR01..10 and T-SR01..06's
/// core paths. The allocation engine, its stock reservation service and the readiness listener are
/// real; a purchase order is a mock, since raising one is Demand's own job, already proven there.
/// </summary>
public class ProductionOrderTests
{
    private const int Author = 7;
    private static readonly DateTime Today = DateTime.UtcNow.Date;

    private sealed class Harness
    {
        public MaterialDbContext           Material       { get; }
        public InventoryDbContext          Inventory      { get; }
        public BomRepository               Boms           { get; }
        public IProductionOrderService     Orders         { get; }
        public IProductionMaterialIssueService Issues     { get; }
        public IAllocationEngine           Engine         { get; }
        public Mock<IPurchaseOrderService> PurchaseOrders { get; } = new();
        public Mock<IManufacturingNotificationService> Notify { get; } = new();
        public Guid Plant { get; }
        public Guid Secondary { get; }

        private int _bomSeq, _prodSeq, _srSeq, _pmiSeq;

        public Harness()
        {
            var tenant = new StaticTenantContext();
            Material  = new MaterialDbContext(new DbContextOptionsBuilder<MaterialDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);
            Inventory = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);

            var numbers = new Mock<IDocumentNumberGenerator>();
            numbers.Setup(n => n.NextAsync(ManufacturingDocumentPrefix.BillOfMaterials, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(() => $"BOM-2026-{++_bomSeq:D5}");
            numbers.Setup(n => n.NextAsync(ManufacturingDocumentPrefix.ProductionOrder, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(() => $"PROD-2026-{++_prodSeq:D5}");
            numbers.Setup(n => n.NextAsync(ManufacturingDocumentPrefix.SupplyRequirement, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(() => $"SR-2026-{++_srSeq:D5}");
            numbers.Setup(n => n.NextAsync(ManufacturingDocumentPrefix.ProductionIssue, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(() => $"PMI-2026-{++_pmiSeq:D5}");

            var plant     = new Warehouse { Uuid = Guid.NewGuid(), Code = "PLANT", Name = "Plant",     IsActive = true, CreatedBy = 1 };
            var secondary = new Warehouse { Uuid = Guid.NewGuid(), Code = "SEC",   Name = "Secondary", IsActive = true, CreatedBy = 1 };
            Inventory.Warehouses.AddRange(plant, secondary);
            Inventory.SaveChanges();
            Plant     = plant.Uuid;
            Secondary = secondary.Uuid;

            var supplierNames = new Mock<ISupplierNameLookupService>();
            supplierNames.Setup(s => s.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()))
                .ReturnsAsync((IReadOnlyList<Guid> ids) => ids.ToDictionary(id => id, _ => "Acme Supplies"));
            var rates = new Mock<IVariantSupplierResolver>();
            rates.Setup(r => r.GetActiveRateAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateOnly>()))
                .ReturnsAsync((ActiveRateInfo?)null);

            var services = new ServiceCollection();
            services.AddSingleton(Material);
            services.AddSingleton(Inventory);
            services.AddSingleton(numbers.Object);
            services.AddSingleton(PurchaseOrders.Object);
            services.AddSingleton(supplierNames.Object);
            services.AddSingleton(rates.Object);
            services.AddSingleton(Notify.Object);
            services.AddSingleton<Microsoft.Extensions.Logging.ILogger<InventoryLedgerService>>(NullLogger<InventoryLedgerService>.Instance);
            services.AddSingleton<IInventoryLedgerService, InventoryLedgerService>();
            services.AddSingleton<IStockReservationService, StockReservationService>();
            services.AddSingleton<IAllocationRunListener, ProductionReadinessListener>();
            services.AddSingleton<IAllocationEngine, AllocationEngine>();
            services.AddSingleton<IProductionOrderRepository, ProductionOrderRepository>();
            services.AddSingleton<ProductionOrderService>();
            services.AddSingleton<IProductionOrderService>(sp => sp.GetRequiredService<ProductionOrderService>());
            services.AddSingleton<ISupplyRequirementEngine, SupplyRequirementEngine>();
            services.AddSingleton<IProductionMaterialIssueService, ProductionMaterialIssueService>();
            var provider = services.BuildServiceProvider();

            Boms      = new BomRepository(Material, Inventory, numbers.Object);
            Orders    = provider.GetRequiredService<IProductionOrderService>();
            Issues    = provider.GetRequiredService<IProductionMaterialIssueService>();
            Engine    = provider.GetRequiredService<IAllocationEngine>();
        }

        /// <summary>A manufactured finished good, or a raw material when <paramref name="manufactured"/> is false.</summary>
        public (Guid Product, Guid Variant) Product(
            string name, bool manufactured, Guid? defaultSupplier = null, decimal purchasePrice = 10m, string uom = "PCS")
        {
            var product = new Product
            {
                Uuid = Guid.NewGuid(), Sku = $"SKU-{Guid.NewGuid():N}"[..12], Name = name, UomCode = uom,
                ProductType   = manufactured ? SMS.Shared.Common.ProductType.FinishedGood : SMS.Shared.Common.ProductType.RawMaterial,
                SupplyMethod  = manufactured ? SupplyMethod.Manufacture : SupplyMethod.Purchase,
                IsActive = true, CreatedBy = 1
            };
            var variant = new ProductVariant
            {
                Uuid = Guid.NewGuid(), Sku = $"{product.Sku}-1", VariantName = "Default", IsDefault = true, IsActive = true,
                PurchasePrice = purchasePrice, IsAvailableForProduction = true, DefaultSupplierId = defaultSupplier, CreatedBy = 1
            };
            product.Variants.Add(variant);
            Inventory.Products.Add(product);
            Inventory.SaveChanges();
            return (product.Uuid, variant.Uuid);
        }

        public void Stock(Guid variantUuid, Guid warehouseUuid, decimal qty, decimal unitCost = 3m)
        {
            var variantId  = Inventory.ProductVariants.First(v => v.Uuid == variantUuid).Id;
            var warehouseId = Inventory.Warehouses.First(w => w.Uuid == warehouseUuid).Id;
            Inventory.InventoryItems.Add(new InventoryItem { Uuid = Guid.NewGuid(), VariantId = variantId, WarehouseId = warehouseId, QtyOnHand = qty, UnitCost = unitCost });
            Inventory.SaveChanges();
        }

        public async Task<Guid> ActiveBomAsync(Guid product, decimal baseQty, params (Guid Variant, decimal Qty, decimal Scrap, bool Critical)[] lines)
        {
            var uuid = await Boms.CreateAsync(new CreateBomRequest
            {
                ProductUuid = product, BaseQuantity = baseQty, WarehouseUuid = Plant,
                Lines = lines.Select(l => new BomLineRequest { MaterialVariantUuid = l.Variant, Quantity = l.Qty, ScrapPercentage = l.Scrap, IsCritical = l.Critical }).ToList()
            }, Author);
            await Boms.SubmitAsync(uuid, Author);
            await Boms.ApproveAsync(uuid, Author + 1);
            await Boms.ActivateAsync(uuid, Author + 1);
            return uuid;
        }

        public InventoryItem Item(Guid variantUuid, Guid warehouseUuid)
        {
            var variantId   = Inventory.ProductVariants.First(v => v.Uuid == variantUuid).Id;
            var warehouseId = Inventory.Warehouses.First(w => w.Uuid == warehouseUuid).Id;
            return Inventory.InventoryItems.AsNoTracking().Single(i => i.VariantId == variantId && i.WarehouseId == warehouseId);
        }
    }

    private static CreateProductionOrderRequest Order(Guid product, decimal qty, Guid warehouse, bool plan = true, int priority = AllocationPriority.Normal) => new()
    {
        ProductUuid = product, PlannedQuantity = qty, WarehouseUuid = warehouse, RequiredDate = Today.AddDays(3), Plan = plan, Priority = priority
    };

    // ── Create + plan (T-PR01..04) ────────────────────────────────────────────

    [Fact]
    public async Task Planning_with_enough_stock_reserves_everything_and_the_order_is_ready()
    {
        var h = new Harness();
        var (shirt, _)   = h.Product("Printed T-Shirt", manufactured: true);
        var (_, plainV)  = h.Product("Plain T-Shirt", manufactured: false);
        var (_, inkV)    = h.Product("Printing Ink", manufactured: false);
        await h.ActiveBomAsync(shirt, 1m, (plainV, 1m, 0m, true), (inkV, 0.1m, 10m, true));
        h.Stock(plainV, h.Plant, 50);
        h.Stock(inkV, h.Plant, 10);

        var uuid = await h.Orders.CreateAsync(Order(shirt, 20, h.Plant), Author);

        var detail = await h.Orders.GetByUuidAsync(uuid);
        detail!.Status.Should().Be(ProductionOrderStatus.Ready);
        detail.MaterialReadiness.Should().Be(MaterialReadiness.Ready);
        detail.Materials.Should().HaveCount(2);

        var plainLine = detail.Materials.Single(m => m.MaterialVariantUuid == plainV);
        plainLine.NetQuantity.Should().Be(20, "1 per shirt × 20 shirts");
        plainLine.RequiredQuantity.Should().Be(20, "no scrap allowance on this line");
        plainLine.ReservedQuantity.Should().Be(20);
        plainLine.IsCovered.Should().BeTrue();

        var inkLine = detail.Materials.Single(m => m.MaterialVariantUuid == inkV);
        inkLine.NetQuantity.Should().Be(2m, "0.1 per shirt × 20 shirts");
        inkLine.RequiredQuantity.Should().Be(2.2m, "10% scrap allowance on 2");
        inkLine.ReservedQuantity.Should().Be(2.2m);

        h.Item(plainV, h.Plant).QtyReserved.Should().Be(20);
    }

    [Fact]
    public async Task A_production_order_needs_a_manufactured_product_a_default_variant_and_an_active_bom()
    {
        var h = new Harness();
        var (laptop, _) = h.Product("Laptop", manufactured: false);
        var notManufactured = () => h.Orders.CreateAsync(Order(laptop, 1, h.Plant), Author);
        await notManufactured.Should().ThrowAsync<BadRequestException>().WithMessage("*not configured for manufacturing*");

        var (shirt, _) = h.Product("Printed T-Shirt", manufactured: true);
        var noBom = () => h.Orders.CreateAsync(Order(shirt, 1, h.Plant), Author);
        await noBom.Should().ThrowAsync<BadRequestException>().WithMessage("*No active BOM found*Printed T-Shirt*");
    }

    [Fact]
    public async Task A_shortage_of_a_purchased_material_raises_and_orders_a_supply_requirement()
    {
        var h = new Harness();
        var supplierId = Guid.NewGuid();
        var (shirt, _)  = h.Product("Printed T-Shirt", manufactured: true);
        var (_, plainV) = h.Product("Plain T-Shirt", manufactured: false, defaultSupplier: supplierId, purchasePrice: 4m);
        await h.ActiveBomAsync(shirt, 1m, (plainV, 1m, 0m, true));
        h.Stock(plainV, h.Plant, 5); // only 5 of the 20 needed

        var poUuid     = Guid.NewGuid();
        var poLineUuid = Guid.NewGuid();
        h.PurchaseOrders.Setup(p => p.CreateAsync(It.IsAny<CreatePoRequest>(), It.IsAny<int>())).ReturnsAsync(poUuid);
        h.PurchaseOrders.Setup(p => p.GetByIdAsync(poUuid)).ReturnsAsync(new PoDetailModel
        {
            UUID = poUuid, PoNumber = "PO-2026-00099", Lines = [new PoLineModel { UUID = poLineUuid, Quantity = 15, UnitPrice = 4m }]
        });

        var uuid   = await h.Orders.CreateAsync(Order(shirt, 20, h.Plant), Author);
        var detail = await h.Orders.GetByUuidAsync(uuid);

        detail!.Status.Should().Be(ProductionOrderStatus.MaterialPending);
        var line = detail.Materials.Single();
        line.ReservedQuantity.Should().Be(5);
        line.ShortageQuantity.Should().Be(15);

        var supply = (await h.Orders.GetSupplyRequirementsForOrderAsync(uuid)).Single();
        supply.QuantityRequired.Should().Be(15);
        supply.SupplyMethod.Should().Be(SupplyMethod.Purchase);
        supply.Status.Should().Be(SupplyRequirementStatus.Ordered);
        supply.SupplySourceReference.Should().Be("PO-2026-00099");

        h.PurchaseOrders.Verify(p => p.CreateAsync(
            It.Is<CreatePoRequest>(r => r.SupplierId == supplierId && r.Lines!.Single().Quantity == 15), Author), Times.Once);
    }

    [Fact]
    public async Task A_shortage_of_a_manufactured_material_raises_and_plans_a_child_production_order()
    {
        var h = new Harness();
        var (kit, _)   = h.Product("Bolt Kit", manufactured: true);
        var (bolt, boltV) = h.Product("Steel Bolt", manufactured: true);
        var (_, rodV)  = h.Product("Steel Rod", manufactured: false);
        await h.ActiveBomAsync(kit, 1m, (boltV, 4m, 0m, true));
        await h.ActiveBomAsync(bolt, 1m, (rodV, 1m, 0m, true));
        h.Stock(rodV, h.Plant, 1000); // the grandchild's own material is plentiful

        var uuid   = await h.Orders.CreateAsync(Order(kit, 10, h.Plant), Author); // needs 40 bolts, has none
        var detail = await h.Orders.GetByUuidAsync(uuid);

        var supply = (await h.Orders.GetSupplyRequirementsForOrderAsync(uuid)).Single();
        supply.SupplyMethod.Should().Be(SupplyMethod.Manufacture);
        supply.Status.Should().Be(SupplyRequirementStatus.Ordered);
        supply.SupplySourceType.Should().Be(SupplySourceType.ProductionOrder);

        var child = detail!.ChildOrders.Single();
        child.ProductUuid.Should().Be(bolt);
        child.PlannedQuantity.Should().Be(40);
        // The grandchild's own material was plentiful, so the child itself is fully covered.
        child.Status.Should().Be(ProductionOrderStatus.Ready);
        child.SourceType.Should().Be(ProductionSourceType.SupplyRequirement);

        // A30-P5-01 wiring proof: the kit's own creation, its shortage, the chained child raised to
        // cover it, and (since the child's own material was plentiful) its readiness all actually
        // reach the notification service, not just the timeline.
        h.Notify.Verify(n => n.ProductionOrderCreatedAsync(It.Is<ProductionOrder>(p => p.UUID == uuid), "Bolt Kit", Author), Times.Once);
        h.Notify.Verify(n => n.ShortageAlertAsync(It.Is<ProductionOrder>(p => p.UUID == uuid), "Steel Bolt", 40m, It.IsAny<string>()), Times.Once);
        h.Notify.Verify(n => n.ChainedProductionOrderCreatedAsync(
            It.Is<ProductionOrder>(p => p.UUID == uuid), It.Is<ProductionOrder>(p => p.ProductUuid == bolt), "Steel Bolt"), Times.Once);
        h.Notify.Verify(n => n.ProductionOrderReadyAsync(It.Is<ProductionOrder>(p => p.ProductUuid == bolt)), Times.Once);
    }

    // ── Readiness after a receipt (T-PR04, T-AL05/08 territory) ───────────────

    [Fact]
    public async Task Receiving_the_purchase_order_completes_the_hold_and_the_order_becomes_ready()
    {
        var h = new Harness();
        var (shirt, _)  = h.Product("Printed T-Shirt", manufactured: true);
        var (_, plainV) = h.Product("Plain T-Shirt", manufactured: false, defaultSupplier: Guid.NewGuid());
        await h.ActiveBomAsync(shirt, 1m, (plainV, 1m, 0m, true));

        var poUuid     = Guid.NewGuid();
        var poLineUuid = Guid.NewGuid();
        h.PurchaseOrders.Setup(p => p.CreateAsync(It.IsAny<CreatePoRequest>(), It.IsAny<int>())).ReturnsAsync(poUuid);
        h.PurchaseOrders.Setup(p => p.GetByIdAsync(poUuid)).ReturnsAsync(new PoDetailModel
        {
            UUID = poUuid, PoNumber = "PO-2026-00050", Lines = [new PoLineModel { UUID = poLineUuid, Quantity = 20 }]
        });

        var uuid = await h.Orders.CreateAsync(Order(shirt, 20, h.Plant), Author);
        (await h.Orders.GetByUuidAsync(uuid))!.Status.Should().Be(ProductionOrderStatus.MaterialPending);

        // The GRN poster's own job (proven in GrnAllocationTriggerTests): book the receipt, re-run allocation.
        h.Stock(plainV, h.Plant, 20);
        await h.Engine.SupplyReceivedAsync(AllocationSupplyType.PurchaseOrder, poUuid, poLineUuid, 20);
        await h.Engine.AllocateAsync(plainV, null, Author);

        var detail = await h.Orders.GetByUuidAsync(uuid);
        detail!.Status.Should().Be(ProductionOrderStatus.Ready);
        detail.Materials.Single().ReservedQuantity.Should().Be(20);

        var supply = (await h.Orders.GetSupplyRequirementsForOrderAsync(uuid)).Single();
        supply.Status.Should().Be(SupplyRequirementStatus.Fulfilled);
    }

    // ── Execution (§17, T-PR08..10) ────────────────────────────────────────────

    [Fact]
    public async Task Start_report_output_and_complete_walk_an_order_through_its_states()
    {
        var h = new Harness();
        var (shirt, _)  = h.Product("Printed T-Shirt", manufactured: true);
        var (_, plainV) = h.Product("Plain T-Shirt", manufactured: false);
        await h.ActiveBomAsync(shirt, 1m, (plainV, 1m, 0m, true));
        h.Stock(plainV, h.Plant, 100);
        var uuid = await h.Orders.CreateAsync(Order(shirt, 20, h.Plant), Author);

        var startTooEarly = () => h.Orders.StartAsync(Guid.NewGuid(), Author);
        await startTooEarly.Should().ThrowAsync<NotFoundException>();

        await h.Orders.StartAsync(uuid, Author);
        (await h.Orders.GetByUuidAsync(uuid))!.Status.Should().Be(ProductionOrderStatus.InProgress);

        var restart = () => h.Orders.StartAsync(uuid, Author);
        await restart.Should().ThrowAsync<BadRequestException>().WithMessage("*only a ready order can be started*");

        var overReport = () => h.Orders.ReportOutputAsync(uuid, new ReportOutputRequest { Quantity = 25 }, Author);
        await overReport.Should().ThrowAsync<BadRequestException>().WithMessage("*more than the 20*planned*");

        await h.Orders.ReportOutputAsync(uuid, new ReportOutputRequest { Quantity = 15 }, Author);
        await h.Orders.ReportOutputAsync(uuid, new ReportOutputRequest { Quantity = 5 }, Author);
        (await h.Orders.GetByUuidAsync(uuid))!.ProducedQuantity.Should().Be(20);

        await h.Orders.CompleteAsync(uuid, Author);
        (await h.Orders.GetByUuidAsync(uuid))!.Status.Should().Be(ProductionOrderStatus.QualityInspection);
    }

    [Fact]
    public async Task Cancelling_an_order_releases_its_holds_and_cancels_its_open_supply_requirement()
    {
        var h = new Harness();
        var (shirt, _)  = h.Product("Printed T-Shirt", manufactured: true);
        var (_, plainV) = h.Product("Plain T-Shirt", manufactured: false, defaultSupplier: Guid.NewGuid());
        await h.ActiveBomAsync(shirt, 1m, (plainV, 1m, 0m, true));
        h.Stock(plainV, h.Plant, 5);

        h.PurchaseOrders.Setup(p => p.CreateAsync(It.IsAny<CreatePoRequest>(), It.IsAny<int>())).ReturnsAsync(Guid.NewGuid());
        h.PurchaseOrders.Setup(p => p.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync((Guid u) => new PoDetailModel { UUID = u, PoNumber = "PO-X", Lines = [new PoLineModel { UUID = Guid.NewGuid() }] });

        var uuid = await h.Orders.CreateAsync(Order(shirt, 20, h.Plant), Author);
        h.Item(plainV, h.Plant).QtyReserved.Should().Be(5);

        await h.Orders.CancelAsync(uuid, new CancelProductionOrderRequest { Reason = "Customer withdrew the order." }, Author);

        var detail = await h.Orders.GetByUuidAsync(uuid);
        detail!.Status.Should().Be(ProductionOrderStatus.Cancelled);
        detail.Materials.Single().Status.Should().Be(PmrStatus.Cancelled);
        h.Item(plainV, h.Plant).QtyReserved.Should().Be(0, "cancelling gives back what was held");

        var supply = (await h.Orders.GetSupplyRequirementsForOrderAsync(uuid)).Single();
        supply.Status.Should().Be(SupplyRequirementStatus.Cancelled);

        var cancelAgain = () => h.Orders.CancelAsync(uuid, new CancelProductionOrderRequest { Reason = "Again." }, Author);
        await cancelAgain.Should().ThrowAsync<BadRequestException>();
    }

    // ── Material issues (§16, T-PR05..07) ─────────────────────────────────────

    [Fact]
    public async Task A_standard_issue_takes_what_was_held_deducts_stock_and_cannot_exceed_it()
    {
        var h = new Harness();
        var (shirt, _)  = h.Product("Printed T-Shirt", manufactured: true);
        var (_, plainV) = h.Product("Plain T-Shirt", manufactured: false);
        await h.ActiveBomAsync(shirt, 1m, (plainV, 1m, 0m, true));
        h.Stock(plainV, h.Plant, 20);
        var uuid = await h.Orders.CreateAsync(Order(shirt, 20, h.Plant), Author);
        await h.Orders.StartAsync(uuid, Author);
        var requirement = (await h.Orders.GetMaterialsAsync(uuid)).Single();

        var tooMuch = () => h.Issues.CreateAsync(uuid, new CreateProductionIssueRequest
        {
            IssueType = "STANDARD", Confirm = true,
            Lines = [new CreateProductionIssueLineRequest { RequirementUuid = requirement.UUID, Quantity = 25 }]
        }, Author);
        await tooMuch.Should().ThrowAsync<BadRequestException>().WithMessage("*Only*20*held*");

        var issueUuid = await h.Issues.CreateAsync(uuid, new CreateProductionIssueRequest
        {
            IssueType = "STANDARD", Confirm = true,
            Lines = [new CreateProductionIssueLineRequest { RequirementUuid = requirement.UUID, Quantity = 12 }]
        }, Author);

        var issue = await h.Issues.GetAsync(issueUuid);
        issue!.Status.Should().Be(ProductionIssueStatus.Confirmed);
        issue.TotalQuantity.Should().Be(12);

        h.Item(plainV, h.Plant).QtyOnHand.Should().Be(8);
        h.Item(plainV, h.Plant).QtyReserved.Should().Be(8);
        (await h.Orders.GetMaterialsAsync(uuid)).Single().IssuedQuantity.Should().Be(12);
    }

    [Fact]
    public async Task A_return_gives_stock_back_and_a_scrap_records_wastage_with_no_stock_movement()
    {
        var h = new Harness();
        var (shirt, _)  = h.Product("Printed T-Shirt", manufactured: true);
        var (_, plainV) = h.Product("Plain T-Shirt", manufactured: false);
        await h.ActiveBomAsync(shirt, 1m, (plainV, 1m, 0m, true));
        h.Stock(plainV, h.Plant, 20);
        var uuid = await h.Orders.CreateAsync(Order(shirt, 20, h.Plant), Author);
        await h.Orders.StartAsync(uuid, Author);
        var requirement = (await h.Orders.GetMaterialsAsync(uuid)).Single();

        await h.Issues.CreateAsync(uuid, new CreateProductionIssueRequest
        {
            IssueType = "STANDARD", Confirm = true,
            Lines = [new CreateProductionIssueLineRequest { RequirementUuid = requirement.UUID, Quantity = 15 }]
        }, Author);

        var onHandAfterIssue = h.Item(plainV, h.Plant).QtyOnHand;

        await h.Issues.CreateAsync(uuid, new CreateProductionIssueRequest
        {
            IssueType = "RETURN", Confirm = true,
            Lines = [new CreateProductionIssueLineRequest { RequirementUuid = requirement.UUID, Quantity = 3 }]
        }, Author);
        h.Item(plainV, h.Plant).QtyOnHand.Should().Be(onHandAfterIssue + 3);
        (await h.Orders.GetMaterialsAsync(uuid)).Single().ReturnedQuantity.Should().Be(3);

        await h.Issues.CreateAsync(uuid, new CreateProductionIssueRequest
        {
            IssueType = "SCRAP", Confirm = true,
            Lines = [new CreateProductionIssueLineRequest { RequirementUuid = requirement.UUID, Quantity = 2 }]
        }, Author);
        h.Item(plainV, h.Plant).QtyOnHand.Should().Be(onHandAfterIssue + 3, "scrap records a loss that already left stock; nothing moves again");
        (await h.Orders.GetMaterialsAsync(uuid)).Single().WastageQuantity.Should().Be(2);
    }

    [Fact]
    public async Task Reversing_a_standard_issue_gives_the_stock_back_and_rolls_back_what_was_issued()
    {
        var h = new Harness();
        var (shirt, _)  = h.Product("Printed T-Shirt", manufactured: true);
        var (_, plainV) = h.Product("Plain T-Shirt", manufactured: false);
        await h.ActiveBomAsync(shirt, 1m, (plainV, 1m, 0m, true));
        h.Stock(plainV, h.Plant, 20);
        var uuid = await h.Orders.CreateAsync(Order(shirt, 20, h.Plant), Author);
        await h.Orders.StartAsync(uuid, Author);
        var requirement = (await h.Orders.GetMaterialsAsync(uuid)).Single();

        var issueUuid = await h.Issues.CreateAsync(uuid, new CreateProductionIssueRequest
        {
            IssueType = "STANDARD", Confirm = true,
            Lines = [new CreateProductionIssueLineRequest { RequirementUuid = requirement.UUID, Quantity = 10 }]
        }, Author);
        h.Item(plainV, h.Plant).QtyOnHand.Should().Be(10);

        await h.Issues.ReverseAsync(issueUuid, "Wrong material picked.", Author);

        h.Item(plainV, h.Plant).QtyOnHand.Should().Be(20);
        (await h.Orders.GetMaterialsAsync(uuid)).Single().IssuedQuantity.Should().Be(0);
        (await h.Issues.GetAsync(issueUuid))!.Status.Should().Be(ProductionIssueStatus.Reversed);

        var reverseAgain = () => h.Issues.ReverseAsync(issueUuid, "Again.", Author);
        await reverseAgain.Should().ThrowAsync<BadRequestException>();
    }

    // ── Shortage dashboard and manual supply requirements (§29.3, T-SR01) ─────

    [Fact]
    public async Task The_shortage_dashboard_lists_an_uncovered_critical_requirement()
    {
        var h = new Harness();
        var (shirt, _)  = h.Product("Printed T-Shirt", manufactured: true);
        var (_, plainV) = h.Product("Plain T-Shirt", manufactured: false, defaultSupplier: Guid.NewGuid());
        await h.ActiveBomAsync(shirt, 1m, (plainV, 1m, 0m, true));
        h.Stock(plainV, h.Plant, 5);
        h.PurchaseOrders.Setup(p => p.CreateAsync(It.IsAny<CreatePoRequest>(), It.IsAny<int>())).ReturnsAsync(Guid.NewGuid());
        h.PurchaseOrders.Setup(p => p.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync((Guid u) => new PoDetailModel { UUID = u, PoNumber = "PO-X", Lines = [new PoLineModel { UUID = Guid.NewGuid() }] });

        var uuid = await h.Orders.CreateAsync(Order(shirt, 20, h.Plant), Author);

        var shortages = await h.Orders.GetShortagesAsync(null);
        var row = shortages.Should().ContainSingle().Subject;
        row.ProductionOrderUuid.Should().Be(uuid);
        row.MaterialVariantUuid.Should().Be(plainV);
        row.ShortageQuantity.Should().Be(15);
        row.SupplyStatus.Should().Be(SupplyRequirementStatus.Ordered);
    }

    [Fact]
    public async Task A_manual_supply_requirement_can_be_raised_without_acting_and_then_cancelled()
    {
        var h = new Harness();
        var (_, plainV) = h.Product("Plain T-Shirt", manufactured: false);

        var uuid = await h.Orders.CreateSupplyRequirementAsync(new CreateSupplyRequirementRequest
        {
            VariantUuid = plainV, QuantityRequired = 50, WarehouseUuid = h.Plant, RequiredDate = Today.AddDays(5), Act = false
        }, Author);

        var sr = await h.Orders.GetSupplyRequirementAsync(uuid);
        sr!.Status.Should().Be(SupplyRequirementStatus.Open);
        sr.QuantityRequired.Should().Be(50);
        h.PurchaseOrders.Verify(p => p.CreateAsync(It.IsAny<CreatePoRequest>(), It.IsAny<int>()), Times.Never);

        await h.Orders.CancelSupplyRequirementAsync(uuid, new CancelSupplyRequirementRequest { Reason = "No longer needed." }, Author);
        (await h.Orders.GetSupplyRequirementAsync(uuid))!.Status.Should().Be(SupplyRequirementStatus.Cancelled);
    }
}

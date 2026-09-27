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
/// Quality inspection, finished goods receipt and the production ledger read over both (A30 §18,
/// §19, §19A; A30-P4-01..08, 16-17). T-QF01..08 and T-PL01..08's core paths.
/// </summary>
public class QualityAndFgrTests
{
    private const int Author   = 7;
    private const int Operator = 9;
    private static readonly DateTime Today = DateTime.UtcNow.Date;

    private sealed class Harness
    {
        public MaterialDbContext               Material  { get; }
        public InventoryDbContext              Inventory { get; }
        public BomRepository                   Boms      { get; }
        public IProductionOrderService         Orders    { get; }
        public IProductionMaterialIssueService Issues    { get; }
        public IQualityInspectionService       Qi        { get; }
        public IFinishedGoodsReceiptService    Fgr       { get; }
        public IProductionLedgerService        Ledger    { get; }
        public IAllocationEngine               Engine    { get; }
        public Mock<IManufacturingNotificationService> Notify { get; } = new();
        public Guid Plant { get; }

        private int _bomSeq, _prodSeq, _srSeq, _pmiSeq, _qiSeq, _fgrSeq;

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
            numbers.Setup(n => n.NextAsync(ManufacturingDocumentPrefix.QualityInspection, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(() => $"QI-2026-{++_qiSeq:D5}");
            numbers.Setup(n => n.NextAsync(ManufacturingDocumentPrefix.FinishedGoodsReceipt, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(() => $"FGR-2026-{++_fgrSeq:D5}");

            var plant = new Warehouse { Uuid = Guid.NewGuid(), Code = "PLANT", Name = "Plant", IsActive = true, CreatedBy = 1 };
            Inventory.Warehouses.Add(plant);
            Inventory.SaveChanges();
            Plant = plant.Uuid;

            var purchaseOrders = new Mock<IPurchaseOrderService>();
            var supplierNames  = new Mock<ISupplierNameLookupService>();
            supplierNames.Setup(s => s.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()))
                .ReturnsAsync((IReadOnlyList<Guid> ids) => ids.ToDictionary(id => id, _ => "Acme Supplies"));
            var rates = new Mock<IVariantSupplierResolver>();
            rates.Setup(r => r.GetActiveRateAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateOnly>()))
                .ReturnsAsync((ActiveRateInfo?)null);

            var services = new ServiceCollection();
            services.AddSingleton(Material);
            services.AddSingleton(Inventory);
            services.AddSingleton(numbers.Object);
            services.AddSingleton(purchaseOrders.Object);
            services.AddSingleton(supplierNames.Object);
            services.AddSingleton(rates.Object);
            services.AddSingleton(Notify.Object);
            services.AddSingleton<Microsoft.Extensions.Logging.ILogger<InventoryLedgerService>>(NullLogger<InventoryLedgerService>.Instance);
            services.AddSingleton<IInventoryLedgerService, InventoryLedgerService>();
            services.AddSingleton<IStockReservationService, StockReservationService>();
            services.AddSingleton<IAllocationRunListener, ProductionReadinessListener>();
            services.AddSingleton<IAllocationReceiptListener, ProductionReadinessListener>();
            services.AddSingleton<IAllocationEngine, AllocationEngine>();
            services.AddSingleton<IProductionOrderRepository, ProductionOrderRepository>();
            services.AddSingleton<ProductionOrderService>();
            services.AddSingleton<IProductionOrderService>(sp => sp.GetRequiredService<ProductionOrderService>());
            services.AddSingleton<ISupplyRequirementEngine, SupplyRequirementEngine>();
            services.AddSingleton<IProductionMaterialIssueService, ProductionMaterialIssueService>();
            services.AddSingleton<IQualityInspectionService, QualityInspectionService>();
            services.AddSingleton<IFinishedGoodsReceiptService, FinishedGoodsReceiptService>();
            services.AddSingleton<IProductionLedgerService, ProductionLedgerService>();
            var provider = services.BuildServiceProvider();

            Boms      = new BomRepository(Material, Inventory, numbers.Object);
            Orders    = provider.GetRequiredService<IProductionOrderService>();
            Issues    = provider.GetRequiredService<IProductionMaterialIssueService>();
            Qi        = provider.GetRequiredService<IQualityInspectionService>();
            Fgr       = provider.GetRequiredService<IFinishedGoodsReceiptService>();
            Ledger    = provider.GetRequiredService<IProductionLedgerService>();
            Engine    = provider.GetRequiredService<IAllocationEngine>();
        }

        public (Guid Product, Guid Variant) Product(string name, bool manufactured, decimal purchasePrice = 10m, string uom = "PCS")
        {
            var product = new Product
            {
                Uuid = Guid.NewGuid(), Sku = $"SKU-{Guid.NewGuid():N}"[..12], Name = name, UomCode = uom,
                ProductType  = manufactured ? SMS.Shared.Common.ProductType.FinishedGood : SMS.Shared.Common.ProductType.RawMaterial,
                SupplyMethod = manufactured ? SupplyMethod.Manufacture : SupplyMethod.Purchase,
                IsActive = true, CreatedBy = 1
            };
            var variant = new ProductVariant
            {
                Uuid = Guid.NewGuid(), Sku = $"{product.Sku}-1", VariantName = "Default", IsDefault = true, IsActive = true,
                PurchasePrice = purchasePrice, IsAvailableForProduction = true, CreatedBy = 1
            };
            product.Variants.Add(variant);
            Inventory.Products.Add(product);
            Inventory.SaveChanges();
            return (product.Uuid, variant.Uuid);
        }

        public void Stock(Guid variantUuid, Guid warehouseUuid, decimal qty)
        {
            var variantId   = Inventory.ProductVariants.First(v => v.Uuid == variantUuid).Id;
            var warehouseId = Inventory.Warehouses.First(w => w.Uuid == warehouseUuid).Id;
            Inventory.InventoryItems.Add(new InventoryItem { Uuid = Guid.NewGuid(), VariantId = variantId, WarehouseId = warehouseId, QtyOnHand = qty, UnitCost = 3m });
            Inventory.SaveChanges();
        }

        public async Task<Guid> ActiveBomAsync(Guid product, decimal baseQty, params (Guid Variant, decimal Qty, decimal Scrap, bool Critical)[] lines)
        {
            var uuid = await Boms.CreateAsync(new CreateBomRequest
            {
                ProductUuid = product, BaseQuantity = baseQty,
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

        /// <summary>Creates, plans, starts and reports full output for a simple one-material order. Returns its uuid.</summary>
        public async Task<Guid> ReadyToInspectOrderAsync(Guid product, Guid material, decimal qty)
        {
            var uuid = await Orders.CreateAsync(new CreateProductionOrderRequest
            {
                ProductUuid = product, PlannedQuantity = qty, WarehouseUuid = Plant, RequiredDate = Today.AddDays(3), Plan = true
            }, Author);
            await Orders.StartAsync(uuid, Author);
            await Orders.ReportOutputAsync(uuid, new ReportOutputRequest { Quantity = qty }, Author);
            await Orders.CompleteAsync(uuid, Author);
            return uuid;
        }
    }

    private static CreateQualityInspectionLineRequest Check(string name, string result, decimal qty) =>
        new() { CheckName = name, Result = result, QuantityChecked = qty };

    // ── Quality inspection (T-QF01..05) ──────────────────────────────────────

    [Fact]
    public async Task An_all_pass_inspection_accepts_everything_and_the_order_stays_in_quality_inspection()
    {
        var h = new Harness();
        var (shirt, _)  = h.Product("Printed T-Shirt", manufactured: true);
        var (_, plainV) = h.Product("Plain T-Shirt", manufactured: false);
        await h.ActiveBomAsync(shirt, 1m, (plainV, 1m, 0m, true));
        h.Stock(plainV, h.Plant, 100);
        var uuid = await h.ReadyToInspectOrderAsync(shirt, plainV, 20);

        var qiUuid = await h.Qi.CreateAsync(uuid, new CreateQualityInspectionRequest
        {
            Lines = [Check("Visual", "PASS", 20)]
        }, Operator);

        var qi = await h.Qi.GetAsync(qiUuid);
        qi!.OverallResult.Should().Be(QiOverallResult.Passed);
        qi.AcceptedQuantity.Should().Be(20);
        qi.OutstandingForFgr.Should().Be(20);

        var order = await h.Orders.GetByUuidAsync(uuid);
        order!.Status.Should().Be(ProductionOrderStatus.QualityInspection, "nothing has been received into stock yet");
    }

    [Fact]
    public async Task Checked_quantity_must_equal_produced_and_the_inspector_cannot_be_who_raised_the_order()
    {
        var h = new Harness();
        var (shirt, _)  = h.Product("Printed T-Shirt", manufactured: true);
        var (_, plainV) = h.Product("Plain T-Shirt", manufactured: false);
        await h.ActiveBomAsync(shirt, 1m, (plainV, 1m, 0m, true));
        h.Stock(plainV, h.Plant, 100);
        var uuid = await h.ReadyToInspectOrderAsync(shirt, plainV, 20);

        var wrongQty = () => h.Qi.CreateAsync(uuid, new CreateQualityInspectionRequest { Lines = [Check("Visual", "PASS", 15)] }, Operator);
        await wrongQty.Should().ThrowAsync<BadRequestException>().WithMessage("*must equal what this order produced*");

        var sameAsCreator = () => h.Qi.CreateAsync(uuid, new CreateQualityInspectionRequest { Lines = [Check("Visual", "PASS", 20)] }, Author);
        await sameAsCreator.Should().ThrowAsync<BadRequestException>().WithMessage("*cannot inspect its output*");
    }

    [Fact]
    public async Task A_mixed_result_is_partially_passed_and_rejected_quantity_lands_on_the_order()
    {
        var h = new Harness();
        var (shirt, _)  = h.Product("Printed T-Shirt", manufactured: true);
        var (_, plainV) = h.Product("Plain T-Shirt", manufactured: false);
        await h.ActiveBomAsync(shirt, 1m, (plainV, 1m, 0m, true));
        h.Stock(plainV, h.Plant, 100);
        var uuid = await h.ReadyToInspectOrderAsync(shirt, plainV, 20);

        await h.Qi.CreateAsync(uuid, new CreateQualityInspectionRequest
        {
            Lines = [Check("Visual", "PASS", 16), Check("Stitching", "FAIL", 4)]
        }, Operator);

        var qi = await h.Qi.GetForOrderAsync(uuid);
        qi!.OverallResult.Should().Be(QiOverallResult.PartiallyPassed);
        qi.AcceptedQuantity.Should().Be(16);
        qi.RejectedQuantity.Should().Be(4);

        (await h.Orders.GetByUuidAsync(uuid))!.RejectedQuantity.Should().Be(4);

        var again = () => h.Qi.CreateAsync(uuid, new CreateQualityInspectionRequest { Lines = [Check("Recheck", "PASS", 20)] }, Operator);
        await again.Should().ThrowAsync<BadRequestException>().WithMessage("*already been inspected*");
    }

    // ── Finished goods receipt (T-QF06..08) ──────────────────────────────────

    [Fact]
    public async Task Confirming_a_full_fgr_credits_stock_and_completes_the_order()
    {
        var h = new Harness();
        var (shirt, shirtV) = h.Product("Printed T-Shirt", manufactured: true);
        var (_, plainV)     = h.Product("Plain T-Shirt", manufactured: false);
        await h.ActiveBomAsync(shirt, 1m, (plainV, 1m, 0m, true));
        h.Stock(plainV, h.Plant, 100);
        var uuid = await h.ReadyToInspectOrderAsync(shirt, plainV, 20);
        await h.Qi.CreateAsync(uuid, new CreateQualityInspectionRequest { Lines = [Check("Visual", "PASS", 20)] }, Operator);

        var fgrUuid = await h.Fgr.CreateAsync(uuid, new CreateFinishedGoodsReceiptRequest { Quantity = 20, Confirm = true }, Operator);

        var fgr = await h.Fgr.GetAsync(fgrUuid);
        fgr!.Status.Should().Be(FgrStatus.Confirmed);

        h.Item(shirtV, h.Plant).QtyOnHand.Should().Be(20);
        var order = await h.Orders.GetByUuidAsync(uuid);
        order!.Status.Should().Be(ProductionOrderStatus.Completed);
        order.AcceptedQuantity.Should().Be(20);
    }

    [Fact]
    public async Task A_partial_fgr_leaves_the_order_in_quality_inspection_until_the_rest_arrives()
    {
        var h = new Harness();
        var (shirt, shirtV) = h.Product("Printed T-Shirt", manufactured: true);
        var (_, plainV)     = h.Product("Plain T-Shirt", manufactured: false);
        await h.ActiveBomAsync(shirt, 1m, (plainV, 1m, 0m, true));
        h.Stock(plainV, h.Plant, 100);
        var uuid = await h.ReadyToInspectOrderAsync(shirt, plainV, 20);
        await h.Qi.CreateAsync(uuid, new CreateQualityInspectionRequest { Lines = [Check("Visual", "PASS", 20)] }, Operator);

        var tooMuch = () => h.Fgr.CreateAsync(uuid, new CreateFinishedGoodsReceiptRequest { Quantity = 25, Confirm = true }, Operator);
        await tooMuch.Should().ThrowAsync<BadRequestException>().WithMessage("*Only*20*left to receive*");

        await h.Fgr.CreateAsync(uuid, new CreateFinishedGoodsReceiptRequest { Quantity = 12, Confirm = true }, Operator);
        (await h.Orders.GetByUuidAsync(uuid))!.Status.Should().Be(ProductionOrderStatus.QualityInspection);
        h.Item(shirtV, h.Plant).QtyOnHand.Should().Be(12);

        await h.Fgr.CreateAsync(uuid, new CreateFinishedGoodsReceiptRequest { Quantity = 8, Confirm = true }, Operator);
        (await h.Orders.GetByUuidAsync(uuid))!.Status.Should().Be(ProductionOrderStatus.Completed);
        h.Item(shirtV, h.Plant).QtyOnHand.Should().Be(20);
    }

    [Fact]
    public async Task An_fgr_of_a_chained_input_covers_the_parent_orders_own_shortage()
    {
        // Steel Bolt is manufactured, then consumed as the Bolt Kit's own BOM input — chained
        // manufacturing (A30-P3-09), proven end to end through a real FGR this time.
        var h = new Harness();
        var (kit, _)      = h.Product("Bolt Kit", manufactured: true);
        var (bolt, boltV) = h.Product("Steel Bolt", manufactured: true);
        var (_, rodV)     = h.Product("Steel Rod", manufactured: false);
        await h.ActiveBomAsync(kit, 1m, (boltV, 4m, 0m, true));
        await h.ActiveBomAsync(bolt, 1m, (rodV, 1m, 0m, true));
        h.Stock(rodV, h.Plant, 1000);

        var kitUuid = await h.Orders.CreateAsync(new CreateProductionOrderRequest
        {
            ProductUuid = kit, PlannedQuantity = 10, WarehouseUuid = h.Plant, RequiredDate = Today.AddDays(5), Plan = true
        }, Author); // needs 40 bolts, has none — raises and plans a child order for the bolt

        var kitDetail  = await h.Orders.GetByUuidAsync(kitUuid);
        var childUuid  = kitDetail!.ChildOrders.Single().UUID;
        var child      = await h.Orders.GetByUuidAsync(childUuid);
        child!.ProductUuid.Should().Be(bolt);
        // T-CM05 — a chained order shares its parent's trace, not a random one of its own, so the
        // whole PO→SR→PO→...→FGR walk shows on one timeline.
        child.TraceId.Should().Be(kitDetail.TraceId);

        // Walk the child through to a confirmed FGR.
        await h.Orders.StartAsync(childUuid, Author);
        await h.Orders.ReportOutputAsync(childUuid, new ReportOutputRequest { Quantity = 40 }, Author);
        await h.Orders.CompleteAsync(childUuid, Author);
        await h.Qi.CreateAsync(childUuid, new CreateQualityInspectionRequest { Lines = [Check("Visual", "PASS", 40)] }, Operator);
        await h.Fgr.CreateAsync(childUuid, new CreateFinishedGoodsReceiptRequest { Quantity = 40, Confirm = true }, Operator);

        h.Item(boltV, h.Plant).QtyOnHand.Should().Be(40);
        var kitMaterial = (await h.Orders.GetMaterialsAsync(kitUuid)).Single();
        kitMaterial.ReservedQuantity.Should().Be(40, "the FGR's own allocation run should have reserved the bolts for the kit");
        kitMaterial.ShortageQuantity.Should().Be(0);

        var kitAfter = await h.Orders.GetByUuidAsync(kitUuid);
        kitAfter!.Status.Should().Be(ProductionOrderStatus.Ready, "its one material is now fully covered");

        // A30-P5-01 wiring proof: the shortage that started the chain, the QI and FGR that finished
        // it, and the child order's own completion all actually reach the notification service.
        h.Notify.Verify(n => n.SupplyRequirementCreatedAsync(It.IsAny<SupplyRequirement>(), "Steel Bolt"), Times.Once);
        h.Notify.Verify(n => n.QualityInspectionCompletedAsync(It.Is<ProductionOrder>(p => p.UUID == childUuid), It.IsAny<QualityInspection>()), Times.Once);
        h.Notify.Verify(n => n.FinishedGoodsReceiptConfirmedAsync(It.Is<ProductionOrder>(p => p.UUID == childUuid), It.IsAny<FinishedGoodsReceipt>(), "Steel Bolt", "Plant"), Times.Once);
        h.Notify.Verify(n => n.ProductionOrderCompletedAsync(It.Is<ProductionOrder>(p => p.UUID == childUuid)), Times.Once);
        // The kit itself becoming Ready (its bolt requirement now fully covered by the FGR's own
        // allocation run) is the same "newly covered" transition the listener also notifies on.
        h.Notify.Verify(n => n.ProductionOrderReadyAsync(It.Is<ProductionOrder>(p => p.UUID == kitUuid)), Times.Once);
    }

    // ── Production ledger (T-PL01..08) ────────────────────────────────────────

    [Fact]
    public async Task The_ledger_shows_debits_for_material_and_scrap_and_a_credit_for_the_finished_good_with_a_correct_yield()
    {
        var h = new Harness();
        var (shirt, shirtV) = h.Product("Printed T-Shirt", manufactured: true);
        var (_, plainV)     = h.Product("Plain T-Shirt", manufactured: false);
        await h.ActiveBomAsync(shirt, 1m, (plainV, 1m, 0m, true));
        h.Stock(plainV, h.Plant, 100);

        var uuid = await h.Orders.CreateAsync(new CreateProductionOrderRequest
        {
            ProductUuid = shirt, PlannedQuantity = 20, WarehouseUuid = h.Plant, RequiredDate = Today.AddDays(3), Plan = true
        }, Author);
        await h.Orders.StartAsync(uuid, Author);

        // Issue the material for real, while the order is still in progress, so a MATERIAL_ISSUE
        // ledger entry exists to read back.
        var requirement = (await h.Orders.GetMaterialsAsync(uuid)).Single();
        await h.Issues.CreateAsync(uuid, new CreateProductionIssueRequest
        {
            IssueType = "STANDARD", Confirm = true,
            Lines = [new CreateProductionIssueLineRequest { RequirementUuid = requirement.UUID, Quantity = 20 }]
        }, Author);

        await h.Orders.ReportOutputAsync(uuid, new ReportOutputRequest { Quantity = 20 }, Author);
        await h.Orders.CompleteAsync(uuid, Author);

        await h.Qi.CreateAsync(uuid, new CreateQualityInspectionRequest
        {
            Lines = [Check("Visual", "PASS", 16), Check("Stitching", "FAIL", 4)]
        }, Operator);
        await h.Fgr.CreateAsync(uuid, new CreateFinishedGoodsReceiptRequest { Quantity = 16, Confirm = true }, Operator);

        var ledger = await h.Ledger.GetForOrderAsync(uuid);
        ledger!.Entries.Should().HaveCount(3, "one material debit, one scrap debit (synthesised from the inspection, not a stock movement), one finished-good credit");

        var materialDebit = ledger.Entries.Single(e => e.MovementType == InventoryTransactionType.ProductionIssue);
        materialDebit.EntryType.Should().Be("DEBIT");
        materialDebit.VariantUuid.Should().Be(plainV);
        materialDebit.Quantity.Should().Be(20);

        var scrapDebit = ledger.Entries.Single(e => e.MovementType == InventoryTransactionType.ProductionScrap);
        scrapDebit.EntryType.Should().Be("DEBIT");
        scrapDebit.VariantUuid.Should().Be(shirtV);
        scrapDebit.Quantity.Should().Be(4);
        scrapDebit.SourceDocumentType.Should().Be("QUALITY_INSPECTION");

        var fgCredit = ledger.Entries.Single(e => e.MovementType == InventoryTransactionType.FinishedGoodsReceipt);
        fgCredit.EntryType.Should().Be("CREDIT");
        fgCredit.VariantUuid.Should().Be(shirtV);
        fgCredit.Quantity.Should().Be(16);

        ledger.Summary.MaterialsConsumedCount.Should().Be(1);
        ledger.Summary.FinishedGoodsQuantity.Should().Be(16);

        // The scrap side is read from the QualityInspection itself (see summary), not a stock movement.
        var summary = await h.Ledger.GetSummaryAsync(new ProductionLedgerListFilter { ProductionOrderUuid = uuid });
        summary.ScrapQuantity.Should().Be(4);
        summary.YieldPercent.Should().Be(80m, "16 finished of 20 total (16 good + 4 scrap)");
    }
}

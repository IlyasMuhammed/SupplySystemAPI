using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Modules.Reports.Models;
using SMS.Modules.Reports.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Reports.Tests;

/// <summary>
/// A30-P5-02..06 — the five manufacturing reports that fill a genuine gap (see
/// <c>ManufacturingReportModels.cs</c>'s own note on why the other twenty-three of FSD §31's 28 are
/// not duplicated here). Seeds Material's and Inventory's InMemory contexts directly, the same shape
/// <c>QualityAndFgrTests</c> uses in the Material test project.
/// </summary>
public class ManufacturingReportServiceTests
{
    private static readonly DateTime Today = DateTime.UtcNow.Date;

    private sealed class Harness
    {
        public MaterialDbContext  Material { get; }
        public InventoryDbContext Inventory { get; }
        public ManufacturingReportService Service { get; }
        public Guid Plant { get; }

        public Harness()
        {
            var tenant = new StaticTenantContext();
            Material  = new MaterialDbContext(new DbContextOptionsBuilder<MaterialDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);
            Inventory = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);
            Service = new ManufacturingReportService(Material, Inventory);

            var plant = new SMS.Modules.Inventory.Domain.Warehouse { Uuid = Guid.NewGuid(), Code = "PLANT", Name = "Plant", IsActive = true, CreatedBy = 1 };
            Inventory.Warehouses.Add(plant);
            Inventory.SaveChanges();
            Plant = plant.Uuid;
        }

        public Guid Product(string name)
        {
            var product = new Product { Uuid = Guid.NewGuid(), Sku = $"SKU-{Guid.NewGuid():N}"[..12], Name = name, UomCode = "PCS", IsActive = true, CreatedBy = 1 };
            Inventory.Products.Add(product);
            Inventory.SaveChanges();
            return product.Uuid;
        }

        public ProductionOrder Order(Guid productUuid, string number, string status, decimal planned, decimal produced, decimal accepted,
            decimal rejected, DateTime createdAt, DateTime requiredDate, DateTime? actualEndDate = null, int? parentId = null)
        {
            var po = new ProductionOrder
            {
                UUID = Guid.NewGuid(), ProductionNumber = number, ProductUuid = productUuid, ProductVariantUuid = Guid.NewGuid(),
                WarehouseUuid = Plant, PlannedQuantity = planned, ProducedQuantity = produced, AcceptedQuantity = accepted,
                RejectedQuantity = rejected, Status = status, CreatedAt = createdAt, UpdatedAt = createdAt, RequiredDate = requiredDate,
                ActualEndDate = actualEndDate, ParentProductionOrderId = parentId, CreatedBy = 1
            };
            Material.ProductionOrders.Add(po);
            Material.SaveChanges();
            return po;
        }

        public void Qi(ProductionOrder po, decimal inspected, decimal accepted, decimal rejected, decimal hold, decimal rework, DateTime at)
        {
            Material.QualityInspections.Add(new QualityInspection
            {
                UUID = Guid.NewGuid(), InspectionNumber = $"QI-{Guid.NewGuid():N}"[..10], ProductionOrderId = po.Id,
                InspectedQuantity = inspected, AcceptedQuantity = accepted, RejectedQuantity = rejected, HoldQuantity = hold,
                ReworkQuantity = rework, OverallResult = QiOverallResult.Passed, InspectedBy = 9, InspectedAt = at, CreatedAt = at
            });
            Material.SaveChanges();
        }

        public void Issue(ProductionOrder po, string status, decimal qty, DateTime createdAt)
        {
            var issue = new ProductionMaterialIssue
            {
                UUID = Guid.NewGuid(), IssueNumber = $"PMI-{Guid.NewGuid():N}"[..10], ProductionOrderId = po.Id,
                WarehouseUuid = Plant, IssueType = ProductionIssueType.Standard, Status = status, CreatedBy = 1, CreatedAt = createdAt,
                Lines = [new ProductionMaterialIssueLine { UUID = Guid.NewGuid(), MaterialVariantUuid = Guid.NewGuid(), Quantity = qty, Uom = "PCS" }]
            };
            Material.ProductionMaterialIssues.Add(issue);
            Material.SaveChanges();
        }

        public void Fgr(ProductionOrder po, string status, decimal qty, DateTime receivedAt)
        {
            Material.FinishedGoodsReceipts.Add(new FinishedGoodsReceipt
            {
                UUID = Guid.NewGuid(), FgrNumber = $"FGR-{Guid.NewGuid():N}"[..10], ProductionOrderId = po.Id, QualityInspectionId = 0,
                WarehouseUuid = Plant, TotalQuantity = qty, Status = status, ReceivedBy = 9, ReceivedAt = receivedAt, CreatedAt = receivedAt
            });
            Material.SaveChanges();
        }
    }

    // ── R4/R5/R16 — Production Efficiency & WIP Summary ──────────────────────

    [Fact]
    public async Task Efficiency_totals_yield_on_time_and_cycle_days_across_orders()
    {
        var h = new Harness();
        var widget = h.Product("Widget");
        h.Order(widget, "PROD-1", ProductionOrderStatus.Completed, planned: 100, produced: 100, accepted: 90, rejected: 10,
            createdAt: Today.AddDays(-10), requiredDate: Today.AddDays(-5), actualEndDate: Today.AddDays(-6)); // on time (ends before required)
        h.Order(widget, "PROD-2", ProductionOrderStatus.Completed, planned: 50, produced: 50, accepted: 40, rejected: 10,
            createdAt: Today.AddDays(-8), requiredDate: Today.AddDays(-7), actualEndDate: Today.AddDays(-2)); // late
        h.Order(widget, "PROD-3", ProductionOrderStatus.InProgress, planned: 20, produced: 0, accepted: 0, rejected: 0,
            createdAt: Today, requiredDate: Today.AddDays(5));

        var report = await h.Service.GetProductionEfficiencyAsync(new ManufacturingReportFilter());

        report.TotalOrders.Should().Be(3);
        report.CountByStatus[ProductionOrderStatus.Completed].Should().Be(2);
        report.CountByStatus[ProductionOrderStatus.InProgress].Should().Be(1);
        report.TotalPlannedQuantity.Should().Be(170);
        report.TotalProducedQuantity.Should().Be(150);
        report.TotalAcceptedQuantity.Should().Be(130);
        report.OverallYieldPercent.Should().Be(Math.Round(130m / 150m * 100m, 2));
        // One of the two completed orders finished on or before its required date.
        report.OnTimeCompletionPercent.Should().Be(50m);
        report.AverageCycleDays.Should().Be(Math.Round((4m + 6m) / 2m, 2));
    }

    [Fact]
    public async Task Date_filter_excludes_orders_created_outside_the_range()
    {
        var h = new Harness();
        var widget = h.Product("Widget");
        h.Order(widget, "PROD-OLD", ProductionOrderStatus.Completed, 10, 10, 10, 0, Today.AddDays(-30), Today.AddDays(-25), Today.AddDays(-26));
        h.Order(widget, "PROD-NEW", ProductionOrderStatus.Draft, 5, 0, 0, 0, Today, Today.AddDays(3));

        var report = await h.Service.GetProductionEfficiencyAsync(new ManufacturingReportFilter { DateFrom = Today.AddDays(-1) });

        report.TotalOrders.Should().Be(1);
        report.TotalPlannedQuantity.Should().Be(5);
    }

    // ── R6/R7 — Quality & Scrap Summary ───────────────────────────────────────

    [Fact]
    public async Task Quality_scrap_aggregates_across_inspections_and_breaks_down_by_product()
    {
        var h = new Harness();
        var widget = h.Product("Widget");
        var gadget = h.Product("Gadget");
        var po1 = h.Order(widget, "PROD-1", ProductionOrderStatus.QualityInspection, 100, 100, 0, 0, Today, Today);
        var po2 = h.Order(gadget, "PROD-2", ProductionOrderStatus.QualityInspection, 50, 50, 0, 0, Today, Today);
        h.Qi(po1, inspected: 100, accepted: 90, rejected: 10, hold: 0, rework: 0, at: Today);
        h.Qi(po2, inspected: 50, accepted: 30, rejected: 20, hold: 0, rework: 0, at: Today);

        var report = await h.Service.GetQualityScrapAsync(new ManufacturingReportFilter());

        report.TotalInspections.Should().Be(2);
        report.TotalInspectedQty.Should().Be(150);
        report.TotalAcceptedQty.Should().Be(120);
        report.TotalRejectedQty.Should().Be(30);
        report.RejectionRatePercent.Should().Be(Math.Round(30m / 150m * 100m, 2));
        report.ByProduct.Should().HaveCount(2);
        // Gadget has the higher rejection rate (40%) and comes first.
        report.ByProduct[0].ProductName.Should().Be("Gadget");
        report.ByProduct[0].RejectionRatePercent.Should().Be(40m);
    }

    // ── R14/R15 — Manufacturing document registers ────────────────────────────

    [Fact]
    public async Task Material_issue_register_lists_confirmed_and_draft_issues_with_line_totals()
    {
        var h = new Harness();
        var widget = h.Product("Widget");
        var po = h.Order(widget, "PROD-1", ProductionOrderStatus.InProgress, 100, 0, 0, 0, Today, Today);
        h.Issue(po, ProductionIssueStatus.Confirmed, qty: 25, createdAt: Today);
        h.Issue(po, ProductionIssueStatus.Draft, qty: 10, createdAt: Today);

        var page = await h.Service.GetMaterialIssueRegisterAsync(new ManufacturingRegisterFilter());

        page.TotalRecords.Should().Be(2);
        page.Data.Should().Contain(i => i.Status == ProductionIssueStatus.Confirmed && i.TotalQuantity == 25 && i.OutputProductName == "Widget");
        page.Data.Should().Contain(i => i.Status == ProductionIssueStatus.Draft && i.TotalQuantity == 10);
    }

    [Fact]
    public async Task Finished_goods_receipt_register_lists_receipts_with_product_and_warehouse_names()
    {
        var h = new Harness();
        var widget = h.Product("Widget");
        var po = h.Order(widget, "PROD-1", ProductionOrderStatus.Completed, 100, 100, 100, 0, Today, Today, Today);
        h.Fgr(po, FgrStatus.Confirmed, qty: 100, receivedAt: Today);

        var page = await h.Service.GetFinishedGoodsReceiptRegisterAsync(new ManufacturingRegisterFilter());

        page.TotalRecords.Should().Be(1);
        page.Data.Single().ProductName.Should().Be("Widget");
        page.Data.Single().WarehouseName.Should().Be("Plant");
        page.Data.Single().TotalQuantity.Should().Be(100);
    }

    // ── R24 — Production Ledger Reconciliation ────────────────────────────────

    [Fact]
    public async Task Reconciliation_flags_an_order_whose_accepted_quantity_does_not_match_its_fgrs()
    {
        var h = new Harness();
        var widget = h.Product("Widget");
        // Produced output, accepted quantity recorded, but no FGR and no issue ever posted — everything about this order is inconsistent.
        var bad = h.Order(widget, "PROD-BAD", ProductionOrderStatus.Completed, 100, 100, 100, 0, Today, Today, Today);

        // A clean order: issue posted, FGR posted matching AcceptedQuantity, QI recorded for its rejection.
        var good = h.Order(widget, "PROD-GOOD", ProductionOrderStatus.Completed, 50, 50, 45, 5, Today, Today, Today);
        h.Issue(good, ProductionIssueStatus.Confirmed, 50, Today);
        h.Qi(good, 50, 45, 5, 0, 0, Today);
        h.Fgr(good, FgrStatus.Confirmed, 45, Today);

        var report = await h.Service.GetLedgerReconciliationAsync(new ManufacturingReportFilter());

        report.TotalOrdersChecked.Should().Be(2);
        report.FlaggedCount.Should().Be(1);
        var flaggedItem = report.Flagged.Single();
        flaggedItem.ProductionNumber.Should().Be("PROD-BAD");
        flaggedItem.Reason.Should().Contain("does not match the sum of confirmed FGRs").And.Contain("no confirmed material issue");
    }

    // ── R26/R27/R28 — Chained manufacturing dependency tree ───────────────────

    [Fact]
    public async Task Chained_report_walks_up_to_the_root_and_back_down_through_every_descendant()
    {
        var h = new Harness();
        var kit  = h.Product("Bolt Kit");
        var bolt = h.Product("Steel Bolt");
        var rod  = h.Product("Steel Rod");

        var parent = h.Order(kit, "PROD-KIT", ProductionOrderStatus.MaterialPending, 10, 0, 0, 0, Today.AddDays(-3), Today.AddDays(5));
        var child  = h.Order(bolt, "PROD-BOLT", ProductionOrderStatus.Completed, 40, 40, 40, 0, Today.AddDays(-3), Today.AddDays(2), Today.AddDays(-1), parentId: parent.Id);
        var grandchild = h.Order(rod, "PROD-ROD", ProductionOrderStatus.Completed, 40, 40, 40, 0, Today.AddDays(-3), Today.AddDays(1), Today.AddDays(-2), parentId: child.Id);

        // Ask for the report starting from the grandchild — it should still walk up to the true root.
        var report = await h.Service.GetChainedManufacturingAsync(grandchild.UUID);

        report.Should().NotBeNull();
        report!.RootProductionNumber.Should().Be("PROD-KIT");
        report.TotalOrdersInChain.Should().Be(3);
        report.MaxDepth.Should().Be(2);
        report.Root.ProductionNumber.Should().Be("PROD-KIT");
        report.Root.Children.Single().ProductionNumber.Should().Be("PROD-BOLT");
        report.Root.Children.Single().Children.Single().ProductionNumber.Should().Be("PROD-ROD");
        // The parent (kit) is not yet complete, so no total cycle time can be computed.
        report.TotalCycleDays.Should().BeNull();
    }

    [Fact]
    public async Task Chained_report_on_an_order_with_no_family_is_a_tree_of_one()
    {
        var h = new Harness();
        var widget = h.Product("Widget");
        var lone = h.Order(widget, "PROD-LONE", ProductionOrderStatus.Draft, 5, 0, 0, 0, Today, Today.AddDays(1));

        var report = await h.Service.GetChainedManufacturingAsync(lone.UUID);

        report.Should().NotBeNull();
        report!.TotalOrdersInChain.Should().Be(1);
        report.MaxDepth.Should().Be(0);
        report.Root.Children.Should().BeEmpty();
    }

    [Fact]
    public async Task Chained_report_on_an_unknown_order_is_null()
    {
        var h = new Harness();
        (await h.Service.GetChainedManufacturingAsync(Guid.NewGuid())).Should().BeNull();
    }
}

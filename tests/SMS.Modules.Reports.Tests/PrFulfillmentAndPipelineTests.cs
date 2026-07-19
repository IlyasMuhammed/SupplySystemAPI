using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Finance.Data;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Modules.Reports.Data;
using SMS.Modules.Reports.Models;
using SMS.Modules.Reports.Repositories;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Warehouse.Domain;
using SMS.Shared.Common;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;
using Xunit;
using WarehouseDbContext = SMS.Modules.Warehouse.Data.WarehouseDbContext;

namespace SMS.Modules.Reports.Tests;

file static class Build
{
    internal static (ReportsRepository Repo, DemandDbContext Demand, WarehouseDbContext Warehouse,
                      FinanceDbContext Finance, MaterialDbContext Material, Mock<ITimelineService> Timeline)
        NewRepo(string? dbName = null)
    {
        var name = dbName ?? Guid.NewGuid().ToString();

        var demand    = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(name).Options);
        var warehouse = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseInMemoryDatabase(name).Options);
        var finance   = new FinanceDbContext(new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(name).Options);
        var material  = new MaterialDbContext(new DbContextOptionsBuilder<MaterialDbContext>().UseInMemoryDatabase(name).Options);
        var timeline  = new Mock<ITimelineService>();
        // Default: no timeline exists, forcing callers to exercise the FK fallback unless a test overrides this.
        timeline.Setup(t => t.GetTimelineDetailAsync(It.IsAny<Guid>())).ReturnsAsync((TimelineDetail?)null);

        var userQuery = new Mock<IUserQueryService>();
        userQuery.Setup(u => u.GetUsersAsync(It.IsAny<IReadOnlyList<int>>()))
            .ReturnsAsync((IReadOnlyList<int> ids) => (IReadOnlyList<UserIdentity>)ids.Select(id => new UserIdentity(id, $"User {id}")).ToList());

        var repo = new ReportsRepository(
            db:        new ReportsDbContext(new DbContextOptionsBuilder<ReportsDbContext>().UseInMemoryDatabase(name).Options),
            demand:    demand,
            warehouse: warehouse,
            inventory: new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(name).Options),
            finance:   finance,
            logistics: new LogisticsDbContext(new DbContextOptionsBuilder<LogisticsDbContext>().UseInMemoryDatabase(name).Options),
            suppliers: new SuppliersDbContext(new DbContextOptionsBuilder<SuppliersDbContext>().UseInMemoryDatabase(name).Options),
            material:  material,
            userQuery: userQuery.Object,
            timeline:  timeline.Object);

        return (repo, demand, warehouse, finance, material, timeline);
    }

    internal static (PurchaseRequisition Pr, List<PrLine> Lines) SeedPr(
        DemandDbContext demand, int lineCount, string status = "APPROVED", string? department = "IT",
        DateTime? approvedAt = null, string prNumber = "PR-2026-00001")
    {
        var pr = new PurchaseRequisition
        {
            UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), PrNumber = prNumber, PrTitle = "Test PR",
            Department = department, RequesterId = 1, RequestedDate = DateTime.UtcNow,
            Status = status, ApprovedBy = status == "CANCELLED" ? null : 1,
            ApprovedAt = status == "CANCELLED" ? null : (approvedAt ?? DateTime.UtcNow),
            IsActive = true, CreatedBy = 1, CreatedDate = DateTime.UtcNow
        };
        demand.PurchaseRequisitions.Add(pr);
        demand.SaveChanges();

        var lines = new List<PrLine>();
        for (var i = 0; i < lineCount; i++)
        {
            var line = new PrLine
            {
                UUID = Guid.NewGuid(), PurchaseRequisitionId = pr.Id, LineNo = i + 1,
                ItemDescription = $"Item {i + 1}", Quantity = 10m, EstimatedUnitPrice = 5m, LineTotal = 50m
            };
            demand.PrLines.Add(line);
            lines.Add(line);
        }
        demand.SaveChanges();
        return (pr, lines);
    }

    internal static PurchaseOrderLine SeedPoLineWithGrn(
        DemandDbContext demand, WarehouseDbContext warehouse, Guid prLineUuid, decimal orderedQty, decimal acceptedQty,
        string poStatus = "APPROVED")
    {
        var po = new PurchaseOrder
        {
            UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), PoNumber = $"PO-{Guid.NewGuid():N}"[..15],
            SupplierId = Guid.NewGuid(), SupplierName = "Test Supplier", Status = poStatus,
            TotalAmount = orderedQty * 5m, IsActive = true, CreatedBy = 1, CreatedDate = DateTime.UtcNow
        };
        demand.PurchaseOrders.Add(po);
        demand.SaveChanges();

        var poLine = new PurchaseOrderLine
        {
            UUID = Guid.NewGuid(), PurchaseOrderId = po.Id, LineNo = 1, SourcePrLineUuid = prLineUuid,
            ItemDescription = "Item", Quantity = orderedQty, UnitPrice = 5m, LineTotal = orderedQty * 5m
        };
        demand.PurchaseOrderLines.Add(poLine);
        demand.SaveChanges();

        if (acceptedQty > 0)
        {
            var grn = new Grn
            {
                UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), GrnNumber = $"GRN-{Guid.NewGuid():N}"[..15],
                PoUuid = po.UUID, PoNumber = po.PoNumber, SupplierId = po.SupplierId, ReceivedAt = DateTime.UtcNow,
                Status = "APPROVED"
            };
            warehouse.Grns.Add(grn);
            warehouse.SaveChanges();

            warehouse.GrnLines.Add(new GrnLine
            {
                UUID = Guid.NewGuid(), GrnId = grn.Id, PoLineUuid = poLine.UUID, LineNo = 1,
                ItemDescription = "Item", QtyOrdered = orderedQty, QtyReceived = acceptedQty, QtyAccepted = acceptedQty
            });
            warehouse.SaveChanges();
        }

        return poLine;
    }

    internal static TimelineEvent Event(string type, string interfaceCode = "PR") =>
        new(type, interfaceCode, Guid.NewGuid(), "REF-001", DateTime.UtcNow, 1, null);

    internal static TimelineDetail Timeline(Guid traceId, params TimelineEvent[] events) => new()
    {
        TraceId = traceId, ChainRootType = "PR", ChainRootRef = "PR-2026-00001",
        FirstEventAt = DateTime.UtcNow, LastEventAt = DateTime.UtcNow, Events = events.ToList()
    };
}

public class GetPrFulfillmentStatusAsync_Tests
{
    [Fact]
    public async Task All_Lines_Fully_Received_Shows_FULLY_FULFILLED()
    {
        var (repo, demand, warehouse, _, _, _) = Build.NewRepo();
        var (pr, lines) = Build.SeedPr(demand, lineCount: 2);
        foreach (var line in lines)
            Build.SeedPoLineWithGrn(demand, warehouse, line.UUID, orderedQty: 10m, acceptedQty: 10m);

        var result = await repo.GetPrFulfillmentStatusAsync(new PrFulfillmentFilter());

        var row = result.Single(r => r.PrUuid == pr.UUID);
        row.FulfillmentStatus.Should().Be("FULLY_FULFILLED");
        row.TotalLines.Should().Be(2);
        row.FulfilledLines.Should().Be(2);
        row.PendingLines.Should().Be(0);
        row.FulfillmentPercentage.Should().Be(100);
    }

    [Fact]
    public async Task Three_Lines_Two_Fully_Received_One_No_Po_Shows_Partial_66_Percent()
    {
        var (repo, demand, warehouse, _, _, _) = Build.NewRepo();
        var (pr, lines) = Build.SeedPr(demand, lineCount: 3);

        Build.SeedPoLineWithGrn(demand, warehouse, lines[0].UUID, orderedQty: 10m, acceptedQty: 10m);
        Build.SeedPoLineWithGrn(demand, warehouse, lines[1].UUID, orderedQty: 10m, acceptedQty: 10m);
        // lines[2] has no PO at all.

        var result = await repo.GetPrFulfillmentStatusAsync(new PrFulfillmentFilter());

        var row = result.Single(r => r.PrUuid == pr.UUID);
        row.FulfillmentStatus.Should().Be("PARTIALLY_FULFILLED");
        row.FulfilledLines.Should().Be(2);
        row.TotalLines.Should().Be(3);
        row.FulfillmentPercentage.Should().BeApproximately(66.67, 0.01);
    }

    [Fact]
    public async Task Zero_Po_References_Shows_UNFULFILLED()
    {
        var (repo, demand, _, _, _, _) = Build.NewRepo();
        var (pr, _) = Build.SeedPr(demand, lineCount: 2);

        var result = await repo.GetPrFulfillmentStatusAsync(new PrFulfillmentFilter());

        result.Single(r => r.PrUuid == pr.UUID).FulfillmentStatus.Should().Be("UNFULFILLED");
    }

    [Fact]
    public async Task Cancelled_Pr_Shows_CANCELLED_Status()
    {
        var (repo, demand, _, _, _, _) = Build.NewRepo();
        var (pr, _) = Build.SeedPr(demand, lineCount: 2, status: "CANCELLED");

        var result = await repo.GetPrFulfillmentStatusAsync(new PrFulfillmentFilter());

        var row = result.Single(r => r.PrUuid == pr.UUID);
        row.FulfillmentStatus.Should().Be("CANCELLED");
        row.FulfillmentPercentage.Should().Be(0);
    }

    [Fact]
    public async Task Filters_By_Department_DateRange_And_FulfillmentStatus_Simultaneously()
    {
        var (repo, demand, warehouse, _, _, _) = Build.NewRepo();
        var (itPr, itLines) = Build.SeedPr(demand, 1, department: "IT", approvedAt: new DateTime(2026, 6, 15), prNumber: "PR-IT");
        Build.SeedPoLineWithGrn(demand, warehouse, itLines[0].UUID, 10m, 10m);

        var (hrPr, _) = Build.SeedPr(demand, 1, department: "HR", approvedAt: new DateTime(2026, 6, 15), prNumber: "PR-HR");

        var (oldPr, oldLines) = Build.SeedPr(demand, 1, department: "IT", approvedAt: new DateTime(2026, 1, 1), prNumber: "PR-OLD");
        Build.SeedPoLineWithGrn(demand, warehouse, oldLines[0].UUID, 10m, 10m);

        var result = await repo.GetPrFulfillmentStatusAsync(new PrFulfillmentFilter
        {
            Department = "IT", DateFrom = new DateTime(2026, 6, 1), DateTo = new DateTime(2026, 6, 30),
            FulfillmentStatus = "FULLY_FULFILLED"
        });

        result.Should().ContainSingle();
        result[0].PrUuid.Should().Be(itPr.UUID);
    }
}

public class GetPrToPoPipelineAsync_Tests
{
    [Fact]
    public async Task Pipeline_Shows_GRN_Received_For_Pr_Whose_Chain_Reached_That_Point()
    {
        var (repo, demand, _, _, _, timeline) = Build.NewRepo();
        var (pr, _) = Build.SeedPr(demand, lineCount: 1);

        timeline.Setup(t => t.GetTimelineDetailAsync(pr.TraceId)).ReturnsAsync(
            Build.Timeline(pr.TraceId,
                Build.Event("PR_CREATED"), Build.Event("PR_APPROVED"),
                Build.Event("QUOTATION_CREATED", "QUOTATION"), Build.Event("QUOTATION_SENT", "QUOTATION"),
                Build.Event("QUOTATION_AWARDED", "QUOTATION"), Build.Event("PO_CREATED", "PO"),
                Build.Event("GRN_CREATED", "GRN")));

        var report = await repo.GetPrToPoPipelineAsync(new PrPipelineFilter());

        report.Items.Single(i => i.PrUuid == pr.UUID).FurthestStage.Should().Be("GRN Received");
    }

    [Fact]
    public async Task Uses_TraceId_Timeline_When_Available()
    {
        var (repo, demand, _, _, _, timeline) = Build.NewRepo();
        var (pr, _) = Build.SeedPr(demand, lineCount: 1);

        timeline.Setup(t => t.GetTimelineDetailAsync(pr.TraceId)).ReturnsAsync(
            Build.Timeline(pr.TraceId, Build.Event("PR_CREATED"), Build.Event("PO_CREATED", "PO")));

        var report = await repo.GetPrToPoPipelineAsync(new PrPipelineFilter());

        var row = report.Items.Single(i => i.PrUuid == pr.UUID);
        row.ResolvedViaTraceId.Should().BeTrue();
        row.FurthestStage.Should().Be("PO Created");
    }

    [Fact]
    public async Task Falls_Back_To_Fk_Traversal_When_No_Timeline_Exists()
    {
        var (repo, demand, warehouse, _, _, timeline) = Build.NewRepo();
        var (pr, lines) = Build.SeedPr(demand, lineCount: 1);
        // timeline mock defaults to null (see Build.NewRepo) -> forces the FK fallback path.
        Build.SeedPoLineWithGrn(demand, warehouse, lines[0].UUID, orderedQty: 10m, acceptedQty: 10m, poStatus: "SENT");

        var report = await repo.GetPrToPoPipelineAsync(new PrPipelineFilter());

        var row = report.Items.Single(i => i.PrUuid == pr.UUID);
        row.ResolvedViaTraceId.Should().BeFalse();
        row.FurthestStage.Should().Be("GRN Received");
    }

    [Fact]
    public async Task Fallback_With_No_Po_At_All_Resolves_To_Awaiting_Quotation()
    {
        var (repo, demand, _, _, _, _) = Build.NewRepo();
        var (pr, _) = Build.SeedPr(demand, lineCount: 1);

        var report = await repo.GetPrToPoPipelineAsync(new PrPipelineFilter());

        report.Items.Single(i => i.PrUuid == pr.UUID).FurthestStage.Should().Be("Awaiting Quotation");
    }

    [Fact]
    public async Task Summary_Counts_Reconcile_With_Detailed_Rows()
    {
        var (repo, demand, warehouse, _, _, timeline) = Build.NewRepo();

        var (fullyPr, fullyLines) = Build.SeedPr(demand, 1, prNumber: "PR-FULL");
        Build.SeedPoLineWithGrn(demand, warehouse, fullyLines[0].UUID, 10m, 10m);
        timeline.Setup(t => t.GetTimelineDetailAsync(fullyPr.TraceId)).ReturnsAsync(
            Build.Timeline(fullyPr.TraceId, Build.Event("PR_APPROVED"), Build.Event("GRN_CREATED", "GRN")));

        var (partialPr, partialLines) = Build.SeedPr(demand, 2, prNumber: "PR-PARTIAL");
        Build.SeedPoLineWithGrn(demand, warehouse, partialLines[0].UUID, 10m, 10m);
        timeline.Setup(t => t.GetTimelineDetailAsync(partialPr.TraceId)).ReturnsAsync(
            Build.Timeline(partialPr.TraceId, Build.Event("PR_APPROVED"), Build.Event("PO_CREATED", "PO")));

        var (unfulfilledPr, _) = Build.SeedPr(demand, 1, prNumber: "PR-UNFULFILLED");
        timeline.Setup(t => t.GetTimelineDetailAsync(unfulfilledPr.TraceId)).ReturnsAsync(
            Build.Timeline(unfulfilledPr.TraceId, Build.Event("PR_APPROVED")));

        var (cancelledPr, _) = Build.SeedPr(demand, 1, status: "CANCELLED", prNumber: "PR-CANCELLED");

        var report = await repo.GetPrToPoPipelineAsync(new PrPipelineFilter());

        report.TotalPrs.Should().Be(report.Items.Count);
        report.FullyFulfilledCount.Should().Be(report.Items.Count(i => i.FulfillmentStatus == "FULLY_FULFILLED"));
        report.PartiallyFulfilledCount.Should().Be(report.Items.Count(i => i.FulfillmentStatus == "PARTIALLY_FULFILLED"));
        report.UnfulfilledCount.Should().Be(report.Items.Count(i => i.FulfillmentStatus == "UNFULFILLED"));
        report.CancelledCount.Should().Be(report.Items.Count(i => i.FulfillmentStatus == "CANCELLED"));

        report.FullyFulfilledCount.Should().Be(1);
        report.PartiallyFulfilledCount.Should().Be(1);
        report.UnfulfilledCount.Should().Be(1);
        report.CancelledCount.Should().Be(1);
        report.TotalPrs.Should().Be(4);
    }
}

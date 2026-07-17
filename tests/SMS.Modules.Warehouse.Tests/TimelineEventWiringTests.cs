using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Warehouse.Data;
using SMS.Modules.Warehouse.Domain;
using SMS.Modules.Warehouse.Models;
using SMS.Modules.Warehouse.Repositories;
using SMS.Modules.Warehouse.Services;
using SMS.Shared.Common;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;
using Xunit;

namespace SMS.Modules.Warehouse.Tests;

// ── Test builder ──────────────────────────────────────────────────────────────

file static class WiringBuild
{
    internal static (GrnRepository repo, WarehouseDbContext wh, DemandDbContext demand) NewGrnRepo(
        Action<DemandDbContext>? seedDemand = null)
    {
        var dbName = Guid.NewGuid().ToString();
        var demand = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(dbName).Options);
        seedDemand?.Invoke(demand);
        demand.SaveChanges();

        var wh = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseInMemoryDatabase(dbName).Options);
        return (new GrnRepository(wh, demand), wh, demand);
    }

    internal static PurchaseOrder SentPo() => new()
    {
        UUID = Guid.NewGuid(), PoNumber = "PO-2026-00001", Title = "Test PO",
        SupplierId = Guid.NewGuid(), SupplierName = "Test Supplier", Status = "SENT",
        IsActive = true, CreatedBy = 1, CreatedDate = DateTime.UtcNow,
        Lines =
        [
            new PurchaseOrderLine
            {
                UUID = Guid.NewGuid(), LineNo = 1, ItemDescription = "Laptop", UnitOfMeasure = "PC",
                Quantity = 10m, UnitPrice = 100m, LineTotal = 1000m, QtyReceived = 0
            }
        ]
    };

    internal static (Mock<IBackgroundJobClient> Mock, List<Job> Captured) MockJobs()
    {
        var captured = new List<Job>();
        var mock = new Mock<IBackgroundJobClient>();
        mock.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()))
            .Callback<Job, IState>((job, _) => captured.Add(job))
            .Returns("fake-job-id");
        return (mock, captured);
    }

    internal static TimelineEvent CapturedEvent(List<Job> captured) =>
        (TimelineEvent)captured.Single(j => j.Method.Name == "AppendAsync").Args[1];
}

// ── GRN events ───────────────────────────────────────────────────────────────

public class GrnService_TimelineWiring_Tests
{
    [Fact]
    public async Task ApproveAsync_Enqueues_GRN_APPROVED_With_Accepted_And_Rejected_Qty_In_Notes()
    {
        var po = WiringBuild.SentPo();
        var (grnRepo, wh, _) = WiringBuild.NewGrnRepo(db => db.PurchaseOrders.Add(po));

        var grnUuid = await grnRepo.CreateAsync(new CreateGrnRequest
        {
            PoUuid        = po.UUID,
            WarehouseUuid = Guid.NewGuid(),
            ReceivedAt    = DateTime.UtcNow,
            Lines =
            [
                new GrnLineReceiveInput
                {
                    PoLineUuid = po.Lines.First().UUID, QtyReceived = 10m, QtyAccepted = 8m, QtyRejected = 2m
                }
            ]
        }, createdBy: 1);

        var workflow = new Mock<IWorkflowActionService>();
        workflow.Setup(w => w.ApproveByDocumentAsync("GRN", grnUuid, 5, null)).ReturnsAsync(Guid.NewGuid());
        var docStatus = new Mock<IDocumentStatusService>();
        var (jobsMock, captured) = WiringBuild.MockJobs();

        var svc = new GrnService(grnRepo, workflow.Object, docStatus.Object, jobsMock.Object);

        await svc.ApproveAsync(grnUuid, 5);

        var evt = WiringBuild.CapturedEvent(captured);
        evt.EventType.Should().Be("GRN_APPROVED");
        evt.Notes.Should().Contain("Accepted: 8").And.Contain("Rejected: 2");
    }

    [Fact]
    public async Task CreateAsync_Enqueues_GRN_CREATED()
    {
        var po = WiringBuild.SentPo();
        var (grnRepo, _, _) = WiringBuild.NewGrnRepo(db => db.PurchaseOrders.Add(po));

        var workflow  = new Mock<IWorkflowActionService>();
        var docStatus = new Mock<IDocumentStatusService>();
        var (jobsMock, captured) = WiringBuild.MockJobs();
        var svc = new GrnService(grnRepo, workflow.Object, docStatus.Object, jobsMock.Object);

        var grnUuid = await svc.CreateAsync(new CreateGrnRequest
        {
            PoUuid = po.UUID, WarehouseUuid = Guid.NewGuid(), ReceivedAt = DateTime.UtcNow
        }, createdBy: 1);

        var evt = WiringBuild.CapturedEvent(captured);
        evt.EventType.Should().Be("GRN_CREATED");
        evt.DocumentId.Should().Be(grnUuid);
    }
}

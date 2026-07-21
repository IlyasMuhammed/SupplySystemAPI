using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Repositories;
using SMS.Modules.Demand.Services;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;
using Xunit;

namespace SMS.Modules.Demand.Tests;

// ── Test builder ──────────────────────────────────────────────────────────────

file static class WiringBuild
{
    internal static DemandDbContext NewDb() =>
        new(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    // Captures every Job handed to IBackgroundJobClient.Create — this is the real interface
    // member the Enqueue<T>() extension method delegates to, so mocking Create is how we
    // observe what Enqueue<T>() was actually called with.
    internal static (Mock<IBackgroundJobClient> Mock, List<Job> Captured) MockJobs()
    {
        var captured = new List<Job>();
        var mock = new Mock<IBackgroundJobClient>();
        mock.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()))
            .Callback<Job, IState>((job, _) => captured.Add(job))
            .Returns("fake-job-id");
        return (mock, captured);
    }

    internal static CreatePrRequest DraftPrRequest() => new()
    {
        PrTitle       = "Test PR",
        Department    = "IT",
        RequestedDate = DateTime.UtcNow.AddDays(7),
        Priority      = "HIGH",
        Lines = [new CreatePrLineRequest { ItemDescription = "Laptop", Quantity = 1m, EstimatedUnitPrice = 500m, UnitOfMeasure = "PC" }]
    };

    internal static TimelineEvent CapturedEvent(List<Job> captured) =>
        (TimelineEvent)captured.Single(j => j.Method.Name == "AppendAsync").Args[1];
}

// ── PR events (Created, Submitted, Approved, Rejected) ──────────────────────────

public class RequisitionService_TimelineWiring_Tests
{
    [Fact]
    public async Task CreateAsync_Enqueues_PR_CREATED_And_The_Business_Action_Still_Succeeds()
    {
        var db       = WiringBuild.NewDb();
        var repo     = new RequisitionRepository(db);
        var workflow = new Mock<IWorkflowActionService>();
        var (jobsMock, captured) = WiringBuild.MockJobs();
        var svc = new RequisitionService(repo, workflow.Object, jobsMock.Object);

        var uuid = await svc.CreateAsync(WiringBuild.DraftPrRequest(), createdBy: 1);

        // Proves the timeline write is enqueued (never executed inline) — the business
        // action already completed successfully before this assertion even runs.
        uuid.Should().NotBeEmpty();

        var evt = WiringBuild.CapturedEvent(captured);
        evt.EventType.Should().Be("PR_CREATED");
        evt.InterfaceCode.Should().Be("PR");
        evt.DocumentId.Should().Be(uuid);
    }

    [Fact]
    public async Task ApproveAsync_Enqueues_PR_APPROVED()
    {
        var db       = WiringBuild.NewDb();
        var repo     = new RequisitionRepository(db);
        var uuid     = await repo.CreateAsync(WiringBuild.DraftPrRequest(), createdBy: 1);

        var workflow = new Mock<IWorkflowActionService>();
        workflow.Setup(w => w.ApproveByDocumentAsync("PR", uuid, 2, "ok")).ReturnsAsync(Guid.NewGuid());
        var (jobsMock, captured) = WiringBuild.MockJobs();
        var svc = new RequisitionService(repo, workflow.Object, jobsMock.Object);

        await svc.ApproveAsync(uuid, 2, "ok");

        WiringBuild.CapturedEvent(captured).EventType.Should().Be("PR_APPROVED");
    }

    [Fact]
    public async Task RejectAsync_Enqueues_PR_REJECTED_With_Reason_In_Notes()
    {
        var db       = WiringBuild.NewDb();
        var repo     = new RequisitionRepository(db);
        var uuid     = await repo.CreateAsync(WiringBuild.DraftPrRequest(), createdBy: 1);

        var workflow = new Mock<IWorkflowActionService>();
        workflow.Setup(w => w.RejectByDocumentAsync("PR", uuid, 3, "Budget exceeded")).ReturnsAsync(Guid.NewGuid());
        var (jobsMock, captured) = WiringBuild.MockJobs();
        var svc = new RequisitionService(repo, workflow.Object, jobsMock.Object);

        await svc.RejectAsync(uuid, 3, "Budget exceeded");

        var evt = WiringBuild.CapturedEvent(captured);
        evt.EventType.Should().Be("PR_REJECTED");
        evt.Notes.Should().Be("Budget exceeded");
    }

    [Fact]
    public async Task SubmitAsync_Enqueues_PR_SUBMITTED()
    {
        var db       = WiringBuild.NewDb();
        var repo     = new RequisitionRepository(db);
        var uuid     = await repo.CreateAsync(WiringBuild.DraftPrRequest(), createdBy: 1);

        var workflow = new Mock<IWorkflowActionService>();
        workflow.Setup(w => w.SubmitAsync(It.IsAny<SubmitDocumentCommand>())).ReturnsAsync(Guid.NewGuid());
        var (jobsMock, captured) = WiringBuild.MockJobs();
        var svc = new RequisitionService(repo, workflow.Object, jobsMock.Object);

        await svc.SubmitAsync(uuid, 1);

        WiringBuild.CapturedEvent(captured).EventType.Should().Be("PR_SUBMITTED");
    }
}

// ── PO events (approver + tier detail on approval) ──────────────────────────────

public class PurchaseOrderService_TimelineWiring_Tests
{
    private static async Task<(Guid poUuid, PurchaseOrderRepository poRepo, DemandDbContext db)> SeedPoAsync()
    {
        var db     = WiringBuild.NewDb();
        var prRepo = new RequisitionRepository(db);
        var prUuid = await prRepo.CreateAsync(WiringBuild.DraftPrRequest(), createdBy: 1);

        var pr = await db.PurchaseRequisitions.FirstAsync(p => p.UUID == prUuid);
        pr.Status = "APPROVED";
        await db.SaveChangesAsync();

        var poRepo = new PurchaseOrderRepository(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<PurchaseOrderRepository>.Instance);
        var poUuid = await poRepo.CreateFromPrAsync(prUuid, new ConvertPrToPoRequest
        {
            SupplierId   = Guid.NewGuid(),
            SupplierName = "Vendor Corp",
            DeliveryDate = DateTime.UtcNow.AddDays(30)
        }, createdBy: 1);

        return (poUuid, poRepo, db);
    }

    [Fact]
    public async Task ApproveAsync_Enqueues_PO_APPROVED_With_Approver_And_Tier_In_Notes()
    {
        var (poUuid, poRepo, _) = await SeedPoAsync();

        var inbox = new Mock<IWorkflowInboxService>();
        inbox.Setup(i => i.GetActiveApprovalByDocumentAsync(poUuid)).ReturnsAsync(new ApprovalDetailDto
        {
            CurrentStepNumber = 2,
            Steps = [new StepDetailDto { StepNumber = 2, StepName = "Finance Manager" }]
        });

        var workflow = new Mock<IWorkflowActionService>();
        workflow.Setup(w => w.ApproveByDocumentAsync("PO", poUuid, 5, null)).ReturnsAsync(Guid.NewGuid());

        var (jobsMock, captured) = WiringBuild.MockJobs();
        var svc = new PurchaseOrderService(poRepo, workflow.Object, inbox.Object, jobsMock.Object);

        await svc.ApproveAsync(poUuid, 5);

        var evt = WiringBuild.CapturedEvent(captured);
        evt.EventType.Should().Be("PO_APPROVED");
        evt.PerformedBy.Should().Be(5);
        evt.Notes.Should().Contain("tier 2").And.Contain("Finance Manager");
    }

    [Fact]
    public async Task CreateFromPrAsync_Enqueues_PO_CREATED()
    {
        var db     = WiringBuild.NewDb();
        var prRepo = new RequisitionRepository(db);
        var prUuid = await prRepo.CreateAsync(WiringBuild.DraftPrRequest(), createdBy: 1);
        var pr     = await db.PurchaseRequisitions.FirstAsync(p => p.UUID == prUuid);
        pr.Status  = "APPROVED";
        await db.SaveChangesAsync();

        var poRepo = new PurchaseOrderRepository(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<PurchaseOrderRepository>.Instance);
        var inbox    = new Mock<IWorkflowInboxService>();
        var workflow = new Mock<IWorkflowActionService>();
        var (jobsMock, captured) = WiringBuild.MockJobs();
        var svc = new PurchaseOrderService(poRepo, workflow.Object, inbox.Object, jobsMock.Object);

        var poUuid = await svc.CreateFromPrAsync(prUuid, new ConvertPrToPoRequest
        {
            SupplierId = Guid.NewGuid(), SupplierName = "Vendor Corp", DeliveryDate = DateTime.UtcNow.AddDays(30)
        }, createdBy: 1);

        var evt = WiringBuild.CapturedEvent(captured);
        evt.EventType.Should().Be("PO_CREATED");
        evt.DocumentId.Should().Be(poUuid);
    }
}

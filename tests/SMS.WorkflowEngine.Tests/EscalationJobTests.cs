using FluentAssertions;
using Moq;
using SMS.Shared.Common;
using SMS.WorkflowEngine.Data;
using SMS.WorkflowEngine.Jobs;
using SMS.WorkflowEngine.Tests.Helpers;
using Xunit;

namespace SMS.WorkflowEngine.Tests;

public class WorkflowEscalationJob_Tier1_Idempotency
{
    private static WorkflowEscalationJob Build(
        WorkflowDbContext db,
        Mock<IOrgChartService>?     org   = null,
        Mock<INotificationService>? notif = null)
        => new(db, (org ?? new Mock<IOrgChartService>()).Object,
                   (notif ?? new Mock<INotificationService>()).Object);

    [Fact]
    public async Task Returns_without_changes_when_step_not_found()
    {
        var db  = DbFactory.Create();
        var svc = Build(db);

        await svc.EscalateStepAsync(Guid.NewGuid()); // unknown UUID — should be a no-op

        db.WorkflowAuditLogs.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns_without_changes_when_step_is_already_APPROVED()
    {
        var approval = Fake.Approval(status: "APPROVED");
        var db = DbFactory.Create(d =>
        {
            d.DocumentApprovals.Add(approval);
            d.SaveChanges();
            d.DocumentApprovalSteps.Add(Fake.ApprovalStep(approval.Id, status: "APPROVED"));
        });
        var svc = Build(db);
        var stepUuid = db.DocumentApprovalSteps.First().UUID;

        await svc.EscalateStepAsync(stepUuid);

        db.WorkflowAuditLogs.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns_without_changes_when_step_is_REJECTED()
    {
        var approval = Fake.Approval(status: "REJECTED");
        var db = DbFactory.Create(d =>
        {
            d.DocumentApprovals.Add(approval);
            d.SaveChanges();
            d.DocumentApprovalSteps.Add(Fake.ApprovalStep(approval.Id, status: "REJECTED"));
        });
        var svc = Build(db);
        var stepUuid = db.DocumentApprovalSteps.First().UUID;

        await svc.EscalateStepAsync(stepUuid);

        db.WorkflowAuditLogs.Should().BeEmpty();
    }
}

public class WorkflowEscalationJob_Tier1_Escalation
{
    private static (WorkflowEscalationJob svc, Mock<INotificationService> notif, Mock<IOrgChartService> org)
        Build(WorkflowDbContext db)
    {
        HangfireSetup.EnsureConfigured();
        var org   = new Mock<IOrgChartService>();
        var notif = new Mock<INotificationService>();
        notif.Setup(n => n.TryCreateAsync(It.IsAny<NotificationRequest>())).Returns(Task.CompletedTask);
        org.Setup(o => o.GetSupervisorAsync(It.IsAny<int>())).ReturnsAsync((UserIdentity?)null);
        var svc = new WorkflowEscalationJob(db, org.Object, notif.Object);
        return (svc, notif, org);
    }

    [Fact]
    public async Task Sets_IsEscalated_true_on_step_and_approval()
    {
        var approval = Fake.Approval(status: "PENDING");
        var db = DbFactory.Create(d =>
        {
            d.DocumentApprovals.Add(approval);
            d.SaveChanges();
            d.DocumentApprovalSteps.Add(Fake.ApprovalStep(approval.Id, status: "PENDING", slaHours: 4));
        });
        var (svc, _, _) = Build(db);
        var stepUuid = db.DocumentApprovalSteps.First().UUID;

        await svc.EscalateStepAsync(stepUuid);

        db.DocumentApprovalSteps.First().IsEscalated.Should().BeTrue();
        db.DocumentApprovals.First().IsEscalated.Should().BeTrue();
    }

    [Fact]
    public async Task Writes_ESCALATED_audit_log()
    {
        var approval = Fake.Approval(status: "PENDING");
        var db = DbFactory.Create(d =>
        {
            d.DocumentApprovals.Add(approval);
            d.SaveChanges();
            d.DocumentApprovalSteps.Add(Fake.ApprovalStep(approval.Id, status: "PENDING", slaHours: 4));
        });
        var (svc, _, _) = Build(db);
        var stepUuid = db.DocumentApprovalSteps.First().UUID;

        await svc.EscalateStepAsync(stepUuid);

        db.WorkflowAuditLogs.Should().ContainSingle(l => l.Action == "ESCALATED");
    }

    [Fact]
    public async Task Notifies_assigned_approver()
    {
        var approval = Fake.Approval(status: "PENDING");
        var db = DbFactory.Create(d =>
        {
            d.DocumentApprovals.Add(approval);
            d.SaveChanges();
            d.DocumentApprovalSteps.Add(
                Fake.ApprovalStep(approval.Id, status: "PENDING", assignedTo: Fake.User1, slaHours: 4));
        });
        var (svc, notif, _) = Build(db);
        var stepUuid = db.DocumentApprovalSteps.First().UUID;

        await svc.EscalateStepAsync(stepUuid);

        notif.Verify(n => n.TryCreateAsync(
            It.Is<NotificationRequest>(r => r.UserId == Fake.User1 && r.Type == "ESCALATION")),
            Times.Once);
    }

    [Fact]
    public async Task Also_notifies_supervisor_when_supervisor_exists()
    {
        var approval = Fake.Approval(status: "PENDING");
        var db = DbFactory.Create(d =>
        {
            d.DocumentApprovals.Add(approval);
            d.SaveChanges();
            d.DocumentApprovalSteps.Add(
                Fake.ApprovalStep(approval.Id, status: "PENDING", assignedTo: Fake.User1, slaHours: 4));
        });

        HangfireSetup.EnsureConfigured();
        var org   = new Mock<IOrgChartService>();
        var notif = new Mock<INotificationService>();
        notif.Setup(n => n.TryCreateAsync(It.IsAny<NotificationRequest>())).Returns(Task.CompletedTask);
        org.Setup(o => o.GetSupervisorAsync(Fake.User1))
           .ReturnsAsync(new UserIdentity(Fake.User2, "Manager Bob"));

        var svc      = new WorkflowEscalationJob(db, org.Object, notif.Object);
        var stepUuid = db.DocumentApprovalSteps.First().UUID;

        await svc.EscalateStepAsync(stepUuid);

        notif.Verify(n => n.TryCreateAsync(
            It.Is<NotificationRequest>(r => r.UserId == Fake.User2)),
            Times.Once);
    }

    [Fact]
    public async Task Does_not_notify_supervisor_when_no_supervisor_assigned()
    {
        var approval = Fake.Approval(status: "PENDING");
        var db = DbFactory.Create(d =>
        {
            d.DocumentApprovals.Add(approval);
            d.SaveChanges();
            d.DocumentApprovalSteps.Add(
                Fake.ApprovalStep(approval.Id, status: "PENDING", assignedTo: Fake.User1, slaHours: 4));
        });
        var (svc, notif, _) = Build(db); // org returns null supervisor by default

        var stepUuid = db.DocumentApprovalSteps.First().UUID;
        await svc.EscalateStepAsync(stepUuid);

        // Only 1 notification (to approver) — supervisor call returns null so no second notification
        notif.Verify(n => n.TryCreateAsync(It.IsAny<NotificationRequest>()), Times.Once);
    }

    [Fact]
    public async Task Saves_Tier2_job_id_on_step()
    {
        var approval = Fake.Approval(status: "PENDING");
        var db = DbFactory.Create(d =>
        {
            d.DocumentApprovals.Add(approval);
            d.SaveChanges();
            d.DocumentApprovalSteps.Add(
                Fake.ApprovalStep(approval.Id, status: "PENDING", assignedTo: Fake.User1, slaHours: 8));
        });
        var (svc, _, _) = Build(db);
        var stepUuid = db.DocumentApprovalSteps.First().UUID;

        await svc.EscalateStepAsync(stepUuid);

        db.DocumentApprovalSteps.First().EscalationJobIdTier2.Should().NotBeNullOrEmpty();
    }
}

public class WorkflowEscalationJob_Tier2_Idempotency
{
    private static WorkflowEscalationJob Build(
        WorkflowDbContext db,
        Mock<INotificationService>? notif = null)
    {
        HangfireSetup.EnsureConfigured();
        return new WorkflowEscalationJob(
            db,
            new Mock<IOrgChartService>().Object,
            (notif ?? new Mock<INotificationService>()).Object);
    }

    [Fact]
    public async Task Returns_without_changes_when_step_is_APPROVED()
    {
        var approval = Fake.Approval(status: "APPROVED");
        var db = DbFactory.Create(d =>
        {
            d.DocumentApprovals.Add(approval);
            d.SaveChanges();
            d.DocumentApprovalSteps.Add(Fake.ApprovalStep(approval.Id, status: "APPROVED"));
        });
        var svc      = Build(db);
        var stepUuid = db.DocumentApprovalSteps.First().UUID;

        await svc.EscalateSecondTierAsync(stepUuid);

        db.WorkflowAuditLogs.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns_without_changes_when_step_not_found()
    {
        var db  = DbFactory.Create();
        var svc = Build(db);

        await svc.EscalateSecondTierAsync(Guid.NewGuid());

        db.WorkflowAuditLogs.Should().BeEmpty();
    }
}

public class WorkflowEscalationJob_Tier2_Escalation
{
    private static (WorkflowEscalationJob svc, Mock<INotificationService> notif)
        Build(WorkflowDbContext db)
    {
        HangfireSetup.EnsureConfigured();
        var notif = new Mock<INotificationService>();
        notif.Setup(n => n.TryCreateAsync(It.IsAny<NotificationRequest>())).Returns(Task.CompletedTask);
        notif.Setup(n => n.BroadcastAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
            .Returns(Task.CompletedTask);
        return (new WorkflowEscalationJob(db, new Mock<IOrgChartService>().Object, notif.Object), notif);
    }

    [Fact]
    public async Task Writes_ESCALATED_TIER2_audit_log()
    {
        var approval = Fake.Approval(status: "PENDING");
        var db = DbFactory.Create(d =>
        {
            d.DocumentApprovals.Add(approval);
            d.SaveChanges();
            d.DocumentApprovalSteps.Add(Fake.ApprovalStep(approval.Id, status: "PENDING", slaHours: 4));
        });
        var (svc, _) = Build(db);
        var stepUuid = db.DocumentApprovalSteps.First().UUID;

        await svc.EscalateSecondTierAsync(stepUuid);

        db.WorkflowAuditLogs.Should().ContainSingle(l => l.Action == "ESCALATED_TIER2");
    }

    [Fact]
    public async Task Notifies_escalation_admin_when_configured()
    {
        var approval = Fake.Approval(status: "PENDING", escalationAdminId: Fake.User3);
        var db = DbFactory.Create(d =>
        {
            d.DocumentApprovals.Add(approval);
            d.SaveChanges();
            d.DocumentApprovalSteps.Add(Fake.ApprovalStep(approval.Id, status: "PENDING"));
        });
        var (svc, notif) = Build(db);
        var stepUuid = db.DocumentApprovalSteps.First().UUID;

        await svc.EscalateSecondTierAsync(stepUuid);

        notif.Verify(n => n.TryCreateAsync(
            It.Is<NotificationRequest>(r => r.UserId == Fake.User3 && r.Type == "CRITICAL_ESCALATION")),
            Times.Once);
    }

    [Fact]
    public async Task Does_not_call_TryCreateAsync_when_no_escalation_admin()
    {
        var approval = Fake.Approval(status: "PENDING", escalationAdminId: null);
        var db = DbFactory.Create(d =>
        {
            d.DocumentApprovals.Add(approval);
            d.SaveChanges();
            d.DocumentApprovalSteps.Add(Fake.ApprovalStep(approval.Id, status: "PENDING"));
        });
        var (svc, notif) = Build(db);
        var stepUuid = db.DocumentApprovalSteps.First().UUID;

        await svc.EscalateSecondTierAsync(stepUuid);

        notif.Verify(n => n.TryCreateAsync(It.IsAny<NotificationRequest>()), Times.Never);
    }

    [Fact]
    public async Task Always_calls_BroadcastAsync_for_system_wide_critical_alert()
    {
        var approval = Fake.Approval(status: "PENDING", escalationAdminId: null);
        var db = DbFactory.Create(d =>
        {
            d.DocumentApprovals.Add(approval);
            d.SaveChanges();
            d.DocumentApprovalSteps.Add(Fake.ApprovalStep(approval.Id, status: "PENDING"));
        });
        var (svc, notif) = Build(db);
        var stepUuid = db.DocumentApprovalSteps.First().UUID;

        await svc.EscalateSecondTierAsync(stepUuid);

        notif.Verify(n => n.BroadcastAsync(
            "CRITICAL_ESCALATION",
            "Workflow",
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<int>()),
            Times.Once);
    }
}

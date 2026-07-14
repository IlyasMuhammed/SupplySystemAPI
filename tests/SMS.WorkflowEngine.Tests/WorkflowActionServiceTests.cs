using FluentAssertions;
using MediatR;
using Moq;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Domain;
using SMS.WorkflowEngine.Events;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;
using SMS.WorkflowEngine.Tests.Helpers;
using Xunit;

namespace SMS.WorkflowEngine.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// Submit
// ═══════════════════════════════════════════════════════════════════════════════

public class WorkflowActionService_Submit
{
    private static ActionSvcContext Setup(WorkflowResolutionResult? resolution = null)
    {
        var ctx = ActionSvcBuilder.Build();
        ctx.DocStatus.Setup(s => s.GetStatusAsync("PO", It.IsAny<Guid>())).ReturnsAsync("DRAFT");
        ctx.Resolution.Setup(r => r.ResolveWorkflowAsync(It.IsAny<SubmitDocumentCommand>()))
           .ReturnsAsync(resolution ?? Fake.Resolution());
        return ctx;
    }

    [Fact]
    public async Task Creates_DocumentApproval_record_with_correct_fields()
    {
        var docId = Guid.NewGuid();
        var ctx   = Setup();

        var uuid = await ctx.Svc.SubmitAsync(new SubmitDocumentCommand
        {
            InterfaceCode  = "PO",
            DocumentId     = docId,
            DocumentNumber = "PO-001",
            SubmittedBy    = Fake.User1
        });

        var approval = ctx.Db.DocumentApprovals.Single();
        approval.UUID.Should().Be(uuid);
        approval.InterfaceCode.Should().Be("PO");
        approval.DocumentId.Should().Be(docId);
        approval.DocumentNumber.Should().Be("PO-001");
        approval.Status.Should().Be("PENDING");
        approval.RevisionNo.Should().Be(1);
        approval.InitiatedBy.Should().Be(Fake.User1);
        approval.TotalSteps.Should().Be(1);
        approval.CurrentStepNumber.Should().Be(1);
    }

    [Fact]
    public async Task Creates_DocumentApprovalStep_for_each_resolved_approver()
    {
        var ctx = Setup(Fake.Resolution(steps: [
            Fake.ResolvedStep(stepNumber: 1, approvers: [
                new ResolvedApprover(Fake.User1, "Alice"),
                new ResolvedApprover(Fake.User2, "Bob")])]));

        await ctx.Svc.SubmitAsync(new SubmitDocumentCommand
        {
            InterfaceCode = "PO", DocumentId = Guid.NewGuid(),
            DocumentNumber = "PO-001", SubmittedBy = Fake.User1
        });

        var steps = ctx.Db.DocumentApprovalSteps.ToList();
        steps.Should().HaveCount(2);
        steps.Should().Contain(s => s.AssignedTo == Fake.User1 && s.Status == "PENDING");
        steps.Should().Contain(s => s.AssignedTo == Fake.User2 && s.Status == "PENDING");
    }

    [Fact]
    public async Task Pre_skipped_steps_written_with_SKIPPED_status()
    {
        var ctx = Setup(Fake.Resolution(steps: [
            Fake.ResolvedStep(stepNumber: 1, isSkipped: true),
            Fake.ResolvedStep(stepNumber: 2, isSkipped: false)]));

        await ctx.Svc.SubmitAsync(new SubmitDocumentCommand
        {
            InterfaceCode = "PO", DocumentId = Guid.NewGuid(),
            DocumentNumber = "PO-001", SubmittedBy = Fake.User1
        });

        ctx.Db.DocumentApprovalSteps.Count(s => s.Status == "SKIPPED").Should().Be(1);
        ctx.Db.DocumentApprovalSteps.Count(s => s.Status == "PENDING").Should().Be(1);
    }

    [Fact]
    public async Task Writes_audit_log_with_SUBMITTED_action()
    {
        var ctx = Setup();

        await ctx.Svc.SubmitAsync(new SubmitDocumentCommand
        {
            InterfaceCode = "PO", DocumentId = Guid.NewGuid(),
            DocumentNumber = "PO-001", SubmittedBy = Fake.User1
        });

        ctx.Db.WorkflowAuditLogs.Should().ContainSingle(l => l.Action == "SUBMITTED");
    }

    [Fact]
    public async Task Publishes_DocumentSubmittedEvent()
    {
        var ctx = Setup();

        await ctx.Svc.SubmitAsync(new SubmitDocumentCommand
        {
            InterfaceCode = "PO", DocumentId = Guid.NewGuid(),
            DocumentNumber = "PO-001", SubmittedBy = Fake.User1
        });

        ctx.Publisher.Verify(
            p => p.Publish(It.IsAny<DocumentSubmittedEvent>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Schedules_Hangfire_escalation_job_when_SLA_configured()
    {
        var ctx = Setup(Fake.Resolution(steps: [Fake.ResolvedStep(slaHours: 24)]));

        await ctx.Svc.SubmitAsync(new SubmitDocumentCommand
        {
            InterfaceCode = "PO", DocumentId = Guid.NewGuid(),
            DocumentNumber = "PO-001", SubmittedBy = Fake.User1
        });

        var step = ctx.Db.DocumentApprovalSteps.Single();
        step.EscalationJobId.Should().NotBeNullOrEmpty();
        step.DueAt.Should().NotBeNull();
    }

    [Fact]
    public async Task No_Hangfire_job_when_step_has_no_SLA()
    {
        var ctx = Setup(Fake.Resolution(steps: [Fake.ResolvedStep(slaHours: null)]));

        await ctx.Svc.SubmitAsync(new SubmitDocumentCommand
        {
            InterfaceCode = "PO", DocumentId = Guid.NewGuid(),
            DocumentNumber = "PO-001", SubmittedBy = Fake.User1
        });

        var step = ctx.Db.DocumentApprovalSteps.Single();
        step.EscalationJobId.Should().BeNull();
        step.DueAt.Should().BeNull();
    }

    [Fact]
    public async Task Throws_UnprocessableEntityException_when_document_is_not_in_DRAFT()
    {
        var ctx = ActionSvcBuilder.Build();
        ctx.DocStatus.Setup(s => s.GetStatusAsync("PO", It.IsAny<Guid>())).ReturnsAsync("PENDING");

        var act = () => ctx.Svc.SubmitAsync(new SubmitDocumentCommand
        {
            InterfaceCode = "PO", DocumentId = Guid.NewGuid(),
            DocumentNumber = "PO-001", SubmittedBy = Fake.User1
        });

        await act.Should().ThrowAsync<UnprocessableEntityException>().WithMessage("*DRAFT*");
    }

    [Fact]
    public async Task Revision_number_increments_on_second_submit_for_same_document()
    {
        var docId = Guid.NewGuid();
        var ctx   = ActionSvcBuilder.Build();

        ctx.DocStatus.Setup(s => s.GetStatusAsync("PO", docId)).ReturnsAsync("DRAFT");
        ctx.Resolution.Setup(r => r.ResolveWorkflowAsync(It.IsAny<SubmitDocumentCommand>()))
           .ReturnsAsync(Fake.Resolution());

        // Seed an existing approval (revision 1) for the same document
        ctx.Db.DocumentApprovals.Add(Fake.Approval(documentId: docId, status: "REJECTED"));
        await ctx.Db.SaveChangesAsync();

        await ctx.Svc.SubmitAsync(new SubmitDocumentCommand
        {
            InterfaceCode = "PO", DocumentId = docId,
            DocumentNumber = "PO-001", SubmittedBy = Fake.User1
        });

        ctx.Db.DocumentApprovals.Max(a => a.RevisionNo).Should().Be(2);
    }

    [Fact]
    public async Task Throws_BadRequestException_on_empty_interface_code()
    {
        var ctx = ActionSvcBuilder.Build();

        var act = () => ctx.Svc.SubmitAsync(new SubmitDocumentCommand
        {
            InterfaceCode = string.Empty, DocumentId = Guid.NewGuid(), SubmittedBy = Fake.User1
        });

        await act.Should().ThrowAsync<BadRequestException>();
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// Approve
// ═══════════════════════════════════════════════════════════════════════════════

public class WorkflowActionService_Approve
{
    private static (ActionSvcContext ctx, DocumentApproval approval) Seed(
        string approvalMode = "ANY_ONE",
        int stepCount = 1,
        bool canReject = true,
        bool finalStep = true)
    {
        var ctx    = ActionSvcBuilder.Build();
        var docId  = Guid.NewGuid();
        int totalSteps = finalStep ? stepCount : stepCount + 1;

        var approval = Fake.Approval(totalSteps: totalSteps, currentStep: 1);
        ctx.Db.DocumentApprovals.Add(approval);
        ctx.Db.SaveChanges();

        for (var i = 0; i < stepCount; i++)
            ctx.Db.DocumentApprovalSteps.Add(
                Fake.ApprovalStep(approval.Id, assignedTo: Fake.User1 + i, approvalMode: approvalMode, canReject: canReject));

        if (!finalStep)
            ctx.Db.DocumentApprovalSteps.Add(
                Fake.ApprovalStep(approval.Id, stepNumber: 2, assignedTo: Fake.User2, approvalMode: "ANY_ONE"));

        ctx.Db.SaveChanges();
        return (ctx, approval);
    }

    [Fact]
    public async Task ANY_ONE_mode_single_approval_marks_step_complete()
    {
        var (ctx, approval) = Seed("ANY_ONE");

        await ctx.Svc.ApproveAsync(new ApproveCommand { ApprovalUUID = approval.UUID, ApprovedBy = Fake.User1 });

        ctx.Db.DocumentApprovals.Single().Status.Should().Be("APPROVED");
    }

    [Fact]
    public async Task ANY_ONE_mode_with_two_approvers_skips_second_on_first_approval()
    {
        var (ctx, approval) = Seed("ANY_ONE", stepCount: 2);

        await ctx.Svc.ApproveAsync(new ApproveCommand { ApprovalUUID = approval.UUID, ApprovedBy = Fake.User1 });

        var steps = ctx.Db.DocumentApprovalSteps.Where(s => s.StepNumber == 1).ToList();
        steps.Count(s => s.Status == "APPROVED").Should().Be(1);
        steps.Count(s => s.Status == "SKIPPED").Should().Be(1);
    }

    [Fact]
    public async Task ALL_mode_requires_all_approvers_before_step_completes()
    {
        var (ctx, approval) = Seed("ALL", stepCount: 2);

        // First approval — step not yet complete
        await ctx.Svc.ApproveAsync(new ApproveCommand { ApprovalUUID = approval.UUID, ApprovedBy = Fake.User1 });
        ctx.Db.DocumentApprovals.Single().Status.Should().Be("PENDING");

        // Second approval — step complete
        await ctx.Svc.ApproveAsync(new ApproveCommand { ApprovalUUID = approval.UUID, ApprovedBy = Fake.User2 });
        ctx.Db.DocumentApprovals.Single().Status.Should().Be("APPROVED");
    }

    [Fact]
    public async Task MAJORITY_mode_completes_at_2_of_3()
    {
        var ctx    = ActionSvcBuilder.Build();
        var approval = Fake.Approval(totalSteps: 1, currentStep: 1);
        ctx.Db.DocumentApprovals.Add(approval);
        ctx.Db.SaveChanges();
        ctx.Db.DocumentApprovalSteps.AddRange(
            Fake.ApprovalStep(approval.Id, assignedTo: Fake.User1, approvalMode: "MAJORITY"),
            Fake.ApprovalStep(approval.Id, assignedTo: Fake.User2, approvalMode: "MAJORITY"),
            Fake.ApprovalStep(approval.Id, assignedTo: Fake.User3, approvalMode: "MAJORITY"));
        ctx.Db.SaveChanges();

        await ctx.Svc.ApproveAsync(new ApproveCommand { ApprovalUUID = approval.UUID, ApprovedBy = Fake.User1 });
        ctx.Db.DocumentApprovals.Single().Status.Should().Be("PENDING"); // 1/3

        await ctx.Svc.ApproveAsync(new ApproveCommand { ApprovalUUID = approval.UUID, ApprovedBy = Fake.User2 });
        ctx.Db.DocumentApprovals.Single().Status.Should().Be("APPROVED"); // 2/3 = majority
    }

    [Fact]
    public async Task MAJORITY_skips_remaining_pending_rows_on_completion()
    {
        var ctx      = ActionSvcBuilder.Build();
        var approval = Fake.Approval(totalSteps: 1);
        ctx.Db.DocumentApprovals.Add(approval);
        ctx.Db.SaveChanges();
        ctx.Db.DocumentApprovalSteps.AddRange(
            Fake.ApprovalStep(approval.Id, assignedTo: Fake.User1, approvalMode: "MAJORITY"),
            Fake.ApprovalStep(approval.Id, assignedTo: Fake.User2, approvalMode: "MAJORITY"),
            Fake.ApprovalStep(approval.Id, assignedTo: Fake.User3, approvalMode: "MAJORITY"));
        ctx.Db.SaveChanges();

        await ctx.Svc.ApproveAsync(new ApproveCommand { ApprovalUUID = approval.UUID, ApprovedBy = Fake.User1 });
        await ctx.Svc.ApproveAsync(new ApproveCommand { ApprovalUUID = approval.UUID, ApprovedBy = Fake.User2 });

        ctx.Db.DocumentApprovalSteps.Count(s => s.Status == "SKIPPED").Should().Be(1);
    }

    [Fact]
    public async Task Non_final_step_approval_advances_to_next_step()
    {
        var (ctx, approval) = Seed("ANY_ONE", stepCount: 1, finalStep: false);

        await ctx.Svc.ApproveAsync(new ApproveCommand { ApprovalUUID = approval.UUID, ApprovedBy = Fake.User1 });

        var updated = ctx.Db.DocumentApprovals.Single();
        updated.CurrentStepNumber.Should().Be(2);
        updated.Status.Should().Be("UNDER_REVIEW");
    }

    [Fact]
    public async Task Non_final_step_publishes_WorkflowStepAdvancedEvent()
    {
        var (ctx, approval) = Seed("ANY_ONE", stepCount: 1, finalStep: false);

        await ctx.Svc.ApproveAsync(new ApproveCommand { ApprovalUUID = approval.UUID, ApprovedBy = Fake.User1 });

        ctx.Publisher.Verify(
            p => p.Publish(It.IsAny<WorkflowStepAdvancedEvent>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Final_step_publishes_DocumentApprovedEvent()
    {
        var (ctx, approval) = Seed("ANY_ONE");

        await ctx.Svc.ApproveAsync(new ApproveCommand { ApprovalUUID = approval.UUID, ApprovedBy = Fake.User1 });

        ctx.Publisher.Verify(
            p => p.Publish(It.IsAny<DocumentApprovedEvent>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Final_step_sets_CompletedAt()
    {
        var before = DateTime.UtcNow;
        var (ctx, approval) = Seed("ANY_ONE");

        await ctx.Svc.ApproveAsync(new ApproveCommand { ApprovalUUID = approval.UUID, ApprovedBy = Fake.User1 });

        ctx.Db.DocumentApprovals.Single().CompletedAt.Should().BeOnOrAfter(before);
    }

    [Fact]
    public async Task Non_assigned_user_throws_ForbiddenException()
    {
        var (ctx, approval) = Seed("ANY_ONE");

        var act = () => ctx.Svc.ApproveAsync(new ApproveCommand
        {
            ApprovalUUID = approval.UUID, ApprovedBy = 999 // not assigned
        });

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Already_actioned_approver_throws_UnprocessableEntityException()
    {
        var (ctx, approval) = Seed("ALL", stepCount: 2);

        // First approval succeeds
        await ctx.Svc.ApproveAsync(new ApproveCommand { ApprovalUUID = approval.UUID, ApprovedBy = Fake.User1 });

        // Same user tries to approve again
        var act = () => ctx.Svc.ApproveAsync(new ApproveCommand
        {
            ApprovalUUID = approval.UUID, ApprovedBy = Fake.User1
        });

        await act.Should().ThrowAsync<UnprocessableEntityException>().WithMessage("*already actioned*");
    }

    [Fact]
    public async Task Approval_in_terminal_status_throws_UnprocessableEntityException()
    {
        var ctx      = ActionSvcBuilder.Build();
        var approval = Fake.Approval(status: "APPROVED");
        ctx.Db.DocumentApprovals.Add(approval);
        ctx.Db.SaveChanges();

        var act = () => ctx.Svc.ApproveAsync(new ApproveCommand
        {
            ApprovalUUID = approval.UUID, ApprovedBy = Fake.User1
        });

        await act.Should().ThrowAsync<UnprocessableEntityException>().WithMessage("*APPROVED*");
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// Reject
// ═══════════════════════════════════════════════════════════════════════════════

public class WorkflowActionService_Reject
{
    private static (ActionSvcContext ctx, DocumentApproval approval) Seed(
        bool canReject = true, string approvalStatus = "PENDING")
    {
        var ctx      = ActionSvcBuilder.Build();
        var approval = Fake.Approval(status: approvalStatus);
        ctx.Db.DocumentApprovals.Add(approval);
        ctx.Db.SaveChanges();
        ctx.Db.DocumentApprovalSteps.AddRange(
            Fake.ApprovalStep(approval.Id, stepNumber: 1, assignedTo: Fake.User1, canReject: canReject),
            Fake.ApprovalStep(approval.Id, stepNumber: 2, assignedTo: Fake.User2));
        ctx.Db.SaveChanges();
        return (ctx, approval);
    }

    [Fact]
    public async Task Rejection_marks_approval_as_REJECTED_and_sets_CompletedAt()
    {
        var before = DateTime.UtcNow;
        var (ctx, approval) = Seed();

        await ctx.Svc.RejectAsync(new RejectCommand
        {
            ApprovalUUID = approval.UUID, RejectedBy = Fake.User1, RejectionReason = "Not approved"
        });

        var updated = ctx.Db.DocumentApprovals.Single();
        updated.Status.Should().Be("REJECTED");
        updated.CompletedAt.Should().BeOnOrAfter(before);
    }

    [Fact]
    public async Task Rejection_skips_all_other_pending_steps()
    {
        var (ctx, approval) = Seed();

        await ctx.Svc.RejectAsync(new RejectCommand
        {
            ApprovalUUID = approval.UUID, RejectedBy = Fake.User1, RejectionReason = "No"
        });

        // Step 1 = REJECTED, step 2 = SKIPPED
        ctx.Db.DocumentApprovalSteps.Count(s => s.Status == "REJECTED").Should().Be(1);
        ctx.Db.DocumentApprovalSteps.Count(s => s.Status == "SKIPPED").Should().Be(1);
    }

    [Fact]
    public async Task Rejection_publishes_DocumentRejectedEvent()
    {
        var (ctx, approval) = Seed();

        await ctx.Svc.RejectAsync(new RejectCommand
        {
            ApprovalUUID = approval.UUID, RejectedBy = Fake.User1, RejectionReason = "No"
        });

        ctx.Publisher.Verify(
            p => p.Publish(It.IsAny<DocumentRejectedEvent>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CanReject_false_throws_UnprocessableEntityException()
    {
        var (ctx, approval) = Seed(canReject: false);

        var act = () => ctx.Svc.RejectAsync(new RejectCommand
        {
            ApprovalUUID = approval.UUID, RejectedBy = Fake.User1, RejectionReason = "No"
        });

        await act.Should().ThrowAsync<UnprocessableEntityException>().WithMessage("*not permitted*");
    }

    [Fact]
    public async Task Non_assigned_user_throws_ForbiddenException()
    {
        var (ctx, approval) = Seed();

        var act = () => ctx.Svc.RejectAsync(new RejectCommand
        {
            ApprovalUUID = approval.UUID, RejectedBy = 999, RejectionReason = "No"
        });

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Rejection_on_completed_approval_throws_UnprocessableEntityException()
    {
        var (ctx, approval) = Seed(approvalStatus: "APPROVED");

        var act = () => ctx.Svc.RejectAsync(new RejectCommand
        {
            ApprovalUUID = approval.UUID, RejectedBy = Fake.User1, RejectionReason = "No"
        });

        await act.Should().ThrowAsync<UnprocessableEntityException>();
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// Recall
// ═══════════════════════════════════════════════════════════════════════════════

public class WorkflowActionService_Recall
{
    [Fact]
    public async Task Recall_at_step_1_sets_approval_status_to_RECALLED()
    {
        var ctx      = ActionSvcBuilder.Build();
        var approval = Fake.Approval(status: "PENDING", currentStep: 1, initiatedBy: Fake.User1);
        ctx.Db.DocumentApprovals.Add(approval);
        ctx.Db.SaveChanges();
        ctx.Db.DocumentApprovalSteps.Add(Fake.ApprovalStep(approval.Id, stepNumber: 1));
        ctx.Db.SaveChanges();

        await ctx.Svc.RecallAsync(new RecallCommand
        {
            ApprovalUUID = approval.UUID, RecalledBy = Fake.User1
        });

        ctx.Db.DocumentApprovals.Single().Status.Should().Be("RECALLED");
    }

    [Fact]
    public async Task Recall_at_step_2_reverts_approval_to_step_1_PENDING()
    {
        var ctx      = ActionSvcBuilder.Build();
        var approval = Fake.Approval(status: "UNDER_REVIEW", currentStep: 2, totalSteps: 2, initiatedBy: Fake.User1);
        ctx.Db.DocumentApprovals.Add(approval);
        ctx.Db.SaveChanges();
        ctx.Db.DocumentApprovalSteps.AddRange(
            Fake.ApprovalStep(approval.Id, stepNumber: 1, assignedTo: Fake.User1, status: "APPROVED"),
            Fake.ApprovalStep(approval.Id, stepNumber: 2, assignedTo: Fake.User2, status: "PENDING"));
        ctx.Db.SaveChanges();

        await ctx.Svc.RecallAsync(new RecallCommand
        {
            ApprovalUUID = approval.UUID, RecalledBy = Fake.User1
        });

        var updated = ctx.Db.DocumentApprovals.Single();
        updated.CurrentStepNumber.Should().Be(1);
        updated.Status.Should().Be("UNDER_REVIEW");

        // Step 1 row should be back to PENDING
        ctx.Db.DocumentApprovalSteps.Single(s => s.StepNumber == 1).Status.Should().Be("PENDING");
        // Step 2 row should be RECALLED
        ctx.Db.DocumentApprovalSteps.Single(s => s.StepNumber == 2).Status.Should().Be("RECALLED");
    }

    [Fact]
    public async Task Recall_publishes_DocumentRecalledEvent()
    {
        var ctx      = ActionSvcBuilder.Build();
        var approval = Fake.Approval(status: "PENDING", currentStep: 1, initiatedBy: Fake.User1);
        ctx.Db.DocumentApprovals.Add(approval);
        ctx.Db.SaveChanges();
        ctx.Db.DocumentApprovalSteps.Add(Fake.ApprovalStep(approval.Id));
        ctx.Db.SaveChanges();

        await ctx.Svc.RecallAsync(new RecallCommand { ApprovalUUID = approval.UUID, RecalledBy = Fake.User1 });

        ctx.Publisher.Verify(
            p => p.Publish(It.IsAny<DocumentRecalledEvent>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AllowRecall_false_throws_UnprocessableEntityException()
    {
        var ctx      = ActionSvcBuilder.Build();
        var approval = Fake.Approval(allowRecall: false, initiatedBy: Fake.User1);
        ctx.Db.DocumentApprovals.Add(approval);
        ctx.Db.SaveChanges();

        var act = () => ctx.Svc.RecallAsync(new RecallCommand
        {
            ApprovalUUID = approval.UUID, RecalledBy = Fake.User1
        });

        await act.Should().ThrowAsync<UnprocessableEntityException>().WithMessage("*not permitted*");
    }

    [Fact]
    public async Task Non_initiator_recall_throws_ForbiddenException()
    {
        var ctx      = ActionSvcBuilder.Build();
        var approval = Fake.Approval(initiatedBy: Fake.User1);
        ctx.Db.DocumentApprovals.Add(approval);
        ctx.Db.SaveChanges();

        var act = () => ctx.Svc.RecallAsync(new RecallCommand
        {
            ApprovalUUID = approval.UUID, RecalledBy = Fake.User2 // different user
        });

        await act.Should().ThrowAsync<ForbiddenException>().WithMessage("*initiator*");
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// Cancel
// ═══════════════════════════════════════════════════════════════════════════════

public class WorkflowActionService_Cancel
{
    [Fact]
    public async Task Cancel_marks_all_pending_steps_SKIPPED_and_closes_approval()
    {
        var ctx      = ActionSvcBuilder.Build();
        var approval = Fake.Approval(status: "UNDER_REVIEW", currentStep: 2, totalSteps: 2);
        ctx.Db.DocumentApprovals.Add(approval);
        ctx.Db.SaveChanges();
        ctx.Db.DocumentApprovalSteps.AddRange(
            Fake.ApprovalStep(approval.Id, stepNumber: 1, status: "APPROVED"),
            Fake.ApprovalStep(approval.Id, stepNumber: 2, status: "PENDING"));
        ctx.Db.SaveChanges();

        await ctx.Svc.CancelAsync(new CancelCommand
        {
            ApprovalUUID = approval.UUID, CancelledBy = Fake.User1, CancellationReason = "Business decision"
        });

        ctx.Db.DocumentApprovals.Single().Status.Should().Be("CANCELLED");
        ctx.Db.DocumentApprovalSteps.Count(s => s.Status == "PENDING").Should().Be(0);
        ctx.Db.DocumentApprovalSteps.Count(s => s.Status == "SKIPPED").Should().Be(1);
    }

    [Fact]
    public async Task Cancel_publishes_DocumentCancelledEvent()
    {
        var ctx      = ActionSvcBuilder.Build();
        var approval = Fake.Approval(status: "PENDING");
        ctx.Db.DocumentApprovals.Add(approval);
        ctx.Db.SaveChanges();
        ctx.Db.DocumentApprovalSteps.Add(Fake.ApprovalStep(approval.Id));
        ctx.Db.SaveChanges();

        await ctx.Svc.CancelAsync(new CancelCommand
        {
            ApprovalUUID = approval.UUID, CancelledBy = Fake.User1, CancellationReason = "No longer needed"
        });

        ctx.Publisher.Verify(
            p => p.Publish(It.IsAny<DocumentCancelledEvent>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Cancel_on_APPROVED_throws_UnprocessableEntityException()
    {
        var ctx      = ActionSvcBuilder.Build();
        var approval = Fake.Approval(status: "APPROVED");
        ctx.Db.DocumentApprovals.Add(approval);
        ctx.Db.SaveChanges();

        var act = () => ctx.Svc.CancelAsync(new CancelCommand
        {
            ApprovalUUID = approval.UUID, CancelledBy = Fake.User1, CancellationReason = "x"
        });

        await act.Should().ThrowAsync<UnprocessableEntityException>().WithMessage("*APPROVED*");
    }

    [Fact]
    public async Task Cancel_on_CANCELLED_throws_UnprocessableEntityException()
    {
        var ctx      = ActionSvcBuilder.Build();
        var approval = Fake.Approval(status: "CANCELLED");
        ctx.Db.DocumentApprovals.Add(approval);
        ctx.Db.SaveChanges();

        var act = () => ctx.Svc.CancelAsync(new CancelCommand
        {
            ApprovalUUID = approval.UUID, CancelledBy = Fake.User1, CancellationReason = "x"
        });

        await act.Should().ThrowAsync<UnprocessableEntityException>().WithMessage("*CANCELLED*");
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// Reissue
// ═══════════════════════════════════════════════════════════════════════════════

public class WorkflowActionService_Reissue
{
    private static ActionSvcContext SetupReissue(DocumentApproval oldApproval)
    {
        var ctx = ActionSvcBuilder.Build(db => db.DocumentApprovals.Add(oldApproval));
        var newDocId = Guid.NewGuid();

        ctx.Cloner.Setup(c => c.CloneDocumentAsync("PO", oldApproval.DocumentId))
                  .ReturnsAsync(newDocId);
        ctx.Resolution.Setup(r => r.ResolveWorkflowAsync(It.IsAny<SubmitDocumentCommand>()))
                      .ReturnsAsync(Fake.Resolution());
        ctx.DocStatus.Setup(s => s.GetStatusAsync(It.IsAny<string>(), It.IsAny<Guid>()))
                     .ReturnsAsync("DRAFT");

        return ctx;
    }

    [Fact]
    public async Task Reissue_calls_CloneDocumentAsync_exactly_once()
    {
        var old = Fake.Approval(status: "REJECTED");
        var ctx = SetupReissue(old);

        await ctx.Svc.ReissueAsync(new ReissueCommand { ApprovalUUID = old.UUID, ReissuedBy = Fake.User1 });

        ctx.Cloner.Verify(
            c => c.CloneDocumentAsync("PO", old.DocumentId),
            Times.Once);
    }

    [Fact]
    public async Task Reissue_increments_revision_number()
    {
        var old = Fake.Approval(status: "REJECTED");
        var ctx = SetupReissue(old);

        var newUuid = await ctx.Svc.ReissueAsync(new ReissueCommand
        {
            ApprovalUUID = old.UUID, ReissuedBy = Fake.User1
        });

        var newApproval = ctx.Db.DocumentApprovals.Single(a => a.UUID == newUuid);
        newApproval.RevisionNo.Should().Be(old.RevisionNo + 1);
    }

    [Fact]
    public async Task Reissue_closes_old_approval_with_CLOSED_status()
    {
        var old = Fake.Approval(status: "REJECTED");
        var ctx = SetupReissue(old);

        await ctx.Svc.ReissueAsync(new ReissueCommand { ApprovalUUID = old.UUID, ReissuedBy = Fake.User1 });

        ctx.Db.DocumentApprovals.Single(a => a.UUID == old.UUID).Status.Should().Be("CLOSED");
    }

    [Fact]
    public async Task Reissue_publishes_DocumentReissuedEvent()
    {
        var old = Fake.Approval(status: "REJECTED");
        var ctx = SetupReissue(old);

        await ctx.Svc.ReissueAsync(new ReissueCommand { ApprovalUUID = old.UUID, ReissuedBy = Fake.User1 });

        ctx.Publisher.Verify(
            p => p.Publish(It.IsAny<DocumentReissuedEvent>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Reissue_on_PENDING_throws_UnprocessableEntityException()
    {
        var old = Fake.Approval(status: "PENDING");
        var ctx = ActionSvcBuilder.Build(db => db.DocumentApprovals.Add(old));

        var act = () => ctx.Svc.ReissueAsync(new ReissueCommand
        {
            ApprovalUUID = old.UUID, ReissuedBy = Fake.User1
        });

        await act.Should().ThrowAsync<UnprocessableEntityException>().WithMessage("*REJECTED*");
    }

    [Fact]
    public async Task Reissue_when_AllowReissue_false_throws_UnprocessableEntityException()
    {
        var old = Fake.Approval(status: "REJECTED", allowReissue: false);
        var ctx = ActionSvcBuilder.Build(db => db.DocumentApprovals.Add(old));

        var act = () => ctx.Svc.ReissueAsync(new ReissueCommand
        {
            ApprovalUUID = old.UUID, ReissuedBy = Fake.User1
        });

        await act.Should().ThrowAsync<UnprocessableEntityException>().WithMessage("*not permitted*");
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// Delegate
// ═══════════════════════════════════════════════════════════════════════════════

public class WorkflowActionService_Delegate
{
    private static (ActionSvcContext ctx, DocumentApproval approval) Seed(int delegationDepth = 0)
    {
        var ctx      = ActionSvcBuilder.Build();
        var approval = Fake.Approval(status: "PENDING", currentStep: 1);
        ctx.Db.DocumentApprovals.Add(approval);
        ctx.Db.SaveChanges();
        ctx.Db.DocumentApprovalSteps.Add(
            Fake.ApprovalStep(approval.Id, assignedTo: Fake.User1, delegationDepth: delegationDepth));
        ctx.Db.SaveChanges();

        // Default: delegate user is active
        ctx.UserQuery.Setup(u => u.GetUserAsync(Fake.User2))
                     .ReturnsAsync(new UserIdentity(Fake.User2, "Bob (Delegate)"));

        return (ctx, approval);
    }

    [Fact]
    public async Task Delegation_marks_original_step_as_DELEGATED()
    {
        var (ctx, approval) = Seed();

        await ctx.Svc.DelegateAsync(new DelegateCommand
        {
            ApprovalUUID   = approval.UUID,
            DelegatedBy    = Fake.User1,
            DelegateUserId = Fake.User2
        });

        var original = ctx.Db.DocumentApprovalSteps.Single(s => s.AssignedTo == Fake.User1);
        original.Status.Should().Be("DELEGATED");
        original.DelegateId.Should().Be(Fake.User2);
        original.DelegatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Delegation_creates_new_PENDING_step_for_delegate()
    {
        var (ctx, approval) = Seed();

        await ctx.Svc.DelegateAsync(new DelegateCommand
        {
            ApprovalUUID   = approval.UUID,
            DelegatedBy    = Fake.User1,
            DelegateUserId = Fake.User2,
            Remarks        = "Out of office"
        });

        var delegateRow = ctx.Db.DocumentApprovalSteps.Single(s => s.AssignedTo == Fake.User2);
        delegateRow.Status.Should().Be("PENDING");
        delegateRow.DelegationDepth.Should().Be(1);
        delegateRow.ResolvedApproverName.Should().Be("Bob (Delegate)");
    }

    [Fact]
    public async Task Delegation_writes_DELEGATED_audit_log()
    {
        var (ctx, approval) = Seed();

        await ctx.Svc.DelegateAsync(new DelegateCommand
        {
            ApprovalUUID = approval.UUID, DelegatedBy = Fake.User1, DelegateUserId = Fake.User2
        });

        ctx.Db.WorkflowAuditLogs.Should().ContainSingle(l => l.Action == "DELEGATED");
    }

    [Fact]
    public async Task Delegation_publishes_StepDelegatedEvent()
    {
        var (ctx, approval) = Seed();

        await ctx.Svc.DelegateAsync(new DelegateCommand
        {
            ApprovalUUID = approval.UUID, DelegatedBy = Fake.User1, DelegateUserId = Fake.User2
        });

        ctx.Publisher.Verify(
            p => p.Publish(It.IsAny<StepDelegatedEvent>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Delegation_depth_1_throws_UnprocessableEntityException_depth_limit()
    {
        var (ctx, approval) = Seed(delegationDepth: 1);

        var act = () => ctx.Svc.DelegateAsync(new DelegateCommand
        {
            ApprovalUUID = approval.UUID, DelegatedBy = Fake.User1, DelegateUserId = Fake.User2
        });

        await act.Should().ThrowAsync<UnprocessableEntityException>().WithMessage("*limit*");
    }

    [Fact]
    public async Task Self_delegation_throws_BadRequestException()
    {
        var (ctx, approval) = Seed();

        var act = () => ctx.Svc.DelegateAsync(new DelegateCommand
        {
            ApprovalUUID = approval.UUID, DelegatedBy = Fake.User1, DelegateUserId = Fake.User1
        });

        await act.Should().ThrowAsync<BadRequestException>().WithMessage("*yourself*");
    }

    [Fact]
    public async Task Inactive_delegate_user_throws_BadRequestException()
    {
        var (ctx, approval) = Seed();
        ctx.UserQuery.Setup(u => u.GetUserAsync(Fake.User2)).ReturnsAsync((UserIdentity?)null);

        var act = () => ctx.Svc.DelegateAsync(new DelegateCommand
        {
            ApprovalUUID = approval.UUID, DelegatedBy = Fake.User1, DelegateUserId = Fake.User2
        });

        await act.Should().ThrowAsync<BadRequestException>().WithMessage("*not active*");
    }

    [Fact]
    public async Task Non_assigned_user_throws_ForbiddenException()
    {
        var (ctx, approval) = Seed();

        var act = () => ctx.Svc.DelegateAsync(new DelegateCommand
        {
            ApprovalUUID = approval.UUID, DelegatedBy = 999, DelegateUserId = Fake.User2
        });

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Delegate_inherits_original_DueAt()
    {
        var ctx      = ActionSvcBuilder.Build();
        var approval = Fake.Approval(status: "PENDING", currentStep: 1);
        ctx.Db.DocumentApprovals.Add(approval);
        ctx.Db.SaveChanges();
        var dueAt = DateTime.UtcNow.AddHours(24);
        ctx.Db.DocumentApprovalSteps.Add(
            Fake.ApprovalStep(approval.Id, assignedTo: Fake.User1, dueAt: dueAt));
        ctx.Db.SaveChanges();
        ctx.UserQuery.Setup(u => u.GetUserAsync(Fake.User2))
                     .ReturnsAsync(new UserIdentity(Fake.User2, "Bob"));

        await ctx.Svc.DelegateAsync(new DelegateCommand
        {
            ApprovalUUID = approval.UUID, DelegatedBy = Fake.User1, DelegateUserId = Fake.User2
        });

        var delegateRow = ctx.Db.DocumentApprovalSteps.Single(s => s.AssignedTo == Fake.User2);
        delegateRow.DueAt.Should().BeCloseTo(dueAt, TimeSpan.FromSeconds(1));
    }
}

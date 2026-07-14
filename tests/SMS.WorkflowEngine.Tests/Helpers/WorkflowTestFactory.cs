using Hangfire;
using Hangfire.InMemory;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using SMS.Shared.Common;
using SMS.WorkflowEngine.Data;
using SMS.WorkflowEngine.Domain;
using SMS.WorkflowEngine.Jobs;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;
using SMS.WorkflowEngine.Validation;

namespace SMS.WorkflowEngine.Tests.Helpers;

// ── InMemory DB ───────────────────────────────────────────────────────────────

internal static class DbFactory
{
    internal static WorkflowDbContext Create(Action<WorkflowDbContext>? seed = null)
    {
        var opts = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var db = new WorkflowDbContext(opts);
        seed?.Invoke(db);
        db.SaveChanges();
        return db;
    }
}

// ── Hangfire (configure once per process) ─────────────────────────────────────

internal static class HangfireSetup
{
    private static bool _configured;

    internal static void EnsureConfigured()
    {
        if (_configured) return;
        _configured = true;
        GlobalConfiguration.Configuration.UseInMemoryStorage();
    }
}

// ── WorkflowActionService builder ─────────────────────────────────────────────

internal sealed class ActionSvcContext
{
    public WorkflowActionService Svc           { get; init; } = null!;
    public WorkflowDbContext     Db            { get; init; } = null!;
    public Mock<IWorkflowResolutionService>  Resolution  { get; init; } = null!;
    public Mock<IDocumentStatusService>      DocStatus   { get; init; } = null!;
    public Mock<IPublisher>                  Publisher   { get; init; } = null!;
    public Mock<IDocumentCloneService>       Cloner      { get; init; } = null!;
    public Mock<IUserQueryService>           UserQuery   { get; init; } = null!;
}

internal static class ActionSvcBuilder
{
    internal static ActionSvcContext Build(Action<WorkflowDbContext>? seed = null)
    {
        HangfireSetup.EnsureConfigured();

        var db         = DbFactory.Create(seed);
        var resolution = new Mock<IWorkflowResolutionService>();
        var docStatus  = new Mock<IDocumentStatusService>();
        var publisher  = new Mock<IPublisher>();
        var cloner     = new Mock<IDocumentCloneService>();
        var userQuery  = new Mock<IUserQueryService>();
        var stepWriter = new DocumentApprovalStepWriter(db);

        var svc = new WorkflowActionService(
            db,
            resolution.Object,
            docStatus.Object,
            stepWriter,
            publisher.Object,
            cloner.Object,
            userQuery.Object,
            new SubmitDocumentCommandValidator(),
            new ApproveCommandValidator(),
            new RejectCommandValidator(),
            new RecallCommandValidator(),
            new CancelCommandValidator(),
            new ReissueCommandValidator(),
            new DelegateCommandValidator());

        return new ActionSvcContext
        {
            Svc        = svc,
            Db         = db,
            Resolution = resolution,
            DocStatus  = docStatus,
            Publisher  = publisher,
            Cloner     = cloner,
            UserQuery  = userQuery
        };
    }
}

// ── Domain entity factories ───────────────────────────────────────────────────

internal static class Fake
{
    internal const int User1 = 101;
    internal const int User2 = 102;
    internal const int User3 = 103;

    internal static WorkflowDefinition Definition(
        string interfaceCode = "PO",
        string? conditionOperator = null,
        decimal? conditionValue = null,
        decimal? conditionValueMin = null,
        decimal? conditionValueMax = null)
        => new()
        {
            Id                        = 1,
            UUID                      = Guid.NewGuid(),
            InterfaceCode             = interfaceCode,
            Name                      = "Test Workflow",
            Version                   = 1,
            IsActive                  = true,
            RequiresSequentialApproval= true,
            AllowRecall               = true,
            AllowReissue              = true,
            ConditionOperator         = conditionOperator,
            ConditionValue            = conditionValue,
            ConditionValueMin         = conditionValueMin,
            ConditionValueMax         = conditionValueMax,
            CreatedBy                 = 1,
            CreatedDate               = DateTime.UtcNow
        };

    internal static WorkflowStep Step(
        int definitionId = 1,
        int stepNumber = 1,
        string stepName = "Manager Approval",
        string approverType = "USER",
        int? approverRefId = User1,
        int? slaHours = null,
        string? skipCondition = null,
        string approvalMode = "ANY_ONE",
        bool canReject = true)
        => new()
        {
            UUID         = Guid.NewGuid(),
            DefinitionId = definitionId,
            StepNumber   = stepNumber,
            StepName     = stepName,
            StepType     = "APPROVAL",
            ApproverType = approverType,
            ApproverRefId= approverRefId,
            IsMandatory  = true,
            SlaHoursOverride = slaHours,
            SkipCondition= skipCondition,
            ApprovalMode = approvalMode,
            CanReject    = canReject,
            CreatedBy    = 1,
            CreatedDate  = DateTime.UtcNow
        };

    internal static DocumentApproval Approval(
        string interfaceCode = "PO",
        Guid? documentId = null,
        string status = "PENDING",
        int currentStep = 1,
        int totalSteps = 1,
        bool allowRecall = true,
        bool allowReissue = true,
        int? escalationAdminId = null,
        int? definitionId = 1,
        int initiatedBy = User1)
        => new()
        {
            UUID              = Guid.NewGuid(),
            DefinitionId      = definitionId,
            InterfaceCode     = interfaceCode,
            DocumentId        = documentId ?? Guid.NewGuid(),
            DocumentNumber    = "PO-2024-001",
            RevisionNo        = 1,
            TotalSteps        = totalSteps,
            CurrentStepNumber = currentStep,
            Status            = status,
            AllowRecall       = allowRecall,
            AllowReissue      = allowReissue,
            EscalationAdminId = escalationAdminId,
            InitiatedBy       = initiatedBy,
            InitiatedAt       = DateTime.UtcNow,
            CreatedBy         = initiatedBy,
            CreatedDate       = DateTime.UtcNow
        };

    internal static DocumentApprovalStep ApprovalStep(
        int approvalId,
        int stepNumber = 1,
        string stepName = "Manager Approval",
        string status = "PENDING",
        int? assignedTo = User1,
        string approvalMode = "ANY_ONE",
        bool canReject = true,
        int? slaHours = null,
        DateTime? dueAt = null,
        int delegationDepth = 0)
        => new()
        {
            UUID                 = Guid.NewGuid(),
            ApprovalId           = approvalId,
            StepNumber           = stepNumber,
            StepName             = stepName,
            StepType             = "APPROVAL",
            ApprovalMode         = approvalMode,
            CanReject            = canReject,
            AssignedTo           = assignedTo,
            ResolvedApproverName = assignedTo.HasValue ? $"User {assignedTo}" : null,
            Status               = status,
            SlaHours             = slaHours,
            DueAt                = dueAt,
            DelegationDepth      = delegationDepth,
            CreatedBy            = 1,
            CreatedDate          = DateTime.UtcNow
        };

    internal static WorkflowResolutionResult Resolution(
        int definitionId = 1,
        bool allowRecall = true,
        bool allowReissue = true,
        int? escalationAdminId = null,
        List<ResolvedWorkflowStep>? steps = null)
    {
        steps ??= [ResolvedStep()];
        return new WorkflowResolutionResult
        {
            Workflow = new WorkflowDefinitionSnapshot
            {
                Id            = definitionId,
                UUID          = Guid.NewGuid(),
                InterfaceCode = "PO",
                Name          = "Test Workflow",
                AllowRecall   = allowRecall,
                AllowReissue  = allowReissue,
                EscalationAdminId = escalationAdminId
            },
            Steps = steps
        };
    }

    internal static ResolvedWorkflowStep ResolvedStep(
        int stepNumber = 1,
        string stepName = "Manager Approval",
        bool isSkipped = false,
        int? slaHours = null,
        string approvalMode = "ANY_ONE",
        bool canReject = true,
        List<ResolvedApprover>? approvers = null)
        => new()
        {
            StepNumber   = stepNumber,
            StepName     = stepName,
            StepType     = "APPROVAL",
            ApproverType = "USER",
            IsMandatory  = true,
            IsSkipped    = isSkipped,
            SlaHours     = slaHours,
            ApprovalMode = approvalMode,
            CanReject    = canReject,
            Approvers    = approvers ?? [new ResolvedApprover(User1, "User 101")]
        };
}

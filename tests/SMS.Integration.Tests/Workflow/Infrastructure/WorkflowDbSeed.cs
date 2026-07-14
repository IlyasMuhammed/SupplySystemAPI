using SMS.WorkflowEngine.Data;
using SMS.WorkflowEngine.Domain;

namespace SMS.Integration.Tests.Workflow.Infrastructure;

internal static class WorkflowDbSeed
{
    internal static async Task SeedAsync(WorkflowDbContext db)
    {
        // ── PO Simple — 1 step, unconditional ────────────────────────────────
        var poSimple = Def("PO", "PO Simple");
        db.WorkflowDefinitions.Add(poSimple);
        await db.SaveChangesAsync();
        db.WorkflowSteps.Add(Step(poSimple.Id, 1, "Approval", "USER", 101));
        await db.SaveChangesAsync();

        // ── PO Standard — 2 steps, BETWEEN 50001–500000 ──────────────────────
        var poStd = Def("PO", "PO Standard",
            condOp: "BETWEEN", condMin: 50_001m, condMax: 500_000m);
        db.WorkflowDefinitions.Add(poStd);
        await db.SaveChangesAsync();
        db.WorkflowSteps.AddRange(
            Step(poStd.Id, 1, "L1 Approval", "USER", 101),
            Step(poStd.Id, 2, "L2 Approval", "USER", 102));
        await db.SaveChangesAsync();

        // ── PO Senior — 3 steps, GT 500000 ───────────────────────────────────
        var poSenior = Def("PO", "PO Senior",
            condOp: "GT", condVal: 500_000m);
        db.WorkflowDefinitions.Add(poSenior);
        await db.SaveChangesAsync();
        db.WorkflowSteps.AddRange(
            Step(poSenior.Id, 1, "L1 Approval", "USER", 101),
            Step(poSenior.Id, 2, "L2 Approval", "USER", 102),
            Step(poSenior.Id, 3, "L3 Approval", "USER", 103));
        await db.SaveChangesAsync();

        // ── Groups for GRPANY and GRPALL ──────────────────────────────────────
        var grpAny = new WorkflowGroup
        {
            UUID = Guid.NewGuid(), Name = "Any-One Group",
            IsActive = true, CreatedBy = 0, CreatedDate = DateTime.UtcNow
        };
        var grpAll = new WorkflowGroup
        {
            UUID = Guid.NewGuid(), Name = "All Group",
            IsActive = true, CreatedBy = 0, CreatedDate = DateTime.UtcNow
        };
        db.WorkflowGroups.AddRange(grpAny, grpAll);
        await db.SaveChangesAsync();

        foreach (var (grpId, gid) in new[] { (grpAny.Id, 1), (grpAll.Id, 2) })
        {
            db.WorkflowGroupMembers.AddRange(
                Member(grpId, 101),
                Member(grpId, 102),
                Member(grpId, 103));
        }
        await db.SaveChangesAsync();

        // ── GRPANY interface — 1 step, ANY_ONE mode ───────────────────────────
        var grpAnyDef = Def("GRPANY", "Group Any-One");
        db.WorkflowDefinitions.Add(grpAnyDef);
        await db.SaveChangesAsync();
        db.WorkflowSteps.Add(Step(grpAnyDef.Id, 1, "Group Approval", "GROUP", grpAny.Id, "ANY_ONE"));
        await db.SaveChangesAsync();

        // ── GRPALL interface — 1 step, ALL mode ──────────────────────────────
        var grpAllDef = Def("GRPALL", "Group All");
        db.WorkflowDefinitions.Add(grpAllDef);
        await db.SaveChangesAsync();
        db.WorkflowSteps.Add(Step(grpAllDef.Id, 1, "Group Approval", "GROUP", grpAll.Id, "ALL"));
        await db.SaveChangesAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static WorkflowDefinition Def(
        string interfaceCode, string name,
        string? condOp = null, decimal? condVal = null,
        decimal? condMin = null, decimal? condMax = null)
        => new()
        {
            UUID                       = Guid.NewGuid(),
            InterfaceCode              = interfaceCode,
            Name                       = name,
            Version                    = 1,
            IsActive                   = true,
            RequiresSequentialApproval = true,
            AllowRecall                = true,
            AllowReissue               = true,
            ConditionOperator          = condOp,
            ConditionValue             = condVal,
            ConditionValueMin          = condMin,
            ConditionValueMax          = condMax,
            CreatedBy                  = 0,
            CreatedDate                = DateTime.UtcNow
        };

    private static WorkflowStep Step(
        int definitionId, int stepNumber, string stepName,
        string approverType, int approverRefId,
        string approvalMode = "ANY_ONE")
        => new()
        {
            UUID          = Guid.NewGuid(),
            DefinitionId  = definitionId,
            StepNumber    = stepNumber,
            StepName      = stepName,
            StepType      = "APPROVAL",
            ApproverType  = approverType,
            ApproverRefId = approverRefId,
            IsMandatory   = true,
            ApprovalMode  = approvalMode,
            CanReject     = true,
            CreatedBy     = 0,
            CreatedDate   = DateTime.UtcNow
        };

    private static WorkflowGroupMember Member(int groupId, int userId)
        => new()
        {
            UUID      = Guid.NewGuid(),
            GroupId   = groupId,
            UserId    = userId,
            IsActive  = true,
            AddedBy   = 0,
            AddedDate = DateTime.UtcNow
        };
}

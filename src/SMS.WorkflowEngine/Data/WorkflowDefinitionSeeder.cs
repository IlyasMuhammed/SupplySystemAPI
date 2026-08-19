using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using SMS.Shared.Common;

namespace SMS.WorkflowEngine.Data;

using SMS.WorkflowEngine.Domain;

/// <summary>
/// Seeds default workflow definitions for PR, PO, GRN, and MIR — per organization (MT-006).
/// Idempotent per org — skips any interface code that already has an active definition with the
/// expected shape for that org. GRN migration: if a legacy 2-step GRN definition exists (QC + IM
/// combined), it is deactivated and replaced with separate GRN_QC (1-step QC team) + GRN (1-step IM)
/// definitions — legacy-data concern, so only ever run for the app-startup path (see SeedAsync),
/// never for a brand-new org, which never has legacy 2-step data to migrate.
/// </summary>
internal sealed class WorkflowDefinitionSeeder : IWorkflowSeedingService
{
    private readonly WorkflowDbContext _db;

    public WorkflowDefinitionSeeder(WorkflowDbContext db) => _db = db;

    // App-startup path — repairs/seeds SCM-DEMO's baseline definitions specifically, since that's
    // the one organization that pre-dates MT-006's per-org-creation-time seeding.
    public async Task SeedAsync()
    {
        await _db.Database.MigrateAsync();

        var scmDemoOrgId = TenantDefaults.ScmDemoOrganizationId;

        await SeedIfMissingAsync(scmDemoOrgId, "PR",  BuildPrDefinition);

        // PO migration: replace a legacy definition seeded before per-tier SkipCondition value
        // thresholds existed — under the old shape every tier was mandatory, so Finance/GM/Board
        // approval was always required regardless of order value.
        await MigratePoWorkflowAsync(scmDemoOrgId);
        await SeedIfMissingAsync(scmDemoOrgId, "PO",  BuildPoDefinition);

        // GRN has two separate workflows: GRN_QC (quality check) and GRN (final IM approval).
        await MigrateGrnWorkflowAsync(scmDemoOrgId);
        await SeedIfMissingAsync(scmDemoOrgId, "GRN_QC", BuildGrnQcDefinition);

        // MIR: PROJECT type uses NAMED approver for Project Manager; DEPARTMENT/MAINTENANCE use role-based only.
        await SeedIfMissingAsync(scmDemoOrgId, "MIR_PROJECT", BuildMirProjectDefinition);
        await SeedIfMissingAsync(scmDemoOrgId, "MIR_GENERAL", BuildMirGeneralDefinition);
    }

    // MT-006 — clones the same standard templates into any organization. Called by
    // TenancyRepository.CreateOrganizationWithAdminAsync for every newly created org, joined to
    // its shared transaction. Never runs the legacy GRN migration above: a brand-new org can never
    // have old 2-step GRN data, so a plain SeedIfMissingAsync for "GRN" is sufficient and correct.
    public async Task SeedDefaultWorkflowsAsync(Guid organizationId, DbTransaction? sharedTransaction)
    {
        if (sharedTransaction is not null)
        {
            _db.Database.SetDbConnection(sharedTransaction.Connection!, contextOwnsConnection: false);
            await _db.Database.UseTransactionAsync(sharedTransaction);
        }

        await SeedIfMissingAsync(organizationId, "PR",  BuildPrDefinition);
        await SeedIfMissingAsync(organizationId, "PO",  BuildPoDefinition);
        await SeedIfMissingAsync(organizationId, "GRN_QC", BuildGrnQcDefinition);
        await SeedIfMissingAsync(organizationId, "GRN",    BuildGrnImDefinition);
        await SeedIfMissingAsync(organizationId, "MIR_PROJECT", BuildMirProjectDefinition);
        await SeedIfMissingAsync(organizationId, "MIR_GENERAL", BuildMirGeneralDefinition);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task SeedIfMissingAsync(
        Guid organizationId, string interfaceCode, Func<Guid, DateTime, WorkflowDefinition> build)
    {
        // Explicit OrganizationId predicate, not just the ambient ITenantContext query filter:
        // both call sites run either with no HttpContext (app startup) or as a Super Admin creating
        // another org (ambient tenant = the Super Admin's own org) — in both cases the filter
        // bypasses rather than narrowing to `organizationId`, so this check must scope itself.
        bool exists = await _db.WorkflowDefinitions
            .Where(d => d.OrganizationId == organizationId && d.InterfaceCode == interfaceCode && d.IsActive)
            .AnyAsync();
        if (exists) return;

        _db.WorkflowDefinitions.Add(build(organizationId, DateTime.UtcNow));
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Deactivates the legacy 2-step GRN definition (QC + IM in one workflow) and
    /// ensures a 1-step IM-only "GRN" definition is active for the approval phase.
    /// </summary>
    private async Task MigrateGrnWorkflowAsync(Guid organizationId)
    {
        var now         = DateTime.UtcNow;
        var definitions = await _db.WorkflowDefinitions
            .Include(d => d.Steps)
            .Where(d => d.OrganizationId == organizationId && d.InterfaceCode == "GRN")
            .ToListAsync();

        bool hasSingleStepActive = false;

        foreach (var def in definitions.Where(d => d.IsActive))
        {
            if (def.Steps.Count > 1)
            {
                // Legacy combined QC + IM definition — deactivate it
                def.IsActive = false;
            }
            else
            {
                hasSingleStepActive = true;
            }
        }

        if (!hasSingleStepActive)
        {
            _db.WorkflowDefinitions.Add(BuildGrnImDefinition(organizationId, now));
        }

        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Deactivates a PO definition seeded before steps 2–4 carried a value-based SkipCondition
    /// (mirrors <see cref="MigrateGrnWorkflowAsync"/> for the equivalent legacy-GRN case). Under
    /// the old shape, Finance/GM/Board approval was unconditionally required for every PO — the
    /// "always four mandatory steps" symptom — instead of being skipped below their thresholds.
    /// In-flight approvals already resolved against the old definition are untouched; only future
    /// submissions pick up the corrected, conditional 4-tier definition.
    /// </summary>
    private async Task MigratePoWorkflowAsync(Guid organizationId)
    {
        var active = await _db.WorkflowDefinitions
            .Include(d => d.Steps)
            .FirstOrDefaultAsync(d => d.OrganizationId == organizationId && d.InterfaceCode == "PO" && d.IsActive);

        if (active is null) return; // nothing seeded yet — SeedIfMissingAsync will create the correct one

        bool isLegacy = active.Steps.Any(s => s.StepNumber >= 2 && string.IsNullOrWhiteSpace(s.SkipCondition));
        if (!isLegacy) return;

        active.IsActive = false;
        await _db.SaveChangesAsync();
    }

    // ── PR: 1 step — Procurement Manager ─────────────────────────────────────

    private static WorkflowDefinition BuildPrDefinition(Guid organizationId, DateTime now) => new()
    {
        UUID                       = Guid.NewGuid(),
        OrganizationId             = organizationId,
        InterfaceCode              = "PR",
        Name                       = "Purchase Requisition — Standard Approval",
        Description                = "Single-step approval by the Procurement Manager.",
        Version                    = 1,
        IsActive                   = true,
        RequiresSequentialApproval = true,
        AllowRecall                = true,
        AllowReissue               = true,
        CreatedBy                  = 1,
        CreatedDate                = now,
        Steps = new List<WorkflowStep>
        {
            Step(organizationId, now, 1, "Procurement Manager Review",
                 role: "PROCUREMENT_MANAGER", slaHours: 48)
        }
    };

    // ── PO: 4 tiers via SkipCondition on steps 2–4 ───────────────────────────

    private static WorkflowDefinition BuildPoDefinition(Guid organizationId, DateTime now) => new()
    {
        UUID                       = Guid.NewGuid(),
        OrganizationId             = organizationId,
        InterfaceCode              = "PO",
        Name                       = "Purchase Order — 4-Tier Approval",
        Description                = "Up to four approval tiers based on total order value. " +
                                     "Steps 2–4 are skipped automatically below their value thresholds.",
        Version                    = 1,
        IsActive                   = true,
        RequiresSequentialApproval = true,
        AllowRecall                = true,
        AllowReissue               = true,
        CreatedBy                  = 1,
        CreatedDate                = now,
        Steps = new List<WorkflowStep>
        {
            Step(organizationId, now, 1, "Procurement Manager Approval",
                 role: "PROCUREMENT_MANAGER", slaHours: 48),

            Step(organizationId, now, 2, "Finance Manager Approval",
                 role: "FINANCE_MANAGER", slaHours: 72,
                 skipCondition: "ConditionValue < 10000"),

            Step(organizationId, now, 3, "General Manager Approval",
                 role: "GENERAL_MANAGER", slaHours: 48,
                 skipCondition: "ConditionValue < 50000"),

            Step(organizationId, now, 4, "Executive / Board Approval",
                 role: "BOARD_MEMBER", slaHours: 72,
                 skipCondition: "ConditionValue < 200000")
        }
    };

    // ── GRN_QC: Quality Check workflow — QC team verifies goods ──────────────

    private static WorkflowDefinition BuildGrnQcDefinition(Guid organizationId, DateTime now) => new()
    {
        UUID                       = Guid.NewGuid(),
        OrganizationId             = organizationId,
        InterfaceCode              = "GRN_QC",
        Name                       = "GRN — Quality Check",
        Description                = "Quality Control team inspects received goods. " +
                                     "On approval, the GRN automatically advances to the Inventory Manager for final approval.",
        Version                    = 1,
        IsActive                   = true,
        RequiresSequentialApproval = true,
        AllowRecall                = true,
        AllowReissue               = false,
        CreatedBy                  = 1,
        CreatedDate                = now,
        Steps = new List<WorkflowStep>
        {
            Step(organizationId, now, 1, "QC Inspector Verification",
                 role: "QC_INSPECTOR", slaHours: 24)
        }
    };

    // ── GRN: Inventory Manager final approval — posts stock on approval ───────

    private static WorkflowDefinition BuildGrnImDefinition(Guid organizationId, DateTime now) => new()
    {
        UUID                       = Guid.NewGuid(),
        OrganizationId             = organizationId,
        InterfaceCode              = "GRN",
        Name                       = "GRN — Inventory Manager Approval",
        Description                = "Inventory Manager confirms receipt and approves stock posting. " +
                                     "Triggered automatically after QC workflow is approved.",
        Version                    = 1,
        IsActive                   = true,
        RequiresSequentialApproval = true,
        AllowRecall                = false,
        AllowReissue               = false,
        CreatedBy                  = 1,
        CreatedDate                = now,
        Steps = new List<WorkflowStep>
        {
            Step(organizationId, now, 1, "Inventory Manager Confirmation",
                 role: "INVENTORY_MANAGER", slaHours: 24)
        }
    };

    // ── MIR_PROJECT: 5 steps (Director skipped when < 50,000) ────────────────

    private static WorkflowDefinition BuildMirProjectDefinition(Guid organizationId, DateTime now) => new()
    {
        UUID                       = Guid.NewGuid(),
        OrganizationId             = organizationId,
        InterfaceCode              = "MIR_PROJECT",
        Name                       = "Material Issue Request — Project Type",
        Description                = "4-step chain: Line Manager → Dept Head → Project Manager (NAMED) → Store Manager. " +
                                     "Director step added when estimated value ≥ 50,000.",
        Version                    = 1,
        IsActive                   = true,
        RequiresSequentialApproval = true,
        AllowRecall                = true,
        AllowReissue               = false,
        CreatedBy                  = 1,
        CreatedDate                = now,
        Steps = new List<WorkflowStep>
        {
            StepEx(organizationId, now, 1, "Line Manager Approval",      "MANAGER", null,               slaHours: 48),
            StepEx(organizationId, now, 2, "Department Head Approval",   "ROLE",    "DEPARTMENT_HEAD",   slaHours: 48),
            StepEx(organizationId, now, 3, "Project Manager Approval",   "NAMED",   "PROJECT_MANAGER",   slaHours: 48),
            StepEx(organizationId, now, 4, "Store / Warehouse Manager",  "ROLE",    "WAREHOUSE_MANAGER", slaHours: 72),
            StepEx(organizationId, now, 5, "Director Approval",          "ROLE",    "DIRECTOR_APPROVER", slaHours: 72,
                   skipCondition: "ConditionValue < 50000")
        }
    };

    // ── MIR_GENERAL: 4 steps — DEPARTMENT or MAINTENANCE type ─────────────────

    private static WorkflowDefinition BuildMirGeneralDefinition(Guid organizationId, DateTime now) => new()
    {
        UUID                       = Guid.NewGuid(),
        OrganizationId             = organizationId,
        InterfaceCode              = "MIR_GENERAL",
        Name                       = "Material Issue Request — General (Dept/Maintenance)",
        Description                = "3-step chain: Line Manager → Dept Head → Store Manager. " +
                                     "Director step added when estimated value ≥ 50,000.",
        Version                    = 1,
        IsActive                   = true,
        RequiresSequentialApproval = true,
        AllowRecall                = true,
        AllowReissue               = false,
        CreatedBy                  = 1,
        CreatedDate                = now,
        Steps = new List<WorkflowStep>
        {
            StepEx(organizationId, now, 1, "Line Manager Approval",     "MANAGER", null,               slaHours: 48),
            StepEx(organizationId, now, 2, "Department Head Approval",  "ROLE",    "DEPARTMENT_HEAD",   slaHours: 48),
            StepEx(organizationId, now, 3, "Store / Warehouse Manager", "ROLE",    "WAREHOUSE_MANAGER", slaHours: 72),
            StepEx(organizationId, now, 4, "Director Approval",         "ROLE",    "DIRECTOR_APPROVER", slaHours: 72,
                   skipCondition: "ConditionValue < 50000")
        }
    };

    // ── Factory ───────────────────────────────────────────────────────────────

    private static WorkflowStep Step(
        Guid     organizationId,
        DateTime now,
        int      stepNumber,
        string   stepName,
        string   role,
        int      slaHours,
        string?  skipCondition = null) => new()
    {
        UUID             = Guid.NewGuid(),
        OrganizationId   = organizationId,
        StepNumber       = stepNumber,
        StepName         = stepName,
        StepType         = "APPROVAL",
        ApproverType     = "ROLE",
        ApproverRole     = role,
        IsMandatory      = true,
        SlaHoursOverride = slaHours,
        SkipCondition    = skipCondition,
        ApprovalMode     = "ANY_ONE",
        CanReject        = true,
        CreatedBy        = 1,
        CreatedDate      = now
    };

    private static WorkflowStep StepEx(
        Guid     organizationId,
        DateTime now,
        int      stepNumber,
        string   stepName,
        string   approverType,
        string?  role,
        int      slaHours,
        string?  skipCondition = null) => new()
    {
        UUID             = Guid.NewGuid(),
        OrganizationId   = organizationId,
        StepNumber       = stepNumber,
        StepName         = stepName,
        StepType         = "APPROVAL",
        ApproverType     = approverType,
        ApproverRole     = role,
        IsMandatory      = true,
        SlaHoursOverride = slaHours,
        SkipCondition    = skipCondition,
        ApprovalMode     = "ANY_ONE",
        CanReject        = true,
        CreatedBy        = 1,
        CreatedDate      = now
    };
}

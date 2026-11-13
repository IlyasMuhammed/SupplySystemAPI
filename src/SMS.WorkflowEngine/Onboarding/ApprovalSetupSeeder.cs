using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SMS.Shared.Common;
using SMS.Shared.Onboarding;
using SMS.WorkflowEngine.Data;
using SMS.WorkflowEngine.Domain;
using SMS.WorkflowEngine.Services;

namespace SMS.WorkflowEngine.Onboarding;

/// <summary>
/// A50 D-18 / D-58 / D-91 / D-96 — APPROVAL_RULES (per rule: PO needs MODULE_DEMAND, ExpenseClaim needs MODULE_HRM). Payload <c>{ rules: [{ documentType, interfaceCode?, afterA49?,
/// levels: [{ level, minAmount, maxAmount|null, approverRole, label, slaHours? }] }] }</c>.
/// <list type="bullet">
/// <item>PurchaseOrder: the organization's active unconditional <c>PO</c> definition gets one ROLE step per level, in level
/// order. Level 1 always applies; level n &gt; 1 is skipped below its minAmount (<c>ConditionValue &lt; minAmount</c>, the
/// engine's existing skip rule). Amounts are in the organization's base currency, as PO ConditionValue is.</item>
/// <item>ExpenseClaim (D-91, after A49): the same for the active unconditional <c>EXPENSE_CLAIM</c> definition, with one difference.
/// Level 1 is the HRM NAMED approver step (<see cref="HrmWorkflowCodes.NamedApprover"/>: the employee's line manager, else the
/// department head, else a holder of the approving permission), because HRM supplies that user at submit time and the engine cannot
/// resolve an HR role from the role map; level 1's <c>approverRole</c> is informational. Levels above 1 are ROLE steps and must
/// resolve. Applies only while MODULE_HRM is on for the organization. A missing definition is created through
/// <see cref="IWorkflowSeedingService.SeedHrmWorkflowsAsync"/> (HRM creates the same on first use), so the wizard works before HRM
/// has been opened.</item>
/// <item>Only while the definition is <b>unedited</b>: its step fingerprint equals the one that is seeded at creation
/// (<see cref="DefaultPoFingerprint"/> / <see cref="DefaultExpenseFingerprint"/>) or the one this seeder last wrote. An edited definition
/// is left alone and reported (Merge = no-op). Overwrite applies when this seeder wrote it; Full applies regardless. Merge-safe, as PO.</item>
/// <item>An approverRole the engine cannot resolve (ApproverResolutionService's role-code map) refuses the rule: a step
/// nobody can approve would block every document.</item>
/// <item>Other document types are reported unsupported.</item>
/// <item>Steps are rewritten in place: in-flight approvals keep their own step rows (DocumentApprovalStep), so nothing
/// references the definition's steps — there is no reference block.</item>
/// </list>
/// </summary>
internal sealed class ApprovalSetupSeeder : ISetupCategorySeeder
{
    internal const string PoKey = "PurchaseOrder";
    internal const string ExpenseKey = "ExpenseClaim";
    private const string PoInterface = "PO";

    private readonly WorkflowDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IWorkflowSeedingService? _seeding;
    private readonly IModuleGate? _gate;

    public ApprovalSetupSeeder(WorkflowDbContext db, ITenantContext tenant, IWorkflowSeedingService? seeding = null, IModuleGate? gate = null)
    {
        _db = db;
        _tenant = tenant;
        _seeding = seeding;
        _gate = gate;
    }

    public string Category => SetupCategories.ApprovalRules;
    /// <summary>
    /// D-96: no category-level module. Each rule has its own: PO tiers need MODULE_DEMAND, the EXPENSE_CLAIM rule needs MODULE_HRM only, so an
    /// organization with HRM and no Demand still gets its expense rule. A rule whose module is off is skipped with a warning.
    /// </summary>
    public string? RequiresModule => null;

    internal static string ModuleOf(bool isExpense) => isExpense ? ModuleCodes.Hrm : ModuleCodes.Demand;

    private Guid Org => _tenant.OrganizationId;

    internal sealed record StepShape(int Number, string Name, string Type, string ApproverType, int? RefId, string? Role, int? Sla,
        string? Skip, bool Mandatory, string Mode);

    /// <summary>What WorkflowDefinitionSeeder.BuildPoDefinition seeds for every new organization (kept in step by a unit test).</summary>
    internal static readonly IReadOnlyList<StepShape> DefaultPoSteps =
    [
        new(1, "Procurement Manager Approval", "APPROVAL", "ROLE", null, "PROCUREMENT_MANAGER", 48, null, true, "ANY_ONE"),
        new(2, "Finance Manager Approval",     "APPROVAL", "ROLE", null, "FINANCE_MANAGER",     72, "ConditionValue < 10000", true, "ANY_ONE"),
        new(3, "General Manager Approval",     "APPROVAL", "ROLE", null, "GENERAL_MANAGER",     48, "ConditionValue < 50000", true, "ANY_ONE"),
        new(4, "Executive / Board Approval",   "APPROVAL", "ROLE", null, "BOARD_MEMBER",        72, "ConditionValue < 200000", true, "ANY_ONE"),
    ];

    /// <summary>What WorkflowDefinitionSeeder.BuildHrmDefinition seeds for EXPENSE_CLAIM (kept in step by a unit test).</summary>
    internal static readonly IReadOnlyList<StepShape> DefaultExpenseSteps =
    [
        new(1, "HR Approval", "APPROVAL", "NAMED", null, HrmWorkflowCodes.NamedApprover, 48, null, true, "ANY_ONE"),
    ];

    /// <summary>One document kind this seeder can configure.</summary>
    private sealed record Kind(string Key, string Interface, IReadOnlyList<StepShape> Default, bool IsExpense);

    private static readonly Kind Po = new(PoKey, PoInterface, DefaultPoSteps, false);
    private static readonly Kind Expense = new(ExpenseKey, HrmWorkflowCodes.ExpenseClaim, DefaultExpenseSteps, true);

    internal static string Fingerprint(IEnumerable<StepShape> steps) =>
        SetupFingerprint.Of(steps.OrderBy(s => s.Number).Select(s => (object?)string.Join("|",
            s.Number, s.Name.Trim(), s.Type, s.ApproverType, s.RefId?.ToString(CultureInfo.InvariantCulture) ?? "-", s.Role ?? "-",
            s.Sla?.ToString(CultureInfo.InvariantCulture) ?? "-", s.Skip ?? "-", s.Mandatory ? "1" : "0", s.Mode)).ToArray());

    internal static string DefaultPoFingerprint => Fingerprint(DefaultPoSteps);
    internal static string DefaultExpenseFingerprint => Fingerprint(DefaultExpenseSteps);

    private static StepShape Shape(WorkflowStep s) =>
        new(s.StepNumber, s.StepName, s.StepType, s.ApproverType, s.ApproverRefId, s.ApproverRole, s.SlaHoursOverride, s.SkipCondition,
            s.IsMandatory, s.ApprovalMode);

    // ── Payload ─────────────────────────────────────────────────────────────────

    private sealed record Rule(string DocumentType, string? InterfaceCode, JsonElement Element, Kind? Kind);

    private static Kind? KindOf(string documentType, string? interfaceCode)
    {
        if (string.Equals(documentType, ExpenseKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(interfaceCode, HrmWorkflowCodes.ExpenseClaim, StringComparison.OrdinalIgnoreCase)) return Expense;
        if (string.Equals(documentType, PoKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(interfaceCode, PoInterface, StringComparison.OrdinalIgnoreCase)) return Po;
        return null;
    }

    private static List<Rule> Rules(JsonElement payload) =>
        SetupJson.Array(payload, "rules").Select(r =>
        {
            var type = SetupJson.Text(r, "documentType") ?? string.Empty;
            var iface = SetupJson.Text(r, "interfaceCode");
            return new Rule(type, iface, r, KindOf(type, iface));
        }).ToList();

    /// <summary>The steps a PO rule asks for, or null with the reason it cannot be applied.</summary>
    internal static (List<StepShape>? Steps, string? Problem) Target(JsonElement rule) => Target(rule, Po);

    private static (List<StepShape>? Steps, string? Problem) Target(JsonElement rule, Kind kind)
    {
        var levels = SetupJson.Array(rule, "levels")
            .Select((l, i) => (Level: SetupJson.Int(l, "level") ?? i + 1, Min: SetupJson.Decimal(l, "minAmount") ?? 0m,
                Role: (SetupJson.Text(l, "approverRole") ?? string.Empty).ToUpperInvariant(), Label: SetupJson.Text(l, "label"),
                Sla: SetupJson.Int(l, "slaHours")))
            .OrderBy(l => l.Level).ThenBy(l => l.Min).ToList();
        if (levels.Count == 0) return (null, "no levels.");
        var steps = new List<StepShape>();
        for (var i = 0; i < levels.Count; i++)
        {
            var l = levels[i];
            // An expense claim's first step is the HRM named approver, which the engine does not resolve from a role.
            var named = kind.IsExpense && i == 0;
            if (!named)
            {
                if (l.Role.Length == 0) return (null, $"level {l.Level} has no approverRole.");
                if (ApproverResolutionService.ResolveLegacyRoleId(l.Role) is null)
                    return (null, $"level {l.Level}: approver role '{l.Role}' is not one the workflow engine can resolve.");
            }
            if (i > 0 && l.Min <= levels[i - 1].Min) return (null, $"level {l.Level}: minAmount must be above the previous level's.");
            // Level 1 always applies — the engine needs at least one step (an all-skipped approval would stay PENDING).
            var skip = i == 0 || l.Min <= 0 ? null : $"ConditionValue < {l.Min.ToString("0.##", CultureInfo.InvariantCulture)}";
            var name = string.IsNullOrWhiteSpace(l.Label) ? (named ? "HR Approval" : $"Level {i + 1} Approval") : l.Label!;
            steps.Add(named
                ? new StepShape(1, name.Length > 200 ? name[..200] : name, "APPROVAL", "NAMED", null, HrmWorkflowCodes.NamedApprover, l.Sla ?? 48, null, true, "ANY_ONE")
                : new StepShape(i + 1, name.Length > 200 ? name[..200] : name, "APPROVAL", "ROLE", null, l.Role, l.Sla ?? 48, skip, true, "ANY_ONE"));
        }
        return (steps, null);
    }

    public IReadOnlyList<string> ValidatePayload(JsonElement payload)
    {
        if (SetupJson.Prop(payload, "rules") is not { ValueKind: JsonValueKind.Array })
            return ["rules: an array is required."];
        var errors = new List<string>();
        foreach (var r in Rules(payload).Where(r => r.Kind is not null))
            if (Target(r.Element, r.Kind!).Problem is { } p) errors.Add($"{r.DocumentType}: {p}");
        return errors;
    }

    private Task<WorkflowDefinition?> DefinitionAsync(Kind kind, CancellationToken ct) =>
        _db.WorkflowDefinitions.IgnoreQueryFilters().Include(d => d.Steps)
            .Where(d => d.OrganizationId == Org && d.InterfaceCode == kind.Interface && d.IsActive && d.ConditionOperator == null)
            .OrderBy(d => d.Id).FirstOrDefaultAsync(ct);

    private async Task<bool> ModuleOnAsync(bool isExpense, CancellationToken ct) =>
        _gate is null || await _gate.IsEnabledAsync(Org, ModuleOf(isExpense), ct);

    private static List<string> UnsupportedWarnings(IEnumerable<Rule> rules) =>
        rules.Where(r => r.Kind is null).Select(r => $"{r.DocumentType}: not supported; only PurchaseOrder and ExpenseClaim rules are applied.").ToList();

    /// <summary>The rules that apply (one per kind; the first wins), with expense dropped while HRM is off.</summary>
    private async Task<(List<(Kind Kind, Rule Rule)> Applicable, List<string> Warnings)> PlanAsync(JsonElement payload, CancellationToken ct)
    {
        var rules = Rules(payload);
        var warnings = UnsupportedWarnings(rules);
        var plan = new List<(Kind, Rule)>();
        foreach (var kind in new[] { Po, Expense })
        {
            var rule = rules.FirstOrDefault(r => r.Kind == kind);
            if (rule is null) continue;
            if (!await ModuleOnAsync(kind.IsExpense, ct))
            {
                warnings.Add(kind.IsExpense
                    ? "ExpenseClaim: skipped — the HRM module is not enabled for this organization."
                    : "PurchaseOrder: skipped — the Demand module is not enabled for this organization.");
                continue;
            }
            plan.Add((kind, rule));
        }
        return (plan, warnings);
    }

    // ── Preview ─────────────────────────────────────────────────────────────────

    public async Task<SetupPreview> PreviewAsync(JsonElement payload, SetupSeedContext context, CancellationToken ct = default)
    {
        var (plan, warnings) = await PlanAsync(payload, ct);
        var present = 0;
        foreach (var (kind, rule) in plan)
        {
            var (target, problem) = Target(rule.Element, kind);
            var def = await DefinitionAsync(kind, ct);
            if (problem is not null) warnings.Add($"{rule.DocumentType}: {problem}");
            else if (def is null)
            {
                if (!kind.IsExpense) warnings.Add("PurchaseOrder: the organization has no active PO approval workflow.");
            }
            else
            {
                var current = Fingerprint(def.Steps.Select(Shape));
                if (current == Fingerprint(target!)) present++;
                else if (!Unedited(kind, current, context)) warnings.Add($"{kind.Key}: the approval workflow was edited; it will be left as it is.");
            }
        }
        return new SetupPreview { Category = Category, InPayload = plan.Count, AlreadyPresent = present, Warnings = warnings };
    }

    private static bool Unedited(Kind kind, string currentFingerprint, SetupSeedContext context) =>
        currentFingerprint == Fingerprint(kind.Default)
        || context.AlreadySeeded.Any(s => s.NaturalKey == kind.Key && s.Fingerprint == currentFingerprint);

    // ── Apply / reset ───────────────────────────────────────────────────────────

    public Task<SetupApplyResult> ApplyAsync(JsonElement payload, SetupSeedContext context, CancellationToken ct = default) =>
        SetupUnitOfWork.RunAsync(_db, () => SeedAsync(payload, context, SetupResetMode.Merge, ct), ct);

    public Task<SetupApplyResult> ResetAsync(JsonElement payload, SetupResetMode mode, SetupSeedContext context, CancellationToken ct = default) =>
        SetupUnitOfWork.RunAsync(_db, () => SeedAsync(payload, context, mode, ct), ct);

    private async Task<SetupApplyResult> SeedAsync(JsonElement payload, SetupSeedContext context, SetupResetMode mode, CancellationToken ct)
    {
        var (plan, warnings) = await PlanAsync(payload, ct);
        int created = 0, skipped = 0;
        var seeded = new List<SeededRecordRef>();

        foreach (var (kind, rule) in plan)
        {
            var one = await SeedOneAsync(kind, rule, context, mode, warnings, ct);
            created += one.Created;
            skipped += one.Skipped;
            seeded.AddRange(one.Seeded);
        }
        return new SetupApplyResult { Category = Category, Created = created, Skipped = skipped, Seeded = seeded, Warnings = warnings };
    }

    private async Task<(int Created, int Skipped, List<SeededRecordRef> Seeded)> SeedOneAsync(
        Kind kind, Rule rule, SetupSeedContext context, SetupResetMode mode, List<string> warnings, CancellationToken ct)
    {
        var (target, problem) = Target(rule.Element, kind);
        if (problem is not null)
        {
            warnings.Add($"{rule.DocumentType}: not applied — {problem}");
            return (0, 1, []);
        }
        var def = await DefinitionAsync(kind, ct);
        if (def is null && kind.IsExpense && _seeding is not null)
        {
            // HRM creates its three definitions on first use; creating them now means the wizard does not depend on that order.
            await _seeding.SeedHrmWorkflowsAsync(Org);
            def = await DefinitionAsync(kind, ct);
        }
        if (def is null)
        {
            warnings.Add(kind.IsExpense
                ? "ExpenseClaim: not applied — the organization has no active EXPENSE_CLAIM approval workflow yet (HRM creates it on first use; apply again after that)."
                : "PurchaseOrder: not applied — the organization has no active PO approval workflow.");
            return (0, 1, []);
        }

        var current = Fingerprint(def.Steps.Select(Shape));
        var wanted = Fingerprint(target!);
        var ours = context.AlreadySeeded.Any(s => s.NaturalKey == kind.Key);
        var seededRef = new SeededRecordRef(kind.Key, def.UUID.ToString(), wanted);

        if (current == wanted) return (0, 1, [seededRef]);

        var allowed = mode switch
        {
            SetupResetMode.Full      => true,
            SetupResetMode.Overwrite => ours || Unedited(kind, current, context),
            _                        => Unedited(kind, current, context)
        };
        if (!allowed)
        {
            warnings.Add($"{kind.Key}: the approval workflow was edited by the organization; it was left as it is.");
            return (0, 1, ours ? [context.AlreadySeeded.First(s => s.NaturalKey == kind.Key)] : []);
        }

        var now = DateTime.UtcNow;
        var userId = context.UserId ?? 1;
        _db.WorkflowSteps.RemoveRange(def.Steps.ToList());
        def.Steps.Clear();
        foreach (var s in target!)
            def.Steps.Add(new WorkflowStep
            {
                UUID = Guid.NewGuid(), OrganizationId = Org, StepNumber = s.Number, StepName = s.Name, StepType = s.Type,
                ApproverType = s.ApproverType, ApproverRole = s.Role, IsMandatory = s.Mandatory, SlaHoursOverride = s.Sla,
                SkipCondition = s.Skip, ApprovalMode = s.Mode, CanReject = true, IsFinalStep = s.Number == target.Count,
                CreatedBy = userId, CreatedDate = now
            });
        def.Description = kind.IsExpense
            ? $"{target.Count} approval step(s) from the setup defaults: the employee's approver first, then finance for larger claims."
            : $"{target.Count} approval tier(s) from the setup defaults. Tiers after the first are skipped below their amounts.";
        def.ModifiedBy = userId;
        def.ModifiedDate = now;
        await _db.SaveChangesAsync(ct);

        return (1, 0, [seededRef]);
    }

    // ── Status / references ─────────────────────────────────────────────────────

    public async Task<SetupCategoryStatus> GetStatusAsync(SetupSeedContext context, CancellationToken ct = default)
    {
        var modified = new List<string>();
        var missing = new List<string>();
        var current = 0;
        foreach (var kind in new[] { Po, Expense })
        {
            var def = await DefinitionAsync(kind, ct);
            if (def is not null) current++;
            var seeded = context.AlreadySeeded.FirstOrDefault(s => s.NaturalKey == kind.Key);
            if (seeded is null) continue;
            if (def is null || def.UUID.ToString() != seeded.EntityRef) missing.Add(kind.Key);
            else if (Fingerprint(def.Steps.Select(Shape)) != seeded.Fingerprint) modified.Add(kind.Key);
        }
        return new SetupCategoryStatus
        {
            Category = Category, SeededCount = context.AlreadySeeded.Count, CurrentCount = current,
            ModifiedKeys = modified, MissingKeys = missing
        };
    }

    /// <summary>Never blocks: a submitted document keeps its own copy of the steps it was routed through.</summary>
    public Task<SetupReferenceCheck> CheckReferencesAsync(SetupSeedContext context, CancellationToken ct = default) =>
        Task.FromResult(new SetupReferenceCheck());
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Onboarding.Data;
using SMS.Modules.Onboarding.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Onboarding.Checklist;

/// <summary>A50 D-28 / FSD §4.4 IOnboardingService, for the ambient organization (the job passes its own).</summary>
public interface IOnboardingChecklistService
{
    /// <summary>Seeds (lazily, D-72), evaluates the signals and returns the status.</summary>
    Task<OnboardingChecklistModel> GetStatusAsync(CancellationToken ct = default);
    Task<OnboardingChecklistModel> CompleteStepAsync(string stepCode, int? userId, CancellationToken ct = default);
    Task<OnboardingChecklistModel> DismissAsync(int? userId, CancellationToken ct = default);
    /// <summary>The checklist with every catalog step; idempotent. Explicit organization (provisioning, the job).</summary>
    Task SeedChecklistAsync(Guid organizationId, CancellationToken ct = default);
    /// <summary>Asks the signals about the steps still open and persists every tick. Never unticks. Returns how many ticked.</summary>
    Task<int> EvaluateAsync(Guid organizationId, CancellationToken ct = default);
    /// <summary>A tick from a module hook (after commit). Unknown codes are ignored.</summary>
    Task<bool> MarkCompletedAsync(Guid organizationId, string stepCode, bool auto, int? userId, CancellationToken ct = default);
}

/// <summary>
/// D-28. Steps are persisted per organization; a step, once completed, is never unticked (BR-50-014). Auto-completion has no
/// event bus to listen to, so each module's <see cref="IOnboardingSignal"/> is asked on read, by the 10-minute job and, for
/// customers / vendors / products, at once through the "onboarding" entity sync target. Demo records never count.
/// </summary>
internal sealed class OnboardingChecklistService : IOnboardingChecklistService
{
    private readonly OnboardingDbContext _db;
    private readonly IReadOnlyList<IOnboardingSignal> _signals;
    private readonly IModuleGate? _gate;
    private readonly ILogger _log;
    private readonly TimeProvider _clock;

    public OnboardingChecklistService(OnboardingDbContext db, IEnumerable<IOnboardingSignal> signals, IModuleGate? gate = null,
        ILogger<OnboardingChecklistService>? log = null, TimeProvider? clock = null)
    {
        _db = db;
        _signals = signals.ToList();
        _gate = gate;
        _log = log ?? (ILogger)NullLogger.Instance;
        _clock = clock ?? TimeProvider.System;
    }

    private Guid OrgId => _db.TenantContext.OrganizationId;
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public async Task<OnboardingChecklistModel> GetStatusAsync(CancellationToken ct = default)
    {
        var org = RequireOrg();
        await SeedChecklistAsync(org, ct);
        await EvaluateAsync(org, ct);
        return await BuildAsync(org, ct);
    }

    public async Task<OnboardingChecklistModel> CompleteStepAsync(string stepCode, int? userId, CancellationToken ct = default)
    {
        var org = RequireOrg();
        if (ChecklistCatalog.Find(stepCode) is null) throw new NotFoundException("Onboarding step", stepCode);
        await SeedChecklistAsync(org, ct);
        await MarkCompletedAsync(org, stepCode, auto: false, userId, ct);
        return await BuildAsync(org, ct);
    }

    public async Task<OnboardingChecklistModel> DismissAsync(int? userId, CancellationToken ct = default)
    {
        var org = RequireOrg();
        await SeedChecklistAsync(org, ct);
        var checklist = await _db.OnboardingChecklists.IgnoreQueryFilters().FirstAsync(c => c.OrganizationId == org, ct);
        if (!checklist.Dismissed)
        {
            checklist.Dismissed = true;
            checklist.DismissedAt = Now;
            checklist.DismissedBy = userId;
            await _db.SaveChangesAsync(ct);
        }
        return await BuildAsync(org, ct);
    }

    public async Task SeedChecklistAsync(Guid organizationId, CancellationToken ct = default)
    {
        if (organizationId == Guid.Empty) return;
        try
        {
            var checklist = await _db.OnboardingChecklists.IgnoreQueryFilters().Include(c => c.Steps)
                .FirstOrDefaultAsync(c => c.OrganizationId == organizationId, ct);
            if (checklist is null)
            {
                // REV50-4: an organization that was live before A50 (setup note LEGACY) gets its checklist already dismissed.
                // It is still kept and ticked, but a long-running customer never sees "Getting started".
                var legacy = await _db.OrganizationSetups.IgnoreQueryFilters()
                    .AnyAsync(s => s.OrganizationId == organizationId && s.Note == SetupCodes.LegacyNote, ct);
                checklist = new OnboardingChecklist { OrganizationId = organizationId, CreatedAt = Now };
                if (legacy) { checklist.Dismissed = true; checklist.DismissedAt = Now; }
                _db.OnboardingChecklists.Add(checklist);
            }

            var changed = _db.Entry(checklist).State == EntityState.Added;
            foreach (var def in ChecklistCatalog.Steps)
            {
                var step = checklist.Steps.FirstOrDefault(s => string.Equals(s.StepCode, def.StepCode, StringComparison.OrdinalIgnoreCase));
                if (step is null)
                {
                    checklist.Steps.Add(new OnboardingChecklistStep
                    {
                        OrganizationId = organizationId, StepCode = def.StepCode, Title = def.Title, Link = def.Link,
                        RequiresModule = def.RequiresModule, SortOrder = def.SortOrder
                    });
                    changed = true;
                }
                else if (step.Title != def.Title || step.Link != def.Link || step.RequiresModule != def.RequiresModule || step.SortOrder != def.SortOrder)
                {
                    // The catalog is the source of the wording and links; the tick is the organization's.
                    step.Title = def.Title; step.Link = def.Link; step.RequiresModule = def.RequiresModule; step.SortOrder = def.SortOrder;
                    changed = true;
                }
            }
            if (changed) await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Seeded concurrently by another request (PK / unique step code): theirs stands.
            _log.LogDebug(ex, "Onboarding checklist for {OrganizationId} was seeded concurrently.", organizationId);
            _db.ChangeTracker.Clear();
        }
    }

    public async Task<int> EvaluateAsync(Guid organizationId, CancellationToken ct = default)
    {
        if (organizationId == Guid.Empty) return 0;
        var steps = await _db.OnboardingChecklistSteps.IgnoreQueryFilters()
            .Where(s => s.OrganizationId == organizationId && s.CompletedAt == null).ToListAsync(ct);
        if (steps.Count == 0) return 0;

        var open = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in steps)
            if (s.RequiresModule is null || await _gate.IsOnAsync(organizationId, s.RequiresModule, ct)) open.Add(s.StepCode);
        if (open.Count == 0) return 0;

        var demoIds = await DemoIdsAsync(organizationId, ct);
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var signal in _signals)
        {
            if (!signal.StepCodes.Any(c => open.Contains(c) && !done.Contains(c))) continue;
            try
            {
                foreach (var code in await signal.CompletedAsync(demoIds, ct))
                    if (open.Contains(code)) done.Add(code);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One module's signal failing must not hide the checklist; the next read or job run asks again.
                _log.LogWarning(ex, "Onboarding signal {Signal} failed for {OrganizationId}.", signal.GetType().Name, organizationId);
            }
        }
        if (done.Count == 0) return 0;

        var now = Now;
        foreach (var s in steps.Where(s => done.Contains(s.StepCode)))
        {
            s.CompletedAt = now;
            s.AutoCompleted = true;
        }
        await _db.SaveChangesAsync(ct);
        return done.Count;
    }

    public async Task<bool> MarkCompletedAsync(Guid organizationId, string stepCode, bool auto, int? userId, CancellationToken ct = default)
    {
        if (organizationId == Guid.Empty || ChecklistCatalog.Find(stepCode) is not { } def) return false;
        await SeedChecklistAsync(organizationId, ct);
        var step = await _db.OnboardingChecklistSteps.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.OrganizationId == organizationId && s.StepCode == def.StepCode, ct);
        if (step is null || step.CompletedAt is not null) return false;
        step.CompletedAt = Now;
        step.CompletedBy = userId;
        step.AutoCompleted = auto;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<OnboardingChecklistModel> BuildAsync(Guid org, CancellationToken ct)
    {
        var checklist = await _db.OnboardingChecklists.IgnoreQueryFilters().AsNoTracking().Include(c => c.Steps)
            .FirstOrDefaultAsync(c => c.OrganizationId == org, ct);
        var model = new OnboardingChecklistModel { Dismissed = checklist?.Dismissed ?? false, DismissedAt = checklist?.DismissedAt };
        foreach (var s in (checklist?.Steps ?? []).OrderBy(s => s.SortOrder).ThenBy(s => s.StepCode))
        {
            if (s.RequiresModule is not null && !await _gate.IsOnAsync(org, s.RequiresModule, ct)) continue;
            model.Steps.Add(new OnboardingChecklistStepModel
            {
                StepCode = s.StepCode, Title = s.Title, Description = ChecklistCatalog.Find(s.StepCode)?.Description, Link = s.Link,
                SortOrder = s.SortOrder, RequiresModule = s.RequiresModule,
                Completed = s.CompletedAt is not null, CompletedAt = s.CompletedAt, AutoCompleted = s.AutoCompleted
            });
        }
        model.TotalCount = model.Steps.Count;
        model.CompletedCount = model.Steps.Count(s => s.Completed);
        model.IsComplete = model.TotalCount > 0 && model.CompletedCount == model.TotalCount;
        return model;
    }

    private async Task<IReadOnlySet<Guid>> DemoIdsAsync(Guid org, CancellationToken ct)
    {
        var refs = await _db.DemoRecords.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.OrganizationId == org).Select(r => r.EntityRef).ToListAsync(ct);
        var ids = new HashSet<Guid>();
        foreach (var r in refs) if (Guid.TryParse(r, out var g)) ids.Add(g);
        return ids;
    }

    private Guid RequireOrg() =>
        OrgId == Guid.Empty ? throw new BadRequestException("The onboarding checklist belongs to an organization; sign in to one.") : OrgId;
}

/// <summary>COMPLETE_SETUP: the org's setup is Completed or Skipped (D-6). CORE's apply also ticks it at once.</summary>
internal sealed class SetupCompletedSignal : IOnboardingSignal
{
    private readonly OnboardingDbContext _db;
    public SetupCompletedSignal(OnboardingDbContext db) => _db = db;

    public IReadOnlyCollection<string> StepCodes { get; } = [OnboardingStepCodes.CompleteSetup];

    public async Task<IReadOnlySet<string>> CompletedAsync(IReadOnlySet<Guid> demoEntityIds, CancellationToken ct = default)
    {
        var org = _db.TenantContext.OrganizationId;
        var done = await _db.OrganizationSetups.IgnoreQueryFilters().AnyAsync(s => s.OrganizationId == org
            && (s.SetupStatus == SetupStatuses.Completed || s.SetupStatus == SetupStatuses.Skipped), ct);
        return done ? new HashSet<string> { OnboardingStepCodes.CompleteSetup } : new HashSet<string>();
    }
}

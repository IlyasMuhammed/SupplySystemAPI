using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Tenancy.Data;
using SMS.Modules.Tenancy.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Tenancy.Services;

/// <summary>
/// A37 MOD-05 / D-7 — daily: a module (or BOM management with it) whose grace period has ended loses it — the row keeps
/// DisabledAt, so the organization still reads its records (D-6) but can no longer change them — and the history gets
/// GRACE_EXPIRED by the system. The filter already treats an expired grace as read-only between runs; this makes the
/// stored state and the module cards say so too. Every organization in one pass (tenant tables have no query filter).
/// </summary>
internal sealed class ModuleGraceExpiryJob
{
    internal const string RecurringJobId = "tenant.module-grace-expiry";

    private readonly TenancyDbContext _db;
    private readonly ITenantSnapshotProvider _snapshots;
    private readonly ILogger<ModuleGraceExpiryJob>? _log;
    private readonly TimeProvider _clock;

    public ModuleGraceExpiryJob(
        TenancyDbContext db, ITenantSnapshotProvider snapshots, ILogger<ModuleGraceExpiryJob>? log = null, TimeProvider? clock = null)
    {
        _db = db;
        _snapshots = snapshots;
        _log = log;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>00:15 UTC daily.</summary>
    internal static void Schedule() =>
        RecurringJob.AddOrUpdate<ModuleGraceExpiryJob>(RecurringJobId, job => job.RunAsync(), "15 0 * * *");

    [AutomaticRetry(Attempts = 3)]
    public async Task RunAsync()
    {
        var cleared = await SweepAsync();
        if (cleared > 0) _log?.LogInformation("Module grace expiry: {Count} grace period(s) ended.", cleared);
    }

    internal async Task<int> SweepAsync()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var expired = await _db.OrganizationFeatures
            .Where(f => !f.IsEnabled && f.GracePeriodEndsAt != null && f.GracePeriodEndsAt <= now)
            .Join(_db.FeatureDefinitions, f => f.FeatureDefinitionId, d => d.Id, (f, d) => new { Row = f, d.FeatureCode })
            .ToListAsync();

        foreach (var e in expired)
        {
            e.Row.GracePeriodEndsAt = null;
            e.Row.ModifiedBy = null;
            e.Row.ModifiedDate = now;
            _db.OrganizationFeatureHistory.Add(new OrganizationFeatureHistory
            {
                Id = Guid.NewGuid(), OrganizationId = e.Row.OrganizationId, FeatureCode = e.FeatureCode,
                Action = FeatureHistoryActions.GraceExpired, PerformedBy = null, PerformedAt = now
            });
        }

        await _db.SaveChangesAsync();
        foreach (var orgId in expired.Select(e => e.Row.OrganizationId).Distinct())
            _snapshots.Invalidate(orgId);
        return expired.Count;
    }
}

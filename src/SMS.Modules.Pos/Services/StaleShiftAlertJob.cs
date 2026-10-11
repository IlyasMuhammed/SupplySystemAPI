using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Pos.Data;
using SMS.Modules.Pos.Domain;
using SMS.Modules.Pos.Hubs;
using SMS.Shared.Common;

namespace SMS.Modules.Pos.Services;

/// <summary>
/// A38 E4 / D-5 — hourly: a shift still OPEN/CLOSING longer than its store's <c>AutoCloseShiftHours</c> is reported once
/// (StaleAlertedAt) to the store's SUPERVISOR staff by in-app notification, and to the terminal as a ShiftAlert. Nothing is
/// closed automatically (counting the drawer needs a person). Per organization with MODULE_POS
/// (<see cref="ModuleGateExtensions.RunForEnabledOrganizationsAsync"/>); recipients are resolved here, per store —
/// never CreateForRoleAsync / BroadcastAsync, which ignore the organization (R-1).
/// </summary>
internal sealed class StaleShiftAlertJob
{
    internal const string RecurringJobId = "pos-stale-shift-alert";
    internal const string Cron = "5 * * * *";
    internal const string NotificationType = "POS_STALE_SHIFT";

    private readonly PosDbContext          _db;
    private readonly INotificationService? _notifications;
    private readonly IPosHubNotifier?      _hub;
    private readonly IModuleGate?          _gate;
    private readonly IOrganizationDirectory? _directory;
    private readonly TimeProvider          _clock;
    private readonly ILogger               _log;

    public StaleShiftAlertJob(
        PosDbContext db, INotificationService? notifications = null, IPosHubNotifier? hub = null, IModuleGate? gate = null,
        IOrganizationDirectory? directory = null, TimeProvider? clock = null, ILogger<StaleShiftAlertJob>? log = null)
    {
        _db            = db;
        _notifications = notifications;
        _hub           = hub;
        _gate          = gate;
        _directory     = directory;
        _clock         = clock ?? TimeProvider.System;
        _log           = log ?? (ILogger)NullLogger.Instance;
    }

    [AutomaticRetry(Attempts = 0)]
    public async Task<int> RunAsync()
    {
        var alerted = 0;
        await _gate.RunForEnabledOrganizationsAsync(_directory, ModuleCodes.Pos, _log, nameof(StaleShiftAlertJob),
            async () => alerted += await AlertAsync());
        return alerted;
    }

    /// <summary>One pass through the tenant filter (one organization inside a HangfireTenantScope, or all without a gate).</summary>
    internal async Task<int> AlertAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var candidates = await _db.Shifts.Include(s => s.Store).Include(s => s.Terminal)
            .Where(s => PosShiftStatus.Active.Contains(s.Status) && s.StaleAlertedAt == null)
            .ToListAsync(ct);
        var stale = candidates.Where(s => now - s.OpenedAt >= TimeSpan.FromHours(Math.Max(1, s.Store.AutoCloseShiftHours))).ToList();
        if (stale.Count == 0) return 0;

        var storeIds    = stale.Select(s => s.StoreId).Distinct().ToList();
        var supervisors = await _db.StoreStaff.Where(s => storeIds.Contains(s.StoreId) && s.IsActive && s.PosRole == PosRole.Supervisor)
            .Select(s => new { s.OrganizationId, s.StoreId, s.UserId }).ToListAsync(ct);

        foreach (var shift in stale)
        {
            var hours   = (int)(now - shift.OpenedAt).TotalHours;
            var message = $"Shift {shift.ShiftNumber} on {shift.Terminal.Name} ({shift.Store.Name}) has been {shift.Status.ToLowerInvariant()} for {hours} hours.";
            foreach (var userId in supervisors.Where(s => s.StoreId == shift.StoreId && s.OrganizationId == shift.OrganizationId).Select(s => s.UserId).Distinct())
                if (_notifications is not null)
                    await _notifications.TryCreateAsync(new NotificationRequest(
                        userId, NotificationType, "POS shift still open", message, "POS", "PosShift", shift.Uuid.ToString(),
                        $"/portal/pages/pos/shifts/{shift.Uuid}"));
            if (_hub is not null) await _hub.ShiftAlertAsync(shift.OrganizationId, shift.Terminal.Uuid, message, "WARNING");
            shift.StaleAlertedAt = now;
        }
        await _db.SaveChangesAsync(ct);
        _log.LogInformation("POS stale shifts: {Count} shift(s) reported.", stale.Count);
        return stale.Count;
    }
}

/// <summary>A37 D-18 — open POS shifts block a quiet MODULE_POS switch-off.</summary>
internal sealed class PosShiftImpactProvider : IModuleImpactProvider
{
    private readonly PosDbContext _db;
    public PosShiftImpactProvider(PosDbContext db) => _db = db;

    public string ModuleCode => ModuleCodes.Pos;

    public async Task<IReadOnlyList<ModuleImpactItem>> GetInProgressAsync(Guid organizationId, CancellationToken ct = default)
    {
        var open = await _db.Shifts.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(s => s.OrganizationId == organizationId && PosShiftStatus.Active.Contains(s.Status), ct);
        return open == 0 ? [] : [new ModuleImpactItem("POS shifts – Open", open)];
    }
}

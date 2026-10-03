using Microsoft.EntityFrameworkCore;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Core.Sync;

/// <summary>
/// Pauses and resumes a connection's outbox. Called by the connection lifecycle (revoked, expired,
/// disconnected, reconnected) and by the executor when QuickBooks refuses our token mid-sync.
/// <para>
/// <b>May run with no tenant at all</b> (the anonymous OAuth callback) or under another organization
/// (a cross-org job), so every query is unfiltered with an explicit <c>ConnectionId</c> predicate —
/// the connection id is what scopes it.
/// </para>
/// </summary>
internal sealed class OutboxControl : IOutboxControl
{
    private readonly IntegrationDbContext _db;
    private readonly TimeProvider         _clock;

    public OutboxControl(IntegrationDbContext db, TimeProvider clock)
    {
        _db    = db;
        _clock = clock;
    }

    public async Task<int> SuspendAllAsync(int connectionId, string reason, CancellationToken ct = default)
    {
        var entries = await _db.Outbox
            .IgnoreQueryFilters()
            .Where(e => e.ConnectionId == connectionId
                     && (e.Status == OutboxStatus.Queued || e.Status == OutboxStatus.WaitingOnDependency))
            .ToListAsync(ct);

        foreach (var entry in entries)
        {
            entry.Status        = OutboxStatus.Suspended;
            entry.BlockedReason = SyncText.Clip(reason, 1000);
        }

        await _db.SaveChangesAsync(ct);
        return entries.Count;
    }

    public async Task<int> ResumeAllAsync(int connectionId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;

        var entries = await _db.Outbox
            .IgnoreQueryFilters()
            .Where(e => e.ConnectionId == connectionId && e.Status == OutboxStatus.Suspended)
            .ToListAsync(ct);

        foreach (var entry in entries)
        {
            // Back to the queue with a clean slate: the failures were the connection's, not the record's.
            // One that was waiting on dependencies re-checks them when it runs. Maps are left alone
            // (they carry a RowVersion the gateway may be writing); the run updates their state.
            entry.Status        = OutboxStatus.Queued;
            entry.AttemptCount  = 0;
            entry.NextAttemptAt = now;
            entry.BlockedReason = null;
        }

        await _db.SaveChangesAsync(ct);
        return entries.Count;
    }
}

using Microsoft.EntityFrameworkCore;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Core.Sync;

/// <summary>
/// The queue of pushes. <b>One open entry per map</b>: re-sending a record merges into the entry that
/// is already waiting instead of adding a second one. The one exception is an entry that is being
/// executed right now (<see cref="OutboxStatus.Running"/>) — it is never touched mid-flight; a
/// follow-up entry is queued behind it, and the executor refuses to run two entries of one map at once.
/// <para>Nothing here saves; callers save with the rest of their unit of work.</para>
/// </summary>
internal interface ISyncOutbox
{
    /// <summary>Queue (or re-queue) a push of the map's current payload. Sets the map's state.</summary>
    /// <param name="waitingOn">When given, the entry waits for these records instead of being due now.</param>
    Task<SyncOutboxEntry> EnqueueAsync(
        EntityMap map, OutboxOperation operation, DateTime now,
        IReadOnlyList<GatewayDependency>? waitingOn = null, CancellationToken ct = default);

    /// <summary>The open entry that is not mid-execution, if any.</summary>
    Task<SyncOutboxEntry?> GetPendingEntryAsync(EntityMap map, CancellationToken ct = default);

    Task<bool> HasRunningEntryAsync(EntityMap map, CancellationToken ct = default);

    /// <summary>Closes every open entry of the map that is not mid-execution.</summary>
    Task<int> CloseOpenEntriesAsync(EntityMap map, OutboxStatus closeAs, string? reason, DateTime now, CancellationToken ct = default);

    /// <summary>Marks the pending entry Blocked (a new payload failed validation). No-op when there is none.</summary>
    Task BlockPendingAsync(EntityMap map, string reason, CancellationToken ct = default);
}

internal static class OutboxStatuses
{
    /// <summary>Statuses of an entry that still has work to do.</summary>
    public static readonly OutboxStatus[] Open =
    [
        OutboxStatus.Queued, OutboxStatus.Running, OutboxStatus.WaitingOnDependency,
        OutboxStatus.Blocked, OutboxStatus.Suspended
    ];

    /// <summary>Open, but safe to rewrite (nobody is executing it).</summary>
    public static readonly OutboxStatus[] Pending =
    [
        OutboxStatus.Queued, OutboxStatus.WaitingOnDependency, OutboxStatus.Blocked, OutboxStatus.Suspended
    ];

    public static bool IsPending(OutboxStatus s) => Array.IndexOf(Pending, s) >= 0;
}

internal sealed class SyncOutbox : ISyncOutbox
{
    private readonly IntegrationDbContext _db;

    public SyncOutbox(IntegrationDbContext db) => _db = db;

    public async Task<SyncOutboxEntry> EnqueueAsync(
        EntityMap map, OutboxOperation operation, DateTime now,
        IReadOnlyList<GatewayDependency>? waitingOn = null, CancellationToken ct = default)
    {
        var entry = await GetPendingEntryAsync(map, ct);
        var wasWaiting = entry?.Status == OutboxStatus.WaitingOnDependency;

        if (entry is null)
        {
            entry = new SyncOutboxEntry
            {
                OrganizationId = map.OrganizationId,
                ConnectionId   = map.ConnectionId,
                EntityMap      = map,
                CreatedAt      = now
            };
            if (map.Id != 0) entry.EntityMapId = map.Id;
            _db.Outbox.Add(entry);

            // Queued behind an entry that is executing now: never run alongside it.
            if (await HasRunningEntryAsync(map, ct)) now = now.AddSeconds(1);
        }

        entry.Operation     = operation;
        entry.AttemptCount  = 0;
        entry.NextAttemptAt = now;
        entry.BlockedReason = null;
        entry.CompletedAt   = null;

        if (waitingOn is { Count: > 0 })
        {
            entry.Status        = OutboxStatus.WaitingOnDependency;
            entry.DependsOnJson = SyncPayloads.SerializeDependencies(waitingOn);
            entry.WaitingSince  = wasWaiting && entry.WaitingSince is not null ? entry.WaitingSince : now;
            map.State           = SyncState.WaitingOnDependency;
        }
        else
        {
            entry.Status        = OutboxStatus.Queued;
            entry.DependsOnJson = null;
            entry.WaitingSince  = null;
            map.State           = SyncState.Pending;
        }

        return entry;
    }

    public async Task<SyncOutboxEntry?> GetPendingEntryAsync(EntityMap map, CancellationToken ct = default)
    {
        // Entries added in this unit of work first — a new map has no id yet.
        var local = _db.Outbox.Local
            .Where(e => (ReferenceEquals(e.EntityMap, map) || (map.Id != 0 && e.EntityMapId == map.Id))
                     && OutboxStatuses.IsPending(e.Status))
            .OrderByDescending(e => e.Id == 0)
            .ThenByDescending(e => e.Id)
            .FirstOrDefault();
        if (local is not null || map.Id == 0) return local;

        return await _db.Outbox
            .Where(e => e.EntityMapId == map.Id && (e.Status == OutboxStatus.Queued || e.Status == OutboxStatus.WaitingOnDependency || e.Status == OutboxStatus.Blocked || e.Status == OutboxStatus.Suspended))
            .OrderByDescending(e => e.Id)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<bool> HasRunningEntryAsync(EntityMap map, CancellationToken ct = default)
    {
        if (map.Id == 0) return false;
        return await _db.Outbox.AnyAsync(e => e.EntityMapId == map.Id && e.Status == OutboxStatus.Running, ct);
    }

    public async Task<int> CloseOpenEntriesAsync(
        EntityMap map, OutboxStatus closeAs, string? reason, DateTime now, CancellationToken ct = default)
    {
        var closed = 0;
        var local = _db.Outbox.Local.Where(e => ReferenceEquals(e.EntityMap, map) && OutboxStatuses.IsPending(e.Status)).ToList();

        var stored = map.Id == 0
            ? []
            : await _db.Outbox.Where(e => e.EntityMapId == map.Id && (e.Status == OutboxStatus.Queued || e.Status == OutboxStatus.WaitingOnDependency || e.Status == OutboxStatus.Blocked || e.Status == OutboxStatus.Suspended)).ToListAsync(ct);

        foreach (var entry in local.Concat(stored).Distinct())
        {
            entry.Status        = closeAs;
            entry.BlockedReason = SyncText.Clip(reason, 1000);
            entry.CompletedAt   = now;
            closed++;
        }

        return closed;
    }

    public async Task BlockPendingAsync(EntityMap map, string reason, CancellationToken ct = default)
    {
        var entry = await GetPendingEntryAsync(map, ct);
        if (entry is null) return;

        entry.Status        = OutboxStatus.Blocked;
        entry.BlockedReason = SyncText.Clip(reason, 1000);
    }
}

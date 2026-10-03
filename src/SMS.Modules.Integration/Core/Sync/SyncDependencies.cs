using Microsoft.EntityFrameworkCore;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Core.Sync;

/// <summary>
/// Documents need their customer / vendor / items in QuickBooks first. This finds what is missing,
/// asks for it (a placeholder map with <see cref="EntityMap.RequestedAt"/>, which the dependency job
/// turns into an <see cref="IQuickBooksSource.PushAsync"/> for SCM records), and releases waiting
/// documents once everything they need is there.
/// <para>
/// Dependencies are always looked up under the document's own source system: a POS invoice waits for
/// the POS customer, never for SCM's.
/// </para>
/// </summary>
internal interface ISyncDependencies
{
    /// <param name="dryRun">In dry-run mode a dependency that dry-ran cleanly counts as satisfied.</param>
    Task<IReadOnlyList<GatewayDependency>> FindMissingAsync(
        EntityMap owner, IReadOnlyList<GatewayDependency> dependencies, bool dryRun, CancellationToken ct = default);

    /// <summary>
    /// Marks each missing record as wanted (creating a placeholder map when the gateway has never seen
    /// it) and queues the ones whose payload is already here. Does not save.
    /// </summary>
    Task RequestAsync(EntityMap owner, IReadOnlyList<GatewayDependency> missing, DateTime now, CancellationToken ct = default);

    /// <summary>Waiting entries that depended on <paramref name="resolved"/> and now have everything → Queued. Saves.</summary>
    Task<int> ReleaseDependentsAsync(EntityMap resolved, bool dryRun, DateTime now, CancellationToken ct = default);

    /// <summary>Every waiting entry of a connection whose dependencies are all satisfied → Queued. Saves.</summary>
    Task<int> ReleaseReadyAsync(int connectionId, bool dryRun, DateTime now, CancellationToken ct = default);
}

internal sealed class SyncDependencies : ISyncDependencies
{
    private readonly IntegrationDbContext _db;
    private readonly ISyncOutbox          _outbox;

    public SyncDependencies(IntegrationDbContext db, ISyncOutbox outbox)
    {
        _db     = db;
        _outbox = outbox;
    }

    public async Task<IReadOnlyList<GatewayDependency>> FindMissingAsync(
        EntityMap owner, IReadOnlyList<GatewayDependency> dependencies, bool dryRun, CancellationToken ct = default)
    {
        if (dependencies.Count == 0) return [];

        var maps = await LoadAsync(owner.ConnectionId, owner.SourceSystem, dependencies, ct);

        return dependencies
            .Where(d => !maps.TryGetValue((d.Kind, d.ExternalId), out var m) || !IsSatisfied(m, dryRun))
            .ToList();
    }

    public async Task RequestAsync(EntityMap owner, IReadOnlyList<GatewayDependency> missing, DateTime now, CancellationToken ct = default)
    {
        if (missing.Count == 0) return;

        var maps = await LoadAsync(owner.ConnectionId, owner.SourceSystem, missing, ct);

        foreach (var dep in missing)
        {
            if (!maps.TryGetValue((dep.Kind, dep.ExternalId), out var map))
            {
                // Never seen: a placeholder the dependency job fills by asking the source (SCM), or
                // that an external caller fills by sending the record it was told is missing.
                map = new EntityMap
                {
                    OrganizationId = owner.OrganizationId,
                    ConnectionId   = owner.ConnectionId,
                    SourceSystem   = owner.SourceSystem,
                    Kind           = dep.Kind,
                    ExternalId     = dep.ExternalId,
                    DisplayLabel   = dep.ExternalId,
                    State          = SyncState.NotSynced,
                    CreatedDate    = now
                };
                _db.EntityMaps.Add(map);
                maps[(dep.Kind, dep.ExternalId)] = map;
            }

            // A document needs it: that overrides the "only when referenced" scope for good.
            map.RequestedAt ??= now;

            // Payload already here and nothing stopping it: send it now rather than waiting for a pull.
            if (map.PayloadJson is not null
                && map.RemoteId is null
                && map.State is SyncState.NotSynced or SyncState.DryRunOk
                && !await IsSkippedAsync(map, ct)
                && await _outbox.GetPendingEntryAsync(map, ct) is null
                && !await _outbox.HasRunningEntryAsync(map, ct))
            {
                await _outbox.EnqueueAsync(map, OutboxOperation.Upsert, now, ct: ct);
            }
        }
    }

    public async Task<int> ReleaseDependentsAsync(EntityMap resolved, bool dryRun, DateTime now, CancellationToken ct = default)
    {
        var needle = SyncPayloads.EncodedForContains(resolved.ExternalId);

        var waiting = await _db.Outbox
            .Include(e => e.EntityMap)
            .Where(e => e.ConnectionId == resolved.ConnectionId
                     && e.Status == OutboxStatus.WaitingOnDependency
                     && e.EntityMap.SourceSystem == resolved.SourceSystem
                     && e.DependsOnJson != null
                     && e.DependsOnJson.Contains(needle))
            .ToListAsync(ct);

        return await ReleaseAsync(waiting, dryRun, now, ct);
    }

    public async Task<int> ReleaseReadyAsync(int connectionId, bool dryRun, DateTime now, CancellationToken ct = default)
    {
        var waiting = await _db.Outbox
            .Include(e => e.EntityMap)
            .Where(e => e.ConnectionId == connectionId && e.Status == OutboxStatus.WaitingOnDependency)
            .ToListAsync(ct);

        return await ReleaseAsync(waiting, dryRun, now, ct);
    }

    private async Task<int> ReleaseAsync(List<SyncOutboxEntry> waiting, bool dryRun, DateTime now, CancellationToken ct)
    {
        var released = 0;

        foreach (var entry in waiting)
        {
            var deps    = SyncPayloads.DeserializeDependencies(entry.DependsOnJson);
            var missing = await FindMissingAsync(entry.EntityMap, deps, dryRun, ct);

            if (missing.Count == 0)
            {
                entry.Status        = OutboxStatus.Queued;
                entry.NextAttemptAt = now;
                entry.DependsOnJson = null;
                entry.WaitingSince  = null;
                entry.BlockedReason = null;
                if (entry.EntityMap.State == SyncState.WaitingOnDependency) entry.EntityMap.State = SyncState.Pending;
                released++;
            }
            else if (missing.Count != deps.Count)
            {
                entry.DependsOnJson = SyncPayloads.SerializeDependencies(missing);
            }
        }

        await _db.SaveChangesAsync(ct);
        return released;
    }

    private static bool IsSatisfied(EntityMap map, bool dryRun) =>
        map.RemoteId is not null || (dryRun && map.State == SyncState.DryRunOk);

    private async Task<bool> IsSkippedAsync(EntityMap map, CancellationToken ct) =>
        map.Id != 0 && await _db.MatchCandidates.AnyAsync(c => c.EntityMapId == map.Id && c.Decision == MatchDecision.Skip, ct);

    private async Task<Dictionary<(SyncKind, string), EntityMap>> LoadAsync(
        int connectionId, string sourceSystem, IReadOnlyList<GatewayDependency> deps, CancellationToken ct)
    {
        var ids   = deps.Select(d => d.ExternalId).Distinct().ToList();
        var kinds = deps.Select(d => d.Kind).Distinct().ToList();

        var stored = await _db.EntityMaps
            .Where(m => m.ConnectionId == connectionId
                     && m.SourceSystem == sourceSystem
                     && ids.Contains(m.ExternalId))
            .ToListAsync(ct);

        var result = new Dictionary<(SyncKind, string), EntityMap>();
        foreach (var m in stored.Where(m => kinds.Contains(m.Kind)))
            result[(m.Kind, m.ExternalId)] = m;

        // Placeholders added earlier in this unit of work.
        foreach (var m in _db.EntityMaps.Local.Where(m => m.Id == 0 && m.ConnectionId == connectionId && m.SourceSystem == sourceSystem))
            result.TryAdd((m.Kind, m.ExternalId), m);

        return result;
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SMS.Modules.Integration.Configuration;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;

namespace SMS.Modules.Integration.Core.Sync;

internal enum SyncClaimDecisionKind
{
    /// <summary>A fresh claim. Make the call.</summary>
    Proceed,
    /// <summary>This exact command already succeeded. Use the stored RemoteId; do not call.</summary>
    AlreadySucceeded,
    /// <summary>QuickBooks already refused this exact command. Sending it again earns the same answer.</summary>
    AlreadyRefused,
    /// <summary>Another worker holds the claim right now. Do not call.</summary>
    InFlight,
    /// <summary>
    /// An earlier attempt's outcome is unknown (timeout, 5xx, a worker that went quiet). The claim is
    /// now yours, but a create may already have happened: <b>look the record up before creating it</b>.
    /// </summary>
    NeedsLookup
}

internal sealed record SyncClaimDecision(SyncClaimDecisionKind Kind, SyncCommandClaim Claim);

/// <summary>
/// The command ledger (same semantics as Logistics' CarrierCommandLedger). A row is written
/// <b>before</b> every call to QuickBooks, keyed by what is being sent; a retry consults it instead
/// of creating a second record.
/// <para>
/// The race guard is the database's unique index on (OrganizationId, CommandKey): two workers
/// inserting the same claim cannot both succeed. Taking over an unknown claim is a conditional
/// update on the attempt count, so two takeovers cannot both win either.
/// </para>
/// <para>
/// Every query here is unfiltered with an explicit organization predicate — the ledger is used from
/// jobs, where the ambient tenant may not be the claim's organization.
/// </para>
/// </summary>
internal interface ISyncLedger
{
    string CommandKey(EntityMap map, OutboxOperation operation, string fingerprint);

    Task<SyncClaimDecision> ClaimAsync(
        EntityMap map, OutboxOperation operation, string fingerprint, DateTime now, CancellationToken ct = default);

    /// <summary>Records the answer on the claim and saves (with anything else pending in the context).</summary>
    Task RecordSucceededAsync(SyncCommandClaim claim, string remoteId, DateTime now, CancellationToken ct = default);
    Task RecordRefusedAsync(SyncCommandClaim claim, string? errorCode, DateTime now, CancellationToken ct = default);
    Task RecordUnknownAsync(SyncCommandClaim claim, string? errorCode, DateTime now, CancellationToken ct = default);

    /// <summary>A succeeded upsert claim of this map with a RemoteId — proof a record exists even if the map lost it.</summary>
    Task<SyncCommandClaim?> FindSucceededAsync(EntityMap map, CancellationToken ct = default);

    /// <summary>
    /// Whether any other claim of this map has an unknown outcome (Unknown, or InFlight past its
    /// lease + grace) — i.e. a create for an earlier payload may have happened.
    /// </summary>
    Task<bool> HasUnresolvedAsync(EntityMap map, string exceptCommandKey, DateTime now, CancellationToken ct = default);

    /// <summary>Removes refused claims of this map so a person's "Retry" really sends again.</summary>
    Task<int> ForgetRefusalsAsync(EntityMap map, CancellationToken ct = default);

    /// <summary>Every organization: InFlight claims past lease + grace → Unknown. For the sweep job.</summary>
    Task<int> ExpireStaleLeasesAsync(DateTime now, CancellationToken ct = default);
}

internal sealed class SyncLedger : ISyncLedger
{
    private const int MaxClaimAttempts = 5;

    private readonly IntegrationDbContext   _db;
    private readonly IntegrationJobOptions  _options;

    public SyncLedger(IntegrationDbContext db, IOptions<IntegrationJobOptions> options)
    {
        _db      = db;
        _options = options.Value;
    }

    private TimeSpan Lease => TimeSpan.FromMinutes(Math.Max(1, _options.LeaseMinutes));
    private TimeSpan Grace => TimeSpan.FromMinutes(Math.Max(0, _options.LeaseGraceMinutes));

    public string CommandKey(EntityMap map, OutboxOperation operation, string fingerprint) =>
        SyncPayloads.CommandKey(map.ConnectionId, map.SourceSystem, map.Kind, map.ExternalId, operation, fingerprint);

    public async Task<SyncClaimDecision> ClaimAsync(
        EntityMap map, OutboxOperation operation, string fingerprint, DateTime now, CancellationToken ct = default)
    {
        var key   = CommandKey(map, operation, fingerprint);
        var owner = map.OrganizationId;

        for (var attempt = 1; attempt <= MaxClaimAttempts; attempt++)
        {
            var existing = await _db.CommandClaims
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.OrganizationId == owner && c.CommandKey == key, ct);

            if (existing is null)
            {
                var claim = new SyncCommandClaim
                {
                    OrganizationId = owner,
                    EntityMapId    = map.Id,
                    CommandKey     = key,
                    Status         = ClaimStatus.InFlight,
                    AttemptCount   = 1,
                    FirstAttemptAt = now,
                    LastAttemptAt  = now,
                    LeaseExpiresAt = now + Lease
                };
                _db.CommandClaims.Add(claim);

                try
                {
                    await _db.SaveChangesAsync(ct);
                    return new SyncClaimDecision(SyncClaimDecisionKind.Proceed, claim);
                }
                catch (DbUpdateException) when (attempt < MaxClaimAttempts)
                {
                    // Another worker inserted the same key first (unique index). Look again and
                    // decide on what the winner wrote.
                    _db.Entry(claim).State = EntityState.Detached;
                    continue;
                }
            }

            switch (existing.Status)
            {
                case ClaimStatus.Succeeded:
                    return new SyncClaimDecision(SyncClaimDecisionKind.AlreadySucceeded, await TrackAsync(existing.Id, ct));

                case ClaimStatus.Refused:
                    return new SyncClaimDecision(SyncClaimDecisionKind.AlreadyRefused, await TrackAsync(existing.Id, ct));

                case ClaimStatus.InFlight when existing.LeaseExpiresAt is { } lease && now < lease + Grace:
                    return new SyncClaimDecision(SyncClaimDecisionKind.InFlight, existing);
            }

            // Unknown, or in flight past its lease: nobody knows what happened. Take the claim over —
            // conditionally, so of two workers doing this at once only one wins.
            if (await TakeOverAsync(existing, now, ct))
                return new SyncClaimDecision(SyncClaimDecisionKind.NeedsLookup, await TrackAsync(existing.Id, ct));
        }

        throw new InvalidOperationException(
            $"Could not claim sync command {key} for map {map.Id} after {MaxClaimAttempts} attempts; the ledger row is under sustained contention.");
    }

    private async Task<bool> TakeOverAsync(SyncCommandClaim observed, DateTime now, CancellationToken ct)
    {
        var newLease = now + Lease;

        if (_db.Database.IsRelational())
        {
            var rows = await _db.CommandClaims
                .IgnoreQueryFilters()
                .Where(c => c.Id == observed.Id
                         && c.AttemptCount == observed.AttemptCount
                         && c.Status == observed.Status)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Status, ClaimStatus.InFlight)
                    .SetProperty(c => c.AttemptCount, observed.AttemptCount + 1)
                    .SetProperty(c => c.LastAttemptAt, now)
                    .SetProperty(c => c.LeaseExpiresAt, (DateTime?)newLease), ct);

            DetachClaim(observed.Id);
            return rows == 1;
        }

        // Providers without set-based updates (the in-memory test provider): a tracked update.
        var tracked = await _db.CommandClaims.IgnoreQueryFilters().FirstAsync(c => c.Id == observed.Id, ct);
        if (tracked.AttemptCount != observed.AttemptCount || tracked.Status != observed.Status) return false;

        tracked.Status         = ClaimStatus.InFlight;
        tracked.AttemptCount   = observed.AttemptCount + 1;
        tracked.LastAttemptAt  = now;
        tracked.LeaseExpiresAt = newLease;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<SyncCommandClaim> TrackAsync(int id, CancellationToken ct)
    {
        DetachClaim(id);
        return await _db.CommandClaims.IgnoreQueryFilters().FirstAsync(c => c.Id == id, ct);
    }

    private void DetachClaim(int id)
    {
        foreach (var entry in _db.ChangeTracker.Entries<SyncCommandClaim>().Where(e => e.Entity.Id == id).ToList())
            entry.State = EntityState.Detached;
    }

    public Task RecordSucceededAsync(SyncCommandClaim claim, string remoteId, DateTime now, CancellationToken ct = default) =>
        RecordAsync(claim, ClaimStatus.Succeeded, remoteId, null, now, ct);

    public Task RecordRefusedAsync(SyncCommandClaim claim, string? errorCode, DateTime now, CancellationToken ct = default) =>
        RecordAsync(claim, ClaimStatus.Refused, null, errorCode, now, ct);

    public Task RecordUnknownAsync(SyncCommandClaim claim, string? errorCode, DateTime now, CancellationToken ct = default) =>
        RecordAsync(claim, ClaimStatus.Unknown, null, errorCode, now, ct);

    private async Task RecordAsync(
        SyncCommandClaim claim, ClaimStatus status, string? remoteId, string? errorCode, DateTime now, CancellationToken ct)
    {
        // A real answer never gets overwritten by "unknown" (a late sweep, a late exception).
        if (status == ClaimStatus.Unknown && claim.Status is ClaimStatus.Succeeded or ClaimStatus.Refused) return;

        claim.Status         = status;
        claim.LastAttemptAt  = now;
        claim.LeaseExpiresAt = null;
        claim.RemoteId       = remoteId ?? claim.RemoteId;
        claim.ErrorCode      = SyncText.Clip(errorCode, 100);

        await _db.SaveChangesAsync(ct);
    }

    public Task<SyncCommandClaim?> FindSucceededAsync(EntityMap map, CancellationToken ct = default) =>
        map.Id == 0
            ? Task.FromResult<SyncCommandClaim?>(null)
            : _db.CommandClaims
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(c => c.OrganizationId == map.OrganizationId
                         && c.EntityMapId == map.Id
                         && c.Status == ClaimStatus.Succeeded
                         && c.RemoteId != null)
                .OrderByDescending(c => c.LastAttemptAt)
                .FirstOrDefaultAsync(ct);

    public async Task<bool> HasUnresolvedAsync(EntityMap map, string exceptCommandKey, DateTime now, CancellationToken ct = default)
    {
        if (map.Id == 0) return false;

        var cutoff = now - Grace;
        return await _db.CommandClaims
            .IgnoreQueryFilters()
            .AnyAsync(c => c.OrganizationId == map.OrganizationId
                        && c.EntityMapId == map.Id
                        && c.CommandKey != exceptCommandKey
                        && (c.Status == ClaimStatus.Unknown
                            || (c.Status == ClaimStatus.InFlight && (c.LeaseExpiresAt == null || c.LeaseExpiresAt < cutoff))), ct);
    }

    public async Task<int> ForgetRefusalsAsync(EntityMap map, CancellationToken ct = default)
    {
        var refused = await _db.CommandClaims
            .IgnoreQueryFilters()
            .Where(c => c.OrganizationId == map.OrganizationId && c.EntityMapId == map.Id && c.Status == ClaimStatus.Refused)
            .ToListAsync(ct);

        _db.CommandClaims.RemoveRange(refused);
        return refused.Count;
    }

    public async Task<int> ExpireStaleLeasesAsync(DateTime now, CancellationToken ct = default)
    {
        var cutoff = now - Grace;

        if (_db.Database.IsRelational())
        {
            return await _db.CommandClaims
                .IgnoreQueryFilters()
                .Where(c => c.Status == ClaimStatus.InFlight && (c.LeaseExpiresAt == null || c.LeaseExpiresAt < cutoff))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Status, ClaimStatus.Unknown)
                    .SetProperty(c => c.ErrorCode, "LEASE_EXPIRED")
                    .SetProperty(c => c.LeaseExpiresAt, (DateTime?)null), ct);
        }

        var stale = await _db.CommandClaims
            .IgnoreQueryFilters()
            .Where(c => c.Status == ClaimStatus.InFlight && (c.LeaseExpiresAt == null || c.LeaseExpiresAt < cutoff))
            .ToListAsync(ct);

        foreach (var claim in stale)
        {
            claim.Status         = ClaimStatus.Unknown;
            claim.ErrorCode      = "LEASE_EXPIRED";
            claim.LeaseExpiresAt = null;
        }

        await _db.SaveChangesAsync(ct);
        return stale.Count;
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Logging;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Core.Sync;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Models;
using SMS.Shared.Exceptions;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Core.Matching;

/// <summary>
/// Matching caller records the gateway holds against records the accountant already has in
/// QuickBooks (QBI-22). <b>Nothing is created in QuickBooks here</b> — a scan only reads, and a
/// confirmation only links, queues, or skips.
/// </summary>
public interface IQuickBooksMatchingService
{
    Task<MatchScanResultModel> ScanAsync(SyncKind kind, CancellationToken ct = default);
    Task<List<MatchCandidateModel>> ListAsync(SyncKind kind, string? decision, CancellationToken ct = default);
    Task<List<MatchCandidateModel>> ConfirmAsync(SyncKind kind, ConfirmMatchesRequest request, int userId, CancellationToken ct = default);
}

internal sealed class MatchingService : IQuickBooksMatchingService
{
    public const int PageSize = 1000;
    private const int MaxPages = 200;

    private readonly IntegrationDbContext        _db;
    private readonly IConnectionAccessor         _connections;
    private readonly IAccountingProviderRegistry _providers;
    private readonly IConnectionHealth           _health;
    private readonly IOutboxControl              _outboxControl;
    private readonly ISyncOutbox                 _outbox;
    private readonly ISyncDependencies           _dependencies;
    private readonly TimeProvider                _clock;
    private readonly ILogger<MatchingService>    _logger;

    public MatchingService(
        IntegrationDbContext db, IConnectionAccessor connections, IAccountingProviderRegistry providers,
        IConnectionHealth health, IOutboxControl outboxControl, ISyncOutbox outbox, ISyncDependencies dependencies,
        TimeProvider clock, ILogger<MatchingService> logger)
    {
        _db            = db;
        _connections   = connections;
        _providers     = providers;
        _health        = health;
        _outboxControl = outboxControl;
        _outbox        = outbox;
        _dependencies  = dependencies;
        _clock         = clock;
        _logger        = logger;
    }

    // ── Scan ─────────────────────────────────────────────────────────────────

    public async Task<MatchScanResultModel> ScanAsync(SyncKind kind, CancellationToken ct = default)
    {
        EnsureMatchable(kind);

        var connection = await _connections.GetCurrentAsync(ct);
        if (connection is null || !connection.IsUsable())
            throw new ConflictException("QuickBooks is not connected. Connect it before matching existing records.");

        var now    = _clock.GetUtcNow().UtcDateTime;
        var remote = await ReadAllAsync(connection, kind, now, ct);

        // A QuickBooks record already linked to another of our records is not offered again.
        var linked = (await _db.EntityMaps
                .Where(m => m.ConnectionId == connection.Id && m.Kind == kind && m.RemoteId != null)
                .Select(m => m.RemoteId!)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        // Records someone already decided about (create new / skip) are not re-proposed.
        var decided = (await _db.MatchCandidates
                .Where(c => c.ConnectionId == connection.Id && c.Kind == kind
                         && (c.Decision == MatchDecision.CreateNew || c.Decision == MatchDecision.Skip))
                .Select(c => c.EntityMapId)
                .ToListAsync(ct))
            .ToHashSet();

        var locals = (await _db.EntityMaps
                .Where(m => m.ConnectionId == connection.Id && m.Kind == kind && m.PayloadJson != null && m.RemoteId == null)
                .ToListAsync(ct))
            .Where(m => !decided.Contains(m.Id))
            .ToList();

        var available = remote.Where(r => !linked.Contains(r.RemoteId)).Select(RemoteKey.Of).ToList();
        var index     = new RemoteIndex(available);

        // Replace this kind's open proposals; decided ones stay as the record of what was decided.
        var stale = await _db.MatchCandidates
            .Where(c => c.ConnectionId == connection.Id && c.Kind == kind && c.Decision == MatchDecision.Pending)
            .ToListAsync(ct);
        _db.MatchCandidates.RemoveRange(stale);

        var result = new MatchScanResultModel { Kind = kind.ToString(), LocalCount = locals.Count, RemoteCount = remote.Count };

        foreach (var map in locals)
        {
            var local = LocalKey.Of(map);
            var (match, confidence, reason) = local is null ? (null, MatchConfidence.None, null) : index.Best(kind, local);

            _db.MatchCandidates.Add(new MatchCandidate
            {
                OrganizationId = map.OrganizationId,
                ConnectionId   = connection.Id,
                EntityMapId    = map.Id,
                Kind           = kind,
                RemoteId       = match?.Entry.RemoteId,
                RemoteName     = SyncText.Clip(match?.Entry.Name, 200),
                Confidence     = confidence,
                Reason         = reason,
                Decision       = MatchDecision.Pending,
                CreatedAt      = now
            });

            switch (confidence)
            {
                case MatchConfidence.Exact:    result.Exact++;     break;
                case MatchConfidence.Probable: result.Probable++;  break;
                default:                       result.Unmatched++; break;
            }
        }

        await _db.SaveChangesAsync(ct);
        return result;
    }

    private async Task<List<RemoteListEntry>> ReadAllAsync(IntegrationConnection connection, SyncKind kind, DateTime now, CancellationToken ct)
    {
        var provider = _providers.Get(connection.ProviderKey);
        var context  = connection.ToProviderContext();
        var all      = new List<RemoteListEntry>();

        for (var page = 0; page < MaxPages; page++)
        {
            var start  = page * PageSize + 1;
            var result = await provider.ListAsync(context, kind, start, PageSize, ct);

            _db.SyncLog.Add(new SyncLogEntry
            {
                OrganizationId = connection.OrganizationId,
                ConnectionId   = connection.Id,
                Operation      = "Query",
                Outcome        = result.Outcome.ToString(),
                ErrorCode      = SyncText.Clip(result.ErrorCode, 100),
                Message        = SyncText.Clip(Redactor.Redact(result.Message ?? $"Matching scan: {kind} from {start}, {result.Value?.Count ?? 0} returned."), 2000),
                RequestJson    = Redactor.Redact(result.RequestJson),
                ResponseJson   = null, // a page of a thousand records is not worth keeping
                DurationMs     = result.DurationMs,
                IntuitTid      = SyncText.Clip(result.IntuitTid, 100),
                CreatedAt      = now
            });

            if (!result.IsSuccess)
            {
                await _db.SaveChangesAsync(ct);

                if (result.Outcome == ProviderOutcomeKind.AuthRevoked)
                {
                    var reason = Redactor.Redact(result.Message) ?? "QuickBooks refused our access token.";
                    await _health.MarkUnavailableAsync(connection.Id, ConnectionStatus.Revoked, reason, ct);
                    await _outboxControl.SuspendAllAsync(connection.Id, $"QuickBooks refused our access ({reason}). Reconnect to resume.", ct);
                    throw new ConflictException("QuickBooks refused our access — the connection has been revoked. Reconnect, then scan again.");
                }

                throw new ConflictException(
                    $"QuickBooks could not be read ({result.Outcome}: {Redactor.Redact(result.Message) ?? "no detail"}). Nothing was changed; try the scan again.");
            }

            var rows = result.Value ?? [];
            all.AddRange(rows);
            if (rows.Count < PageSize) break;
        }

        return all;
    }

    // ── List ─────────────────────────────────────────────────────────────────

    public async Task<List<MatchCandidateModel>> ListAsync(SyncKind kind, string? decision, CancellationToken ct = default)
    {
        EnsureMatchable(kind);

        var connection = await _connections.GetCurrentAsync(ct);
        if (connection is null) return [];

        var query = _db.MatchCandidates.Where(c => c.ConnectionId == connection.Id && c.Kind == kind);

        if (!string.IsNullOrWhiteSpace(decision))
        {
            if (!Enum.TryParse<MatchDecision>(decision.Trim(), ignoreCase: true, out var d))
                throw new BadRequestException($"Unknown decision '{decision}'. Use Pending, Link, CreateNew or Skip.");
            query = query.Where(c => c.Decision == d);
        }

        var candidates = await query.OrderBy(c => c.Id).ToListAsync(ct);
        return await ToModelsAsync(candidates, ct);
    }

    // ── Confirm ──────────────────────────────────────────────────────────────

    public async Task<List<MatchCandidateModel>> ConfirmAsync(
        SyncKind kind, ConfirmMatchesRequest request, int userId, CancellationToken ct = default)
    {
        EnsureMatchable(kind);
        if (request?.Decisions is not { Count: > 0 })
            throw new BadRequestException("Send at least one decision.");

        var connection = await _connections.GetCurrentAsync(ct)
            ?? throw new ConflictException("QuickBooks is not connected.");

        var now     = _clock.GetUtcNow().UtcDateTime;
        var touched = new List<MatchCandidate>();
        var linkedNow = new Dictionary<string, int>(StringComparer.Ordinal); // remote id → map id, within this batch
        var linkedMaps = new List<EntityMap>();

        foreach (var item in request.Decisions)
        {
            var candidate = await _db.MatchCandidates.FirstOrDefaultAsync(
                    c => c.Uuid == item.CandidateId && c.ConnectionId == connection.Id && c.Kind == kind, ct)
                ?? throw new NotFoundException("Match candidate", item.CandidateId);

            var map = await _db.EntityMaps.FirstAsync(m => m.Id == candidate.EntityMapId, ct);

            if (!Enum.TryParse<MatchDecision>(item.Decision?.Trim(), ignoreCase: true, out var decision) || decision == MatchDecision.Pending)
                throw new BadRequestException($"Unknown decision '{item.Decision}'. Use Link, CreateNew or Skip.");

            switch (decision)
            {
                case MatchDecision.Link:
                {
                    var remoteId = string.IsNullOrWhiteSpace(item.RemoteId) ? candidate.RemoteId : item.RemoteId.Trim();
                    if (string.IsNullOrWhiteSpace(remoteId))
                        throw new BadRequestException($"'{map.DisplayLabel}' has no proposed QuickBooks record; give the id to link it to.");

                    if (map.RemoteId is not null && map.RemoteId != remoteId)
                        throw new BadRequestException($"'{map.DisplayLabel}' is already linked to QuickBooks record {map.RemoteId}.");

                    var takenBy = await _db.EntityMaps
                        .Where(m => m.ConnectionId == map.ConnectionId && m.Kind == map.Kind && m.RemoteId == remoteId && m.Id != map.Id)
                        .Select(m => m.DisplayLabel)
                        .FirstOrDefaultAsync(ct);
                    if (takenBy is not null || (linkedNow.TryGetValue(remoteId, out var other) && other != map.Id))
                        throw new BadRequestException(
                            $"QuickBooks record {remoteId} is already linked to '{takenBy ?? "another record in this request"}'. One QuickBooks record can back only one {kind}.");

                    if (await _outbox.HasRunningEntryAsync(map, ct))
                        throw new ConflictException($"'{map.DisplayLabel}' is being sent to QuickBooks right now. Wait a minute and link it then.");

                    linkedNow[remoteId] = map.Id;

                    map.RemoteId        = remoteId;
                    map.RemoteSyncToken = null; // fetched before any later update
                    map.RemoteName      = remoteId == candidate.RemoteId ? candidate.RemoteName : null;
                    map.LinkOrigin      = LinkOrigin.Adopted;
                    map.LinkedByUserId  = userId;
                    map.LinkedAt        = now;
                    map.State           = SyncState.Synced;
                    // The accountant's record is left exactly as it is: nothing is pushed until SCM's data changes.
                    map.LastPushedFingerprint = map.PayloadFingerprint;
                    map.LastErrorCode   = null;
                    map.LastError       = null;
                    map.ModifiedDate    = now;
                    await _outbox.CloseOpenEntriesAsync(map, OutboxStatus.Done, "Linked to an existing QuickBooks record.", now, ct);

                    candidate.RemoteId = remoteId;
                    linkedMaps.Add(map);
                    break;
                }

                case MatchDecision.CreateNew:
                    // Wanted in QuickBooks: queued now (a dry run in dry-run mode), created on the next push.
                    map.RequestedAt ??= now;
                    if (map.PayloadJson is not null && map.RemoteId is null && map.State != SyncState.Blocked
                        && !await _outbox.HasRunningEntryAsync(map, ct))
                        await _outbox.EnqueueAsync(map, OutboxOperation.Upsert, now, ct: ct);
                    break;

                case MatchDecision.Skip:
                    await _outbox.CloseOpenEntriesAsync(map, OutboxStatus.Done, "Skipped in matching: not synced.", now, ct);
                    if (map.RemoteId is null) map.State = SyncState.NotSynced;
                    break;
            }

            candidate.Decision  = decision;
            candidate.DecidedBy = userId;
            candidate.DecidedAt = now;
            touched.Add(candidate);
        }

        await _db.SaveChangesAsync(ct);

        // Invoices and bills waiting for these partners/items can go now.
        foreach (var map in linkedMaps)
            await _dependencies.ReleaseDependentsAsync(map, dryRun: false, now, ct);

        return await ToModelsAsync(touched, ct);
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    private static void EnsureMatchable(SyncKind kind)
    {
        if (!SyncPayloads.IsMasterData(kind))
            throw new BadRequestException($"Matching is for customers, vendors and items, not {kind}.");
    }

    private async Task<List<MatchCandidateModel>> ToModelsAsync(List<MatchCandidate> candidates, CancellationToken ct)
    {
        var mapIds = candidates.Select(c => c.EntityMapId).Distinct().ToList();
        var maps = await _db.EntityMaps
            .AsNoTracking()
            .Where(m => mapIds.Contains(m.Id))
            .Select(m => new { m.Id, m.ExternalId, m.SourceSystem, m.DisplayLabel })
            .ToDictionaryAsync(m => m.Id, ct);

        return candidates.Select(c =>
        {
            maps.TryGetValue(c.EntityMapId, out var m);
            return new MatchCandidateModel
            {
                Id           = c.Uuid,
                Kind         = c.Kind.ToString(),
                ExternalId   = m?.ExternalId ?? string.Empty,
                SourceSystem = m?.SourceSystem ?? string.Empty,
                LocalLabel   = m?.DisplayLabel ?? string.Empty,
                RemoteId     = c.RemoteId,
                RemoteName   = c.RemoteName,
                Confidence   = c.Confidence.ToString(),
                Reason       = c.Reason,
                Decision     = c.Decision.ToString()
            };
        }).ToList();
    }

    /// <summary>The comparable parts of one stored payload.</summary>
    private sealed record LocalKey(string Exact, string Loose, string Email, string TaxId, string AccountNumber, string Code, string Sku)
    {
        public static LocalKey? Of(EntityMap map)
        {
            object payload;
            try
            {
                payload = SyncPayloads.Deserialize(map.Kind, map.PayloadJson!);
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }

            return payload switch
            {
                VendorPayload v => new LocalKey(
                    MatchNormalizer.Exact(v.DisplayName), MatchNormalizer.Loose(v.DisplayName), MatchNormalizer.Email(v.Email),
                    MatchNormalizer.Id(v.TaxId), MatchNormalizer.Id(v.AccountNumber), MatchNormalizer.Id(v.Code), string.Empty),
                PartyPayload p => new LocalKey(
                    MatchNormalizer.Exact(p.DisplayName), MatchNormalizer.Loose(p.DisplayName), MatchNormalizer.Email(p.Email),
                    MatchNormalizer.Id(p.TaxId), string.Empty, MatchNormalizer.Id(p.Code), string.Empty),
                ItemPayload i => new LocalKey(
                    MatchNormalizer.Exact(RemoteNameResolver.ItemBaseName(i)), MatchNormalizer.Loose(RemoteNameResolver.ItemBaseName(i)),
                    string.Empty, string.Empty, string.Empty, string.Empty, MatchNormalizer.Id(i.Sku)),
                _ => null
            };
        }
    }

    private sealed record RemoteKey(RemoteListEntry Entry, string Exact, string Loose, string Email, string TaxId, string AccountNumber, string Sku)
    {
        public static RemoteKey Of(RemoteListEntry e) => new(
            e, MatchNormalizer.Exact(e.Name), MatchNormalizer.Loose(e.Name), MatchNormalizer.Email(e.Email),
            MatchNormalizer.Id(e.TaxId), MatchNormalizer.Id(e.AccountNumber), MatchNormalizer.Id(e.Sku));
    }

    /// <summary>Lookups by every exact key; the "contains" rule is the only linear scan.</summary>
    private sealed class RemoteIndex
    {
        private readonly List<RemoteKey> _all;
        private readonly ILookup<string, RemoteKey> _byExact, _byLoose, _byEmail, _byTaxId, _byAccount, _bySku;

        public RemoteIndex(List<RemoteKey> all)
        {
            _all       = all;
            _byExact   = all.Where(r => r.Exact.Length > 0).ToLookup(r => r.Exact);
            _byLoose   = all.Where(r => r.Loose.Length > 0).ToLookup(r => r.Loose);
            _byEmail   = all.Where(r => r.Email.Length > 0).ToLookup(r => r.Email);
            _byTaxId   = all.Where(r => r.TaxId.Length > 0).ToLookup(r => r.TaxId);
            _byAccount = all.Where(r => r.AccountNumber.Length > 0).ToLookup(r => r.AccountNumber);
            _bySku     = all.Where(r => r.Sku.Length > 0).ToLookup(r => r.Sku);
        }

        public (RemoteKey? match, MatchConfidence confidence, string? reason) Best(SyncKind kind, LocalKey local)
        {
            // Exact: the same name, or the same identifier.
            if (First(_byExact, local.Exact) is { } byName) return (byName, MatchConfidence.Exact, "Exact name");
            if (First(_byTaxId, local.TaxId) is { } byTax) return (byTax, MatchConfidence.Exact, "Tax id");
            if (kind == SyncKind.Vendor)
            {
                if (First(_byAccount, local.AccountNumber) is { } byAcct) return (byAcct, MatchConfidence.Exact, "Account number");
                if (First(_byAccount, local.Code) is { } byCode) return (byCode, MatchConfidence.Exact, "Code");
            }
            if (kind == SyncKind.Item && First(_bySku, local.Sku) is { } bySku) return (bySku, MatchConfidence.Exact, "SKU");

            // Probable: the same email, a name that differs only in punctuation / company form, or containment.
            if (First(_byEmail, local.Email) is { } byEmail) return (byEmail, MatchConfidence.Probable, "Email");
            if (First(_byLoose, local.Loose) is { } byLoose) return (byLoose, MatchConfidence.Probable, "Similar name");

            if (local.Loose.Length > 0)
            {
                var contains = _all.FirstOrDefault(r => MatchNormalizer.Contains(local.Loose, r.Loose));
                if (contains is not null) return (contains, MatchConfidence.Probable, "Name contains");
            }

            return (null, MatchConfidence.None, null);
        }

        private static RemoteKey? First(ILookup<string, RemoteKey> lookup, string key) =>
            key.Length == 0 ? null : lookup[key].FirstOrDefault();
    }
}

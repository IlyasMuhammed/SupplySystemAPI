using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Core.Settings;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Models;
using SMS.Shared.Exceptions;
using SMS.Shared.Integration.QuickBooks;
using SMS.Shared.Pagination;

namespace SMS.Modules.Integration.Core.Sync;

/// <summary>The sync dashboard and its actions (plan §5.1 /sync/*, /status/lookup).</summary>
public interface IQuickBooksSyncAdminService
{
    Task<SyncSummaryModel> GetSummaryAsync(CancellationToken ct = default);
    Task<PaginatedResponse<SyncItemModel>> GetItemsAsync(SyncItemQuery query, CancellationToken ct = default);
    Task<List<SyncLogModel>> GetLogAsync(Guid itemId, CancellationToken ct = default);

    /// <summary>Re-queues the record now with a fresh attempt count and runs it straight away.</summary>
    Task<SyncItemModel> RetryAsync(Guid itemId, CancellationToken ct = default);

    Task<SyncItemModel> ResolveAsync(Guid itemId, ResolveSyncItemRequest request, int userId, CancellationToken ct = default);
    Task<ManualPushResult> PushAsync(ManualPushRequest request, CancellationToken ct = default);
    Task<BackfillResult> BackfillAsync(SyncKind kind, CancellationToken ct = default);
    Task<StatusLookupResult> LookupStatusAsync(StatusLookupRequest request, CancellationToken ct = default);

    // A44 (CTRL) — D-17 unlink, D-11 dead letter.
    /// <summary>Forgets the link to the QuickBooks record (kept there). 409 (<see cref="UnlinkRefusedModel"/>) while open synced documents use it.</summary>
    Task<SyncItemModel> UnlinkAsync(Guid itemId, int userId, CancellationToken ct = default);
    /// <summary>Re-queues every Failed / NeedsResolution record (of one kind) that can be sent again.</summary>
    Task<RetryFailedResult> RetryFailedAsync(RetryFailedRequest request, int userId, CancellationToken ct = default);
    /// <summary>Dismisses one dead-letter record: nothing more is sent until its data changes.</summary>
    Task<SyncItemModel> SkipAsync(Guid itemId, int userId, CancellationToken ct = default);

    // A44 D-34 — the QuickBooks logs page: every call, across all records.
    Task<PaginatedResponse<SyncLogItemModel>> GetCallLogAsync(SyncLogQuery query, CancellationToken ct = default);
    Task<SyncLogItemModel> GetCallLogEntryAsync(Guid id, CancellationToken ct = default);
}

/// <summary>Parses the kind names used in routes and requests.</summary>
public static class SyncKindNames
{
    /// <summary>Accepts SyncKind names (any case) and the data endpoints' path forms (customers, sales-invoices…).</summary>
    public static bool TryParse(string? value, out SyncKind kind)
    {
        kind = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var v = value.Trim();
        switch (v.ToLowerInvariant())
        {
            case "customers":      kind = SyncKind.Customer;     return true;
            case "vendors":        kind = SyncKind.Vendor;       return true;
            case "items":          kind = SyncKind.Item;         return true;
            case "sales-invoices":
            case "salesinvoices":  kind = SyncKind.SalesInvoice; return true;
            case "bills":          kind = SyncKind.Bill;         return true;
            // A44 kinds' path forms.
            case "payments":       kind = SyncKind.Payment;       return true;
            case "bill-payments":
            case "billpayments":   kind = SyncKind.BillPayment;   return true;
            case "sales-receipts":
            case "salesreceipts":  kind = SyncKind.SalesReceipt;  return true;
            case "credit-memos":
            case "creditmemos":    kind = SyncKind.CreditMemo;    return true;
            case "refund-receipts":
            case "refundreceipts": kind = SyncKind.RefundReceipt; return true;
            case "vendor-credits":
            case "vendorcredits":  kind = SyncKind.VendorCredit;  return true;
        }

        return !int.TryParse(v, out _) && Enum.TryParse(v, ignoreCase: true, out kind) && Enum.IsDefined(kind);
    }

    public static SyncKind Parse(string? value) =>
        TryParse(value, out var kind)
            ? kind
            : throw new BadRequestException($"Unknown kind '{value}'. Use one of: {string.Join(", ", Enum.GetNames<SyncKind>())}.");
}

internal sealed class SyncAdminService : IQuickBooksSyncAdminService
{
    public const int MaxIdsPerRequest = 500;

    private readonly IntegrationDbContext           _db;
    private readonly IConnectionAccessor            _connections;
    private readonly IAccountingProviderRegistry    _providers;
    private readonly ISyncOutbox                    _outbox;
    private readonly ISyncLedger                    _ledger;
    private readonly ISyncExecutor                  _executor;
    private readonly ISyncDependencies              _dependencies;
    private readonly IQuickBooksGateway             _gateway;
    private readonly IEnumerable<IQuickBooksSource> _sources;
    private readonly TimeProvider                   _clock;
    private readonly ILogger<SyncAdminService>      _logger;
    private readonly IServiceProvider               _services;

    public SyncAdminService(
        IntegrationDbContext db, IConnectionAccessor connections, IAccountingProviderRegistry providers, ISyncOutbox outbox,
        ISyncLedger ledger, ISyncExecutor executor, ISyncDependencies dependencies, IQuickBooksGateway gateway,
        IEnumerable<IQuickBooksSource> sources, TimeProvider clock, ILogger<SyncAdminService> logger, IServiceProvider services)
    {
        _services     = services;
        _db           = db;
        _connections  = connections;
        _providers    = providers;
        _outbox       = outbox;
        _ledger       = ledger;
        _executor     = executor;
        _dependencies = dependencies;
        _gateway      = gateway;
        _sources      = sources;
        _clock        = clock;
        _logger       = logger;
    }

    // ── Dashboard ────────────────────────────────────────────────────────────

    public async Task<SyncSummaryModel> GetSummaryAsync(CancellationToken ct = default)
    {
        var summary = new SyncSummaryModel();
        var connection = await _connections.GetCurrentAsync(ct);

        var counts = new List<(SyncKind Kind, SyncState State, int Count)>();

        if (connection is not null)
        {
            summary.ConnectionStatus = connection.Status.ToString();
            summary.Mode = (await _connections.GetOrCreateSettingsAsync(connection, ct)).Mode.ToString();

            var grouped = await _db.EntityMaps
                .Where(m => m.ConnectionId == connection.Id)
                .GroupBy(m => new { m.Kind, m.State })
                .Select(g => new { g.Key.Kind, g.Key.State, Count = g.Count() })
                .ToListAsync(ct);
            counts.AddRange(grouped.Select(g => (g.Kind, g.State, g.Count)));

            summary.QueueDepth = await _db.Outbox.CountAsync(e => e.ConnectionId == connection.Id
                && (e.Status == OutboxStatus.Queued || e.Status == OutboxStatus.Running), ct);

            summary.LastRunAt = await _db.SyncLog
                .Where(l => l.ConnectionId == connection.Id)
                .OrderByDescending(l => l.CreatedAt)
                .Select(l => (DateTime?)l.CreatedAt)
                .FirstOrDefaultAsync(ct);
        }

        foreach (var kind in Enum.GetValues<SyncKind>())
        {
            var model = new SyncKindSummaryModel { Kind = kind.ToString() };
            foreach (var state in Enum.GetValues<SyncState>())
                model.CountsByState[state.ToString()] = counts.Where(c => c.Kind == kind && c.State == state).Sum(c => c.Count);
            model.Total = model.CountsByState.Values.Sum();
            summary.Kinds.Add(model);
        }

        return summary;
    }

    public async Task<PaginatedResponse<SyncItemModel>> GetItemsAsync(SyncItemQuery query, CancellationToken ct = default)
    {
        query ??= new SyncItemQuery();
        var page     = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var response = new PaginatedResponse<SyncItemModel> { Page = page, PageSize = pageSize };

        var connection = await _connections.GetCurrentAsync(ct);
        if (connection is null) return response;

        var maps = _db.EntityMaps.AsNoTracking().Where(m => m.ConnectionId == connection.Id);

        if (!string.IsNullOrWhiteSpace(query.Kind))
        {
            var kind = SyncKindNames.Parse(query.Kind);
            maps = maps.Where(m => m.Kind == kind);
        }

        if (!string.IsNullOrWhiteSpace(query.State))
        {
            if (!Enum.TryParse<SyncState>(query.State.Trim(), ignoreCase: true, out var state) || !Enum.IsDefined(state))
                throw new BadRequestException($"Unknown state '{query.State}'.");
            maps = maps.Where(m => m.State == state);
        }

        // D-11: the dead letter — retries ran out, refused, or the outcome needs a person.
        if (query.DeadLetter == true)
            maps = maps.Where(m => m.State == SyncState.Failed || m.State == SyncState.NeedsResolution);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var s = query.Search.Trim();
            maps = maps.Where(m => m.DisplayLabel.Contains(s)
                                || m.ExternalId.Contains(s)
                                || (m.RemoteDocNumber != null && m.RemoteDocNumber.Contains(s)));
        }

        response.TotalRecords = await maps.CountAsync(ct);
        response.TotalPages   = (int)Math.Ceiling(response.TotalRecords / (double)pageSize);

        var rows = await maps
            .OrderByDescending(m => m.ModifiedDate ?? m.CreatedDate)
            .ThenByDescending(m => m.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        response.Data = await ToModelsAsync(connection, rows, ct);
        return response;
    }

    public async Task<List<SyncLogModel>> GetLogAsync(Guid itemId, CancellationToken ct = default)
    {
        var map = await FindMapAsync(itemId, ct);

        return await _db.SyncLog
            .AsNoTracking()
            .Where(l => l.EntityMapId == map.Id)
            .OrderByDescending(l => l.CreatedAt)
            .ThenByDescending(l => l.Id)
            .Take(200)
            .Select(l => new SyncLogModel
            {
                Id           = l.Uuid,
                Operation    = l.Operation,
                Outcome      = l.Outcome,
                ErrorCode    = l.ErrorCode,
                Message      = l.Message,
                DurationMs   = l.DurationMs,
                IntuitTid    = l.IntuitTid,
                CreatedAt    = l.CreatedAt,
                RequestJson  = l.RequestJson,
                ResponseJson = l.ResponseJson
            })
            .ToListAsync(ct);
    }

    // ── Actions ──────────────────────────────────────────────────────────────

    public async Task<SyncItemModel> RetryAsync(Guid itemId, CancellationToken ct = default)
    {
        var map   = await FindMapAsync(itemId, ct);
        var entry = await RequeueAsync(map, ct);

        var outcome = await _executor.ExecuteAsync(entry.Id, ct);
        _logger.LogInformation("Manual retry of {Kind} {ExternalId}: {Outcome}.", map.Kind, map.ExternalId, outcome.Status);

        return await ToModelAsync(map.Id, ct);
    }

    public async Task<SyncItemModel> ResolveAsync(Guid itemId, ResolveSyncItemRequest request, int userId, CancellationToken ct = default)
    {
        var map = await FindMapAsync(itemId, ct);
        var now = Now;

        switch (request?.Action?.Trim().ToLowerInvariant())
        {
            case "linkremote":
            {
                var remoteId = request.RemoteId?.Trim();
                if (string.IsNullOrEmpty(remoteId))
                    throw new BadRequestException("Give the id of the QuickBooks record to link to.");
                if (remoteId.Length > 50)
                    throw new BadRequestException("That is not a QuickBooks id (too long).");

                var takenBy = await _db.EntityMaps
                    .Where(m => m.ConnectionId == map.ConnectionId && m.Kind == map.Kind && m.RemoteId == remoteId && m.Id != map.Id)
                    .Select(m => m.DisplayLabel)
                    .FirstOrDefaultAsync(ct);
                if (takenBy is not null)
                    throw new BadRequestException($"QuickBooks record {remoteId} is already linked to '{takenBy}'.");

                await EnsureNotRunningAsync(map, ct);

                if (map.RemoteId != remoteId)
                {
                    map.RemoteSyncToken = null;
                    map.RemoteName      = null;
                }
                map.RemoteId       = remoteId;
                map.LinkOrigin     = LinkOrigin.Adopted;
                map.LinkedByUserId = userId;
                map.LinkedAt       = now;
                map.State          = await LastOperationAsync(map, ct) == OutboxOperation.Void ? SyncState.Voided : SyncState.Synced;
                // A person says this QuickBooks record is the one; it is not overwritten until SCM's data changes.
                map.LastPushedFingerprint = map.PayloadFingerprint;
                ClearErrors(map, now);
                await _outbox.CloseOpenEntriesAsync(map, OutboxStatus.Done, "Resolved: linked by a user.", now, ct);
                await _db.SaveChangesAsync(ct);
                await _dependencies.ReleaseDependentsAsync(map, dryRun: false, now, ct);
                break;
            }

            case "markresolved":
            {
                await EnsureNotRunningAsync(map, ct);

                if (map.RemoteId is not null)
                {
                    map.State = await LastOperationAsync(map, ct) == OutboxOperation.Void ? SyncState.Voided : SyncState.Synced;
                    map.LastPushedFingerprint = map.PayloadFingerprint;
                }
                else
                {
                    map.State = SyncState.NotSynced;
                }
                ClearErrors(map, now);
                await _outbox.CloseOpenEntriesAsync(map, OutboxStatus.Done, "Resolved by a user without sending.", now, ct);
                await _db.SaveChangesAsync(ct);
                break;
            }

            case "requeue":
                await RequeueAsync(map, ct);
                break;

            default:
                throw new BadRequestException($"Unknown action '{request?.Action}'. Use LinkRemote, MarkResolved or Requeue.");
        }

        return await ToModelAsync(map.Id, ct);
    }

    public async Task<ManualPushResult> PushAsync(ManualPushRequest request, CancellationToken ct = default)
    {
        var kind = SyncKindNames.Parse(request?.Kind);
        var ids  = CleanIds(request!.ExternalIds);
        if (ids.Count == 0) throw new BadRequestException("Send at least one ExternalId.");

        var connection = await RequireUsableConnectionAsync(ct);
        var settings   = await _connections.GetOrCreateSettingsAsync(connection, ct);
        var now = Now;
        var skipped = new List<string>();
        var unseenDocuments = new List<string>();

        foreach (var id in ids)
        {
            var map = await _db.EntityMaps.FirstOrDefaultAsync(m => m.ConnectionId == connection.Id
                                                                 && m.SourceSystem == QuickBooksSourceSystems.Scm
                                                                 && m.Kind == kind && m.ExternalId == id, ct);

            // "Skip" in matching means not synced — the gateway, the revalidator and dependencies all honour it, and so
            // does a push (it once created skipped items in QuickBooks: found live, 2026-10-13).
            if (map is not null && await _db.MatchCandidates.AnyAsync(c => c.EntityMapId == map.Id && c.Decision == MatchDecision.Skip, ct))
            {
                skipped.Add(id);
                continue;
            }

            // REV-20 / D-69b: a document the gateway has never seen gets no placeholder — an empty document map misleads
            // the callers' rules (an empty POS invoice next to a synced sales receipt). Only what SCM sends below creates it.
            if (map is null && !SyncPayloads.IsMasterData(kind))
            {
                unseenDocuments.Add(id);
                continue;
            }

            if (map is null)
            {
                map = new EntityMap
                {
                    OrganizationId = connection.OrganizationId,
                    ConnectionId   = connection.Id,
                    SourceSystem   = QuickBooksSourceSystems.Scm,
                    Kind           = kind,
                    ExternalId     = id,
                    DisplayLabel   = id,
                    State          = SyncState.NotSynced,
                    CreatedDate    = now
                };
                _db.EntityMaps.Add(map);
            }

            // Explicitly wanted: overrides the partner/item scope from now on.
            map.RequestedAt = now;

            // A held POS sale is not queued directly: the source's fresh send below goes through the gateway, which
            // holds it while "Sync POS sales" is off and queues it once it is on.
            if (map.PayloadJson is not null && map.State != SyncState.Voided && !SyncHolds.IsHeldCode(map.LastErrorCode)
                && !await _outbox.HasRunningEntryAsync(map, ct))
            {
                await _ledger.ForgetRefusalsAsync(map, ct);
                await _outbox.EnqueueAsync(map, OutboxOperation.Upsert, now, ct: ct);
            }
        }

        await _db.SaveChangesAsync(ct);

        // Ask SCM for fresh data too; what it sends merges into the entries queued above.
        var toPush = ids.Except(skipped).ToList();
        if (toPush.Count > 0)
            foreach (var source in SourcesFor(kind))
            {
                try
                {
                    await source.PushAsync(kind, toPush, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "QuickBooks source {Source} failed to push {Kind} records.", source.GetType().Name, kind);
                }
            }

        // Documents seen for the first time: the map exists now only if SCM sent the document through the gateway. Those
        // are marked wanted; the rest are reported — there was nothing to send.
        var notFound = new List<string>();
        if (unseenDocuments.Count > 0)
        {
            _db.ChangeTracker.Clear();
            var arrived = await _db.EntityMaps
                .Where(m => m.ConnectionId == connection.Id && m.SourceSystem == QuickBooksSourceSystems.Scm
                         && m.Kind == kind && unseenDocuments.Contains(m.ExternalId))
                .ToListAsync(ct);

            foreach (var map in arrived) map.RequestedAt ??= now;
            if (arrived.Count > 0) await _db.SaveChangesAsync(ct);

            notFound = unseenDocuments.Except(arrived.Select(m => m.ExternalId), StringComparer.Ordinal).ToList();
        }

        return new ManualPushResult { Requested = toPush.Count - notFound.Count, SkippedInMatching = skipped, NotFound = notFound };
    }

    public async Task<BackfillResult> BackfillAsync(SyncKind kind, CancellationToken ct = default)
    {
        await RequireUsableConnectionAsync(ct);

        var sent = 0;
        foreach (var source in SourcesFor(kind))
            sent += await source.PushAllAsync(kind, null, ct);

        return new BackfillResult { Kind = kind.ToString(), Sent = sent };
    }

    public async Task<StatusLookupResult> LookupStatusAsync(StatusLookupRequest request, CancellationToken ct = default)
    {
        var kind = SyncKindNames.Parse(request?.Kind);
        var ids  = CleanIds(request!.ExternalIds);

        var items = await _gateway.GetStatusAsync(kind, ids, ct);
        return new StatusLookupResult { Items = items.ToList() };
    }

    // ── A44: unlink, dead letter ─────────────────────────────────────────────

    /// <summary>LastErrorCode of a dead-letter record a person dismissed (D-11).</summary>
    public const string DismissedCode = "DISMISSED";

    public async Task<SyncItemModel> UnlinkAsync(Guid itemId, int userId, CancellationToken ct = default)
    {
        var map = await FindMapAsync(itemId, ct);
        if (!SyncPayloads.IsMasterData(map.Kind))
            throw new BadRequestException($"Unlinking is for customers, vendors and items, not {map.Kind}. Void or flag the document instead.");
        if (map.RemoteId is null)
            throw new BadRequestException($"'{map.DisplayLabel}' is not linked to a QuickBooks record.");
        await EnsureNotRunningAsync(map, ct);

        // D-17: documents already in QuickBooks against this record would lose their party / item here.
        var open = await OpenDocumentsUsingAsync(map, ct);
        if (open.Count > 0)
            throw new IntegrationConflictException(
                $"'{map.DisplayLabel}' is used by {open.Count} open document(s) synced to QuickBooks "
              + $"({string.Join(", ", open.Take(5))}{(open.Count > 5 ? ", …" : string.Empty)}). It cannot be unlinked while they are open.",
                new UnlinkRefusedModel { OpenDocuments = open.Count, Examples = open.Take(20).ToList() });

        var now    = Now;
        var before = new { Kind = map.Kind.ToString(), map.ExternalId, map.RemoteId, map.RemoteName, LinkOrigin = map.LinkOrigin?.ToString() };

        await _outbox.CloseOpenEntriesAsync(map, OutboxStatus.Done, "Unlinked by a user.", now, ct);
        map.RemoteId              = null;
        map.RemoteSyncToken       = null;
        map.RemoteName            = null;
        map.RemoteDocNumber       = null;
        map.LinkOrigin            = null;
        map.LinkedByUserId        = null;
        map.LinkedAt              = null;
        map.LastPushedFingerprint = null;
        map.State                 = SyncState.NotSynced;
        ClearErrors(map, now);

        // The ledger would otherwise answer a later create with the old record ("already done"), and an old proposal
        // would offer it again as decided: both go, so a rescan or a push starts clean.
        _db.CommandClaims.RemoveRange(await _db.CommandClaims.Where(c => c.EntityMapId == map.Id).ToListAsync(ct));
        _db.MatchCandidates.RemoveRange(await _db.MatchCandidates.Where(c => c.EntityMapId == map.Id).ToListAsync(ct));

        IntegrationAudit.Add(_db, map.ConnectionId, AuditAreas.Link, $"Unlinked {map.Kind}", before,
            new { Kind = map.Kind.ToString(), map.ExternalId, RemoteId = (string?)null }, userId);
        await _db.SaveChangesAsync(ct);

        return await ToModelAsync(map.Id, ct);
    }

    /// <summary>Labels of the synced, not voided documents (same source system) whose payload references the record.</summary>
    private async Task<List<string>> OpenDocumentsUsingAsync(EntityMap map, CancellationToken ct)
    {
        var encoded = SyncPayloads.EncodedForContains(map.ExternalId);
        var candidates = await _db.EntityMaps.AsNoTracking()
            .Where(m => m.ConnectionId == map.ConnectionId && m.SourceSystem == map.SourceSystem
                     && m.Kind != SyncKind.Customer && m.Kind != SyncKind.Vendor && m.Kind != SyncKind.Item
                     && m.RemoteId != null && m.State != SyncState.Voided
                     && m.PayloadJson != null && m.PayloadJson.Contains(encoded))
            .Select(m => new { m.Kind, m.PayloadJson, m.DisplayLabel, m.RemoteDocNumber })
            .ToListAsync(ct);

        var open = new List<string>();
        foreach (var c in candidates)
        {
            bool uses;
            try
            {
                uses = SyncPayloads.DependenciesOf(c.Kind, SyncPayloads.Deserialize(c.Kind, c.PayloadJson!))
                    .Any(d => d.Kind == map.Kind && string.Equals(d.ExternalId, map.ExternalId, StringComparison.Ordinal));
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentOutOfRangeException or NotSupportedException)
            {
                uses = true;   // cannot tell: refuse rather than orphan it
            }
            if (uses) open.Add(c.RemoteDocNumber ?? c.DisplayLabel);
        }
        return open;
    }

    public async Task<RetryFailedResult> RetryFailedAsync(RetryFailedRequest request, int userId, CancellationToken ct = default)
    {
        var connection = await RequireUsableConnectionAsync(ct);
        var maps = _db.EntityMaps.Where(m => m.ConnectionId == connection.Id
                                          && (m.State == SyncState.Failed || m.State == SyncState.NeedsResolution));
        if (!string.IsNullOrWhiteSpace(request?.Kind))
        {
            var kind = SyncKindNames.Parse(request.Kind);
            maps = maps.Where(m => m.Kind == kind);
        }

        var dead   = await maps.OrderBy(m => m.Id).Take(MaxIdsPerRequest).ToListAsync(ct);
        var result = new RetryFailedResult { Found = dead.Count };

        foreach (var map in dead)
        {
            try
            {
                await RequeueAsync(map, ct);
                result.Requeued++;
            }
            catch (Exception ex) when (ex is BadRequestException or ConflictException)
            {
                result.Skipped.Add(new RetrySkippedItemModel
                {
                    Id = map.Uuid, Kind = map.Kind.ToString(), DisplayLabel = map.DisplayLabel, Reason = ex.Message
                });
            }
        }

        _logger.LogInformation("Bulk retry by user {UserId}: {Requeued} of {Found} dead-letter record(s) re-queued.", userId, result.Requeued, result.Found);
        return result;
    }

    public async Task<SyncItemModel> SkipAsync(Guid itemId, int userId, CancellationToken ct = default)
    {
        var map = await FindMapAsync(itemId, ct);
        if (map.State is not (SyncState.Failed or SyncState.NeedsResolution or SyncState.Blocked))
            throw new BadRequestException($"Only failed, blocked or unresolved records can be dismissed; this one is {map.State}.");
        await EnsureNotRunningAsync(map, ct);

        var now      = Now;
        var previous = map.LastError;
        await _outbox.CloseOpenEntriesAsync(map, OutboxStatus.Done, "Dismissed by a user.", now, ct);

        // What QuickBooks has stays as it is; nothing more goes until SCM's data changes (a new fingerprint).
        map.State = map.RemoteId is null ? SyncState.NotSynced
                  : await LastOperationAsync(map, ct) == OutboxOperation.Void ? SyncState.Voided : SyncState.Synced;
        map.LastErrorCode = DismissedCode;
        map.LastError     = SyncText.Clip($"Dismissed by user {userId} on {now:dd MMM yyyy HH:mm} UTC." + (previous is null ? "" : $" Was: {previous}"), 2000);
        map.ModifiedDate  = now;
        await _db.SaveChangesAsync(ct);

        return await ToModelAsync(map.Id, ct);
    }

    // ── A44 D-34: the call log across all records ────────────────────────────

    /// <summary>Outcomes that are not failures (the "failed only" filter leaves them out).</summary>
    private static readonly string[] SuccessOutcomes = [nameof(ProviderOutcomeKind.Succeeded), "DryRun"];

    /// <remarks>
    /// Scoped to the current organization's connection explicitly (as the sync items are): the seek is on
    /// (ConnectionId, CreatedAt) — the existing IX_SyncLog_ConnectionId_CreatedAt — newest first; the other filters are
    /// residual over at most the 90 days the retention job keeps. Request/response bodies are left out of the list.
    /// </remarks>
    public async Task<PaginatedResponse<SyncLogItemModel>> GetCallLogAsync(SyncLogQuery query, CancellationToken ct = default)
    {
        query ??= new SyncLogQuery();
        var page     = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var response = new PaginatedResponse<SyncLogItemModel> { Page = page, PageSize = pageSize };

        var connection = await _connections.GetCurrentAsync(ct);
        if (connection is null) return response;

        var rows = from l in _db.SyncLog.AsNoTracking().Where(l => l.ConnectionId == connection.Id)
                   join m in _db.EntityMaps.AsNoTracking() on l.EntityMapId equals m.Id into maps
                   from m in maps.DefaultIfEmpty()
                   select new { Log = l, Map = m };

        if (query.FailedOnly)
            rows = rows.Where(r => !SuccessOutcomes.Contains(r.Log.Outcome));
        // QA-7: what still needs a person — the record is not Synced / Voided now. Calls that belong to no record
        // (matching scans, imports) were answered on the screen that made them and are left out too (D-69a).
        if (query.UnresolvedOnly)
            rows = rows.Where(r => r.Map != null && r.Map.State != SyncState.Synced && r.Map.State != SyncState.Voided);
        if (!string.IsNullOrWhiteSpace(query.Kind))
        {
            var kind = SyncKindNames.Parse(query.Kind);
            rows = rows.Where(r => r.Map != null && r.Map.Kind == kind);
        }
        if (!string.IsNullOrWhiteSpace(query.Operation))
        {
            var operation = query.Operation.Trim();
            rows = rows.Where(r => r.Log.Operation == operation);
        }
        if (!string.IsNullOrWhiteSpace(query.Outcome))
        {
            var outcome = query.Outcome.Trim();
            rows = rows.Where(r => r.Log.Outcome == outcome);
        }
        if (query.From is { } from) rows = rows.Where(r => r.Log.CreatedAt >= from);
        if (query.To is { } to)
        {
            var end = to.TimeOfDay == TimeSpan.Zero ? to.Date.AddDays(1) : to.AddTicks(1);
            rows = rows.Where(r => r.Log.CreatedAt < end);
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var s = query.Search.Trim();
            rows = rows.Where(r => (r.Log.ErrorCode != null && r.Log.ErrorCode.Contains(s))
                                || (r.Log.Message != null && r.Log.Message.Contains(s))
                                || (r.Log.IntuitTid != null && r.Log.IntuitTid.Contains(s))
                                || (r.Map != null && (r.Map.DisplayLabel.Contains(s)
                                                   || r.Map.ExternalId.Contains(s)
                                                   || (r.Map.RemoteDocNumber != null && r.Map.RemoteDocNumber.Contains(s))
                                                   || (r.Map.RemoteName != null && r.Map.RemoteName.Contains(s)))));
        }

        response.TotalRecords = await rows.CountAsync(ct);
        response.TotalPages   = (int)Math.Ceiling(response.TotalRecords / (double)pageSize);

        var pageRows = await rows
            .OrderByDescending(r => r.Log.CreatedAt)
            .ThenByDescending(r => r.Log.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var provider = TryProvider(connection);
        response.Data = pageRows.Select(r => ToLogModel(r.Log, r.Map, connection, provider, withBodies: false)).ToList();
        return response;
    }

    public async Task<SyncLogItemModel> GetCallLogEntryAsync(Guid id, CancellationToken ct = default)
    {
        var connection = await _connections.GetCurrentAsync(ct) ?? throw new NotFoundException("Sync log entry", id);

        // The current organization's connection only, as the list (a super admin's request bypasses the tenant filter).
        var log = await _db.SyncLog.AsNoTracking().FirstOrDefaultAsync(l => l.Uuid == id && l.ConnectionId == connection.Id, ct)
            ?? throw new NotFoundException("Sync log entry", id);
        var map = log.EntityMapId is { } mapId ? await _db.EntityMaps.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mapId, ct) : null;

        return ToLogModel(log, map, connection, TryProvider(connection), withBodies: true);
    }

    private IAccountingProvider? TryProvider(IntegrationConnection connection)
    {
        try
        {
            return _providers.Get(connection.ProviderKey);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Bodies are returned as stored: every writer of SyncLog (SyncExecutor, MatchingService) passes message, request
    /// and response through <c>Redactor</c> before saving (D-34 / plan: redacted at the boundary, never afterwards).
    /// </summary>
    private static SyncLogItemModel ToLogModel(
        SyncLogEntry l, EntityMap? m, IntegrationConnection connection, IAccountingProvider? provider, bool withBodies) => new()
    {
        Id              = l.Uuid,
        CreatedAt       = l.CreatedAt,
        Kind            = m?.Kind.ToString(),
        ItemId          = m?.Uuid,
        ExternalId      = m?.ExternalId,
        SourceSystem    = m?.SourceSystem,
        DisplayLabel    = m?.DisplayLabel,
        Operation       = l.Operation,
        Outcome         = l.Outcome,
        ErrorCode       = l.ErrorCode,
        Message         = l.Message,
        DurationMs      = l.DurationMs,
        IntuitTid       = l.IntuitTid,
        State           = m?.State.ToString(),
        RemoteId        = m?.RemoteId,
        RemoteDocNumber = m?.RemoteDocNumber,
        DeepLink        = m?.RemoteId is { } remote ? provider?.BuildDeepLink(connection.Environment, m.Kind, remote) : null,
        RequestJson     = withBodies ? l.RequestJson : null,
        ResponseJson    = withBodies ? l.ResponseJson : null
    };

    // ── Plumbing ─────────────────────────────────────────────────────────────

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private IEnumerable<IQuickBooksSource> SourcesFor(SyncKind kind) => _sources.Where(s => s.Kinds.Contains(kind));

    private async Task<SyncOutboxEntry> RequeueAsync(EntityMap map, CancellationToken ct)
    {
        if (map.PayloadJson is null)
            throw new BadRequestException("Nothing to send: the gateway has not received this record's data yet. Use Push now.");
        if (map.State == SyncState.Voided)
            throw new BadRequestException("This invoice is voided; there is nothing more to send.");
        if (Gateway.QuickBooksGateway.IsFlaggedForAccountant(map))
            throw new BadRequestException(
                $"This record was flagged for the accountant: {map.LastError} Sending it again would undo that. " +
                "Deal with it in QuickBooks, then use Resolve → Mark resolved.");
        await EnsureNotRunningAsync(map, ct);

        var connection = await _db.Connections.FirstOrDefaultAsync(c => c.Id == map.ConnectionId, ct);
        if (connection is null || !connection.IsUsable())
            throw new ConflictException("QuickBooks is not connected. Reconnect, then retry.");

        // "Skip" in matching means not synced; a retry does not override it either.
        if (await _db.MatchCandidates.AnyAsync(c => c.EntityMapId == map.Id && c.Decision == MatchDecision.Skip, ct))
            throw new ConflictException(
                $"{map.DisplayLabel ?? "This record"} was skipped in matching, so it is not sent to QuickBooks. " +
                "Change its decision under Match existing first.");

        // A44 D-14: held while its document type is off (POS sales, or any type disabled with hold).
        if (SyncHolds.IsHeldCode(map.LastErrorCode))
        {
            var type     = SyncHolds.TypeOf(map);
            var settings = await _connections.GetOrCreateSettingsAsync(connection, ct);
            var enabled  = _services.GetService<ISyncControlReader>() is { } reader
                ? (await reader.CheckAsync(connection.Id, type, ct)).IsEnabled
                : SyncControlDefaults.Effective(null, settings, type).IsEnabled;
            if (!enabled)
                throw new BadRequestException(SyncHolds.IsPosType(type)
                    ? "This POS sale is held because \"Sync POS sales\" (or sales invoices) is off in the QuickBooks settings. " +
                      "Turn it on there; the held sales are then sent."
                    : $"This record is held because {SyncControlDefaults.Labels[type].ToLowerInvariant()} are turned off under Sync control. " +
                      "Turn them on there; the held records are then sent.");
            ClearErrors(map, Now);
        }

        // A person asked for another try: forget "QuickBooks said no to exactly this" so it really is sent.
        await _ledger.ForgetRefusalsAsync(map, ct);

        var operation = await LastOperationAsync(map, ct);
        var entry = await _outbox.EnqueueAsync(map, operation, Now, ct: ct);
        await _db.SaveChangesAsync(ct);
        return entry;
    }

    private async Task<OutboxOperation> LastOperationAsync(EntityMap map, CancellationToken ct) =>
        await _db.Outbox.Where(e => e.EntityMapId == map.Id)
            .OrderByDescending(e => e.Id)
            .Select(e => (OutboxOperation?)e.Operation)
            .FirstOrDefaultAsync(ct) ?? OutboxOperation.Upsert;

    private async Task EnsureNotRunningAsync(EntityMap map, CancellationToken ct)
    {
        if (await _outbox.HasRunningEntryAsync(map, ct))
            throw new ConflictException("This record is being sent to QuickBooks right now. Try again in a minute.");
    }

    private async Task<IntegrationConnection> RequireUsableConnectionAsync(CancellationToken ct)
    {
        var connection = await _connections.GetCurrentAsync(ct);
        if (connection is null || !connection.IsUsable())
            throw new ConflictException("QuickBooks is not connected. Connect it first.");
        return connection;
    }

    /// <remarks>
    /// Limited to the current organization <b>explicitly</b>, not only through the tenant query filter: a super
    /// admin's requests bypass that filter, and retrying, resolving or reading another organization's record from
    /// here would validate and build it with <i>this</i> organization's exchange rates and tax codes (SMS.Shared's
    /// lookups read the current tenant) — the same reason <c>ConnectionAccessor</c> filters explicitly.
    /// </remarks>
    private async Task<EntityMap> FindMapAsync(Guid itemId, CancellationToken ct)
    {
        var organizationId = _db.TenantContext.OrganizationId;
        return await _db.EntityMaps.FirstOrDefaultAsync(m => m.Uuid == itemId && m.OrganizationId == organizationId, ct)
            ?? throw new NotFoundException("Sync item", itemId);
    }

    private static void ClearErrors(EntityMap map, DateTime now)
    {
        map.LastErrorCode = null;
        map.LastError     = null;
        map.ModifiedDate  = now;
    }

    private static List<string> CleanIds(List<string>? ids)
    {
        var clean = (ids ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct().ToList();
        if (clean.Count > MaxIdsPerRequest)
            throw new BadRequestException($"At most {MaxIdsPerRequest} ids per request.");
        return clean;
    }

    private async Task<SyncItemModel> ToModelAsync(int mapId, CancellationToken ct)
    {
        var map = await _db.EntityMaps.AsNoTracking().FirstAsync(m => m.Id == mapId, ct);
        var connection = await _db.Connections.AsNoTracking().FirstAsync(c => c.Id == map.ConnectionId, ct);
        return (await ToModelsAsync(connection, [map], ct))[0];
    }

    private async Task<List<SyncItemModel>> ToModelsAsync(IntegrationConnection connection, List<EntityMap> maps, CancellationToken ct)
    {
        var ids = maps.Select(m => m.Id).ToList();
        var attempts = (await _db.Outbox.AsNoTracking()
                .Where(e => ids.Contains(e.EntityMapId))
                .Select(e => new { e.Id, e.EntityMapId, e.AttemptCount })
                .ToListAsync(ct))
            .GroupBy(e => e.EntityMapId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.Id).First().AttemptCount);

        IAccountingProvider? provider = null;
        try
        {
            provider = _providers.Get(connection.ProviderKey);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            // No provider registered: the list still works, without deep links.
        }

        return maps.Select(m => new SyncItemModel
        {
            Id              = m.Uuid,
            Kind            = m.Kind.ToString(),
            ExternalId      = m.ExternalId,
            SourceSystem    = m.SourceSystem,
            DisplayLabel    = m.DisplayLabel,
            State           = m.State.ToString(),
            RemoteId        = m.RemoteId,
            RemoteDocNumber = m.RemoteDocNumber,
            DeepLink        = m.RemoteId is null ? null : provider?.BuildDeepLink(connection.Environment, m.Kind, m.RemoteId),
            LastErrorCode   = m.LastErrorCode,
            LastError       = m.LastError,
            Warning         = m.Warning,
            LastSyncedAt    = m.LastSyncedAt,
            UpdatedAt       = m.ModifiedDate ?? m.PayloadReceivedAt ?? m.CreatedDate,
            AttemptCount    = attempts.TryGetValue(m.Id, out var a) ? a : 0
        }).ToList();
    }
}

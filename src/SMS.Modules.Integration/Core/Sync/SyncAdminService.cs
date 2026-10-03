using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Providers;
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
        }

        return !int.TryParse(v, out _) && Enum.TryParse(v, ignoreCase: true, out kind) && Enum.IsDefined(kind);
    }

    public static SyncKind Parse(string? value) =>
        TryParse(value, out var kind)
            ? kind
            : throw new BadRequestException($"Unknown kind '{value}'. Use Customer, Vendor, Item, SalesInvoice or Bill.");
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

    public SyncAdminService(
        IntegrationDbContext db, IConnectionAccessor connections, IAccountingProviderRegistry providers, ISyncOutbox outbox,
        ISyncLedger ledger, ISyncExecutor executor, ISyncDependencies dependencies, IQuickBooksGateway gateway,
        IEnumerable<IQuickBooksSource> sources, TimeProvider clock, ILogger<SyncAdminService> logger)
    {
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
        var now = Now;

        foreach (var id in ids)
        {
            var map = await _db.EntityMaps.FirstOrDefaultAsync(m => m.ConnectionId == connection.Id
                                                                 && m.SourceSystem == QuickBooksSourceSystems.Scm
                                                                 && m.Kind == kind && m.ExternalId == id, ct);
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

            if (map.PayloadJson is not null && map.State != SyncState.Voided && !await _outbox.HasRunningEntryAsync(map, ct))
            {
                await _ledger.ForgetRefusalsAsync(map, ct);
                await _outbox.EnqueueAsync(map, OutboxOperation.Upsert, now, ct: ct);
            }
        }

        await _db.SaveChangesAsync(ct);

        // Ask SCM for fresh data too; what it sends merges into the entries queued above.
        foreach (var source in SourcesFor(kind))
        {
            try
            {
                await source.PushAsync(kind, ids, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "QuickBooks source {Source} failed to push {Kind} records.", source.GetType().Name, kind);
            }
        }

        return new ManualPushResult { Requested = ids.Count };
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

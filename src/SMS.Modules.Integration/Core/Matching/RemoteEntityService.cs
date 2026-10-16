using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Core.Settings;
using SMS.Modules.Integration.Core.Sync;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Models;
using SMS.Shared.Exceptions;
using SMS.Shared.Integration.QuickBooks;
using SMS.Shared.Pagination;

namespace SMS.Modules.Integration.Core.Matching;

/// <summary>"Import from QuickBooks" on the Match tab (A44 D-17): QuickBooks' records as remote-only link candidates.</summary>
public interface IQuickBooksRemoteEntityService
{
    Task<RemoteImportResultModel> ImportAsync(SyncKind kind, CancellationToken ct = default);
    Task<PaginatedResponse<RemoteEntityModel>> ListAsync(SyncKind kind, RemoteEntityQuery query, CancellationToken ct = default);
    Task<RemoteEntityModel> LinkAsync(SyncKind kind, string remoteId, LinkRemoteEntityRequest request, int userId, CancellationToken ct = default);
}

/// <summary>
/// Reads every customer / vendor / item of the QuickBooks company into <c>integration.RemoteEntities</c> — <b>nothing is
/// created anywhere</b>: not in SCM (BR-18), not in QuickBooks. Linking one to an SCM record is the same as Link in matching
/// (adopted, synced, not pushed until SCM's data changes, dependents released). The gateway-side services are resolved when
/// used, so this registers with the connection services.
/// </summary>
internal sealed class RemoteEntityService : IQuickBooksRemoteEntityService
{
    private readonly IntegrationDbContext          _db;
    private readonly IConnectionAccessor           _accessor;
    private readonly IServiceProvider              _services;
    private readonly ILogger<RemoteEntityService>  _logger;

    public RemoteEntityService(IntegrationDbContext db, IConnectionAccessor accessor, IServiceProvider services, ILogger<RemoteEntityService> logger)
    {
        _db       = db;
        _accessor = accessor;
        _services = services;
        _logger   = logger;
    }

    private DateTime Now => (_services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow().UtcDateTime;

    // ── Import ──────────────────────────────────────────────────────────────────────────────

    public async Task<RemoteImportResultModel> ImportAsync(SyncKind kind, CancellationToken ct = default)
    {
        EnsureMasterData(kind);
        var connection = await _accessor.GetCurrentAsync(ct);
        if (connection is null || !connection.IsUsable())
            throw new ConflictException("QuickBooks is not connected. Connect it before importing its records.");

        var now    = Now;
        var remote = await MatchingService.ReadAllAsync(
            _db, _services.GetRequiredService<IAccountingProviderRegistry>(), _services.GetRequiredService<IConnectionHealth>(),
            _services.GetRequiredService<IOutboxControl>(), connection, kind, now, "Import from QuickBooks", ct);

        var existing = await _db.RemoteEntities.Where(r => r.ConnectionId == connection.Id && r.Kind == kind).ToListAsync(ct);
        var byId     = existing.ToDictionary(r => r.RemoteId, StringComparer.Ordinal);
        var seen     = new HashSet<string>(StringComparer.Ordinal);
        var result   = new RemoteImportResultModel { Kind = kind.ToString(), ImportedAt = now };

        foreach (var entry in remote.Where(e => !string.IsNullOrWhiteSpace(e.RemoteId)))
        {
            var id = entry.RemoteId.Trim();
            if (id.Length > 50 || !seen.Add(id)) continue;
            result.Read++;

            if (!byId.TryGetValue(id, out var row))
            {
                row = new RemoteEntityRecord { OrganizationId = connection.OrganizationId, ConnectionId = connection.Id, Kind = kind, RemoteId = id };
                _db.RemoteEntities.Add(row);
                byId[id] = row;
                result.Added++;
            }
            else
            {
                result.Updated++;
            }

            row.Name          = SyncText.Clip(entry.Name, 200) ?? string.Empty;
            row.CompanyName   = SyncText.Clip(entry.CompanyName, 200);
            row.Email         = SyncText.Clip(entry.Email, 200);
            row.TaxId         = SyncText.Clip(entry.TaxId, 50);
            row.AccountNumber = SyncText.Clip(entry.AccountNumber, 100);
            row.Sku           = SyncText.Clip(entry.Sku, 100);
            row.CurrencyCode  = SyncText.Clip(entry.CurrencyCode, 10);
            row.Active        = entry.Active;
            row.ImportedAt    = now;
        }

        // Gone from QuickBooks' list (deleted / merged there): no longer offered.
        foreach (var stale in existing.Where(r => !seen.Contains(r.RemoteId)))
        {
            _db.RemoteEntities.Remove(stale);
            result.Removed++;
        }

        await _db.SaveChangesAsync(ct);

        var linked = await LinkedRemoteIdsAsync(connection.Id, kind, ct);
        result.Linked   = seen.Count(linked.ContainsKey);
        result.Unlinked = result.Read - result.Linked;
        return result;
    }

    // ── List ────────────────────────────────────────────────────────────────────────────────

    public async Task<PaginatedResponse<RemoteEntityModel>> ListAsync(SyncKind kind, RemoteEntityQuery query, CancellationToken ct = default)
    {
        EnsureMasterData(kind);
        query ??= new RemoteEntityQuery();
        var page     = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var response = new PaginatedResponse<RemoteEntityModel> { Page = page, PageSize = pageSize };

        var connection = await _accessor.GetCurrentAsync(ct);
        if (connection is null) return response;

        var linked = await LinkedRemoteIdsAsync(connection.Id, kind, ct);
        var rows   = _db.RemoteEntities.AsNoTracking().Where(r => r.ConnectionId == connection.Id && r.Kind == kind);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var s = query.Search.Trim();
            rows = rows.Where(r => r.Name.Contains(s) || r.RemoteId == s
                                || (r.CompanyName != null && r.CompanyName.Contains(s))
                                || (r.Email != null && r.Email.Contains(s))
                                || (r.Sku != null && r.Sku.Contains(s)));
        }

        // Linked-ness lives on EntityMaps; filtered in memory (a company's list of one kind, not millions).
        var all = (await rows.OrderBy(r => r.Name).ThenBy(r => r.RemoteId).ToListAsync(ct))
            .Where(r => query.Linked is null || linked.ContainsKey(r.RemoteId) == query.Linked.Value)
            .ToList();

        response.TotalRecords = all.Count;
        response.TotalPages   = (int)Math.Ceiling(all.Count / (double)pageSize);

        var provider = TryProvider(connection);
        response.Data = all.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(r => ToModel(r, linked.GetValueOrDefault(r.RemoteId), provider, connection))
            .ToList();
        return response;
    }

    // ── Link ────────────────────────────────────────────────────────────────────────────────

    public async Task<RemoteEntityModel> LinkAsync(
        SyncKind kind, string remoteId, LinkRemoteEntityRequest request, int userId, CancellationToken ct = default)
    {
        EnsureMasterData(kind);
        var externalId = request?.ExternalId?.Trim();
        if (string.IsNullOrEmpty(externalId)) throw new BadRequestException("Give the SCM record's id (externalId) to link.");
        if (externalId.Length > 100) throw new BadRequestException("That id is longer than 100 characters.");

        var connection = await _accessor.GetCurrentAsync(ct) ?? throw new ConflictException("QuickBooks is not connected.");
        remoteId = (remoteId ?? string.Empty).Trim();

        var remote = await _db.RemoteEntities.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ConnectionId == connection.Id && r.Kind == kind && r.RemoteId == remoteId, ct)
            ?? throw new NotFoundException($"QuickBooks {kind}", remoteId);

        var map = await FindScmMapAsync(connection.Id, kind, externalId, ct);
        if (map is null)
        {
            // The gateway has not seen it yet: ask SCM to send it (it arrives through the gateway), then look again.
            foreach (var source in _services.GetServices<IQuickBooksSource>().Where(s => s.Kinds.Contains(kind)))
            {
                try
                {
                    await source.PushAsync(kind, [externalId], ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "QuickBooks source {Source} failed to send {Kind} {ExternalId} for linking.", source.GetType().Name, kind, externalId);
                }
            }
            _db.ChangeTracker.Clear();
            map = await FindScmMapAsync(connection.Id, kind, externalId, ct)
                ?? throw new NotFoundException($"SCM {kind}", externalId);
        }

        if (map.RemoteId == remoteId) return await ModelAsync(connection, remote, ct);
        if (map.RemoteId is not null)
            throw new BadRequestException(
                $"'{map.DisplayLabel}' is already linked to QuickBooks record {map.RemoteId}. Unlink it first (Sync tab).");

        var takenBy = await _db.EntityMaps
            .Where(m => m.ConnectionId == connection.Id && m.Kind == kind && m.RemoteId == remoteId && m.Id != map.Id)
            .Select(m => m.DisplayLabel)
            .FirstOrDefaultAsync(ct);
        if (takenBy is not null)
            throw new BadRequestException($"QuickBooks record {remoteId} is already linked to '{takenBy}'. One QuickBooks record can back only one {kind}.");

        var outbox = _services.GetRequiredService<ISyncOutbox>();
        if (await outbox.HasRunningEntryAsync(map, ct))
            throw new ConflictException($"'{map.DisplayLabel}' is being sent to QuickBooks right now. Wait a minute and link it then.");

        var now = Now;
        map.RemoteId        = remoteId;
        map.RemoteSyncToken = null;   // fetched before any later update
        map.RemoteName      = SyncText.Clip(remote.Name, 200);
        map.LinkOrigin      = LinkOrigin.Adopted;
        map.LinkedByUserId  = userId;
        map.LinkedAt        = now;
        map.State           = SyncState.Synced;
        // As matching's Link: the accountant's record is left as it is until SCM's data changes.
        map.LastPushedFingerprint = map.PayloadFingerprint;
        map.LastErrorCode   = null;
        map.LastError       = null;
        map.ModifiedDate    = now;
        await outbox.CloseOpenEntriesAsync(map, OutboxStatus.Done, "Linked to an imported QuickBooks record.", now, ct);

        // The match screen shows the decision too.
        foreach (var candidate in await _db.MatchCandidates.Where(c => c.EntityMapId == map.Id).ToListAsync(ct))
        {
            candidate.Decision   = MatchDecision.Link;
            candidate.RemoteId   = remoteId;
            candidate.RemoteName = map.RemoteName;
            candidate.DecidedBy  = userId;
            candidate.DecidedAt  = now;
        }

        IntegrationAudit.Add(_db, connection.Id, AuditAreas.Link, $"Linked {kind}", null,
            new { Kind = kind.ToString(), map.ExternalId, RemoteId = remoteId }, userId);
        await _db.SaveChangesAsync(ct);

        await _services.GetRequiredService<ISyncDependencies>().ReleaseDependentsAsync(map, dryRun: false, now, ct);

        return await ModelAsync(connection, remote, ct);
    }

    // ── Plumbing ────────────────────────────────────────────────────────────────────────────

    private static void EnsureMasterData(SyncKind kind)
    {
        if (!SyncPayloads.IsMasterData(kind))
            throw new BadRequestException($"Importing from QuickBooks is for customers, vendors and items, not {kind}.");
    }

    private Task<EntityMap?> FindScmMapAsync(int connectionId, SyncKind kind, string externalId, CancellationToken ct) =>
        _db.EntityMaps.FirstOrDefaultAsync(m => m.ConnectionId == connectionId && m.SourceSystem == QuickBooksSourceSystems.Scm
                                             && m.Kind == kind && m.ExternalId == externalId, ct);

    private sealed record LinkedMap(Guid Uuid, string ExternalId, string DisplayLabel);

    private async Task<Dictionary<string, LinkedMap>> LinkedRemoteIdsAsync(int connectionId, SyncKind kind, CancellationToken ct) =>
        (await _db.EntityMaps.AsNoTracking()
            .Where(m => m.ConnectionId == connectionId && m.Kind == kind && m.RemoteId != null)
            .Select(m => new { m.RemoteId, m.Uuid, m.ExternalId, m.DisplayLabel })
            .ToListAsync(ct))
        .GroupBy(m => m.RemoteId!, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => new LinkedMap(g.First().Uuid, g.First().ExternalId, g.First().DisplayLabel), StringComparer.Ordinal);

    private async Task<RemoteEntityModel> ModelAsync(IntegrationConnection connection, RemoteEntityRecord remote, CancellationToken ct)
    {
        var linked = await LinkedRemoteIdsAsync(connection.Id, remote.Kind, ct);
        return ToModel(remote, linked.GetValueOrDefault(remote.RemoteId), TryProvider(connection), connection);
    }

    private IAccountingProvider? TryProvider(IntegrationConnection connection)
    {
        try
        {
            return _services.GetService<IAccountingProviderRegistry>()?.Get(connection.ProviderKey);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static RemoteEntityModel ToModel(RemoteEntityRecord r, LinkedMap? linked, IAccountingProvider? provider, IntegrationConnection connection) => new()
    {
        RemoteId         = r.RemoteId,
        Kind             = r.Kind.ToString(),
        Name             = r.Name,
        CompanyName      = r.CompanyName,
        Email            = r.Email,
        TaxId            = r.TaxId,
        AccountNumber    = r.AccountNumber,
        Sku              = r.Sku,
        CurrencyCode     = r.CurrencyCode,
        Active           = r.Active,
        ImportedAt       = r.ImportedAt,
        Linked           = linked is not null,
        LinkedItemId     = linked?.Uuid,
        LinkedExternalId = linked?.ExternalId,
        LinkedLabel      = linked?.DisplayLabel,
        DeepLink         = provider?.BuildDeepLink(connection.Environment, r.Kind, r.RemoteId)
    };
}

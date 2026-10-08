using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Gateway;
using SMS.Modules.Integration.Gateway.Validation;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Core.Sync;

/// <summary>What started a re-validation pass.</summary>
internal enum RevalidationTrigger
{
    /// <summary>Settings, tax or term mappings were saved, or the reference data was refreshed: a bounded full pass.</summary>
    AfterChange,
    /// <summary>The outbox job, every run: a small batch, rotating through the connection's Blocked records.</summary>
    Periodic
}

internal sealed record RevalidationSummary(int Checked, int Released, int StillBlocked, int Skipped)
{
    public static readonly RevalidationSummary None = new(0, 0, 0, 0);
}

/// <summary>
/// Re-validates Blocked records from their stored payloads, so a record refused for a reason that lives outside
/// its payload — a tax code not mapped yet, an account not chosen, a currency not active in QuickBooks, an exchange
/// rate not entered in SMS — goes on its own once that is fixed, without anyone pressing Retry or saving it again.
/// A record that now passes is stored and queued exactly as a fresh upsert of the same payload would be
/// (<see cref="ISyncAdmission"/>); one that still fails keeps its state, with its reasons brought up to date.
/// <para>
/// Runs in a DI scope of its own (a fresh DbContext, so it never touches the caller's unit of work), as the
/// connection's own organization: SMS's exchange rates and tax codes are read for the CURRENT tenant, so a pass
/// started under any other organization does nothing.
/// </para>
/// </summary>
internal interface IBlockedRecordRevalidator
{
    Task<RevalidationSummary> RevalidateAsync(
        int connectionId, Guid organizationId, RevalidationTrigger trigger, CancellationToken ct = default);
}

/// <summary>Where each connection's periodic pass got to, so a long Blocked list is worked through in turn.</summary>
internal sealed class RevalidationCursor
{
    private readonly ConcurrentDictionary<int, int> _lastId = new();

    public int Get(int connectionId) => _lastId.TryGetValue(connectionId, out var id) ? id : 0;
    public void Set(int connectionId, int lastId) => _lastId[connectionId] = lastId;
}

internal sealed class BlockedRecordRevalidator : IBlockedRecordRevalidator
{
    /// <summary>
    /// The codes of refusals that depend on settings, mappings, QuickBooks' reference data, SMS's exchange rates or
    /// another record — not on the payload alone. VALIDATION_FAILED (several reasons at once) may include one.
    /// </summary>
    public static readonly string[] RevalidatableCodes =
    [
        "TAX_UNMAPPED",
        "SETTINGS_ACCOUNT_MISSING",
        "EXCHANGE_RATE_MISSING",
        "CURRENCY_NOT_ACTIVE",
        "CURRENCY_NOT_HOME",
        "CURRENCY_PARTY_MISMATCH",
        "PURCHASE_BASE_NOT_HOME",
        "VALIDATION_FAILED"
    ];

    /// <summary>At most this many records per pass after a change; the periodic pass picks up any rest.</summary>
    public const int MaxPerChange = 500;

    /// <summary>At most this many records per connection per periodic run (every minute).</summary>
    public const int PeriodicBatch = 50;

    private readonly IServiceScopeFactory _scopes;

    public BlockedRecordRevalidator(IServiceScopeFactory scopes) => _scopes = scopes;

    public async Task<RevalidationSummary> RevalidateAsync(
        int connectionId, Guid organizationId, RevalidationTrigger trigger, CancellationToken ct = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var pass = ActivatorUtilities.CreateInstance<RevalidationPass>(scope.ServiceProvider);
        return await pass.RunAsync(connectionId, organizationId, trigger, ct);
    }

    /// <summary>
    /// For the places a change is saved (settings, mappings, reference data): runs a pass when the gateway is
    /// registered at all, and never lets it fail the change that was just saved.
    /// </summary>
    public static async Task AfterChangeAsync(
        IServiceProvider services, IntegrationConnection connection, ILogger logger, CancellationToken ct)
    {
        var revalidator = services.GetService<IBlockedRecordRevalidator>();
        if (revalidator is null) return;

        try
        {
            var summary = await revalidator.RevalidateAsync(connection.Id, connection.OrganizationId, RevalidationTrigger.AfterChange, ct);
            if (summary.Released > 0)
                logger.LogInformation("QuickBooks connection {ConnectionId}: {Released} blocked record(s) passed validation again and were queued.",
                    connection.Id, summary.Released);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "QuickBooks connection {ConnectionId}: re-validating blocked records failed; the periodic pass will try again.",
                connection.Id);
        }
    }
}

/// <summary>One pass, in the scope <see cref="BlockedRecordRevalidator"/> made for it.</summary>
internal sealed class RevalidationPass
{
    private readonly IntegrationDbContext             _db;
    private readonly IConnectionAccessor              _accessor;
    private readonly IPayloadValidator                _validator;
    private readonly ISyncAdmission                   _admission;
    private readonly ISyncOutbox                      _outbox;
    private readonly RevalidationCursor               _cursor;
    private readonly TimeProvider                     _clock;
    private readonly ILogger<BlockedRecordRevalidator> _logger;

    public RevalidationPass(
        IntegrationDbContext db, IConnectionAccessor accessor, IPayloadValidator validator, ISyncAdmission admission,
        ISyncOutbox outbox, RevalidationCursor cursor, TimeProvider clock, ILogger<BlockedRecordRevalidator> logger)
    {
        _db        = db;
        _accessor  = accessor;
        _validator = validator;
        _admission = admission;
        _outbox    = outbox;
        _cursor    = cursor;
        _clock     = clock;
        _logger    = logger;
    }

    public async Task<RevalidationSummary> RunAsync(int connectionId, Guid organizationId, RevalidationTrigger trigger, CancellationToken ct)
    {
        // Exchange rates and tax codes come from SMS for the current tenant: never validate another
        // organization's records with them.
        if (_db.TenantContext.OrganizationId != organizationId)
        {
            _logger.LogWarning("QuickBooks re-validation of connection {ConnectionId} skipped: it belongs to organization {OrganizationId}, " +
                               "but the pass runs as {CurrentOrganization}.", connectionId, organizationId, _db.TenantContext.OrganizationId);
            return RevalidationSummary.None;
        }

        var connection = await _db.Connections.FirstOrDefaultAsync(c => c.Id == connectionId && c.OrganizationId == organizationId, ct);
        if (connection is null || !connection.IsUsable()) return RevalidationSummary.None;
        var settings = await _accessor.GetOrCreateSettingsAsync(connection, ct);

        var ids = await SelectAsync(connection.Id, trigger, ct);
        int released = 0, stillBlocked = 0, skipped = 0;

        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                switch (await RevalidateOneAsync(id, connection, settings, ct))
                {
                    case Outcome.Released:     released++;     break;
                    case Outcome.StillBlocked: stillBlocked++; break;
                    default:                   skipped++;      break;
                }
            }
            catch (DbUpdateException)
            {
                // Changed meanwhile (a new payload arrived, or another pass got there first): that write wins.
                skipped++;
            }
            finally
            {
                // Each record is its own unit of work; nothing in this scope needs what was tracked.
                _db.ChangeTracker.Clear();
            }
        }

        return new RevalidationSummary(ids.Count, released, stillBlocked, skipped);
    }

    private IQueryable<EntityMap> Candidates(int connectionId) =>
        _db.EntityMaps.Where(m => m.ConnectionId == connectionId
                               && m.State == SyncState.Blocked
                               && m.PayloadJson != null
                               && m.LastErrorCode != null
                               && BlockedRecordRevalidator.RevalidatableCodes.Contains(m.LastErrorCode));

    private async Task<List<int>> SelectAsync(int connectionId, RevalidationTrigger trigger, CancellationToken ct)
    {
        if (trigger == RevalidationTrigger.AfterChange)
            return await Candidates(connectionId).OrderBy(m => m.Id).Select(m => m.Id)
                .Take(BlockedRecordRevalidator.MaxPerChange).ToListAsync(ct);

        // Periodic: the next batch after where the last run stopped, wrapping round at the end — so every
        // Blocked record is looked at in turn, however many there are, at a fixed cost per run.
        var after = _cursor.Get(connectionId);
        var ids = await Candidates(connectionId).Where(m => m.Id > after).OrderBy(m => m.Id).Select(m => m.Id)
            .Take(BlockedRecordRevalidator.PeriodicBatch).ToListAsync(ct);

        if (ids.Count == 0 && after > 0)
            ids = await Candidates(connectionId).OrderBy(m => m.Id).Select(m => m.Id)
                .Take(BlockedRecordRevalidator.PeriodicBatch).ToListAsync(ct);

        _cursor.Set(connectionId, ids.Count < BlockedRecordRevalidator.PeriodicBatch ? 0 : ids[^1]);
        return ids;
    }

    private enum Outcome { Skipped, StillBlocked, Released }

    private async Task<Outcome> RevalidateOneAsync(int mapId, IntegrationConnection connection, IntegrationSettings settings, CancellationToken ct)
    {
        var map = await _db.EntityMaps.FirstOrDefaultAsync(m => m.Id == mapId, ct);
        if (map is null || map.State != SyncState.Blocked || map.PayloadJson is null) return Outcome.Skipped;

        // What a fresh upsert would answer "Disabled" to (and so leave alone): auto-push off for the kind, the
        // record skipped in matching, a document dated before the start date.
        if (!QuickBooksGateway.AutoPush(settings, map.Kind)) return Outcome.Skipped;
        if (await _db.MatchCandidates.AnyAsync(c => c.EntityMapId == map.Id && c.Decision == MatchDecision.Skip, ct)) return Outcome.Skipped;

        object payload;
        try
        {
            payload = SyncPayloads.Deserialize(map.Kind, map.PayloadJson);
        }
        catch (JsonException)
        {
            return Outcome.Skipped;
        }

        if (QuickBooksGateway.DocumentDate(payload) is { } date && settings.DocumentStartDate is { } start && date.Date < start.Date)
            return Outcome.Skipped;

        var now         = _clock.GetUtcNow().UtcDateTime;
        var fingerprint = map.PayloadFingerprint ?? SyncPayloads.Fingerprint(map.PayloadJson);

        if (map.RemoteId is not null && map.LastPushedFingerprint == fingerprint)
        {
            // As a fresh upsert: QuickBooks already has exactly this, so there is nothing to validate or send.
            map.State         = SyncState.Synced;
            map.LastErrorCode = null;
            map.LastError     = null;
            map.ModifiedDate  = now;
            await _outbox.CloseOpenEntriesAsync(map, OutboxStatus.Done, "Superseded: QuickBooks already has this data.", now, ct);
            await _db.SaveChangesAsync(ct);
            return Outcome.Released;
        }

        var validation = await _validator.ValidateAsync(map.Kind, payload, map, connection, settings, ct);
        var warning    = validation.Warnings.Count == 0 ? null : SyncText.Clip(string.Join(" ", validation.Warnings), 1000);

        if (!validation.IsValid)
        {
            // Still refused. Only the reasons are brought up to date (a different date's rate, one code of two now
            // mapped…); nothing is written when nothing changed, so repeated passes cost no writes.
            var code    = SyncAdmission.ErrorCodeOf(validation);
            var message = SyncAdmission.MessageOf(validation);
            var clipped = SyncText.Clip(message, 2000);
            if (map.LastErrorCode != code || map.LastError != clipped || map.Warning != warning)
            {
                map.LastErrorCode = code;
                map.LastError     = clipped;
                map.Warning       = warning;
                await _outbox.BlockPendingAsync(map, message, ct);
                await _db.SaveChangesAsync(ct);
            }
            return Outcome.StillBlocked;
        }

        map.Warning      = warning;
        map.ModifiedDate = now;
        await _admission.AdmitAsync(map.Kind, payload, map, settings, now, ct);

        _logger.LogInformation("QuickBooks {Kind} {ExternalId} passes validation now and was queued.", map.Kind, map.ExternalId);
        return Outcome.Released;
    }
}

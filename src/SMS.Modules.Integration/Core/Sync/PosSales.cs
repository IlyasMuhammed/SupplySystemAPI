using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Core.Sync;

/// <summary>
/// "Sync POS sales" (<see cref="IntegrationSettings.AutoPushPosSales"/>). A point-of-sale invoice (payload SourceType
/// POS) is sent only while that switch and <see cref="IntegrationSettings.AutoPushSalesInvoices"/> are both on.
/// <list type="bullet">
/// <item>While POS is off, the gateway <b>holds</b> it: the payload is stored on its map, nothing is queued, and the map
/// carries <see cref="HeldCode"/> — so it is counted ("POS sales waiting"), and a cancel of a sale that was never sent
/// voids it here without sending anything.</item>
/// <item>When both are on, <see cref="ReleaseAsync"/> asks SCM (the sources) for every held sale again; they come back
/// through the gateway, which now queues them — once: the hold marker is cleared on admission, and the outbox keeps
/// one open entry per record. The settings change runs it, and so does every hourly reconciliation.</item>
/// <item>Turning it off again holds what is queued but not yet sent (<see cref="HoldQueuedAsync"/>); what QuickBooks
/// already has stays there.</item>
/// </list>
/// </summary>
internal static class PosSales
{
    /// <summary>LastErrorCode of a held POS sale.</summary>
    public const string HeldCode = "POS_SALES_HELD";

    public const string HeldMessage =
        "Held: \"Sync POS sales\" is off in the QuickBooks settings. It is sent when that is turned on.";

    /// <summary>Ids per source call when releasing.</summary>
    public const int ReleaseBatch = 200;

    public static bool IsPos(object payload) =>
        payload is SalesInvoicePayload s && SalesInvoicePayloadSources.IsPos(s.SourceType);

    /// <summary>Whether an upsert of this payload is held under these settings.</summary>
    public static bool IsHeld(IntegrationSettings settings, SyncKind kind, object payload) =>
        kind == SyncKind.SalesInvoice && !settings.AutoPushPosSales && IsPos(payload);

    public static bool Enabled(IntegrationSettings settings) => settings.AutoPushSalesInvoices && settings.AutoPushPosSales;

    /// <summary>Marks the map held and drops whatever upsert of it was still queued. Does not save.</summary>
    public static async Task HoldAsync(ISyncOutbox outbox, EntityMap map, DateTime now, CancellationToken ct)
    {
        await outbox.CloseOpenEntriesAsync(map, OutboxStatus.Done, "Held: Sync POS sales is off.", now, ct);

        // A send already under way cannot be stopped; its outcome sets the state. Otherwise: never sent → NotSynced;
        // in QuickBooks already → it stays Synced there (the newer data is what is held).
        if (!await outbox.HasRunningEntryAsync(map, ct))
            map.State = map.RemoteId is null ? SyncState.NotSynced : SyncState.Synced;

        map.LastErrorCode = HeldCode;
        map.LastError     = HeldMessage;
        map.ModifiedDate  = now;
    }

    /// <summary>The held POS sales of a connection — "POS sales waiting".</summary>
    public static IQueryable<EntityMap> Held(IntegrationDbContext db, int connectionId) =>
        db.EntityMaps.Where(m => m.ConnectionId == connectionId
                              && m.Kind == SyncKind.SalesInvoice
                              && m.LastErrorCode == HeldCode
                              && m.State != SyncState.Voided);

    public static Task<int> CountHeldAsync(IntegrationDbContext db, int connectionId, CancellationToken ct) =>
        Held(db, connectionId).CountAsync(ct);

    /// <summary>
    /// Switch turned off: POS sales queued but not yet sent (an upsert waiting in the outbox) are held instead.
    /// Voids still go — they only undo what QuickBooks already has. Saves.
    /// </summary>
    public static async Task<int> HoldQueuedAsync(IntegrationDbContext db, ISyncOutbox outbox, int connectionId, DateTime now, CancellationToken ct)
    {
        var candidates = await db.Outbox
            .Where(e => e.ConnectionId == connectionId
                     && e.Operation == OutboxOperation.Upsert
                     && (e.Status == OutboxStatus.Queued || e.Status == OutboxStatus.WaitingOnDependency
                         || e.Status == OutboxStatus.Blocked || e.Status == OutboxStatus.Suspended))
            .Select(e => e.EntityMapId)
            .Distinct()
            .ToListAsync(ct);
        if (candidates.Count == 0) return 0;

        var maps = await db.EntityMaps
            .Where(m => candidates.Contains(m.Id) && m.Kind == SyncKind.SalesInvoice && m.PayloadJson != null)
            .ToListAsync(ct);

        var held = 0;
        foreach (var map in maps)
        {
            if (!IsPosJson(map.PayloadJson!)) continue;
            await HoldAsync(outbox, map, now, ct);
            held++;
        }

        if (held > 0) await db.SaveChangesAsync(ct);
        return held;
    }

    private static bool IsPosJson(string json)
    {
        try
        {
            return IsPos(SyncPayloads.Deserialize(SyncKind.SalesInvoice, json));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// When POS sales may go (both switches on): asks the sources for every held SCM POS sale, as they stand in SCM
    /// now — an issued one comes back as an upsert (queued), a cancelled one as a void (closed here, nothing sent).
    /// Idempotent; runs in a DI scope of its own, as the current tenant. Returns how many were asked for. Never throws
    /// (a failure is logged; the next reconciliation tries again).
    /// <para>
    /// Only SCM's records: an API client that held a POS sale sends it again itself.
    /// </para>
    /// </summary>
    public static async Task<int> ReleaseAsync(IServiceProvider services, int connectionId, ILogger logger, CancellationToken ct)
    {
        try
        {
            await using var scope = services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<IntegrationDbContext>();

            var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.ConnectionId == connectionId, ct);
            if (settings is null || !Enabled(settings)) return 0;

            var ids = await Held(db, connectionId)
                .Where(m => m.SourceSystem == QuickBooksSourceSystems.Scm)
                .OrderBy(m => m.Id)
                .Select(m => m.ExternalId)
                .ToListAsync(ct);
            if (ids.Count == 0) return 0;

            var sources = sp.GetServices<IQuickBooksSource>().Where(s => s.Kinds.Contains(SyncKind.SalesInvoice)).ToList();
            foreach (var chunk in ids.Chunk(ReleaseBatch))
            foreach (var source in sources)
                await source.PushAsync(SyncKind.SalesInvoice, chunk, ct);

            logger.LogInformation("QuickBooks connection {ConnectionId}: {Count} held POS sale(s) offered again.", connectionId, ids.Count);
            return ids.Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "QuickBooks connection {ConnectionId}: releasing held POS sales failed; the hourly reconciliation will try again.",
                connectionId);
            return 0;
        }
    }
}

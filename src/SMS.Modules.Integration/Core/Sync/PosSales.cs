using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SMS.Modules.Integration.Core.Settings;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Core.Sync;

/// <summary>
/// "Sync POS sales" (<see cref="IntegrationSettings.AutoPushPosSales"/>, A44: the POSSale / POSReturn sync-control types).
/// A point-of-sale document (payload SourceType POS) is sent only while its type is enabled.
/// <list type="bullet">
/// <item>While it is off (with hold), the gateway <b>holds</b> it: the payload is stored on its map, nothing is queued, and
/// the map carries <see cref="HeldCode"/> — so it is counted ("POS sales waiting"), and a cancel of a sale that was never
/// sent voids it here without sending anything.</item>
/// <item>When it is on, <see cref="ReleaseAsync"/> asks SCM (the sources) for every held sale again; they come back
/// through the gateway, which now queues them — once: the hold marker is cleared on admission, and the outbox keeps
/// one open entry per record. The settings change runs it, and so does every hourly reconciliation.</item>
/// <item>Turning it off again holds what is queued but not yet sent (<see cref="HoldQueuedAsync"/>); what QuickBooks
/// already has stays there.</item>
/// </list>
/// A44 D-50: the mechanics are <see cref="SyncHolds"/> / <see cref="HeldRecordReleaser"/> (any type); this class
/// keeps the POS entry points the settings service, the reconciliation and the dashboard call.
/// </summary>
internal static class PosSales
{
    /// <summary>LastErrorCode of a held POS document.</summary>
    public const string HeldCode = "POS_SALES_HELD";

    public const string HeldMessage =
        "Held: \"Sync POS sales\" is off in the QuickBooks settings. It is sent when that is turned on.";

    /// <summary>Ids per source call when releasing.</summary>
    public const int ReleaseBatch = 200;

    public static bool IsPos(object payload) => SalesInvoicePayloadSources.IsPos(SyncPayloads.SourceTypeOf(payload));

    /// <summary>Whether an upsert of this payload is held under these (pre-A44) settings.</summary>
    public static bool IsHeld(IntegrationSettings settings, SyncKind kind, object payload) =>
        SyncHolds.IsPosType(SyncHolds.TypeOf(kind, payload))
        && LegacySyncControlReader.Decide(settings, SyncHolds.TypeOf(kind, payload)).Action == SyncControlAction.Hold;

    public static bool Enabled(IntegrationSettings settings) => settings.AutoPushSalesInvoices && settings.AutoPushPosSales;

    /// <summary>Marks the map held and drops whatever upsert of it was still queued. Does not save.</summary>
    public static Task HoldAsync(ISyncOutbox outbox, EntityMap map, DateTime now, CancellationToken ct) =>
        SyncHolds.HoldAsync(outbox, map, SyncControlDocumentTypes.PosSale, now, ct);

    /// <summary>The held POS sales of a connection — "POS sales waiting".</summary>
    public static IQueryable<EntityMap> Held(IntegrationDbContext db, int connectionId) =>
        SyncHolds.Held(db, connectionId, SyncControlDocumentTypes.PosSale);

    /// <summary>Held POS sales and POS returns.</summary>
    public static async Task<int> CountHeldAsync(IntegrationDbContext db, int connectionId, CancellationToken ct) =>
        await SyncHolds.Held(db, connectionId, SyncControlDocumentTypes.PosSale).CountAsync(ct)
        + await SyncHolds.Held(db, connectionId, SyncControlDocumentTypes.PosReturn).CountAsync(ct);

    /// <summary>"Sync POS sales" turned off: POS sales and returns queued but not yet sent are held instead. Saves.</summary>
    public static async Task<int> HoldQueuedAsync(IntegrationDbContext db, ISyncOutbox outbox, int connectionId, DateTime now, CancellationToken ct) =>
        await SyncHolds.HoldQueuedAsync(db, outbox, connectionId, SyncControlDocumentTypes.PosSale, now, ct)
        + await SyncHolds.HoldQueuedAsync(db, outbox, connectionId, SyncControlDocumentTypes.PosReturn, now, ct);

    /// <summary>
    /// When POS documents may go: releases every held POS sale and return (<see cref="IHeldRecordReleaser"/>).
    /// Idempotent; never throws. Returns how many were offered again.
    /// </summary>
    public static async Task<int> ReleaseAsync(IServiceProvider services, int connectionId, ILogger logger, CancellationToken ct)
    {
        try
        {
            await using var scope = services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
            var releaser = scope.ServiceProvider.GetService<IHeldRecordReleaser>();
            if (releaser is null) return 0;

            return await releaser.ReleaseAsync(connectionId, SyncControlDocumentTypes.PosSale, ct)
                 + await releaser.ReleaseAsync(connectionId, SyncControlDocumentTypes.PosReturn, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "QuickBooks connection {ConnectionId}: releasing held POS sales failed; the hourly reconciliation will try again.",
                connectionId);
            return 0;
        }
    }
}

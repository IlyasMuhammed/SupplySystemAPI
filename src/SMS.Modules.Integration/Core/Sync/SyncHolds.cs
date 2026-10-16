using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Settings;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Gateway.Validation;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Core.Sync;

/// <summary>
/// A44 D-14 / D-50: the POS hold (<see cref="PosSales"/>) generalised to every sync-control document type. A record
/// whose type is disabled <i>with hold</i> is stored on its map, nothing is queued, and the map carries a hold marker —
/// <see cref="PosSales.HeldCode"/> (POS_SALES_HELD) for the POS types, as before A44, and <see cref="HeldCode"/>
/// (HELD) for every other type. The marker and the kind together say which type holds it, so held records are counted
/// and released per type without reading payloads. Enabling the type releases them (<see cref="IHeldRecordReleaser"/>).
/// </summary>
internal static class SyncHolds
{
    /// <summary>LastErrorCode of a held record of a non-POS type.</summary>
    public const string HeldCode = "HELD";

    public static bool IsHeldCode(string? code) => code is HeldCode or PosSales.HeldCode;

    public static bool IsPosType(string documentType) =>
        documentType is SyncControlDocumentTypes.PosSale or SyncControlDocumentTypes.PosReturn;

    public static string HeldCodeFor(string documentType) => IsPosType(documentType) ? PosSales.HeldCode : HeldCode;

    public static string HeldMessageFor(string documentType) => documentType switch
    {
        SyncControlDocumentTypes.PosSale => PosSales.HeldMessage,
        _ => $"Held: sending {Label(documentType)} to QuickBooks is switched off (QuickBooks Integration → Sync control). " +
             "It is sent when that is turned on."
    };

    /// <summary>The kinds whose records a type governs (<see cref="SyncControlDocumentTypes.For"/> in reverse).</summary>
    public static IReadOnlyList<SyncKind> KindsOf(string documentType) => documentType switch
    {
        SyncControlDocumentTypes.Customer     => [SyncKind.Customer],
        SyncControlDocumentTypes.Vendor       => [SyncKind.Vendor],
        SyncControlDocumentTypes.Item         => [SyncKind.Item],
        SyncControlDocumentTypes.SalesInvoice => [SyncKind.SalesInvoice],
        SyncControlDocumentTypes.CreditNote   => [SyncKind.CreditMemo, SyncKind.RefundReceipt],
        SyncControlDocumentTypes.Payment      => [SyncKind.Payment],
        SyncControlDocumentTypes.PosSale      => [SyncKind.SalesInvoice, SyncKind.SalesReceipt, SyncKind.Payment],
        SyncControlDocumentTypes.PosReturn    => [SyncKind.CreditMemo, SyncKind.RefundReceipt],
        SyncControlDocumentTypes.PurchaseBill => [SyncKind.Bill],
        SyncControlDocumentTypes.BillPayment  => [SyncKind.BillPayment],
        SyncControlDocumentTypes.VendorCredit => [SyncKind.VendorCredit],
        _                                     => []
    };

    /// <summary>The sync-control type of a payload of this kind.</summary>
    public static string TypeOf(SyncKind kind, object payload) => SyncControlDocumentTypes.For(kind, SyncPayloads.SourceTypeOf(payload));

    /// <summary>The type of a stored map (its payload's SourceType decides POS vs back office). Null payload → by kind alone.</summary>
    public static string TypeOf(EntityMap map)
    {
        if (map.PayloadJson is null) return SyncControlDocumentTypes.For(map.Kind, null);
        try
        {
            return TypeOf(map.Kind, SyncPayloads.Deserialize(map.Kind, map.PayloadJson));
        }
        catch (JsonException)
        {
            return SyncControlDocumentTypes.For(map.Kind, null);
        }
    }

    /// <summary>Held records of one type on a connection (not voided).</summary>
    public static IQueryable<EntityMap> Held(IntegrationDbContext db, int connectionId, string documentType)
    {
        var code  = HeldCodeFor(documentType);
        var kinds = KindsOf(documentType).ToList();
        return db.EntityMaps.Where(m => m.ConnectionId == connectionId
                                     && kinds.Contains(m.Kind)
                                     && m.LastErrorCode == code
                                     && m.State != SyncState.Voided);
    }

    /// <summary>Marks the map held for <paramref name="documentType"/> and drops whatever upsert of it was still queued. Does not save.</summary>
    public static async Task HoldAsync(ISyncOutbox outbox, EntityMap map, string documentType, DateTime now, CancellationToken ct)
    {
        await outbox.CloseOpenEntriesAsync(map, OutboxStatus.Done, $"Held: sync of {Label(documentType)} is off.", now, ct);

        // A send already under way cannot be stopped; its outcome sets the state. Otherwise: never sent → NotSynced;
        // in QuickBooks already → it stays Synced there (the newer data is what is held).
        if (!await outbox.HasRunningEntryAsync(map, ct))
            map.State = map.RemoteId is null ? SyncState.NotSynced : SyncState.Synced;

        map.LastErrorCode = HeldCodeFor(documentType);
        map.LastError     = HeldMessageFor(documentType);
        map.ModifiedDate  = now;
    }

    /// <summary>
    /// A type was just disabled with hold: its records queued but not yet sent (an upsert waiting in the outbox) are
    /// held instead. Voids still go — they only undo what QuickBooks already has. Saves.
    /// </summary>
    public static async Task<int> HoldQueuedAsync(
        IntegrationDbContext db, ISyncOutbox outbox, int connectionId, string documentType, DateTime now, CancellationToken ct)
    {
        var kinds = KindsOf(documentType).ToList();
        if (kinds.Count == 0) return 0;

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
            .Where(m => candidates.Contains(m.Id) && kinds.Contains(m.Kind) && m.PayloadJson != null)
            .ToListAsync(ct);

        var held = 0;
        foreach (var map in maps)
        {
            if (TypeOf(map) != documentType) continue;
            await HoldAsync(outbox, map, documentType, now, ct);
            held++;
        }

        if (held > 0) await db.SaveChangesAsync(ct);
        return held;
    }

    private static string Label(string documentType) => documentType switch
    {
        SyncControlDocumentTypes.Customer     => "customers",
        SyncControlDocumentTypes.Vendor       => "vendors",
        SyncControlDocumentTypes.Item         => "products",
        SyncControlDocumentTypes.SalesInvoice => "sales invoices",
        SyncControlDocumentTypes.CreditNote   => "credit notes",
        SyncControlDocumentTypes.Payment      => "customer payments",
        SyncControlDocumentTypes.PosSale      => "POS sales",
        SyncControlDocumentTypes.PosReturn    => "POS returns",
        SyncControlDocumentTypes.PurchaseBill => "supplier invoices",
        SyncControlDocumentTypes.BillPayment  => "supplier payments",
        SyncControlDocumentTypes.VendorCredit => "supplier credit / debit notes",
        _                                     => documentType
    };
}

/// <summary>
/// <see cref="IHeldRecordReleaser"/> (A44 D-50), generalised from <c>PosSales.ReleaseAsync</c>. Releasing a type that
/// may go now (its sync control says Process):
/// <list type="bullet">
/// <item>SCM's records are asked of the SCM source that owns their kind again, as they stand in SCM now — an issued
/// document comes back as an upsert (queued), a cancelled one as a void (closed here, nothing sent);</item>
/// <item>records of other callers, or of a kind no source owns, are admitted from their stored payload, exactly as a
/// fresh upsert of it would be (validation, scope, dependencies).</item>
/// </list>
/// Idempotent; runs in a DI scope of its own as the current tenant; never throws (logged; the hourly reconciliation
/// calls it again).
/// </summary>
internal sealed class HeldRecordReleaser : IHeldRecordReleaser
{
    /// <summary>Ids per source call.</summary>
    public const int ReleaseBatch = PosSales.ReleaseBatch;

    private readonly IntegrationDbContext        _db;
    private readonly ISyncOutbox                 _outbox;
    private readonly IServiceScopeFactory        _scopes;
    private readonly TimeProvider                _clock;
    private readonly ILogger<HeldRecordReleaser> _logger;

    public HeldRecordReleaser(IntegrationDbContext db, ISyncOutbox outbox, IServiceScopeFactory scopes, TimeProvider clock,
                              ILogger<HeldRecordReleaser> logger)
    {
        _db     = db;
        _outbox = outbox;
        _scopes = scopes;
        _clock  = clock;
        _logger = logger;
    }

    public Task<int> CountHeldAsync(int connectionId, string documentType, CancellationToken ct = default) =>
        SyncHolds.Held(_db, connectionId, documentType).CountAsync(ct);

    public Task<int> HoldQueuedAsync(int connectionId, string documentType, CancellationToken ct = default) =>
        SyncHolds.HoldQueuedAsync(_db, _outbox, connectionId, documentType, _clock.GetUtcNow().UtcDateTime, ct);

    public async Task<int> ReleaseAsync(int connectionId, string documentType, CancellationToken ct = default)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<IntegrationDbContext>();

            var decision = await sp.GetRequiredService<ISyncControlReader>().CheckAsync(connectionId, documentType, ct);
            if (decision.Action != SyncControlAction.Process) return 0;

            var held = await SyncHolds.Held(db, connectionId, documentType)
                .OrderBy(m => m.Id)
                .Select(m => new { m.Id, m.Kind, m.ExternalId, m.SourceSystem })
                .ToListAsync(ct);
            if (held.Count == 0) return 0;

            var sources  = sp.GetServices<IQuickBooksSource>().ToList();
            var fromScm  = held.Where(h => h.SourceSystem == QuickBooksSourceSystems.Scm && sources.Any(s => s.Kinds.Contains(h.Kind))).ToList();
            var readmit  = held.Except(fromScm).Select(h => h.Id).ToList();

            foreach (var group in fromScm.GroupBy(h => h.Kind))
            foreach (var chunk in group.Select(h => h.ExternalId).Chunk(ReleaseBatch))
            foreach (var source in sources.Where(s => s.Kinds.Contains(group.Key)))
                await source.PushAsync(group.Key, chunk, ct);

            if (fromScm.Count > 0) await CloseSupersededAsync(db, connectionId, documentType, fromScm.Select(h => h.Id).ToList(), ct);

            var readmitted = 0;
            foreach (var mapId in readmit)
            {
                // Each record its own unit of work, in a scope of its own.
                await using var one = _scopes.CreateAsyncScope();
                if (await ReadmitAsync(one.ServiceProvider, mapId, documentType, ct)) readmitted++;
            }

            _logger.LogInformation("QuickBooks connection {ConnectionId}: {Count} held {Type} record(s) offered again ({Readmitted} from stored payloads).",
                connectionId, held.Count, documentType, readmitted);
            return fromScm.Count + readmitted;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "QuickBooks connection {ConnectionId}: releasing held {Type} records failed; the hourly reconciliation will try again.",
                connectionId, documentType);
            return 0;
        }
    }

    /// <summary>
    /// REV-11: a source may answer a released record as another kind of the same type — a POS sale held as an Invoice
    /// before A44 comes back as a SalesReceipt (D-21) under the same ExternalId. The old map would then stay held forever;
    /// it is closed (Voided, never sent, with a note) when it never reached QuickBooks and a map of another kind of the type
    /// now carries the record. One that did reach QuickBooks is flagged for the accountant instead of being closed.
    /// </summary>
    private async Task CloseSupersededAsync(IntegrationDbContext db, int connectionId, string documentType, List<int> releasedIds, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var code  = SyncHolds.HeldCodeFor(documentType);
        var kinds = SyncHolds.KindsOf(documentType).ToList();

        var stillHeld = await db.EntityMaps
            .Where(m => releasedIds.Contains(m.Id) && m.LastErrorCode == code && m.State != SyncState.Voided)
            .ToListAsync(ct);
        if (stillHeld.Count == 0) return;

        var ids = stillHeld.Select(m => m.ExternalId).Distinct().ToList();
        var carriers = await db.EntityMaps
            .Where(m => m.ConnectionId == connectionId && m.SourceSystem == QuickBooksSourceSystems.Scm
                     && ids.Contains(m.ExternalId) && kinds.Contains(m.Kind) && m.LastErrorCode != code)
            .Select(m => new { m.ExternalId, m.Kind })
            .ToListAsync(ct);

        var now = _clock.GetUtcNow().UtcDateTime;
        var closed = 0;
        foreach (var map in stillHeld)
        {
            var carrier = carriers.FirstOrDefault(c => c.ExternalId == map.ExternalId && c.Kind != map.Kind);
            if (carrier is null) continue;

            var note = $"Superseded: SCM now sends this record as a {carrier.Kind}.";
            if (map.RemoteId is null)
            {
                map.State         = SyncState.Voided;
                map.LastErrorCode = null;
                map.LastError     = null;
                map.Warning       = note;
            }
            else
            {
                map.State         = SyncState.NeedsResolution;
                map.LastErrorCode = Gateway.QuickBooksGateway.AccountantActionCode;
                map.LastError     = note + " The earlier version is in QuickBooks — check it there for a duplicate.";
            }
            map.ModifiedDate = now;
            closed++;
        }

        if (closed > 0)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("QuickBooks connection {ConnectionId}: {Count} held {Type} record(s) superseded by another kind and closed.",
                connectionId, closed, documentType);
        }
    }

    private static async Task<bool> ReadmitAsync(IServiceProvider sp, int mapId, string documentType, CancellationToken ct)
    {
        var db  = sp.GetRequiredService<IntegrationDbContext>();
        var map = await db.EntityMaps.FirstOrDefaultAsync(m => m.Id == mapId, ct);
        if (map?.PayloadJson is null || map.LastErrorCode != SyncHolds.HeldCodeFor(documentType)) return false;

        var connection = await db.Connections.FirstOrDefaultAsync(c => c.Id == map.ConnectionId, ct);
        if (connection is null || !connection.IsUsable()) return false;
        var settings = await sp.GetRequiredService<IConnectionAccessor>().GetOrCreateSettingsAsync(connection, ct);

        object payload;
        try
        {
            payload = SyncPayloads.Deserialize(map.Kind, map.PayloadJson);
        }
        catch (JsonException)
        {
            return false;
        }

        var now        = sp.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        var validation = await sp.GetRequiredService<IPayloadValidator>().ValidateAsync(map.Kind, payload, map, connection, settings, ct);
        map.Warning      = validation.Warnings.Count == 0 ? null : SyncText.Clip(string.Join(" ", validation.Warnings), 1000);
        map.ModifiedDate = now;

        var admission = sp.GetRequiredService<ISyncAdmission>();
        if (validation.IsValid) await admission.AdmitAsync(map.Kind, payload, map, settings, now, ct);
        else await admission.RefuseAsync(map, validation, ct);
        return true;
    }
}

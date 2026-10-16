using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Core.Settings;
using SMS.Modules.Integration.Core.Sync;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Gateway.Validation;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Gateway;

/// <summary>
/// The QuickBooks gateway (plan §2.6 steps 1–6). Validates, stores the payload on its
/// <see cref="EntityMap"/> and queues it — <b>it never calls Intuit</b>, so SCM can call it inside its
/// own request. The HTTP data endpoints are a thin layer over this same class.
/// <list type="number">
/// <item>No usable connection → NotConnected.</item>
/// <item>Its sync-control type switched off (A44 D-14; before A44 the AutoPush* switch), the record skipped in
/// matching, or a document dated before the start date → Disabled.</item>
/// <item>Same payload as last pushed (and in QuickBooks) → Accepted(Synced), nothing queued.</item>
/// <item>Its type disabled with hold (POS sales while "Sync POS sales" is off, and since A44 any type) → stored and
/// held (<see cref="SyncHolds"/>), Accepted(NotSynced).</item>
/// <item>Validation fails → Invalid; the payload is still stored, as Blocked, so the dashboard shows it.</item>
/// <item>Customer/vendor/item out of scope (D-6) → stored, Accepted(NotSynced).</item>
/// <item>Document whose customer/vendor/items are not in QuickBooks yet → WaitingOnDependency; they are requested.</item>
/// <item>Otherwise queued → Accepted(Pending), and the outbox is kicked so it goes within seconds (A44 D-11).</item>
/// </list>
/// </summary>
internal sealed class QuickBooksGateway : IQuickBooksGateway
{
    private const int MaxExternalIdLength = 100;
    /// <summary>Concurrent sends of one record each lose at most once per winner; this covers a burst of them.</summary>
    private const int MaxSaveAttempts     = 8;

    private readonly IntegrationDbContext        _db;
    private readonly IConnectionAccessor         _connections;
    private readonly IGatewayCallerContext       _caller;
    private readonly IPayloadValidator           _validator;
    private readonly ISyncOutbox                 _outbox;
    private readonly ISyncAdmission              _admission;
    private readonly ISyncLedger                 _ledger;
    private readonly IAccountingProviderRegistry _providers;
    private readonly TimeProvider                _clock;
    private readonly ILogger<QuickBooksGateway>  _logger;
    private readonly ISyncControlReader          _syncControl;
    private readonly IOutboxKick                 _kick;
    private readonly IDocumentDefaultsSource?    _defaults;

    /// <param name="syncControl">A44 D-14. Null (hosts without the registrations): read from the AutoPush* settings.</param>
    /// <param name="kick">A44 D-11. Null: no immediate kick (the recurring outbox job still runs).</param>
    public QuickBooksGateway(
        IntegrationDbContext db, IConnectionAccessor connections, IGatewayCallerContext caller, IPayloadValidator validator,
        ISyncOutbox outbox, ISyncAdmission admission, ISyncLedger ledger, IAccountingProviderRegistry providers,
        TimeProvider clock, ILogger<QuickBooksGateway> logger,
        ISyncControlReader? syncControl = null, IOutboxKick? kick = null, IDocumentDefaultsSource? defaults = null)
    {
        _db           = db;
        _connections  = connections;
        _caller       = caller;
        _validator    = validator;
        _outbox       = outbox;
        _admission    = admission;
        _ledger       = ledger;
        _providers    = providers;
        _clock        = clock;
        _logger       = logger;
        _syncControl  = syncControl ?? new LegacySyncControlReader(db);
        _kick         = kick ?? NoOutboxKick.Instance;
        _defaults     = defaults;
    }

    public Task<GatewayResult> UpsertCustomerAsync(CustomerPayload payload, CancellationToken ct = default) =>
        UpsertAsync(SyncKind.Customer, payload, payload?.ExternalId, ct);

    public Task<GatewayResult> UpsertVendorAsync(VendorPayload payload, CancellationToken ct = default) =>
        UpsertAsync(SyncKind.Vendor, payload, payload?.ExternalId, ct);

    public Task<GatewayResult> UpsertItemAsync(ItemPayload payload, CancellationToken ct = default) =>
        UpsertAsync(SyncKind.Item, payload, payload?.ExternalId, ct);

    public Task<GatewayResult> UpsertSalesInvoiceAsync(SalesInvoicePayload payload, CancellationToken ct = default) =>
        // Plan D-7: a cancelled invoice is a void, however it arrives.
        payload?.Status == SalesInvoicePayloadStatus.Cancelled
            ? VoidSalesInvoiceAsync(payload.ExternalId, ct)
            : UpsertAsync(SyncKind.SalesInvoice, payload, payload?.ExternalId, ct);

    public Task<GatewayResult> UpsertBillAsync(BillPayload payload, CancellationToken ct = default) =>
        UpsertAsync(SyncKind.Bill, payload, payload?.ExternalId, ct);

    // A44 (D-19..D-24)
    public Task<GatewayResult> UpsertPaymentAsync(PaymentPayload payload, CancellationToken ct = default) =>
        UpsertAsync(SyncKind.Payment, payload, payload?.ExternalId, ct);

    public Task<GatewayResult> UpsertBillPaymentAsync(BillPaymentPayload payload, CancellationToken ct = default) =>
        UpsertAsync(SyncKind.BillPayment, payload, payload?.ExternalId, ct);

    public Task<GatewayResult> UpsertSalesReceiptAsync(SalesReceiptPayload payload, CancellationToken ct = default) =>
        UpsertAsync(SyncKind.SalesReceipt, payload, payload?.ExternalId, ct);

    public Task<GatewayResult> UpsertCreditMemoAsync(CreditMemoPayload payload, CancellationToken ct = default) =>
        UpsertAsync(SyncKind.CreditMemo, payload, payload?.ExternalId, ct);

    public Task<GatewayResult> UpsertRefundReceiptAsync(RefundReceiptPayload payload, CancellationToken ct = default) =>
        UpsertAsync(SyncKind.RefundReceipt, payload, payload?.ExternalId, ct);

    public Task<GatewayResult> UpsertVendorCreditAsync(VendorCreditPayload payload, CancellationToken ct = default) =>
        UpsertAsync(SyncKind.VendorCredit, payload, payload?.ExternalId, ct);

    // ── Upsert ───────────────────────────────────────────────────────────────

    private async Task<GatewayResult> UpsertAsync(SyncKind kind, object? payload, string? externalId, CancellationToken ct)
    {
        if (payload is null)
            return GatewayResult.Invalid([new GatewayError("payload", "PAYLOAD_REQUIRED", "No data was sent.")]);

        var idError = CheckExternalId(externalId);
        if (idError is not null) return GatewayResult.Invalid([idError]);

        // Callers may send "  abc " — the map key is the trimmed id, and so is the payload.
        SetExternalId(payload, externalId!.Trim());

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await UpsertOnceAsync(kind, payload, externalId.Trim(), ct);
            }
            catch (DbUpdateException) when (attempt < MaxSaveAttempts)
            {
                // Another request created the same map first (unique key), or the executor changed it
                // (RowVersion). Start again from what is stored now.
                await BackOffAsync(attempt, ct);
            }
        }
    }

    /// <summary>Forget what was read, and step aside briefly so concurrent writers of one record interleave.</summary>
    private async Task BackOffAsync(int attempt, CancellationToken ct)
    {
        _db.ChangeTracker.Clear();
        await Task.Delay(Random.Shared.Next(5, 25) * attempt, ct);
    }

    private async Task<GatewayResult> UpsertOnceAsync(SyncKind kind, object payload, string externalId, CancellationToken ct)
    {
        var connection = await _connections.GetCurrentAsync(ct);
        if (connection is null || !connection.IsUsable()) return GatewayResult.NotConnected();

        var settings = await _connections.GetOrCreateSettingsAsync(connection, ct);

        // A44 D-14: the sync control of the record's type — Skip is what "auto-push off" was; Hold is below.
        var documentType = SyncHolds.TypeOf(kind, payload);
        var control      = await _syncControl.CheckAsync(connection.Id, documentType, ct);
        if (control.Action == SyncControlAction.Skip) return GatewayResult.Disabled();

        var now          = _clock.GetUtcNow().UtcDateTime;
        var sourceSystem = _caller.SourceSystem;
        var map          = await FindMapAsync(connection.Id, sourceSystem, kind, externalId, ct);

        if (map is not null && await IsSkippedAsync(map, ct)) return GatewayResult.Disabled();

        if (DocumentDate(payload) is { } date && settings.DocumentStartDate is { } start && date.Date < start.Date)
            return GatewayResult.Disabled();

        if (map?.State == SyncState.Voided) return GatewayResult.Accepted(SyncState.Voided);

        // Flagged for the accountant (e.g. reversed in SCM after it reached QuickBooks): nothing more is sent until
        // a person has dealt with it in QuickBooks and resolved it on the dashboard.
        if (IsFlaggedForAccountant(map)) return GatewayResult.Accepted(SyncState.NeedsResolution);

        var json        = SyncPayloads.Serialize(payload);
        var fingerprint = SyncPayloads.Fingerprint(json);
        var dryRun      = settings.Mode == SyncMode.DryRun;

        if (map is null)
        {
            map = new EntityMap
            {
                OrganizationId = connection.OrganizationId,
                ConnectionId   = connection.Id,
                SourceSystem   = sourceSystem,
                Kind           = kind,
                ExternalId     = externalId,
                State          = SyncState.NotSynced,
                CreatedDate    = now
            };
            _db.EntityMaps.Add(map);
        }
        else if (map.RemoteId is not null && map.LastPushedFingerprint == fingerprint)
        {
            // QuickBooks already has exactly this. Nothing to validate or send — even if a setting
            // changed since, the record in QuickBooks is what this payload says.
            StorePayload(map, payload, json, fingerprint, now);
            map.State         = SyncState.Synced;
            map.LastErrorCode = null;
            map.LastError     = null;
            await _outbox.CloseOpenEntriesAsync(map, OutboxStatus.Done, "Superseded: QuickBooks already has this data.", now, ct);
            await _db.SaveChangesAsync(ct);
            return GatewayResult.Accepted(SyncState.Synced);
        }
        else if (dryRun && map.State == SyncState.DryRunOk && map.PayloadFingerprint == fingerprint)
        {
            return GatewayResult.Accepted(SyncState.DryRunOk);
        }

        // Its type is disabled with hold (POS sales while "Sync POS sales" is off, and since A44 any type): held — stored
        // (so it is counted, and a cancel voids it here), never queued. Enabling the type offers every held one again
        // (IHeldRecordReleaser).
        if (control.Action == SyncControlAction.Hold)
        {
            StorePayload(map, payload, json, fingerprint, now);
            map.Warning = null;
            await SyncHolds.HoldAsync(_outbox, map, documentType, now, ct);
            await _db.SaveChangesAsync(ct);
            return GatewayResult.Accepted(map.State);
        }

        // D-54: a sales receipt / credit memo / refund receipt without a customer goes under the walk-in customer.
        if (payload is SalesDocumentPayload { CustomerExternalId: var customer } && string.IsNullOrWhiteSpace(customer))
            await EnsureWalkInCustomerAsync(connection, sourceSystem, documentType, now, ct);

        var validation = await _validator.ValidateAsync(kind, payload, map, connection, settings, ct);

        StorePayload(map, payload, json, fingerprint, now);
        map.Warning = validation.Warnings.Count == 0 ? null : SyncText.Clip(string.Join(" ", validation.Warnings), 1000);

        // Shared with the re-validation of Blocked records, so a record unblocked later is queued exactly
        // as it would have been had it passed now.
        if (!validation.IsValid) return await _admission.RefuseAsync(map, validation, ct);

        var admitted = await _admission.AdmitAsync(kind, payload, map, settings, now, ct);
        if (admitted.State is SyncState.Pending or SyncState.WaitingOnDependency) _kick.Request(connection.Id);
        return admitted;
    }

    /// <summary>
    /// D-54: the one "POS Walk-in" customer map of this connection and caller. With a <c>PosDefaultCustomerRef</c>
    /// default it is linked to that QuickBooks customer (nothing is created or changed there); otherwise it is queued
    /// once and created by the executor (a QuickBooks customer of that name already there is adopted, as any duplicate
    /// name is). Does not save — the caller's admission does.
    /// </summary>
    private async Task EnsureWalkInCustomerAsync(
        IntegrationConnection connection, string sourceSystem, string documentType, DateTime now, CancellationToken ct)
    {
        var defaults = _defaults is null ? DocumentDefaults.None : await _defaults.GetAsync(connection.Id, documentType, ct);
        var payload  = new CustomerPayload
        {
            ExternalId  = SyncPayloads.WalkInCustomerExternalId,
            DisplayName = SyncPayloads.WalkInCustomerName,
            Notes       = "Walk-in point-of-sale customer (SCM)."
        };
        var json        = SyncPayloads.Serialize(payload);
        var fingerprint = SyncPayloads.Fingerprint(json);

        var map = await FindMapAsync(connection.Id, sourceSystem, SyncKind.Customer, payload.ExternalId, ct)
                  ?? _db.EntityMaps.Local.FirstOrDefault(m => m.ConnectionId == connection.Id && m.SourceSystem == sourceSystem
                                                            && m.Kind == SyncKind.Customer && m.ExternalId == payload.ExternalId);
        if (map is null)
        {
            map = new EntityMap
            {
                OrganizationId = connection.OrganizationId,
                ConnectionId   = connection.Id,
                SourceSystem   = sourceSystem,
                Kind           = SyncKind.Customer,
                ExternalId     = payload.ExternalId,
                State          = SyncState.NotSynced,
                CreatedDate    = now
            };
            _db.EntityMaps.Add(map);
        }

        if (map.PayloadJson is null) StorePayload(map, payload, json, fingerprint, now);

        if (defaults.PosCustomerId is { } remoteId)
        {
            if (map.RemoteId == remoteId) return;
            await _outbox.CloseOpenEntriesAsync(map, OutboxStatus.Done, "Linked to the POS default customer.", now, ct);
            map.RemoteId              = remoteId;
            map.RemoteSyncToken       = null;
            map.LinkOrigin            = LinkOrigin.Adopted;
            map.LinkedAt              = now;
            map.State                 = SyncState.Synced;
            // The accountant's customer is never updated from SCM's placeholder data.
            map.LastPushedFingerprint = map.PayloadFingerprint;
            map.LastErrorCode         = null;
            map.LastError             = null;
            return;
        }

        // No default: wanted (the documents reference it), so it is sent; the dependency step queues it.
        map.RequestedAt ??= now;
    }

    // ── Flag for the accountant ──────────────────────────────────────────────

    /// <summary>LastErrorCode of a record flagged for the accountant (<see cref="FlagForAccountantAsync"/>).</summary>
    public const string AccountantActionCode = "ACCOUNTANT_ACTION_NEEDED";

    internal static bool IsFlaggedForAccountant(EntityMap? map) =>
        map is { State: SyncState.NeedsResolution, LastErrorCode: AccountantActionCode };

    public async Task<GatewayResult> FlagForAccountantAsync(SyncKind kind, string externalId, string reason, CancellationToken ct = default)
    {
        var idError = CheckExternalId(externalId);
        if (idError is not null) return GatewayResult.Invalid([idError]);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await FlagOnceAsync(kind, externalId.Trim(), reason, ct);
            }
            catch (DbUpdateException) when (attempt < MaxSaveAttempts)
            {
                await BackOffAsync(attempt, ct);
            }
        }
    }

    private async Task<GatewayResult> FlagOnceAsync(SyncKind kind, string externalId, string reason, CancellationToken ct)
    {
        // Whatever the connection's status: a revoked connection's records are still the accountant's to fix.
        var connection = await _connections.GetCurrentAsync(ct);
        if (connection is null) return GatewayResult.NotConnected();

        var now = _clock.GetUtcNow().UtcDateTime;
        var map = await FindMapAsync(connection.Id, _caller.SourceSystem, kind, externalId, ct);

        // Never seen: nothing of it is in QuickBooks. Already closed: nothing more to do.
        if (map is null) return GatewayResult.Accepted(SyncState.NotSynced);
        if (map.State == SyncState.Voided) return GatewayResult.Accepted(SyncState.Voided);

        var mayExist = map.RemoteId is not null
                    || await _outbox.HasRunningEntryAsync(map, ct)
                    || await _ledger.HasUnresolvedAsync(map, string.Empty, now, ct)
                    || await _ledger.FindSucceededAsync(map, ct) is not null;

        var text = SyncText.Clip(string.IsNullOrWhiteSpace(reason) ? "Flagged in SCM for the accountant." : reason.Trim(), 2000)!;
        await _outbox.CloseOpenEntriesAsync(map, OutboxStatus.Done, "Flagged for the accountant: " + text, now, ct);

        if (mayExist)
        {
            map.State         = SyncState.NeedsResolution;
            map.LastErrorCode = AccountantActionCode;
            map.LastError     = text;
        }
        else
        {
            // Never reached QuickBooks: there is nothing there to fix, and nothing more is sent.
            map.State         = SyncState.Voided;
            map.LastErrorCode = null;
            map.LastError     = null;
        }
        map.ModifiedDate = now;

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("QuickBooks {Kind} {ExternalId} flagged for the accountant ({State}).", kind, externalId, map.State);
        return GatewayResult.Accepted(map.State);
    }

    // ── Void ─────────────────────────────────────────────────────────────────

    public Task<GatewayResult> VoidSalesInvoiceAsync(string externalId, CancellationToken ct = default) =>
        VoidAsync(SyncKind.SalesInvoice, externalId, ct);

    /// <summary>
    /// A44 D-51: undo a document in QuickBooks. Invoice, Payment, SalesReceipt → voided there; BillPayment,
    /// RefundReceipt → deleted there (the SDK cannot void them). CreditMemo, VendorCredit and Bill are never undone by
    /// the integration — a credit may already be applied, a bill paid — they are flagged for the accountant
    /// (<see cref="FlagForAccountantAsync"/>). Master data cannot be voided.
    /// </summary>
    public Task<GatewayResult> VoidAsync(SyncKind kind, string externalId, CancellationToken ct = default)
    {
        if (SyncPayloads.IsMasterData(kind))
            return Task.FromResult(GatewayResult.Invalid([new GatewayError("kind", "VOID_NOT_APPLICABLE",
                $"A {kind} is not voided; deactivate it instead.")]));

        if (!SyncPayloads.IsVoidable(kind))
            return FlagForAccountantAsync(kind, externalId,
                $"Cancelled in SCM. QuickBooks {kind} records are not voided by the integration — void or delete it in QuickBooks " +
                "by hand (check first whether it was applied), then mark this resolved.", ct);

        var idError = CheckExternalId(externalId);
        if (idError is not null) return Task.FromResult(GatewayResult.Invalid([idError]));

        return VoidWithRetryAsync(kind, externalId.Trim(), ct);
    }

    private async Task<GatewayResult> VoidWithRetryAsync(SyncKind kind, string externalId, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await VoidOnceAsync(kind, externalId, ct);
            }
            catch (DbUpdateException) when (attempt < MaxSaveAttempts)
            {
                await BackOffAsync(attempt, ct);
            }
        }
    }

    private async Task<GatewayResult> VoidOnceAsync(SyncKind kind, string externalId, CancellationToken ct)
    {
        var connection = await _connections.GetCurrentAsync(ct);
        if (connection is null || !connection.IsUsable()) return GatewayResult.NotConnected();

        var now = _clock.GetUtcNow().UtcDateTime;
        var map = await FindMapAsync(connection.Id, _caller.SourceSystem, kind, externalId, ct);

        // Voids go whether the type is enabled or held — only a type switched off entirely (Skip) leaves it alone, as
        // "auto-push sales invoices off" did. The type comes from the stored payload (POS or not).
        var documentType = map is null ? SyncControlDocumentTypes.For(kind, null) : SyncHolds.TypeOf(map);
        if ((await _syncControl.CheckAsync(connection.Id, documentType, ct)).Action == SyncControlAction.Skip)
            return GatewayResult.Disabled();

        // Never seen, or already void: nothing to do.
        if (map is null || map.State == SyncState.Voided) return GatewayResult.Accepted(SyncState.Voided);

        // In QuickBooks — or possibly (a create is running, or ended with an unknown outcome): void it there.
        var mayExist = map.RemoteId is not null
                    || await _outbox.HasRunningEntryAsync(map, ct)
                    || await _ledger.HasUnresolvedAsync(map, string.Empty, now, ct)
                    || await _ledger.FindSucceededAsync(map, ct) is not null;

        if (mayExist)
        {
            await _outbox.EnqueueAsync(map, OutboxOperation.Void, now, ct: ct);
            await _db.SaveChangesAsync(ct);
            _kick.Request(connection.Id);
            return GatewayResult.Accepted(SyncState.Pending);
        }

        // Never reached QuickBooks: cancel whatever was queued and void it here.
        await _outbox.CloseOpenEntriesAsync(map, OutboxStatus.Done, "Voided before it was sent to QuickBooks.", now, ct);
        map.State         = SyncState.Voided;
        map.LastErrorCode = null;
        map.LastError     = null;
        map.ModifiedDate  = now;
        await _db.SaveChangesAsync(ct);
        return GatewayResult.Accepted(SyncState.Voided);
    }

    // ── Status ───────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<SyncStatus>> GetStatusAsync(
        SyncKind kind, IReadOnlyCollection<string> externalIds, CancellationToken ct = default)
    {
        var ids = (externalIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct().ToList();
        if (ids.Count == 0) return [];

        var connection = await _connections.GetCurrentAsync(ct);
        if (connection is null) return [];

        var sourceSystem = _caller.SourceSystem;
        var maps = await _db.EntityMaps
            .AsNoTracking()
            .Where(m => m.ConnectionId == connection.Id && m.SourceSystem == sourceSystem && m.Kind == kind && ids.Contains(m.ExternalId))
            .ToListAsync(ct);

        var provider = TryProvider(connection.ProviderKey);

        return maps
            .OrderBy(m => ids.IndexOf(m.ExternalId))
            .Select(m => new SyncStatus(
                m.Kind, m.ExternalId, m.State, m.RemoteId, m.RemoteDocNumber, m.LastErrorCode, m.LastError, m.LastSyncedAt,
                m.RemoteId is null ? null : provider?.BuildDeepLink(connection.Environment, m.Kind, m.RemoteId)))
            .ToList();
    }

    private IAccountingProvider? TryProvider(string providerKey)
    {
        try
        {
            return _providers.Get(providerKey);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            _logger.LogWarning("No accounting provider registered for {ProviderKey}; deep links omitted.", providerKey);
            return null;
        }
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    private Task<EntityMap?> FindMapAsync(int connectionId, string sourceSystem, SyncKind kind, string externalId, CancellationToken ct) =>
        _db.EntityMaps.FirstOrDefaultAsync(m => m.ConnectionId == connectionId
                                             && m.SourceSystem == sourceSystem
                                             && m.Kind == kind
                                             && m.ExternalId == externalId, ct);

    private Task<bool> IsSkippedAsync(EntityMap map, CancellationToken ct) =>
        _db.MatchCandidates.AnyAsync(c => c.EntityMapId == map.Id && c.Decision == MatchDecision.Skip, ct);

    private static void StorePayload(EntityMap map, object payload, string json, string fingerprint, DateTime now)
    {
        map.PayloadJson        = json;
        map.PayloadFingerprint = fingerprint;
        map.PayloadReceivedAt  = now;
        map.DisplayLabel       = SyncText.Clip(LabelOf(payload), 300) ?? map.ExternalId;
        map.ModifiedDate       = now;
    }

    private static string LabelOf(object payload) => payload switch
    {
        PartyPayload p        => string.IsNullOrWhiteSpace(p.DisplayName) ? p.ExternalId : p.DisplayName.Trim(),
        ItemPayload i         => string.IsNullOrWhiteSpace(i.Name) ? i.ExternalId : RemoteNameResolver.ItemBaseName(i),
        Shared.Finance.Posting.IAccountingDocument d =>
            SyncPayloads.DocNumberOf(payload) is { Length: > 0 } number ? number : d.ExternalId,
        _                     => string.Empty
    };

    internal static DateTime? DocumentDate(object payload) => SyncPayloads.DocumentDateOf(payload);

    internal static bool AutoPush(IntegrationSettings s, SyncKind kind) => kind switch
    {
        SyncKind.Customer     => s.AutoPushCustomers,
        SyncKind.Vendor       => s.AutoPushVendors,
        SyncKind.Item         => s.AutoPushItems,
        SyncKind.SalesInvoice => s.AutoPushSalesInvoices,
        SyncKind.Bill         => s.AutoPushBills,
        _                     => false
    };

    private static GatewayError? CheckExternalId(string? externalId)
    {
        if (string.IsNullOrWhiteSpace(externalId))
            return new GatewayError("externalId", "EXTERNAL_ID_REQUIRED", "The record has no ExternalId — the caller's own id for it is the upsert key.");
        if (externalId.Trim().Length > MaxExternalIdLength)
            return new GatewayError("externalId", "EXTERNAL_ID_TOO_LONG", $"The ExternalId is longer than {MaxExternalIdLength} characters.");
        return null;
    }

    private static void SetExternalId(object payload, string externalId)
    {
        switch (payload)
        {
            case PartyPayload p:        p.ExternalId = externalId; break;
            case ItemPayload i:         i.ExternalId = externalId; break;
            case SalesInvoicePayload s: s.ExternalId = externalId; break;
            case BillPayload b:         b.ExternalId = externalId; break;
            case PaymentPayload p:      p.ExternalId = externalId; break;
            case BillPaymentPayload bp: bp.ExternalId = externalId; break;
            case SalesDocumentPayload d: d.ExternalId = externalId; break;
            case VendorCreditPayload v: v.ExternalId = externalId; break;
        }
    }
}

using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SMS.Modules.Integration.Configuration;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Logging;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Gateway.Validation;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Core.Sync;

internal enum SyncExecutionStatus
{
    NotFound,
    /// <summary>The entry was not in a runnable state (already done, or someone else is on it).</summary>
    Skipped,
    /// <summary>Another execution of the same record holds the claim; re-queued behind it.</summary>
    InFlight,
    /// <summary>The connection cannot be used; the entry (and on auth failure, the whole outbox) is suspended.</summary>
    Suspended,
    /// <summary>Validation refused the stored payload; nothing was sent.</summary>
    Blocked,
    WaitingOnDependency,
    DryRun,
    Succeeded,
    Voided,
    /// <summary>Throttled or outcome unknown; re-queued with backoff.</summary>
    RetryScheduled,
    /// <summary>QuickBooks refused it; final until a new payload (or a person's retry).</summary>
    Failed,
    NeedsResolution
}

internal sealed record SyncExecutionResult(SyncExecutionStatus Status, string? Message = null, bool StopConnection = false);

/// <summary>Runs one outbox entry: dry-run, or claim → call QuickBooks → record the outcome (plan §2.6, second half).</summary>
internal interface ISyncExecutor
{
    Task<SyncExecutionResult> ExecuteAsync(int entryId, CancellationToken ct = default);
}

/// <summary>
/// The sync executor. Runs inside a tenant scope (a job's per-connection scope, or an admin's request),
/// one entry at a time.
/// <para>
/// <b>The rule everything here serves: never create a record twice.</b> A ledger claim is written
/// before every call. A call whose outcome is unknown (timeout, 5xx, a worker that died) is followed by
/// a lookup by name / document number before anything is created again. A create is also preceded by
/// a lookup whenever an <i>earlier</i> payload's create is still unresolved, and a succeeded claim
/// always wins over a map that lost its RemoteId.
/// </para>
/// </summary>
internal sealed class SyncExecutor : ISyncExecutor
{
    private readonly IntegrationDbContext         _db;
    private readonly IConnectionAccessor          _accessor;
    private readonly IAccountingProviderRegistry  _registry;
    private readonly IPayloadValidator            _validator;
    private readonly IQboObjectBuilder            _builder;
    private readonly ISyncLedger                  _ledger;
    private readonly ISyncDependencies            _dependencies;
    private readonly IOutboxControl               _outboxControl;
    private readonly IConnectionHealth            _health;
    private readonly IntegrationJobOptions        _options;
    private readonly TimeProvider                 _clock;
    private readonly ILogger<SyncExecutor>        _logger;

    public SyncExecutor(
        IntegrationDbContext db, IConnectionAccessor accessor, IAccountingProviderRegistry registry,
        IPayloadValidator validator, IQboObjectBuilder builder, ISyncLedger ledger, ISyncDependencies dependencies,
        IOutboxControl outboxControl, IConnectionHealth health, IOptions<IntegrationJobOptions> options,
        TimeProvider clock, ILogger<SyncExecutor> logger)
    {
        _db            = db;
        _accessor      = accessor;
        _registry      = registry;
        _validator     = validator;
        _builder       = builder;
        _ledger        = ledger;
        _dependencies  = dependencies;
        _outboxControl = outboxControl;
        _health        = health;
        _options       = options.Value;
        _clock         = clock;
        _logger        = logger;
    }

    private TimeSpan Lease => TimeSpan.FromMinutes(Math.Max(1, _options.LeaseMinutes));
    private TimeSpan Grace => TimeSpan.FromMinutes(Math.Max(0, _options.LeaseGraceMinutes));
    private int MaxAttempts => Math.Max(1, _options.MaxAttempts);

    private sealed class Run
    {
        public required SyncOutboxEntry        Entry      { get; init; }
        public required EntityMap              Map        { get; init; }
        public required IntegrationConnection  Connection { get; init; }
        public required IntegrationSettings    Settings   { get; init; }
        public required DateTime               Now        { get; init; }
        public required SyncState              PreviousState { get; init; }
        public IAccountingProvider             Provider   { get; set; } = null!;
        public ProviderContext                 Context    { get; set; } = null!;
        public SyncCommandClaim?               Claim      { get; set; }
        public string                          Fingerprint { get; set; } = string.Empty;
        public object?                         Payload    { get; set; }
        public BuiltRemoteEntity?              Built      { get; set; }
        public List<string>                    Warnings   { get; } = new();
        public bool                            DryRun => Settings.Mode == SyncMode.DryRun;
    }

    public async Task<SyncExecutionResult> ExecuteAsync(int entryId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;

        var entry = await _db.Outbox.Include(e => e.EntityMap).FirstOrDefaultAsync(e => e.Id == entryId, ct);
        if (entry is null) return new(SyncExecutionStatus.NotFound, $"Outbox entry {entryId} was not found.");
        if (entry.Status != OutboxStatus.Queued) return new(SyncExecutionStatus.Skipped, $"The entry is {entry.Status}, not Queued.");

        var map = entry.EntityMap;

        // Never two executions of one record at once (a follow-up entry waits for the running one).
        var runningCutoff = now - Lease - Grace;
        if (await _db.Outbox.AnyAsync(e => e.EntityMapId == map.Id && e.Id != entry.Id
                                        && e.Status == OutboxStatus.Running && e.NextAttemptAt > runningCutoff, ct))
        {
            entry.NextAttemptAt = now.AddMinutes(1);
            await _db.SaveChangesAsync(ct);
            return new(SyncExecutionStatus.InFlight, "Another push of this record is running; queued behind it.");
        }

        var connection = await _db.Connections.FirstOrDefaultAsync(c => c.Id == entry.ConnectionId, ct);
        if (connection is null || !connection.IsUsable())
        {
            entry.Status        = OutboxStatus.Suspended;
            entry.BlockedReason = $"The QuickBooks connection is {connection?.Status.ToString() ?? "missing"}; paused until it is reconnected.";
            await _db.SaveChangesAsync(ct);
            return new(SyncExecutionStatus.Suspended, entry.BlockedReason, StopConnection: true);
        }

        var settings = await _accessor.GetOrCreateSettingsAsync(connection, ct);

        var run = new Run
        {
            Entry         = entry,
            Map           = map,
            Connection    = connection,
            Settings      = settings,
            Now           = now,
            PreviousState = map.State
        };

        // Take the entry for this run. The sweep hands it back if we die (Running past the lease).
        entry.Status        = OutboxStatus.Running;
        entry.NextAttemptAt = now;
        await SaveMapAsync(map, m => m.State = SyncState.InProgress, ct);

        try
        {
            return entry.Operation == OutboxOperation.Void
                ? await VoidAsync(run, ct)
                : await UpsertAsync(run, ct);
        }
        catch (ConnectionUnavailableException ex)
        {
            return await ConnectionLostAsync(run, ex.Status, ex.Message, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "QuickBooks sync of {Kind} {ExternalId} (entry {EntryId}) failed unexpectedly.",
                map.Kind, map.ExternalId, entry.Id);
            return await RecoverAsync(entry.Id, map.Id, run.Claim?.Id, ex, now, ct);
        }
    }

    // ── Upsert ───────────────────────────────────────────────────────────────

    private async Task<SyncExecutionResult> UpsertAsync(Run run, CancellationToken ct)
    {
        var map = run.Map;

        if (run.PreviousState == SyncState.Voided)
            return await FinishAsync(run, OutboxStatus.Done, "The invoice is voided; later changes are not sent.",
                m => m.State = SyncState.Voided, SyncExecutionStatus.Voided, ct);

        if (map.PayloadJson is null)
            return await FinishAsync(run, OutboxStatus.Failed, "Nothing to send: no payload has been received for this record.",
                m =>
                {
                    m.State         = SyncState.NotSynced;
                    m.LastErrorCode = "NO_PAYLOAD";
                    m.LastError     = "The gateway has not received this record's data yet, so there is nothing to send. Use Push now.";
                }, SyncExecutionStatus.Failed, ct);

        run.Fingerprint = map.PayloadFingerprint ?? SyncPayloads.Fingerprint(map.PayloadJson);
        run.Payload     = SyncPayloads.Deserialize(map.Kind, map.PayloadJson);

        var validation = await _validator.ValidateAsync(map.Kind, run.Payload, map, run.Connection, run.Settings, ct);
        if (!validation.IsValid)
        {
            var message = string.Join(" ", validation.Errors.Select(e => e.Message));
            return await FinishAsync(run, OutboxStatus.Blocked, message, m =>
            {
                m.State         = SyncState.Blocked;
                m.LastErrorCode = validation.Errors.Count == 1 ? validation.Errors[0].Code : "VALIDATION_FAILED";
                m.LastError     = SyncText.Clip(message, 2000);
            }, SyncExecutionStatus.Blocked, ct);
        }
        run.Warnings.AddRange(validation.Warnings);

        var built = await _builder.BuildAsync(map, run.Payload, run.Connection, run.Settings, run.DryRun, ct);
        run.Built = built;
        run.Warnings.AddRange(built.Warnings.Where(w => !run.Warnings.Contains(w)));

        if (!run.DryRun && built.Unresolved.Count > 0)
            return await WaitForDependenciesAsync(run, built.Unresolved, ct);

        if (run.DryRun)
            return await DryRunAsync(run, map.RemoteId is null ? "Create" : "Update", built.Entity, ct);

        run.Provider = _registry.Get(run.Connection.ProviderKey);
        run.Context  = run.Connection.ToProviderContext();

        var decision = await _ledger.ClaimAsync(map, OutboxOperation.Upsert, run.Fingerprint, run.Now, ct);
        run.Claim = decision.Claim;

        switch (decision.Kind)
        {
            case SyncClaimDecisionKind.InFlight:
                return await RequeueBehindClaimAsync(run, ct);

            case SyncClaimDecisionKind.AlreadySucceeded:
                return await SucceededEarlierAsync(run, decision.Claim, ct);

            case SyncClaimDecisionKind.AlreadyRefused:
            {
                // Not asked again — the same data earns the same answer. Repeat QuickBooks' reason.
                var reason = await _db.SyncLog
                    .Where(l => l.EntityMapId == map.Id && l.Outcome != "Succeeded" && l.Outcome != "DryRun" && l.Message != null)
                    .OrderByDescending(l => l.Id)
                    .Select(l => l.Message)
                    .FirstOrDefaultAsync(ct);
                return await FinalAsync(run, decision.Claim.ErrorCode ?? "REFUSED",
                    $"QuickBooks already refused exactly this data{(reason is null ? "" : $" ({reason})")}. " +
                    "Change the record in SCM and save it again, or use Retry after fixing the cause in QuickBooks.",
                    SyncState.Failed, recordRefusal: false, ct);
            }

            case SyncClaimDecisionKind.NeedsLookup when map.RemoteId is null:
            {
                // An earlier attempt of this very command may have created the record. Ask before creating.
                var lookup = await CallAsync(run, "Find", () => run.Provider.FindAsync(run.Context, map.Kind, built.Lookup, ct));
                if (!lookup.IsSuccess) return await ProviderFailureAsync(run, lookup, "Find", ct);
                if (lookup.Value is { } found)
                {
                    if (await LinkedElsewhereAsync(map, found.RemoteId, ct) is { } other)
                        return await TakenAsync(run, found, other, ct);
                    return await SucceedAsync(run, found, LinkOrigin.Created,
                        "Found in QuickBooks after an attempt whose outcome was unknown — linked, not created again.", ct);
                }
                break;
            }
        }

        if (map.RemoteId is null)
        {
            // A succeeded claim is proof a record exists, even if saving its id to the map failed.
            var earlier = await _ledger.FindSucceededAsync(map, ct);
            if (earlier?.RemoteId is not null)
            {
                map.RemoteId        = earlier.RemoteId;
                map.RemoteSyncToken = null;
                map.LinkOrigin    ??= LinkOrigin.Created;
            }
            else if (await _ledger.HasUnresolvedAsync(map, decision.Claim.CommandKey, run.Now, ct))
            {
                // A create for an earlier payload has an unknown outcome: look before creating again —
                // by the current name, and by the name that earlier create reserved if it was renamed since.
                var lookup = await CallAsync(run, "Find", () => run.Provider.FindAsync(run.Context, map.Kind, built.Lookup, ct));
                if (!lookup.IsSuccess) return await ProviderFailureAsync(run, lookup, "Find", ct);

                var reserved = map.RemoteName;
                if (lookup.Value is null && SyncPayloads.IsMasterData(map.Kind) && !string.IsNullOrWhiteSpace(reserved)
                    && !Same(reserved, built.EffectiveName))
                {
                    lookup = await CallAsync(run, "Find", () => run.Provider.FindAsync(run.Context, map.Kind, new RemoteLookup(Name: reserved), ct));
                    if (!lookup.IsSuccess) return await ProviderFailureAsync(run, lookup, "Find", ct);
                }

                if (lookup.Value is { } found)
                {
                    if (await LinkedElsewhereAsync(map, found.RemoteId, ct) is { } other)
                        return await TakenAsync(run, found, other, ct);
                    map.RemoteId        = found.RemoteId;
                    map.RemoteSyncToken = found.SyncToken;
                    map.LinkOrigin    ??= LinkOrigin.Created;
                }
            }
        }

        // D-2: reserve the name before creating. If the outcome turns out unknown, the retry looks the
        // record up by exactly this name — and no other record takes it in the meantime.
        if (map.RemoteId is null && SyncPayloads.IsMasterData(map.Kind)
            && built.EffectiveName is { } name && map.RemoteName != name)
            await SaveMapAsync(map, m => m.RemoteName = SyncText.Clip(name, 200), ct);

        return await WriteAsync(run, ct);
    }

    /// <summary>The QuickBooks record backing another map of the same kind, if any (by that map's label).</summary>
    private Task<string?> LinkedElsewhereAsync(EntityMap map, string remoteId, CancellationToken ct) =>
        _db.EntityMaps
            .Where(m => m.ConnectionId == map.ConnectionId && m.Kind == map.Kind && m.RemoteId == remoteId && m.Id != map.Id)
            .Select(m => m.DisplayLabel)
            .FirstOrDefaultAsync(ct);

    /// <summary>A lookup found a record that already backs another of our records: never link one to two.</summary>
    private Task<SyncExecutionResult> TakenAsync(Run run, RemoteRecord found, string otherLabel, CancellationToken ct) =>
        FinalAsync(run, "REMOTE_ALREADY_LINKED",
            $"QuickBooks has '{found.Name ?? found.DocNumber}' (id {found.RemoteId}), but it already belongs to '{otherLabel}'. " +
            "Two records here cannot share one QuickBooks record — rename one of them, or link this one by hand (Resolve → Link).",
            SyncState.NeedsResolution, recordRefusal: true, ct);

    private async Task<SyncExecutionResult> WriteAsync(Run run, CancellationToken ct)
    {
        var map   = run.Map;
        var built = run.Built!;
        var staleRetried    = false;
        var adoptedDocument = false;

        while (true)
        {
            var isCreate  = map.RemoteId is null;
            var operation = isCreate ? "Create" : "Update";
            ProviderResult<RemoteRecord> result;

            if (isCreate)
            {
                result = await CallAsync(run, "Create", () => run.Provider.CreateAsync(run.Context, built.Entity, ct));
            }
            else
            {
                var token = map.RemoteSyncToken;
                if (token is null)
                {
                    var current = await CallAsync(run, "Get", () => run.Provider.GetByIdAsync(run.Context, map.Kind, map.RemoteId!, ct));
                    if (!current.IsSuccess) return await ProviderFailureAsync(run, current, "Get", ct);
                    token = current.Value!.SyncToken;
                    map.RemoteSyncToken = token;
                }

                var remoteId = map.RemoteId!;
                result = await CallAsync(run, "Update", () => run.Provider.UpdateAsync(run.Context, built.Entity, remoteId, token, ct));
            }

            switch (result.Outcome)
            {
                case ProviderOutcomeKind.Succeeded:
                    return await SucceedAsync(run, result.Value!, isCreate ? LinkOrigin.Created : null, null, ct);

                case ProviderOutcomeKind.StaleObject when !isCreate && !staleRetried:
                {
                    // Someone changed the record in QuickBooks. Re-read it and apply ours once more.
                    staleRetried = true;
                    var fresh = await CallAsync(run, "Get", () => run.Provider.GetByIdAsync(run.Context, map.Kind, map.RemoteId!, ct));
                    if (!fresh.IsSuccess) return await ProviderFailureAsync(run, fresh, "Get", ct);
                    map.RemoteSyncToken = fresh.Value!.SyncToken;
                    continue;
                }

                case ProviderOutcomeKind.Duplicate when isCreate && !adoptedDocument:
                {
                    var lookup = await CallAsync(run, "Find", () => run.Provider.FindAsync(run.Context, map.Kind, built.Lookup, ct));
                    if (!lookup.IsSuccess) return await ProviderFailureAsync(run, lookup, "Find", ct);

                    var found = lookup.Value;
                    if (found is not null && IsExactMatch(run, found))
                    {
                        if (await LinkedElsewhereAsync(map, found.RemoteId, ct) is { } other)
                            return await TakenAsync(run, found, other, ct);

                        if (SyncPayloads.IsMasterData(map.Kind))
                            // The accountant's record: link it and leave it exactly as it is.
                            return await SucceedAsync(run, found, LinkOrigin.Adopted,
                                $"Linked to the existing QuickBooks record '{found.Name}' (id {found.RemoteId}); it was not changed. " +
                                "Later changes in SCM will update it.", ct);

                        // A document with our number: take it over and bring it in line with ours.
                        map.RemoteId        = found.RemoteId;
                        map.RemoteSyncToken = found.SyncToken;
                        map.LinkOrigin      = LinkOrigin.Adopted;
                        map.LinkedAt        = run.Now;
                        adoptedDocument     = true;
                        continue;
                    }

                    return await DuplicateUnresolvedAsync(run, found, result, ct);
                }

                default:
                    return await ProviderFailureAsync(run, result, operation, ct);
            }
        }
    }

    private bool IsExactMatch(Run run, RemoteRecord found)
    {
        var built = run.Built!;
        return SyncPayloads.IsMasterData(run.Map.Kind)
            ? Same(found.Name, built.EffectiveName)
            : Same(found.DocNumber, built.DocNumber);
    }

    private static bool Same(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    // ── Void ─────────────────────────────────────────────────────────────────

    private async Task<SyncExecutionResult> VoidAsync(Run run, CancellationToken ct)
    {
        var map = run.Map;
        run.Fingerprint = "void:" + (map.PayloadFingerprint ?? map.ExternalId);

        if (map.RemoteId is null && !run.DryRun)
        {
            // A create may be in doubt: find out before declaring there is nothing to void.
            var earlier = await _ledger.FindSucceededAsync(map, ct);
            if (earlier?.RemoteId is not null)
            {
                map.RemoteId = earlier.RemoteId;
            }
            else if (await _ledger.HasUnresolvedAsync(map, string.Empty, run.Now, ct))
            {
                var docNumber = map.RemoteDocNumber ?? DocNumberOf(map);
                if (docNumber is not null)
                {
                    run.Provider = _registry.Get(run.Connection.ProviderKey);
                    run.Context  = run.Connection.ToProviderContext();
                    var lookup = await CallAsync(run, "Find", () => run.Provider.FindAsync(run.Context, map.Kind, new RemoteLookup(DocNumber: docNumber), ct));
                    if (!lookup.IsSuccess) return await ProviderFailureAsync(run, lookup, "Find", ct);
                    if (lookup.Value is { } found)
                    {
                        map.RemoteId        = found.RemoteId;
                        map.RemoteSyncToken = found.SyncToken;
                        map.LinkOrigin    ??= LinkOrigin.Created;
                    }
                }
            }
        }

        if (map.RemoteId is null)
            return await FinishAsync(run, OutboxStatus.Done, "Never sent to QuickBooks — voided locally.", m =>
            {
                m.State         = SyncState.Voided;
                m.LastErrorCode = null;
                m.LastError     = null;
            }, SyncExecutionStatus.Voided, ct);

        if (run.DryRun)
            return await DryRunAsync(run, "Void", new { Void = map.Kind.ToString(), RemoteId = map.RemoteId }, ct);

        run.Provider = _registry.Get(run.Connection.ProviderKey);
        run.Context  = run.Connection.ToProviderContext();

        var decision = await _ledger.ClaimAsync(map, OutboxOperation.Void, run.Fingerprint, run.Now, ct);
        run.Claim = decision.Claim;

        switch (decision.Kind)
        {
            case SyncClaimDecisionKind.InFlight:
                return await RequeueBehindClaimAsync(run, ct);
            case SyncClaimDecisionKind.AlreadySucceeded:
                return await VoidedAsync(run, map.RemoteSyncToken, ct);
            case SyncClaimDecisionKind.AlreadyRefused:
                return await FinalAsync(run, decision.Claim.ErrorCode ?? "VOID_REFUSED",
                    map.LastError ?? "QuickBooks already refused to void this invoice. Check it in QuickBooks.",
                    SyncState.NeedsResolution, recordRefusal: false, ct);
        }

        var staleRetried = false;
        while (true)
        {
            var token = map.RemoteSyncToken;
            if (token is null)
            {
                var current = await CallAsync(run, "Get", () => run.Provider.GetByIdAsync(run.Context, map.Kind, map.RemoteId!, ct));
                if (!current.IsSuccess) return await ProviderFailureAsync(run, current, "Get", ct);
                token = current.Value!.SyncToken;
                map.RemoteSyncToken = token;
            }

            var remoteId = map.RemoteId!;
            var result = await CallAsync(run, "Void", () => run.Provider.VoidInvoiceAsync(run.Context, remoteId, token, ct));

            if (result.IsSuccess)
            {
                await _ledger.RecordSucceededAsync(run.Claim!, remoteId, run.Now, ct);
                return await VoidedAsync(run, result.Value!.SyncToken, ct);
            }

            if (result.Outcome == ProviderOutcomeKind.StaleObject && !staleRetried)
            {
                staleRetried = true;
                map.RemoteSyncToken = null;
                continue;
            }

            return await ProviderFailureAsync(run, result, "Void", ct);
        }
    }

    private Task<SyncExecutionResult> VoidedAsync(Run run, string? syncToken, CancellationToken ct) =>
        FinishAsync(run, OutboxStatus.Done, null, m =>
        {
            m.State           = SyncState.Voided;
            m.RemoteSyncToken = syncToken ?? m.RemoteSyncToken;
            m.LastSyncedAt    = run.Now;
            m.LastErrorCode   = null;
            m.LastError       = null;
        }, SyncExecutionStatus.Voided, ct);

    private static string? DocNumberOf(EntityMap map)
    {
        if (map.PayloadJson is null) return null;
        try
        {
            return SyncPayloads.Deserialize(map.Kind, map.PayloadJson) switch
            {
                SalesInvoicePayload s => s.DocNumber?.Trim(),
                BillPayload b         => b.DocNumber?.Trim(),
                _                     => null
            };
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    // ── Outcomes ─────────────────────────────────────────────────────────────

    private async Task<SyncExecutionResult> SucceedAsync(
        Run run, RemoteRecord record, LinkOrigin? origin, string? note, CancellationToken ct)
    {
        var map   = run.Map;
        var built = run.Built;
        var fp    = run.Fingerprint;

        if (run.Claim is not null) await _ledger.RecordSucceededAsync(run.Claim, record.RemoteId, run.Now, ct);

        var warnings = new List<string>(run.Warnings);
        if (note is not null) warnings.Add(note);
        if (map.Kind == SyncKind.SalesInvoice && run.Payload is SalesInvoicePayload invoice && record.TotalTax is { } qboTax
            && Math.Abs(qboTax - invoice.ExpectedTaxAmount) > PayloadValidator.Tolerance)
            warnings.Add(
                $"QuickBooks calculated tax of {SyncPayloads.Amount(qboTax)}, but SCM's invoice says {SyncPayloads.Amount(invoice.ExpectedTaxAmount)}. " +
                "Check the tax code mapping for this invoice's rates.");

        var warning = warnings.Count == 0 ? null : SyncText.Clip(string.Join(" ", warnings.Distinct()), 1000);

        run.Entry.Status        = OutboxStatus.Done;
        run.Entry.CompletedAt   = run.Now;
        run.Entry.BlockedReason = null;

        await SaveMapAsync(map, m =>
        {
            m.RemoteId        = record.RemoteId;
            m.RemoteSyncToken = record.SyncToken;
            m.RemoteDocNumber = record.DocNumber ?? built?.DocNumber ?? m.RemoteDocNumber;
            if (SyncPayloads.IsMasterData(m.Kind))
                m.RemoteName = SyncText.Clip((built?.EffectiveName ?? record.Name)?.Trim(), 200);
            if (origin == LinkOrigin.Adopted)
            {
                m.LinkOrigin = LinkOrigin.Adopted;
                m.LinkedAt ??= run.Now;
            }
            else
            {
                m.LinkOrigin ??= origin ?? LinkOrigin.Created;
            }
            m.LastSyncedAt          = run.Now;
            m.LastPushedFingerprint = fp;

            // A newer payload arrived while this one was on its way: its own entry sends it; the
            // state and errors are about that one now.
            if (m.PayloadFingerprint == fp)
            {
                m.State         = SyncState.Synced;
                m.LastErrorCode = null;
                m.LastError     = null;
                m.Warning       = warning;
            }
        }, ct);

        await _dependencies.ReleaseDependentsAsync(map, dryRun: false, run.Now, ct);
        return new(SyncExecutionStatus.Succeeded, note);
    }

    private async Task<SyncExecutionResult> SucceededEarlierAsync(Run run, SyncCommandClaim claim, CancellationToken ct)
    {
        var map = run.Map;
        var sameRecord = map.RemoteId == claim.RemoteId;

        run.Entry.Status      = OutboxStatus.Done;
        run.Entry.CompletedAt = run.Now;

        await SaveMapAsync(map, m =>
        {
            if (claim.RemoteId is not null && m.RemoteId != claim.RemoteId)
            {
                m.RemoteId        = claim.RemoteId;
                m.RemoteSyncToken = null; // unknown; fetched before any later update
            }
            else if (!sameRecord)
            {
                m.RemoteSyncToken = null;
            }
            if (SyncPayloads.IsMasterData(m.Kind) && run.Built?.EffectiveName is { } name) m.RemoteName ??= name;
            if (run.Built?.DocNumber is { } doc) m.RemoteDocNumber ??= doc;
            m.LinkOrigin          ??= LinkOrigin.Created;
            m.LastSyncedAt        ??= run.Now;
            m.LastPushedFingerprint = run.Fingerprint;
            if (m.PayloadFingerprint == run.Fingerprint)
            {
                m.State         = SyncState.Synced;
                m.LastErrorCode = null;
                m.LastError     = null;
            }
        }, ct);

        await _dependencies.ReleaseDependentsAsync(map, dryRun: false, run.Now, ct);
        return new(SyncExecutionStatus.Succeeded, "Already sent earlier; nothing was called.");
    }

    private async Task<SyncExecutionResult> DryRunAsync(Run run, string operation, object entity, CancellationToken ct)
    {
        var map = run.Map;

        _db.SyncLog.Add(new SyncLogEntry
        {
            OrganizationId = map.OrganizationId,
            ConnectionId   = run.Connection.Id,
            EntityMapId    = map.Id,
            Operation      = operation,
            Outcome        = "DryRun",
            Message        = "Dry run: built and validated; nothing was sent to QuickBooks.",
            RequestJson    = Redactor.Redact(SyncPayloads.ToLogJson(entity)),
            CreatedAt      = run.Now
        });

        var warning = run.Warnings.Count == 0 ? null : SyncText.Clip(string.Join(" ", run.Warnings.Distinct()), 1000);

        var result = await FinishAsync(run, OutboxStatus.Done, null, m =>
        {
            // A newer payload arrived meanwhile: its own entry dry-runs it; leave the state to it.
            if (operation != "Void" && m.PayloadFingerprint != run.Fingerprint) return;
            m.State         = SyncState.DryRunOk;
            m.LastErrorCode = null;
            m.LastError     = null;
            m.Warning       = warning;
        }, SyncExecutionStatus.DryRun, ct);

        await _dependencies.ReleaseDependentsAsync(map, dryRun: true, run.Now, ct);
        return result;
    }

    private async Task<SyncExecutionResult> WaitForDependenciesAsync(
        Run run, IReadOnlyList<GatewayDependency> unresolved, CancellationToken ct)
    {
        await _dependencies.RequestAsync(run.Map, unresolved, run.Now, ct);

        var entry = run.Entry;
        entry.Status        = OutboxStatus.WaitingOnDependency;
        entry.DependsOnJson = SyncPayloads.SerializeDependencies(unresolved);
        entry.WaitingSince ??= run.Now;

        await SaveMapAsync(run.Map, m => m.State = SyncState.WaitingOnDependency, ct);
        return new(SyncExecutionStatus.WaitingOnDependency,
            "Waiting for " + string.Join(", ", unresolved.Select(d => $"{d.Kind} {d.ExternalId}")));
    }

    private async Task<SyncExecutionResult> RequeueBehindClaimAsync(Run run, CancellationToken ct)
    {
        run.Entry.Status        = OutboxStatus.Queued;
        run.Entry.NextAttemptAt = (run.Claim?.LeaseExpiresAt ?? run.Now) + Grace;
        await SaveMapAsync(run.Map, m => m.State = SyncState.Pending, ct);
        return new(SyncExecutionStatus.InFlight, "Another worker is sending exactly this; checking again after its lease.");
    }

    private async Task<SyncExecutionResult> DuplicateUnresolvedAsync(
        Run run, RemoteRecord? found, ProviderResult<RemoteRecord> refusal, CancellationToken ct)
    {
        var map   = run.Map;
        var built = run.Built!;
        var what  = SyncPayloads.IsDocument(map.Kind) ? $"number '{built.DocNumber}'" : $"name '{built.EffectiveName}'";

        var message = found is not null
            ? $"QuickBooks already has a record with this {what.Split(' ')[0]}: '{found.Name ?? found.DocNumber}' (id {found.RemoteId}), " +
              $"which is not an exact match for {what}. If it is the same, link it (Resolve → Link); otherwise rename one of them."
            : $"QuickBooks refused {what} as a duplicate, but no {map.Kind} with it was found — it is probably used by another " +
              "kind of record (a customer, vendor or employee share one list of names). Rename the record in SCM or in QuickBooks, then Retry.";

        return await FinalAsync(run, refusal.ErrorCode ?? "DUPLICATE", message, SyncState.NeedsResolution, recordRefusal: true, ct);
    }

    /// <summary>What a failed provider call means for this record.</summary>
    private async Task<SyncExecutionResult> ProviderFailureAsync<T>(
        Run run, ProviderResult<T> result, string operation, CancellationToken ct)
    {
        var map  = run.Map;
        var code = result.ErrorCode ?? result.Outcome.ToString().ToUpperInvariant();
        var what = KindLabel(map.Kind);
        var field = string.IsNullOrWhiteSpace(result.ErrorField) ? string.Empty : $" (field: {result.ErrorField})";

        switch (result.Outcome)
        {
            case ProviderOutcomeKind.Throttled:
                return await RetryLaterAsync(run, code,
                    "QuickBooks asked us to slow down (too many requests).", stopConnection: true, ct);

            case ProviderOutcomeKind.Transient:
                return await RetryLaterAsync(run, code,
                    $"QuickBooks did not answer clearly ({Redactor.Redact(result.Message) ?? "timeout or server error"}).", stopConnection: false, ct);

            case ProviderOutcomeKind.AuthRevoked:
                return await ConnectionLostAsync(run, ConnectionStatus.Revoked,
                    Redactor.Redact(result.Message) ?? "QuickBooks refused our access token.", ct);

            case ProviderOutcomeKind.NotFound:
                return await FinalAsync(run, "REMOTE_NOT_FOUND",
                    $"The QuickBooks {what} (id {map.RemoteId}) no longer exists — it was probably deleted in QuickBooks. " +
                    "Link this record to another QuickBooks record (Resolve → Link) or mark it resolved.",
                    SyncState.NeedsResolution, recordRefusal: true, ct);

            case ProviderOutcomeKind.StaleObject:
                return await FinalAsync(run, "STALE_OBJECT",
                    $"The {what} kept changing in QuickBooks while it was being updated. Retry it once nobody is editing it there.",
                    SyncState.Failed, recordRefusal: true, ct);

            case ProviderOutcomeKind.Duplicate:
                return await FinalAsync(run, code,
                    $"QuickBooks refused the change: another record already uses this name or number{field}. " +
                    $"{Redactor.Redact(result.Message)} Rename it in SCM or QuickBooks, then Retry.",
                    SyncState.NeedsResolution, recordRefusal: true, ct);

            default: // Refused
            {
                // Plan D-9: a refused edit or void of a document already in QuickBooks (changed or paid
                // there) needs a person — never a delete-and-recreate.
                var needsPerson = SyncPayloads.IsDocument(map.Kind) && operation is "Update" or "Void";
                var message = $"QuickBooks refused the {what}{field}: {Redactor.Redact(result.Message) ?? "no reason given"}. " +
                              (needsPerson
                                  ? "The document may have been changed or paid in QuickBooks — check it there, then Resolve."
                                  : "Correct it in SCM and save it again; it is not retried until then.");
                return await FinalAsync(run, code, message,
                    needsPerson ? SyncState.NeedsResolution : SyncState.Failed, recordRefusal: true, ct);
            }
        }
    }

    /// <summary>A final answer for this payload: entry Failed, map in <paramref name="state"/>.</summary>
    private async Task<SyncExecutionResult> FinalAsync(
        Run run, string code, string message, SyncState state, bool recordRefusal, CancellationToken ct)
    {
        if (recordRefusal && run.Claim is not null)
            await _ledger.RecordRefusedAsync(run.Claim, code, run.Now, ct);

        return await FinishAsync(run, OutboxStatus.Failed, message, m =>
        {
            m.State         = state;
            m.LastErrorCode = SyncText.Clip(code, 100);
            m.LastError     = SyncText.Clip(message, 2000);
            // Nothing was created: release the name reserved for the create.
            if (m.RemoteId is null && SyncPayloads.IsMasterData(m.Kind)) m.RemoteName = null;
        }, state == SyncState.NeedsResolution ? SyncExecutionStatus.NeedsResolution : SyncExecutionStatus.Failed, ct);
    }

    private async Task<SyncExecutionResult> RetryLaterAsync(
        Run run, string code, string message, bool stopConnection, CancellationToken ct)
    {
        // The call may or may not have happened. The next attempt looks the record up first.
        if (run.Claim is not null) await _ledger.RecordUnknownAsync(run.Claim, code, run.Now, ct);

        var entry = run.Entry;
        entry.AttemptCount++;

        if (entry.AttemptCount >= MaxAttempts)
        {
            var final = $"Gave up after {entry.AttemptCount} attempts. Last problem: {message} " +
                        "Whether QuickBooks received the last attempt is unknown — check QuickBooks, then Resolve " +
                        "(link the record if it is there, or requeue it).";
            entry.Status        = OutboxStatus.Failed;
            entry.CompletedAt   = run.Now;
            entry.BlockedReason = SyncText.Clip(final, 1000);
            await SaveMapAsync(run.Map, m =>
            {
                m.State         = SyncState.NeedsResolution;
                m.LastErrorCode = SyncText.Clip(code, 100);
                m.LastError     = SyncText.Clip(final, 2000);
            }, ct);
            return new(SyncExecutionStatus.NeedsResolution, final, stopConnection);
        }

        var next = run.Now + SyncBackoff.After(entry.AttemptCount);
        var note = $"{message} Retrying automatically at {next:u} (attempt {entry.AttemptCount + 1} of {MaxAttempts}).";
        entry.Status        = OutboxStatus.Queued;
        entry.NextAttemptAt = next;
        entry.BlockedReason = SyncText.Clip(note, 1000);
        await SaveMapAsync(run.Map, m =>
        {
            m.State         = SyncState.Pending;
            m.LastErrorCode = SyncText.Clip(code, 100);
            m.LastError     = SyncText.Clip(note, 2000);
        }, ct);

        return new(SyncExecutionStatus.RetryScheduled, note, stopConnection);
    }

    private async Task<SyncExecutionResult> ConnectionLostAsync(Run run, ConnectionStatus status, string reason, CancellationToken ct)
    {
        if (run.Claim is not null) await _ledger.RecordUnknownAsync(run.Claim, "AUTH_REVOKED", run.Now, ct);

        var unavailable = status is ConnectionStatus.Revoked or ConnectionStatus.Expired ? status : ConnectionStatus.Revoked;
        var message = $"QuickBooks refused our access ({reason}). Everything queued for this company is paused and resumes " +
                      "when an administrator reconnects.";

        await _health.MarkUnavailableAsync(run.Connection.Id, unavailable, SyncText.Clip(reason, 900)!, ct);
        await _outboxControl.SuspendAllAsync(run.Connection.Id, SyncText.Clip(message, 1000)!, ct);

        run.Entry.Status        = OutboxStatus.Suspended;
        run.Entry.BlockedReason = SyncText.Clip(message, 1000);
        await SaveMapAsync(run.Map, m =>
        {
            m.State         = SyncState.Pending;
            m.LastErrorCode = "CONNECTION_" + unavailable.ToString().ToUpperInvariant();
            m.LastError     = SyncText.Clip(message, 2000);
        }, ct);

        return new(SyncExecutionStatus.Suspended, message, StopConnection: true);
    }

    private async Task<SyncExecutionResult> FinishAsync(
        Run run, OutboxStatus entryStatus, string? reason, Action<EntityMap> applyToMap, SyncExecutionStatus status,
        CancellationToken ct)
    {
        run.Entry.Status        = entryStatus;
        run.Entry.BlockedReason = SyncText.Clip(reason, 1000);
        if (entryStatus is OutboxStatus.Done or OutboxStatus.Failed) run.Entry.CompletedAt = run.Now;

        await SaveMapAsync(run.Map, applyToMap, ct);
        return new(status, reason);
    }

    /// <summary>
    /// Last resort after an unexpected exception: start from a clean context, record the claim as
    /// unknown (the call may have happened) and schedule a retry with backoff.
    /// </summary>
    private async Task<SyncExecutionResult> RecoverAsync(
        int entryId, int mapId, int? claimId, Exception error, DateTime now, CancellationToken ct)
    {
        try
        {
            _db.ChangeTracker.Clear();

            if (claimId is { } id)
            {
                var claim = await _db.CommandClaims.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == id, ct);
                if (claim is { Status: ClaimStatus.InFlight }) await _ledger.RecordUnknownAsync(claim, "UNEXPECTED_ERROR", now, ct);
            }

            var entry = await _db.Outbox.FirstAsync(e => e.Id == entryId, ct);
            var map   = await _db.EntityMaps.FirstAsync(m => m.Id == mapId, ct);

            entry.AttemptCount++;
            var giveUp = entry.AttemptCount >= MaxAttempts;
            var message = Redactor.Redact($"Unexpected error: {error.Message}")!;

            entry.Status        = giveUp ? OutboxStatus.Failed : OutboxStatus.Queued;
            entry.NextAttemptAt = now + SyncBackoff.After(entry.AttemptCount);
            entry.CompletedAt   = giveUp ? now : null;
            entry.BlockedReason = SyncText.Clip(message, 1000);

            map.State         = giveUp ? SyncState.NeedsResolution : SyncState.Pending;
            map.LastErrorCode = "UNEXPECTED_ERROR";
            map.LastError     = SyncText.Clip(message, 2000);

            await _db.SaveChangesAsync(ct);
            return new(giveUp ? SyncExecutionStatus.NeedsResolution : SyncExecutionStatus.RetryScheduled, message);
        }
        catch (Exception inner) when (inner is not OperationCanceledException)
        {
            // Leave it Running: the sweep hands it back after the lease, and the ledger keeps it safe.
            _logger.LogError(inner, "Could not record the failure of outbox entry {EntryId}.", entryId);
            return new(SyncExecutionStatus.RetryScheduled, error.Message);
        }
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Saves, re-applying our outcome if the gateway stored a newer payload on the map in the meantime
    /// (the map's RowVersion). The payload the gateway wrote is kept; only our fields are re-applied.
    /// </summary>
    private async Task SaveMapAsync(EntityMap map, Action<EntityMap> apply, CancellationToken ct)
    {
        apply(map);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 4)
            {
                await _db.Entry(map).ReloadAsync(ct);
                apply(map);
            }
        }
    }

    /// <summary>Times a provider call and writes its sync-log row (redacted again here, whatever the provider did).</summary>
    private async Task<ProviderResult<T>> CallAsync<T>(Run run, string operation, Func<Task<ProviderResult<T>>> call)
    {
        var started = Stopwatch.GetTimestamp();
        ProviderResult<T> result;

        try
        {
            result = await call();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log(run, operation, "Exception", ex.GetType().Name, ex.Message, null, null,
                (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds, null);

            // Kept now: the recovery path starts from a clean context and would drop it.
            try
            {
                await _db.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception saveError) when (saveError is not OperationCanceledException)
            {
                _logger.LogWarning(saveError, "Could not save the sync-log row for a failed {Operation}.", operation);
            }

            throw;
        }

        Log(run, operation, result.Outcome.ToString(), result.ErrorCode,
            result.Message ?? (result.IsSuccess ? null : result.Outcome.ToString()),
            result.RequestJson, result.ResponseJson,
            result.DurationMs > 0 ? result.DurationMs : (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            result.IntuitTid);

        return result;
    }

    private void Log(Run run, string operation, string outcome, string? errorCode, string? message,
                     string? request, string? response, int durationMs, string? intuitTid) =>
        _db.SyncLog.Add(new SyncLogEntry
        {
            OrganizationId = run.Map.OrganizationId,
            ConnectionId   = run.Connection.Id,
            EntityMapId    = run.Map.Id,
            Operation      = SyncText.Clip(operation, 30)!,
            Outcome        = SyncText.Clip(outcome, 30)!,
            ErrorCode      = SyncText.Clip(errorCode, 100),
            Message        = SyncText.Clip(Redactor.Redact(message), 2000),
            RequestJson    = Redactor.Redact(request),
            ResponseJson   = Redactor.Redact(response),
            DurationMs     = durationMs,
            IntuitTid      = SyncText.Clip(intuitTid, 100),
            CreatedAt      = run.Now
        });

    private static string KindLabel(SyncKind kind) => kind switch
    {
        SyncKind.Customer     => "customer",
        SyncKind.Vendor       => "vendor",
        SyncKind.Item         => "item",
        SyncKind.SalesInvoice => "invoice",
        SyncKind.Bill         => "bill",
        _                     => kind.ToString()
    };
}

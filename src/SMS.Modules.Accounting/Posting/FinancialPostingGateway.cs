using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;
using SMS.Shared.Common;
using SMS.Shared.Finance.Posting;

namespace SMS.Modules.Accounting.Posting;

/// <summary>
/// A44 C4 — the posting gateway (D-9, D-10, D-13). Called by the source modules <b>after</b> their own commit; it
/// <list type="number">
/// <item>validates the transaction (BR-01 Σdebit = Σcredit within 0.001, BR-02 lines non-negative and one-sided, every
///   line has an account role or code) → <see cref="PostingStatus.ValidationFailed"/>;</item>
/// <item>checks the licence (D-10): FEATURE_GENERAL_LEDGER or MODULE_INTEGRATION → else ModuleNotLicensed;</item>
/// <item>asks every registered <see cref="IFinancialPostingTarget"/> whether it is active → none: NoTargetConfigured;</item>
/// <item>records the posting in <c>finance.FinancialPostings</c> (one row per organization + key) with one target row per
///   active target, then dispatches to each — one target's failure never stops another (PartialFailure);</item>
/// <item>is idempotent by key (D-13): the same payload again is not posted to the GL again (DuplicateSkipped when nothing
///   else is called), while external targets are always called again (D-44 — they dedupe by their own fingerprint); a
///   changed payload is an update, sent to every active target again.</item>
/// <item>a void skips the original's still-open target rows (D-45), so a later retry never posts a voided document.</item>
/// </list>
/// Failed targets are retried by <see cref="PostingRetryJob"/> (30 s, 2 min, 8 min, 32 min, 2 h, then DeadLetter). The
/// gateway never throws: anything unexpected is logged and answered as PartialFailure.
/// </summary>
internal sealed class FinancialPostingGateway : IFinancialPostingGateway
{
    internal const decimal BalanceTolerance = 0.001m;

    /// <summary>D-9 — the wait after the n-th failed attempt; one more failure after the last → DeadLetter.</summary>
    internal static readonly TimeSpan[] Backoff =
        [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(32), TimeSpan.FromHours(2)];

    internal static int MaxRetries => Backoff.Length;

    internal static readonly IReadOnlySet<FinDocType> CreditTypes = new HashSet<FinDocType>
    {
        FinDocType.CreditNote, FinDocType.VendorCredit, FinDocType.RefundReceipt, FinDocType.POSReturn,
        FinDocType.SalesReturn, FinDocType.PurchaseReturn,
    };

    private readonly AccountingDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IReadOnlyList<IFinancialPostingTarget> _targets;
    private readonly IModuleGate? _gate;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;

    public FinancialPostingGateway(AccountingDbContext db, ITenantContext tenant, IEnumerable<IFinancialPostingTarget> targets,
        IModuleGate? gate = null, TimeProvider? clock = null, ILogger<FinancialPostingGateway>? log = null)
    {
        _db = db;
        _tenant = tenant;
        _targets = targets.GroupBy(t => t.TargetCode, StringComparer.Ordinal).Select(g => g.First()).ToList();
        _gate = gate;
        _clock = clock ?? TimeProvider.System;
        _log = log ?? (ILogger)NullLogger.Instance;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    // ── IFinancialPostingGateway ───────────────────────────────────────────────────────────────────────────────────
    // D-47 — the caller's token is not used: the gateway runs after the caller's commit, and a client that disconnects must
    // not leave the posting unrecorded (REV-6). Everything below runs with CancellationToken.None.

    public Task<PostingResult> PostAsync(FinancialTransaction transaction, CancellationToken ct = default) =>
        Guard(transaction?.SourceDocumentId ?? Guid.Empty, transaction?.DocumentType ?? default, () => PostCoreAsync(transaction!));

    public Task<PostingResult> IssueCreditAsync(FinancialTransaction credit, CancellationToken ct = default)
    {
        if (credit is not null && !CreditTypes.Contains(credit.DocumentType))
            return Task.FromResult(Result(PostingStatus.ValidationFailed, credit.SourceDocumentId, credit.DocumentType,
                $"{credit.DocumentType} is not a credit document."));
        return PostAsync(credit!);
    }

    public Task<PostingResult> VoidAsync(VoidRequest request, CancellationToken ct = default) =>
        Guard(request?.SourceDocumentId ?? Guid.Empty, request?.DocumentType ?? default, () => VoidCoreAsync(request!, PostingKinds.Void));

    public Task<PostingResult> ReverseAsync(VoidRequest request, CancellationToken ct = default) =>
        Guard(request?.SourceDocumentId ?? Guid.Empty, request?.DocumentType ?? default, () => VoidCoreAsync(request!, PostingKinds.Reverse));

    /// <summary>BR-03 — the caller's commit is already done; nothing here may fail it.</summary>
    private async Task<PostingResult> Guard(Guid sourceId, FinDocType type, Func<Task<PostingResult>> work)
    {
        try
        {
            return await work();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Financial posting of {DocumentType} {SourceDocumentId} failed unexpectedly.", type, sourceId);
            return Result(PostingStatus.PartialFailure, sourceId, type, "The posting could not be recorded: " + ex.Message);
        }
    }

    /// <summary>
    /// D-44 (REV-3) — which targets get a repeated identical payload. The internal GL dedupes on the fingerprint here (it
    /// keeps no other dedupe); an external target is always called again, because its own map fingerprint dedupes cheaply
    /// and a re-send is how held records are released, "Push now" works and a reconnected company is filled (D-3).
    /// </summary>
    private static bool DedupesHere(IFinancialPostingTarget target) => target.TargetCode == PostingTargetCodes.InternalGl;

    // ── post ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<PostingResult> PostCoreAsync(FinancialTransaction tx)
    {
        if (tx is null) return Result(PostingStatus.ValidationFailed, Guid.Empty, default, "The transaction is required.");
        var org = _tenant.OrganizationId;
        var problems = Validate(tx, org);
        if (problems.Count > 0)
        {
            _log.LogWarning("Financial posting {Key} refused: {Problems}", tx.IdempotencyKey, string.Join("; ", problems));
            return Result(PostingStatus.ValidationFailed, tx.SourceDocumentId, tx.DocumentType, string.Join(" ", problems));
        }
        if (!await LicensedAsync(org))
            return Result(PostingStatus.ModuleNotLicensed, tx.SourceDocumentId, tx.DocumentType,
                "Neither the general ledger nor an accounting integration is licensed for this organization.");

        var active = await ActiveTargetsAsync();
        var snap = PostingSerializer.Snapshot(tx);
        var key = tx.IdempotencyKey;
        var posting = await LoadAsync(org, key);

        if (posting is null)
        {
            if (active.Count == 0) return NoTarget(tx.SourceDocumentId, tx.DocumentType);
            posting = NewPosting(org, key, PostingKinds.Post, tx.DocumentType, tx.SourceModule, tx.SourceDocumentId, tx.SourceDocumentNumber,
                DateOnly.FromDateTime(tx.TransactionDate), tx.PartnerId, tx.PartnerType?.ToString(), tx.PartnerName, tx.CurrencyCode,
                tx.Lines.Sum(l => l.Debit), snap);
            var created = await TryCreateAsync(posting, active);
            if (created is null) return await DispatchAsync(posting.Id, active, t => t.PostAsync(tx), snap.Fingerprint, tx.SourceDocumentId, tx.DocumentType);
            posting = created; // lost the insert race: the winner's row is the existing posting
        }

        if (posting.Kind != PostingKinds.Post)
            return Result(PostingStatus.ValidationFailed, tx.SourceDocumentId, tx.DocumentType, "The idempotency key belongs to a void.");

        if (posting.Fingerprint == snap.Fingerprint)
        {
            // D-13 / D-44 — same payload: the GL only when it has no row yet; external targets always.
            var send = active.Where(t => !DedupesHere(t) || posting.Targets.All(s => s.TargetCode != t.TargetCode)).ToList();
            if (send.Count == 0) return Duplicate(posting, tx.SourceDocumentId, tx.DocumentType);
            AddTargetRows(posting, send.Where(t => posting.Targets.All(s => s.TargetCode != t.TargetCode)));
            await _db.SaveChangesAsync(CancellationToken.None);
            return await DispatchAsync(posting.Id, send, t => t.PostAsync(tx), snap.Fingerprint, tx.SourceDocumentId, tx.DocumentType);
        }

        // D-13 — same key, changed payload: an update. Stored first, then every active target gets it again.
        posting.Version++;
        posting.Fingerprint = snap.Fingerprint;
        posting.TransactionJson = snap.TransactionJson;
        posting.DocumentJson = snap.DocumentJson;
        posting.DocumentPayloadType = snap.DocumentType;
        posting.SourceDocumentNumber = Truncate(tx.SourceDocumentNumber ?? string.Empty, 100);
        posting.TransactionDate = DateOnly.FromDateTime(tx.TransactionDate);
        posting.PartnerUuid = tx.PartnerId;
        posting.PartnerType = tx.PartnerType?.ToString();
        posting.PartnerName = tx.PartnerName is null ? null : Truncate(tx.PartnerName, 200);
        posting.CurrencyCode = Truncate(tx.CurrencyCode.Trim().ToUpperInvariant(), 10);
        posting.TotalAmount = tx.Lines.Sum(l => l.Debit);
        if (active.Count == 0)
        {
            await _db.SaveChangesAsync(CancellationToken.None);
            return NoTarget(tx.SourceDocumentId, tx.DocumentType);
        }
        AddTargetRows(posting, active.Where(t => posting.Targets.All(s => s.TargetCode != t.TargetCode)));
        foreach (var state in posting.Targets.Where(s => active.Any(t => t.TargetCode == s.TargetCode)))
        {
            state.Status = PostingTargetStatuses.Pending;
            state.Attempts = 0;
            state.NextAttemptAt = null;
        }
        await _db.SaveChangesAsync(CancellationToken.None);
        _log.LogInformation("Financial posting {Key} updated to version {Version} (payload changed).", key, posting.Version);
        return await DispatchAsync(posting.Id, active, t => t.PostAsync(tx), snap.Fingerprint, tx.SourceDocumentId, tx.DocumentType);
    }

    // ── void / reverse ─────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<PostingResult> VoidCoreAsync(VoidRequest request, string kind)
    {
        if (request is null) return Result(PostingStatus.ValidationFailed, Guid.Empty, default, "The void request is required.");
        var org = _tenant.OrganizationId;
        var problems = new List<string>();
        if (org == Guid.Empty) problems.Add("There is no organization to post for.");
        if (request.SourceDocumentId == Guid.Empty) problems.Add("The source document id is required.");
        if (string.IsNullOrWhiteSpace(request.SourceModule)) problems.Add("The source module is required.");
        if (problems.Count > 0) return Result(PostingStatus.ValidationFailed, request.SourceDocumentId, request.DocumentType, string.Join(" ", problems));
        if (!await LicensedAsync(org))
            return Result(PostingStatus.ModuleNotLicensed, request.SourceDocumentId, request.DocumentType,
                "Neither the general ledger nor an accounting integration is licensed for this organization.");

        var active = await ActiveTargetsAsync();
        var key = request.IdempotencyKey;
        var json = PostingSerializer.Serialize(request);
        var fingerprint = PostingSerializer.Fingerprint(key, null, kind); // one void per document: the reason is not part of it
        var original = await LoadAsync(org, OriginalKey(request));
        var posting = await LoadAsync(org, key);
        if (posting is null)
        {
            if (active.Count == 0) return NoTarget(request.SourceDocumentId, request.DocumentType);
            posting = NewPosting(org, key, kind, request.DocumentType, request.SourceModule, request.SourceDocumentId,
                string.IsNullOrEmpty(request.SourceDocumentNumber) ? original?.SourceDocumentNumber ?? string.Empty : request.SourceDocumentNumber,
                DateOnly.FromDateTime(request.VoidDate ?? Now), original?.PartnerUuid, original?.PartnerType, original?.PartnerName,
                original?.CurrencyCode, original?.TotalAmount ?? 0m, new PostingSnapshot(json, null, null, fingerprint));
            var created = await TryCreateAsync(posting, active);
            if (created is not null) posting = created;
            else
            {
                await SkipOpenOriginalTargetsAsync(original);
                return await DispatchAsync(posting.Id, active, t => kind == PostingKinds.Reverse ? t.ReverseAsync(request) : t.VoidAsync(request),
                    fingerprint, request.SourceDocumentId, request.DocumentType);
            }
        }

        await SkipOpenOriginalTargetsAsync(original);
        var send = active.Where(t => !DedupesHere(t) || posting.Targets.All(s => s.TargetCode != t.TargetCode)).ToList();
        if (send.Count == 0) return Duplicate(posting, request.SourceDocumentId, request.DocumentType);
        AddTargetRows(posting, send.Where(t => posting.Targets.All(s => s.TargetCode != t.TargetCode)));
        await _db.SaveChangesAsync(CancellationToken.None);
        return await DispatchAsync(posting.Id, send, t => kind == PostingKinds.Reverse ? t.ReverseAsync(request) : t.VoidAsync(request),
            fingerprint, request.SourceDocumentId, request.DocumentType);
    }

    internal static string OriginalKey(VoidRequest r) => $"{r.SourceModule}:{r.DocumentType}:{r.SourceDocumentId}";

    /// <summary>
    /// D-45 (REV-2) — a void that arrives while the original is still Pending / Failed / DeadLetter at a target: that target
    /// must never post the original later (e.g. after the period reopens), so its row becomes Skipped.
    /// </summary>
    private async Task SkipOpenOriginalTargetsAsync(FinancialPosting? original)
    {
        if (original is null) return;
        var open = original.Targets.Where(t => PostingTargetStatuses.IsOpen(t.Status)).ToList();
        if (open.Count == 0) return;
        foreach (var t in open)
        {
            t.Status = PostingTargetStatuses.Skipped;
            t.NextAttemptAt = null;
            t.LastError = "VOIDED_BEFORE_POSTING: the document was voided before it reached this target.";
        }
        await _db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>A void or reversal of the same document has been recorded.</summary>
    internal Task<bool> IsVoidedAsync(Guid org, string postingKey) =>
        _db.FinancialPostings.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(p => p.OrganizationId == org && p.IdempotencyKey == postingKey + ":void");

    // ── retry (job, manual) ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends one stored posting to one target again, rebuilt from the log (the transaction + its document).
    /// <paramref name="resetAttempts"/> (manual retry) starts the retry count again. A target that is gone or no longer
    /// active → Skipped "TARGET_INACTIVE"; a Post whose document has been voided since → Skipped "VOIDED" (REV-2). Never throws.
    /// </summary>
    internal async Task<TargetResult> RetryAsync(int postingId, string targetCode, bool resetAttempts = false)
    {
        try
        {
            var state = await StateAsync(postingId, targetCode);
            if (state is null) return TargetResult.Failed(targetCode, "The posting target row no longer exists.");
            var posting = state.Posting;
            if (resetAttempts)
            {
                state.Attempts = 0;
                state.NextAttemptAt = null;
            }
            var target = _targets.FirstOrDefault(t => t.TargetCode == targetCode);
            TargetResult? skipped = null;
            if (target is null || !await IsActiveSafeAsync(target))
                skipped = TargetResult.Skipped(targetCode, "TARGET_INACTIVE: the target no longer takes postings for this organization.");
            else if (posting.Kind == PostingKinds.Post && await IsVoidedAsync(posting.OrganizationId, posting.IdempotencyKey))
                skipped = TargetResult.Skipped(targetCode, "VOIDED: the document was voided before it reached this target.");
            if (skipped is not null)
            {
                Apply(state, skipped, posting.Fingerprint);
                await _db.SaveChangesAsync(CancellationToken.None);
                return skipped;
            }
            if (posting.Kind == PostingKinds.Post)
            {
                var document = PostingSerializer.DeserializeDocument(posting.DocumentJson, posting.DocumentPayloadType);
                var tx = PostingSerializer.DeserializeTransaction(posting.TransactionJson, document);
                return await DispatchOneAsync(state, posting.Fingerprint, () => target!.PostAsync(tx));
            }
            var request = PostingSerializer.DeserializeVoid(posting.TransactionJson);
            return await DispatchOneAsync(state, posting.Fingerprint,
                () => posting.Kind == PostingKinds.Reverse ? target!.ReverseAsync(request) : target!.VoidAsync(request));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Retry of posting {PostingId} at {Target} failed unexpectedly.", postingId, targetCode);
            return TargetResult.Failed(targetCode, ex.Message);
        }
    }

    // ── shared ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<PostingResult> DispatchAsync(int postingId, IReadOnlyList<IFinancialPostingTarget> targets,
        Func<IFinancialPostingTarget, Task<TargetResult>> call, string fingerprint, Guid sourceId, FinDocType type)
    {
        var results = new List<TargetResult>();
        foreach (var target in targets)
        {
            // Re-read each time: a failed target clears the change tracker (REV-4).
            var state = await StateAsync(postingId, target.TargetCode)
                        ?? throw new InvalidOperationException("The posting target row disappeared.");
            results.Add(await DispatchOneAsync(state, fingerprint, () => call(target)));
        }
        var posting = await _db.FinancialPostings.IgnoreQueryFilters().Include(p => p.Targets).SingleAsync(p => p.Id == postingId);
        return Summarize(posting, sourceId, type, results);
    }

    /// <summary>The target row, tracked (the same instance a target sees in this scope).</summary>
    private Task<FinancialPostingTargetState?> StateAsync(int postingId, string targetCode) =>
        _db.FinancialPostingTargets.IgnoreQueryFilters().Include(t => t.Posting)
            .FirstOrDefaultAsync(t => t.PostingId == postingId && t.TargetCode == targetCode);

    /// <summary>
    /// One target call. A throwing target is a Failed result: the change tracker is cleared (whatever the target left
    /// there — added journals, rows saved inside its rolled-back transaction — must not reach a later save, REV-4) and the
    /// target row is read again before the failure is recorded and saved.
    /// </summary>
    private async Task<TargetResult> DispatchOneAsync(FinancialPostingTargetState state, string fingerprint, Func<Task<TargetResult>> call)
    {
        TargetResult result;
        try
        {
            result = await call() ?? TargetResult.Failed(state.TargetCode, "The target returned no result.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Posting {Key} failed at {Target}.", state.Posting.IdempotencyKey, state.TargetCode);
            var (postingId, code) = (state.PostingId, state.TargetCode);
            _db.ChangeTracker.Clear();
            state = await StateAsync(postingId, code) ?? throw new InvalidOperationException("The posting target row disappeared.");
            result = TargetResult.Failed(code, ex.GetBaseException().Message);
        }
        result = new TargetResult
        {
            TargetCode = state.TargetCode, Status = result.Status, InternalId = result.InternalId,
            ExternalId = result.ExternalId, Error = result.Error,
        };
        Apply(state, result, fingerprint);
        await _db.SaveChangesAsync(CancellationToken.None);
        _log.LogInformation("Posting {Key} at {Target}: {Status}{Error}.", state.Posting.IdempotencyKey, state.TargetCode, state.Status,
            state.LastError is null ? string.Empty : " — " + state.LastError);
        return state.Status == PostingTargetStatuses.DeadLetter
            ? new TargetResult { TargetCode = result.TargetCode, Status = TargetPostingStatus.Failed, InternalId = result.InternalId,
                ExternalId = result.ExternalId, Error = result.Error }
            : result;
    }

    /// <summary>D-9 — a result onto the target row: success clears the retry state; a failure schedules the next attempt,
    /// or dead-letters after <see cref="MaxRetries"/> retries.</summary>
    internal void Apply(FinancialPostingTargetState state, TargetResult result, string fingerprint)
    {
        var now = Now;
        state.LastAttemptAt = now;
        state.MirroredFromTarget = false; // an Accounting dispatch (or its answer) is never a mirror
        if (result.InternalId is not null) state.InternalId = Truncate(result.InternalId, 100);
        if (result.ExternalId is not null) state.ExternalId = Truncate(result.ExternalId, 100);
        switch (result.Status)
        {
            case TargetPostingStatus.Failed:
                state.Attempts++;
                state.LastError = Truncate(result.Error ?? "Failed.", 1000);
                if (state.Attempts > MaxRetries)
                {
                    state.Status = PostingTargetStatuses.DeadLetter;
                    state.NextAttemptAt = null;
                }
                else
                {
                    state.Status = PostingTargetStatuses.Failed;
                    state.NextAttemptAt = now + Backoff[state.Attempts - 1];
                }
                break;
            default:
                state.Status = result.Status switch
                {
                    TargetPostingStatus.Posted => PostingTargetStatuses.Posted,
                    TargetPostingStatus.Queued => PostingTargetStatuses.Queued,
                    TargetPostingStatus.Held   => PostingTargetStatuses.Held,
                    _                          => PostingTargetStatuses.Skipped,
                };
                state.Attempts = 0;
                state.NextAttemptAt = null;
                state.LastError = result.Error is null ? null : Truncate(result.Error, 1000);
                state.PostedFingerprint = fingerprint;
                if (result.Status is not TargetPostingStatus.Skipped) state.PostedAt = now;
                break;
        }
    }

    internal static List<string> Validate(FinancialTransaction tx, Guid org)
    {
        var problems = new List<string>();
        if (org == Guid.Empty) problems.Add("There is no organization to post for.");
        if (tx.SourceDocumentId == Guid.Empty) problems.Add("The source document id is required.");
        if (string.IsNullOrWhiteSpace(tx.SourceModule)) problems.Add("The source module is required.");
        if (tx.TransactionDate == default) problems.Add("The transaction date is required.");
        if (string.IsNullOrWhiteSpace(tx.CurrencyCode)) problems.Add("The currency is required.");
        if (tx.ExchangeRate is { } rate && rate <= 0) problems.Add("The exchange rate must be greater than zero.");
        foreach (var line in tx.Lines ?? [])
        {
            if (line.Debit < 0 || line.Credit < 0) problems.Add($"Line {line.LineNumber}: amounts cannot be negative.");
            else if (line.Debit > 0 && line.Credit > 0) problems.Add($"Line {line.LineNumber}: a line is either a debit or a credit.");
            if (string.IsNullOrWhiteSpace(line.AccountCode))
            {
                if (string.IsNullOrWhiteSpace(line.AccountRole)) problems.Add($"Line {line.LineNumber}: an account role or account code is required.");
                else if (!DefaultKeys.ByKey.ContainsKey(line.AccountRole)) problems.Add($"Line {line.LineNumber}: unknown account role {line.AccountRole}.");
            }
        }
        var debit = tx.Lines?.Sum(l => l.Debit) ?? 0m;
        var credit = tx.Lines?.Sum(l => l.Credit) ?? 0m;
        if (Math.Abs(debit - credit) > BalanceTolerance)
            problems.Add($"Debits ({debit:0.####}) and credits ({credit:0.####}) do not balance.");
        return problems;
    }

    /// <summary>D-10 — MODULE_FINANCE is always on; at least one target module must be licensed. No gate (tests) = licensed.</summary>
    private async Task<bool> LicensedAsync(Guid org) =>
        _gate is null
        || await _gate.IsEnabledAsync(org, AccountingCodes.FeatureGeneralLedger)
        || await _gate.IsEnabledAsync(org, ModuleCodes.Integration);

    private async Task<IReadOnlyList<IFinancialPostingTarget>> ActiveTargetsAsync()
    {
        var active = new List<IFinancialPostingTarget>();
        foreach (var target in _targets)
            if (await IsActiveSafeAsync(target)) active.Add(target);
        return active;
    }

    private async Task<bool> IsActiveSafeAsync(IFinancialPostingTarget target)
    {
        try
        {
            return await target.IsActiveAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Posting target {Target} could not say whether it is active; treated as inactive.", target.TargetCode);
            return false;
        }
    }

    private Task<FinancialPosting?> LoadAsync(Guid org, string key) =>
        _db.FinancialPostings.IgnoreQueryFilters().Include(p => p.Targets)
            .FirstOrDefaultAsync(p => p.OrganizationId == org && p.IdempotencyKey == key);

    private static FinancialPosting NewPosting(Guid org, string key, string kind, FinDocType type, string module, Guid sourceId, string number,
        DateOnly date, Guid? partner, string? partnerType, string? partnerName, string? currency, decimal total, PostingSnapshot snap) => new()
    {
        OrganizationId       = org,
        IdempotencyKey       = Truncate(key, 200),
        Kind                 = kind,
        DocumentType         = type.ToString(),
        SourceModule         = Truncate(module, 30),
        SourceDocumentUuid   = sourceId,
        SourceDocumentNumber = Truncate(number ?? string.Empty, 100),
        TransactionDate      = date,
        PartnerUuid          = partner,
        PartnerType          = partnerType,
        PartnerName          = partnerName is null ? null : Truncate(partnerName, 200),
        CurrencyCode         = currency is null ? null : Truncate(currency.Trim().ToUpperInvariant(), 10),
        TotalAmount          = total,
        Fingerprint          = snap.Fingerprint,
        TransactionJson      = snap.TransactionJson,
        DocumentJson         = snap.DocumentJson,
        DocumentPayloadType  = snap.DocumentType,
    };

    private static void AddTargetRows(FinancialPosting posting, IEnumerable<IFinancialPostingTarget> targets)
    {
        foreach (var t in targets)
            posting.Targets.Add(new FinancialPostingTargetState { OrganizationId = posting.OrganizationId, TargetCode = t.TargetCode });
    }

    /// <summary>Inserts the new posting with its Pending target rows. Null when saved; else the row that won a concurrent
    /// insert of the same key (UX_FinancialPostings_Org_Key).</summary>
    private async Task<FinancialPosting?> TryCreateAsync(FinancialPosting posting, IReadOnlyList<IFinancialPostingTarget> active)
    {
        AddTargetRows(posting, active);
        _db.FinancialPostings.Add(posting);
        try
        {
            await _db.SaveChangesAsync(CancellationToken.None);
            return null;
        }
        catch (DbUpdateException) when (_db.Database.IsRelational())
        {
            _db.Entry(posting).State = EntityState.Detached;
            foreach (var s in posting.Targets) _db.Entry(s).State = EntityState.Detached;
            return await LoadAsync(posting.OrganizationId, posting.IdempotencyKey)
                   ?? throw new InvalidOperationException("The posting could not be saved.");
        }
    }

    private PostingResult Summarize(FinancialPosting posting, Guid sourceId, FinDocType type, List<TargetResult> dispatched)
    {
        // Targets not dispatched in this call keep their stored answer.
        var all = dispatched.Concat(posting.Targets.Where(s => dispatched.All(d => d.TargetCode != s.TargetCode)).Select(ToResult)).ToList();
        var failed = dispatched.Any(r => r.Status == TargetPostingStatus.Failed);
        return new PostingResult
        {
            Status = failed ? PostingStatus.PartialFailure : PostingStatus.Success,
            Message = failed ? string.Join(" ", dispatched.Where(r => r.Status == TargetPostingStatus.Failed).Select(r => $"{r.TargetCode}: {r.Error}")) : null,
            SourceDocumentId = sourceId,
            DocumentType = type,
            TargetResults = all,
        };
    }

    private static PostingResult Duplicate(FinancialPosting posting, Guid sourceId, FinDocType type) => new()
    {
        Status = PostingStatus.DuplicateSkipped,
        Message = "Already posted with the same content.",
        SourceDocumentId = sourceId,
        DocumentType = type,
        TargetResults = posting.Targets.Select(ToResult).ToList(),
    };

    /// <summary>A stored target row as a Shared result (Pending → Queued: it will be retried; DeadLetter → Failed).</summary>
    internal static TargetResult ToResult(FinancialPostingTargetState s) => new()
    {
        TargetCode = s.TargetCode,
        Status = s.Status switch
        {
            PostingTargetStatuses.Posted => TargetPostingStatus.Posted,
            PostingTargetStatuses.Queued or PostingTargetStatuses.Pending => TargetPostingStatus.Queued,
            PostingTargetStatuses.Held => TargetPostingStatus.Held,
            PostingTargetStatuses.Skipped => TargetPostingStatus.Skipped,
            _ => TargetPostingStatus.Failed,
        },
        InternalId = s.InternalId,
        ExternalId = s.ExternalId,
        Error = s.LastError,
    };

    private static PostingResult NoTarget(Guid sourceId, FinDocType type) =>
        Result(PostingStatus.NoTargetConfigured, sourceId, type, "No accounting target is active for this organization.");

    private static PostingResult Result(PostingStatus status, Guid sourceId, FinDocType type, string? message) =>
        new() { Status = status, SourceDocumentId = sourceId, DocumentType = type, Message = message };

    internal static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

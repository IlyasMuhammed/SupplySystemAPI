using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Common;
using SMS.Shared.Finance.Posting;

namespace SMS.Modules.Accounting.Posting;

/// <summary>
/// A44 D-8 / D-9 / API-CONTRACT §3 — the internal general ledger as a posting target (<c>internal_gl</c>).
/// <para>
/// Active when FEATURE_GENERAL_LEDGER is on, <c>InternalAccountingEnabled</c> and the A42 setup is Complete (D-10). A post
/// resolves each line's account (explicit leaf code, else the A42 default of its role), converts document currency to base
/// (× ExchangeRate, 2 dp; the rounding difference to DEFAULT_ROUNDING), needs an Open fiscal period for the date
/// (<see cref="IFiscalPeriodGuard"/>; closed → Failed "PERIOD_CLOSED", retried by the gateway), and writes one journal
/// entry (EntryType = FinDocType upper snake, Reference = document number, SourceModule / SourceDocumentUuid /
/// IdempotencyKey) — Posted, or Draft when AutoPostToGL is off. The journal and the posting's internal_gl row are saved
/// in one transaction (T-22).
/// </para>
/// <para>
/// Update (same key, changed payload, D-13) = a reversing journal of the current one + the new journal (a Draft is just
/// replaced). Void / Reverse = a reversing journal under the void key; never posted → Skipped "NOT_POSTED" (T-60).
/// </para>
/// <para>
/// A47 C11 (D-19, A-14) — every journal is written by the journal engine (<see cref="JournalEntryWriter.CreateAsync"/>, lines
/// already in base: <c>AmountsInBase</c>): the journal type by D-12 (<see cref="JournalTypeResolver"/>, CASH when a line is on
/// DEFAULT_CASH, else BANK), the per-type number from the sequence service (e.g. <c>SALE/2026/0001</c>), the D-11 period, the
/// balance move. The document's partner goes on its receivable/payable lines (D-GW-3; D-13: the journal type's
/// RequiresPartner is not enforced). A reversal (update or void) marks the original <c>Reversed</c> → <c>ReversedByEntryId</c>
/// and auto-reconciles original and reversal lines per account like a manual reversal (D-4, BR-RV-008). An engine refusal is a
/// Failed result with the A44 code (PERIOD_CLOSED, ACCOUNT_NOT_FOUND, EXCHANGE_RATE_MISSING, else JOURNAL_INVALID; task 411).
/// </para>
/// </summary>
internal sealed class InternalGlPostingTarget : IFinancialPostingTarget
{
    internal const string NotReady = "GL_NOT_READY", Disabled = "GL_DISABLED", NoLines = "NO_LINES", NotPosted = "NOT_POSTED",
        DraftDiscarded = "DRAFT_DISCARDED", PeriodClosed = "PERIOD_CLOSED", UnmappedRole = "UNMAPPED_ROLE",
        AccountNotFound = "ACCOUNT_NOT_FOUND", RateMissing = "EXCHANGE_RATE_MISSING", Voided = "VOIDED",
        /// <summary>A47 task 411 — any other journal-engine refusal (balance, lines, journal type …).</summary>
        JournalInvalid = "JOURNAL_INVALID";

    private readonly AccountingDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly GlReadiness _readiness;
    private readonly IFiscalPeriodGuard _periods;
    private readonly JournalEntryWriter _journals;
    private readonly JournalTypeResolver _types;
    private readonly JournalReconciler _reconciler;
    private readonly TimeProvider _clock;

    /// <summary>A47 C11 — journals through <see cref="JournalEntryWriter"/>, types through <see cref="JournalTypeResolver"/>,
    /// the reversal's auto-reconciliation through <see cref="JournalReconciler"/>.</summary>
    public InternalGlPostingTarget(AccountingDbContext db, ITenantContext tenant, GlReadiness readiness, IFiscalPeriodGuard periods,
        JournalEntryWriter journals, JournalTypeResolver types, JournalReconciler reconciler, TimeProvider? clock = null)
    {
        _db = db;
        _tenant = tenant;
        _readiness = readiness;
        _periods = periods;
        _journals = journals;
        _types = types;
        _reconciler = reconciler;
        _clock = clock ?? TimeProvider.System;
    }

    public string TargetCode => PostingTargetCodes.InternalGl;

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public async Task<bool> IsActiveAsync(CancellationToken ct = default)
    {
        var org = _tenant.OrganizationId;
        if (org == Guid.Empty || !(await _readiness.GetAsync(org, ct)).Ready) return false;
        return (await _readiness.SettingsAsync(org, ct)).InternalAccountingEnabled;
    }

    // ── post / update ──────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<TargetResult> PostAsync(FinancialTransaction tx, CancellationToken ct = default)
    {
        var org = _tenant.OrganizationId;
        if (await NotReadyAsync(org, ct) is { } skip) return skip;
        var (_, autoPost) = await _readiness.SettingsAsync(org, ct);
        var key = tx.IdempotencyKey;

        // REV-2 — a document voided before it reached the GL is never posted afterwards.
        if (await _db.FinancialPostings.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(p => p.OrganizationId == org && p.IdempotencyKey == key + ":void", ct))
            return TargetResult.Skipped(TargetCode, $"{Voided}: the document was voided before it reached the general ledger.");

        var fingerprint = PostingSerializer.Snapshot(tx).Fingerprint;
        var lines = tx.Lines.Where(l => l.Debit != 0 || l.Credit != 0).ToList();
        var date = DateOnly.FromDateTime(tx.TransactionDate);
        var entryType = FinancialPostingGateway.Truncate(JournalEntryTypes.For(tx.DocumentType.ToString()), 30);
        if (lines.Count == 0 && await CurrentJournalAsync(org, key, ct) is null)
            return TargetResult.Skipped(TargetCode, $"{NoLines}: the document has no amounts to post.");
        if (!await _periods.IsPostingAllowedAsync(org, date, entryType == JournalEntryTypes.OpeningBalance, ct))
            return TargetResult.Failed(TargetCode, PeriodClosedText(date));

        var baseCurrency = await _readiness.ReportingCurrencyAsync(org, date, ct);
        if (tx.ExchangeRate is null && baseCurrency is not null && !string.IsNullOrWhiteSpace(tx.CurrencyCode)
            && !string.Equals(tx.CurrencyCode.Trim(), baseCurrency, StringComparison.OrdinalIgnoreCase))
            return TargetResult.Failed(TargetCode, $"{RateMissing}: {tx.CurrencyCode} is not the base currency {baseCurrency} and no exchange rate was given.");

        // Accounts (D-5): explicit leaf code wins, else the role's default.
        var built = lines.Count == 0 ? null : await BuildLinesAsync(org, tx, lines, baseCurrency, ct);
        if (built is { Error: { } error }) return TargetResult.Failed(TargetCode, error);
        if (built is { Lines.Count: 0 }) built = null; // every amount rounded to 0.00 in base

        // A47 — outside the posting transaction: the system journal types (short seeding lock) and the D-12 type.
        await _journals.EnsureJournalTypesAsync(org, ct);
        var typeId = built is null ? 0 : (await _types.ResolveAsync(org, entryType, built.Lines.Select(l => l.Account.Id), ct)).Id;

        TargetResult answer = TargetResult.Skipped(TargetCode, $"{NoLines}: the document has no amounts to post.");
        await EngineAsync(date, () => _db.InTransactionAsync(async () =>
        {
            // REV-5 — one writer per document: the current journal and the target row are read again under the lock.
            await LockAsync(org, key, ct);
            // REV-13 — checked again under the lock: a void recorded meanwhile must win.
            if (await _db.FinancialPostings.IgnoreQueryFilters().AsNoTracking()
                    .AnyAsync(p => p.OrganizationId == org && p.IdempotencyKey == key + ":void", ct))
            {
                answer = TargetResult.Skipped(TargetCode, $"{Voided}: the document was voided before it reached the general ledger.");
                return;
            }
            var state = await TargetRowAsync(org, key, ct);
            var current = await CurrentJournalAsync(org, key, ct);
            if (current is not null && state is { Status: PostingTargetStatuses.Posted } && state.PostedFingerprint == fingerprint)
            {
                answer = Posted(current.EntryNumber); // already posted with this exact payload
                return;
            }
            if (built is null && current is null) return;

            if (current is not null)
            {
                if (current.Status == JournalStatuses.Draft) await DiscardDraftAsync(org, current.Id, ct);
                else await ReverseJournalAsync(org, current.Id, key, date, "document changed", ct);
            }
            string? number = null;
            if (built is not null)
            {
                var written = await _journals.CreateAsync(NewDraft(org, tx, typeId, entryType, date, key, built.Lines), autoPost, ct);
                number = written.Entry.EntryNumber;
            }
            MarkPosted(state, number, fingerprint);
            await _db.SaveChangesAsync(ct);
            answer = number is null
                ? TargetResult.Skipped(TargetCode, $"{NoLines}: the document has no amounts any more; the previous journal was reversed.")
                : Posted(number);
        }, ct));
        return answer;
    }

    // ── void / reverse ─────────────────────────────────────────────────────────────────────────────────────────────

    public Task<TargetResult> VoidAsync(VoidRequest request, CancellationToken ct = default) => ReverseCoreAsync(request, ct);

    /// <summary>D-26 — the GL side of a supplier reversal is the same reversing journal as a void.</summary>
    public Task<TargetResult> ReverseAsync(VoidRequest request, CancellationToken ct = default) => ReverseCoreAsync(request, ct);

    private async Task<TargetResult> ReverseCoreAsync(VoidRequest request, CancellationToken ct)
    {
        var org = _tenant.OrganizationId;
        if (await NotReadyAsync(org, ct) is { } skip) return skip;

        var voidKey = request.IdempotencyKey;
        var originalKey = FinancialPostingGateway.OriginalKey(request);
        var date = DateOnly.FromDateTime(request.VoidDate ?? Now);
        var periodOpen = await _periods.IsPostingAllowedAsync(org, date, ct);

        TargetResult answer = TargetResult.Skipped(TargetCode, $"{NotPosted}: the document was never posted to the general ledger.");
        await _journals.EnsureJournalTypesAsync(org, ct);
        await EngineAsync(date, () => _db.InTransactionAsync(async () =>
        {
            await LockAsync(org, originalKey, ct); // the same lock as the document's post (REV-5)
            var done = await _db.JournalEntries.IgnoreQueryFilters().AsNoTracking()
                .Where(j => j.OrganizationId == org && j.IdempotencyKey == voidKey && j.ReversesEntryId != null)
                .Select(j => j.EntryNumber).FirstOrDefaultAsync(ct);
            if (done is not null) { answer = Posted(done); return; }

            var current = await CurrentJournalAsync(org, originalKey, ct);
            if (current is null) return; // T-60 — never posted: nothing to reverse
            var state = await TargetRowAsync(org, voidKey, ct);
            if (current.Status == JournalStatuses.Draft)
            {
                await DiscardDraftAsync(org, current.Id, ct);
                MarkPosted(state, null, null);
                await _db.SaveChangesAsync(ct);
                answer = TargetResult.Skipped(TargetCode, $"{DraftDiscarded}: the draft journal {current.EntryNumber} was deleted.");
                return;
            }
            if (!periodOpen && !(current.IsOpeningEntry && await _periods.IsPostingAllowedAsync(org, date, true, ct)))
            {
                answer = TargetResult.Failed(TargetCode, PeriodClosedText(date));
                return;
            }
            var reason = string.IsNullOrWhiteSpace(request.Reason) ? "voided" : request.Reason.Trim();
            var reversal = await ReverseJournalAsync(org, current.Id, voidKey, date, reason, ct);
            MarkPosted(state, reversal.EntryNumber, null);
            await _db.SaveChangesAsync(ct);
            answer = Posted(reversal.EntryNumber);
        }, ct));
        return answer;
    }

    // ── journal building ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One base-currency line of the journal (A44 D-5) with what the engine stores next to it (A47 D-3).</summary>
    internal sealed record GlLine(ChartOfAccount Account, decimal Debit, decimal Credit, string? Memo, decimal AmountCurrency, string? CurrencyCode,
        decimal? ExchangeRate, Guid? PartnerUuid, string? PartnerType, string? PartnerName, string? TaxCode, decimal? TaxAmount, string? CostCenter,
        string? Project);

    private sealed record BuiltLines(List<GlLine> Lines, string? Error);

    private async Task<BuiltLines> BuildLinesAsync(Guid org, FinancialTransaction tx, List<FinancialTransactionLine> lines, string? baseCurrency,
        CancellationToken ct)
    {
        var defaults = await _db.AccountingDefaults.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.OrganizationId == org && d.AccountId != null)
            .Select(d => new { d.DefaultKey, AccountId = d.AccountId!.Value })
            .ToListAsync(ct);
        var byRole = defaults.ToDictionary(d => d.DefaultKey, d => d.AccountId, StringComparer.Ordinal);
        var codes = lines.Where(l => !string.IsNullOrWhiteSpace(l.AccountCode)).Select(l => l.AccountCode!.Trim()).Distinct().ToList();
        var ids = byRole.Values.Distinct().ToList();
        var accounts = await _db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.OrganizationId == org && a.IsActive && !a.IsGroup && (ids.Contains(a.Id) || codes.Contains(a.AccountCode)))
            .ToListAsync(ct);
        var byId = accounts.ToDictionary(a => a.Id);
        var byCode = accounts.GroupBy(a => a.AccountCode, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        ChartOfAccount? ForRole(string role) => byRole.TryGetValue(role, out var id) ? byId.GetValueOrDefault(id) : null;

        var foreign = tx.ExchangeRate is not null && baseCurrency is not null && !string.IsNullOrWhiteSpace(tx.CurrencyCode)
                      && !string.Equals(tx.CurrencyCode.Trim(), baseCurrency, StringComparison.OrdinalIgnoreCase);
        var result = new List<GlLine>();
        var problems = new List<string>();
        foreach (var line in lines.OrderBy(l => l.LineNumber))
        {
            ChartOfAccount? account;
            if (!string.IsNullOrWhiteSpace(line.AccountCode))
            {
                account = byCode.GetValueOrDefault(line.AccountCode.Trim());
                if (account is null) { problems.Add($"{AccountNotFound}: account {line.AccountCode} is not an active leaf account."); continue; }
            }
            else
            {
                account = ForRole(line.AccountRole!);
                if (account is null) { problems.Add($"{UnmappedRole}: {line.AccountRole} has no active leaf account assigned."); continue; }
            }
            var debit = ToBase(line.Debit, tx.ExchangeRate);
            var credit = ToBase(line.Credit, tx.ExchangeRate);
            // A document amount that rounds to 0.00 in base carries no line (the engine refuses empty lines); the
            // difference it leaves goes to the rounding line below.
            if (debit == 0 && credit == 0) continue;
            // Two-sided after rounding (a line with both a debit and a credit): the net on one side (CK_OneSide).
            if (debit > 0 && credit > 0) (debit, credit) = debit >= credit ? (debit - credit, 0m) : (0m, credit - debit);
            if (debit == 0 && credit == 0) continue;
            var (partner, partnerType) = PartnerOf(tx, line, account);
            result.Add(new GlLine(account, debit, credit, line.Label ?? tx.Description, line.Debit - line.Credit, null,
                foreign ? tx.ExchangeRate : null, partner, partnerType, partner is null ? null : tx.PartnerName,
                line.TaxCode, line.TaxAmount, line.CostCenter, line.Project));
        }
        if (problems.Count > 0) return new BuiltLines(result, string.Join(" ", problems.Distinct()));

        // D-5 — the base-currency rounding difference goes to DEFAULT_ROUNDING.
        var difference = result.Sum(l => l.Debit) - result.Sum(l => l.Credit);
        if (difference != 0)
        {
            var rounding = ForRole(DefaultKeys.Rounding);
            if (rounding is null)
                return new BuiltLines(result, $"{UnmappedRole}: {DefaultKeys.Rounding} has no active leaf account assigned (rounding difference {difference:0.####}).");
            result.Add(difference > 0
                ? new GlLine(rounding, 0m, difference, "Rounding difference", -difference, baseCurrency, null, null, null, null, null, null, null, null)
                : new GlLine(rounding, -difference, 0m, "Rounding difference", -difference, baseCurrency, null, null, null, null, null, null, null, null));
        }
        return new BuiltLines(result, null);
    }

    /// <summary>
    /// D-GW-3 (D-13) — the partner of a journal line: the line's own <c>PartnerId</c> when the source sends one, else the
    /// document's partner on a receivable / payable account line. The type is the document's, else the account's side
    /// (receivable → Customer, payable → Vendor). No type known → no partner (never a refusal).
    /// </summary>
    internal static (Guid? Partner, string? Type) PartnerOf(FinancialTransaction tx, FinancialTransactionLine line, ChartOfAccount account)
    {
        var onPartnerAccount = account.AccountType is AccountTypes.Receivable or AccountTypes.Payable;
        var partner = line.PartnerId is { } own && own != Guid.Empty ? own
            : onPartnerAccount && tx.PartnerId is { } doc && doc != Guid.Empty ? doc
            : (Guid?)null;
        if (partner is null) return (null, null);
        var type = tx.PartnerType switch
        {
            Shared.Finance.Posting.PartnerType.Customer => JournalPartnerTypes.Customer,
            Shared.Finance.Posting.PartnerType.Vendor   => JournalPartnerTypes.Vendor,
            _ => account.AccountType switch
            {
                AccountTypes.Receivable => JournalPartnerTypes.Customer,
                AccountTypes.Payable    => JournalPartnerTypes.Vendor,
                _                       => null,
            },
        };
        return type is null ? (null, null) : (partner, type);
    }

    /// <summary>1 document-currency unit = rate × base; converted amounts are rounded to 2 dp. No rate: the amount as given.</summary>
    internal static decimal ToBase(decimal amount, decimal? rate) =>
        rate is { } r ? Math.Round(amount * r, 2, MidpointRounding.AwayFromZero) : amount;

    /// <summary>The engine request of a document's journal (A47 C11): base amounts, the D-12 type, the A44 key.</summary>
    private static JournalDraft NewDraft(Guid org, FinancialTransaction tx, int typeId, string entryType, DateOnly date, string key, List<GlLine> lines) => new()
    {
        OrganizationId     = org,
        JournalTypeId      = typeId,
        EntryDate          = date,
        DocumentDate       = date,
        Reference          = string.IsNullOrWhiteSpace(tx.SourceDocumentNumber) ? null : FinancialPostingGateway.Truncate(tx.SourceDocumentNumber, 200),
        Description        = FinancialPostingGateway.Truncate(tx.Description ?? $"{tx.DocumentType} {tx.SourceDocumentNumber}", 500),
        EntryType          = entryType,
        SourceModule       = string.IsNullOrWhiteSpace(tx.SourceModule) ? null : FinancialPostingGateway.Truncate(tx.SourceModule, 30),
        SourceDocumentUuid = tx.SourceDocumentId,
        IdempotencyKey     = FinancialPostingGateway.Truncate(key, 200),
        CurrencyCode       = string.IsNullOrWhiteSpace(tx.CurrencyCode) ? null : tx.CurrencyCode.Trim().ToUpperInvariant(),
        ExchangeRate       = tx.ExchangeRate,
        AmountsInBase      = true, // A44 converted and balanced them (rounding line)
        IsManual           = false, // D-13 — no RequiresPartner, no partner lookup
        Lines = lines.Select(l => new JournalDraftLine
        {
            AccountId      = l.Account.Id,
            Debit          = l.Debit,
            Credit         = l.Credit,
            AmountCurrency = l.AmountCurrency,
            CurrencyCode   = l.CurrencyCode,
            ExchangeRate   = l.ExchangeRate,
            PartnerUuid    = l.PartnerUuid,
            PartnerType    = l.PartnerType,
            PartnerName    = l.PartnerName,
            Description    = l.Memo is null ? null : FinancialPostingGateway.Truncate(l.Memo, 500),
            TaxCode        = l.TaxCode,
            TaxAmount      = l.TaxAmount,
            CostCenter     = l.CostCenter,
            Project        = l.Project,
        }).ToList(),
    };

    /// <summary>
    /// A Posted mirror of the journal <paramref name="originalId"/> (every line's sides swapped), dated <paramref name="date"/>,
    /// under the A44 key <paramref name="key"/> (the document's key for an update, <c>key:void</c> for a void), written by the
    /// engine (RR's mirror). D-4: the original becomes Reversed → the reversal; BR-RV-008: per account the original's lines and
    /// their mirrors are reconciled (Full, cleared by the reversal). On SQL Server under RR's locks (entry, then accounts) so a
    /// concurrent manual reversal or reconcile of the same journal waits. Not saved.
    /// </summary>
    private async Task<JournalEntry> ReverseJournalAsync(Guid org, int originalId, string key, DateOnly date, string reason, CancellationToken ct)
    {
        await _reconciler.LockAsync($"glrev:{org:N}:{originalId}", ct);
        var accountIds = await _db.JournalEntryLines.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.JournalEntryId == originalId).Select(l => l.AccountId).Distinct().ToListAsync(ct);
        await _reconciler.LockAccountsAsync(org, accountIds, ct);

        var original = await _journals.GetEntityAsync(org, originalId, ct)
                       ?? throw new InvalidOperationException($"Journal {originalId} disappeared.");
        if (original.ReversedByEntryId is { } by // reversed by hand meanwhile (RR): that (posted) reversal stands
            && await _db.JournalEntries.IgnoreQueryFilters().FirstOrDefaultAsync(j => j.Id == by && j.Status == JournalStatuses.Posted, ct) is { } existing)
            return existing;

        var lines = original.Lines.Where(l => l.DebitAmount != 0 || l.CreditAmount != 0).OrderBy(l => l.LineNumber).ToList();
        reason = reason.Length <= JournalReversalService.MaxReasonLength ? reason : reason[..JournalReversalService.MaxReasonLength];
        var typeId = await JournalReversalService.ActiveTypeOrMiscAsync(_db, org, original.JournalTypeId, ct);
        var draft = JournalReversalService.MirrorDraft(original, lines, date, reason, typeId, AccountingCaller.System(org));
        draft.IdempotencyKey = FinancialPostingGateway.Truncate(key, 200); // A44 D-13 — not reversal:{uuid}
        draft.Description = FinancialPostingGateway.Truncate(draft.Description ?? string.Empty, 500);
        draft.UserId = null;
        draft.UserName = null;
        var reversal = (await _journals.CreateAsync(draft, post: true, ct)).Entry;
        // REV-10 — only a Posted reversal marks the original Reversed and is auto-reconciled (a reversal is never a draft).
        if (reversal.Status != JournalStatuses.Posted)
            throw new InvalidOperationException($"The reversal of {original.EntryNumber} was not posted.");

        original.Status = JournalStatuses.Reversed;
        original.ReversedByEntry = reversal;
        original.ReversalReason = reason;
        original.ModifiedAt = Now;
        await _reconciler.ReconcileReversalAsync(org, original, lines, reversal, date, null, null, ct);
        return reversal;
    }

    private async Task DiscardDraftAsync(Guid org, int draftId, CancellationToken ct)
    {
        var tracked = await _journals.GetEntityAsync(org, draftId, ct) ?? throw new InvalidOperationException($"Journal {draftId} disappeared.");
        await _journals.DeleteDraftAsync(tracked, ct);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Task 411 — runs the engine work; a journal-engine refusal (it rolled the transaction back) becomes the A44 target
    /// failure text: period → PERIOD_CLOSED, account → ACCOUNT_NOT_FOUND, currency → EXCHANGE_RATE_MISSING, else
    /// JOURNAL_INVALID. Thrown as <see cref="GlPostingRefusedException"/> so the gateway clears what the target left in the
    /// change tracker (REV-4), records Failed with this text and schedules the retry — the other targets go on (TS-GW-005).
    /// </summary>
    private static async Task EngineAsync(DateOnly date, Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex) when (ex is IJournalCodedError coded)
        {
            throw new GlPostingRefusedException(RefusalText(coded, date));
        }
    }

    internal static string RefusalText(IJournalCodedError error, DateOnly date)
    {
        var detail = string.Join("; ", error.Errors.Select(e => e.LineNumber is { } n ? JournalMessages.Line(n, e.Message) : e.Message).Distinct());
        var code = error.ErrorCode switch
        {
            JournalErrorCodes.NoPeriod or JournalErrorCodes.PeriodClosed or JournalErrorCodes.PeriodLocked or JournalErrorCodes.AdjustmentOnly => PeriodClosed,
            JournalErrorCodes.AccountNotPostable or JournalErrorCodes.AccountInactive => AccountNotFound,
            JournalErrorCodes.ExchangeRateRequired => RateMissing,
            _ => JournalInvalid,
        };
        return code == PeriodClosed
            ? $"{PeriodClosedText(date)} ({error.ErrorCode} {detail})"
            : $"{code}: {error.ErrorCode} {detail}";
    }

    private static string PeriodClosedText(DateOnly date) => $"{PeriodClosed}: {date:yyyy-MM-dd} is not in an open fiscal period.";

    /// <summary>REV-5 — an exclusive application lock on the document for the rest of the transaction (SQL Server only).</summary>
    private async Task LockAsync(Guid org, string key, CancellationToken ct)
    {
        if (!_db.Database.IsRelational()) return;
        var resource = FinancialPostingGateway.Truncate($"finpost:{org:N}:{key}", 255);
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"DECLARE @r int; EXEC @r = sp_getapplock @Resource = {resource}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 30000; IF @r < 0 THROW 51000, 'The posting lock could not be taken.', 1;", ct);
    }

    /// <summary>The latest journal of <paramref name="key"/> that is not a reversal and has not been reversed.</summary>
    private async Task<JournalEntry?> CurrentJournalAsync(Guid org, string key, CancellationToken ct)
    {
        var journals = _db.JournalEntries.IgnoreQueryFilters();
        return await journals.AsNoTracking()
            .Where(j => j.OrganizationId == org && j.IdempotencyKey == key && j.ReversesEntryId == null && j.ReversedByEntryId == null
                        && j.Status != JournalStatuses.Reversed
                        && !journals.Any(r => r.OrganizationId == org && r.ReversesEntryId == j.Id && r.Status != JournalStatuses.Draft)) // REV-10
            .OrderByDescending(j => j.Id)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>The posting's internal_gl row (tracked — the gateway's instance), so it is saved with the journal (T-22).</summary>
    private Task<FinancialPostingTargetState?> TargetRowAsync(Guid org, string key, CancellationToken ct) =>
        _db.FinancialPostingTargets.IgnoreQueryFilters().Include(t => t.Posting)
            .FirstOrDefaultAsync(t => t.OrganizationId == org && t.TargetCode == PostingTargetCodes.InternalGl && t.Posting.IdempotencyKey == key, ct);

    private void MarkPosted(FinancialPostingTargetState? state, string? number, string? fingerprint)
    {
        if (state is null) return;
        state.Status = number is null ? PostingTargetStatuses.Skipped : PostingTargetStatuses.Posted;
        state.InternalId = number ?? state.InternalId;
        state.PostedFingerprint = fingerprint ?? state.Posting.Fingerprint;
        state.Attempts = 0;
        state.NextAttemptAt = null;
        state.LastError = null;
        state.LastAttemptAt = Now;
        if (number is not null) state.PostedAt = Now;
    }

    private async Task<TargetResult?> NotReadyAsync(Guid org, CancellationToken ct)
    {
        var ready = await _readiness.GetAsync(org, ct);
        if (!ready.Ready) return TargetResult.Skipped(TargetCode, $"{NotReady}: {ready.NotReadyReason}.");
        if (!(await _readiness.SettingsAsync(org, ct)).InternalAccountingEnabled)
            return TargetResult.Skipped(TargetCode, $"{Disabled}: internal accounting is switched off.");
        return null;
    }

    private TargetResult Posted(string number) => new() { TargetCode = TargetCode, Status = TargetPostingStatus.Posted, InternalId = number };
}

/// <summary>
/// A47 task 411 — the journal engine refused a gateway journal; the message is the A44 target failure text (e.g.
/// "PERIOD_CLOSED: 2026-10-15 is not in an open fiscal period. (JE-007 Period is locked)"). The gateway records it as Failed and retries.
/// </summary>
internal sealed class GlPostingRefusedException(string message) : InvalidOperationException(message);

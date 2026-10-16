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
/// IdempotencyKey) — Posted, or Draft when AutoPostToGL is off. Posted journals move the accounts' CurrentBalance (normal
/// direction) and set HasTransactions, like the A42 opening post. The journal and the posting's internal_gl row are saved
/// in one transaction (T-22).
/// </para>
/// <para>
/// Update (same key, changed payload, D-13) = a reversing journal of the current one + the new journal (a Draft is just
/// replaced). Void / Reverse = a reversing journal under the void key; never posted → Skipped "NOT_POSTED" (T-60).
/// </para>
/// </summary>
internal sealed class InternalGlPostingTarget : IFinancialPostingTarget
{
    internal const string NotReady = "GL_NOT_READY", Disabled = "GL_DISABLED", NoLines = "NO_LINES", NotPosted = "NOT_POSTED",
        DraftDiscarded = "DRAFT_DISCARDED", PeriodClosed = "PERIOD_CLOSED", UnmappedRole = "UNMAPPED_ROLE",
        AccountNotFound = "ACCOUNT_NOT_FOUND", RateMissing = "EXCHANGE_RATE_MISSING", Voided = "VOIDED";

    private readonly AccountingDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly GlReadiness _readiness;
    private readonly IFiscalPeriodGuard _periods;
    private readonly JournalNumbers _numbers;
    private readonly TimeProvider _clock;

    public InternalGlPostingTarget(AccountingDbContext db, ITenantContext tenant, GlReadiness readiness, IFiscalPeriodGuard periods,
        JournalNumbers numbers, TimeProvider? clock = null)
    {
        _db = db;
        _tenant = tenant;
        _readiness = readiness;
        _periods = periods;
        _numbers = numbers;
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
        if (lines.Count == 0 && await CurrentJournalAsync(org, key, ct) is null)
            return TargetResult.Skipped(TargetCode, $"{NoLines}: the document has no amounts to post.");
        if (!await _periods.IsPostingAllowedAsync(org, date, ct))
            return TargetResult.Failed(TargetCode, $"{PeriodClosed}: {date:yyyy-MM-dd} is not in an open fiscal period.");

        var baseCurrency = await _readiness.ReportingCurrencyAsync(org, date, ct);
        if (tx.ExchangeRate is null && baseCurrency is not null && !string.IsNullOrWhiteSpace(tx.CurrencyCode)
            && !string.Equals(tx.CurrencyCode.Trim(), baseCurrency, StringComparison.OrdinalIgnoreCase))
            return TargetResult.Failed(TargetCode, $"{RateMissing}: {tx.CurrencyCode} is not the base currency {baseCurrency} and no exchange rate was given.");

        // Accounts (D-5): explicit leaf code wins, else the role's default.
        var built = lines.Count == 0 ? null : await BuildLinesAsync(org, tx, lines, ct);
        if (built is { Error: { } error }) return TargetResult.Failed(TargetCode, error);

        TargetResult answer = TargetResult.Skipped(TargetCode, $"{NoLines}: the document has no amounts to post.");
        await _db.InTransactionAsync(async () =>
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
                if (current.Status == JournalStatuses.Draft) await DiscardDraftAsync(current, ct);
                else await ReverseJournalAsync(org, current, key, date, $"Reversal of {current.EntryNumber}: document changed", ct);
            }
            string? number = null;
            if (built is not null)
            {
                var journal = NewJournal(org, tx.DocumentType.ToString(), date, tx.SourceDocumentNumber,
                    tx.Description ?? $"{tx.DocumentType} {tx.SourceDocumentNumber}", baseCurrency, autoPost, tx.SourceModule,
                    tx.SourceDocumentId, key);
                journal.EntryNumber = await _numbers.NextAsync(org, Now, ct);
                var n = 0;
                foreach (var (account, debit, credit, memo) in built.Lines)
                    journal.Lines.Add(new JournalEntryLine
                    {
                        OrganizationId = org, LineNumber = ++n, AccountId = account.Id, DebitAmount = debit, CreditAmount = credit,
                        Memo = memo is null ? null : FinancialPostingGateway.Truncate(memo, 500),
                    });
                journal.TotalDebit = built.Lines.Sum(l => l.Debit);
                journal.TotalCredit = built.Lines.Sum(l => l.Credit);
                _db.JournalEntries.Add(journal);
                number = journal.EntryNumber;
                // A Draft moves no balance but still marks the accounts as used (they cannot be deleted under it).
                await MoveBalancesAsync(built.Lines.Select(l => (l.Account.Id, l.Account.RootType, l.Debit, l.Credit)), autoPost, ct);
            }
            MarkPosted(state, number, fingerprint);
            await _db.SaveChangesAsync(ct);
            answer = number is null
                ? TargetResult.Skipped(TargetCode, $"{NoLines}: the document has no amounts any more; the previous journal was reversed.")
                : Posted(number);
        }, ct);
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
        await _db.InTransactionAsync(async () =>
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
                await DiscardDraftAsync(current, ct);
                MarkPosted(state, null, null);
                await _db.SaveChangesAsync(ct);
                answer = TargetResult.Skipped(TargetCode, $"{DraftDiscarded}: the draft journal {current.EntryNumber} was deleted.");
                return;
            }
            if (!periodOpen)
            {
                answer = TargetResult.Failed(TargetCode, $"{PeriodClosed}: {date:yyyy-MM-dd} is not in an open fiscal period.");
                return;
            }
            var reason = string.IsNullOrWhiteSpace(request.Reason) ? "voided" : request.Reason.Trim();
            var number = await ReverseJournalAsync(org, current, voidKey, date, $"Reversal of {current.EntryNumber}: {reason}", ct);
            MarkPosted(state, number, null);
            await _db.SaveChangesAsync(ct);
            answer = Posted(number);
        }, ct);
        return answer;
    }

    // ── journal building ───────────────────────────────────────────────────────────────────────────────────────────

    private sealed record BuiltLines(List<(ChartOfAccount Account, decimal Debit, decimal Credit, string? Memo)> Lines, string? Error);

    private async Task<BuiltLines> BuildLinesAsync(Guid org, FinancialTransaction tx, List<FinancialTransactionLine> lines, CancellationToken ct)
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

        var result = new List<(ChartOfAccount, decimal, decimal, string?)>();
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
            result.Add((account, ToBase(line.Debit, tx.ExchangeRate), ToBase(line.Credit, tx.ExchangeRate), line.Label ?? tx.Description));
        }
        if (problems.Count > 0) return new BuiltLines(result, string.Join(" ", problems.Distinct()));

        // D-5 — the base-currency rounding difference goes to DEFAULT_ROUNDING.
        var difference = result.Sum(l => l.Item2) - result.Sum(l => l.Item3);
        if (difference != 0)
        {
            var rounding = ForRole(DefaultKeys.Rounding);
            if (rounding is null)
                return new BuiltLines(result, $"{UnmappedRole}: {DefaultKeys.Rounding} has no active leaf account assigned (rounding difference {difference:0.####}).");
            result.Add(difference > 0 ? (rounding, 0m, difference, "Rounding difference") : (rounding, -difference, 0m, "Rounding difference"));
        }
        return new BuiltLines(result, null);
    }

    /// <summary>1 document-currency unit = rate × base; converted amounts are rounded to 2 dp. No rate: the amount as given.</summary>
    internal static decimal ToBase(decimal amount, decimal? rate) =>
        rate is { } r ? Math.Round(amount * r, 2, MidpointRounding.AwayFromZero) : amount;

    private JournalEntry NewJournal(Guid org, string documentType, DateOnly date, string? reference, string description, string? currency,
        bool posted, string? module, Guid? sourceUuid, string key) => new()
    {
        OrganizationId     = org,
        EntryType          = FinancialPostingGateway.Truncate(JournalEntryTypes.For(documentType), 30),
        EntryDate          = date,
        Reference          = string.IsNullOrWhiteSpace(reference) ? null : FinancialPostingGateway.Truncate(reference, 200),
        Description        = FinancialPostingGateway.Truncate(description, 500),
        Status             = posted ? JournalStatuses.Posted : JournalStatuses.Draft,
        CurrencyCode       = currency,
        PostedAt           = posted ? Now : null,
        SourceModule       = module is null ? null : FinancialPostingGateway.Truncate(module, 30),
        SourceDocumentUuid = sourceUuid,
        IdempotencyKey     = FinancialPostingGateway.Truncate(key, 200),
    };

    /// <summary>A Posted journal with every line's sides swapped, dated <paramref name="date"/>, under <paramref name="key"/>.</summary>
    private async Task<string> ReverseJournalAsync(Guid org, JournalEntry original, string key, DateOnly date, string description, CancellationToken ct)
    {
        var lines = await _db.JournalEntryLines.IgnoreQueryFilters().Where(l => l.JournalEntryId == original.Id).OrderBy(l => l.LineNumber).ToListAsync(ct);
        var accountIds = lines.Select(l => l.AccountId).Distinct().ToList();
        var roots = await _db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking().Where(a => accountIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.RootType, ct);

        var reversal = NewJournal(org, original.EntryType, date, original.Reference, description, original.CurrencyCode, posted: true,
            original.SourceModule, original.SourceDocumentUuid, key);
        reversal.EntryType = original.EntryType;
        reversal.ReversesEntryId = original.Id;
        reversal.EntryNumber = await _numbers.NextAsync(org, Now, ct);
        foreach (var l in lines)
        {
            reversal.Lines.Add(new JournalEntryLine
            {
                OrganizationId = org, LineNumber = l.LineNumber, AccountId = l.AccountId, DebitAmount = l.CreditAmount, CreditAmount = l.DebitAmount,
                Memo = l.Memo,
            });
        }
        reversal.TotalDebit = original.TotalCredit;
        reversal.TotalCredit = original.TotalDebit;
        _db.JournalEntries.Add(reversal);
        await MoveBalancesAsync(lines.Select(l => (l.AccountId, roots[l.AccountId], l.CreditAmount, l.DebitAmount)), true, ct);
        // Saved here so that a following new journal gets the next number (the local numbering reads the table).
        await _db.SaveChangesAsync(ct);
        return reversal.EntryNumber;
    }

    private async Task DiscardDraftAsync(JournalEntry draft, CancellationToken ct)
    {
        var tracked = await _db.JournalEntries.IgnoreQueryFilters().Include(j => j.Lines).SingleAsync(j => j.Id == draft.Id, ct);
        _db.JournalEntries.Remove(tracked);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// A42 opening-post rule — the balance moves in the account's normal direction — applied atomically (REV-1): on SQL
    /// Server one <c>UPDATE … SET CurrentBalance = CurrentBalance + @delta</c> per account inside the posting transaction, so
    /// concurrent postings to the same account never lose an amount; tracked account entities are not touched. The
    /// in-memory test provider has no ExecuteUpdate: there the tracked row is changed.
    /// </summary>
    private async Task MoveBalancesAsync(IEnumerable<(int AccountId, string RootType, decimal Debit, decimal Credit)> moves, bool moveBalance,
        CancellationToken ct)
    {
        foreach (var g in moves.GroupBy(m => m.AccountId))
        {
            var root = g.First().RootType;
            var delta = moveBalance ? g.Sum(m => RootTypes.IsDebitNormal(root) ? m.Debit - m.Credit : m.Credit - m.Debit) : 0m;
            var id = g.Key;
            if (_db.Database.IsRelational())
            {
                await _db.ChartOfAccounts.IgnoreQueryFilters().Where(a => a.Id == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.CurrentBalance, a => a.CurrentBalance + delta)
                                              .SetProperty(a => a.HasTransactions, true), ct);
                continue;
            }
            var account = await _db.ChartOfAccounts.IgnoreQueryFilters().SingleAsync(a => a.Id == id, ct);
            account.CurrentBalance += delta;
            account.HasTransactions = true;
        }
    }

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
            .Where(j => j.OrganizationId == org && j.IdempotencyKey == key && j.ReversesEntryId == null
                        && !journals.Any(r => r.OrganizationId == org && r.ReversesEntryId == j.Id))
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

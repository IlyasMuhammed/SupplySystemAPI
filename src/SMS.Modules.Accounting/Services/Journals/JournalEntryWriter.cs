using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Accounting.Services;

/// <summary>
/// A47 C6 — what a writer wants posted: the engine's internal request (int ids, the organization explicit). Built by the
/// journal entry API (manual entries), the A44 internal GL target (C11, GW), the reversal (C7, RR) and the year-end close
/// (C10, RPT).
/// </summary>
internal sealed class JournalDraft
{
    public Guid      OrganizationId     { get; set; }
    public int       JournalTypeId      { get; set; }
    public DateOnly  EntryDate          { get; set; }
    public DateOnly? DocumentDate       { get; set; }
    public string?   Reference          { get; set; }
    public string?   Description        { get; set; }
    /// <summary>The source document kind (FinDocType upper snake, MANUAL_JOURNAL, YEAR_END_CLOSE …).</summary>
    public string    EntryType          { get; set; } = JournalEntryTypes.ManualJournal;
    public string?   SourceModule       { get; set; }
    public Guid?     SourceDocumentUuid { get; set; }
    /// <summary>Required. Keys with a <see cref="JournalKeys.UniquePrefixes"/> prefix are idempotent: a second create returns the first entry.</summary>
    public string    IdempotencyKey     { get; set; } = string.Empty;
    /// <summary>Null = the base (reporting) currency of the date's fiscal year.</summary>
    public string?   CurrencyCode       { get; set; }
    /// <summary>1 entry-currency unit = rate × base. Required (&gt; 0) when the currency is not the base currency.</summary>
    public decimal?  ExchangeRate       { get; set; }
    public bool      IsOpeningEntry     { get; set; }
    public bool      IsClosingEntry     { get; set; }
    /// <summary>C7 — the entry this one reverses (RR), with the reason.</summary>
    public int?      ReversesEntryId    { get; set; }
    public string?   ReversalReason     { get; set; }
    /// <summary>D-13 — a manual entry: the journal type's RequiresPartner applies and partners are looked up.</summary>
    public bool      IsManual           { get; set; }
    /// <summary>
    /// The lines' Debit/Credit are already base amounts (A44: converted at the document's rate, rounding line added); else
    /// they are in the line's currency and converted × rate, rounded to 2 dp (D-8).
    /// </summary>
    public bool      AmountsInBase      { get; set; }
    /// <summary>System writers only (the reversal): an account deactivated since the original was posted is still accepted.</summary>
    public bool      AllowInactiveAccounts { get; set; }
    /// <summary>
    /// System writers only (the year-end close when the year has no period 13: period 12 is Locked): the period is resolved
    /// for the FKs but its status is not checked. A closed fiscal year is still refused.
    /// </summary>
    public bool      SkipPeriodCheck    { get; set; }
    public int?      UserId             { get; set; }
    public string?   UserName           { get; set; }
    public List<JournalDraftLine> Lines { get; set; } = [];
}

internal sealed class JournalDraftLine
{
    public int      AccountId      { get; set; }
    public decimal  Debit          { get; set; }
    public decimal  Credit         { get; set; }
    /// <summary>Only with <see cref="JournalDraft.AmountsInBase"/>: the signed transaction-currency amount (null = Debit − Credit).</summary>
    public decimal? AmountCurrency { get; set; }
    /// <summary>Null = the entry's currency.</summary>
    public string?  CurrencyCode   { get; set; }
    /// <summary>Null = the entry's rate.</summary>
    public decimal? ExchangeRate   { get; set; }
    public Guid?    PartnerUuid    { get; set; }
    public string?  PartnerType    { get; set; }
    public string?  PartnerName    { get; set; }
    public string?  Description    { get; set; }
    public string?  CostCenter     { get; set; }
    public string?  Project        { get; set; }
    public string?  TaxCode        { get; set; }
    public decimal? TaxAmount      { get; set; }
}

/// <summary>The written (tracked, not yet saved) entry; <c>Created</c> false when a unique key returned an existing one.</summary>
internal sealed record JournalWriteResult(JournalEntry Entry, bool Created);

/// <summary>
/// A47 C6 — the journal engine: validates (§9.4 steps 2–7), resolves the period (D-11), numbers (D-7), writes and posts
/// journal entries, moving balances through <see cref="GlBalanceMover"/> (D-9). It never opens a transaction and (except
/// <see cref="UpdateDraftAsync"/>) never calls SaveChanges: the caller does both (so a journal, its sequence row and the caller's own rows commit together, and the
/// sequence lock is held until then). Rule violations throw <see cref="JournalValidationException"/> (400) /
/// <see cref="JournalConflictException"/> (409) with the JE-0xx codes. This is the extension point for C7 (reversal),
/// C10 (year-end close) and C11 (gateway delegation).
/// </summary>
internal sealed class JournalEntryWriter
{
    private static readonly Regex CurrencyPattern = new("^[A-Z]{3}$", RegexOptions.Compiled);

    private readonly AccountingDbContext _db;
    private readonly IJournalEntrySequenceService _sequences;
    private readonly JournalPeriodResolver _periods;
    private readonly GlBalanceMover _balances;
    private readonly JournalTypeResolver _types;
    private readonly IPartnerRoleLookup? _partners;
    private readonly TimeProvider _clock;

    public JournalEntryWriter(AccountingDbContext db, IJournalEntrySequenceService sequences, JournalPeriodResolver periods,
        GlBalanceMover balances, JournalTypeResolver types, IPartnerRoleLookup? partners = null, TimeProvider? clock = null)
    {
        _db = db;
        _sequences = sequences;
        _periods = periods;
        _balances = balances;
        _types = types;
        _partners = partners;
        _clock = clock ?? TimeProvider.System;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    // ── reads ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The entry (tracked, with its lines) — the gateway's accessor (spec §14.2 GetEntityByIdAsync).</summary>
    public Task<JournalEntry?> GetEntityAsync(Guid organizationId, int journalEntryId, CancellationToken ct = default) =>
        _db.JournalEntries.IgnoreQueryFilters().Include(j => j.Lines.OrderBy(l => l.LineNumber))
            .FirstOrDefaultAsync(j => j.OrganizationId == organizationId && j.Id == journalEntryId, ct);

    /// <summary>The entry with this idempotency key (tracked, with lines) — the newest when a key is shared (A44).</summary>
    public Task<JournalEntry?> FindByKeyAsync(Guid organizationId, string key, CancellationToken ct = default) =>
        _db.JournalEntries.IgnoreQueryFilters().Include(j => j.Lines.OrderBy(l => l.LineNumber))
            .Where(j => j.OrganizationId == organizationId && j.IdempotencyKey == key).OrderByDescending(j => j.Id).FirstOrDefaultAsync(ct);

    // ── create / post ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates and adds the entry — Posted when <paramref name="post"/> or the journal type auto-posts, else Draft — and
    /// moves the balances (a draft only marks its accounts used, A-11). Not saved. A unique key (manual:, reversal:,
    /// yearclose:) that exists returns that entry instead (BR-JE-007, TS-JE-008).
    /// </summary>
    public async Task<JournalWriteResult> CreateAsync(JournalDraft draft, bool post, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(draft.IdempotencyKey)) throw new ArgumentException("A journal needs an idempotency key.", nameof(draft));
        var org = draft.OrganizationId;
        if (JournalKeys.IsUnique(draft.IdempotencyKey) && await FindByKeyAsync(org, draft.IdempotencyKey, ct) is { } existing)
            return new JournalWriteResult(existing, false);

        var type = await LoadTypeAsync(org, draft.JournalTypeId, ct);
        await PeriodLockAsync(org, ct);
        var built = await BuildAsync(draft, type, ct);
        post |= type.AutoPost;

        var entry = new JournalEntry
        {
            OrganizationId     = org,
            JournalTypeId      = type.Id,
            EntryType          = Truncate(draft.EntryType, 30),
            EntryDate          = draft.EntryDate,
            DocumentDate       = draft.DocumentDate,
            Reference          = TrimTo(draft.Reference, 200),
            Description        = TrimTo(draft.Description, 500),
            SourceModule       = TrimTo(draft.SourceModule, 30),
            SourceDocumentUuid = draft.SourceDocumentUuid,
            IdempotencyKey     = Truncate(draft.IdempotencyKey, 200),
            CurrencyCode       = built.Currency,
            ExchangeRate       = built.Rate,
            FiscalYearId       = built.Period.FiscalYearId,
            FiscalPeriodId     = built.Period.FiscalPeriodId,
            IsOpeningEntry     = built.IsOpening,
            IsClosingEntry     = built.IsClosing,
            ReversesEntryId    = draft.ReversesEntryId,
            ReversalReason     = TrimTo(draft.ReversalReason, 200),
            Status             = JournalStatuses.Draft,
            CreatedAt          = Now,
            CreatedBy          = draft.UserId,
            CreatedByName      = TrimTo(draft.UserName, 200),
        };
        Fill(entry, built.Lines);
        entry.EntryNumber = await _sequences.NextEntryNumberAsync(org, type.Id, built.Period.FiscalYearId, draft.EntryDate, ct);
        _db.JournalEntries.Add(entry);

        if (post) MarkPosted(entry, draft.UserId, draft.UserName);
        await _balances.MoveLinesAsync(entry.Lines.ToList(), post, ct);
        return new JournalWriteResult(entry, true);
    }

    /// <summary>
    /// §9.5 — a Draft (tracked, lines loaded) becomes Posted: the period and the accounts are checked again (BR-JE-009,
    /// TS-JE-019), the balances move. On SQL Server the Draft → Posted switch is a conditional UPDATE inside the caller's
    /// transaction, so two concurrent posts move the balances once. Not saved.
    /// </summary>
    public async Task PostAsync(JournalEntry entry, int? userId, string? userName, CancellationToken ct = default)
    {
        EnsureDraft(entry, posting: true);
        var org = entry.OrganizationId;
        await PeriodLockAsync(org, ct);
        var period = await _periods.ResolveAsync(org, entry.EntryDate, entry.IsOpeningEntry, entry.IsClosingEntry, ct);
        var errors = new List<JournalError>();
        if (!period.Allowed) errors.Add(new JournalError(period.ErrorCode!, period.Message!));
        var lines = entry.Lines.OrderBy(l => l.LineNumber).ToList();
        errors.AddRange(await CheckAccountsAsync(org, lines.Select(l => (l.LineNumber, l.AccountId)), ct));
        if (errors.Count > 0) throw new JournalValidationException(errors);

        if (_db.Database.IsRelational())
        {
            var id = entry.Id;
            var claimed = await _db.JournalEntries.IgnoreQueryFilters()
                .Where(j => j.Id == id && j.Status == JournalStatuses.Draft)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JournalStatuses.Posted), ct);
            if (claimed == 0) throw new JournalConflictException(JournalErrorCodes.NotDraft, JournalMessages.AlreadyPosted);
        }
        entry.FiscalYearId = period.FiscalYearId;
        entry.FiscalPeriodId = period.FiscalPeriodId;
        MarkPosted(entry, userId, userName);
        await _balances.MoveLinesAsync(lines, true, ct);
    }

    /// <summary>
    /// Replaces a Draft's header and lines (tracked, lines loaded) with <paramref name="draft"/>, validated like a create.
    /// The number, the journal type and the key never change (BR-JE-010); the date must stay in the number's fiscal year.
    /// Saves once (the old lines must be deleted before new lines with the same numbers are inserted): call it inside the
    /// caller's transaction.
    /// </summary>
    public async Task UpdateDraftAsync(JournalEntry entry, JournalDraft draft, CancellationToken ct = default)
    {
        EnsureDraft(entry, posting: false);
        draft.OrganizationId = entry.OrganizationId;
        draft.JournalTypeId = entry.JournalTypeId;
        var type = await LoadTypeAsync(entry.OrganizationId, entry.JournalTypeId, ct, requireActive: false);
        await PeriodLockAsync(entry.OrganizationId, ct);
        var built = await BuildAsync(draft, type, ct);
        if (entry.FiscalYearId is { } fy && built.Period.FiscalYearId != fy)
            throw new JournalValidationException(JournalErrorCodes.NoPeriod,
                "The entry date must stay in the fiscal year of the entry number");

        if (_db.Database.IsRelational())
        {
            var id = entry.Id;
            var now = Now;
            var claimed = await _db.JournalEntries.IgnoreQueryFilters()
                .Where(j => j.Id == id && j.Status == JournalStatuses.Draft)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.ModifiedAt, now), ct);
            if (claimed == 0) throw new JournalConflictException(JournalErrorCodes.NotDraft, JournalMessages.PostedImmutable);
        }

        entry.EntryDate      = draft.EntryDate;
        entry.DocumentDate   = draft.DocumentDate;
        entry.Reference      = TrimTo(draft.Reference, 200);
        entry.Description    = TrimTo(draft.Description, 500);
        entry.CurrencyCode   = built.Currency;
        entry.ExchangeRate   = built.Rate;
        entry.FiscalYearId   = built.Period.FiscalYearId;
        entry.FiscalPeriodId = built.Period.FiscalPeriodId;
        entry.IsOpeningEntry = built.IsOpening;
        entry.IsClosingEntry = built.IsClosing;
        entry.ModifiedBy     = draft.UserId;
        entry.ModifiedByName = TrimTo(draft.UserName, 200);
        _db.JournalEntryLines.RemoveRange(entry.Lines);
        entry.Lines.Clear();
        Fill(entry, built.Lines);
        // D-CORE-7 — the old lines are deleted before the new ones (same line numbers) are inserted.
        await _db.SaveChangesAsync(ct);
        await _balances.MoveLinesAsync(entry.Lines.ToList(), false, ct);
    }

    /// <summary>Removes a Draft (and its lines). On SQL Server a conditional DELETE, so a concurrent post wins cleanly. Not saved in memory.</summary>
    public async Task DeleteDraftAsync(JournalEntry entry, CancellationToken ct = default)
    {
        if (entry.Status != JournalStatuses.Draft)
            throw new JournalConflictException(JournalErrorCodes.NotDraft, JournalMessages.PostedNotDeletable);
        if (_db.Database.IsRelational())
        {
            var id = entry.Id;
            // The lines go with it (FK ON DELETE CASCADE, A42).
            var deleted = await _db.JournalEntries.IgnoreQueryFilters().Where(j => j.Id == id && j.Status == JournalStatuses.Draft).ExecuteDeleteAsync(ct);
            if (deleted == 0) throw new JournalConflictException(JournalErrorCodes.NotDraft, JournalMessages.PostedNotDeletable);
            foreach (var e in _db.ChangeTracker.Entries<JournalEntryLine>().Where(e => e.Entity.JournalEntryId == id).ToList()) e.State = EntityState.Detached;
            _db.Entry(entry).State = EntityState.Detached;
            return;
        }
        _db.JournalEntryLines.RemoveRange(entry.Lines);
        _db.JournalEntries.Remove(entry);
    }

    // ── legacy writers (A42 opening post, A44 target) ─────────────────────────────────────────────────────────────

    /// <summary>Seeds the system journal types when missing — call before opening a posting transaction, so the
    /// per-organization seeding lock is never held across one.</summary>
    public Task EnsureJournalTypesAsync(Guid organizationId, CancellationToken ct = default) => _types.EnsureSeededAsync(organizationId, ct);

    /// <summary>
    /// D-CORE-2 — fills the A47 columns of a journal built by an A42/A44 writer that does not go through the engine yet:
    /// the journal type by its EntryType (D-12), the fiscal year/period of its date, the opening flag, the line count, the
    /// lines' currency/residual. Seeds the system journal types when missing (saves them). The numbering and the posting
    /// stay the writer's (C11 moves the A44 target onto <see cref="CreateAsync"/>).
    /// </summary>
    public async Task StampLegacyAsync(JournalEntry journal, CancellationToken ct = default)
    {
        var org = journal.OrganizationId;
        if (journal.JournalTypeId == 0)
            journal.JournalTypeId = (await _types.ResolveAsync(org, journal.EntryType, journal.Lines.Select(l => l.AccountId), ct)).Id;
        if (journal.EntryType == JournalEntryTypes.OpeningBalance) journal.IsOpeningEntry = true;
        if (journal.FiscalPeriodId is null)
        {
            var period = await _periods.ResolveAsync(org, journal.EntryDate, journal.IsOpeningEntry, journal.IsClosingEntry, ct);
            journal.FiscalYearId = period.FiscalYearId;
            journal.FiscalPeriodId = period.FiscalPeriodId;
        }
        journal.LineCount = journal.Lines.Count;
        foreach (var l in journal.Lines)
        {
            l.CurrencyCode ??= journal.CurrencyCode;
            l.AmountCurrency ??= l.DebitAmount - l.CreditAmount;
            if (!l.Reconciled && l.ReconcileGroupId is null) l.AmountResidual = l.DebitAmount - l.CreditAmount;
        }
    }

    // ── validation ─────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record BuiltLine(JournalDraftLine Source, int LineNumber, decimal BaseDebit, decimal BaseCredit, decimal AmountCurrency,
        string Currency, decimal? Rate, string? PartnerType, string? PartnerName);

    private sealed record Built(PeriodResolution Period, string? Currency, decimal? Rate, bool IsOpening, bool IsClosing, List<BuiltLine> Lines);

    private async Task<JournalType> LoadTypeAsync(Guid org, int typeId, CancellationToken ct, bool requireActive = true)
    {
        var type = await _db.JournalTypes.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(t => t.Id == typeId && t.OrganizationId == org, ct)
            ?? throw new JournalValidationException(JournalErrorCodes.JournalTypeInvalid, JournalMessages.JournalTypeNotFound);
        if (requireActive && !type.IsActive)
            throw new JournalValidationException(JournalErrorCodes.JournalTypeInvalid, JournalMessages.JournalTypeInactive);
        return type;
    }

    /// <summary>§9.4 steps 2–7 — every problem at once (the first is the message).</summary>
    private async Task<Built> BuildAsync(JournalDraft draft, JournalType type, CancellationToken ct)
    {
        var org = draft.OrganizationId;
        var errors = new List<JournalError>();

        // Flags: the Opening / Closing journal types make opening / closing entries.
        var isOpening = draft.IsOpeningEntry || type.Type == JournalTypeKinds.Opening;
        var isClosing = draft.IsClosingEntry || type.Type == JournalTypeKinds.Closing;
        if (isOpening && isClosing) errors.Add(new JournalError(JournalErrorCodes.JournalTypeInvalid, JournalMessages.OpeningAndClosing));

        // Step 3 — the period (D-11).
        var period = await _periods.ResolveAsync(org, draft.EntryDate, isOpening, isClosing, ct);
        if (!period.Allowed && !(draft.SkipPeriodCheck && period.FiscalPeriodId is not null && period.Message != JournalMessages.YearClosed))
            errors.Add(new JournalError(period.ErrorCode!, period.Message!));
        if (!period.Allowed && draft.SkipPeriodCheck && period.FiscalPeriodId is not null)
            period = period with { ErrorCode = null, Message = null };

        // Step 6 — currency (BR-JE-012).
        var baseCurrency = await BaseCurrencyAsync(org, draft.EntryDate, ct);
        var currency = NormalizeCurrency(draft.CurrencyCode) ?? baseCurrency;
        decimal? rate = draft.ExchangeRate;
        if (currency is not null && !CurrencyPattern.IsMatch(currency))
            errors.Add(new JournalError(JournalErrorCodes.ExchangeRateRequired, JournalMessages.CurrencyInvalid));
        else if (IsForeign(currency, baseCurrency))
        {
            if (rate is null) errors.Add(new JournalError(JournalErrorCodes.ExchangeRateRequired, JournalMessages.ExchangeRateRequired));
            else if (rate <= 0) errors.Add(new JournalError(JournalErrorCodes.ExchangeRateRequired, JournalMessages.ExchangeRateInvalid));
        }
        else rate = null; // the base currency: rate 1

        // Step 4 — lines (BR-JE-002 / 003).
        if (draft.Lines.Count < 2) errors.Add(new JournalError(JournalErrorCodes.TooFewLines, JournalMessages.TooFewLines));
        if (draft.Lines.Count > JournalLimits.MaxLines) errors.Add(new JournalError(JournalErrorCodes.TooFewLines, JournalMessages.TooManyLines));

        var built = new List<BuiltLine>();
        var n = 0;
        foreach (var line in draft.Lines)
        {
            n++;
            var debit = Math.Round(line.Debit, 4, MidpointRounding.AwayFromZero);
            var credit = Math.Round(line.Credit, 4, MidpointRounding.AwayFromZero);
            if (debit < 0 || credit < 0) { errors.Add(new JournalError(JournalErrorCodes.NoAmount, JournalMessages.Negative, n)); continue; }
            if (debit > 0 && credit > 0) { errors.Add(new JournalError(JournalErrorCodes.BothSides, JournalMessages.BothSides, n)); continue; }
            if (debit == 0 && credit == 0) { errors.Add(new JournalError(JournalErrorCodes.NoAmount, JournalMessages.NoAmount, n)); continue; }

            var lineCurrency = NormalizeCurrency(line.CurrencyCode) ?? currency ?? baseCurrency ?? string.Empty;
            var lineRate = line.ExchangeRate ?? (lineCurrency == currency ? rate : null);
            decimal baseDebit = debit, baseCredit = credit;
            if (!draft.AmountsInBase && IsForeign(lineCurrency, baseCurrency))
            {
                if (lineRate is not { } lr || lr <= 0)
                {
                    if (lineCurrency != currency) errors.Add(new JournalError(JournalErrorCodes.ExchangeRateRequired, JournalMessages.ExchangeRateRequired, n));
                    continue;
                }
                baseDebit = Math.Round(debit * lr, 2, MidpointRounding.AwayFromZero);   // BR-JE-013, D-8
                baseCredit = Math.Round(credit * lr, 2, MidpointRounding.AwayFromZero);
            }
            else if (!draft.AmountsInBase) lineRate = null;

            // Partner (A-12, D-13).
            string? partnerType = null;
            if (line.PartnerType is not null || line.PartnerUuid is not null)
            {
                partnerType = JournalPartnerTypes.All.FirstOrDefault(t => string.Equals(t, line.PartnerType?.Trim(), StringComparison.OrdinalIgnoreCase));
                if (partnerType is null || line.PartnerUuid is null || line.PartnerUuid == Guid.Empty)
                    errors.Add(new JournalError(JournalErrorCodes.PartnerRequired, JournalMessages.PartnerTypeInvalid, n));
            }
            else if (draft.IsManual && type.RequiresPartner)
                errors.Add(new JournalError(JournalErrorCodes.PartnerRequired, JournalMessages.PartnerRequired(type.Type), n));

            var partnerName = TrimTo(line.PartnerName, 200);
            if (draft.IsManual && partnerType is not null && line.PartnerUuid is { } pid && pid != Guid.Empty && _partners is not null)
            {
                var partner = await _partners.GetAsync(pid, ct);
                if (partner is null) errors.Add(new JournalError(JournalErrorCodes.PartnerRequired, JournalMessages.PartnerNotFound, n));
                else partnerName = TrimTo(partner.Name, 200);
            }

            built.Add(new BuiltLine(line, n, baseDebit, baseCredit,
                draft.AmountsInBase ? line.AmountCurrency ?? baseDebit - baseCredit : debit - credit,
                lineCurrency, lineRate, partnerType, partnerName));
        }

        // Step 5 — accounts (BR-JE-006).
        errors.AddRange(await CheckAccountsAsync(org, built.Select(b => (b.LineNumber, b.Source.AccountId)), ct, draft.AllowInactiveAccounts));

        // Step 4 — balance (BR-JE-001): in the transaction currency when the lines share one, and always in base.
        if (built.Count == draft.Lines.Count && built.Count >= 2)
        {
            var currencies = built.Select(b => b.Currency).Distinct().ToList();
            if (!draft.AmountsInBase && currencies.Count == 1 && IsForeign(currencies[0], baseCurrency))
            {
                var d = built.Sum(b => Math.Max(b.AmountCurrency, 0)); var c = built.Sum(b => Math.Max(-b.AmountCurrency, 0));
                if (Math.Abs(d - c) >= JournalLimits.BalanceTolerance)
                    errors.Add(new JournalError(JournalErrorCodes.NotBalanced, JournalMessages.NotBalancedDetail(d, c, currencies[0])));
            }
            var bd = built.Sum(b => b.BaseDebit); var bc = built.Sum(b => b.BaseCredit);
            // REV-2 — base totals must be equal exactly (converted amounts are already 2 dp; the gateway adds its rounding line).
            if (bd != bc && !errors.Any(e => e.ErrorCode == JournalErrorCodes.NotBalanced))
                errors.Add(new JournalError(JournalErrorCodes.NotBalanced,
                    JournalMessages.NotBalancedDetail(bd, bc, IsForeign(currency, baseCurrency) ? baseCurrency : null)));
        }

        if (errors.Count > 0) throw new JournalValidationException(errors);
        return new Built(period, currency, rate, isOpening, isClosing, built);
    }

    /// <summary>BR-JE-006 — each account exists in the organization, is a leaf (level 4) and is active.</summary>
    private async Task<List<JournalError>> CheckAccountsAsync(Guid org, IEnumerable<(int LineNumber, int AccountId)> lines, CancellationToken ct,
        bool allowInactive = false)
    {
        var list = lines.ToList();
        var ids = list.Select(l => l.AccountId).Distinct().ToList();
        var accounts = await _db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.OrganizationId == org && ids.Contains(a.Id))
            .Select(a => new { a.Id, a.IsGroup, a.Level, a.IsActive }).ToDictionaryAsync(a => a.Id, ct);
        var errors = new List<JournalError>();
        foreach (var (lineNumber, accountId) in list)
        {
            if (!accounts.TryGetValue(accountId, out var a))
                errors.Add(new JournalError(JournalErrorCodes.AccountNotPostable, JournalMessages.AccountNotFound, lineNumber));
            else if (a.IsGroup || a.Level != 4)
                errors.Add(new JournalError(JournalErrorCodes.AccountNotPostable, JournalMessages.AccountNotPostable, lineNumber));
            else if (!a.IsActive && !allowInactive)
                errors.Add(new JournalError(JournalErrorCodes.AccountInactive, JournalMessages.AccountInactive, lineNumber));
        }
        return errors;
    }

    /// <summary>The reporting currency of the fiscal year containing the date, else of the latest year (as A44).</summary>
    internal async Task<string?> BaseCurrencyAsync(Guid org, DateOnly date, CancellationToken ct)
    {
        var years = _db.FiscalYears.IgnoreQueryFilters().AsNoTracking().Where(y => y.OrganizationId == org);
        return await years.Where(y => y.StartDate <= date && date <= y.EndDate).Select(y => y.ReportingCurrencyCode).FirstOrDefaultAsync(ct)
               ?? await years.OrderByDescending(y => y.StartDate).Select(y => y.ReportingCurrencyCode).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// A47 REV-11 — the organization's period lock, <b>shared</b>, before the period is resolved: journals wait for a running
    /// period / year-end close (which holds it exclusive) but not for each other. Owned by the caller's transaction, so a
    /// year-end close that already holds it exclusive in the same transaction is not blocked. No transaction, or the
    /// in-memory provider: nothing (GlPeriodLock is a no-op in memory).
    /// </summary>
    private Task PeriodLockAsync(Guid org, CancellationToken ct) =>
        _db.Database.IsRelational() && _db.Database.CurrentTransaction is not null
            ? GlPeriodLock.AcquireAsync(_db, org, exclusive: false, ct)
            : Task.CompletedTask;

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────────────────────

    private void Fill(JournalEntry entry, List<BuiltLine> lines)
    {
        var now = Now;
        foreach (var b in lines)
            entry.Lines.Add(new JournalEntryLine
            {
                OrganizationId = entry.OrganizationId,
                LineNumber     = b.LineNumber,
                AccountId      = b.Source.AccountId,
                DebitAmount    = b.BaseDebit,
                CreditAmount   = b.BaseCredit,
                Memo           = TrimTo(b.Source.Description, 500),
                PartnerUuid    = b.PartnerType is null ? null : b.Source.PartnerUuid,
                PartnerType    = b.PartnerType,
                PartnerName    = b.PartnerType is null ? null : b.PartnerName,
                CurrencyCode   = string.IsNullOrEmpty(b.Currency) ? null : b.Currency,
                AmountCurrency = b.AmountCurrency,
                ExchangeRate   = b.Rate,
                CostCenter     = TrimTo(b.Source.CostCenter, 50),
                Project        = TrimTo(b.Source.Project, 50),
                TaxCode        = TrimTo(b.Source.TaxCode, 50),
                TaxAmount      = b.Source.TaxAmount,
                AmountResidual = b.BaseDebit - b.BaseCredit,
                CreatedAt      = now,
            });
        entry.TotalDebit = entry.Lines.Sum(l => l.DebitAmount);
        entry.TotalCredit = entry.Lines.Sum(l => l.CreditAmount);
        entry.LineCount = entry.Lines.Count;
    }

    private void MarkPosted(JournalEntry entry, int? userId, string? userName)
    {
        entry.Status = JournalStatuses.Posted;
        entry.PostedAt = Now;
        entry.PostedBy = userId;
        entry.PostedByName = TrimTo(userName, 200);
    }

    private static void EnsureDraft(JournalEntry entry, bool posting)
    {
        if (entry.Status == JournalStatuses.Reversed)
            throw new JournalConflictException(JournalErrorCodes.NotDraft, JournalMessages.AlreadyReversed);
        if (entry.Status != JournalStatuses.Draft)
            throw new JournalConflictException(JournalErrorCodes.NotDraft, posting ? JournalMessages.AlreadyPosted : JournalMessages.PostedImmutable);
    }

    private static bool IsForeign(string? currency, string? baseCurrency) =>
        currency is not null && baseCurrency is not null && !string.Equals(currency, baseCurrency, StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeCurrency(string? code) => string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static string? TrimTo(string? value, int max)
    {
        var t = value?.Trim();
        return string.IsNullOrEmpty(t) ? null : Truncate(t, max);
    }
}

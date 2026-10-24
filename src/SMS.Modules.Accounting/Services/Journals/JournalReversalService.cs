using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Accounting.Services;

/// <summary>A47 C7 — the reversal's texts (API-CONTRACT §3). The rule code is JE-015 (<see cref="JournalErrorCodes.Reversal"/>).</summary>
public static class ReversalMessages
{
    public const string NotPosted       = "Only posted entries can be reversed";
    public const string AlreadyReversed = "Entry has already been reversed";
    public const string IsReversal      = "A reversal entry cannot be reversed";
    public const string OpeningBalance  = "The opening balance entry cannot be reversed";
    /// <summary>D-RR-3 (contract Changes) — the year-end close is undone by reopening the year, not by a reversal.</summary>
    public const string YearEndClose    = "The year-end closing entry cannot be reversed";
    public const string ReasonRequired  = "A reversal reason is required";
    public const string ReasonTooLong   = "The reversal reason can be at most 200 characters";
    public const string NoPeriod        = "No fiscal period covers the reversal date";
    public const string PeriodClosed    = "Reversal period is closed";
    public const string PeriodLocked    = "Reversal period is locked";
    public const string AdjustmentOnly  = "Reversal period accepts only adjustment entries";
    public const string NoPermission    = "You do not have permission to reverse journal entries.";

    /// <summary>§10.3 step 3 / TS-RV-006 — "Reversal of SALE/2026/0042: Incorrect amount".</summary>
    public static string Description(string entryNumber, string reason) => $"Reversal of {entryNumber}: {reason}";

    /// <summary>D-14 — returned with the reversal of a journal posted from a source document.</summary>
    public static string SourceWarning(string? module) =>
        $"This journal was posted from a source document{(string.IsNullOrWhiteSpace(module) ? "" : $" ({module})")}. The document itself was not changed.";
}

/// <summary>
/// A47 C7 §10 (D-14, API-CONTRACT §3) — reverses a Posted journal entry: a mirror entry (every line's debit and credit swapped, same
/// account, partner, description, dimensions and currency amounts negated) is created and posted at once through
/// <see cref="JournalEntryWriter"/> (sequence, period check, balance move — net zero, BR-RV-004/005), keyed
/// <c>reversal:{original uuid}</c>; the original becomes Reversed with <c>ReversedByEntryId</c> (BR-RV-006/007); per account the
/// original's lines and their mirrors are put into one Full reconciliation (BR-RV-008) unless an original line of that account is
/// already reconciled. All in one transaction; on SQL Server under application locks on the entry and on its accounts.
/// </summary>
public interface IJournalReversalService
{
    /// <summary>GL_JOURNAL_REVERSE. 404 unknown, 409 not reversible (JE-015), 400 reason (JE-015) / period (JE-005..008).</summary>
    Task<JournalReversalResultModel> ReverseAsync(AccountingCaller caller, Guid entryUuid, ReverseJournalEntryRequest request, CancellationToken ct = default);
}

internal sealed class JournalReversalService : IJournalReversalService
{
    internal const int MaxReasonLength = 200;

    private readonly AccountingDbContext _db;
    private readonly JournalEntryWriter _writer;
    private readonly JournalPeriodResolver _periods;
    private readonly JournalReconciler _reconciler;
    private readonly IAccountingEventPublisher _events;

    public JournalReversalService(AccountingDbContext db, JournalEntryWriter writer, JournalPeriodResolver periods, JournalReconciler reconciler,
        IAccountingEventPublisher events)
    {
        _db = db;
        _writer = writer;
        _periods = periods;
        _reconciler = reconciler;
        _events = events;
    }

    public async Task<JournalReversalResultModel> ReverseAsync(AccountingCaller caller, Guid entryUuid, ReverseJournalEntryRequest request,
        CancellationToken ct = default)
    {
        caller.Require(caller.CanReverseJournal, ReversalMessages.NoPermission);
        request ??= new ReverseJournalEntryRequest();
        var org = caller.OrganizationId;

        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason)) throw new JournalValidationException(JournalErrorCodes.Reversal, ReversalMessages.ReasonRequired);
        if (reason.Length > MaxReasonLength) throw new JournalValidationException(JournalErrorCodes.Reversal, ReversalMessages.ReasonTooLong);

        var originalId = await _db.JournalEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(j => j.OrganizationId == org && j.Uuid == entryUuid).Select(j => (int?)j.Id).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException(JournalMessages.EntryNotFound + ".");

        JournalEntry? original = null, reversal = null;
        var created = false;
        var groups = new List<string>();
        await _db.InTransactionAsync(async () =>
        {
            groups.Clear();
            // One reverser per entry, then the accounts (the same locks as a reconcile) before any line is read.
            await _reconciler.LockAsync($"glrev:{org:N}:{originalId}", ct);
            var accountIds = await _db.JournalEntryLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.JournalEntryId == originalId).Select(l => l.AccountId).Distinct().ToListAsync(ct);
            await _reconciler.LockAccountsAsync(org, accountIds, ct);

            original = await _db.JournalEntries.IgnoreQueryFilters().Include(j => j.Lines)
                .FirstAsync(j => j.OrganizationId == org && j.Id == originalId, ct);
            EnsureReversible(original);

            // §10.3 step 2 / BR-RV-003 — the reversal date's period, by the original's kind (D-11, D-RR-2).
            var date = request.ReversalDate ?? original.EntryDate;
            var period = await _periods.ResolveAsync(org, date, original.IsOpeningEntry, original.IsClosingEntry, ct);
            if (!period.Allowed) throw new JournalValidationException(period.ErrorCode!, PeriodMessage(period.ErrorCode!, period.Message));

            // REV-8 — 0.00/0.00 lines (an A44 line whose base amount rounded to zero) need no mirror and no reconciliation; the
            // writer numbers the mirrors 1..n contiguously and the auto-reconciliation matches them by position in this same list.
            var lines = original.Lines.Where(l => l.DebitAmount != 0 || l.CreditAmount != 0).OrderBy(l => l.LineNumber).ToList();
            var written = await _writer.CreateAsync(MirrorDraft(original, lines, date, reason, await ReversalTypeIdAsync(org, original.JournalTypeId, ct), caller), post: true, ct);
            reversal = written.Entry;
            created = written.Created;

            // BR-RV-006 / 007 — the original points to its reversal (also when an earlier, interrupted call left the reversal only).
            original.Status = JournalStatuses.Reversed;
            original.ReversedByEntry = reversal;
            original.ReversalReason = written.Created ? reason : reversal.ReversalReason ?? reason;
            original.ModifiedBy = caller.UserIdOrNull;
            original.ModifiedByName = caller.UserName is { Length: > 200 } n ? n[..200] : caller.UserName;

            if (written.Created) groups.AddRange(await AutoReconcileAsync(org, original, lines, reversal, date, caller, ct));

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // The unique reversal:{uuid} key: a concurrent reversal won.
                throw new JournalConflictException(JournalErrorCodes.Reversal, ReversalMessages.AlreadyReversed);
            }
        }, ct);

        if (created)
            await _events.PublishAsync(new JournalEntryReversedEvent(org, original!.Uuid, original.EntryNumber, reversal!.Uuid, reversal.EntryNumber,
                original.ReversalReason, caller.UserIdOrNull, caller.UserName), ct);

        return new JournalReversalResultModel
        {
            Reversal = await DetailAsync(caller, reversal!.Id, ct),
            Original = new JournalEntryRefModel { Uuid = original!.Uuid, EntryNumber = original.EntryNumber },
            ReconcileGroupNumbers = groups,
            Warning = IsSourceJournal(original) ? ReversalMessages.SourceWarning(original.SourceModule) : null,
        };
    }

    /// <summary>§10.3 steps 3–4 — the mirror of <paramref name="original"/> as an engine draft (base amounts, sides swapped).</summary>
    internal static JournalDraft MirrorDraft(JournalEntry original, IReadOnlyList<JournalEntryLine> lines, DateOnly date, string reason, int journalTypeId,
        AccountingCaller caller) => new()
    {
        OrganizationId        = original.OrganizationId,
        JournalTypeId         = journalTypeId,
        EntryDate             = date,
        Reference             = original.Reference,
        Description           = ReversalMessages.Description(original.EntryNumber, reason),
        EntryType             = original.EntryType,
        SourceModule          = original.SourceModule,
        SourceDocumentUuid    = original.SourceDocumentUuid,
        IdempotencyKey        = JournalKeys.Reversal(original.Uuid),
        CurrencyCode          = original.CurrencyCode,
        ExchangeRate          = original.ExchangeRate,
        IsOpeningEntry        = original.IsOpeningEntry,
        IsClosingEntry        = original.IsClosingEntry,
        ReversesEntryId       = original.Id,
        ReversalReason        = reason,
        AmountsInBase         = true,
        AllowInactiveAccounts = true,
        UserId                = caller.UserIdOrNull,
        UserName              = caller.UserName,
        Lines = lines.Select(l => new JournalDraftLine
        {
            AccountId      = l.AccountId,
            Debit          = l.CreditAmount,   // BR-RV-004 — the sides swap
            Credit         = l.DebitAmount,
            AmountCurrency = -(l.AmountCurrency ?? l.DebitAmount - l.CreditAmount),
            CurrencyCode   = l.CurrencyCode,
            ExchangeRate   = l.ExchangeRate,
            PartnerUuid    = l.PartnerUuid,
            PartnerType    = l.PartnerType,
            PartnerName    = l.PartnerName,
            Description    = l.Memo,
            CostCenter     = l.CostCenter,
            Project        = l.Project,
            TaxCode        = l.TaxCode,
            TaxAmount      = l.TaxAmount,      // its side follows the line's, like the amounts
        }).ToList(),
    };

    /// <summary>
    /// BR-RV-008 — per account: the original's lines and their mirrors in one Full group, cleared by the reversal on its date;
    /// skipped for an account where an original line is already reconciled (it stays with its group, its mirror stays open).
    /// </summary>
    /// <remarks>A47 GW — the per-account grouping lives in <see cref="JournalReconciler.ReconcileReversalAsync"/>, shared with the A44 gateway reversal.</remarks>
    private Task<List<string>> AutoReconcileAsync(Guid org, JournalEntry original, IReadOnlyList<JournalEntryLine> lines, JournalEntry reversal,
        DateOnly date, AccountingCaller caller, CancellationToken ct) =>
        _reconciler.ReconcileReversalAsync(org, original, lines, reversal, date, caller.UserIdOrNull, caller.UserName, ct);

    /// <summary>D-RR-2 / D-12 — the journal type a reversal of <paramref name="typeId"/> is written with (shared with the A44 gateway reversal).</summary>
    internal static async Task<int> ActiveTypeOrMiscAsync(AccountingDbContext db, Guid org, int typeId, CancellationToken ct)
    {
        var types = db.JournalTypes.IgnoreQueryFilters().AsNoTracking().Where(t => t.OrganizationId == org && t.IsActive);
        if (await types.AnyAsync(t => t.Id == typeId, ct)) return typeId;
        return await types.Where(t => t.Code == SystemJournalTypes.Misc).Select(t => (int?)t.Id).FirstOrDefaultAsync(ct)
               ?? typeId; // the writer then refuses with "Journal type is inactive"
    }

    /// <summary>BR-RV-001 / 002 and the contract's other refusals — 409 JE-015.</summary>
    internal static void EnsureReversible(JournalEntry e)
    {
        if (e.Status == JournalStatuses.Reversed || e.ReversedByEntryId is not null)
            throw new JournalConflictException(JournalErrorCodes.Reversal, ReversalMessages.AlreadyReversed);
        if (e.Status != JournalStatuses.Posted)
            throw new JournalConflictException(JournalErrorCodes.Reversal, ReversalMessages.NotPosted);
        if (e.ReversesEntryId is not null)
            throw new JournalConflictException(JournalErrorCodes.Reversal, ReversalMessages.IsReversal);
        if (e.EntryType == JournalEntryTypes.OpeningBalance)
            throw new JournalConflictException(JournalErrorCodes.Reversal, ReversalMessages.OpeningBalance);
        if (e.EntryType == JournalEntryTypes.YearEndClose)
            throw new JournalConflictException(JournalErrorCodes.Reversal, ReversalMessages.YearEndClose);
    }

    /// <summary>TS-RV-004 — the period refusal in the reversal's words; the code stays the period's (JE-005..008).</summary>
    internal static string PeriodMessage(string code, string? message) => code switch
    {
        _ when message == JournalMessages.YearClosed => JournalMessages.YearClosed,
        JournalErrorCodes.NoPeriod       => ReversalMessages.NoPeriod,
        JournalErrorCodes.PeriodLocked   => ReversalMessages.PeriodLocked,
        JournalErrorCodes.AdjustmentOnly => ReversalMessages.AdjustmentOnly,
        _                                => ReversalMessages.PeriodClosed,
    };

    /// <summary>D-RR-2 — the original's journal type; a deactivated one falls back to MISC (as the D-12 resolver does).</summary>
    private Task<int> ReversalTypeIdAsync(Guid org, int typeId, CancellationToken ct) => ActiveTypeOrMiscAsync(_db, org, typeId, ct);

    /// <summary>D-14 — posted from a source document (A44 gateway), not entered by hand.</summary>
    internal static bool IsSourceJournal(JournalEntry e) => e.SourceDocumentUuid is not null && !JournalEntryService.IsManual(e);

    private async Task<JournalEntryDetailModel> DetailAsync(AccountingCaller caller, int id, CancellationToken ct)
    {
        var j = await _db.JournalEntries.IgnoreQueryFilters().AsNoTracking().AsSplitQuery()
            .Include(x => x.JournalType).Include(x => x.FiscalYear).Include(x => x.FiscalPeriod)
            .Include(x => x.ReversesEntry).Include(x => x.ReversedByEntry)
            .Include(x => x.Lines).ThenInclude(l => l.Account)
            .Include(x => x.Lines).ThenInclude(l => l.ReconcileGroup)
            .Include(x => x.Lines).ThenInclude(l => l.ClearingEntry)
            .FirstAsync(x => x.OrganizationId == caller.OrganizationId && x.Id == id, ct);
        return JournalEntryService.ToDetail(j, caller);
    }
}

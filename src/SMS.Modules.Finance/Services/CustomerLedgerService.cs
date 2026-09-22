using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.Services;

/// <summary>What to write on a customer's account.</summary>
/// <param name="Debit">Increases what the customer owes (§10: invoice, debit note, refund, opening balance).</param>
/// <param name="Credit">Decreases it (payment, credit note, advance).</param>
internal sealed record CustomerLedgerPosting(
    Guid     PartnerId,
    string   EntryType,
    string   ReferenceType,
    Guid     ReferenceId,
    string   ReferenceNumber,
    decimal  Debit,
    decimal  Credit,
    string   CurrencyCode,
    string?  Narration,
    DateTime EntryDate,
    int      CreatedBy);

/// <summary>
/// The receivables ledger (A29 §10) — the customer-side mirror of <see cref="ISupplierLedgerService"/>:
/// append-only, each entry's running balance derived from the one before it
/// (<c>previous + debit − credit</c>), and a per-customer <c>SequenceNo</c> whose unique index is the
/// concurrency guard. There is no update and no delete; a correction is a new, offsetting entry.
/// <para>
/// There are two ways to write, and which one a caller wants depends on what else must commit with it.
/// </para>
/// <list type="bullet">
/// <item><see cref="TrackEntryAsync"/> — the entry is one half of a business action (§9.5: an invoice
/// issued, a payment received). It adds the entry to the caller's own context and <b>does not save</b>,
/// so the caller's single <c>SaveChangesAsync</c> commits the status change and the entry together or
/// neither. The caller owns the retry.</item>
/// <item><see cref="AppendEntryAsync"/> — the entry is the whole action (an opening balance, a manual
/// adjustment). It saves, and retries a lost sequence race itself. As with the supplier ledger, that
/// save also commits anything else the caller has tracked on the same context.</item>
/// </list>
/// </summary>
internal interface ICustomerLedgerService : ICustomerLedgerQueryService
{
    /// <summary>Builds the customer's next entry and tracks it, unsaved, on the shared context.</summary>
    Task<CustomerLedgerEntry> TrackEntryAsync(CustomerLedgerPosting posting);

    /// <summary>
    /// Appends an entry to the customer's ledger and saves it: <c>RunningBalance = previous balance +
    /// debit − credit</c>, <c>SequenceNo = previous + 1</c>. Two concurrent appends for one customer
    /// always end up with distinct, correctly chained rows — the loser of the race re-reads the fresh
    /// last entry and tries again, so no balance is ever lost or forked.
    /// </summary>
    /// <param name="entryType">One of §10's kinds — INVOICE, PAYMENT, CREDIT_NOTE, DEBIT_NOTE, ADVANCE, REFUND, OPENING_BAL.</param>
    /// <param name="debit">Increases what the customer owes. Exactly one of <paramref name="debit"/> and <paramref name="credit"/> is above zero.</param>
    /// <param name="credit">Decreases it.</param>
    /// <param name="currencyCode">Required rather than defaulted: a ledger line in a guessed currency is worse than a refused one.</param>
    /// <param name="entryDate">The business date. Defaults to now; a back-dated receipt passes its receipt date.</param>
    Task<CustomerLedgerEntryModel> AppendEntryAsync(
        Guid partnerId, string entryType, decimal debit, decimal credit, CustomerLedgerReference reference,
        string currencyCode, int createdBy, string? narration = null, DateTime? entryDate = null);

}

/// <summary>
/// Reading a customer's ledger. Public and read-only, like <see cref="IMasterLedgerQueryService"/>: an
/// endpoint or a report may ask what a customer owes, but nothing outside Finance may write to the
/// ledger, because an entry written outside the transaction of the business action behind it is
/// exactly what §9.5 rules out.
/// </summary>
public interface ICustomerLedgerQueryService
{
    /// <summary>
    /// A page of the customer's ledger, newest posting first, optionally within a date range. Ordered
    /// by <c>SequenceNo</c> — the order the running balance was computed in — so every row's balance
    /// equals the next row's plus this row's debit less its credit, even where a back-dated receipt
    /// makes the entry dates run out of order.
    /// </summary>
    Task<PaginatedResponse<CustomerLedgerEntryModel>> GetLedgerAsync(Guid partnerId, CustomerLedgerFilter filter);
}

internal sealed class CustomerLedgerService : ICustomerLedgerService, ICustomerLedgerQueryService
{
    private const int MaxAttempts = 5;

    private readonly FinanceDbContext _db;
    private readonly TimeProvider     _clock;

    public CustomerLedgerService(FinanceDbContext db, TimeProvider? clock = null)
    {
        _db    = db;
        _clock = clock ?? TimeProvider.System;
    }

    // ── Write ────────────────────────────────────────────────────────────────

    public async Task<CustomerLedgerEntry> TrackEntryAsync(CustomerLedgerPosting posting)
    {
        ArgumentNullException.ThrowIfNull(posting);
        Validate(posting);

        var last = await LastEntryAsync(posting.PartnerId);

        var entry = new CustomerLedgerEntry
        {
            UUID            = Guid.NewGuid(),
            PartnerId       = posting.PartnerId,
            SequenceNo      = (last?.SequenceNo ?? 0) + 1,
            EntryDate       = posting.EntryDate,
            EntryType       = posting.EntryType,
            ReferenceType   = posting.ReferenceType,
            ReferenceId     = posting.ReferenceId,
            ReferenceNumber = posting.ReferenceNumber,
            DebitAmount     = posting.Debit,
            CreditAmount    = posting.Credit,
            // §10: running_balance = previous + debit − credit.
            RunningBalance  = (last?.RunningBalance ?? 0m) + posting.Debit - posting.Credit,
            CurrencyCode    = posting.CurrencyCode,
            Narration       = posting.Narration,
            CreatedBy       = posting.CreatedBy,
            CreatedDate     = _clock.GetUtcNow().UtcDateTime
        };

        _db.CustomerLedgerEntries.Add(entry);
        return entry;
    }

    public async Task<CustomerLedgerEntryModel> AppendEntryAsync(
        Guid partnerId, string entryType, decimal debit, decimal credit, CustomerLedgerReference reference,
        string currencyCode, int createdBy, string? narration = null, DateTime? entryDate = null)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var posting = new CustomerLedgerPosting(
            partnerId, entryType, reference.Type, reference.Id, reference.Number,
            debit, credit, currencyCode, narration,
            entryDate ?? _clock.GetUtcNow().UtcDateTime, createdBy);

        for (var attempt = 1; ; attempt++)
        {
            // Rebuilt on every pass: the balance and the sequence both come from the customer's
            // last committed entry, which is exactly what a lost race changed.
            var entry = await TrackEntryAsync(posting);

            try
            {
                await _db.SaveChangesAsync();
                return ToModel(entry);
            }
            catch (DbUpdateException)
            {
                // Another writer committed this customer's next SequenceNo first. Detach only the
                // losing entry, so anything else the caller has tracked survives for the retry —
                // and so that, when the last attempt fails too, the entry is not left tracked for
                // some later save on this context to commit against a balance that has moved on.
                _db.Entry(entry).State = EntityState.Detached;
                if (attempt >= MaxAttempts) throw;
            }
        }
    }

    // ── Read ─────────────────────────────────────────────────────────────────

    public async Task<PaginatedResponse<CustomerLedgerEntryModel>> GetLedgerAsync(Guid partnerId, CustomerLedgerFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var (start, endExclusive) = DayRange.Of(filter.DateFrom, filter.DateTo, "ledger");

        var query = _db.CustomerLedgerEntries.AsNoTracking().Where(e => e.PartnerId == partnerId);

        if (start is { } from)        query = query.Where(e => e.EntryDate >= from);
        if (endExclusive is { } end)  query = query.Where(e => e.EntryDate < end);

        var total = await query.CountAsync();
        var (page, pageSize) = PagedResults.Clamp(filter.Page, filter.PageSize);

        var items = await query
            .OrderByDescending(e => e.SequenceNo)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new CustomerLedgerEntryModel
            {
                Uuid            = e.UUID,
                PartnerId       = e.PartnerId,
                SequenceNo      = e.SequenceNo,
                EntryDate       = e.EntryDate,
                EntryType       = e.EntryType,
                ReferenceType   = e.ReferenceType,
                ReferenceId     = e.ReferenceId,
                ReferenceNumber = e.ReferenceNumber,
                DebitAmount     = e.DebitAmount,
                CreditAmount    = e.CreditAmount,
                RunningBalance  = e.RunningBalance,
                CurrencyCode    = e.CurrencyCode,
                Narration       = e.Narration,
                CreatedBy       = e.CreatedBy,
                CreatedDate     = e.CreatedDate
            })
            .ToListAsync();

        return PagedResults.Of(items, total, page, pageSize);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void Validate(CustomerLedgerPosting posting)
    {
        if (posting.PartnerId == Guid.Empty)
            throw new ArgumentException("A ledger entry belongs to a customer.", nameof(posting));

        if (!CustomerLedgerEntryTypes.All.Contains(posting.EntryType))
            throw new ArgumentException(
                $"'{posting.EntryType}' is not a customer ledger entry type. Valid: {string.Join(", ", CustomerLedgerEntryTypes.All)}.",
                nameof(posting));

        if (posting.Debit < 0 || posting.Credit < 0)
            throw new ArgumentException("A ledger entry's debit and credit are never negative; a reversal is the opposite entry.", nameof(posting));

        if ((posting.Debit > 0) == (posting.Credit > 0))
            throw new ArgumentException("A ledger entry is either a debit or a credit, never both and never neither.", nameof(posting));

        // Money is kept to two decimal places; a third would be rounded away by the column, and the
        // running balance held in memory would no longer match the one stored.
        if (decimal.Round(posting.Debit, 2) != posting.Debit || decimal.Round(posting.Credit, 2) != posting.Credit)
            throw new ArgumentException("A ledger amount has more than two decimal places.", nameof(posting));

        Required(posting.CurrencyCode,    10, "currency code");
        Required(posting.ReferenceType,   30, "reference type");
        Required(posting.ReferenceNumber, 30, "reference number");

        if (posting.Narration is { Length: > 500 })
            throw new ArgumentException("A ledger narration is longer than 500 characters.", nameof(posting));
    }

    private static void Required(string? value, int max, string what)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"A ledger entry needs a {what}.");
        if (value.Length > max)
            throw new ArgumentException($"The ledger entry's {what} is longer than {max} characters.");
    }

    /// <summary>
    /// The customer's latest entry — including one this context has already tracked but not saved,
    /// so two entries built for the same customer in one unit of work chain rather than collide.
    /// </summary>
    private async Task<CustomerLedgerEntry?> LastEntryAsync(Guid partnerId)
    {
        var saved = await _db.CustomerLedgerEntries
            .Where(e => e.PartnerId == partnerId)
            .OrderByDescending(e => e.SequenceNo)
            .FirstOrDefaultAsync();

        var pending = _db.ChangeTracker.Entries<CustomerLedgerEntry>()
            .Where(e => e.State == EntityState.Added && e.Entity.PartnerId == partnerId)
            .Select(e => e.Entity)
            .OrderByDescending(e => e.SequenceNo)
            .FirstOrDefault();

        return pending is not null && pending.SequenceNo > (saved?.SequenceNo ?? 0) ? pending : saved;
    }

    private static CustomerLedgerEntryModel ToModel(CustomerLedgerEntry e) => new()
    {
        Uuid            = e.UUID,
        PartnerId       = e.PartnerId,
        SequenceNo      = e.SequenceNo,
        EntryDate       = e.EntryDate,
        EntryType       = e.EntryType,
        ReferenceType   = e.ReferenceType,
        ReferenceId     = e.ReferenceId,
        ReferenceNumber = e.ReferenceNumber,
        DebitAmount     = e.DebitAmount,
        CreditAmount    = e.CreditAmount,
        RunningBalance  = e.RunningBalance,
        CurrencyCode    = e.CurrencyCode,
        Narration       = e.Narration,
        CreatedBy       = e.CreatedBy,
        CreatedDate     = e.CreatedDate
    };
}

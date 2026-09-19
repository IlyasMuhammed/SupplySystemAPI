using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;

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
/// Writes the receivables ledger (A29 §10) — the customer-side mirror of <see cref="ISupplierLedgerService"/>.
/// <para>
/// <b>It does not save.</b> §9.5 requires the ledger entry to be written "in the same transaction as
/// the business action", so this adds the entry to the caller's own <see cref="FinanceDbContext"/> and
/// leaves the one <c>SaveChangesAsync</c> to the caller, which commits the status change and the entry
/// together or neither. (The supplier ledger saves inside its own retry loop; that is right when the
/// ledger is the whole action, and wrong when it is one half of it.)
/// </para>
/// <para>
/// The caller therefore owns the retry: two writers racing for the same customer's next
/// <c>SequenceNo</c> both compute the same number, the loser's save fails on the unique index, and it
/// must detach what it tracked and call this again against the fresh last row.
/// </para>
/// </summary>
internal interface ICustomerLedgerService
{
    /// <summary>Builds the customer's next entry and tracks it, unsaved, on the shared context.</summary>
    Task<CustomerLedgerEntry> TrackEntryAsync(CustomerLedgerPosting posting);
}

internal sealed class CustomerLedgerService : ICustomerLedgerService
{
    private readonly FinanceDbContext _db;

    public CustomerLedgerService(FinanceDbContext db) => _db = db;

    public async Task<CustomerLedgerEntry> TrackEntryAsync(CustomerLedgerPosting posting)
    {
        ArgumentNullException.ThrowIfNull(posting);

        if (!CustomerLedgerEntryTypes.All.Contains(posting.EntryType))
            throw new ArgumentException(
                $"'{posting.EntryType}' is not a customer ledger entry type. Valid: {string.Join(", ", CustomerLedgerEntryTypes.All)}.",
                nameof(posting));

        if (posting.Debit < 0 || posting.Credit < 0)
            throw new ArgumentException("A ledger entry's debit and credit are never negative; a reversal is the opposite entry.", nameof(posting));

        if ((posting.Debit > 0) == (posting.Credit > 0))
            throw new ArgumentException("A ledger entry is either a debit or a credit, never both and never neither.", nameof(posting));

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
            CreatedDate     = DateTime.UtcNow
        };

        _db.CustomerLedgerEntries.Add(entry);
        return entry;
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
}

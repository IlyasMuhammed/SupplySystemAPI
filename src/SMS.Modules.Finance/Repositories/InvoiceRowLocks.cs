using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;

namespace SMS.Modules.Finance.Repositories;

/// <summary>
/// One change at a time to a supplier invoice, for everything that changes one: its approval, rejection, reversal
/// and edits (<see cref="InvoiceRepository"/>), and the payments and credit/debit notes that settle or reduce it.
/// <para>
/// The <c>invoices</c> table has no concurrency token, and adding one is a model change (EF 9 then refuses to
/// migrate), so the lock is taken explicitly: inside one transaction, a no-op UPDATE of each invoice's row (EF's
/// ExecuteUpdate) takes its exclusive lock until commit, and the work then reads the invoice afresh. Any other
/// change to the same invoice waits at its own UPDATE until the first has committed, and then sees what it did.
/// </para>
/// <para>
/// <b>Lock order.</b> Invoices first, one statement per invoice in ascending Id — the one order every change that
/// touches several invoices (a multi-invoice payment) uses, so two of them cannot deadlock; then, for an approval or
/// reversal only, the purchase order's row. Nothing takes an invoice lock after a purchase order's.
/// </para>
/// <para>
/// Only on a relational provider: the in-memory one (unit tests) has neither transactions nor ExecuteUpdate, and
/// there the work simply runs.
/// </para>
/// </summary>
internal static class InvoiceRowLocks
{
    /// <summary>
    /// The invoices a change may touch: this organization's own, not deleted. A super admin reads every
    /// organization's invoices (the tenant filter lets them), but changing another organization's would book its
    /// ledger entries — stamped with the caller's organization — into the caller's own books.
    /// </summary>
    internal static IQueryable<Invoice> Own(FinanceDbContext db)
    {
        var organizationId = db.TenantContext.OrganizationId;
        return db.Invoices.Where(x => !x.IsDelete && x.OrganizationId == organizationId);
    }

    /// <summary>
    /// Runs <paramref name="work"/> in one transaction on SQL Server — inside the retrying execution strategy, as
    /// production configures every context — with each of <paramref name="joined"/> (e.g. Demand's context, which
    /// lives in the same database) on the same connection and transaction, so their saves commit or roll back with
    /// Finance's. A retried attempt starts again from what is committed. Inside a caller's transaction, or on the
    /// in-memory provider, the work just runs.
    /// </summary>
    internal static async Task<T> InTransactionAsync<T>(FinanceDbContext db, Func<Task<T>> work, params DbContext[] joined)
    {
        if (!db.Database.IsRelational() || db.Database.CurrentTransaction is not null)
            return await work();

        var attempt  = 0;
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            if (attempt++ > 0)
            {
                db.ChangeTracker.Clear();
                foreach (var other in joined) other.ChangeTracker.Clear();
            }

            await using var transaction = await db.Database.BeginTransactionAsync();
            var connection = db.Database.GetDbConnection();
            var enlisted   = new List<DbContext>();
            try
            {
                foreach (var other in joined.Where(o => o.Database.IsRelational()))
                {
                    if (!ReferenceEquals(other.Database.GetDbConnection(), connection))
                        other.Database.SetDbConnection(connection);
                    await other.Database.UseTransactionAsync(transaction.GetDbTransaction());
                    enlisted.Add(other);
                }

                var result = await work();
                await transaction.CommitAsync();
                return result;
            }
            finally
            {
                foreach (var other in enlisted) await other.Database.UseTransactionAsync(null);
            }
        });
    }

    /// <summary>
    /// Takes the exclusive row locks of these invoices — this organization's own, not deleted — one statement per
    /// invoice in ascending Id, and forgets any copy of them the context was tracking, so the work reads them afresh.
    /// Returns the ids of those found and locked; one that is missing (or another organization's) is the caller's to
    /// report. Must run inside <see cref="InTransactionAsync{T}"/> (a lock outside a transaction ends with its
    /// statement). On the in-memory provider it only finds them.
    /// </summary>
    internal static async Task<IReadOnlyList<int>> LockAsync(FinanceDbContext db, IEnumerable<Guid> invoiceUuids)
    {
        var uuids = invoiceUuids.Distinct().ToList();
        if (uuids.Count == 0) return [];

        // One equality lookup per uuid — an index seek. A Contains() over a list would be translated to a join on
        // OPENJSON, which SQL Server answers by scanning the table: under plain READ COMMITTED that scan waits on
        // every other invoice row some other change has locked.
        var ids = new List<int>(uuids.Count);
        foreach (var uuid in uuids)
            if (await Own(db).Where(x => x.UUID == uuid).Select(x => (int?)x.Id).FirstOrDefaultAsync() is { } id)
                ids.Add(id);
        ids.Sort();

        if (db.Database.IsRelational())
        {
            var locked = new List<int>(ids.Count);
            foreach (var id in ids)
            {
                var rows = await Own(db).Where(x => x.Id == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.MatchStatus, x => x.MatchStatus));
                if (rows > 0) locked.Add(id);
            }
            ids = locked;
        }

        Forget(db, uuids);
        return ids;
    }

    /// <summary>
    /// One invoice, by uuid: true when it is this organization's and is now locked. The lock is the first statement
    /// (an UPDATE by uuid — a seek), so nothing about the invoice is read before it is held.
    /// </summary>
    internal static async Task<bool> LockOneAsync(FinanceDbContext db, Guid invoiceUuid)
    {
        if (!db.Database.IsRelational())
            return await Own(db).AnyAsync(x => x.UUID == invoiceUuid);

        var rows = await Own(db).Where(x => x.UUID == invoiceUuid)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.MatchStatus, x => x.MatchStatus));

        Forget(db, [invoiceUuid]);
        return rows > 0;
    }

    /// <summary>Detaches any copy of these invoices the context was tracking, so the work reads them afresh.</summary>
    private static void Forget(FinanceDbContext db, IReadOnlyCollection<Guid> uuids)
    {
        foreach (var stale in db.ChangeTracker.Entries<Invoice>().Where(e => uuids.Contains(e.Entity.UUID)).ToList())
            stale.State = EntityState.Detached;
    }
}

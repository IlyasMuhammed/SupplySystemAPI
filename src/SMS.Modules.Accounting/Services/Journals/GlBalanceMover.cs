using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;

namespace SMS.Modules.Accounting.Services;

/// <summary>
/// A47 D-9 — the one place that moves <c>ChartOfAccounts.CurrentBalance</c> and sets <c>HasTransactions</c> (moved here from
/// A44's InternalGlPostingTarget; used by the internal GL target, the journal engine's post, the reversal and the year-end
/// close). A42 rule: the balance moves in the account's normal direction (debit-normal Asset/Expense: debit − credit; the
/// others: credit − debit). REV-1 — on SQL Server one <c>UPDATE … SET CurrentBalance = CurrentBalance + @delta</c> per
/// account inside the caller's transaction, so concurrent postings to the same account never lose an amount; tracked
/// account entities are not touched. The in-memory test provider has no ExecuteUpdate: there the tracked row is changed
/// (saved by the caller's SaveChanges). A-11 / D-10 — no trigger: a draft (<c>moveBalance</c> false) marks the accounts
/// used without moving them.
/// </summary>
internal sealed class GlBalanceMover
{
    private readonly AccountingDbContext _db;

    public GlBalanceMover(AccountingDbContext db) => _db = db;

    /// <summary>Moves (or, for a draft, only marks) every account of <paramref name="moves"/>, one statement per account.</summary>
    public async Task MoveAsync(IEnumerable<(int AccountId, string RootType, decimal Debit, decimal Credit)> moves, bool moveBalance,
        CancellationToken ct = default)
    {
        // REV-3 — ascending account id: concurrent posts lock the accounts in the same order (no deadlock).
        foreach (var g in moves.GroupBy(m => m.AccountId).OrderBy(g => g.Key))
        {
            var root = g.First().RootType;
            var delta = moveBalance ? g.Sum(m => Delta(root, m.Debit, m.Credit)) : 0m;
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

    /// <summary>
    /// Moves the accounts of a journal's lines (<paramref name="sign"/> −1 undoes them). The lines' accounts are read for
    /// their root type.
    /// </summary>
    public async Task MoveLinesAsync(IReadOnlyCollection<JournalEntryLine> lines, bool moveBalance, CancellationToken ct = default, int sign = 1)
    {
        if (lines.Count == 0) return;
        var ids = lines.Select(l => l.AccountId).Distinct().ToList();
        var roots = await _db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking().Where(a => ids.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.RootType, ct);
        await MoveAsync(lines.Select(l => sign >= 0
            ? (l.AccountId, roots[l.AccountId], l.DebitAmount, l.CreditAmount)
            : (l.AccountId, roots[l.AccountId], l.CreditAmount, l.DebitAmount)), moveBalance, ct);
    }

    /// <summary>The A42 normal-direction change of one line.</summary>
    internal static decimal Delta(string rootType, decimal debit, decimal credit) =>
        RootTypes.IsDebitNormal(rootType) ? debit - credit : credit - debit;
}

using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;

namespace SMS.Modules.Accounting.Services;

/// <summary>
/// A47 C8 (§11.4, D-16, contract §4) — the reconciliation engine shared by the reconciliation API (<see cref="ReconciliationService"/>)
/// and the reversal's auto-reconciliation (<see cref="JournalReversalService"/>, BR-RV-008). Like <see cref="JournalEntryWriter"/> it
/// never opens a transaction and never saves: the caller does both (and, on SQL Server, holds <see cref="LockAccountsAsync"/>).
/// <list type="bullet">
/// <item>Full (BR-RC-004/006): Σ residual within 0.01 of zero → every line residual 0, Reconciled, cleared.</item>
/// <item>Partial (BR-RC-005): matched = min(Σ debit residuals, −Σ credit residuals); the smaller side is cleared completely, the
/// larger side's lines are reduced oldest first (entry date, then line id). Every line keeps the group (one active group per line,
/// BR-RC-008); a line left with a residual stays open.</item>
/// <item>Cleared lines: clearing entry = the latest entry of the group (date, then id), clearing date = its entry date — or the
/// caller's override (a reversal: the reversal and its date).</item>
/// <item>Unreconcile (BR-RC-007): residual back to Debit − Credit, flags and clearing cleared, the group deleted.</item>
/// </list>
/// </summary>
internal sealed class JournalReconciler
{
    private readonly AccountingDbContext _db;
    private readonly IJournalEntrySequenceService _sequences;
    private readonly TimeProvider _clock;

    public JournalReconciler(AccountingDbContext db, IJournalEntrySequenceService sequences, TimeProvider? clock = null)
    {
        _db = db;
        _sequences = sequences;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>One line to reconcile with its (loaded) entry.</summary>
    internal readonly record struct Item(JournalEntryLine Line, JournalEntry Entry);

    /// <summary>What reconciling these residuals does: the type, the matched amount, each line's new residual (same order).</summary>
    internal sealed record Allocation(string ReconcileType, decimal Matched, IReadOnlyList<decimal> NewResiduals);

    /// <summary>
    /// The allocation of signed residuals (debit +); <paramref name="oldestFirst"/> lists their indexes oldest first (the order the
    /// larger side of a partial is used up in). Null when nothing offsets (all on one side, or all zero).
    /// </summary>
    internal static Allocation? Allocate(IReadOnlyList<decimal> residuals, IReadOnlyList<int> oldestFirst)
    {
        var debit = residuals.Where(r => r > 0).Sum();
        var credit = -residuals.Where(r => r < 0).Sum();
        if (debit == 0 || credit == 0) return null;

        if (Math.Abs(debit - credit) < JournalLimits.BalanceTolerance)
            return new Allocation(ReconcileTypes.Full, Math.Min(debit, credit), residuals.Select(_ => 0m).ToList());

        var matched = Math.Min(debit, credit);
        var largerIsDebit = debit > credit;
        var result = residuals.ToArray();
        var left = matched;
        foreach (var i in oldestFirst)
        {
            var r = residuals[i];
            if (largerIsDebit ? r < 0 : r > 0) { result[i] = 0m; continue; } // the smaller side is cleared
            if (r == 0) continue;
            var take = Math.Min(Math.Abs(r), left);
            left -= take;
            result[i] = largerIsDebit ? r - take : r + take;
        }
        return new Allocation(ReconcileTypes.Partial, matched, result);
    }

    /// <summary>Indexes of <paramref name="items"/> oldest first: entry date, then line id (new lines last, by entry).</summary>
    internal static IReadOnlyList<int> OldestFirst(IReadOnlyList<Item> items) =>
        Enumerable.Range(0, items.Count)
            .OrderBy(i => items[i].Entry.EntryDate)
            .ThenBy(i => items[i].Line.Id == 0 ? int.MaxValue : items[i].Line.Id)
            .ThenBy(i => items[i].Line.LineNumber)
            .ToList();

    /// <summary>
    /// Reconciles <paramref name="items"/> (already validated: one organization and account, posted, none in a group, ≥ 2 lines) into
    /// a new group (tracked, not saved). Null when nothing offsets. <paramref name="clearing"/> overrides the clearing entry/date.
    /// </summary>
    public async Task<ReconcileGroup?> ReconcileAsync(Guid organizationId, int accountId, IReadOnlyList<Item> items, int? userId, string? userName,
        (JournalEntry Entry, DateOnly Date)? clearing = null, CancellationToken ct = default)
    {
        var allocation = Allocate(items.Select(i => i.Line.AmountResidual).ToList(), OldestFirst(items));
        if (allocation is null) return null;

        var now = _clock.GetUtcNow().UtcDateTime;
        var latest = items.OrderByDescending(i => i.Entry.EntryDate).ThenByDescending(i => i.Entry.Id == 0 ? int.MaxValue : i.Entry.Id).First().Entry;
        var (clearingEntry, clearingDate) = clearing ?? (latest, latest.EntryDate);

        var group = new ReconcileGroup
        {
            OrganizationId = organizationId,
            GroupNumber    = await _sequences.NextReconcileGroupNumberAsync(organizationId, DateOnly.FromDateTime(now), ct),
            ReconcileType  = allocation.ReconcileType,
            AccountId      = accountId,
            Amount         = Math.Round(allocation.Matched, 4, MidpointRounding.AwayFromZero),
            CurrencyCode   = await BaseCurrencyAsync(organizationId, clearingDate, ct),
            ReconciledAt   = now,
            CreatedBy      = userId,
            CreatedByName  = TrimTo(userName, 200),
        };
        _db.ReconcileGroups.Add(group);

        for (var k = 0; k < items.Count; k++)
        {
            var line = items[k].Line;
            line.ReconcileGroup = group;
            line.AmountResidual = allocation.NewResiduals[k];
            line.Reconciled = line.AmountResidual == 0m;
            if (!line.Reconciled) continue;
            line.ClearingEntry = clearingEntry;
            line.ClearingDate = clearingDate;
        }
        return group;
    }

    /// <summary>
    /// BR-RV-008 (D-RR-4) — per account: the original's lines and their mirrors in one Full group, cleared by the reversal on
    /// <paramref name="date"/>; skipped for an account where an original line is already reconciled (it stays with its group, its
    /// mirror stays open). <paramref name="lines"/> are the original's lines (tracked) in the order they were mirrored: the writer
    /// numbers the mirror lines 1..n in that order. Shared by the manual reversal (RR) and the A44 gateway reversal (GW, C11).
    /// Not saved. Returns the group numbers.
    /// </summary>
    public async Task<List<string>> ReconcileReversalAsync(Guid organizationId, JournalEntry original, IReadOnlyList<JournalEntryLine> lines,
        JournalEntry reversal, DateOnly date, int? userId, string? userName, CancellationToken ct = default)
    {
        var byNumber = reversal.Lines.ToDictionary(l => l.LineNumber);
        var mirror = lines.Select((l, k) => (l, k)).ToDictionary(x => x.l, x => byNumber[x.k + 1]);
        var numbers = new List<string>();
        foreach (var account in lines.GroupBy(l => l.AccountId).OrderBy(g => g.Key))
        {
            if (account.Any(l => l.ReconcileGroupId is not null || l.Reconciled || l.AmountResidual != l.DebitAmount - l.CreditAmount)) continue;
            var items = account.Select(l => new Item(l, original)).Concat(account.Select(l => new Item(mirror[l], reversal))).ToList();
            var group = await ReconcileAsync(organizationId, account.Key, items, userId, userName, (reversal, date), ct);
            if (group is not null) numbers.Add(group.GroupNumber);
        }
        return numbers;
    }

    /// <summary>BR-RC-007 — the group's lines (tracked, loaded) are opened again and the group removed (not saved). Returns the line count.</summary>
    public int Unreconcile(ReconcileGroup group, IReadOnlyCollection<JournalEntryLine> lines)
    {
        foreach (var line in lines)
        {
            line.AmountResidual = line.DebitAmount - line.CreditAmount;
            line.Reconciled = false;
            line.ReconcileGroupId = null;
            line.ReconcileGroup = null;
            line.ClearingEntryId = null;
            line.ClearingEntry = null;
            line.ClearingDate = null;
        }
        group.Lines.Clear();
        _db.ReconcileGroups.Remove(group);
        return lines.Count;
    }

    /// <summary>
    /// SQL Server: an exclusive application lock per account for the rest of the transaction (ascending ids, so two writers never
    /// deadlock). Every reconcile, unreconcile and reversal takes it before reading the lines it changes.
    /// </summary>
    public async Task LockAccountsAsync(Guid organizationId, IEnumerable<int> accountIds, CancellationToken ct = default)
    {
        foreach (var id in accountIds.Distinct().OrderBy(i => i))
            await LockAsync($"glrec:{organizationId:N}:{id}", ct);
    }

    /// <summary>An exclusive transaction-owned application lock (SQL Server only; a no-op in memory).</summary>
    internal async Task LockAsync(string resource, CancellationToken ct)
    {
        if (!_db.Database.IsRelational()) return;
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"DECLARE @r int; EXEC @r = sp_getapplock @Resource = {resource}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 30000; IF @r < 0 THROW 51000, 'The reconciliation lock could not be taken.', 1;", ct);
    }

    /// <summary>The reporting currency of the fiscal year containing the date, else of the latest year.</summary>
    private async Task<string?> BaseCurrencyAsync(Guid org, DateOnly date, CancellationToken ct)
    {
        var years = _db.FiscalYears.IgnoreQueryFilters().AsNoTracking().Where(y => y.OrganizationId == org);
        var code = await years.Where(y => y.StartDate <= date && date <= y.EndDate).Select(y => y.ReportingCurrencyCode).FirstOrDefaultAsync(ct)
                   ?? await years.OrderByDescending(y => y.StartDate).Select(y => y.ReportingCurrencyCode).FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(code) ? null : code.Length <= 3 ? code : code[..3];
    }

    private static string? TrimTo(string? value, int max)
    {
        var t = value?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length <= max ? t : t[..max];
    }
}

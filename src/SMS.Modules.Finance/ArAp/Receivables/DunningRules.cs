namespace SMS.Modules.Finance.ArAp.Receivables;

/// <summary>One overdue receivable of a customer, as the dunning engine sees it (amounts in the base currency, D-13).</summary>
internal sealed record DunningOpenItem(
    Guid InvoiceUuid, string InvoiceNumber, DateTime InvoiceDate, DateTime DueDate,
    string CurrencyCode, decimal Open, decimal OpenBase);

/// <summary>A dunning level's timing, as the rules need it.</summary>
internal sealed record DunningLevelRule(int LevelNumber, int DaysAfterPrevious);

/// <summary>A procedure's timing and threshold.</summary>
internal sealed record DunningProcedureRule(int GraceDays, decimal MinimumAmount, IReadOnlyList<DunningLevelRule> Levels);

/// <summary>What the rules say about one customer on one day.</summary>
internal sealed record DunningEligibility(
    bool Eligible, int CurrentLevel, int ProposedLevel, string? SkipReason,
    IReadOnlyList<DunningOpenItem> OverdueItems, decimal OverdueBase, DateTime? OldestDueDate, int DaysOverdue);

/// <summary>
/// A48 C5 rules BR-DN-001..004 as pure functions (TS-DN-001..005). "Overdue" for dunning = open with
/// due date + grace days before the as-of day (BR-DN-001).
/// <list type="bullet">
/// <item>Level 0 → 1: the oldest such item is at least max(grace, level 1's days-after-due) days past due.</item>
/// <item>Level N → N+1: at least level N+1's days-after-previous days since the last dunning (BR-DN-003).</item>
/// <item>Never skips (BR-DN-004): the next level is always current + 1, and a procedure without that level stops there.</item>
/// <item>Skipped when the total overdue (base) is below the procedure's minimum (BR-DN-002).</item>
/// </list>
/// </summary>
internal static class DunningRules
{
    public static int DaysBetween(DateTime from, DateTime to) => (int)(to.Date - from.Date).TotalDays;

    public static DunningEligibility Evaluate(
        DunningProcedureRule procedure, int currentLevel, DateTime? lastDunningDate,
        IReadOnlyList<DunningOpenItem> openItems, DateTime asOf)
    {
        var day = asOf.Date;
        var overdue = openItems
            .Where(i => i.Open > 0m && i.DueDate.Date.AddDays(Math.Max(0, procedure.GraceDays)) < day)
            .OrderBy(i => i.DueDate).ThenBy(i => i.InvoiceNumber)
            .ToList();
        var total  = overdue.Sum(i => i.OpenBase);
        var oldest = overdue.Count == 0 ? (DateTime?)null : overdue[0].DueDate.Date;
        var days   = oldest is { } o ? DaysBetween(o, day) : 0;
        var level  = Math.Max(0, currentLevel);

        DunningEligibility Skip(string why, int proposed = 0) =>
            new(false, level, proposed, why, overdue, total, oldest, days);

        if (overdue.Count == 0)
            return Skip(procedure.GraceDays > 0
                ? $"Nothing is overdue beyond the {procedure.GraceDays}-day grace period."
                : "Nothing is overdue.");

        if (total < procedure.MinimumAmount)
            return Skip($"Total overdue {total:0.00} is below the procedure's minimum of {procedure.MinimumAmount:0.00}.");

        var next = level + 1;
        var rule = procedure.Levels.FirstOrDefault(l => l.LevelNumber == next);
        if (rule is null)
            return Skip(level == 0
                ? "The dunning procedure has no level 1."
                : $"Already at level {level}, the procedure's last level.");

        if (level == 0)
        {
            var needed = Math.Max(Math.Max(0, procedure.GraceDays), Math.Max(0, rule.DaysAfterPrevious));
            if (days < needed)
                return Skip($"Oldest item is {days} days overdue; level 1 starts at {needed} days.", next);
        }
        else
        {
            if (lastDunningDate is { } last)
            {
                var since = DaysBetween(last, day);
                if (since < rule.DaysAfterPrevious)
                    return Skip($"{since} days since the last dunning; level {next} needs {rule.DaysAfterPrevious}.", next);
            }
            // No last date while at a level (data from before A48 or a reset gone wrong): progress, never skip a level.
        }

        return new DunningEligibility(true, level, next, null, overdue, total, oldest, days);
    }
}

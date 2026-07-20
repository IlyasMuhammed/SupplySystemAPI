namespace SMS.Modules.Suppliers.Services;

// Resolves the most recently COMPLETED period for a given recalculation frequency, e.g. for a job
// running "daily" this is yesterday (not today's still-accumulating, partial data).
internal static class ScorecardPeriodResolver
{
    public static (DateTime Start, DateTime End) ResolvePreviousPeriod(string? frequency, DateTime now)
    {
        var today = now.Date;

        switch ((frequency ?? "DAILY").Trim().ToUpperInvariant())
        {
            case "WEEKLY":
                var startOfThisWeek = today.AddDays(-(int)today.DayOfWeek);
                var startOfLastWeek = startOfThisWeek.AddDays(-7);
                return (startOfLastWeek, startOfThisWeek);

            case "MONTHLY":
                var startOfThisMonth = new DateTime(today.Year, today.Month, 1);
                var startOfLastMonth = startOfThisMonth.AddMonths(-1);
                return (startOfLastMonth, startOfThisMonth);

            default: // DAILY
                return (today.AddDays(-1), today);
        }
    }
}

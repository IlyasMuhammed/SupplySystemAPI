namespace SMS.Modules.Finance.Services;

/// <summary>SFM-006 aging bucket boundaries (FSD Section 5) — days overdue relative to due_date.</summary>
internal static class AgingBucketCalculator
{
    internal const string Current      = "Current";
    internal const string Days31To60   = "31-60";
    internal const string Days61To90   = "61-90";
    internal const string Days91To120  = "91-120";
    internal const string Days120Plus  = "120+";

    internal static readonly string[] BucketOrder = [Current, Days31To60, Days61To90, Days91To120, Days120Plus];

    /// <summary>Negative when the invoice isn't due yet — callers should treat that as 0 for display.</summary>
    internal static int DaysOverdue(DateTime dueDate, DateTime today) => (int)(today.Date - dueDate.Date).TotalDays;

    internal static string BucketFor(int daysOverdue)
    {
        if (daysOverdue <= 30) return Current;
        if (daysOverdue <= 60) return Days31To60;
        if (daysOverdue <= 90) return Days61To90;
        if (daysOverdue <= 120) return Days91To120;
        return Days120Plus;
    }
}

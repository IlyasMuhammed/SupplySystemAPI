namespace SMS.Modules.Finance.ArAp.Payables;

/// <summary>FSD C15 DiscountCalculation (task 366).</summary>
public sealed record DiscountCalculation(
    decimal OriginalAmount, decimal DiscountPercent, decimal DiscountAmount, decimal NetPaymentAmount,
    DateOnly? DiscountDeadline, bool IsEligible, int DaysUntilDeadline);

/// <summary>
/// A48 C15 / C10 — the pure arithmetic of early-payment discounts and the payables cash-flow forecast (no database).
/// </summary>
internal static class PayablesMath
{
    /// <summary>
    /// BR-EP-001/002 — eligible only when the payment date is on or before the discount deadline (invoice date +
    /// discount days, stored on the schedule as DiscountDate); the discount is amount × percent / 100, rounded to the
    /// cent. The amount is what is still owed (a partly paid invoice earns the discount on its remainder, D-AP-6).
    /// </summary>
    public static DiscountCalculation Discount(decimal amount, decimal? percent, DateOnly? deadline, DateOnly paymentDate)
    {
        var pct = percent ?? 0m;
        var days = deadline is { } d ? d.DayNumber - paymentDate.DayNumber : 0;
        var eligible = deadline is not null && pct > 0m && amount > 0m && paymentDate <= deadline.Value;
        var discount = eligible ? Money(amount * pct / 100m) : 0m;
        return new DiscountCalculation(amount, pct, discount, amount - discount, deadline, eligible, days);
    }

    /// <summary>BR-EP-004 + the term's own sanity: 0 &lt; percent &lt; 100, 0 ≤ discount days &lt; net days.</summary>
    public static string? TermProblem(decimal percent, int discountDays, int netDays)
    {
        if (percent <= 0m || percent >= 100m) return "The discount percent must be greater than 0 and less than 100.";
        if (discountDays < 0) return "Discount days cannot be negative.";
        if (netDays <= 0) return "Net days must be greater than 0.";
        if (discountDays >= netDays) return "Discount days must be fewer than net days.";
        return null;
    }

    /// <summary>The discount deadline of an invoice under a term: invoice date + discount days.</summary>
    public static DateOnly DiscountDate(DateOnly invoiceDate, int discountDays) => invoiceDate.AddDays(discountDays);

    /// <summary>The Monday on or before <paramref name="date"/> — forecast weeks run Monday to Sunday (ISO).</summary>
    public static DateOnly WeekStart(DateOnly date)
    {
        var offset = ((int)date.DayOfWeek + 6) % 7; // Monday = 0
        return date.AddDays(-offset);
    }

    /// <summary>
    /// Which forecast week (0-based) a due date falls in, counting from the week of <paramref name="from"/>; −1 when it
    /// is before that week (overdue), <paramref name="weeks"/> when it is after the horizon.
    /// </summary>
    public static int WeekIndex(DateOnly due, DateOnly from, int weeks)
    {
        var start = WeekStart(from);
        if (due < start) return -1;
        var index = (due.DayNumber - start.DayNumber) / 7;
        return Math.Min(index, weeks);
    }

    internal static decimal Money(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}

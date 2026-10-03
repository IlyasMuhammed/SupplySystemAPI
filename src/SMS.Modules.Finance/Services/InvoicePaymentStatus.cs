namespace SMS.Modules.Finance.Services;

/// <summary>
/// SFM-004 payment-status derivation (FSD Section 4) — UNPAID | PARTIALLY_PAID | FULLY_PAID |
/// OVERPAID, based on Invoice.PaidAmount vs Invoice.TotalAmount. Written by the SupplierPayment
/// posting flow. The legacy single-invoice Payment flow still writes the older
/// Unpaid/Partial/Paid/Overdue vocabulary to the same field — <see cref="IsFullyPaid"/> treats
/// both "Paid" and "FULLY_PAID" as fully paid so existing "is this invoice settled?" checks stay
/// correct regardless of which flow last touched the invoice.
/// </summary>
internal static class InvoicePaymentStatus
{
    internal const string Unpaid        = "UNPAID";
    internal const string PartiallyPaid = "PARTIALLY_PAID";
    internal const string FullyPaid     = "FULLY_PAID";
    internal const string Overpaid      = "OVERPAID";

    internal static string Derive(decimal paidAmount, decimal totalAmount)
    {
        if (paidAmount <= 0m) return Unpaid;
        if (paidAmount < totalAmount) return PartiallyPaid;
        if (paidAmount == totalAmount) return FullyPaid;
        return Overpaid;
    }

    internal static bool IsFullyPaid(string? status) => status is "Paid" or FullyPaid;

    /// <summary>Settled, in either vocabulary — or more than settled: nothing more can be paid on it.</summary>
    internal static bool IsSettled(string? status) => IsFullyPaid(status) || status is Overpaid;

    /// <summary>
    /// The payment status once the money is counted from both flows: what supplier payments posted
    /// (<paramref name="postedPaid"/>, Invoice.PaidAmount) and legacy single-invoice payments not reversed
    /// (<paramref name="legacyPaid"/>, which never reach PaidAmount). In the SFM-004 vocabulary as soon as a supplier
    /// payment is involved; otherwise in the legacy one (Unpaid/Partial/Paid) the legacy flow has always written.
    /// </summary>
    internal static string AfterSettlement(decimal postedPaid, decimal legacyPaid, decimal totalAmount)
    {
        if (postedPaid > 0m) return Derive(postedPaid + legacyPaid, totalAmount);
        return legacyPaid >= totalAmount ? "Paid" : legacyPaid > 0m ? "Partial" : "Unpaid";
    }
}

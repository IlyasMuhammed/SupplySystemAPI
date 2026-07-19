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
}

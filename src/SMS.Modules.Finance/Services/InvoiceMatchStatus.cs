namespace SMS.Modules.Finance.Services;

/// <summary>
/// The supplier invoice's <c>MatchStatus</c> vocabulary — a plain string column. Pending, Matched and
/// Variance are what the three-way match says (S-8: on the net Subtotal); Approved books the invoice to
/// the supplier ledger; Rejected and Reversed are final.
/// </summary>
internal static class InvoiceMatchStatus
{
    internal const string Pending  = "Pending";
    internal const string Matched  = "Matched";
    internal const string Variance = "Variance";
    internal const string Approved = "Approved";
    internal const string Rejected = "Rejected";

    /// <summary>
    /// SAP alignment (S-7): an approved invoice whose ledger debit was cancelled by an opposite entry
    /// (INVOICE_REVERSED). Nothing about the invoice itself was edited.
    /// </summary>
    internal const string Reversed = "Reversed";

    internal static readonly IReadOnlyList<string> All = [Pending, Matched, Variance, Approved, Rejected, Reversed];

    internal static bool Is(string? status, string value) =>
        string.Equals(status?.Trim(), value, StringComparison.OrdinalIgnoreCase);

    /// <summary>The statuses an invoice may be approved from — never twice, never once rejected or reversed.</summary>
    internal static bool IsApprovable(string? status) =>
        Is(status, Pending) || Is(status, Matched) || Is(status, Variance);

    /// <summary>The canonical spelling of a known status, or null when it is not one.</summary>
    internal static string? Canonical(string? status) => All.FirstOrDefault(v => Is(status, v));
}

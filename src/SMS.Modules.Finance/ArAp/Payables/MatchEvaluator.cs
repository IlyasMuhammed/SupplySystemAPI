namespace SMS.Modules.Finance.ArAp.Payables;

/// <summary>A48 C9 — the line and header statuses of a three-way (or two-way) match (FSD §C9, D-7).</summary>
internal static class MatchStatuses
{
    // Header
    public const string Pending      = "Pending";
    public const string Matched      = "Matched";
    public const string PartialMatch = "PartialMatch";
    public const string Mismatch     = "Mismatch";
    public const string Overridden   = "Overridden";
    public const string Approved     = "Approved";
    public const string Cancelled    = "Cancelled";

    // Line (Pending / Matched / Overridden shared with the header)
    public const string QuantityMismatch = "QuantityMismatch";
    public const string PriceMismatch    = "PriceMismatch";
    /// <summary>No PO line stands behind the invoice line (API-CONTRACT §0.1).</summary>
    public const string Unmatched        = "Unmatched";

    public const string ThreeWay = "ThreeWay";
    public const string TwoWay   = "TwoWay";

    /// <summary>A header that may be overridden (BR-MT-006 / task 322).</summary>
    public static bool IsOverridable(string status) => status is Mismatch or PartialMatch;

    /// <summary>A header that may be approved (task 326, BR-MT-008).</summary>
    public static bool IsApprovable(string status) => status is Matched or Overridden;
}

/// <summary>The tolerance a match is judged by (a MatchToleranceRule, vendor-specific or the org default).</summary>
/// <param name="PricePercent">BR-MT-003: |invoice − PO price| / PO price ≤ this % …</param>
/// <param name="PriceAmount">… OR |invoice − PO price| ≤ this amount (per unit).</param>
/// <param name="QuantityPercent">BR-MT-004: invoice qty may exceed the GRN (PO) qty by at most this %.</param>
/// <param name="TotalPercent">Header: |Σ invoice − Σ expected| / Σ expected ≤ this % … (0 and 0 = not checked, D-AP-3).</param>
/// <param name="TotalAmount">… OR ≤ this amount.</param>
internal sealed record MatchTolerance(
    decimal PricePercent, decimal PriceAmount, decimal QuantityPercent,
    decimal TotalPercent, decimal TotalAmount, bool AutoApprove, int? RuleId = null, string? RuleName = null)
{
    /// <summary>When an organization has no rule at all: exact match only, never auto-approve.</summary>
    public static readonly MatchTolerance Exact = new(0m, 0m, 0m, 0m, 0m, false);
}

/// <summary>One invoice line as the match sees it, with what stands behind it on the PO and the GRN.</summary>
/// <param name="ReferenceQuantity">
/// What the invoice quantity is compared with: the GRN's accepted quantity (3-way) or the PO quantity not yet invoiced
/// (2-way). Null when no PO line stands behind the invoice line.
/// </param>
internal sealed record MatchLineInput(
    int LineNumber, string ItemDescription, Guid? InvoiceLineUuid, Guid? PoLineUuid, Guid? GrnLineUuid,
    decimal PoQuantity, decimal PoUnitPrice, decimal? GrnQuantity, decimal? ReferenceQuantity,
    decimal InvoiceQuantity, decimal InvoiceUnitPrice);

internal sealed record MatchLineResult(
    MatchLineInput Input, decimal PoLineTotal, decimal InvoiceLineTotal, decimal ExpectedLineTotal,
    decimal QuantityVariance, decimal PriceVariance, decimal TotalVariance,
    bool QuantityWithin, bool PriceWithin, string Status);

internal sealed record MatchEvaluation(
    IReadOnlyList<MatchLineResult> Lines, decimal TotalPo, decimal? TotalGrn, decimal TotalInvoice, decimal TotalExpected,
    decimal VarianceAmount, decimal? VariancePercent, bool WithinTolerance, string Status);

/// <summary>
/// A48 C9 (tasks 298–305, BR-MT-001..004) — the pure comparison: no database, so every tolerance rule is unit-testable.
/// <list type="bullet">
/// <item>Quantity (BR-MT-004): invoice qty ≤ reference qty × (1 + qty% / 100). Billing less than was received is fine
/// (a partial invoice, TS-MT-010).</item>
/// <item>Price (BR-MT-003): |invoice price − PO price| / PO price ≤ price% OR |difference| ≤ price amount.</item>
/// <item>Line status: QuantityMismatch wins over PriceMismatch; else Matched.</item>
/// <item>Header: Matched when every line is; Mismatch when none is, or when the header total is outside the total
/// tolerance (if one is set); otherwise PartialMatch.</item>
/// </list>
/// Variances: quantity = invoice − reference; price = invoice − PO price; total = invoice line total − invoice qty × PO
/// price (what this invoice should bill, as the existing match does — never the whole PO line, D-AP-2).
/// </summary>
internal static class MatchEvaluator
{
    public static MatchLineResult EvaluateLine(MatchLineInput line, MatchTolerance tolerance)
    {
        var invoiceTotal  = Money(line.InvoiceQuantity * line.InvoiceUnitPrice);
        var poLineTotal   = Money(line.PoQuantity * line.PoUnitPrice);
        var expectedTotal = Money(line.InvoiceQuantity * line.PoUnitPrice);

        var reference   = line.ReferenceQuantity;
        var qtyVariance = line.InvoiceQuantity - (reference ?? 0m);
        var qtyWithin   = reference is { } r && QuantityWithin(line.InvoiceQuantity, r, tolerance.QuantityPercent);

        var priceVariance = line.InvoiceUnitPrice - line.PoUnitPrice;
        var priceWithin   = reference is not null && PriceWithin(line.InvoiceUnitPrice, line.PoUnitPrice, tolerance);

        var status = reference is null ? MatchStatuses.Unmatched
                   : !qtyWithin ? MatchStatuses.QuantityMismatch
                   : !priceWithin ? MatchStatuses.PriceMismatch
                   : MatchStatuses.Matched;

        return new MatchLineResult(line, poLineTotal, invoiceTotal, expectedTotal,
            qtyVariance, priceVariance, invoiceTotal - expectedTotal, qtyWithin, priceWithin, status);
    }

    /// <summary>BR-MT-004.</summary>
    public static bool QuantityWithin(decimal invoiceQty, decimal referenceQty, decimal tolerancePercent) =>
        invoiceQty <= referenceQty + referenceQty * Math.Max(0m, tolerancePercent) / 100m;

    /// <summary>BR-MT-003 (either test passes the line).</summary>
    public static bool PriceWithin(decimal invoicePrice, decimal poPrice, MatchTolerance tolerance)
    {
        var diff = Math.Abs(invoicePrice - poPrice);
        if (diff <= Math.Max(0m, tolerance.PriceAmount)) return true;
        return poPrice > 0m && diff / poPrice * 100m <= Math.Max(0m, tolerance.PricePercent);
    }

    public static MatchEvaluation Evaluate(IReadOnlyList<MatchLineInput> lines, MatchTolerance tolerance, bool twoWay)
    {
        var results = lines.Select(l => EvaluateLine(l, tolerance)).ToList();

        var totalInvoice  = results.Sum(r => r.InvoiceLineTotal);
        var totalExpected = results.Sum(r => r.ExpectedLineTotal);
        var totalPo       = results.Where(r => r.Input.PoLineUuid is not null).GroupBy(r => r.Input.PoLineUuid).Sum(g => g.First().PoLineTotal);
        decimal? totalGrn = twoWay ? null : Money(results.Where(r => r.Input.GrnQuantity is not null)
                                                       .Sum(r => r.Input.GrnQuantity!.Value * r.Input.PoUnitPrice));

        var variance         = totalInvoice - totalExpected;
        decimal? variancePct = totalExpected > 0m ? Math.Round(variance / totalExpected * 100m, 2) : null;
        var totalWithin      = TotalWithin(totalInvoice, totalExpected, tolerance);

        var matched = results.Count(r => r.Status == MatchStatuses.Matched);
        var within  = results.Count > 0 && matched == results.Count && totalWithin;

        var status = results.Count == 0           ? MatchStatuses.Mismatch
                   : within                       ? MatchStatuses.Matched
                   : matched == 0 || !totalWithin ? MatchStatuses.Mismatch
                   : MatchStatuses.PartialMatch;

        return new MatchEvaluation(results, totalPo, totalGrn, totalInvoice, totalExpected, variance, variancePct, within, status);
    }

    /// <summary>The header check. With neither a total % nor a total amount set it is not applied (D-AP-3).</summary>
    public static bool TotalWithin(decimal invoiceTotal, decimal expectedTotal, MatchTolerance tolerance)
    {
        if (tolerance.TotalPercent <= 0m && tolerance.TotalAmount <= 0m) return true;
        var diff = Math.Abs(invoiceTotal - expectedTotal);
        if (tolerance.TotalAmount > 0m && diff <= tolerance.TotalAmount) return true;
        return tolerance.TotalPercent > 0m && expectedTotal > 0m && diff / expectedTotal * 100m <= tolerance.TotalPercent;
    }

    internal static decimal Money(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}

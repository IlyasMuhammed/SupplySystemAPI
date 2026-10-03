using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Core.Sync;

/// <summary>
/// Sales-invoice arithmetic, shared by validation and the object builder so the two can never disagree.
/// <para>
/// <b>The caller's side</b> (SCM's <c>SalesInvoiceTotals.Header</c>) rounds <i>once</i> over the whole
/// invoice: subtotal = round(Σ qty × price), discount = round(Σ qty × price × disc%) + header discount,
/// grand total = subtotal − discount + tax. That is what the payload's <c>ExpectedTotal</c> is checked against.
/// </para>
/// <para>
/// <b>QuickBooks' side</b> validates every line's Amount against Qty × UnitPrice, so each line goes at
/// round(qty × price) and the lines sum to <see cref="LineAmounts"/> — which can differ from the caller's
/// subtotal by a cent or two. Plan D-5's single discount line absorbs that: it is the residue
/// <c>LineAmounts + tax − ExpectedTotal</c>, so QuickBooks' total equals the caller's (given QuickBooks'
/// tax matches). With no discount on the invoice no discount line is invented; a residual cent is
/// reported as a warning instead.
/// </para>
/// </summary>
internal sealed record InvoiceMath(
    decimal LineAmounts,
    decimal Subtotal,
    decimal Discount,
    decimal ComputedTotal,
    bool    HasDiscount,
    decimal DiscountLine,
    decimal QuickBooksDifference)
{
    public const decimal Tolerance = 0.01m;

    public static InvoiceMath Of(SalesInvoicePayload p)
    {
        decimal gross = 0m, discount = 0m, lineAmounts = 0m;
        var hasDiscount = p.HeaderDiscountAmount > 0;

        foreach (var line in p.Lines ?? [])
        {
            var lineGross = line.Quantity * line.UnitPrice;
            gross       += lineGross;
            discount    += lineGross * line.DiscountPercent / 100m;
            lineAmounts += SyncPayloads.Money(lineGross);
            if (line.DiscountPercent > 0) hasDiscount = true;
        }

        var subtotal      = SyncPayloads.Money(gross);
        var totalDiscount = SyncPayloads.Money(discount) + p.HeaderDiscountAmount;
        var computed      = subtotal - totalDiscount + p.ExpectedTaxAmount;

        var residue      = lineAmounts + p.ExpectedTaxAmount - p.ExpectedTotal;
        var discountLine = hasDiscount ? SyncPayloads.Money(Math.Max(residue, 0m)) : 0m;
        var difference   = lineAmounts - discountLine + p.ExpectedTaxAmount - p.ExpectedTotal;

        return new InvoiceMath(lineAmounts, subtotal, totalDiscount, computed, hasDiscount, discountLine, difference);
    }

    /// <summary>The caller's lines agree with its own total (to the cent).</summary>
    public bool AddsUp(SalesInvoicePayload p) => Math.Abs(ComputedTotal - p.ExpectedTotal) <= Tolerance;

    /// <summary>QuickBooks' total (lines − discount line + tax) will not match the caller's to the cent.</summary>
    public bool QuickBooksTotalDiffers => Math.Abs(QuickBooksDifference) >= Tolerance;
}

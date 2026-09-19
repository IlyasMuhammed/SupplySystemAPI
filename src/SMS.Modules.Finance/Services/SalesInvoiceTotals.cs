namespace SMS.Modules.Finance.Services;

/// <summary>
/// Sale invoice arithmetic, identical to how the sale order computes its own totals (A29 §4.1/§4.2),
/// so billing the whole of an order produces exactly the order's grand total — never a cent adrift
/// because two modules rounded differently.
/// </summary>
internal static class SalesInvoiceTotals
{
    /// <summary>§4.2 — <c>line_total = qty × price × (1 − disc%) × (1 + tax%)</c>, 2dp away from zero.</summary>
    public static decimal LineTotal(decimal quantity, decimal unitPrice, decimal discountPercent, decimal taxPercent) =>
        Math.Round(
            quantity * unitPrice * (1 - discountPercent / 100m) * (1 + taxPercent / 100m),
            2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// §4.1 — <c>grand_total = subtotal − discount + tax</c>, where each of the three is the sum, over
    /// the lines, of the same quantity every line's own total was built from, rounded once.
    /// </summary>
    public static (decimal Subtotal, decimal Discount, decimal Tax, decimal Grand) Header(
        IEnumerable<(decimal Quantity, decimal UnitPrice, decimal DiscountPercent, decimal TaxPercent)> lines)
    {
        decimal subtotal = 0, discount = 0, tax = 0;

        foreach (var (quantity, unitPrice, discountPercent, taxPercent) in lines)
        {
            var gross         = quantity * unitPrice;
            var lineDiscount  = gross * discountPercent / 100m;
            var afterDiscount = gross - lineDiscount;
            var lineTax       = afterDiscount * taxPercent / 100m;

            subtotal += gross;
            discount += lineDiscount;
            tax      += lineTax;
        }

        subtotal = Math.Round(subtotal, 2, MidpointRounding.AwayFromZero);
        discount = Math.Round(discount, 2, MidpointRounding.AwayFromZero);
        tax      = Math.Round(tax,      2, MidpointRounding.AwayFromZero);

        return (subtotal, discount, tax, subtotal - discount + tax);
    }
}

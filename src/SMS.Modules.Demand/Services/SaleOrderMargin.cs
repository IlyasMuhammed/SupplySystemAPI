namespace SMS.Modules.Demand.Services;

/// <summary>
/// A29-P5-10 §14.2 — <c>margin = SO_line.unit_price − PO_line.unit_price</c>,
/// <c>margin_percent = margin / SO_line.unit_price × 100</c>. Per unit, informational (it feeds the
/// Margin Analysis report), stored on the sale order line.
/// <para>
/// The selling price here is the line's resolved <c>UnitPrice</c>, before any line discount or tax —
/// the spec's own wording — so a discounted line's real margin is lower than this figure says.
/// </para>
/// </summary>
internal static class SaleOrderMargin
{
    // SaleOrderLine.MarginPercent is decimal(5,2). A cost more than ten times the selling price is
    // already a pricing error, but it must not be one that makes the whole save fail: it is clamped
    // to the column's range instead, which still reads plainly as "far below zero".
    private const decimal PercentLimit = 999.99m;

    /// <returns>The percentage is null when there is no selling price to take a percentage of.</returns>
    public static (decimal Margin, decimal? Percent) Compute(decimal sellingPrice, decimal purchasePrice)
    {
        var raw    = sellingPrice - purchasePrice;
        var margin = Math.Round(raw, 2, MidpointRounding.AwayFromZero);
        if (sellingPrice <= 0m) return (margin, null);

        var percent = Math.Round(raw / sellingPrice * 100m, 2, MidpointRounding.AwayFromZero);
        return (margin, Math.Clamp(percent, -PercentLimit, PercentLimit));
    }
}

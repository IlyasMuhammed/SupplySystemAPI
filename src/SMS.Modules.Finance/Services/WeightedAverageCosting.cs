namespace SMS.Modules.Finance.Services;

/// <summary>What one ledger entry moved, and where the variant stands after it.</summary>
internal readonly record struct CostingStep(decimal UnitCost, decimal TotalCost, decimal RunningQty, decimal RunningValue);

/// <summary>
/// A29 §11.3 — the weighted-average cost arithmetic of the product ledger, with no database in it.
/// <para>
/// The running value is kept to two decimal places and every entry's total cost is rounded to two before
/// it is added or taken off, so <c>running value = previous ± total cost</c> holds exactly, entry after
/// entry, and the value of a variant is always the sum of what the ledger says moved.
/// </para>
/// </summary>
internal static class WeightedAverageCosting
{
    /// <summary>Money is rounded the way the invoices round it: half away from zero.</summary>
    internal static decimal Money(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary><c>value / qty</c> to the four places a unit cost is kept to; nothing on hand has no cost.</summary>
    internal static decimal Wac(decimal qty, decimal value) =>
        qty <= 0m ? 0m : Math.Round(value / qty, 4, MidpointRounding.AwayFromZero);

    /// <summary>§11.3, IN: <c>running_qty += qty; running_value += qty × unit_cost</c>.</summary>
    internal static CostingStep In(decimal previousQty, decimal previousValue, decimal qty, decimal unitCost)
    {
        var total = Money(qty * unitCost);
        return new CostingStep(unitCost, total, previousQty + qty, previousValue + total);
    }

    /// <summary>
    /// §11.3, OUT: each unit leaves at the current weighted-average cost. The caller has checked that the
    /// ledger holds <paramref name="qty"/>. Taking out <i>everything</i> takes out the whole value, so a
    /// variant that has been sold down to nothing is worth exactly nothing and not a rounding residue.
    /// </summary>
    internal static CostingStep Out(decimal previousQty, decimal previousValue, decimal qty)
    {
        var total = qty == previousQty
            ? previousValue
            : Money(qty * previousValue / previousQty);

        return new CostingStep(Wac(previousQty, previousValue), total, previousQty - qty, previousValue - total);
    }
}

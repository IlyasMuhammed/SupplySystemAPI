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

    /// <summary>
    /// A38 (FIN, D-9/D-17) — an OUT larger than the running quantity (POS counter sales only). The units the ledger holds
    /// leave with all of their value; the units it does not hold (the shortfall) leave at the current weighted-average cost —
    /// <c>value / qty</c> while something is on hand, else <paramref name="lastUnitCost"/> (the unit cost of the variant's
    /// latest entry: the last receipt's cost or the cost the last sale left at; 0 when the variant has no history).
    /// The running quantity goes negative and the running value with it (−shortfall × cost), so
    /// <c>running value = previous − total cost</c> still holds exactly.
    /// <para>
    /// <b>A later IN onto a negative quantity</b> uses <see cref="In"/> unchanged: <c>running value += qty × receipt cost</c>.
    /// The difference between the provisional cost the short units left at and what they really cost stays in the running
    /// value and is released through the WAC of the next sales — value is conserved (every unit of cost received leaves as
    /// cost of sales exactly once) and an IN entry's total cost is still exactly what was received.
    /// </para>
    /// </summary>
    internal static CostingStep OutAllowingNegative(decimal previousQty, decimal previousValue, decimal qty, decimal lastUnitCost)
    {
        if (qty <= previousQty) return Out(previousQty, previousValue, qty);

        var unit = previousQty > 0m ? Wac(previousQty, previousValue) : Math.Max(0m, lastUnitCost);
        if (unit < 0m) unit = 0m;

        var covered   = previousQty > 0m ? previousQty : 0m;
        var shortfall = qty - covered;
        var total     = (covered > 0m ? previousValue : 0m) + Money(shortfall * unit);

        return new CostingStep(unit, total, previousQty - qty, previousValue - total);
    }
}

using SMS.Modules.Finance.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// What must hold of one variant's product ledger after <i>any</i> sequence of receipts, sales, returns,
/// adjustments and write-offs — stated from §11.3 and from the arithmetic of money, not by re-running the
/// service's own formula, so that a mistake in the formula cannot also be a mistake in the check. Returned
/// as a list of what is wrong so that a failing test can say exactly which rule broke, and at which entry.
/// <para>
/// Costs are compared to the exact average within the rounding the columns impose: a cent on a total, and
/// half a hundredth of a cent on a unit cost.
/// </para>
/// </summary>
internal static class ProductLedgerInvariants
{
    // Half a cent is the most a rounded total can be from the exact figure; the extra sliver is what a
    // 28-digit division leaves behind on a figure that is exactly half a cent (78099.045000…001).
    private const decimal Cent     = 0.005m + 0.000000001m;
    private const decimal UnitCost = 0.00005m + 0.000000001m;

    /// <param name="entries">One variant's entries in one organization, in the order they were written.</param>
    public static IReadOnlyList<string> Violations(IReadOnlyList<ProductLedgerEntry> entries)
    {
        var bad   = new List<string>();
        var qty   = 0m;
        var value = 0m;
        var seq   = 1;

        foreach (var e in entries)
        {
            var at = $"entry {e.SequenceNo} ({e.EntryType} {e.Direction} {e.Quantity:0.####})";

            if (e.SequenceNo != seq) bad.Add($"{at}: the sequence should be {seq}");
            seq++;

            var incoming = e.Direction == ProductLedgerDirections.In;
            if (!incoming && e.Direction != ProductLedgerDirections.Out) bad.Add($"{at}: a direction that is neither IN nor OUT");
            if (e.Quantity <= 0m) bad.Add($"{at}: a quantity of {e.Quantity}");

            if (incoming)
            {
                // §11.3, IN: the value added is the quantity times the cost it came in at.
                if (Math.Abs(e.TotalCost - e.Quantity * e.UnitCost) > Cent)
                    bad.Add($"{at}: it added {e.TotalCost}, but {e.Quantity} at {e.UnitCost} is {e.Quantity * e.UnitCost}");
            }
            else if (e.Quantity > qty)
            {
                bad.Add($"{at}: it took out more than the {qty} that was held");
            }
            else
            {
                // §11.3, OUT: each unit leaves at the average of the moment.
                var average = value / qty;

                if (Math.Abs(e.UnitCost - average) > UnitCost)
                    bad.Add($"{at}: it left at {e.UnitCost}, but the average was {average}");
                if (Math.Abs(e.TotalCost - e.Quantity * average) > Cent)
                    bad.Add($"{at}: it took out {e.TotalCost} of value, but {e.Quantity} at an average of {average} is {e.Quantity * average}");
                if (e.Quantity == qty && e.TotalCost != value)
                    bad.Add($"{at}: it took out all {qty} but only {e.TotalCost} of the {value}");
            }

            // The running totals are the ones before, plus or minus what moved — exactly.
            var sign = incoming ? 1m : -1m;
            if (e.RunningQty != qty + sign * e.Quantity)
                bad.Add($"{at}: running quantity is {e.RunningQty}, but {qty} {(incoming ? "+" : "−")} {e.Quantity} is {qty + sign * e.Quantity}");
            if (e.RunningValue != value + sign * e.TotalCost)
                bad.Add($"{at}: running value is {e.RunningValue}, but {value} {(incoming ? "+" : "−")} {e.TotalCost} is {value + sign * e.TotalCost}");

            if (e.RunningQty < 0m || e.RunningValue < 0m) bad.Add($"{at}: it left {e.RunningQty} worth {e.RunningValue}; neither can be negative");
            if (e.RunningQty == 0m && e.RunningValue != 0m) bad.Add($"{at}: no stock but {e.RunningValue} of value");

            if (!incoming && qty > 0m && e.Quantity < qty)
            {
                // A sale does not move the average: what is left is worth what it was worth, to the cent.
                var expected = e.RunningQty * (value / qty);
                if (Math.Abs(e.RunningValue - expected) > Cent)
                    bad.Add($"{at}: it changed the average — {e.RunningQty} left should be worth {expected}, not {e.RunningValue}");
            }

            if (incoming && qty > 0m && e.RunningQty > 0m)
            {
                // New stock pulls the average toward its own cost, never past it.
                var before = value / qty;
                var after  = e.RunningValue / e.RunningQty;
                var slack  = Cent / e.RunningQty;
                if (after < Math.Min(before, e.UnitCost) - slack || after > Math.Max(before, e.UnitCost) + slack)
                    bad.Add($"{at}: the average went from {before} to {after}, outside {before} and the {e.UnitCost} it came in at");
            }

            (qty, value) = (e.RunningQty, e.RunningValue);
        }

        return bad;
    }
}

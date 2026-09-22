namespace SMS.Modules.Material.Services;

/// <summary>
/// What the shared reservation ledger holds for one line of a material issue request, and where a voucher
/// should take a quantity from it.
/// <para>
/// A line's stock is not always on one shelf: the ledger can hold it across several rows (bins, batches), so
/// the hold is a list, not a single figure. A voucher line issues from one stock row, or from a row per hold
/// when the quantity spans them, and it must take from the holds <b>in the order the ledger consumes them</b>
/// when the voucher posts (oldest hold first). Otherwise the units that leave one row would not be the units
/// the ledger stops holding, and the two rows' reserved counters would drift apart.
/// </para>
/// </summary>
internal static class MirLineHolds
{
    /// <summary>One active hold: the stock row it sits on, and how much of it is still held.</summary>
    internal readonly record struct Held(int InventoryItemId, decimal Quantity);

    /// <summary>How much of the line is held in all.</summary>
    internal static decimal Total(IEnumerable<Held> holds) => holds.Sum(h => h.Quantity);

    /// <summary>
    /// Where <paramref name="quantity"/> comes from: the holds in the order given, each taken as far as it goes,
    /// one entry for each stock row (two holds on the same row are one source). Asking for more than is held
    /// returns everything held; asking for nothing returns nothing.
    /// </summary>
    internal static IReadOnlyList<Held> Take(IEnumerable<Held> holdsInConsumptionOrder, decimal quantity)
    {
        var sources   = new List<Held>();
        var remaining = quantity;

        foreach (var hold in holdsInConsumptionOrder)
        {
            if (remaining <= 0) break;

            var take = Math.Min(remaining, hold.Quantity);
            if (take <= 0) continue;
            remaining -= take;

            var existing = sources.FindIndex(s => s.InventoryItemId == hold.InventoryItemId);
            if (existing >= 0) sources[existing] = sources[existing] with { Quantity = sources[existing].Quantity + take };
            else sources.Add(new Held(hold.InventoryItemId, take));
        }

        return sources;
    }
}

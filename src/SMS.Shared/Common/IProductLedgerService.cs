using System.Data.Common;

namespace SMS.Shared.Common;

/// <summary>A29 §11.1's closed vocabulary of product ledger entry kinds.</summary>
public static class ProductLedgerEntryTypes
{
    public const string Purchase   = "PURCHASE";
    public const string Sale       = "SALE";
    public const string ReturnIn   = "RETURN_IN";
    public const string ReturnOut  = "RETURN_OUT";
    public const string Adjustment = "ADJUSTMENT";
    public const string WriteOff   = "WRITE_OFF";

    public static readonly IReadOnlyList<string> All =
        [Purchase, Sale, ReturnIn, ReturnOut, Adjustment, WriteOff];
}

public static class ProductLedgerDirections
{
    public const string In  = "IN";
    public const string Out = "OUT";

    public static readonly IReadOnlyList<string> All = [In, Out];
}

/// <summary>What to write on a variant's product ledger.</summary>
/// <param name="VariantUuid">The variant. Bare UUID onto inventory.ProductVariants.</param>
/// <param name="EntryType">
/// One of <see cref="ProductLedgerEntryTypes"/>. Its direction is fixed by §11.2 — PURCHASE and
/// RETURN_IN come in, SALE, RETURN_OUT and WRITE_OFF go out — except ADJUSTMENT, which may be either.
/// </param>
/// <param name="Direction">One of <see cref="ProductLedgerDirections"/>.</param>
/// <param name="Quantity">Always positive; <paramref name="Direction"/> says which way it moved. At most four decimal places.</param>
/// <param name="UnitCost">
/// What one unit cost, for an IN entry: the PO line price of a purchase, or the cost a returned or
/// adjusted-in unit is being taken back at. <b>Must be <c>null</c> for an OUT entry</b>, which is always
/// costed at the variant's current weighted-average cost (§11.3) — a caller cannot choose what a sale cost.
/// </param>
/// <param name="ProductUuid">
/// The variant's product, when the caller already knows it. Otherwise it is taken from the variant's
/// earlier entries, and for its first entry looked up through <see cref="IProductVariantResolver"/> —
/// which reads Inventory on its own connection, so inside a transaction that may hold locks on
/// inventory.ProductVariants, pass it.
/// </param>
/// <param name="PartnerId">The supplier or customer on the other side; none for an adjustment or a write-off.</param>
/// <param name="EntryDate">The business date. Defaults to now.</param>
public sealed record ProductLedgerPosting(
    Guid      VariantUuid,
    string    EntryType,
    string    Direction,
    decimal   Quantity,
    decimal?  UnitCost,
    string    ReferenceType,
    Guid      ReferenceId,
    string    ReferenceNumber,
    int       CreatedBy,
    Guid?     ProductUuid = null,
    Guid?     PartnerId   = null,
    string?   Narration   = null,
    DateTime? EntryDate   = null);

/// <summary>What was written, and where the variant stands after it.</summary>
/// <param name="UnitCost">For an OUT entry this is the weighted-average cost the units left at.</param>
/// <param name="TotalCost">
/// The value that moved: <c>quantity × unit cost</c> in, and the cost of goods sold out — the amount a sale
/// books as COGS.
/// </param>
/// <param name="WeightedAverageCost">
/// <c>RunningValue / RunningQty</c> after this entry, to four places; zero when nothing is left.
/// </param>
public sealed record ProductLedgerPosted(
    Guid    EntryUuid,
    int     SequenceNo,
    Guid    VariantUuid,
    Guid    ProductUuid,
    string  Direction,
    decimal Quantity,
    decimal UnitCost,
    decimal TotalCost,
    decimal RunningQty,
    decimal RunningValue,
    decimal WeightedAverageCost);

/// <summary>
/// Writes the per-variant product ledger (A29 §11): each entry carries the variant's running quantity
/// and running value, and its weighted-average cost follows from them. Implemented by SMS.Modules.Finance
/// and consumed by whichever module owns the business action behind an entry — a GRN approval in
/// Warehouse, an invoice issued in Finance — without a project reference, the same arrangement as
/// <see cref="IMasterProductLedgerService"/>.
/// </summary>
public interface IProductLedgerService
{
    /// <summary>
    /// Appends an entry to the variant's ledger and saves it. §11.3: an IN entry adds
    /// <c>quantity × unit cost</c> to the running value and its quantity to the running quantity; an OUT
    /// entry is costed at the current weighted-average cost and takes that much value out. An OUT larger
    /// than what the ledger holds is refused rather than costed at a guess.
    /// <para>
    /// When <paramref name="transaction"/> is given, the write joins it — the implementation enlists its own
    /// DbContext into that ADO.NET transaction — so the entry commits or rolls back with the business
    /// action that caused it (§17.1). The caller owns the transaction and commits it.
    /// </para>
    /// <para>
    /// Two concurrent appends for one variant always end up as distinct, correctly chained rows: the loser
    /// of the race re-reads the fresh last entry and tries again, so no running total is ever lost or forked.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">The posting is malformed: an unknown type or direction, a direction the type cannot have, a non-positive or over-precise amount, or a unit cost that is missing on IN or present on OUT.</exception>
    /// <exception cref="SMS.Shared.Exceptions.NotFoundException">The variant is unknown to Inventory and has no history here.</exception>
    /// <exception cref="SMS.Shared.Exceptions.ConflictException">An OUT for more than the ledger holds of the variant.</exception>
    Task<ProductLedgerPosted> AppendEntryAsync(ProductLedgerPosting posting, DbTransaction? transaction = null);
}

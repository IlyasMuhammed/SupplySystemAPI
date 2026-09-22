namespace SMS.Shared.Common;

/// <summary>
/// The inventory transaction types of stock leaving, transferring and — for sales — returning or passing
/// through the books, named in one place.
/// <para>
/// Free-form strings in the ledger, as everywhere else in the system — nothing enumerates them —
/// but named here so the two sides of a transfer cannot drift apart, and so a sales movement is spelled
/// one way by whoever writes it and whoever reports on it. The poster writes the issue and transfer
/// types; of the four sales kinds of A29 §12.1, it writes <see cref="SalesShip"/> and
/// <see cref="SalesHandover"/>, while <see cref="SalesReturn"/> and <see cref="DropShipVirtual"/> are
/// for the writers of a customer return and a drop-ship receipt to use.
/// </para>
/// </summary>
public static class GoodsIssueTransactionType
{
    /// <summary>
    /// Stock leaving on a delivery. Deliberately distinct from Material's <c>ISSUE</c>, which
    /// means "issued to a project": folding the two together would make the movement report
    /// unable to separate a dispatch from a site issue.
    /// </summary>
    public const string DeliveryIssue = "DELIVERY_ISSUE";

    public const string TransferOut = "TRANSFER_OUT";
    public const string TransferIn  = "TRANSFER_IN";

    /// <summary>A29 §12.1 — stock leaving on a sale-order delivery that is shipped to the customer.</summary>
    public const string SalesShip = "SALES_SHIP";

    /// <summary>A29 §12.1 — stock handed over at the warehouse to a customer collecting a sale order.</summary>
    public const string SalesHandover = "SALES_HANDOVER";

    /// <summary>
    /// A29 §12.1 — stock coming <b>in</b>: a customer's return received back into a warehouse. The one
    /// sales kind that adds to stock, so it is never the type of an issue.
    /// </summary>
    public const string SalesReturn = "SALES_RETURN";

    /// <summary>
    /// A29 §12.1 — a drop-ship's virtual receipt and dispatch: the vendor sends the goods straight to the
    /// customer, so the movement is recorded for the trail but no warehouse stock moves.
    /// </summary>
    public const string DropShipVirtual = "DROP_SHIP_VIRTUAL";
}

/// <param name="ToWarehouseUuid">
/// Set for a transfer, where the same units are received somewhere else. Null for a plain issue,
/// where the stock simply leaves.
/// </param>
/// <param name="TransactionType">
/// The ledger's transaction type for a plain issue. Null means <see cref="GoodsIssueTransactionType.DeliveryIssue"/>;
/// a sale-order delivery names <see cref="GoodsIssueTransactionType.SalesShip"/> or
/// <see cref="GoodsIssueTransactionType.SalesHandover"/>. A transfer always writes its own two types.
/// </param>
public sealed record GoodsIssuePosting(
    string  ReferenceType,
    Guid    ReferenceUuid,
    string  ReferenceNumber,
    Guid?   ToWarehouseUuid = null,
    string? DestinationName = null,
    string? Notes           = null,
    string? TransactionType = null);

/// <param name="QuantityIn">Non-zero only for a transfer, where the units land somewhere else.</param>
public sealed record GoodsIssueResult(
    int     MovementsPosted,
    decimal QuantityOut,
    decimal QuantityIn);

/// <summary>
/// Takes stock off the books against the holds a document is carrying.
/// <para>
/// <b>Posted against the reservations, not against the document's lines.</b> The reservation
/// already names the exact stock rows — warehouse, bin, batch — that were committed at release,
/// walked at picking and boxed at packing. Re-deriving which rows to deduct from at this point
/// would let the stock that leaves the books differ from the stock that left the building, and
/// batch-tracked inventory would be wrong in a way nothing downstream could detect.
/// </para>
/// <para>
/// Consuming the holds and writing the movements happen in <b>one transaction</b>. Half of this
/// having happened is the state nothing can recover from: either the units are gone and still
/// reserved, or they are free and already deducted.
/// </para>
/// <para>
/// Implemented in SMS.Modules.Inventory, where the counters and the ledger live, and resolved
/// through DI so callers need no project reference to it.
/// </para>
/// </summary>
public interface IGoodsIssuePoster
{
    /// <summary>
    /// Deducts every active hold for a source document, closes those holds as consumed, and writes
    /// the ledger entries. For a transfer, also receives the same units into the destination.
    /// <para>
    /// Idempotent in the only way that matters: a document with no active holds posts nothing and
    /// returns zeroes, so a retry after a successful post cannot deduct twice.
    /// </para>
    /// </summary>
    Task<GoodsIssueResult> PostAsync(
        string sourceType,
        Guid sourceUuid,
        GoodsIssuePosting posting,
        int userId,
        CancellationToken ct = default);
}

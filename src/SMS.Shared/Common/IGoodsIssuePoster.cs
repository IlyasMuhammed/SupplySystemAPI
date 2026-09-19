namespace SMS.Shared.Common;

/// <summary>
/// The inventory transaction types this poster writes.
/// <para>
/// Free-form strings in the ledger, as everywhere else in the system — nothing enumerates them —
/// but named here so the two sides of a transfer cannot drift apart.
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
}

/// <param name="ToWarehouseUuid">
/// Set for a transfer, where the same units are received somewhere else. Null for a plain issue,
/// where the stock simply leaves.
/// </param>
public sealed record GoodsIssuePosting(
    string  ReferenceType,
    Guid    ReferenceUuid,
    string  ReferenceNumber,
    Guid?   ToWarehouseUuid = null,
    string? DestinationName = null,
    string? Notes           = null);

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

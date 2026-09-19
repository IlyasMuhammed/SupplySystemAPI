using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Tests;

/// <summary>
/// Stands in for SMS.Modules.Inventory's goods-issue poster.
/// <para>
/// It exists mainly to be <b>counted</b>: the central rule of T-27 is that a delivery raised from
/// an MIV or an SRO must not post the movement a second time, and the only way to assert that is
/// to show the poster was never asked.
/// </para>
/// <para>
/// It consumes the holds through the same fake reservation ledger the rest of the suite uses, so
/// "the stock stopped being reserved" stays true whichever path the delivery took.
/// </para>
/// </summary>
internal sealed class FakeGoodsIssuePoster : IGoodsIssuePoster
{
    private readonly FakeStockReservationService _reservations;

    internal FakeGoodsIssuePoster(FakeStockReservationService reservations) =>
        _reservations = reservations;

    internal sealed record Posted(
        string  SourceType,
        Guid    SourceUuid,
        string  ReferenceType,
        string  ReferenceNumber,
        Guid?   ToWarehouseUuid,
        string? DestinationName,
        decimal QuantityOut,
        decimal QuantityIn,
        string? TransactionType = null);

    private readonly List<Posted> _postings = [];

    internal IReadOnlyList<Posted> Postings => _postings;
    internal int CallCount => _postings.Count;
    internal Posted Last => _postings[^1];

    public async Task<GoodsIssueResult> PostAsync(
        string sourceType,
        Guid sourceUuid,
        GoodsIssuePosting posting,
        int userId,
        CancellationToken ct = default)
    {
        var held = await _reservations.GetAllocationsAsync(sourceType, sourceUuid, ct);

        // Nothing held means nothing to deduct — the retry case, and the fake models it so a
        // second issue cannot appear to post twice.
        if (held.Count == 0)
        {
            _postings.Add(new Posted(
                sourceType, sourceUuid, posting.ReferenceType, posting.ReferenceNumber,
                posting.ToWarehouseUuid, posting.DestinationName, 0m, 0m));

            return new GoodsIssueResult(0, 0m, 0m);
        }

        var isTransfer = posting.ToWarehouseUuid is not null;
        var outQty     = held.Sum(h => h.Quantity);
        var inQty      = isTransfer ? outQty : 0m;
        var movements  = isTransfer ? held.Count * 2 : held.Count;

        await _reservations.ConsumeBySourceAsync(sourceType, sourceUuid, userId, ct);

        _postings.Add(new Posted(
            sourceType, sourceUuid, posting.ReferenceType, posting.ReferenceNumber,
            posting.ToWarehouseUuid, posting.DestinationName, outQty, inQty,
            isTransfer ? GoodsIssueTransactionType.TransferOut
                       : posting.TransactionType ?? GoodsIssueTransactionType.DeliveryIssue));

        return new GoodsIssueResult(movements, outQty, inQty);
    }
}

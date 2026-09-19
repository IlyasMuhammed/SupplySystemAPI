using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Domain.StateMachines;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Logistics.Repositories;

internal interface IGoodsIssueRepository
{
    Task<bool> StageAsync(Guid uuid, int userId);
    Task<GoodsIssueResultModel?> IssueAsync(Guid uuid, int userId);
}

/// <summary>
/// Goods issue — the point of no return.
/// <para>
/// Everything before this is reversible: a release can be cancelled, a pick corrected, a carton
/// voided. Once the stock is issued it has left the books, and the state machine offers no way
/// back — <c>GOODS_ISSUED</c> leads only onward.
/// </para>
/// <para>
/// <b>The one rule that matters here is not deducting twice.</b> A delivery raised from an MIV or
/// an SRO is a <em>movement reference</em>: the source document already wrote the negative stock
/// movement, and posting again would understate inventory by exactly the quantity shipped, with
/// nothing to point at the cause. <see cref="DeliverySourceTypeInfo.PostsGoodsIssue"/> decides,
/// off the source <em>type</em>, so no row and no user can get it wrong.
/// </para>
/// <para>
/// Either way the <b>hold ends</b> — the units are gone, so continuing to reserve them would make
/// stock permanently unavailable that has already left the building.
/// </para>
/// </summary>
internal sealed class GoodsIssueRepository : IGoodsIssueRepository
{
    private readonly LogisticsDbContext       _db;
    private readonly IStockReservationService _reservations;
    private readonly IGoodsIssuePoster        _poster;

    public GoodsIssueRepository(
        LogisticsDbContext db,
        IStockReservationService reservations,
        IGoodsIssuePoster poster)
    {
        _db           = db;
        _reservations = reservations;
        _poster       = poster;
    }

    // ── Stage ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Moves a packed delivery to the dock. A separate step from packing because staging is where
    /// a shipping rule can divert it to approval, and because "boxed" and "loaded" are different
    /// physical facts that a warehouse reports at different times.
    /// </summary>
    public async Task<bool> StageAsync(Guid uuid, int userId)
    {
        var delivery = await _db.DeliveryOrders
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.UUID == uuid && !d.IsDelete);

        if (delivery is null) return false;

        DeliveryStateMachine.Instance.EnsureCanTransition(
            LogisticsCode.Parse<DeliveryStatus>(delivery.Status), DeliveryStatus.Staged);

        if (delivery.Lines.Any(l => l.QtyPacked <= 0) && delivery.Lines.Any(l => l.QtyPicked > 0))
            throw new ConflictException(
                $"Delivery {delivery.DeliveryNumber} has picked lines that are not in a carton. " +
                "Pack them, or void what is packed and short-close, before staging.");

        delivery.Status       = LogisticsCode.Of(DeliveryStatus.Staged);
        delivery.ModifiedBy   = userId;
        delivery.ModifiedDate = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return true;
    }

    // ── Issue ─────────────────────────────────────────────────────────────────

    public async Task<GoodsIssueResultModel?> IssueAsync(Guid uuid, int userId)
    {
        var delivery = await _db.DeliveryOrders
            .Include(d => d.Lines)
            .Include(d => d.ShipToAddress)
            .FirstOrDefaultAsync(d => d.UUID == uuid && !d.IsDelete);

        if (delivery is null) return null;

        DeliveryStateMachine.Instance.EnsureCanTransition(
            LogisticsCode.Parse<DeliveryStatus>(delivery.Status), DeliveryStatus.GoodsIssued);

        var sourceType = LogisticsCode.Parse<DeliverySourceType>(delivery.SourceType);
        var posts      = DeliverySourceTypeInfo.PostsGoodsIssue(sourceType);
        var shipped    = delivery.Lines.Sum(l => l.QtyPacked);

        if (shipped <= 0)
            throw new ConflictException(
                $"Nothing is packed on delivery {delivery.DeliveryNumber}, so there is nothing to " +
                "issue. Cancel it instead — a goods issue for zero is a movement that did not happen.");

        var now = DateTime.UtcNow;

        var result = posts
            ? await PostAsync(delivery, sourceType, userId)
            : await ReferenceOnlyAsync(delivery, sourceType, userId);

        // What physically went out is what was in the boxes. Recorded now rather than derived
        // later, because packing can no longer change once the delivery is issued.
        foreach (var line in delivery.Lines)
            line.QtyShipped = line.QtyPacked;

        delivery.Status        = LogisticsCode.Of(DeliveryStatus.GoodsIssued);
        delivery.GoodsIssuedAt = now;
        delivery.GoodsIssuedBy = userId;
        delivery.ModifiedBy    = userId;
        delivery.ModifiedDate  = now;

        await _db.SaveChangesAsync();

        result.QtyShipped = shipped;
        return result;
    }

    /// <summary>
    /// The delivery is the document that moves the stock. Consuming the holds and writing the
    /// ledger entries happen together, inside the poster's own transaction.
    /// </summary>
    private async Task<GoodsIssueResultModel> PostAsync(
        DeliveryOrder delivery, DeliverySourceType sourceType, int userId)
    {
        var isTransfer = sourceType == DeliverySourceType.Transfer;

        if (isTransfer && delivery.ShipToWarehouseUuid is null)
            throw new ConflictException(
                $"Transfer {delivery.DeliveryNumber} has no destination warehouse, so the stock " +
                "would leave one site and arrive nowhere. Nothing was posted.");

        var posted = await _poster.PostAsync(
            ReservationSourceType.Delivery,
            delivery.UUID,
            new GoodsIssuePosting(
                ReferenceType:   DeliveryStatusHandler.Code,
                ReferenceUuid:   delivery.UUID,
                ReferenceNumber: delivery.DeliveryNumber,
                ToWarehouseUuid: isTransfer ? delivery.ShipToWarehouseUuid : null,
                DestinationName: DestinationOf(delivery),
                Notes:           $"Goods issued on delivery {delivery.DeliveryNumber}."),
            userId);

        return new GoodsIssueResultModel
        {
            DeliveryUuid     = delivery.UUID,
            DeliveryNumber   = delivery.DeliveryNumber,
            Status           = LogisticsCode.Of(DeliveryStatus.GoodsIssued),
            PostedStock      = true,
            MovementsPosted  = posted.MovementsPosted,
            QtyOut           = posted.QuantityOut,
            QtyIn            = posted.QuantityIn,
            ReservationsClosed = posted.MovementsPosted
        };
    }

    /// <summary>
    /// The source document already posted the movement, so this delivery only proves it happened.
    /// The holds still end: the units have gone, and a reservation on stock that has left the
    /// building makes it permanently unavailable to everyone else.
    /// </summary>
    private async Task<GoodsIssueResultModel> ReferenceOnlyAsync(
        DeliveryOrder delivery, DeliverySourceType sourceType, int userId)
    {
        var closed = await _reservations.ConsumeBySourceAsync(
            ReservationSourceType.Delivery, delivery.UUID, userId);

        return new GoodsIssueResultModel
        {
            DeliveryUuid       = delivery.UUID,
            DeliveryNumber     = delivery.DeliveryNumber,
            Status             = LogisticsCode.Of(DeliveryStatus.GoodsIssued),
            PostedStock        = false,
            MovementsPosted    = 0,
            ReservationsClosed = closed,
            Note = $"{LogisticsCode.Of(sourceType)} already posted this movement, so the delivery "
                 + "records it rather than posting it again."
        };
    }

    private static string? DestinationOf(DeliveryOrder delivery) =>
        delivery.ShipToAddress?.ContactName
        ?? delivery.ShipToAddress?.CityName
        ?? delivery.SourceNumber;
}

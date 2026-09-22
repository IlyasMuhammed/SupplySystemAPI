using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Services;
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

    /// <summary>The customer collects a self-pickup delivery: issue if not yet issued, then DELIVERED.</summary>
    Task<PickupResultModel?> RecordPickupAsync(Guid uuid, RecordPickupRequest req, int userId);
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
    private const string ActiveReservation = "ACTIVE";

    private static readonly string FulfilledSoLine =
        Demand.Domain.EnumCode<Demand.Domain.SaleOrderLineStatus>.Of(Demand.Domain.SaleOrderLineStatus.Fulfilled);

    private static readonly string PartiallyFulfilledSoLine =
        Demand.Domain.EnumCode<Demand.Domain.SaleOrderLineStatus>.Of(Demand.Domain.SaleOrderLineStatus.PartiallyFulfilled);

    private readonly LogisticsDbContext            _db;
    private readonly DemandDbContext               _demand;
    private readonly IStockReservationService      _reservations;
    private readonly IGoodsIssuePoster             _poster;
    private readonly ISaleOrderFulfillmentService  _fulfillment;
    private readonly ILogger<GoodsIssueRepository> _log;

    public GoodsIssueRepository(
        LogisticsDbContext db,
        DemandDbContext demand,
        IStockReservationService reservations,
        IGoodsIssuePoster poster,
        ISaleOrderFulfillmentService fulfillment,
        ILogger<GoodsIssueRepository> log)
    {
        _db           = db;
        _demand       = demand;
        _reservations = reservations;
        _poster       = poster;
        _fulfillment  = fulfillment;
        _log          = log;
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
        var isTransfer  = sourceType == DeliverySourceType.Transfer;
        var isSaleOrder = sourceType == DeliverySourceType.SaleOrder;

        if (isTransfer && delivery.ShipToWarehouseUuid is null)
            throw new ConflictException(
                $"Transfer {delivery.DeliveryNumber} has no destination warehouse, so the stock " +
                "would leave one site and arrive nowhere. Nothing was posted.");

        // Read before posting: the poster closes these holds, and what it deducts per line is
        // what the sale order line gets credited with.
        var heldByLine = isSaleOrder ? await HeldByLineAsync(delivery) : new Dictionary<Guid, decimal>();

        var posted = await _poster.PostAsync(
            ReservationSourceType.Delivery,
            delivery.UUID,
            new GoodsIssuePosting(
                ReferenceType:   DeliveryStatusHandler.Code,
                ReferenceUuid:   delivery.UUID,
                ReferenceNumber: delivery.DeliveryNumber,
                ToWarehouseUuid: isTransfer ? delivery.ShipToWarehouseUuid : null,
                DestinationName: DestinationOf(delivery),
                Notes:           $"Goods issued on delivery {delivery.DeliveryNumber}.",
                TransactionType: isSaleOrder ? SalesMovementOf(delivery) : null),
            userId);

        if (isSaleOrder) await CreditSaleOrderAsync(delivery, heldByLine);

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

    // ── Self-pickup (A29 §8.2) ────────────────────────────────────────────────

    /// <summary>
    /// The moment a customer walks out with their order. One call does what the counter needs:
    /// records who collected and what they showed, issues the stock if the warehouse had not
    /// already (a customer may turn up before or after the store posts the issue), and marks the
    /// delivery DELIVERED — straight from GOODS_ISSUED, because nothing was ever in transit.
    /// </summary>
    public async Task<PickupResultModel?> RecordPickupAsync(Guid uuid, RecordPickupRequest req, int userId)
    {
        ArgumentNullException.ThrowIfNull(req);

        var name     = Require(req.PickupPersonName, "The name of the person collecting");
        var idNumber = Require(req.PickupPersonIdNumber, "The collector's ID number");

        if (!LogisticsCode.TryParse<PickupIdType>(req.PickupPersonIdType?.Trim().ToUpperInvariant(), out var idType))
            throw new BadRequestException(
                $"'{req.PickupPersonIdType}' is not a valid ID type. Valid values: " +
                $"{string.Join(", ", LogisticsCode.Codes<PickupIdType>())}.");

        var delivery = await _db.DeliveryOrders.AsNoTracking()
            .FirstOrDefaultAsync(d => d.UUID == uuid && !d.IsDelete);

        if (delivery is null) return null;

        if (!LogisticsCode.TryParse<DeliveryMode>(delivery.DeliveryMode, out var mode) || mode != DeliveryMode.SelfPickup)
            throw new ConflictException(
                $"Delivery {delivery.DeliveryNumber} is not a self-pickup" +
                (delivery.DeliveryMode is null ? "" : $" (its mode is {delivery.DeliveryMode})") +
                ". A collection can only be recorded for a delivery the customer collects; a shipped " +
                "delivery is proved delivered by its consignment.");

        var status = LogisticsCode.Parse<DeliveryStatus>(delivery.Status);

        if (status is DeliveryStatus.Delivered or DeliveryStatus.Closed)
            throw new ConflictException(
                $"Delivery {delivery.DeliveryNumber} was already collected" +
                (delivery.PickedUpAt is { } at ? $" on {at:yyyy-MM-dd HH:mm} by {delivery.PickupPersonName}" : "") +
                ". A collection is recorded once.");

        // Packed but not yet at the dock: the counter is the dock for a collection.
        if (status == DeliveryStatus.Packed)
        {
            await StageAsync(uuid, userId);
            status = DeliveryStatus.Staged;
        }

        GoodsIssueResultModel? issued = null;

        if (status is DeliveryStatus.Staged or DeliveryStatus.PendingApproval)
            issued = await IssueAsync(uuid, userId);
        else if (status != DeliveryStatus.GoodsIssued)
            throw new ConflictException(
                $"Delivery {delivery.DeliveryNumber} is {delivery.Status}, so there is nothing to hand " +
                "over yet. Pick, pack and stage it first; the collection then issues the stock.");

        // Re-read tracked: the issue above saved through its own load.
        var tracked = await _db.DeliveryOrders
            .Include(d => d.Lines)
            .FirstAsync(d => d.UUID == uuid);

        DeliveryStateMachine.Instance.EnsureCanTransition(
            LogisticsCode.Parse<DeliveryStatus>(tracked.Status), DeliveryStatus.Delivered);

        var now = DateTime.UtcNow;

        // Whatever was issued is what the customer walked out with.
        foreach (var line in tracked.Lines)
            line.QtyDelivered = line.QtyShipped;

        tracked.Status               = LogisticsCode.Of(DeliveryStatus.Delivered);
        tracked.PickupPersonName     = name;
        tracked.PickupPersonIdType   = LogisticsCode.Of(idType);
        tracked.PickupPersonIdNumber = idNumber;
        tracked.PickupAuthorization  = string.IsNullOrWhiteSpace(req.PickupAuthorization) ? null : req.PickupAuthorization.Trim();
        tracked.PickedUpAt           = now;
        tracked.PickedUpBy           = userId;
        tracked.ModifiedBy           = userId;
        tracked.ModifiedDate         = now;

        await _db.SaveChangesAsync();

        var fulfillment = await NotifySaleOrderAsync(tracked, userId);

        return new PickupResultModel
        {
            DeliveryUuid     = tracked.UUID,
            DeliveryNumber   = tracked.DeliveryNumber,
            Status           = tracked.Status,
            PickedUpAt       = now,
            PickupPersonName = name,
            GoodsIssue       = issued,
            SaleOrderStatus  = fulfillment?.Status
        };
    }

    /// <summary>
    /// Tells the sale order its delivery has arrived (A29-P6-06 §7.6). After the delivery's own save,
    /// and never allowed to fail it: the customer has the goods whatever the order's bookkeeping
    /// does next, and a notification-side error is logged for someone to reconcile.
    /// </summary>
    private Task<FulfillmentResult?> NotifySaleOrderAsync(DeliveryOrder delivery, int userId) =>
        SaleOrderDeliveryNotifier.NotifyDeliveredAsync(_fulfillment, _log, delivery, userId);

    private static string Require(string? value, string what) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new BadRequestException($"{what} is required — the gate pass has to say who took the goods.")
            : value.Trim();

    // ── Sale orders (A29 §7.5) ────────────────────────────────────────────────

    /// <summary>
    /// SALES_SHIP when the goods travel to the customer, SALES_HANDOVER when the customer collects
    /// them here (§12.1). A sale-order delivery with no mode cannot be classified, and a movement
    /// filed under the wrong kind is a ledger error nothing downstream would notice — so it is
    /// refused rather than guessed.
    /// </summary>
    private static string SalesMovementOf(DeliveryOrder delivery)
    {
        if (!LogisticsCode.TryParse<DeliveryMode>(delivery.DeliveryMode, out var mode))
            throw new ConflictException(
                $"Delivery {delivery.DeliveryNumber} is for a sale order but has no delivery mode " +
                "(SHIP or SELF_PICKUP), so its stock movement cannot be classified. Nothing was posted.");

        return mode == DeliveryMode.SelfPickup
            ? GoodsIssueTransactionType.SalesHandover
            : GoodsIssueTransactionType.SalesShip;
    }

    /// <summary>What each line of the delivery still holds — the quantity the poster will deduct.</summary>
    private async Task<Dictionary<Guid, decimal>> HeldByLineAsync(DeliveryOrder delivery)
    {
        var holds = await _reservations.GetBySourceAsync(ReservationSourceType.Delivery, delivery.UUID);

        return holds
            .Where(h => h.Status == ActiveReservation && h.SourceLineUuid is not null)
            .GroupBy(h => h.SourceLineUuid!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(h => h.ReservedQty));
    }

    /// <summary>
    /// <c>soLine.fulfilled_qty += issuedQty</c>, and the line's status with it. Credited with what
    /// actually left the books, not what was ordered: a short-picked line ships less, and the
    /// order must still see the balance as outstanding.
    /// </summary>
    private async Task CreditSaleOrderAsync(DeliveryOrder delivery, Dictionary<Guid, decimal> heldByLine)
    {
        var issued = delivery.Lines
            .Where(l => l.SoLineUuid is not null && heldByLine.GetValueOrDefault(l.UUID) > 0)
            .GroupBy(l => l.SoLineUuid!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(l => heldByLine[l.UUID]));

        if (issued.Count == 0) return;

        var soLineUuids = issued.Keys.ToList();
        var soLines = await _demand.SaleOrderLines
            .Where(l => soLineUuids.Contains(l.UUID))
            .ToListAsync();

        foreach (var soLine in soLines)
        {
            soLine.FulfilledQty += issued[soLine.UUID];
            soLine.Status = soLine.FulfilledQty >= soLine.Quantity ? FulfilledSoLine : PartiallyFulfilledSoLine;
        }

        await _demand.SaveChangesAsync();
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Demand.Services;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Domain.StateMachines;

namespace SMS.Modules.Logistics.Services;

/// <summary>
/// Carries what a carrier reports back onto the delivery it is carrying.
/// <para>
/// A delivery and its consignment are separate records — the delivery is the warehouse's promise, the
/// consignment is the carrier's movement — and until this existed nothing joined them after goods
/// issue. A shipped delivery stayed at GOODS_ISSUED for ever, so its sale order never moved, and it
/// could never be invoiced, because an invoice needs goods that have reached the customer.
/// </para>
/// </summary>
internal interface IDeliveryProgressService
{
    /// <summary>
    /// Brings the deliveries on one consignment in line with where the carrier has got to. Returns how
    /// many moved.
    /// <para>
    /// For a person who has just recorded a handover and is waiting to see it. Never throws for a
    /// failure on the delivery side: the handover is already saved, and failing the caller now would
    /// tempt them to record it twice. <see cref="SweepAsync"/> picks up whatever this missed.
    /// </para>
    /// </summary>
    Task<int> SyncAsync(Guid consignmentUuid, int userId, CancellationToken ct = default);

    /// <summary>
    /// Every consignment whose deliveries are behind it, whichever way the carrier told us — a webhook,
    /// a poll, a proof typed in by hand, or a delivery issued after its consignment had already moved.
    /// One question catches all of them, which is why this is a sweep and not a hook on each path.
    /// </summary>
    Task<int> SweepAsync(int userId, CancellationToken ct = default);
}

internal sealed class DeliveryProgressService : IDeliveryProgressService
{
    private static readonly DeliveryStateMachine Machine = DeliveryStateMachine.Instance;

    private static readonly string GoodsIssued = LogisticsCode.Of(DeliveryStatus.GoodsIssued);
    private static readonly string InTransit   = LogisticsCode.Of(DeliveryStatus.InTransit);

    private static readonly string ConsignmentDelivered = LogisticsCode.Of(ShipmentStatus.Delivered);

    /// <summary>
    /// The consignment statuses that mean the carrier has taken the goods and not yet handed them over.
    /// <para>
    /// EXCEPTION is left out on purpose. A pickup that failed is an exception too, and then the goods
    /// are still on our dock; a delivery that had already left was moved on by the scans before it.
    /// </para>
    /// </summary>
    private static readonly string[] CarrierHasGoods =
    [
        LogisticsCode.Of(ShipmentStatus.PickedUp),
        LogisticsCode.Of(ShipmentStatus.InTransit),
        LogisticsCode.Of(ShipmentStatus.OutForDelivery),
        LogisticsCode.Of(ShipmentStatus.DeliveryAttempted)
    ];

    private static readonly string[] AnyCarrierProgress = [.. CarrierHasGoods, ConsignmentDelivered];

    private readonly LogisticsDbContext           _db;
    private readonly ISaleOrderFulfillmentService _fulfillment;
    private readonly ILogger<DeliveryProgressService> _log;

    public DeliveryProgressService(
        LogisticsDbContext db, ISaleOrderFulfillmentService fulfillment, ILogger<DeliveryProgressService> log)
    {
        _db          = db;
        _fulfillment = fulfillment;
        _log         = log;
    }

    public async Task<int> SyncAsync(Guid consignmentUuid, int userId, CancellationToken ct = default)
    {
        try
        {
            return await SyncCoreAsync(consignmentUuid, userId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex,
                "Consignment {Consignment} moved but its deliveries could not be brought up to date; " +
                "the next sweep will retry.", consignmentUuid);
            return 0;
        }
    }

    public async Task<int> SweepAsync(int userId, CancellationToken ct = default)
    {
        // Only what can actually move. A delivery already IN_TRANSIT under a consignment that is
        // still travelling has nothing left to learn, and asking again every run would be noise.
        var behind = await _db.ConsignmentDeliveries.AsNoTracking()
            .Where(l => !l.Consignment.IsDelete && !l.DeliveryOrder.IsDelete
                     && ((l.DeliveryOrder.Status == GoodsIssued && AnyCarrierProgress.Contains(l.Consignment.Status))
                      || (l.DeliveryOrder.Status == InTransit   && l.Consignment.Status == ConsignmentDelivered)))
            .Select(l => l.Consignment.UUID)
            .Distinct()
            .ToListAsync(ct);

        var moved = 0;

        foreach (var consignmentUuid in behind)
        {
            try
            {
                moved += await SyncCoreAsync(consignmentUuid, userId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Consignment {Consignment}'s deliveries could not be brought up to date.",
                    consignmentUuid);

                // One consignment that will not save must not stop the rest, and its half-applied
                // changes must not ride along on the next save.
                _db.ChangeTracker.Clear();
            }
        }

        return moved;
    }

    // ── One consignment ───────────────────────────────────────────────────────

    private async Task<int> SyncCoreAsync(Guid consignmentUuid, int userId, CancellationToken ct)
    {
        var links = await _db.ConsignmentDeliveries
            .Include(l => l.Consignment)
            .Include(l => l.ConsignmentStop)
            .Include(l => l.DeliveryOrder).ThenInclude(d => d.Lines)
            .Where(l => l.Consignment.UUID == consignmentUuid
                     && !l.Consignment.IsDelete && !l.DeliveryOrder.IsDelete)
            .ToListAsync(ct);

        var moved   = 0;
        var arrived = new List<DeliveryOrder>();

        foreach (var link in links)
        {
            if (!TryMove(link, userId, out var reachedCustomer)) continue;

            moved++;
            if (reachedCustomer) arrived.Add(link.DeliveryOrder);
        }

        if (moved == 0) return 0;

        await _db.SaveChangesAsync(ct);

        // After the delivery's own save and outside it: the order is another module's database, and
        // a failure there must not undo a handover that has already happened.
        foreach (var delivery in arrived)
            await SaleOrderDeliveryNotifier.NotifyDeliveredAsync(_fulfillment, _log, delivery, userId);

        return moved;
    }

    /// <summary>
    /// Moves one delivery to where its consignment says it is, if the delivery's own state machine
    /// allows it. A delivery that cannot follow — not issued yet, on hold, cancelled — is left alone:
    /// the carrier does not get to overrule the warehouse.
    /// </summary>
    private static bool TryMove(ConsignmentDelivery link, int userId, out bool reachedCustomer)
    {
        reachedCustomer = false;

        var delivery = link.DeliveryOrder;

        // A collection is proved at the counter, with a name and an ID. A courier's scan is not that.
        if (LogisticsCode.TryParse<DeliveryMode>(delivery.DeliveryMode, out var mode)
         && mode == DeliveryMode.SelfPickup)
            return false;

        if (!LogisticsCode.TryParse<DeliveryStatus>(delivery.Status, out var current)) return false;
        if (TargetFor(link) is not { } target || target == current) return false;
        if (!Machine.CanTransition(current, target)) return false;

        delivery.Status       = LogisticsCode.Of(target);
        delivery.ModifiedBy   = userId;
        delivery.ModifiedDate = DateTime.UtcNow;

        if (target != DeliveryStatus.Delivered) return true;

        // What went out in the boxes is what reached the customer. A partial handover would need a
        // per-line account from the carrier, which no scan gives us.
        foreach (var line in delivery.Lines)
            line.QtyDelivered = line.QtyShipped;

        reachedCustomer = true;
        return true;
    }

    /// <summary>Where the consignment's status puts this delivery, or null when it says nothing about it.</summary>
    internal static DeliveryStatus? TargetFor(ConsignmentDelivery link)
    {
        var status = link.Consignment.Status;

        if (status == ConsignmentDelivered)
            // Recording a handover at one stop of a run marks the whole consignment delivered. A
            // delivery dropped at a stop nobody has reached yet is still on the vehicle.
            return link.ConsignmentStop is { ActualArrival: null }
                ? DeliveryStatus.InTransit
                : DeliveryStatus.Delivered;

        return CarrierHasGoods.Contains(status) ? DeliveryStatus.InTransit : null;
    }
}

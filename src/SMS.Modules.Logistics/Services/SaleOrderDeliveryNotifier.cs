using Microsoft.Extensions.Logging;
using SMS.Modules.Demand.Services;
using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Services;

/// <summary>
/// Tells a sale order that one of its deliveries has reached the customer (A29-P6-06 §7.6).
/// <para>
/// Shared by the two ways a delivery becomes DELIVERED — the counter recording a collection, and a
/// consignment reporting a handover — because the order must hear about both in exactly the same
/// terms, and two copies of this would be two places to forget a change.
/// </para>
/// </summary>
internal static class SaleOrderDeliveryNotifier
{
    /// <summary>
    /// Called after the delivery's own save, and never allowed to fail it: the customer has the goods
    /// whatever the order's bookkeeping does next, and a notification-side error is logged for
    /// someone to reconcile. Null when the delivery is not for a sale order, or the call failed.
    /// </summary>
    internal static async Task<FulfillmentResult?> NotifyDeliveredAsync(
        ISaleOrderFulfillmentService fulfillment, ILogger log, DeliveryOrder delivery, int userId)
    {
        if (delivery.SaleOrderUuid is not { } saleOrderUuid) return null;

        try
        {
            var lines = delivery.Lines
                .Where(l => l.SoLineUuid is not null)
                .Select(l => new DeliveredLine(l.SoLineUuid!.Value, l.QtyDelivered))
                .ToList();

            return await fulfillment.RecordDeliveryCompletedAsync(
                new DeliveryCompletion(saleOrderUuid, delivery.UUID, delivery.DeliveryNumber, lines, userId));
        }
        catch (Exception ex)
        {
            log.LogError(ex,
                "Delivery {Delivery} was delivered but sale order {So} could not be updated; reconcile its status by hand.",
                delivery.DeliveryNumber, saleOrderUuid);
            return null;
        }
    }
}

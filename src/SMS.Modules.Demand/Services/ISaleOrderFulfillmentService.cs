namespace SMS.Modules.Demand.Services;

// A29-P6-06 §7.6 — what Logistics tells Demand when a sale-order delivery has reached the customer.
// Same arrangement as ISaleOrderGrnLinkService: the decision (what the order's status becomes, what
// gets recorded and who is told) is sale order state, and Logistics already depends on Demand.
public interface ISaleOrderFulfillmentService
{
    /// <summary>
    /// Re-derives the order's status from its lines — PARTIALLY_FULFILLED while anything is still
    /// to go, FULFILLED once every open line's <c>fulfilled_qty</c> has reached what was ordered —
    /// and, on the transition to FULFILLED, records SO_FULFILLED on the order's timeline and emails
    /// the fulfilment notice. Orders that are not CONFIRMED or PARTIALLY_FULFILLED are left alone.
    /// <para>
    /// <c>fulfilled_qty</c> itself is not touched here: goods issue credits it when the stock leaves
    /// (§7.5), and a delivery only reaches DELIVERED after its goods issue.
    /// </para>
    /// </summary>
    Task<FulfillmentResult> RecordDeliveryCompletedAsync(DeliveryCompletion completion);
}

/// <param name="Lines">What the delivery handed over, per sale order line — for the record; the
/// order's own <c>fulfilled_qty</c> is the figure the status is derived from.</param>
public sealed record DeliveryCompletion(
    Guid                         SaleOrderUuid,
    Guid                         DeliveryUuid,
    string                       DeliveryNumber,
    IReadOnlyList<DeliveredLine> Lines,
    int                          UserId);

public sealed record DeliveredLine(Guid SoLineUuid, decimal QtyDelivered);

/// <param name="Found">False when no such order exists here — nothing else in the result means anything then.</param>
/// <param name="Status">The order's status after this call.</param>
/// <param name="BecameFulfilled">True only on the call that moved the order to FULFILLED.</param>
public sealed record FulfillmentResult(
    bool    Found,
    string? Status,
    bool    BecameFulfilled,
    decimal OrderedQty,
    decimal FulfilledQty);

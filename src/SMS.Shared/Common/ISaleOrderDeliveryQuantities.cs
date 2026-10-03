namespace SMS.Shared.Common;

/// <summary>
/// A32 PE-02 — how much of each sale order line is on the order's deliveries that have not been goods-issued yet
/// (DRAFT through STAGED, PENDING_APPROVAL, ON_HOLD — the same "in flight" rule Logistics uses for what may still be
/// delivered). Those units are either held by the delivery (its release moved the order's hold onto it) or about to
/// be, so they are neither the order's own reservation nor fulfilled: Demand counts them as held for the delivery
/// indicator and as not reservable again. Implemented in Logistics, which owns deliveries; optional in Demand (no
/// Logistics = nothing in flight).
/// </summary>
public interface ISaleOrderDeliveryQuantities
{
    /// <summary>In-flight quantity per sale order line uuid; lines with none are absent.</summary>
    Task<IReadOnlyDictionary<Guid, decimal>> GetInFlightBySoLineAsync(Guid saleOrderUuid, CancellationToken ct = default);
}

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
    /// <summary>
    /// In-flight quantity per sale order line uuid; lines with none are absent. DRAFT included: this is "already on a
    /// delivery", what may not be put on another one.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, decimal>> GetInFlightBySoLineAsync(Guid saleOrderUuid, CancellationToken ct = default);

    /// <summary>
    /// A33 C-2 — quantity per sale order line that the order's deliveries actually <b>hold</b>: those released and not
    /// yet goods-issued (RELEASED through STAGED, PENDING_APPROVAL, ON_HOLD). A DRAFT delivery holds nothing — the
    /// order's own SALES_ORDER hold moves onto the delivery only at release — so counting it as held made every line
    /// with a draft look covered: nothing reservable, DeficitQty zero, and the GRN link reserving nothing when the
    /// back-to-back goods arrived. This is the number for holds arithmetic (<c>SaleOrderHolds</c>);
    /// <see cref="GetInFlightBySoLineAsync"/> stays the number for "what may still be put on a delivery".
    /// <para>
    /// The default keeps the old reading for an implementation that predates this member; Logistics overrides it.
    /// </para>
    /// </summary>
    Task<IReadOnlyDictionary<Guid, decimal>> GetHeldBySoLineAsync(Guid saleOrderUuid, CancellationToken ct = default) =>
        GetInFlightBySoLineAsync(saleOrderUuid, ct);
}

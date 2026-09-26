namespace SMS.Modules.Demand.Services;

/// <summary>
/// A30 Phase 4 Track C, decision D1 — the manufacturing half of §6.1's deficit flow. A line whose
/// product is supplied by MANUFACTURE never reaches <see cref="IAutoPurchaseOrderService"/>; this is
/// what it reaches instead (see <see cref="AutoPoCreationJob"/>'s own branch on
/// <c>Product.SupplyMethod</c>).
/// </summary>
public interface ISaleOrderManufacturingService
{
    /// <summary>
    /// Registers the line's own demand with the shared allocation engine (so existing stock and
    /// supply already on its way can cover some or all of it for free) and raises a production
    /// order for whatever is still short after that. Idempotent per line the same shape
    /// <see cref="IAutoPurchaseOrderService"/> is per its own PO link.
    /// </summary>
    Task FulfillDeficitAsync(Guid saleOrderUuid, Guid saleOrderLineUuid, decimal deficit, int userId, CancellationToken ct = default);
}

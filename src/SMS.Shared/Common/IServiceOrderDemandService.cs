namespace SMS.Shared.Common;

/// <summary>A36 D-10 — a service order raised for a sale order line, as the sale order sees it.</summary>
public sealed record SaleOrderServiceOrderRef(
    Guid ServiceOrderUuid, string ServiceNumber, Guid SoLineUuid, string Status, decimal Quantity);

/// <summary>What cancelling a sale order did to its service orders (mirrors production's cancellation cascade).</summary>
public sealed record SaleOrderServiceCancellation(
    IReadOnlyList<SaleOrderServiceOrderRef> Cancelled, IReadOnlyList<SaleOrderServiceOrderRef> KeptRunning);

/// <summary>
/// A36 D-10 — how a confirmed sale order's service lines become service orders, without Demand referencing Material's
/// internals (same pattern as <see cref="IProductionDemandService"/>). Implemented in SMS.Modules.Material, resolved via DI.
/// </summary>
public interface IServiceOrderDemandService
{
    /// <summary>
    /// Ensures a service order exists for the line. Idempotent per (sale order, line): a second call returns the existing
    /// live order instead of raising another. The order is created in DRAFT with source SALES_ORDER and the caller's trace.
    /// </summary>
    Task<SaleOrderServiceOrderRef> EnsureForSaleOrderLineAsync(
        Guid saleOrderUuid, Guid saleOrderLineUuid, string saleOrderNumber, Guid customerUuid,
        Guid variantUuid, decimal quantity, DateTime? scheduledDate, Guid? warehouseUuid,
        int userId, Guid? traceId = null, CancellationToken ct = default);

    /// <summary>Every service order raised for the sale order (any status).</summary>
    Task<IReadOnlyList<SaleOrderServiceOrderRef>> GetForSaleOrderAsync(Guid organizationId, Guid saleOrderUuid, CancellationToken ct = default);

    /// <summary>Cancels the sale order's service orders that have not started; started ones are kept and reported.</summary>
    Task<SaleOrderServiceCancellation> CancelForSaleOrderAsync(Guid saleOrderUuid, string reason, int userId, CancellationToken ct = default);
}

/// <summary>
/// A36 D-10 — told by Material when a sale-order-sourced service order reaches COMPLETED, CLOSED or CANCELLED, so the
/// sale order can recount its line's fulfilment. Implemented in SMS.Modules.Demand. Optional: absent in unit hosts.
/// </summary>
public interface ISaleOrderServiceFulfillmentListener
{
    Task OnServiceOrderChangedAsync(Guid organizationId, Guid saleOrderUuid, Guid saleOrderLineUuid, int userId, CancellationToken ct = default);
}

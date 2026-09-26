namespace SMS.Shared.Common;

/// <summary>
/// A30 Phase 4 Track C, decision D1 — the manufacturing half of a sale order's deficit. Sales has no
/// project reference to Material (and Material already references Demand, for
/// <see cref="IPurchaseOrderService"/> — a reference the other way would be circular), so this small
/// interface is how a manufactured product's shortfall becomes a production order without one.
/// Implemented in SMS.Modules.Material, resolved through DI.
/// </summary>
public interface IProductionDemandService
{
    /// <summary>
    /// Ensures a production order exists and is planned for at least <paramref name="quantity"/> of
    /// the manufactured product behind <paramref name="variantUuid"/>, sourced from the caller's own
    /// document. Idempotent per source (<paramref name="sourceType"/> + <paramref name="sourceUuid"/>
    /// + <paramref name="sourceLineUuid"/>): a second call for the same source tops up the existing
    /// order's quantity — while it is still a DRAFT nothing has snapshotted yet — or raises a further
    /// order for the extra once it is not, rather than ever raising a duplicate for the same source.
    /// </summary>
    /// <param name="traceId">
    /// A30-P5-07 — the caller's own document trace, carried onto the production order so the whole
    /// chain (sale order → production order → ... → finished goods receipt) shows on one timeline.
    /// Omitted, the order starts a trace of its own.
    /// </param>
    Task<Guid> EnsureForSourceAsync(
        Guid variantUuid, decimal quantity, DateTime requiredDate, int priority,
        string sourceType, Guid sourceUuid, Guid? sourceLineUuid, string? sourceReference,
        int userId, Guid? traceId = null, CancellationToken ct = default);
}

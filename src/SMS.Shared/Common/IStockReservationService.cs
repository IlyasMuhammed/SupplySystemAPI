namespace SMS.Shared.Common;

/// <summary>
/// Who is holding the stock. A reservation is meaningless without knowing what to release it
/// against, and every consumer needs to find its own rows.
/// </summary>
public static class ReservationSourceType
{
    /// <summary>Material issue request — reserved on approval, consumed when the MIV posts.</summary>
    public const string Mir = "MIR";

    /// <summary>Delivery order — reserved on release, consumed at goods issue.</summary>
    public const string Delivery = "DELIVERY";

    /// <summary>Sales order. Not in use yet; the ledger is shaped for it so it needs no schema change.</summary>
    public const string SalesOrder = "SALES_ORDER";
}

/// <summary>One line of stock to hold.</summary>
/// <param name="WarehouseUuid">
/// Which warehouse to reserve from. When null the service picks the one with the most available
/// stock, which is what MIR approval already does.
/// </param>
public sealed record ReservationRequest(
    Guid     VariantUuid,
    Guid?    WarehouseUuid,
    decimal  Quantity,
    Guid?    SourceLineUuid = null);

/// <param name="Shortfall">How much could not be held. Zero when the line was satisfied in full.</param>
public sealed record ReservationLineResult(
    Guid     VariantUuid,
    Guid?    SourceLineUuid,
    decimal  Requested,
    decimal  Reserved,
    decimal  Shortfall,
    decimal  Available,
    string?  Reason);

/// <param name="Succeeded">
/// True only when every line was held in full. A reservation that partly succeeded holds nothing:
/// see <see cref="IStockReservationService.ReserveAsync"/>.
/// </param>
public sealed record ReservationResult(
    bool                                Succeeded,
    IReadOnlyList<ReservationLineResult> Lines)
{
    public IEnumerable<ReservationLineResult> Shortfalls => Lines.Where(l => l.Shortfall > 0);
}

/// <summary>
/// What one variant's best single warehouse could cover right now.
/// </summary>
/// <param name="Available">
/// The total free stock in the <b>best single warehouse</b> — summed across every bin, batch and
/// serial row within it, because a picker can walk to a second bin — but never summed across
/// warehouses, because a reservation draws from one. A figure summed across warehouses would show
/// a line as coverable when no single location can ship it.
/// </param>
public sealed record VariantAvailability(
    Guid    VariantUuid,
    Guid?   WarehouseUuid,
    string? WarehouseName,
    decimal Available);

public sealed record ReservationSummary(
    Guid    Uuid,
    Guid    VariantUuid,
    Guid    WarehouseUuid,
    decimal ReservedQty,
    string  Status,
    Guid?   SourceLineUuid);

/// <summary>
/// One hold, told as a place to walk to and something to take off a shelf.
/// <para>
/// This is <see cref="ReservationSummary"/> plus the location and batch detail a pick list needs.
/// It is deliberately a read of what is <em>already held</em> rather than a fresh choice of stock:
/// the allocation was made when the stock was reserved, and re-deciding it at pick time would let
/// the picker walk to a bin the reservation is not holding.
/// </para>
/// </summary>
/// <param name="ExpiryDate">Null for stock that does not expire. Present rows were chosen FEFO.</param>
/// <param name="BinCode">
/// Null when the stock has not been put away to a bin — which is the normal state for goods
/// received but not yet located, so a pick list has to be able to say "somewhere in this
/// warehouse" rather than pretend to know.
/// </param>
public sealed record StockAllocation(
    Guid      ReservationUuid,
    Guid      VariantUuid,
    Guid?     SourceLineUuid,
    decimal   Quantity,
    Guid      WarehouseUuid,
    string    WarehouseName,
    string?   ZoneName,
    string?   BinCode,
    string?   BatchNumber,
    string?   SerialNumber,
    DateTime? ExpiryDate);

/// <summary>
/// The one place stock is held and released, for every module that needs to.
/// <para>
/// Availability is <c>InventoryItem.QtyOnHand − QtyReserved</c>, so a reservation is really two
/// writes: a detail row saying who holds what, and an increment of that counter. Keeping both
/// behind this interface is what stops them drifting — and means a second, third or fourth
/// consumer (deliveries today, sales orders later) needs no new table and no new reconciliation.
/// </para>
/// <para>
/// Implemented in SMS.Modules.Inventory, where the counter lives, and resolved through DI so
/// callers need no project reference to it.
/// </para>
/// </summary>
public interface IStockReservationService
{
    /// <summary>
    /// Holds stock for a source document. <b>All or nothing</b>: if any line cannot be held in
    /// full, nothing is held and the result explains which lines fell short and by how much.
    /// Partially reserving would leave a document promising units it does not have, which is
    /// worse than refusing.
    /// </summary>
    Task<ReservationResult> ReserveAsync(
        string sourceType,
        Guid sourceUuid,
        IReadOnlyList<ReservationRequest> requests,
        int userId,
        CancellationToken ct = default);

    /// <summary>
    /// Frees every active reservation for a source document and decrements the counter.
    /// Idempotent: releasing twice frees nothing the second time. Returns how many were released.
    /// </summary>
    Task<int> ReleaseBySourceAsync(
        string sourceType, Guid sourceUuid, string reason, int userId, CancellationToken ct = default);

    /// <summary>
    /// Marks a source's reservations consumed — the stock has actually left, so the hold ends but
    /// the units are gone rather than returned to available. Decrements the counter exactly as a
    /// release does; the distinction is for audit, not arithmetic.
    /// </summary>
    Task<int> ConsumeBySourceAsync(
        string sourceType, Guid sourceUuid, int userId, CancellationToken ct = default);

    /// <summary>
    /// Consumes <paramref name="quantity"/> from one line's hold, leaving the rest reserved.
    /// <para>
    /// Needed because a document is not always fulfilled in one go: a material issue request can
    /// be issued across several vouchers, each taking part of what was held and leaving the
    /// balance reserved for the next. Closing the whole hold on the first issue would hand back
    /// stock the request is still waiting for, letting something else take it.
    /// </para>
    /// <para>
    /// The hold closes as CONSUMED once nothing is left. Consuming more than remains takes only
    /// what is there. Returns the quantity actually consumed.
    /// </para>
    /// </summary>
    Task<decimal> ConsumeLineAsync(
        string sourceType,
        Guid sourceUuid,
        Guid sourceLineUuid,
        decimal quantity,
        int userId,
        CancellationToken ct = default);

    /// <summary>
    /// Gives back part of <b>one</b> hold, leaving the rest of it active. Returns how much was
    /// actually freed.
    /// <para>
    /// This is what a short pick needs. When a picker is sent for 30 and finds 22, the other 8 are
    /// not going to ship, and the hold has to shrink to match — otherwise the goods issue that
    /// follows would consume 30 and deduct eight units that never moved. Addressed by reservation
    /// rather than by source line because one line's stock can be held across several bins, and
    /// only the bin that came up short should shrink.
    /// </para>
    /// <para>
    /// Freeing more than remains frees only what is there, and the hold closes as RELEASED once
    /// nothing is left. A reservation that is already closed frees nothing.
    /// </para>
    /// </summary>
    Task<decimal> ReleaseAllocationAsync(
        Guid reservationUuid, decimal quantity, string reason, int userId,
        CancellationToken ct = default);

    /// <summary>Every reservation held for a source document, whatever its status.</summary>
    Task<IReadOnlyList<ReservationSummary>> GetBySourceAsync(
        string sourceType, Guid sourceUuid, CancellationToken ct = default);

    /// <summary>
    /// The <b>active</b> holds for a source document, each with the location and batch it sits in,
    /// so a pick list can be written from them. Ordered as the stock was allocated — FEFO, then
    /// arrival order.
    /// </summary>
    Task<IReadOnlyList<StockAllocation>> GetAllocationsAsync(
        string sourceType, Guid sourceUuid, CancellationToken ct = default);

    /// <summary>
    /// What could be reserved right now, per variant, <b>using the same warehouse-selection rule
    /// as <see cref="ReserveAsync"/></b>.
    /// <para>
    /// Answered here rather than from a general availability query so the two cannot disagree: a
    /// screen that offers to release a line the reservation then refuses is worse than no
    /// preview at all. Variants with no stock record are absent from the result.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<VariantAvailability>> GetAvailableAsync(
        IReadOnlyList<Guid> variantUuids, Guid? warehouseUuid, CancellationToken ct = default);
}

namespace SMS.Modules.Demand.Services;

/// <summary>
/// A29-P5-07 §13.3 — the seven event_type values a sale order's trace carries, the one place their
/// spelling lives. Timeline events are free-form strings inside DocumentTimelines' JSON array (there
/// is no column, constraint or registry to add a value to), so "adding" one means naming it here and
/// emitting it at its lifecycle point.
/// <para>
/// <see cref="SoFulfilled"/> is emitted by <see cref="SaleOrderFulfillmentService"/> when the order's
/// last delivery reaches the customer (A29-P6-06). <see cref="SoInvoiced"/> is defined but not emitted
/// yet: its lifecycle point (a sales invoice) belongs to a section of the addendum that is not built.
/// Whoever builds it emits this, rather than inventing a second spelling.
/// </para>
/// </summary>
public static class SaleOrderTimelineEventTypes
{
    public const string SoCreated        = "SO_CREATED";
    public const string SoConfirmed      = "SO_CONFIRMED";
    public const string PoCreatedFromSo  = "PO_CREATED_FROM_SO";
    public const string GrnForSoPo       = "GRN_FOR_SO_PO";
    public const string SoStockReserved  = "SO_STOCK_RESERVED";
    public const string SoFulfilled      = "SO_FULFILLED";
    public const string SoInvoiced       = "SO_INVOICED";

    public static IReadOnlyList<string> All { get; } =
    [
        SoCreated, SoConfirmed, PoCreatedFromSo, GrnForSoPo, SoStockReserved, SoFulfilled, SoInvoiced
    ];

    /// <summary>
    /// SAP alignment (S-7) — an issued sales invoice of the order was cancelled (reversed), emitted by
    /// Finance's SalesInvoiceService. Like <c>SO_CANCELLED</c>, it is outside §13.3's seven and so not in
    /// <see cref="All"/>; it is named here so its spelling still lives in one place.
    /// </summary>
    public const string SoInvoiceCancelled = "SO_INVOICE_CANCELLED";

    /// <summary>A32 C4 — stock held for the order was given back by hand (manual release). Outside §13.3's seven, like the two above.</summary>
    public const string SoStockReleased = "SO_STOCK_RELEASED";

    /// <summary>A33 D-1 — the order's deliveries were created (at confirm, or by the D-12 sweep). Outside §13.3's seven.</summary>
    public const string SoDeliveriesCreated = "SO_DELIVERIES_CREATED";
    /// <summary>A33 D-1 — creating them after the confirm failed; the D-12 sweep or the recovery button finishes it.</summary>
    public const string SoDeliveryCreationFailed = "SO_DELIVERY_CREATION_FAILED";
    /// <summary>A33 D-15 — cancelling the order cancelled its open deliveries; issued ones are listed for a manual reversal.</summary>
    public const string SoDeliveriesCancelled = "SO_DELIVERIES_CANCELLED";
}

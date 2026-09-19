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
}

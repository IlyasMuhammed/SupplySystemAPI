namespace SMS.Modules.Reports.Models;

// ── Shared ────────────────────────────────────────────────────────────────────

public class ReportDateFilter
{
    public string? DateFrom  { get; set; }
    public string? DateTo    { get; set; }
    public string? Search    { get; set; }
    public int     Page      { get; set; } = 1;
    public int     PageSize  { get; set; } = 100;
}

// ── KPI Dashboard ─────────────────────────────────────────────────────────────

public class KpiDashboardModel
{
    public double  PoCycleTimeDays           { get; set; }
    public double  SupplierOnTimeDeliveryRate { get; set; }
    public double  PoFillRate                { get; set; }
    public double  StockTurnoverRatio        { get; set; }
    public double  InventoryAccuracy         { get; set; }
    public double  InvoiceProcessingTimeDays { get; set; }
    public double  ThreeWayMatchRate         { get; set; }
    public double  BudgetVariancePercent     { get; set; }
    public double  GrnRejectionRate          { get; set; }
    public int     ReorderTriggerCount       { get; set; }
}

// ── Supplier Performance ──────────────────────────────────────────────────────

public class SupplierPerformanceItem
{
    public string  SupplierId           { get; set; } = string.Empty;
    public string  SupplierName         { get; set; } = string.Empty;
    public int     PoCount              { get; set; }
    public decimal TotalSpend           { get; set; }
    public double  OnTimeDeliveryRate   { get; set; }
    public double  QualityScore         { get; set; }
    public int     GrnCount             { get; set; }
    public int     RejectedGrnCount     { get; set; }
}

// ── PO Summary ────────────────────────────────────────────────────────────────

public class PoSummaryItem
{
    public string  Status      { get; set; } = string.Empty;
    public int     Count       { get; set; }
    public decimal TotalValue  { get; set; }
}

public class SpendBySupplierItem
{
    public string   SupplierId   { get; set; } = string.Empty;
    public string   SupplierName { get; set; } = string.Empty;
    public int      PoCount      { get; set; }
    public decimal  TotalSpend   { get; set; }
    public DateTime? LatestPoDate { get; set; }
}

// ── Pending Approvals ─────────────────────────────────────────────────────────

public class PendingApprovalItem
{
    public string   Module        { get; set; } = string.Empty;
    public string   EntityType    { get; set; } = string.Empty;
    public string   EntityNumber  { get; set; } = string.Empty;
    public Guid     EntityUuid    { get; set; }
    public string   Status        { get; set; } = string.Empty;
    public DateTime CreatedDate   { get; set; }
    public string?  Description   { get; set; }
}

// ── Stock Level ───────────────────────────────────────────────────────────────

public class StockLevelItem
{
    public string   ProductName    { get; set; } = string.Empty;
    public string?  Sku            { get; set; }
    public string?  WarehouseName  { get; set; }
    public decimal  QtyOnHand      { get; set; }
    public decimal  QtyReserved    { get; set; }
    public decimal  QtyAvailable   { get; set; }
    public decimal  UnitCost       { get; set; }
    public decimal  StockValue     { get; set; }
}

// ── PR Fulfillment Status (SFM-009) ───────────────────────────────────────────
//
// FulfillmentStatus values: FULLY_FULFILLED | PARTIALLY_FULFILLED | UNFULFILLED | CANCELLED

public class PrFulfillmentFilter
{
    public DateTime? DateFrom          { get; set; }
    public DateTime? DateTo            { get; set; }
    public string?   Department        { get; set; }
    public string?   FulfillmentStatus { get; set; }
}

public class PrFulfillmentItem
{
    public Guid      PrUuid                { get; set; }
    public string    PrNumber              { get; set; } = string.Empty;
    public string?   Department            { get; set; }
    public string    Requester             { get; set; } = string.Empty;
    public DateTime? ApprovalDate          { get; set; }
    public int       TotalLines            { get; set; }
    public int       FulfilledLines        { get; set; }
    public int       PendingLines          { get; set; }
    public double    FulfillmentPercentage { get; set; }
    public string    FulfillmentStatus     { get; set; } = string.Empty;
}

// ── PR-to-PO Pipeline (SFM-009) ───────────────────────────────────────────────

public class PrPipelineFilter
{
    public DateTime? DateFrom   { get; set; }
    public DateTime? DateTo     { get; set; }
    public string?   Department { get; set; }
}

public class PrPipelineItem
{
    public Guid    PrUuid             { get; set; }
    public string  PrNumber           { get; set; } = string.Empty;
    public string? Department         { get; set; }
    public string  FurthestStage      { get; set; } = string.Empty;
    public bool    ResolvedViaTraceId { get; set; }
    public string  FulfillmentStatus  { get; set; } = string.Empty;
}

public class PrPipelineReport
{
    public int  TotalPrs                { get; set; }
    public int  FullyFulfilledCount      { get; set; }
    public int  PartiallyFulfilledCount  { get; set; }
    public int  UnfulfilledCount         { get; set; }
    public int  CancelledCount           { get; set; }
    public List<PrPipelineItem> Items    { get; set; } = [];
}

// ── Stock Level Summary (SFM-008) ─────────────────────────────────────────────

public class StockLevelSummaryFilter
{
    public int?    WarehouseId   { get; set; }
    public int?    CategoryId    { get; set; }
    public int?    SubCategoryId { get; set; }
    public string? Search        { get; set; }
}

public class StockLevelSummaryItem
{
    public string   ProductCode       { get; set; } = string.Empty;
    public string   ProductName       { get; set; } = string.Empty;
    public string?  Category          { get; set; }
    public string?  SubCategory       { get; set; }
    public string?  Warehouse         { get; set; }
    public decimal  QtyOnHand         { get; set; }
    public decimal  QtyReserved       { get; set; }
    public decimal  QtyAvailable      { get; set; }
    public decimal? ReorderPoint      { get; set; }
    public decimal  UnitCost          { get; set; }
    public decimal  TotalValue        { get; set; }
    public bool     BelowReorderLevel { get; set; }
}

public class StockLevelSummaryReport
{
    public List<StockLevelSummaryItem> Items { get; set; } = [];
    public decimal GrandTotalValue { get; set; }
}

// ── Reorder Alert ─────────────────────────────────────────────────────────────

public class ReorderAlertItem
{
    public string   ProductName           { get; set; } = string.Empty;
    public string?  Sku                   { get; set; }
    public string?  WarehouseName         { get; set; }
    public decimal  QtyOnHand             { get; set; }
    public decimal  ReorderPoint          { get; set; }
    public decimal  ReorderQty            { get; set; }
    public decimal  Shortfall             { get; set; }
}

// ── Inventory Valuation ───────────────────────────────────────────────────────

public class InventoryValuationItem
{
    public string?  WarehouseName  { get; set; }
    public int      ProductCount   { get; set; }
    public decimal  TotalQty       { get; set; }
    public decimal  TotalValue     { get; set; }
}

// ── GRN vs PO Variance ────────────────────────────────────────────────────────

public class GrnVarianceItem
{
    public string   GrnNumber      { get; set; } = string.Empty;
    public string   PoNumber       { get; set; } = string.Empty;
    public string   SupplierName   { get; set; } = string.Empty;
    public DateTime ReceivedAt     { get; set; }
    public decimal  TotalOrdered   { get; set; }
    public decimal  TotalReceived  { get; set; }
    public decimal  TotalAccepted  { get; set; }
    public decimal  TotalRejected  { get; set; }
    public decimal  VarianceQty    { get; set; }
    public string   Status         { get; set; } = string.Empty;
}

// ── Shipment Tracker ──────────────────────────────────────────────────────────

public class ShipmentTrackerItem
{
    public string    ShipmentNumber   { get; set; } = string.Empty;
    public string    PoNumber         { get; set; } = string.Empty;
    public string?   CarrierName      { get; set; }
    public string    ShipmentType     { get; set; } = string.Empty;
    public string    Status           { get; set; } = string.Empty;
    public DateTime  DispatchDate     { get; set; }
    public DateTime  EstimatedArrival { get; set; }
    public DateTime? ActualArrival    { get; set; }
    public bool      IsOverdue        { get; set; }
    public string?   TrackingNumber   { get; set; }
    public string?   TrackingUrl      { get; set; }
}

// ── Invoice Aging ─────────────────────────────────────────────────────────────

public class InvoiceAgingItem
{
    public string   InvoiceNumber    { get; set; } = string.Empty;
    public string?  SupplierInvoiceNo { get; set; }
    public string   SupplierName     { get; set; } = string.Empty;
    public DateTime DueDate          { get; set; }
    public decimal  TotalAmount      { get; set; }
    public string   PaymentStatus    { get; set; } = string.Empty;
    public int      DaysOverdue      { get; set; }
    public string   AgingBucket      { get; set; } = string.Empty;
}

public class InvoiceAgingBucketSummary
{
    public string  Bucket          { get; set; } = string.Empty;
    public int     Count           { get; set; }
    public decimal TotalAmount     { get; set; }
}

// ── Payment Summary ───────────────────────────────────────────────────────────

public class PaymentSummaryModel
{
    public decimal TotalProcessed  { get; set; }
    public int     ProcessedCount  { get; set; }
    public decimal TotalPending    { get; set; }
    public int     PendingCount    { get; set; }
    public decimal TotalReversed   { get; set; }
    public int     ReversedCount   { get; set; }
    public List<PaymentByMethodItem> ByMethod { get; set; } = new();
}

public class PaymentByMethodItem
{
    public string  Method      { get; set; } = string.Empty;
    public int     Count       { get; set; }
    public decimal TotalAmount { get; set; }
}

// ── Supplier Ledger Summary (SC-001) ────────────────────────────────────────────

public class SupplierLedgerSummaryFilter
{
    public string? DateFrom       { get; set; }
    public string? DateTo         { get; set; }
    /// <summary>Filters on Supplier.TypeMappings — this system has no separate "category" concept;
    /// SupplierType is the closest existing classification (same field SupplierListFilter.SupplierType filters on).</summary>
    public Guid?   SupplierCategory { get; set; }
    public string? SupplierStatus   { get; set; }
    public decimal? MinOutstanding  { get; set; }
    /// <summary>outstanding_balance (default) | supplier_name | last_transaction_date</summary>
    public string? SortBy           { get; set; }
}

public class SupplierLedgerSummaryItem
{
    public Guid      SupplierId          { get; set; }
    public string    SupplierName        { get; set; } = string.Empty;
    public string?   SupplierCode        { get; set; }
    public decimal   TotalInvoiced       { get; set; }
    public decimal   TotalPaid           { get; set; }
    public decimal   OutstandingBalance  { get; set; }
    public decimal   AdvanceBalance      { get; set; }
    public DateTime? LastTransactionDate { get; set; }
    /// <summary>Drill-down into the per-supplier ledger (Addendum 22), carrying this report's date range.</summary>
    public string    DrillDownUrl        { get; set; } = string.Empty;
}

public class SupplierLedgerSummaryReport
{
    public List<SupplierLedgerSummaryItem> Items { get; set; } = [];
    public decimal GrandTotalInvoiced    { get; set; }
    public decimal GrandTotalPaid        { get; set; }
    public decimal GrandTotalOutstanding { get; set; }
}

// ── Supplier Orders Report (SC-002, FSD Addendum 23) ────────────────────────────

public class SupplierOrdersFilter
{
    public string? DateFrom            { get; set; }
    public string? DateTo              { get; set; }
    public Guid?   SupplierId          { get; set; }
    public string? PoStatus            { get; set; }
    /// <summary>on_time | late | all (default: all)</summary>
    public string? DeliveryPerformance { get; set; }
}

public class SupplierOrderDetailItem
{
    public Guid      PoUuid               { get; set; }
    public string    PoNumber             { get; set; } = string.Empty;
    public DateTime  PoDate               { get; set; }
    public decimal   TotalAmount          { get; set; }
    public string    Status               { get; set; } = string.Empty;
    public decimal   QtyOrdered           { get; set; }
    public decimal   QtyReceived          { get; set; }
    public int       GrnCount             { get; set; }
    public DateTime? ExpectedDeliveryDate { get; set; }
    public DateTime? FirstGrnReceivedAt   { get; set; }
    /// <summary>first GRN ReceivedAt minus PO ExpectedDeliveryDate, in days. Negative = early, 0 = on time,
    /// positive = late. Null when there's no expected delivery date or no GRN has been received yet.</summary>
    public int?      DeliveryVarianceDays { get; set; }
    /// <summary>green (on time/early) | amber (1-7 days late) | red (8+ days late) | null (no variance to show).</summary>
    public string?   DeliveryColor        { get; set; }
}

public class SupplierOrdersSummaryItem
{
    public Guid    SupplierId              { get; set; }
    public string  SupplierName            { get; set; } = string.Empty;
    public int     TotalPoCount            { get; set; }
    public decimal TotalPoValue            { get; set; }
    public decimal AvgPoValue              { get; set; }
    public int     FullyReceivedCount      { get; set; }
    public int     PartiallyReceivedCount  { get; set; }
    public int     PendingCount            { get; set; }
    public int     CancelledCount          { get; set; }
    /// <summary>Every PO for this supplier (post-filter) — embedded so the frontend can drill down without
    /// a page reload or a second request.</summary>
    public List<SupplierOrderDetailItem> PurchaseOrders { get; set; } = [];
}

public class SupplierOrdersReport
{
    public List<SupplierOrdersSummaryItem> Suppliers { get; set; } = [];
    public int     GrandTotalPoCount { get; set; }
    public decimal GrandTotalPoValue { get; set; }
}

// ── Supplier Comparison (SC-008) ──────────────────────────────────────────────

public class SupplierComparisonColumn
{
    public Guid      SupplierId              { get; set; }
    public string    SupplierName            { get; set; } = string.Empty;
    public string?   Grade                   { get; set; }
    public decimal?  CompositeScore          { get; set; }
    public int       PoCount                 { get; set; }
    public decimal   TotalPoValue            { get; set; }
    /// <summary>Average (first GRN ReceivedAt - PO DeliveryDate) across POs with both dates known. Null
    /// when this supplier has no such comparable POs.</summary>
    public decimal?  AvgDeliveryVarianceDays { get; set; }
    /// <summary>% of inspected GRN lines that did not pass (Fail or PartialPass).</summary>
    public decimal   RejectionRatePercent    { get; set; }
    public decimal   TotalInvoiced           { get; set; }
    public decimal   TotalPaid               { get; set; }
    public decimal   OutstandingBalance      { get; set; }
}

public class SupplierComparisonResponse
{
    public List<SupplierComparisonColumn> Suppliers { get; set; } = [];
}

// ── GRN Quality Analysis (SC-008) ─────────────────────────────────────────────

public class GrnQualityAnalysisFilter
{
    public string? DateFrom   { get; set; }
    public string? DateTo     { get; set; }
    public Guid?   SupplierId { get; set; }
}

public class GrnQualityMonthlyPoint
{
    public string  Month       { get; set; } = string.Empty; // "yyyy-MM"
    public decimal PassRate    { get; set; }
    public decimal FailRate    { get; set; }
    public decimal PartialRate { get; set; }
    public int     TotalLines  { get; set; }
}

public class SupplierGrnQualityItem
{
    public Guid    SupplierId      { get; set; }
    public string  SupplierName    { get; set; } = string.Empty;
    public decimal OverallPassRate { get; set; }
    public int     TotalLines      { get; set; }
    public List<GrnQualityMonthlyPoint> MonthlyTrend { get; set; } = [];
}

public class FailedGrnLineItem
{
    public Guid      GrnId            { get; set; }
    public string?   GrnNumber        { get; set; }
    public Guid      SupplierId       { get; set; }
    public string    SupplierName     { get; set; } = string.Empty;
    public string    ItemDescription  { get; set; } = string.Empty;
    public string?   InspectionResult { get; set; }
    public string?   RejectionReason  { get; set; }
    public DateTime? InspectedAt      { get; set; }
}

public class GrnQualityAnalysisResponse
{
    public List<SupplierGrnQualityItem> Suppliers   { get; set; } = [];
    public List<FailedGrnLineItem>      FailedLines { get; set; } = [];
}

// ── Supplier Spend Analysis (SC-008) ──────────────────────────────────────────

public class SupplierSpendFilter
{
    public string? DateFrom { get; set; }
    public string? DateTo   { get; set; }
}

public class SupplierSpendItem
{
    public Guid    SupplierId    { get; set; }
    public string  SupplierName  { get; set; } = string.Empty;
    public decimal TotalInvoiced { get; set; }
}

public class CategorySpendItem
{
    public string  Category      { get; set; } = string.Empty;
    public string? SubCategory   { get; set; }
    public decimal TotalInvoiced { get; set; }
}

public class SupplierSpendAnalysisResponse
{
    public decimal GrandTotalSpend          { get; set; }
    /// <summary>Top 10 suppliers by invoiced amount, descending.</summary>
    public List<SupplierSpendItem> TopSuppliers { get; set; } = [];
    /// <summary>Top-5 suppliers' combined spend as a % of GrandTotalSpend.</summary>
    public decimal Top5ConcentrationPercent { get; set; }
    public List<CategorySpendItem> SpendByCategory { get; set; } = [];
}

// ── Delivery Performance Heatmap (SC-008) ─────────────────────────────────────

public class DeliveryHeatmapDayItem
{
    public DateTime Date         { get; set; }
    public bool     HasGrn       { get; set; }
    public int?     VarianceDays { get; set; }
    /// <summary>green (on time/early) | amber (1-7 late) | red (8+ late) | grey (no GRN that day).</summary>
    public string   Color        { get; set; } = "grey";
}

public class DeliveryHeatmapResponse
{
    public Guid   SupplierId   { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public int    Year         { get; set; }
    public List<DeliveryHeatmapDayItem> Days { get; set; } = [];
}

// ── Budget Utilization ────────────────────────────────────────────────────────

public class BudgetUtilizationItem
{
    public string?  BudgetCode       { get; set; }
    public string?  Department       { get; set; }
    public int      PoCount          { get; set; }
    public decimal  EstimatedAmount  { get; set; }
    public decimal  ActualSpend      { get; set; }
    public decimal  VarianceAmount   { get; set; }
    public double   VariancePercent  { get; set; }
}

// ── Audit Trail ───────────────────────────────────────────────────────────────

public class AuditLogFilter
{
    public string? Module     { get; set; }
    public string? Action     { get; set; }
    public string? EntityType { get; set; }
    public Guid?   EntityId   { get; set; }
    public int?    UserId     { get; set; }
    public string? DateFrom   { get; set; }
    public string? DateTo     { get; set; }
    public int     Page       { get; set; } = 1;
    public int     PageSize   { get; set; } = 50;
}

public class AuditLogItemModel
{
    public Guid     UUID         { get; set; }
    public DateTime Timestamp    { get; set; }
    public int?     UserId       { get; set; }
    public string?  UserName     { get; set; }
    public string   Module       { get; set; } = string.Empty;
    public string   Action       { get; set; } = string.Empty;
    public string   EntityType   { get; set; } = string.Empty;
    public Guid?    EntityId     { get; set; }
    public string?  FieldChanged { get; set; }
    public string?  OldValue     { get; set; }
    public string?  NewValue     { get; set; }
    public string   IpAddress    { get; set; } = string.Empty;
    public string?  Notes        { get; set; }
}

// ── User Activity ─────────────────────────────────────────────────────────────

public class UserActivityItem
{
    public int      UserId        { get; set; }
    public string?  UserName      { get; set; }
    public int      TotalActions  { get; set; }
    public int      CreateCount   { get; set; }
    public int      UpdateCount   { get; set; }
    public int      DeleteCount   { get; set; }
    public int      ApproveCount  { get; set; }
    public DateTime? LastActionAt { get; set; }
}

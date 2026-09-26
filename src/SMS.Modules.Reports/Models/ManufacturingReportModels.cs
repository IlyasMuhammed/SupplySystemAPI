namespace SMS.Modules.Reports.Models;

// A30-P5-02..06. Twenty-eight reports are listed in FSD §31; most of the register/status ones are
// already served by an existing endpoint (a production order list, the shortage dashboard, the
// supply-requirement list, the allocation dashboard, the production-ledger query) that would just be
// duplicated by a Reports-module copy — see the task register's own DONE note on A30-P5-02..06 for the
// full per-report accounting. What follows are the five that filled a genuine gap: no existing
// endpoint aggregates across production orders, lists floor documents in one flat register, or walks
// a chained-manufacturing tree.

// ── R4/R5/R16 — Production Efficiency & WIP Summary ─────────────────────────

public class ManufacturingReportFilter
{
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo   { get; set; }
}

public class ProductionEfficiencyReport
{
    public DateTime  GeneratedAt   { get; set; } = DateTime.UtcNow;
    public DateTime? DateFrom      { get; set; }
    public DateTime? DateTo        { get; set; }
    public int        TotalOrders  { get; set; }
    public Dictionary<string, int> CountByStatus { get; set; } = [];
    public decimal    TotalPlannedQuantity  { get; set; }
    public decimal    TotalProducedQuantity { get; set; }
    public decimal    TotalAcceptedQuantity { get; set; }
    public decimal    TotalRejectedQuantity { get; set; }
    /// <summary>Accepted over produced; null when nothing has been produced yet.</summary>
    public decimal?   OverallYieldPercent   { get; set; }
    /// <summary>Completed orders only: actual end date on or before the required date.</summary>
    public decimal?   OnTimeCompletionPercent { get; set; }
    /// <summary>Completed orders only: calendar days from creation to actual completion, averaged.</summary>
    public decimal?   AverageCycleDays        { get; set; }
}

// ── R6/R7 — Quality & Scrap Summary ──────────────────────────────────────────

public class QualityScrapReport
{
    public DateTime  GeneratedAt  { get; set; } = DateTime.UtcNow;
    public DateTime? DateFrom     { get; set; }
    public DateTime? DateTo       { get; set; }
    public int        TotalInspections  { get; set; }
    public decimal     TotalInspectedQty { get; set; }
    public decimal     TotalAcceptedQty  { get; set; }
    public decimal     TotalRejectedQty  { get; set; }
    public decimal     TotalHoldQty      { get; set; }
    public decimal     TotalReworkQty    { get; set; }
    public decimal?    AcceptanceRatePercent { get; set; }
    public decimal?    RejectionRatePercent  { get; set; }
    public List<QualityScrapByProductItem> ByProduct { get; set; } = [];
}

public class QualityScrapByProductItem
{
    public Guid     ProductUuid   { get; set; }
    public string   ProductName   { get; set; } = string.Empty;
    public decimal  InspectedQty  { get; set; }
    public decimal  RejectedQty   { get; set; }
    public decimal? RejectionRatePercent { get; set; }
}

// ── R14/R15 — Manufacturing document registers ──────────────────────────────

public class ManufacturingRegisterFilter
{
    public DateTime? DateFrom      { get; set; }
    public DateTime? DateTo        { get; set; }
    public Guid?      WarehouseUuid { get; set; }
    public string?    Status        { get; set; }
    public int         Page         { get; set; } = 1;
    public int         PageSize     { get; set; } = 50;
}

public class ProductionMaterialIssueRegisterItem
{
    public Guid      IssueUuid           { get; set; }
    public string    IssueNumber         { get; set; } = string.Empty;
    public Guid      ProductionOrderUuid { get; set; }
    public string    ProductionNumber    { get; set; } = string.Empty;
    public string    OutputProductName   { get; set; } = string.Empty;
    public Guid      WarehouseUuid       { get; set; }
    public string    WarehouseName       { get; set; } = string.Empty;
    public string    IssueType           { get; set; } = string.Empty;
    public string    Status              { get; set; } = string.Empty;
    public decimal   TotalQuantity       { get; set; }
    public int        LineCount          { get; set; }
    public DateTime  CreatedAt           { get; set; }
    public DateTime? ConfirmedAt         { get; set; }
}

public class FinishedGoodsReceiptRegisterItem
{
    public Guid      FgrUuid             { get; set; }
    public string    FgrNumber           { get; set; } = string.Empty;
    public Guid      ProductionOrderUuid { get; set; }
    public string    ProductionNumber    { get; set; } = string.Empty;
    public string    ProductName         { get; set; } = string.Empty;
    public Guid      WarehouseUuid       { get; set; }
    public string    WarehouseName       { get; set; } = string.Empty;
    public decimal   TotalQuantity       { get; set; }
    public string    Status              { get; set; } = string.Empty;
    public DateTime  ReceivedAt          { get; set; }
}

// ── R24 — Production Ledger Reconciliation ──────────────────────────────────

public class LedgerReconciliationReport
{
    public DateTime GeneratedAt        { get; set; } = DateTime.UtcNow;
    public DateTime? DateFrom          { get; set; }
    public DateTime? DateTo            { get; set; }
    public int        TotalOrdersChecked { get; set; }
    public int         FlaggedCount      { get; set; }
    public List<LedgerReconciliationItem> Flagged { get; set; } = [];
}

public class LedgerReconciliationItem
{
    public Guid     ProductionOrderUuid   { get; set; }
    public string   ProductionNumber      { get; set; } = string.Empty;
    public string   Status                { get; set; } = string.Empty;
    public decimal  AcceptedQuantity      { get; set; }
    public decimal  FinishedGoodsCredited { get; set; }
    public decimal  RejectedQuantity      { get; set; }
    public bool     HasMaterialIssues     { get; set; }
    public bool     HasQualityInspection  { get; set; }
    public string   Reason                { get; set; } = string.Empty;
}

// ── R26/R27/R28 — Chained manufacturing dependency tree ──────────────────────

public class ChainedManufacturingReport
{
    public Guid     RootProductionOrderUuid { get; set; }
    public string   RootProductionNumber    { get; set; } = string.Empty;
    public int      TotalOrdersInChain      { get; set; }
    public int      MaxDepth                { get; set; }
    /// <summary>Root's own creation to the latest completion in the chain, in days; null while any node is still open.</summary>
    public decimal? TotalCycleDays          { get; set; }
    public ChainedManufacturingNode Root    { get; set; } = new();
}

public class ChainedManufacturingNode
{
    public Guid      ProductionOrderUuid { get; set; }
    public string    ProductionNumber    { get; set; } = string.Empty;
    public string    ProductName         { get; set; } = string.Empty;
    public decimal   PlannedQuantity     { get; set; }
    public decimal   AcceptedQuantity    { get; set; }
    public string    Status              { get; set; } = string.Empty;
    public DateTime  CreatedAt           { get; set; }
    public DateTime? ActualEndDate       { get; set; }
    public decimal?  CycleDays           { get; set; }
    public int        Depth              { get; set; }
    public List<ChainedManufacturingNode> Children { get; set; } = [];
}

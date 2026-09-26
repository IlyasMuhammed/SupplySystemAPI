namespace SMS.Modules.Material.Models;

// ── Quality inspection (A30 §18) ────────────────────────────────────────────────

public class CreateQualityInspectionLineRequest
{
    public string  CheckName       { get; set; } = string.Empty;
    /// <summary>PASS, FAIL, HOLD or REWORK — the line's whole checked quantity goes to this one outcome.</summary>
    public string  Result          { get; set; } = "PASS";
    public decimal QuantityChecked { get; set; }
    public string? DefectCode      { get; set; }
    public string? Notes           { get; set; }
}

public class CreateQualityInspectionRequest
{
    public List<CreateQualityInspectionLineRequest> Lines { get; set; } = [];
    public string? Notes { get; set; }
}

public class QualityInspectionLineModel
{
    public Guid    UUID            { get; set; }
    public string  CheckName       { get; set; } = string.Empty;
    public string  Result          { get; set; } = string.Empty;
    public decimal QuantityChecked { get; set; }
    public string? DefectCode      { get; set; }
    public string? Notes           { get; set; }
}

public class QualityInspectionModel
{
    public Guid      UUID                { get; set; }
    public string    InspectionNumber    { get; set; } = string.Empty;
    public Guid      ProductionOrderUuid { get; set; }
    public string    ProductionNumber    { get; set; } = string.Empty;
    public decimal   InspectedQuantity   { get; set; }
    public decimal   AcceptedQuantity    { get; set; }
    public decimal   RejectedQuantity    { get; set; }
    public decimal   HoldQuantity        { get; set; }
    public decimal   ReworkQuantity      { get; set; }
    public string    OverallResult       { get; set; } = string.Empty;
    public int       InspectedBy         { get; set; }
    public DateTime  InspectedAt         { get; set; }
    public string?   Notes               { get; set; }
    /// <summary>What is left of the accepted quantity that no FGR has received yet.</summary>
    public decimal   OutstandingForFgr   { get; set; }
    public List<QualityInspectionLineModel> Lines { get; set; } = [];
}

// ── Finished goods receipt (A30 §19) ─────────────────────────────────────────────

public class CreateFinishedGoodsReceiptRequest
{
    public decimal Quantity      { get; set; }
    public Guid?   WarehouseUuid { get; set; }
    public string? Notes         { get; set; }
    /// <summary>Confirm (credit inventory) straight away. Off by default, matching the production issue dialog's shape.</summary>
    public bool    Confirm       { get; set; }
}

public class FinishedGoodsReceiptModel
{
    public Guid      UUID                { get; set; }
    public string    FgrNumber           { get; set; } = string.Empty;
    public Guid      ProductionOrderUuid { get; set; }
    public string    ProductionNumber    { get; set; } = string.Empty;
    public Guid      QualityInspectionUuid { get; set; }
    public Guid      WarehouseUuid       { get; set; }
    public string    WarehouseName       { get; set; } = string.Empty;
    public decimal   TotalQuantity       { get; set; }
    public string    Status              { get; set; } = string.Empty;
    public int       ReceivedBy          { get; set; }
    public DateTime  ReceivedAt          { get; set; }
    public string?   Notes               { get; set; }
}

// ── Production ledger (A30 §19A) — a query over what MI/FGR/QI already recorded, not a stored table ──

public class ProductionLedgerEntryModel
{
    public Guid      ProductionOrderUuid   { get; set; }
    public string    ProductionNumber      { get; set; } = string.Empty;
    /// <summary>DEBIT (material consumed / scrapped) or CREDIT (finished goods produced / material returned).</summary>
    public string    EntryType             { get; set; } = string.Empty;
    public Guid      VariantUuid           { get; set; }
    public string    ProductName           { get; set; } = string.Empty;
    public string    VariantName           { get; set; } = string.Empty;
    public decimal   Quantity              { get; set; }
    public string    Uom                   { get; set; } = string.Empty;
    public Guid      WarehouseUuid         { get; set; }
    public string    WarehouseName         { get; set; } = string.Empty;
    public string    SourceDocumentType    { get; set; } = string.Empty;
    public string    SourceDocumentNumber  { get; set; } = string.Empty;
    public string    MovementType          { get; set; } = string.Empty;
    public DateTime  TransactionDate       { get; set; }
    public string?   Notes                 { get; set; }
}

public class ProductionLedgerSummaryModel
{
    public int      MaterialsConsumedCount { get; set; }
    public decimal  FinishedGoodsQuantity  { get; set; }
    public decimal  ScrapQuantity          { get; set; }
    /// <summary>Finished goods over (finished goods + scrap); null when nothing has been produced or scrapped yet.</summary>
    public decimal? YieldPercent           { get; set; }
}

public class ProductionLedgerModel
{
    public Guid      ProductionOrderUuid { get; set; }
    public string    ProductionNumber    { get; set; } = string.Empty;
    public ProductionLedgerSummaryModel Summary { get; set; } = new();
    public List<ProductionLedgerEntryModel> Entries { get; set; } = [];
}

public class ProductionLedgerListFilter
{
    public Guid?     ProductionOrderUuid { get; set; }
    public Guid?     VariantUuid         { get; set; }
    public string?   EntryType           { get; set; }
    public string?   MovementType        { get; set; }
    public DateTime? DateFrom            { get; set; }
    public DateTime? DateTo              { get; set; }
    public int        Page               { get; set; } = 1;
    public int        PageSize           { get; set; } = 50;
}

namespace SMS.Modules.Reports.Models;

// ── Shared filter base ────────────────────────────────────────────────────────

public class MaterialReportFilter
{
    public string? DateFrom   { get; set; }
    public string? DateTo     { get; set; }
    public string? Search     { get; set; }
    public int     Page       { get; set; } = 1;
    public int     PageSize   { get; set; } = 100;
}

// ── 1. Material Issue Register ────────────────────────────────────────────────

public class MaterialIssueRegisterFilter : MaterialReportFilter
{
    public string? Status       { get; set; }
    public string? WarehouseId  { get; set; }
    public string? RequestType  { get; set; }
}

public class MaterialIssueRegisterItem
{
    public Guid     MivUuid      { get; set; }
    public string   IssueNo      { get; set; } = string.Empty;
    public string   MirNo        { get; set; } = string.Empty;
    public string   RequestType  { get; set; } = string.Empty;
    public string?  IssuedTo     { get; set; }
    public DateTime IssueDate    { get; set; }
    public string   Status       { get; set; } = string.Empty;
    public decimal  TotalValue   { get; set; }
    public int      LineCount    { get; set; }
    public string?  Notes        { get; set; }
    public DateTime CreatedDate  { get; set; }
    // PV-008 — every line's product+variant description (e.g. "Dell Latitude 5450 (i7 / 16GB /
    // 512GB)"), joined, since a register row is voucher-level and can span multiple products.
    public string   ItemsSummary { get; set; } = string.Empty;
}

// ── 2. Material Consumption Report ───────────────────────────────────────────

public class MaterialConsumptionReportFilter : MaterialReportFilter
{
    public string? SourceType   { get; set; }
    public string? MirUuid      { get; set; }
}

public class MaterialConsumptionReportItem
{
    public string   ProductName     { get; set; } = string.Empty;
    public Guid     ProductUuid     { get; set; }
    // PV-008 — resolved from ProductVariant via ProductUuid (which semantically holds the
    // variant's uuid since PV-005); empty if the variant has since been hard-deleted.
    public string   VariantName     { get; set; } = string.Empty;
    public string?  Sku             { get; set; }
    public string?  UnitOfMeasure   { get; set; }
    public string   MirNo           { get; set; } = string.Empty;
    public Guid     MirUuid         { get; set; }
    public decimal  IssuedQty       { get; set; }
    public decimal  ConsumedQty     { get; set; }
    public decimal  BalanceQty      { get; set; }
    public decimal  UnitCost        { get; set; }
    public decimal  BalanceValue    { get; set; }
    public string?  SourceType      { get; set; }
}

// ── 3. Project Consumption Report ────────────────────────────────────────────

public class ProjectConsumptionFilter : MaterialReportFilter
{
    public string? ProjectUuid     { get; set; }
    public string? TransactionType { get; set; }
}

public class ProjectConsumptionItem
{
    public Guid     ProjectUuid     { get; set; }
    public string   ProjectCode     { get; set; } = string.Empty;
    public string   ProjectName     { get; set; } = string.Empty;
    public string   ItemDescription { get; set; } = string.Empty;
    public Guid     ProductUuid     { get; set; }
    // PV-008 — resolved from ProductVariant via ProductUuid (variant uuid since PV-005).
    public string   ProductName     { get; set; } = string.Empty;
    public string   VariantName     { get; set; } = string.Empty;
    public string?  Sku             { get; set; }
    public string   TransactionType { get; set; } = string.Empty;
    public string   ReferenceNumber { get; set; } = string.Empty;
    public decimal  Quantity        { get; set; }
    public decimal  UnitCost        { get; set; }
    public decimal  Amount          { get; set; }
    public DateTime PostedDate      { get; set; }
}

// ── 4. Department Consumption Report ─────────────────────────────────────────

public class DepartmentConsumptionFilter : MaterialReportFilter
{
    public string? Department      { get; set; }
    public string? TransactionType { get; set; }
}

public class DepartmentConsumptionItem
{
    public string   Department      { get; set; } = string.Empty;
    public string?  CostCenter      { get; set; }
    public string   ItemDescription { get; set; } = string.Empty;
    public Guid     ProductUuid     { get; set; }
    // PV-008 — resolved from ProductVariant via ProductUuid (variant uuid since PV-005).
    public string   ProductName     { get; set; } = string.Empty;
    public string   VariantName     { get; set; } = string.Empty;
    public string?  Sku             { get; set; }
    public string   TransactionType { get; set; } = string.Empty;
    public string   ReferenceNumber { get; set; } = string.Empty;
    public decimal  Quantity        { get; set; }
    public decimal  UnitCost        { get; set; }
    public decimal  Amount          { get; set; }
    public DateTime PostedDate      { get; set; }
}

// ── 5. Stock Movement Report ──────────────────────────────────────────────────

public class StockMovementFilter : MaterialReportFilter
{
    public string? WarehouseId      { get; set; }
    public string? ProductUuid      { get; set; }
    public string? TransactionType  { get; set; }
}

public class StockMovementItem
{
    public Guid     LedgerUuid      { get; set; }
    public string   TransactionType { get; set; } = string.Empty;
    public string   ProductName     { get; set; } = string.Empty;
    public Guid     ProductUuid     { get; set; }
    public string?  Sku             { get; set; }
    public string?  VariantName     { get; set; }
    public string   WarehouseName   { get; set; } = string.Empty;
    public decimal  QuantityIn      { get; set; }
    public decimal  QuantityOut     { get; set; }
    public decimal  UnitCost        { get; set; }
    public decimal  TotalValue      { get; set; }
    public string?  ReferenceNumber { get; set; }
    public string?  BatchNumber     { get; set; }
    public DateTime TransactionDate { get; set; }
}

// ── 6. Stock Ledger Report ────────────────────────────────────────────────────

public class StockLedgerFilter : MaterialReportFilter
{
    public string? WarehouseId  { get; set; }
    public string? ProductUuid  { get; set; }
}

public class StockLedgerItem
{
    public Guid     LedgerUuid      { get; set; }
    public DateTime TransactionDate { get; set; }
    public string   TransactionType { get; set; } = string.Empty;
    public string   ProductName     { get; set; } = string.Empty;
    public Guid     ProductUuid     { get; set; }
    public string?  Sku             { get; set; }
    public string?  VariantName     { get; set; }
    public string   WarehouseName   { get; set; } = string.Empty;
    public decimal  QuantityIn      { get; set; }
    public decimal  QuantityOut     { get; set; }
    public decimal  RunningBalance  { get; set; }
    public decimal  UnitCost        { get; set; }
    public string?  ReferenceNumber { get; set; }
}

// ── 7. Material Return Report ─────────────────────────────────────────────────

public class MaterialReturnReportFilter : MaterialReportFilter
{
    public string? Status    { get; set; }
    public string? Condition { get; set; }
}

public class MaterialReturnReportItem
{
    public Guid     ReturnUuid      { get; set; }
    public string   ReturnNo        { get; set; } = string.Empty;
    public string   MivNo           { get; set; } = string.Empty;
    public string   MirNo           { get; set; } = string.Empty;
    public string   Status          { get; set; } = string.Empty;
    public DateTime ReturnDate      { get; set; }
    public string   ItemDescription { get; set; } = string.Empty;
    public Guid     ProductUuid     { get; set; }
    // PV-008 — resolved from ProductVariant via ProductUuid (variant uuid since PV-005).
    public string   ProductName     { get; set; } = string.Empty;
    public string   VariantName     { get; set; } = string.Empty;
    public string?  Sku             { get; set; }
    public string?  UnitOfMeasure   { get; set; }
    public decimal  ReturnedQty     { get; set; }
    public string   Condition       { get; set; } = string.Empty;
    public string?  Reason          { get; set; }
    public decimal  UnitCost        { get; set; }
    public decimal  LineValue       { get; set; }
}

// ── 8. Wastage Report ────────────────────────────────────────────────────────

public class WastageReportFilter : MaterialReportFilter
{
    public string? Status     { get; set; }
    public string? SourceType { get; set; }
}

public class WastageReportItem
{
    public Guid     WastageUuid     { get; set; }
    public string   WastageNo       { get; set; } = string.Empty;
    public string   SourceType      { get; set; } = string.Empty;
    public string   ItemDescription { get; set; } = string.Empty;
    public Guid     ProductUuid     { get; set; }
    // PV-008 — resolved from ProductVariant via ProductUuid (variant uuid since PV-005).
    public string   ProductName     { get; set; } = string.Empty;
    public string   VariantName     { get; set; } = string.Empty;
    public string?  Sku             { get; set; }
    public string?  UnitOfMeasure   { get; set; }
    public decimal  WastedQty       { get; set; }
    public decimal  UnitCost        { get; set; }
    public decimal  Amount          { get; set; }
    public string   Reason          { get; set; } = string.Empty;
    public string   Status          { get; set; } = string.Empty;
    public int?     ApprovedBy      { get; set; }
    public DateTime? ApprovedAt     { get; set; }
    public DateTime CreatedDate     { get; set; }
}

// ── 9. Reserved Stock Report ──────────────────────────────────────────────────

public class ReservedStockFilter : MaterialReportFilter
{
    public string? WarehouseId  { get; set; }
    public string? Status       { get; set; }
}

public class ReservedStockItem
{
    public Guid     ReservationUuid { get; set; }

    /// <summary>MIR, DELIVERY or SALES_ORDER — what is holding the stock.</summary>
    public string   SourceType      { get; set; } = string.Empty;
    public Guid     SourceUuid      { get; set; }

    // Added rather than replaced: the existing screen reads these, and they stay populated for
    // material-issue holds. Blank for any other source, which SourceType identifies.
    public string   MirNo           { get; set; } = string.Empty;
    public Guid     MirUuid         { get; set; }
    public string   RequestType     { get; set; } = string.Empty;
    public Guid     ProductUuid     { get; set; }
    public string   ProductName     { get; set; } = string.Empty;
    public string?  Sku             { get; set; }
    public string?  VariantName     { get; set; }
    public string   WarehouseName   { get; set; } = string.Empty;
    public decimal  ReservedQty     { get; set; }
    public string   Status          { get; set; } = string.Empty;
    public DateTime ReservedAt      { get; set; }
    public int      AgeDays         { get; set; }
    public bool     IsFlagged       { get; set; }
}

namespace SMS.Modules.Inventory.Models;

// RC-001 (FSD Addendum 28) — Supplier Rate Card Management.

public class DiscountTierDto
{
    public int     QtyFrom     { get; set; }
    public int?    QtyTo       { get; set; }
    public decimal DiscountPct { get; set; }
}

public class CreateVariantSupplierRequest
{
    public Guid    VariantUuid    { get; set; }
    public Guid    SupplierUuid   { get; set; }
    public decimal VendorUnitCost { get; set; }
    public int?    LeadTimeDays   { get; set; }
    public DateTime? EffectiveFrom { get; set; }
    public DateTime? EffectiveTo   { get; set; }
    public Guid?    CurrencyId     { get; set; }
    public decimal? MinOrderValue  { get; set; }
    public int?     MinOrderQty    { get; set; }
    public List<DiscountTierDto>? DiscountTiers { get; set; }
    public string?  QuotationRef   { get; set; }
    public string?  Notes          { get; set; }
    public string?  VendorPartNo   { get; set; }
}

public class UpdateVariantSupplierRequest
{
    public decimal VendorUnitCost { get; set; }
    public int?    LeadTimeDays   { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo  { get; set; }
    public Guid    CurrencyId     { get; set; }
    public decimal? MinOrderValue { get; set; }
    public int?     MinOrderQty   { get; set; }
    public List<DiscountTierDto>? DiscountTiers { get; set; }
    public string?  QuotationRef  { get; set; }
    public string?  Notes         { get; set; }
    public string?  VendorPartNo  { get; set; }
    public bool     IsActive      { get; set; } = true;
    public DateTime? LastReviewedAt { get; set; }
    public int?     LastReviewedBy { get; set; }

    // Required server-side when VendorUnitCost moves by more than 10% — see
    // VariantSupplierService.UpdateAsync.
    public string?  ChangeReason  { get; set; }
}

public class VariantSupplierModel
{
    public Guid    Uuid           { get; set; }
    public Guid    VariantUuid    { get; set; }
    public string  VariantSku     { get; set; } = string.Empty;
    public string  ProductName    { get; set; } = string.Empty;
    public Guid    SupplierId     { get; set; }
    public decimal VendorUnitCost { get; set; }
    public int?    LeadTimeDays   { get; set; }
    public bool    IsActive       { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo  { get; set; }
    public Guid    CurrencyId     { get; set; }
    public decimal? MinOrderValue { get; set; }
    public int?    MinOrderQty    { get; set; }
    public List<DiscountTierDto> DiscountTiers { get; set; } = [];
    public string? QuotationRef   { get; set; }
    public string? Notes          { get; set; }
    // RC-004 — resolved from the latest SupplierRateHistory row where FieldChanged == "Notes",
    // rather than adding dedicated Notes-audit columns.
    public DateTime? NotesUpdatedAt { get; set; }
    public string?   NotesUpdatedByName { get; set; }
    public string? VendorPartNo   { get; set; }
    public bool    IsPreferred    { get; set; }
    public DateTime? LastReviewedAt { get; set; }
    public int?    LastReviewedBy { get; set; }
    // RC-007 — resolved from LastReviewedBy, same pattern as NotesUpdatedByName.
    public string? LastReviewedByName { get; set; }
    public DateTime CreatedDate   { get; set; }
    public DateTime? ModifiedDate { get; set; }

    // RC-004 — Section 6, PO Reference. Null when no qualifying PO exists yet.
    public PoReferenceSummaryModel? PoReference { get; set; }
}

public class PoReferenceSummaryModel
{
    public Guid    LastPoUuid           { get; set; }
    public string  LastPoNumber         { get; set; } = string.Empty;
    public DateTime LastPoDate          { get; set; }
    public decimal LastPoPrice          { get; set; }
    public int     PoCountLast12Months  { get; set; }
}

public class SupplierRateHistoryModel
{
    public int      Id           { get; set; }
    public string   FieldChanged { get; set; } = string.Empty;
    public string?  OldValue     { get; set; }
    public string?  NewValue     { get; set; }
    public string?  ChangeReason { get; set; }
    public int      ChangedBy    { get; set; }
    public string?  ChangedByName { get; set; }
    public DateTime ChangedAt    { get; set; }
}

public class RateHistoryFilter
{
    public int Page     { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

// RC-002 — Supplier Rate Card grid.

public class RateCardListFilter
{
    public Guid    SupplierId { get; set; }
    public string? Search     { get; set; }
    public string? SortBy     { get; set; }
    public string? SortDir    { get; set; }
    public int     Page       { get; set; } = 1;
    public int     PageSize   { get; set; } = 20;
}

public class RateCardGridRowModel
{
    public Guid    Uuid           { get; set; }
    public Guid    VariantUuid    { get; set; }
    public string  ProductName    { get; set; } = string.Empty;
    public string  VariantName    { get; set; } = string.Empty;
    public string  Sku            { get; set; } = string.Empty;
    public string? VendorPartNo   { get; set; }
    public decimal VendorUnitCost { get; set; }
    public Guid    CurrencyId     { get; set; }
    // Null when no qualifying (approved/sent/received/closed) PO line exists yet for this
    // variant+supplier — rendered as a grey dash, not a comparison, on the grid.
    public decimal? LastPoPrice   { get; set; }
    public int?    LeadTimeDays   { get; set; }
    public decimal? MinOrderValue { get; set; }
    public int?    MinOrderQty    { get; set; }
    public bool    IsPreferred    { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo  { get; set; }
    // ACTIVE | EXPIRED | PENDING | STALE — computed server-side, see VariantSupplierService.
    public string  Status         { get; set; } = string.Empty;

    // Not shown as grid columns, but round-tripped on every inline-edit save (PUT re-sends the
    // whole row) so an edit to Rate/Lead Days/Min Qty can never silently null these out.
    public List<DiscountTierDto> DiscountTiers { get; set; } = [];
    public string?  QuotationRef   { get; set; }
    public string?  Notes          { get; set; }
    public bool     IsActive       { get; set; } = true;
    public DateTime? LastReviewedAt { get; set; }
    public int?     LastReviewedBy { get; set; }
}

// RC-003 — Product Comparison View: every active supplier's rate for one variant, side by side.
public class RateComparisonRowModel
{
    public Guid    Uuid           { get; set; }
    public Guid    SupplierId     { get; set; }
    public string  SupplierName   { get; set; } = string.Empty;
    public string? VendorPartNo   { get; set; }
    public decimal VendorUnitCost { get; set; }
    public Guid    CurrencyId     { get; set; }
    public int?    LeadTimeDays   { get; set; }
    public decimal? MinOrderValue { get; set; }
    public int?    MinOrderQty    { get; set; }
    public DateTime? LastPoDate   { get; set; }
    // A–F, or null when the supplier has never been scored (renders as '-', never an error).
    public string? ScorecardGrade { get; set; }
    public bool    IsPreferred    { get; set; }
}

// RC-005 — Bulk Rate Adjustment.

public class BulkAdjustRequest
{
    public List<Guid> VariantSupplierIds { get; set; } = [];
    // PERCENTAGE | FIXED
    public string Method { get; set; } = string.Empty;
    public decimal Value { get; set; }
    public string ChangeReason { get; set; } = string.Empty;
}

public class BulkAdjustPreviewRowModel
{
    public Guid    VariantSupplierId { get; set; }
    public string  ProductName       { get; set; } = string.Empty;
    public string  VariantName       { get; set; } = string.Empty;
    public decimal CurrentRate       { get; set; }
    public decimal NewRate           { get; set; }
    public decimal Difference        { get; set; }
    public decimal DiffPct           { get; set; }
}

public class BulkAdjustConfirmResult
{
    public Guid    BulkOperationId    { get; set; }
    public int     AffectedCount      { get; set; }
    public decimal TotalImpactAmount  { get; set; }
}

public class BulkRateOperationModel
{
    public Guid     Uuid              { get; set; }
    public string   Method            { get; set; } = string.Empty;
    public decimal  Value             { get; set; }
    public int      AffectedCount     { get; set; }
    public decimal  TotalImpactAmount { get; set; }
    public string   ChangeReason      { get; set; } = string.Empty;
    public int      PerformedBy       { get; set; }
    public string?  PerformedByName   { get; set; }
    public DateTime PerformedAt       { get; set; }
    public bool     IsUndone          { get; set; }
}

// RC-006 — Excel import/export.

public class RateCardExportRowModel
{
    public string  ProductCode   { get; set; } = string.Empty;
    public string  ProductName   { get; set; } = string.Empty;
    public string  VariantSku    { get; set; } = string.Empty;
    public string  VariantName   { get; set; } = string.Empty;
    public string? VendorPartNo  { get; set; }
    public decimal CurrentRate   { get; set; }
    public int?    LeadDays      { get; set; }
    public int?    MinQty        { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo  { get; set; }
    public string? Notes         { get; set; }
}

// Import re-imports by Variant SKU only — Product Code/Name, Variant Name and Vendor Part No are
// exported for human reference but not read back on import.
public class ImportPreviewRowModel
{
    public int      Row          { get; set; }
    public string   Sku          { get; set; } = string.Empty;
    public string?  ProductName  { get; set; }
    public decimal? CurrentRate  { get; set; }
    public decimal? ImportedRate { get; set; }
    public bool     RateChanged  { get; set; }
    public bool     NewRecord    { get; set; }
    // Set when the row is invalid (bad SKU, non-positive rate, bad dates) — in that case every
    // other field above is left at its default, per the ticket's "errors don't block valid rows."
    public string?  Error        { get; set; }
}

public class ImportRowError
{
    public int    Row     { get; set; }
    public string Sku     { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public class ImportConfirmResult
{
    public int UpdatedCount { get; set; }
    public int CreatedCount { get; set; }
    public List<ImportRowError> Errors { get; set; } = [];
}

// RC-006 — Copy rates between suppliers.

public class CopyRatesRequest
{
    public Guid    SourceSupplierId { get; set; }
    public Guid    TargetSupplierId { get; set; }
    public decimal AdjustmentPct    { get; set; }
}

public class CopyPreviewRowModel
{
    // The SOURCE supplier's VariantSupplier uuid — identifies which row this preview entry
    // describes, not anything created for the target yet.
    public Guid    VariantSupplierId { get; set; }
    public string  ProductName       { get; set; } = string.Empty;
    public string  VariantName       { get; set; } = string.Empty;
    public string  Sku               { get; set; } = string.Empty;
    public decimal SourceRate        { get; set; }
    public decimal AdjustedRate      { get; set; }
    public bool    WillSkip          { get; set; }
    public string? SkipReason        { get; set; }
}

public class CopyConfirmResult
{
    public int CreatedCount { get; set; }
    public int SkippedCount { get; set; }
}

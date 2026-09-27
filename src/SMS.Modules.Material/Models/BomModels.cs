using SMS.Shared.Pagination;

namespace SMS.Modules.Material.Models;

// ── Requests ──────────────────────────────────────────────────────────────────

public class BomLineRequest
{
    public Guid     MaterialVariantUuid  { get; set; }
    public decimal  Quantity             { get; set; }
    /// <summary>Defaults to the material's own unit of measure.</summary>
    public string?  Uom                  { get; set; }
    public decimal  ScrapPercentage      { get; set; }
    public bool     IsCritical           { get; set; } = true;
    public Guid?    AlternateVariantUuid { get; set; }
    public string?  Notes                { get; set; }
    public int?     Sequence             { get; set; }
}

public class CreateBomRequest
{
    public Guid      ProductUuid        { get; set; }
    public Guid?     ProductVariantUuid { get; set; }
    public decimal   BaseQuantity       { get; set; } = 1m;
    /// <summary>Defaults to the product's unit of measure.</summary>
    public string?   BaseUom            { get; set; }
    /// <summary>A31-C5 — defaults to today (BomRepository.CreateAsync) when left null.</summary>
    public DateTime? EffectiveFrom      { get; set; }
    public DateTime? EffectiveTo        { get; set; }
    public string?   Notes              { get; set; }
    public List<BomLineRequest> Lines   { get; set; } = [];
}

/// <summary>Whole-document replace of what a DRAFT (or REJECTED) BOM says. Lines omitted = lines kept.</summary>
public class UpdateBomRequest
{
    public decimal?  BaseQuantity  { get; set; }
    public string?   BaseUom       { get; set; }
    public DateTime? EffectiveFrom { get; set; }
    public DateTime? EffectiveTo   { get; set; }
    public bool      ClearEffectiveDates { get; set; }
    public string?   Notes         { get; set; }
    public List<BomLineRequest>? Lines { get; set; }
}

public class RejectBomRequest
{
    public string Reason { get; set; } = string.Empty;
}

public class ObsoleteBomRequest
{
    public string? Reason { get; set; }
}

// ── Filters ───────────────────────────────────────────────────────────────────

public class BomListFilter
{
    public Guid?   ProductUuid { get; set; }
    public string? Status      { get; set; }
    public string? Search      { get; set; }
    public int     Page        { get; set; } = 1;
    public int     PageSize    { get; set; } = 20;
}

// ── Responses ─────────────────────────────────────────────────────────────────

public class BomListItemModel
{
    public Guid      UUID               { get; set; }
    public string    BomNumber          { get; set; } = string.Empty;
    public Guid      ProductUuid        { get; set; }
    public string    ProductName        { get; set; } = string.Empty;
    public string    ProductSku         { get; set; } = string.Empty;
    public Guid?     ProductVariantUuid { get; set; }
    public string?   VariantName        { get; set; }
    public int       Version            { get; set; }
    public string    Status             { get; set; } = string.Empty;
    public decimal   BaseQuantity       { get; set; }
    public string    BaseUom            { get; set; } = string.Empty;
    public DateTime? EffectiveFrom      { get; set; }
    public DateTime? EffectiveTo        { get; set; }
    public int       LineCount          { get; set; }
    public DateTime  CreatedAt          { get; set; }
    public DateTime  UpdatedAt          { get; set; }
    public DateTime? ActivatedAt        { get; set; }
}

public class BomLineModel
{
    public Guid     UUID                 { get; set; }
    public int      Sequence             { get; set; }
    public Guid     MaterialProductUuid  { get; set; }
    public string   MaterialProductName  { get; set; } = string.Empty;
    public string   MaterialProductType  { get; set; } = string.Empty;
    public string   MaterialSupplyMethod { get; set; } = string.Empty;
    public Guid     MaterialVariantUuid  { get; set; }
    public string   MaterialSku          { get; set; } = string.Empty;
    public string   MaterialVariantName  { get; set; } = string.Empty;
    public string?  MaterialImageUrl     { get; set; }
    public decimal  Quantity             { get; set; }
    public string   Uom                  { get; set; } = string.Empty;
    public decimal  ScrapPercentage      { get; set; }
    /// <summary>Quantity plus the scrap allowance — what a production order actually needs per base quantity.</summary>
    public decimal  GrossQuantity        { get; set; }
    public bool     IsCritical           { get; set; }
    public Guid?    AlternateVariantUuid { get; set; }
    public string?  AlternateVariantName { get; set; }
    public string?  Notes                { get; set; }
}

public class BomDetailModel : BomListItemModel
{
    public Guid      TraceId         { get; set; }
    public string?   Notes           { get; set; }
    public int       CreatedBy       { get; set; }
    public int?      SubmittedBy     { get; set; }
    public DateTime? SubmittedAt     { get; set; }
    public int?      ApprovedBy      { get; set; }
    public DateTime? ApprovedAt      { get; set; }
    public int?      RejectedBy      { get; set; }
    public DateTime? RejectedAt      { get; set; }
    public string?   RejectionReason { get; set; }
    public int?      ActivatedBy     { get; set; }
    public int?      ObsoletedBy     { get; set; }
    public DateTime? ObsoletedAt     { get; set; }
    public List<BomLineModel> Lines  { get; set; } = [];
}

public class BomVersionModel
{
    public Guid      UUID        { get; set; }
    public string    BomNumber   { get; set; } = string.Empty;
    public int       Version     { get; set; }
    public string    Status      { get; set; } = string.Empty;
    public int       LineCount   { get; set; }
    public DateTime  CreatedAt   { get; set; }
    public DateTime? ActivatedAt { get; set; }
    public DateTime? ObsoletedAt { get; set; }
}

/// <summary>§8.4 — what changed between two versions of a product's recipe.</summary>
public class BomComparisonModel
{
    public Guid   LeftUuid     { get; set; }
    public int    LeftVersion  { get; set; }
    public string LeftStatus   { get; set; } = string.Empty;
    public Guid   RightUuid    { get; set; }
    public int    RightVersion { get; set; }
    public string RightStatus  { get; set; } = string.Empty;
    public List<string>       HeaderChanges { get; set; } = [];
    public List<BomLineModel> Added         { get; set; } = [];
    public List<BomLineModel> Removed       { get; set; } = [];
    public List<BomLineChangeModel> Changed { get; set; } = [];
}

public class BomLineChangeModel
{
    public Guid         MaterialVariantUuid { get; set; }
    public string       MaterialName        { get; set; } = string.Empty;
    public BomLineModel Before              { get; set; } = new();
    public BomLineModel After               { get; set; } = new();
    public List<string> Fields              { get; set; } = [];
}

/// <summary>§7.3 / A30-P2-08 — what one base quantity of output costs in materials, rolled up through chained BOMs.</summary>
public class BomCostModel
{
    public Guid    BomUuid      { get; set; }
    public int     Version      { get; set; }
    public decimal BaseQuantity { get; set; }
    public decimal TotalCost    { get; set; }
    public decimal CostPerUnit  { get; set; }
    public List<BomLineCostModel> Lines { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public class BomLineCostModel
{
    public Guid    LineUuid            { get; set; }
    public Guid    MaterialVariantUuid { get; set; }
    public string  MaterialName        { get; set; } = string.Empty;
    public decimal Quantity            { get; set; }
    public decimal ScrapPercentage     { get; set; }
    public decimal GrossQuantity       { get; set; }
    public decimal UnitCost            { get; set; }
    public decimal LineCost            { get; set; }
    /// <summary>LAST_PURCHASE_PRICE, PURCHASE_PRICE, BOM_ROLLUP (a manufactured input's own active recipe) or NONE.</summary>
    public string  CostSource          { get; set; } = string.Empty;
    public Guid?   NestedBomUuid       { get; set; }
}

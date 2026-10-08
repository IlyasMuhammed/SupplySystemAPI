using SMS.Shared.Common;

namespace SMS.Modules.Material.Models;

// ── Production orders — requests ──────────────────────────────────────────────

public class CreateProductionOrderRequest
{
    public Guid      ProductUuid         { get; set; }
    /// <summary>Defaults to the product's default variant.</summary>
    public Guid?     ProductVariantUuid  { get; set; }
    public decimal   PlannedQuantity     { get; set; }
    /// <summary>Defaults to the product's default production warehouse.</summary>
    public Guid?     WarehouseUuid       { get; set; }
    public Guid?     OutputWarehouseUuid { get; set; }
    public DateTime  RequiredDate        { get; set; }
    public DateTime? PlannedStartDate    { get; set; }
    public int       Priority            { get; set; } = AllocationPriority.Normal;
    public string?   Notes               { get; set; }
    public string?   SourceType          { get; set; }
    public Guid?     SourceUuid          { get; set; }
    public Guid?     SourceLineUuid      { get; set; }
    public string?   SourceReference     { get; set; }
    /// <summary>Plan it straight away (explode the recipe, hold materials, raise supply). Off by default.</summary>
    public bool      Plan                { get; set; }
}

public class UpdateProductionOrderRequest
{
    public decimal?  PlannedQuantity     { get; set; }
    public Guid?     WarehouseUuid       { get; set; }
    public Guid?     OutputWarehouseUuid { get; set; }
    public DateTime? RequiredDate        { get; set; }
    public DateTime? PlannedStartDate    { get; set; }
    public int?      Priority            { get; set; }
    public string?   Notes               { get; set; }
}

public class ReportOutputRequest
{
    public decimal Quantity { get; set; }
    public string? Notes    { get; set; }
}

public class CancelProductionOrderRequest
{
    public string Reason { get; set; } = string.Empty;
}

public class ProductionOrderListFilter
{
    public string?   Status      { get; set; }
    public Guid?     ProductUuid { get; set; }
    // A31-C7 §9.5 — reverse navigation: e.g. every production order raised for one sale order
    // (SourceType=SALES_ORDER, SourceUuid=that order's own uuid), without a dedicated endpoint.
    public Guid?     SourceUuid  { get; set; }
    public int?      Priority    { get; set; }
    public DateTime? DateFrom    { get; set; }
    public DateTime? DateTo      { get; set; }
    public string?   Search      { get; set; }
    public bool      OpenOnly    { get; set; }
    public int       Page        { get; set; } = 1;
    public int       PageSize    { get; set; } = 20;
}

// ── Production orders — responses ─────────────────────────────────────────────

public class ProductionOrderListItemModel
{
    public Guid      UUID                    { get; set; }
    public string    ProductionNumber        { get; set; } = string.Empty;
    public Guid      ProductUuid             { get; set; }
    public string    ProductName             { get; set; } = string.Empty;
    public string    ProductSku              { get; set; } = string.Empty;
    public Guid      ProductVariantUuid      { get; set; }
    public string    VariantName             { get; set; } = string.Empty;
    public string    BomNumber               { get; set; } = string.Empty;
    public int       BomVersion              { get; set; }
    public decimal   PlannedQuantity         { get; set; }
    public decimal   ProducedQuantity        { get; set; }
    public decimal   AcceptedQuantity        { get; set; }
    public decimal   RejectedQuantity        { get; set; }
    public Guid      WarehouseUuid           { get; set; }
    public string    WarehouseName           { get; set; } = string.Empty;
    public string    SourceType              { get; set; } = string.Empty;
    // A31-C7 — the originating document's own uuid/line uuid, e.g. a sale order + line when
    // SourceType is SALES_ORDER. Already tracked generically for every source type (chained
    // manufacturing, fulfillment, etc.); this just surfaces it in the response so the UI can link
    // to it, rather than adding a second, sale-order-specific pair of columns that would duplicate it.
    public Guid?     SourceUuid              { get; set; }
    public Guid?     SourceLineUuid          { get; set; }
    public string?   SourceReference         { get; set; }
    public Guid?     ParentProductionOrderUuid { get; set; }
    public string?   ParentProductionNumber  { get; set; }
    public int       Priority                { get; set; }
    public DateTime  RequiredDate            { get; set; }
    public DateTime? PlannedStartDate        { get; set; }
    public DateTime? ActualStartDate         { get; set; }
    public DateTime? ActualEndDate           { get; set; }
    public string    Status                  { get; set; } = string.Empty;
    public string    MaterialReadiness       { get; set; } = string.Empty;
    public int       MaterialCount           { get; set; }
    public int       ShortMaterialCount      { get; set; }
    public DateTime  CreatedAt               { get; set; }
    public DateTime  UpdatedAt               { get; set; }

    // ── A34 (D-18, API-CONTRACT §7) — make to order and the delivery made from the order ──
    /// <summary>Made to order for a sale order line: <see cref="FulfillmentRouteUuid"/> is set (only the A34 path sets it).</summary>
    public bool      IsMakeToOrder            { get; set; }
    public Guid?     FulfillmentRouteUuid     { get; set; }
    /// <summary>Code, name and category through IFulfillmentRouteLookup (also for a route deactivated since); null when unknown.</summary>
    public string?   FulfillmentRouteCode     { get; set; }
    public string?   FulfillmentRouteName     { get; set; }
    public string?   FulfillmentRouteCategory { get; set; }
    /// <summary>The latest delivery created from this order (live status: GET api/logistics/deliveries?productionOrderUuid=).</summary>
    public Guid?     DeliveryOrderUuid        { get; set; }
    public string?   DeliveryNumber           { get; set; }
    /// <summary>The delivery handoff has not settled yet; the sweep retries it.</summary>
    public bool      DeliveryCreationPending  { get; set; }
    /// <summary>1-based position of the sale order line (by line id, as A33's "Line N"), when SourceType is SALES_ORDER.</summary>
    public int?      SaleOrderLineNumber      { get; set; }
    /// <summary>Make to order only: planned − accepted once COMPLETED (when above zero), or the planned quantity on zero yield.</summary>
    public decimal?  ShortfallQuantity        { get; set; }
}

public class ProductionMaterialModel
{
    public Guid     UUID                 { get; set; }
    public int      Sequence             { get; set; }
    public Guid     MaterialProductUuid  { get; set; }
    public string   MaterialProductName  { get; set; } = string.Empty;
    public string   MaterialSupplyMethod { get; set; } = string.Empty;
    public Guid     MaterialVariantUuid  { get; set; }
    public string   MaterialSku          { get; set; } = string.Empty;
    public string   MaterialVariantName  { get; set; } = string.Empty;
    public decimal  NetQuantity          { get; set; }
    public decimal  ScrapAllowance       { get; set; }
    public decimal  RequiredQuantity     { get; set; }
    public decimal  ReservedQuantity     { get; set; }
    public decimal  PlannedQuantity      { get; set; }
    public decimal  IssuedQuantity       { get; set; }
    public decimal  ReturnedQuantity     { get; set; }
    public decimal  WastageQuantity      { get; set; }
    public decimal  ConsumedQuantity     { get; set; }
    public decimal  ShortageQuantity     { get; set; }
    public decimal  Outstanding          { get; set; }
    public string   Uom                  { get; set; } = string.Empty;
    public Guid     WarehouseUuid        { get; set; }
    public string   WarehouseName        { get; set; } = string.Empty;
    public bool     IsCritical           { get; set; }
    public string   Status               { get; set; } = string.Empty;
    public DateTime RequiredDate         { get; set; }
    public Guid?    AllocationDemandUuid { get; set; }
    /// <summary>Is this line's requirement physically covered by held stock?</summary>
    public bool     IsCovered            { get; set; }
}

public class SupplyRequirementModel
{
    public Guid      UUID                  { get; set; }
    public string    SupplyNumber          { get; set; } = string.Empty;
    public Guid      ProductUuid           { get; set; }
    public string    ProductName           { get; set; } = string.Empty;
    public Guid      VariantUuid           { get; set; }
    public string    VariantName           { get; set; } = string.Empty;
    public string    MaterialSku           { get; set; } = string.Empty;
    public decimal   QuantityRequired      { get; set; }
    public decimal   QuantityOrdered       { get; set; }
    public decimal   QuantityReceived      { get; set; }
    public decimal   QuantityOutstanding   { get; set; }
    public string    DemandSourceType      { get; set; } = string.Empty;
    public Guid      DemandSourceUuid      { get; set; }
    public string?   DemandReference       { get; set; }
    public string    SupplyMethod          { get; set; } = string.Empty;
    public string?   SupplySourceType      { get; set; }
    public Guid?     SupplySourceUuid      { get; set; }
    public string?   SupplySourceReference { get; set; }
    public Guid      WarehouseUuid         { get; set; }
    public string    WarehouseName         { get; set; } = string.Empty;
    public DateTime  RequiredDate          { get; set; }
    public int       Priority              { get; set; }
    public string    Status                { get; set; } = string.Empty;
    public string?   Notes                 { get; set; }
    public DateTime  CreatedAt             { get; set; }
    public DateTime  UpdatedAt             { get; set; }
}

public class ProductionIssueLineModel
{
    public Guid     UUID                { get; set; }
    public Guid     RequirementUuid     { get; set; }
    public Guid     MaterialVariantUuid { get; set; }
    public string   MaterialName        { get; set; } = string.Empty;
    public string   MaterialSku         { get; set; } = string.Empty;
    public decimal  Quantity            { get; set; }
    public string   Uom                 { get; set; } = string.Empty;
    public decimal  UnitCost            { get; set; }
    public string?  BatchNumber         { get; set; }
    public string?  Notes               { get; set; }
}

public class ProductionIssueModel
{
    public Guid      UUID              { get; set; }
    public string    IssueNumber       { get; set; } = string.Empty;
    public Guid      ProductionOrderUuid { get; set; }
    public string    ProductionNumber  { get; set; } = string.Empty;
    public Guid      WarehouseUuid     { get; set; }
    public string    WarehouseName     { get; set; } = string.Empty;
    public string    IssueType         { get; set; } = string.Empty;
    public string    Status            { get; set; } = string.Empty;
    public int       CreatedBy         { get; set; }
    public DateTime  CreatedAt         { get; set; }
    public int?      ConfirmedBy       { get; set; }
    public DateTime? ConfirmedAt       { get; set; }
    public DateTime? ReversedAt        { get; set; }
    public string?   Notes             { get; set; }
    public decimal   TotalQuantity     { get; set; }
    public List<ProductionIssueLineModel> Lines { get; set; } = [];
}

public class ProductionOrderDetailModel : ProductionOrderListItemModel
{
    public Guid      TraceId             { get; set; }
    public Guid      BomUuid             { get; set; }
    public Guid?     OutputWarehouseUuid { get; set; }
    public string?   OutputWarehouseName { get; set; }
    public Guid?     SourceUuid          { get; set; }
    public Guid?     SourceLineUuid      { get; set; }
    public decimal   ScrappedQuantity    { get; set; }
    public string?   Notes               { get; set; }
    public int       CreatedBy           { get; set; }
    public List<ProductionMaterialModel> Materials          { get; set; } = [];
    public List<SupplyRequirementModel>  SupplyRequirements { get; set; } = [];
    public List<ProductionIssueModel>    Issues             { get; set; } = [];
    public List<ProductionOrderListItemModel> ChildOrders    { get; set; } = [];
}

/// <summary>§29.5 — the material status panel.</summary>
public class ProductionReadinessModel
{
    public Guid    ProductionOrderUuid { get; set; }
    public string  ProductionNumber    { get; set; } = string.Empty;
    public string  Status              { get; set; } = string.Empty;
    public string  MaterialReadiness   { get; set; } = string.Empty;
    public bool    AllCriticalCovered  { get; set; }
    public List<ProductionMaterialModel> Materials          { get; set; } = [];
    public List<SupplyRequirementModel>  SupplyRequirements { get; set; } = [];
}

/// <summary>§29.3 — one row of the shortage dashboard: a requirement nothing physically covers yet.</summary>
public class MaterialShortageModel
{
    public Guid     ProductionOrderUuid { get; set; }
    public string   ProductionNumber    { get; set; } = string.Empty;
    public string   ProductionStatus    { get; set; } = string.Empty;
    public string   OutputProductName   { get; set; } = string.Empty;
    public int      Priority            { get; set; }
    public DateTime RequiredDate        { get; set; }
    public Guid     RequirementUuid     { get; set; }
    public Guid     MaterialVariantUuid { get; set; }
    public string   MaterialName        { get; set; } = string.Empty;
    public string   MaterialSku         { get; set; } = string.Empty;
    public decimal  RequiredQuantity    { get; set; }
    public decimal  ReservedQuantity    { get; set; }
    public decimal  PlannedQuantity     { get; set; }
    public decimal  ShortageQuantity    { get; set; }
    public string   Uom                 { get; set; } = string.Empty;
    public Guid     WarehouseUuid       { get; set; }
    public string   WarehouseName       { get; set; } = string.Empty;
    public bool     IsCritical          { get; set; }
    public string?  SupplyNumber        { get; set; }
    public string?  SupplyStatus        { get; set; }
    public string?  SupplySourceReference { get; set; }
}

// ── Supply requirements ───────────────────────────────────────────────────────

public class SupplyRequirementListFilter
{
    public string?  Status           { get; set; }
    public string?  SupplyMethod     { get; set; }
    public Guid?    VariantUuid      { get; set; }
    public Guid?    ProductUuid      { get; set; }
    public string?  DemandSourceType { get; set; }
    public Guid?    DemandSourceUuid { get; set; }
    public string?  Search           { get; set; }
    public bool     OpenOnly         { get; set; }
    public int      Page             { get; set; } = 1;
    public int      PageSize         { get; set; } = 20;
}

public class CreateSupplyRequirementRequest
{
    public Guid      VariantUuid      { get; set; }
    public decimal   QuantityRequired { get; set; }
    public Guid      WarehouseUuid    { get; set; }
    public DateTime  RequiredDate     { get; set; }
    public int       Priority         { get; set; } = AllocationPriority.Normal;
    /// <summary>Defaults to the product's supply method.</summary>
    public string?   SupplyMethod     { get; set; }
    public string?   Notes            { get; set; }
    /// <summary>Raise the purchase or child production order straight away. On by default.</summary>
    public bool      Act              { get; set; } = true;
}

public class CancelSupplyRequirementRequest
{
    public string Reason { get; set; } = string.Empty;
}

// ── Production material issues ────────────────────────────────────────────────

public class CreateProductionIssueLineRequest
{
    public Guid    RequirementUuid     { get; set; }
    /// <summary>Only for a SUBSTITUTION: the variant issued in place of the requirement's.</summary>
    public Guid?   MaterialVariantUuid { get; set; }
    public decimal Quantity            { get; set; }
    public string? BatchNumber         { get; set; }
    public string? Notes               { get; set; }
}

public class CreateProductionIssueRequest
{
    public string  IssueType     { get; set; } = "STANDARD";
    /// <summary>Defaults to the order's production warehouse.</summary>
    public Guid?   WarehouseUuid { get; set; }
    public string? Notes         { get; set; }
    public List<CreateProductionIssueLineRequest> Lines { get; set; } = [];
    /// <summary>Confirm (post the stock movement) straight away. Off by default.</summary>
    public bool    Confirm       { get; set; }
}

public class ReverseProductionIssueRequest
{
    public string Reason { get; set; } = string.Empty;
}

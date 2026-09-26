using SMS.Shared.Common;

namespace SMS.Modules.Material.Domain;

// ── Codes (A30 §11.2, §12.1, §13.1, §16) ─────────────────────────────────────

internal static class ProductionOrderStatus
{
    public const string Draft             = "DRAFT";
    public const string Planned           = "PLANNED";
    public const string MaterialPending   = "MATERIAL_PENDING";
    public const string Ready             = "READY";
    public const string InProgress        = "IN_PROGRESS";
    public const string QualityInspection = "QUALITY_INSPECTION";
    public const string Completed         = "COMPLETED";
    public const string Closed            = "CLOSED";
    public const string Cancelled         = "CANCELLED";

    public static readonly IReadOnlyList<string> All =
        [Draft, Planned, MaterialPending, Ready, InProgress, QualityInspection, Completed, Closed, Cancelled];

    /// <summary>States in which material readiness still matters.</summary>
    public static bool IsAwaitingMaterials(string status) => status is Planned or MaterialPending or Ready;

    /// <summary>§11.2 — a person may cancel anything not yet inspected, completed or closed.</summary>
    public static bool CanCancel(string status) => status is Draft or Planned or MaterialPending or Ready or InProgress;

    public static bool IsTerminal(string status) => status is Completed or Closed or Cancelled;
}

internal static class MaterialReadiness
{
    public const string NotChecked = "NOT_CHECKED";
    public const string Partial    = "PARTIAL";
    public const string Ready      = "READY";
    public const string Shortage   = "SHORTAGE";
}

// ProductionSourceType moved to SMS.Shared.Common (Phase 4 Track C) — Sales, not just Material,
// needs to name it now that a sale order's own deficit can raise a production order.

internal static class PmrStatus
{
    public const string Pending           = "PENDING";
    public const string PartiallyReserved = "PARTIALLY_RESERVED";
    public const string FullyReserved     = "FULLY_RESERVED";
    public const string Issued            = "ISSUED";
    public const string Consumed          = "CONSUMED";
    public const string Cancelled         = "CANCELLED";
}

internal static class SupplyRequirementStatus
{
    public const string Open              = "OPEN";
    public const string Planned           = "PLANNED";
    public const string Ordered           = "ORDERED";
    public const string PartiallyReceived = "PARTIALLY_RECEIVED";
    public const string Fulfilled         = "FULFILLED";
    public const string Cancelled         = "CANCELLED";

    /// <summary>
    /// Same values as <see cref="IsLive"/>, as an array — SQL Server's EF provider can translate
    /// <c>LiveStatuses.Contains(s.Status)</c> into a SQL <c>IN (...)</c>, but cannot translate a call
    /// to <see cref="IsLive"/> itself inside a query (confirmed the hard way, A30-P4-21: EF's
    /// InMemory provider — every unit test's own — evaluates it without complaint, since InMemory
    /// never needs to generate SQL; only a real SQL Server query throws
    /// "could not be translated"). Use this form in any LINQ-to-Entities query; <see cref="IsLive"/>
    /// itself remains the right call on an already-loaded entity or an in-memory list.
    /// </summary>
    public static readonly string[] LiveStatuses = [Open, Planned, Ordered, PartiallyReceived];

    public static bool IsLive(string status) => status is Open or Planned or Ordered or PartiallyReceived;
}

internal static class SupplyDemandSourceType
{
    public const string ProductionMaterialRequirement = "PRODUCTION_MATERIAL_REQ";
    public const string FulfillmentRequirement        = "FULFILLMENT_REQ";
    public const string Replenishment                 = "REPLENISHMENT";
    public const string ServiceOrder                  = "SERVICE_ORDER";
}

internal static class SupplySourceType
{
    public const string PurchaseOrder   = "PURCHASE_ORDER";
    public const string TransferOrder   = "TRANSFER_ORDER";
    public const string ProductionOrder = "PRODUCTION_ORDER";
}

internal static class ProductionIssueType
{
    /// <summary>What the requirements say, out of what was held for them.</summary>
    public const string Standard     = "STANDARD";
    /// <summary>More than the requirement, out of free stock. Needs a production manager.</summary>
    public const string Additional   = "ADDITIONAL";
    /// <summary>Unused material coming back from the floor into stock.</summary>
    public const string Return       = "RETURN";
    /// <summary>Issued material lost on the floor. A record, not a stock movement — it already left.</summary>
    public const string Scrap        = "SCRAP";
    /// <summary>A different variant issued in place of the one on the requirement, out of free stock.</summary>
    public const string Substitution = "SUBSTITUTION";

    public static readonly IReadOnlyList<string> All = [Standard, Additional, Return, Scrap, Substitution];
}

internal static class ProductionIssueStatus
{
    public const string Draft     = "DRAFT";
    public const string Confirmed = "CONFIRMED";
    public const string Reversed  = "REVERSED";
}

// ── Entities ──────────────────────────────────────────────────────────────────

/// <summary>A job to make some quantity of a product from a snapshotted recipe (A30 §11).</summary>
internal class ProductionOrder : ITenantScopedEntity
{
    public int       Id                       { get; set; }
    public Guid      UUID                     { get; set; } = Guid.NewGuid();
    public Guid      OrganizationId           { get; set; }
    public Guid      TraceId                  { get; set; } = Guid.NewGuid();
    public string    ProductionNumber         { get; set; } = string.Empty;
    public Guid      ProductUuid              { get; set; }
    /// <summary>The variant that comes out. Stock is per variant, so an order always names one.</summary>
    public Guid      ProductVariantUuid       { get; set; }
    /// <summary>The recipe, snapshotted at creation; never changed once planned (§11.4).</summary>
    public int       BomId                    { get; set; }
    public int       BomVersion               { get; set; }
    public decimal   PlannedQuantity          { get; set; }
    public decimal   ProducedQuantity         { get; set; }
    public decimal   AcceptedQuantity         { get; set; }
    public decimal   RejectedQuantity         { get; set; }
    public decimal   ScrappedQuantity         { get; set; }
    /// <summary>Where it is made and materials are drawn from.</summary>
    public Guid      WarehouseUuid            { get; set; }
    /// <summary>Where the output lands; the production warehouse when null.</summary>
    public Guid?     OutputWarehouseUuid      { get; set; }
    public string    SourceType               { get; set; } = ProductionSourceType.Manual;
    public Guid?     SourceUuid               { get; set; }
    public Guid?     SourceLineUuid           { get; set; }
    public string?   SourceReference          { get; set; }
    public int?      ParentProductionOrderId  { get; set; }
    /// <summary>0 low … 3 urgent — the same scale the allocation engine ranks demands by.</summary>
    public int       Priority                 { get; set; } = AllocationPriority.Normal;
    public DateTime  RequiredDate             { get; set; }
    public DateTime? PlannedStartDate         { get; set; }
    public DateTime? ActualStartDate          { get; set; }
    public DateTime? ActualEndDate            { get; set; }
    public string    Status                   { get; set; } = ProductionOrderStatus.Draft;
    public string    MaterialReadiness        { get; set; } = Domain.MaterialReadiness.NotChecked;
    public string?   Notes                    { get; set; }
    public int       CreatedBy                { get; set; }
    public DateTime  CreatedAt                { get; set; } = DateTime.UtcNow;
    public DateTime  UpdatedAt                { get; set; } = DateTime.UtcNow;
    public byte[]    RowVersion               { get; set; } = [];

    public BillOfMaterial   Bom    { get; set; } = null!;
    public ProductionOrder? Parent { get; set; }
    public ICollection<ProductionMaterialRequirement> Materials { get; set; } = new List<ProductionMaterialRequirement>();
}

/// <summary>One input the order needs, exploded from a recipe line (A30 §12).</summary>
internal class ProductionMaterialRequirement : ITenantScopedEntity
{
    public int       Id                   { get; set; }
    public Guid      UUID                 { get; set; } = Guid.NewGuid();
    public Guid      OrganizationId       { get; set; }
    public int       ProductionOrderId    { get; set; }
    public int?      BomLineId            { get; set; }
    public int       Sequence             { get; set; }
    public Guid      MaterialProductUuid  { get; set; }
    public Guid      MaterialVariantUuid  { get; set; }
    public decimal   NetQuantity          { get; set; }
    public decimal   ScrapAllowance       { get; set; }
    /// <summary>Net plus scrap allowance: what has to be on hand.</summary>
    public decimal   RequiredQuantity     { get; set; }
    /// <summary>Held for this requirement by the allocation engine.</summary>
    public decimal   ReservedQuantity     { get; set; }
    /// <summary>Assigned to supply on its way (a purchase or child production order).</summary>
    public decimal   PlannedQuantity      { get; set; }
    public decimal   IssuedQuantity       { get; set; }
    public decimal   ReturnedQuantity     { get; set; }
    public decimal   WastageQuantity      { get; set; }
    /// <summary>Issued less returned — what the floor has actually used.</summary>
    public decimal   ConsumedQuantity     { get; set; }
    /// <summary>Required less issued less held: what nothing physically covers yet.</summary>
    public decimal   ShortageQuantity     { get; set; }
    public string    Uom                  { get; set; } = string.Empty;
    public Guid      WarehouseUuid        { get; set; }
    public bool      IsCritical           { get; set; } = true;
    public string    Status               { get; set; } = PmrStatus.Pending;
    public DateTime  RequiredDate         { get; set; }
    /// <summary>The allocation engine's registry id for this requirement's demand.</summary>
    public Guid?     AllocationDemandUuid { get; set; }
    public DateTime  CreatedAt            { get; set; } = DateTime.UtcNow;
    public DateTime  UpdatedAt            { get; set; } = DateTime.UtcNow;

    public ProductionOrder ProductionOrder { get; set; } = null!;

    public decimal Outstanding => Math.Max(0m, RequiredQuantity - IssuedQuantity + ReturnedQuantity);
}

/// <summary>A shortage that something has to be done about (A30 §13).</summary>
internal class SupplyRequirement : ITenantScopedEntity
{
    public int       Id                      { get; set; }
    public Guid      UUID                    { get; set; } = Guid.NewGuid();
    public Guid      OrganizationId          { get; set; }
    public Guid      TraceId                 { get; set; } = Guid.NewGuid();
    public string    SupplyNumber            { get; set; } = string.Empty;
    public Guid      ProductUuid             { get; set; }
    public Guid      VariantUuid             { get; set; }
    public decimal   QuantityRequired        { get; set; }
    public decimal   QuantityOrdered         { get; set; }
    public decimal   QuantityReceived        { get; set; }
    public string    DemandSourceType        { get; set; } = string.Empty;
    public Guid      DemandSourceUuid        { get; set; }
    public string?   DemandReference         { get; set; }
    public string    SupplyMethod            { get; set; } = string.Empty;
    public string?   SupplySourceType        { get; set; }
    public Guid?     SupplySourceUuid        { get; set; }
    public Guid?     SupplySourceLineUuid    { get; set; }
    public string?   SupplySourceReference   { get; set; }
    public Guid      WarehouseUuid           { get; set; }
    public DateTime  RequiredDate            { get; set; }
    public int       Priority                { get; set; } = AllocationPriority.Normal;
    public string    Status                  { get; set; } = SupplyRequirementStatus.Open;
    public string?   Notes                   { get; set; }
    public int       CreatedBy               { get; set; }
    public DateTime  CreatedAt               { get; set; } = DateTime.UtcNow;
    public DateTime  UpdatedAt               { get; set; } = DateTime.UtcNow;
    public byte[]    RowVersion              { get; set; } = [];

    public decimal QuantityOutstanding => Math.Max(0m, QuantityRequired - QuantityReceived);
}

/// <summary>
/// Materials moving between stock and the production floor for one order (A30 §16). Named so it
/// cannot be confused with the project-site material issue request/voucher this module already has.
/// </summary>
internal class ProductionMaterialIssue : ITenantScopedEntity
{
    public int       Id                { get; set; }
    public Guid      UUID              { get; set; } = Guid.NewGuid();
    public Guid      OrganizationId    { get; set; }
    public string    IssueNumber       { get; set; } = string.Empty;
    public int       ProductionOrderId { get; set; }
    public Guid      WarehouseUuid     { get; set; }
    public string    IssueType         { get; set; } = ProductionIssueType.Standard;
    public string    Status            { get; set; } = ProductionIssueStatus.Draft;
    public int       CreatedBy         { get; set; }
    public DateTime  CreatedAt         { get; set; } = DateTime.UtcNow;
    public int?      ConfirmedBy       { get; set; }
    public DateTime? ConfirmedAt       { get; set; }
    public int?      ReversedBy        { get; set; }
    public DateTime? ReversedAt        { get; set; }
    public string?   Notes             { get; set; }

    public ProductionOrder ProductionOrder { get; set; } = null!;
    public ICollection<ProductionMaterialIssueLine> Lines { get; set; } = new List<ProductionMaterialIssueLine>();
}

internal class ProductionMaterialIssueLine : ITenantScopedEntity
{
    public int      Id                  { get; set; }
    public Guid     UUID                { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId      { get; set; }
    public int      IssueId             { get; set; }
    public int      RequirementId       { get; set; }
    /// <summary>The variant that actually moved — differs from the requirement's on a substitution.</summary>
    public Guid     MaterialVariantUuid { get; set; }
    public decimal  Quantity            { get; set; }
    public string   Uom                 { get; set; } = string.Empty;
    public decimal  UnitCost            { get; set; }
    public string?  BatchNumber         { get; set; }
    public string?  Notes               { get; set; }

    public ProductionMaterialIssue        Issue       { get; set; } = null!;
    public ProductionMaterialRequirement  Requirement { get; set; } = null!;
}

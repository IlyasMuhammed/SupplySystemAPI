using SMS.Shared.Common;

namespace SMS.Modules.Material.Domain;

/// <summary>A30 §9.1 — the six states a bill of materials moves through.</summary>
internal static class BomStatus
{
    public const string Draft     = "DRAFT";
    public const string Submitted = "SUBMITTED";
    public const string Approved  = "APPROVED";
    public const string Active    = "ACTIVE";
    public const string Obsolete  = "OBSOLETE";
    public const string Rejected  = "REJECTED";

    public static readonly IReadOnlyList<string> All = [Draft, Submitted, Approved, Active, Obsolete, Rejected];

    /// <summary>States in which the lines can still change in place.</summary>
    public static bool IsEditable(string status) => status is Draft or Rejected;

    /// <summary>States that take part in the circular-reference walk: anything that could become active.</summary>
    public static bool IsLive(string status) => status is Draft or Submitted or Approved or Active;
}

/// <summary>A36 SVC-BOM-01..05 — the user-facing refusals (API-CONTRACT §2).</summary>
internal static class ServiceBomMessages
{
    public const string NotEnabled                    = "This product does not have service BOM enabled";
    public const string SupplierRequired              = "Subcontract supplier is required for subcontracted BOM lines";
    public const string SupplierOnlyForSubcontract    = "Supplier reference is only valid for subcontracted lines";
    public const string SubcontractMaterialNotService = "Subcontracted line material must be a service-type product";
    public const string LaborNotHours                 = "Internal labor lines must use hours (HR) as unit of measure";
    public const string SupplierNotVendor             = "Subcontract supplier must be a vendor of your organization";
    public const string SourceOnlyOnServiceBom        = "Subcontracted and internal labor lines are only allowed on a service BOM";
}

/// <summary>
/// How a manufactured product is made (A30 §7): one version of the recipe. Versions are immutable
/// once they leave DRAFT — a change to a used recipe is a new version, so a production order that
/// snapshotted this one is never rewritten underneath it.
/// </summary>
internal class BillOfMaterial : ITenantScopedEntity, IHasModifiedAt
{
    public int       Id                 { get; set; }
    public Guid      UUID               { get; set; } = Guid.NewGuid();
    public Guid      OrganizationId     { get; set; }
    public Guid      TraceId            { get; set; } = Guid.NewGuid();
    public string    BomNumber          { get; set; } = string.Empty;
    /// <summary>The output product, by inventory.Products.Uuid. Must be supplied by MANUFACTURE.</summary>
    public Guid      ProductUuid        { get; set; }
    /// <summary>Optional: the recipe for one variant of the product rather than the product as a whole.</summary>
    public Guid?     ProductVariantUuid { get; set; }
    public int       Version            { get; set; }
    public string    Status             { get; set; } = BomStatus.Draft;
    public DateTime? EffectiveFrom      { get; set; }
    public DateTime? EffectiveTo        { get; set; }
    /// <summary>How many units of output the line quantities are for. Usually 1.</summary>
    public decimal   BaseQuantity       { get; set; } = 1m;
    public string    BaseUom            { get; set; } = string.Empty;
    public string?   Notes              { get; set; }
    public int       CreatedBy          { get; set; }
    public DateTime  CreatedAt          { get; set; } = DateTime.UtcNow;
    public DateTime  UpdatedAt          { get; set; } = DateTime.UtcNow;
    /// <summary>A37 D-16 — UTC, set by MaterialDbContext on every insert/update (sync delta).</summary>
    public DateTime  ModifiedAt         { get; set; } = DateTime.UtcNow;
    /// <summary>
    /// A37 D-11 — UNIVERSAL (default), PRODUCTION_PREFERRED or SERVICE_PREFERRED (<see cref="SMS.Shared.Common.BomUsage"/>):
    /// which pickers list it first. Advisory; editable in any status but OBSOLETE.
    /// </summary>
    public string    BomUsage           { get; set; } = SMS.Shared.Common.BomUsage.Universal;
    public int?      SubmittedBy        { get; set; }
    public DateTime? SubmittedAt        { get; set; }
    public int?      ApprovedBy         { get; set; }
    public DateTime? ApprovedAt         { get; set; }
    public int?      RejectedBy         { get; set; }
    public DateTime? RejectedAt         { get; set; }
    public string?   RejectionReason    { get; set; }
    public int?      ActivatedBy        { get; set; }
    public DateTime? ActivatedAt        { get; set; }
    public int?      ObsoletedBy        { get; set; }
    public DateTime? ObsoletedAt        { get; set; }
    public byte[]    RowVersion         { get; set; } = [];

    public ICollection<BillOfMaterialLine> Lines { get; set; } = new List<BillOfMaterialLine>();
}

/// <summary>One input of the recipe: this much of that variant per <see cref="BillOfMaterial.BaseQuantity"/> of output.</summary>
internal class BillOfMaterialLine : ITenantScopedEntity, IHasModifiedAt
{
    public int      Id                   { get; set; }
    public Guid     UUID                 { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId       { get; set; }
    public int      BomId                { get; set; }
    public int      Sequence             { get; set; }
    /// <summary>The input, by inventory.ProductVariants.Uuid. Must be available for production (D2).</summary>
    public Guid     MaterialVariantUuid  { get; set; }
    /// <summary>Its parent product, denormalised for the circular-reference walk and listing.</summary>
    public Guid     MaterialProductUuid  { get; set; }
    public decimal  Quantity             { get; set; }
    public string   Uom                  { get; set; } = string.Empty;
    public decimal  ScrapPercentage      { get; set; }
    /// <summary>A shortage of a critical material keeps the production order from becoming READY.</summary>
    public bool     IsCritical           { get; set; } = true;
    public Guid?    AlternateVariantUuid { get; set; }
    public string?  Notes                { get; set; }
    /// <summary>
    /// A36 D-4 — STOCK (default), SUBCONTRACT or INTERNAL_LABOR (<see cref="BomLineSourceType"/>). Only a service BOM
    /// may carry non-STOCK lines.
    /// </summary>
    public string   SourceType           { get; set; } = BomLineSourceType.Stock;
    /// <summary>A36 D-4 — the vendor (business partner UUID, no FK) a SUBCONTRACT line is bought from; null otherwise.</summary>
    public Guid?    SubcontractSupplierUuid { get; set; }
    /// <summary>A37 D-16 — UTC, set by MaterialDbContext on every insert/update.</summary>
    public DateTime ModifiedAt           { get; set; } = DateTime.UtcNow;

    public BillOfMaterial Bom { get; set; } = null!;
}

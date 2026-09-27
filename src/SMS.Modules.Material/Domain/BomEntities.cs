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

/// <summary>
/// How a manufactured product is made (A30 §7): one version of the recipe. Versions are immutable
/// once they leave DRAFT — a change to a used recipe is a new version, so a production order that
/// snapshotted this one is never rewritten underneath it.
/// </summary>
internal class BillOfMaterial : ITenantScopedEntity
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
internal class BillOfMaterialLine : ITenantScopedEntity
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

    public BillOfMaterial Bom { get; set; } = null!;
}

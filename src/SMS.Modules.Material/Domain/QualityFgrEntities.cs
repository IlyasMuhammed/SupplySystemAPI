using SMS.Shared.Common;

namespace SMS.Modules.Material.Domain;

// ── Codes (A30 §18, §19) ──────────────────────────────────────────────────────

internal static class QiLineResult
{
    public const string Pass   = "PASS";
    public const string Fail   = "FAIL";
    public const string Hold   = "HOLD";
    public const string Rework = "REWORK";

    public static readonly IReadOnlyList<string> All = [Pass, Fail, Hold, Rework];
}

/// <summary>§18.1 overall_result — derived from the lines, never chosen directly (see QualityInspectionService).</summary>
internal static class QiOverallResult
{
    public const string Passed          = "PASSED";
    public const string PartiallyPassed = "PARTIALLY_PASSED";
    public const string Rejected        = "REJECTED";
    /// <summary>Also covers an inspection that was entirely REWORK lines — neither accepted nor rejected, both need a person's follow-up.</summary>
    public const string Hold            = "HOLD";
}

internal static class FgrStatus
{
    public const string Draft     = "DRAFT";
    public const string Confirmed = "CONFIRMED";
    public const string Reversed  = "REVERSED";
}

// ── Entities ──────────────────────────────────────────────────────────────────

/// <summary>
/// One inspection of a production order's output (A30 §18). One-shot: exactly one per order, taken
/// against the whole of <see cref="ProductionOrder.ProducedQuantity"/> at the point production
/// finished — not a stock movement itself, only the decision that makes accepted quantity eligible
/// for a Finished Goods Receipt and rejected quantity a scrap record.
/// </summary>
internal class QualityInspection : ITenantScopedEntity
{
    public int       Id                { get; set; }
    public Guid      UUID              { get; set; } = Guid.NewGuid();
    public Guid      OrganizationId    { get; set; }
    public string    InspectionNumber  { get; set; } = string.Empty;
    public int       ProductionOrderId { get; set; }
    public decimal   InspectedQuantity { get; set; }
    public decimal   AcceptedQuantity  { get; set; }
    public decimal   RejectedQuantity  { get; set; }
    public decimal   HoldQuantity      { get; set; }
    public decimal   ReworkQuantity    { get; set; }
    public string    OverallResult     { get; set; } = QiOverallResult.Hold;
    public int       InspectedBy       { get; set; }
    public DateTime  InspectedAt       { get; set; } = DateTime.UtcNow;
    public string?   Notes             { get; set; }
    public DateTime  CreatedAt         { get; set; } = DateTime.UtcNow;

    public ProductionOrder ProductionOrder { get; set; } = null!;
    public ICollection<QualityInspectionLine> Lines { get; set; } = new List<QualityInspectionLine>();
}

/// <summary>One check within an inspection. Its whole checked quantity goes to exactly one outcome (§18.2).</summary>
internal class QualityInspectionLine : ITenantScopedEntity
{
    public int      Id               { get; set; }
    public Guid     UUID             { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId   { get; set; }
    public int      InspectionId     { get; set; }
    public string   CheckName        { get; set; } = string.Empty;
    public string   Result           { get; set; } = QiLineResult.Pass;
    public decimal  QuantityChecked  { get; set; }
    public string?  DefectCode       { get; set; }
    public string?  Notes            { get; set; }

    public QualityInspection Inspection { get; set; } = null!;
}

/// <summary>
/// Accepted production output moving into finished-goods stock (A30 §19). One production order's
/// accepted quantity (from its one <see cref="QualityInspection"/>) can be received across more than
/// one FGR — partial production is normal, and a receipt need not wait for all of it.
/// </summary>
internal class FinishedGoodsReceipt : ITenantScopedEntity
{
    public int       Id                   { get; set; }
    public Guid      UUID                 { get; set; } = Guid.NewGuid();
    public Guid      OrganizationId       { get; set; }
    public string    FgrNumber            { get; set; } = string.Empty;
    public int       ProductionOrderId    { get; set; }
    public int       QualityInspectionId  { get; set; }
    public Guid      WarehouseUuid        { get; set; }
    public decimal   TotalQuantity        { get; set; }
    public string    Status               { get; set; } = FgrStatus.Draft;
    public int       ReceivedBy           { get; set; }
    public DateTime  ReceivedAt           { get; set; } = DateTime.UtcNow;
    public string?   Notes                { get; set; }
    public DateTime  CreatedAt            { get; set; } = DateTime.UtcNow;

    public ProductionOrder     ProductionOrder    { get; set; } = null!;
    public QualityInspection   QualityInspection  { get; set; } = null!;
}

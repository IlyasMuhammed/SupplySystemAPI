namespace SMS.Modules.Suppliers.Domain;

// FSD Section 5.4 — configurable scoring weights per dimension. Exactly 5 rows (DELIVERY, QUANTITY,
// QUALITY, PRICE, DOCUMENTATION), seeded once and only ever updated in place via PUT /api/admin/scorecard-weights.
internal class ScorecardDimensionWeight
{
    public int      Id               { get; set; }
    public string   DimensionCode    { get; set; } = string.Empty; // DELIVERY | QUANTITY | QUALITY | PRICE | DOCUMENTATION
    public string   DimensionName    { get; set; } = string.Empty;
    public decimal  WeightPercentage { get; set; }
    public decimal  MaxPoints        { get; set; }
    public bool     IsActive         { get; set; } = true;
    public DateTime? ModifiedDate    { get; set; }
}

// FSD Section 5.3 — periodic rolled-up supplier score, one row per supplier per scored period.
// A given (SupplierId, PeriodStart, PeriodEnd) is overwritten in place on re-recalculation (SC-005),
// so this is "current snapshot per period", not a strictly append-only history.
internal class SupplierScoreSnapshot
{
    public int      Id                 { get; set; }
    public Guid     UUID               { get; set; }
    public Guid     SupplierId         { get; set; }
    public DateTime PeriodStart        { get; set; }
    public DateTime PeriodEnd          { get; set; }
    public decimal  DeliveryScore      { get; set; }
    public decimal  QuantityScore      { get; set; }
    public decimal  QualityScore       { get; set; }
    public decimal  PriceScore         { get; set; }
    public decimal  DocumentationScore { get; set; }
    public decimal  TotalScore         { get; set; } // composite_score: average GrnScoreDetail.WeightedScore for the period
    public string   Grade              { get; set; } = string.Empty; // A | B | C | D | F
    /// <summary>IMPROVING | STABLE | DECLINING vs the immediately preceding period's snapshot; null when
    /// there is no prior snapshot to compare against (e.g. a supplier's first-ever scored period).</summary>
    public string?  Trend              { get; set; }
    public int      GrnCount           { get; set; }
    public int      CreatedBy          { get; set; }
    public DateTime CreatedDate        { get; set; }
    public int?      ModifiedBy        { get; set; }
    public DateTime? ModifiedDate      { get; set; }
}

// Per-GRN raw dimension scores — the input rows that SupplierScoreSnapshots are rolled up from.
internal class GrnScoreDetail
{
    public int      Id                  { get; set; } // grn_score_id
    public Guid     GrnId               { get; set; }
    public Guid     SupplierId          { get; set; }
    public decimal  DeliveryPoints      { get; set; }
    public decimal  QuantityPoints      { get; set; }
    public decimal  QualityPoints       { get; set; }
    public decimal  PricePoints         { get; set; }
    public decimal  DocumentationPoints { get; set; }
    public decimal  TotalRawScore       { get; set; }
    public decimal  WeightedScore       { get; set; }
    public DateTime ScoredAt            { get; set; }
}

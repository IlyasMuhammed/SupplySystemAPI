namespace SMS.Modules.Suppliers.Models;

public class SupplierScorecardRankingFilter
{
    public string? PeriodStart { get; set; }
    public string? PeriodEnd   { get; set; }
}

public class SupplierScorecardRankingItem
{
    public int     Rank               { get; set; }
    public Guid    SupplierId         { get; set; }
    public string  SupplierName       { get; set; } = string.Empty;
    public string  Grade              { get; set; } = string.Empty;
    public decimal CompositeScore     { get; set; }
    public decimal DeliveryScore      { get; set; }
    public decimal QuantityScore      { get; set; }
    public decimal QualityScore       { get; set; }
    public decimal PriceScore         { get; set; }
    public decimal DocumentationScore { get; set; }
    public int      GrnCount          { get; set; }
    /// <summary>IMPROVING | STABLE | DECLINING | null — taken directly from the most recent snapshot
    /// within the window, not recomputed for the window as a whole.</summary>
    public string?  Trend             { get; set; }
    /// <summary>Most-recent-in-window snapshot's TotalScore minus the snapshot immediately before it
    /// (which may fall outside the window). Null when there's no prior snapshot to compare against —
    /// such suppliers are excluded from "most improved"/"most declined" but still ranked.</summary>
    public decimal? ScoreDelta        { get; set; }
}

public class SupplierScorecardRankingResponse
{
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd   { get; set; }
    public List<SupplierScorecardRankingItem> Suppliers { get; set; } = [];
}

public class GrnScoreListItem
{
    public Guid     GrnId         { get; set; }
    public string?  GrnNumber     { get; set; }
    public decimal  TotalRawScore { get; set; }
    public decimal  WeightedScore { get; set; }
    public DateTime ScoredAt      { get; set; }
}

public class ScorecardTrendPoint
{
    public DateTime PeriodStart    { get; set; }
    public DateTime PeriodEnd      { get; set; }
    public decimal  CompositeScore { get; set; }
    public string   Grade          { get; set; } = string.Empty;
}

// FSD Addendum 23, SC-007 — lightweight payload for inline grade checks (Supplier Master banner,
// PO creation warning). Intentionally carries nothing beyond grade + composite score.
public class SupplierScoreSummaryModel
{
    public string?  Grade          { get; set; } // null when the supplier has never been scored
    public decimal? CompositeScore { get; set; }
}

public class SupplierScorecardDetailModel
{
    public Guid    SupplierId         { get; set; }
    public string  SupplierName       { get; set; } = string.Empty;
    public string  Grade              { get; set; } = string.Empty;
    public decimal CompositeScore     { get; set; }
    public string? Trend              { get; set; }
    public decimal DeliveryScore      { get; set; }
    public decimal QuantityScore      { get; set; }
    public decimal QualityScore       { get; set; }
    public decimal PriceScore         { get; set; }
    public decimal DocumentationScore { get; set; }
    public DateTime? LastScoredAt     { get; set; }
    public List<GrnScoreListItem>     GrnScores     { get; set; } = [];
    /// <summary>Composite score per period, oldest first, for the last 4 periods — line chart data.</summary>
    public List<ScorecardTrendPoint>  TrendHistory  { get; set; } = [];
}

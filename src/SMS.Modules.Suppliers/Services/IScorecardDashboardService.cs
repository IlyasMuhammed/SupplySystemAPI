using SMS.Modules.Suppliers.Models;

namespace SMS.Modules.Suppliers.Services;

public interface IScorecardDashboardService
{
    /// <summary>Ranked supplier list built from pre-computed SupplierScoreSnapshots within the window
    /// (never recalculates from raw GrnScoreDetails) — one row per supplier with at least one snapshot
    /// fully contained in [periodStart, periodEnd).</summary>
    Task<SupplierScorecardRankingResponse> GetRankingAsync(DateTime periodStart, DateTime periodEnd);

    /// <summary>Per-dimension breakdown (from the supplier's most recent snapshot), recent per-GRN
    /// score list, and up to the last 4 periods' composite-score trend. Null if the supplier has never
    /// been scored.</summary>
    Task<SupplierScorecardDetailModel?> GetSupplierDetailAsync(Guid supplierId);

    /// <summary>Grade + composite score only, from the supplier's most recent snapshot — for inline
    /// grade checks (SC-007). Both fields null when the supplier has never been scored.</summary>
    Task<SupplierScoreSummaryModel> GetScoreSummaryAsync(Guid supplierId);
}

using Microsoft.EntityFrameworkCore;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Models;
using SMS.Modules.Warehouse.Data;

namespace SMS.Modules.Suppliers.Services;

// FSD Section 5.2 — read-only dashboard queries over pre-computed SupplierScoreSnapshots /
// GrnScoreDetails (SC-003/SC-004/SC-005). Never recalculates scores itself.
internal sealed class ScorecardDashboardService : IScorecardDashboardService
{
    private readonly SuppliersDbContext _db;
    private readonly WarehouseDbContext _warehouse;

    public ScorecardDashboardService(SuppliersDbContext db, WarehouseDbContext warehouse)
    {
        _db        = db;
        _warehouse = warehouse;
    }

    public async Task<SupplierScorecardRankingResponse> GetRankingAsync(DateTime periodStart, DateTime periodEnd)
    {
        var snapshots = await _db.SupplierScoreSnapshots.AsNoTracking()
            .Where(s => s.PeriodStart >= periodStart && s.PeriodEnd <= periodEnd)
            .ToListAsync();

        if (snapshots.Count == 0)
            return new SupplierScorecardRankingResponse { PeriodStart = periodStart, PeriodEnd = periodEnd };

        var supplierIds = snapshots.Select(s => s.SupplierId).Distinct().ToList();
        var supplierNames = await _db.Suppliers.AsNoTracking()
            .Where(s => !s.IsDelete && supplierIds.Contains(s.UUID))
            .Select(s => new { s.UUID, s.SupplierName })
            .ToDictionaryAsync(s => s.UUID, s => s.SupplierName);

        var items = snapshots
            .Where(s => supplierNames.ContainsKey(s.SupplierId))
            .GroupBy(s => s.SupplierId)
            .Select(g =>
            {
                var rows       = g.OrderByDescending(s => s.PeriodEnd).ToList();
                var totalGrns  = rows.Sum(s => s.GrnCount);
                var weight     = totalGrns > 0 ? totalGrns : rows.Count; // fall back to a simple average if every row has GrnCount 0

                decimal WeightedAvg(Func<SMS.Modules.Suppliers.Domain.SupplierScoreSnapshot, decimal> selector) =>
                    totalGrns > 0
                        ? Math.Round(rows.Sum(s => selector(s) * (s.GrnCount > 0 ? s.GrnCount : 1)) / weight, 2)
                        : Math.Round(rows.Average(selector), 2);

                var composite = WeightedAvg(s => s.TotalScore);
                var mostRecent = rows[0];
                var secondMostRecent = rows.Count > 1 ? rows[1] : null;

                return new SupplierScorecardRankingItem
                {
                    SupplierId         = g.Key,
                    SupplierName       = supplierNames[g.Key],
                    Grade              = ScorecardGradeCalculator.GradeFor(composite),
                    CompositeScore     = composite,
                    DeliveryScore      = WeightedAvg(s => s.DeliveryScore),
                    QuantityScore      = WeightedAvg(s => s.QuantityScore),
                    QualityScore       = WeightedAvg(s => s.QualityScore),
                    PriceScore         = WeightedAvg(s => s.PriceScore),
                    DocumentationScore = WeightedAvg(s => s.DocumentationScore),
                    GrnCount           = totalGrns,
                    Trend              = mostRecent.Trend,
                    ScoreDelta         = secondMostRecent is null ? null : Math.Round(mostRecent.TotalScore - secondMostRecent.TotalScore, 2)
                };
            })
            .OrderByDescending(i => i.CompositeScore)
            .ToList();

        for (var i = 0; i < items.Count; i++)
            items[i].Rank = i + 1;

        return new SupplierScorecardRankingResponse { PeriodStart = periodStart, PeriodEnd = periodEnd, Suppliers = items };
    }

    public async Task<SupplierScorecardDetailModel?> GetSupplierDetailAsync(Guid supplierId)
    {
        var supplier = await _db.Suppliers.AsNoTracking()
            .Where(s => s.UUID == supplierId && !s.IsDelete)
            .Select(s => new { s.SupplierName })
            .FirstOrDefaultAsync();
        if (supplier is null) return null;

        var latestSnapshot = await _db.SupplierScoreSnapshots.AsNoTracking()
            .Where(s => s.SupplierId == supplierId)
            .OrderByDescending(s => s.PeriodEnd)
            .FirstOrDefaultAsync();
        if (latestSnapshot is null) return null; // never scored

        var trendHistory = await _db.SupplierScoreSnapshots.AsNoTracking()
            .Where(s => s.SupplierId == supplierId)
            .OrderByDescending(s => s.PeriodEnd)
            .Take(4)
            .Select(s => new ScorecardTrendPoint
            {
                PeriodStart = s.PeriodStart, PeriodEnd = s.PeriodEnd, CompositeScore = s.TotalScore, Grade = s.Grade
            })
            .ToListAsync();
        trendHistory.Reverse(); // oldest first, for a left-to-right line chart

        var grnScores = await _db.GrnScoreDetails.AsNoTracking()
            .Where(s => s.SupplierId == supplierId)
            .OrderByDescending(s => s.ScoredAt)
            .Take(50)
            .ToListAsync();

        var grnIds = grnScores.Select(s => s.GrnId).ToList();
        var grnNumbers = await _warehouse.Grns.AsNoTracking()
            .Where(g => grnIds.Contains(g.UUID))
            .Select(g => new { g.UUID, g.GrnNumber })
            .ToDictionaryAsync(g => g.UUID, g => g.GrnNumber);

        return new SupplierScorecardDetailModel
        {
            SupplierId          = supplierId,
            SupplierName        = supplier.SupplierName,
            Grade               = latestSnapshot.Grade,
            CompositeScore      = latestSnapshot.TotalScore,
            Trend               = latestSnapshot.Trend,
            DeliveryScore       = latestSnapshot.DeliveryScore,
            QuantityScore       = latestSnapshot.QuantityScore,
            QualityScore        = latestSnapshot.QualityScore,
            PriceScore          = latestSnapshot.PriceScore,
            DocumentationScore  = latestSnapshot.DocumentationScore,
            LastScoredAt        = grnScores.Count > 0 ? grnScores[0].ScoredAt : null,
            GrnScores = grnScores.Select(s => new GrnScoreListItem
            {
                GrnId         = s.GrnId,
                GrnNumber     = grnNumbers.GetValueOrDefault(s.GrnId),
                TotalRawScore = s.TotalRawScore,
                WeightedScore = s.WeightedScore,
                ScoredAt      = s.ScoredAt
            }).ToList(),
            TrendHistory = trendHistory
        };
    }

    public async Task<SupplierScoreSummaryModel> GetScoreSummaryAsync(Guid supplierId)
    {
        var latest = await _db.SupplierScoreSnapshots.AsNoTracking()
            .Where(s => s.SupplierId == supplierId)
            .OrderByDescending(s => s.PeriodEnd)
            .Select(s => new { s.Grade, s.TotalScore })
            .FirstOrDefaultAsync();

        return new SupplierScoreSummaryModel
        {
            Grade          = latest?.Grade,
            CompositeScore = latest?.TotalScore
        };
    }
}

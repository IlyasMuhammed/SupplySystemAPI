using Microsoft.EntityFrameworkCore;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Domain;

namespace SMS.Modules.Suppliers.Services;

// FSD Section 6.2 — rolls per-GRN weighted scores (SC-004's GrnScoreDetails) up into a periodic
// SupplierScoreSnapshot per supplier, with a grade (A/B/C/D/F, FSD Section 5.1) and a trend relative
// to the immediately preceding period. Grade thresholds and trend bands are reverse-engineered from
// the ticket's worked examples (87.5 -> B; 70 -> 82 -> IMPROVING; 85 -> 83 -> STABLE), since the
// underlying FSD tables weren't available.
internal sealed class ScorecardRecalculationService : IScorecardRecalculationService
{
    private readonly SuppliersDbContext _db;
    public ScorecardRecalculationService(SuppliersDbContext db) => _db = db;

    public async Task<int> RecalculateAllAsync(DateTime periodStart, DateTime periodEnd, int triggeredBy)
    {
        var supplierIdsWithScores = await _db.GrnScoreDetails
            .Where(s => s.ScoredAt >= periodStart && s.ScoredAt < periodEnd)
            .Select(s => s.SupplierId)
            .Distinct()
            .ToListAsync();

        if (supplierIdsWithScores.Count == 0) return 0;

        var activeSupplierIds = await _db.Suppliers
            .Where(s => !s.IsDelete && s.IsActive && supplierIdsWithScores.Contains(s.UUID))
            .Select(s => s.UUID)
            .ToListAsync();

        var recalculated = 0;
        foreach (var supplierId in activeSupplierIds)
        {
            if (await RecalculateSupplierAsync(supplierId, periodStart, periodEnd, triggeredBy))
                recalculated++;
        }

        return recalculated;
    }

    public async Task<bool> RecalculateSupplierAsync(Guid supplierId, DateTime periodStart, DateTime periodEnd, int triggeredBy)
    {
        var scores = await _db.GrnScoreDetails
            .Where(s => s.SupplierId == supplierId && s.ScoredAt >= periodStart && s.ScoredAt < periodEnd)
            .ToListAsync();

        if (scores.Count == 0) return false; // zero GRNs in the period -> no snapshot

        var composite = Math.Round(scores.Average(s => s.WeightedScore), 2);
        var grade     = ScorecardGradeCalculator.GradeFor(composite);

        var previous = await _db.SupplierScoreSnapshots
            .Where(s => s.SupplierId == supplierId && s.PeriodEnd <= periodStart)
            .OrderByDescending(s => s.PeriodEnd)
            .FirstOrDefaultAsync();

        var trend = previous is null ? null : TrendFor(composite, previous.TotalScore);
        var now   = DateTime.UtcNow;

        var existing = await _db.SupplierScoreSnapshots.FirstOrDefaultAsync(
            s => s.SupplierId == supplierId && s.PeriodStart == periodStart && s.PeriodEnd == periodEnd);

        if (existing is null)
        {
            _db.SupplierScoreSnapshots.Add(new SupplierScoreSnapshot
            {
                UUID               = Guid.NewGuid(),
                SupplierId         = supplierId,
                PeriodStart        = periodStart,
                PeriodEnd          = periodEnd,
                DeliveryScore      = Math.Round(scores.Average(s => s.DeliveryPoints), 2),
                QuantityScore      = Math.Round(scores.Average(s => s.QuantityPoints), 2),
                QualityScore       = Math.Round(scores.Average(s => s.QualityPoints), 2),
                PriceScore         = Math.Round(scores.Average(s => s.PricePoints), 2),
                DocumentationScore = Math.Round(scores.Average(s => s.DocumentationPoints), 2),
                TotalScore         = composite,
                Grade              = grade,
                Trend              = trend,
                GrnCount           = scores.Count,
                CreatedBy          = triggeredBy,
                CreatedDate        = now
            });
        }
        else
        {
            // Admin-triggered recalculation overwrites the existing snapshot for the same period.
            existing.DeliveryScore      = Math.Round(scores.Average(s => s.DeliveryPoints), 2);
            existing.QuantityScore      = Math.Round(scores.Average(s => s.QuantityPoints), 2);
            existing.QualityScore       = Math.Round(scores.Average(s => s.QualityPoints), 2);
            existing.PriceScore         = Math.Round(scores.Average(s => s.PricePoints), 2);
            existing.DocumentationScore = Math.Round(scores.Average(s => s.DocumentationPoints), 2);
            existing.TotalScore         = composite;
            existing.Grade              = grade;
            existing.Trend              = trend;
            existing.GrnCount           = scores.Count;
            existing.ModifiedBy         = triggeredBy;
            existing.ModifiedDate       = now;
        }

        await _db.SaveChangesAsync();
        return true;
    }

    private static string TrendFor(decimal current, decimal previous)
    {
        var diff = current - previous;
        return diff switch
        {
            > 5m  => "IMPROVING",
            < -5m => "DECLINING",
            _     => "STABLE"
        };
    }
}

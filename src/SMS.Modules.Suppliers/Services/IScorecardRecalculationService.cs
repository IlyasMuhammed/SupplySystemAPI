namespace SMS.Modules.Suppliers.Services;

public interface IScorecardRecalculationService
{
    /// <summary>Recalculates every active supplier that has at least one scored GRN in the period.
    /// Suppliers with zero scored GRNs are skipped entirely (no snapshot, excluded from the dashboard).
    /// Returns the number of suppliers actually recalculated.</summary>
    Task<int> RecalculateAllAsync(DateTime periodStart, DateTime periodEnd, int triggeredBy);

    /// <summary>Averages this supplier's GrnScoreDetails.WeightedScore within the period into a
    /// SupplierScoreSnapshot (grade + trend vs the immediately preceding period), overwriting any
    /// existing snapshot for the same (SupplierId, PeriodStart, PeriodEnd). Returns false — and writes
    /// nothing — when the supplier has no scored GRNs in the period.</summary>
    Task<bool> RecalculateSupplierAsync(Guid supplierId, DateTime periodStart, DateTime periodEnd, int triggeredBy);
}

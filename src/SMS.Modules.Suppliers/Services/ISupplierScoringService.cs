namespace SMS.Modules.Suppliers.Services;

public interface ISupplierScoringService
{
    /// <summary>Scores a single GRN across all 5 scorecard dimensions (FSD Section 4.1) and writes/updates
    /// its GrnScoreDetails row. Safe to call more than once for the same GRN (e.g. after a weight change) —
    /// re-scoring updates the existing row in place rather than duplicating it.</summary>
    Task ScoreGrnAsync(Guid grnId);
}

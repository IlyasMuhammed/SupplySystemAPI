namespace SMS.Modules.Suppliers.Services;

public interface ISupplierScoringService
{
    /// <summary>Scores a single GRN across all 5 scorecard dimensions (FSD Section 4.1) and writes/updates
    /// its GrnScoreDetails row. Safe to call more than once for the same GRN (e.g. after a weight change) —
    /// re-scoring updates the existing row in place rather than duplicating it.</summary>
    Task ScoreGrnAsync(Guid grnId);

    /// <summary>Scores every APPROVED GRN that has no GrnScoreDetails row yet — for GRNs approved before
    /// the approval-triggered scoring hook was in place (or after any outage of it). Safe to call anytime;
    /// already-scored GRNs are left untouched unless <paramref name="force"/> is true, in which case every
    /// approved GRN is rescored (e.g. after a scoring-logic change). Returns how many GRNs were scored.</summary>
    Task<int> BackfillMissingScoresAsync(bool force = false);
}

using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.Services;

/// <summary>
/// FSD Addendum 24 (ML-001) — the organization-wide mirror of ISupplierLedgerService. Internal to
/// SMS.Modules.Finance: its only caller is SupplierLedgerService.PostEntryAsync, which must add
/// this entry to the SAME DbContext instance BEFORE its own SaveChangesAsync call so both ledgers
/// commit atomically. Never call SaveChangesAsync from here.
/// </summary>
internal interface IMasterFinancialLedgerService
{
    /// <summary>
    /// Reads the last master entry's BalanceAfter fresh, computes the next SequenceNo/BalanceAfter,
    /// and Adds (but does not save) the new MasterFinancialLedger row to the tracked DbContext.
    /// Callers must re-invoke this (after detaching the previously returned entity) on every retry
    /// following a DbUpdateException, exactly as SupplierLedgerService already does for its own
    /// entry — a stale read here would silently produce a wrong organization-level running balance.
    /// </summary>
    Task<MasterFinancialLedger> BuildAndTrackEntryAsync(
        Guid supplierId, string supplierName, string transactionType, string referenceType,
        Guid referenceId, string referenceNo, decimal debitAmount, decimal creditAmount,
        string? narration, int createdBy);
}

/// <summary>
/// FSD Addendum 24 (ML-002) — read side of the master ledger, consumed by MasterLedgerController.
/// Public (unlike IMasterFinancialLedgerService) because it only deals in public model types, so a
/// public controller can depend on it directly.
/// </summary>
public interface IMasterLedgerQueryService
{
    /// <summary>Paginated master ledger entries, most recent first, with the given filters applied.</summary>
    Task<PaginatedResponse<MasterLedgerEntryModel>> GetLedgerAsync(MasterLedgerFilter filter);

    /// <summary>
    /// TotalPayables is always the latest BalanceAfter across the whole table (unfiltered); the
    /// other three fields are computed over entries matching the given filter.
    /// </summary>
    Task<MasterLedgerSummaryModel> GetSummaryAsync(MasterLedgerFilter filter);

    /// <summary>The current organization-wide balance — the latest entry's BalanceAfter, unfiltered.</summary>
    Task<MasterLedgerBalanceModel> GetCurrentBalanceAsync();
}
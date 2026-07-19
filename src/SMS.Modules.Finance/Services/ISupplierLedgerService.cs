using SMS.Modules.Finance.Models;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.Services;

public interface ISupplierLedgerService
{
    /// <summary>
    /// Appends a ledger entry for a supplier, computing BalanceAfter from the previous entry
    /// (previous BalanceAfter + debitAmount - creditAmount) and saving it. Concurrency-safe: two
    /// concurrent calls for the same supplier always produce distinct, correctly sequential
    /// balances — never a lost update.
    ///
    /// Callers that need this write to be atomic with their own business-entity write (e.g. an
    /// Invoice being created) should Add() that entity to the same scoped FinanceDbContext WITHOUT
    /// calling SaveChangesAsync themselves — PostEntryAsync's own SaveChangesAsync call will then
    /// commit both together, and its retry-on-conflict path leaves those other tracked changes
    /// alone (only the losing ledger-entry attempt is detached and retried).
    /// </summary>
    Task<SupplierLedgerEntryModel> PostEntryAsync(
        Guid supplierId, string transactionType, string referenceType, Guid referenceId, string referenceNo,
        decimal debitAmount, decimal creditAmount, string? narration, int createdBy);

    /// <summary>Paginated ledger entries for a supplier, newest first, optionally filtered by entry date range.</summary>
    Task<PaginatedResponse<SupplierLedgerEntryModel>> GetLedgerAsync(Guid supplierId, SupplierLedgerFilter filter);

    /// <summary>Aggregate debit/credit/net-balance summary for a supplier.</summary>
    Task<SupplierBalanceSummary> GetBalanceAsync(Guid supplierId);
}

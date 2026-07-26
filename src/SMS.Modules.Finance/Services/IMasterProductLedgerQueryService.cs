using SMS.Modules.Finance.Models;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.Services;

/// <summary>
/// FSD Addendum 24 (ML-004) — read side of the master product ledger, consumed by
/// MasterProductLedgerController. Public (unlike IMasterProductLedgerService, the write-side
/// contract in SMS.Shared.Common) because it only deals in public model types and its only
/// consumer lives in this same module.
/// </summary>
public interface IMasterProductLedgerQueryService
{
    /// <summary>Paginated master product ledger entries, most recent first, with filters applied.</summary>
    Task<PaginatedResponse<MasterProductLedgerEntryModel>> GetProductLedgerAsync(MasterProductLedgerFilter filter);

    /// <summary>Receipt/issue quantity and value totals for entries matching the given filter.</summary>
    Task<MasterProductLedgerSummaryModel> GetProductLedgerSummaryAsync(MasterProductLedgerFilter filter);

    /// <summary>
    /// Every movement for one product across ALL warehouses, chronological (oldest first) — the
    /// "Product Journey" view. Unlike GetProductLedgerAsync this is not paginated and ignores every
    /// filter except the product itself, by design (a journey is the product's complete history).
    /// </summary>
    Task<List<MasterProductLedgerEntryModel>> GetProductJourneyAsync(int productId);
}

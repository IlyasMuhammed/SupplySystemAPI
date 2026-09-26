using SMS.Modules.Material.Models;
using SMS.Shared.Pagination;

namespace SMS.Modules.Material.Services;

/// <summary>
/// A30 §19A — the debit/credit history of material consumed and finished goods produced, read
/// straight from what Material Issue, Finished Goods Receipt and Quality Inspection already
/// recorded. Not a stored ledger: the spec's own instruction (§19A) is that this is a query, not a
/// second source of truth, and everything it reports already exists in <c>inventory.InventoryLedgerEntries</c>
/// (material movements) or on the <see cref="Domain.QualityInspection"/> row (the scrap side, which
/// never touched inventory — see <see cref="QualityInspectionService"/>'s own note on why).
/// </summary>
public interface IProductionLedgerService
{
    Task<ProductionLedgerModel?> GetForOrderAsync(Guid productionOrderUuid);
    Task<PaginatedResponse<ProductionLedgerEntryModel>> GetListAsync(ProductionLedgerListFilter filter);
    Task<ProductionLedgerSummaryModel> GetSummaryAsync(ProductionLedgerListFilter filter);
}

using SMS.Modules.Reports.Models;
using SMS.Shared.Pagination;

namespace SMS.Modules.Reports.Services;

/// <summary>
/// A30-P5-02..06 §31 — the manufacturing reports that fill a genuine gap (see the model file's own
/// note on why most of the 28 listed reports are not duplicated here).
/// </summary>
public interface IManufacturingReportService
{
    Task<ProductionEfficiencyReport> GetProductionEfficiencyAsync(ManufacturingReportFilter filter);
    Task<QualityScrapReport> GetQualityScrapAsync(ManufacturingReportFilter filter);
    Task<PaginatedResponse<ProductionMaterialIssueRegisterItem>> GetMaterialIssueRegisterAsync(ManufacturingRegisterFilter filter);
    Task<PaginatedResponse<FinishedGoodsReceiptRegisterItem>> GetFinishedGoodsReceiptRegisterAsync(ManufacturingRegisterFilter filter);
    Task<LedgerReconciliationReport> GetLedgerReconciliationAsync(ManufacturingReportFilter filter);
    Task<ChainedManufacturingReport?> GetChainedManufacturingAsync(Guid productionOrderUuid);
}

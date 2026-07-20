using SMS.Modules.Reports.Models;
using SMS.Modules.Reports.Repositories;
using SMS.Shared.Pagination;

namespace SMS.Modules.Reports.Services;

internal sealed class ReportsService : IReportsService
{
    private readonly IReportsRepository _repo;
    public ReportsService(IReportsRepository repo) => _repo = repo;

    public Task<KpiDashboardModel>                          GetKpiDashboardAsync()                              => _repo.GetKpiDashboardAsync();
    public Task<List<SupplierPerformanceItem>>              GetSupplierPerformanceAsync(ReportDateFilter filter) => _repo.GetSupplierPerformanceAsync(filter);
    public Task<List<PoSummaryItem>>                        GetPoSummaryAsync(ReportDateFilter filter)           => _repo.GetPoSummaryAsync(filter);
    public Task<List<SpendBySupplierItem>>                  GetSpendBySupplierAsync(ReportDateFilter filter)     => _repo.GetSpendBySupplierAsync(filter);
    public Task<List<PendingApprovalItem>>                  GetPendingApprovalsAsync()                          => _repo.GetPendingApprovalsAsync();
    public Task<List<StockLevelItem>>                       GetStockLevelsAsync(ReportDateFilter filter)         => _repo.GetStockLevelsAsync(filter);
    public Task<StockLevelSummaryReport>                    GetStockLevelSummaryAsync(StockLevelSummaryFilter filter) => _repo.GetStockLevelSummaryAsync(filter);
    public Task<List<PrFulfillmentItem>>                    GetPrFulfillmentStatusAsync(PrFulfillmentFilter filter)  => _repo.GetPrFulfillmentStatusAsync(filter);
    public Task<PrPipelineReport>                           GetPrToPoPipelineAsync(PrPipelineFilter filter)          => _repo.GetPrToPoPipelineAsync(filter);
    public Task<List<ReorderAlertItem>>                     GetReorderAlertsAsync()                             => _repo.GetReorderAlertsAsync();
    public Task<List<InventoryValuationItem>>               GetInventoryValuationAsync()                        => _repo.GetInventoryValuationAsync();
    public Task<List<GrnVarianceItem>>                      GetGrnVarianceAsync(ReportDateFilter filter)         => _repo.GetGrnVarianceAsync(filter);
    public Task<List<ShipmentTrackerItem>>                  GetShipmentTrackerAsync(string? status)              => _repo.GetShipmentTrackerAsync(status);
    public Task<(List<InvoiceAgingItem>, List<InvoiceAgingBucketSummary>)> GetInvoiceAgingAsync()               => _repo.GetInvoiceAgingAsync();
    public Task<PaymentSummaryModel>                        GetPaymentSummaryAsync(ReportDateFilter filter)      => _repo.GetPaymentSummaryAsync(filter);
    public Task<SupplierLedgerSummaryReport>                GetSupplierLedgerSummaryAsync(SupplierLedgerSummaryFilter filter) => _repo.GetSupplierLedgerSummaryAsync(filter);
    public Task<SupplierOrdersReport>                       GetSupplierOrdersAsync(SupplierOrdersFilter filter)                => _repo.GetSupplierOrdersAsync(filter);
    public Task<SupplierComparisonResponse>                  GetSupplierComparisonAsync(List<Guid> supplierIds)                => _repo.GetSupplierComparisonAsync(supplierIds);
    public Task<GrnQualityAnalysisResponse>                   GetGrnQualityAnalysisAsync(GrnQualityAnalysisFilter filter)       => _repo.GetGrnQualityAnalysisAsync(filter);
    public Task<SupplierSpendAnalysisResponse>                GetSupplierSpendAnalysisAsync(SupplierSpendFilter filter)         => _repo.GetSupplierSpendAnalysisAsync(filter);
    public Task<DeliveryHeatmapResponse?>                     GetDeliveryPerformanceHeatmapAsync(Guid supplierId, int year)     => _repo.GetDeliveryPerformanceHeatmapAsync(supplierId, year);
    public Task<List<BudgetUtilizationItem>>                GetBudgetUtilizationAsync(ReportDateFilter filter)   => _repo.GetBudgetUtilizationAsync(filter);
    public Task<PaginatedResponse<AuditLogItemModel>>       GetAuditTrailAsync(AuditLogFilter filter)            => _repo.GetAuditTrailAsync(filter);
    public Task<List<UserActivityItem>>                     GetUserActivityAsync(ReportDateFilter filter)        => _repo.GetUserActivityAsync(filter);

    // ── Material Reports ──────────────────────────────────────────────────────
    public Task<List<MaterialIssueRegisterItem>>     GetMaterialIssueRegisterAsync(MaterialIssueRegisterFilter filter)         => _repo.GetMaterialIssueRegisterAsync(filter);
    public Task<List<MaterialConsumptionReportItem>> GetMaterialConsumptionReportAsync(MaterialConsumptionReportFilter filter) => _repo.GetMaterialConsumptionReportAsync(filter);
    public Task<List<ProjectConsumptionItem>>        GetProjectConsumptionAsync(ProjectConsumptionFilter filter)               => _repo.GetProjectConsumptionAsync(filter);
    public Task<List<DepartmentConsumptionItem>>     GetDepartmentConsumptionAsync(DepartmentConsumptionFilter filter)         => _repo.GetDepartmentConsumptionAsync(filter);
    public Task<List<StockMovementItem>>             GetStockMovementAsync(StockMovementFilter filter)                         => _repo.GetStockMovementAsync(filter);
    public Task<List<StockLedgerItem>>               GetStockLedgerAsync(StockLedgerFilter filter)                             => _repo.GetStockLedgerAsync(filter);
    public Task<List<MaterialReturnReportItem>>      GetMaterialReturnReportAsync(MaterialReturnReportFilter filter)           => _repo.GetMaterialReturnReportAsync(filter);
    public Task<List<WastageReportItem>>             GetWastageReportAsync(WastageReportFilter filter)                         => _repo.GetWastageReportAsync(filter);
    public Task<List<ReservedStockItem>>             GetReservedStockAsync(ReservedStockFilter filter)                         => _repo.GetReservedStockAsync(filter);
}

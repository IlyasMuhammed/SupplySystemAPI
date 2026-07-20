using SMS.Modules.Reports.Models;
using SMS.Shared.Pagination;

namespace SMS.Modules.Reports.Services;

public interface IReportsService
{
    Task<KpiDashboardModel>                          GetKpiDashboardAsync();
    Task<List<SupplierPerformanceItem>>              GetSupplierPerformanceAsync(ReportDateFilter filter);
    Task<List<PoSummaryItem>>                        GetPoSummaryAsync(ReportDateFilter filter);
    Task<List<SpendBySupplierItem>>                  GetSpendBySupplierAsync(ReportDateFilter filter);
    Task<List<PendingApprovalItem>>                  GetPendingApprovalsAsync();
    Task<List<StockLevelItem>>                       GetStockLevelsAsync(ReportDateFilter filter);
    Task<StockLevelSummaryReport>                    GetStockLevelSummaryAsync(StockLevelSummaryFilter filter);
    Task<List<PrFulfillmentItem>>                    GetPrFulfillmentStatusAsync(PrFulfillmentFilter filter);
    Task<PrPipelineReport>                           GetPrToPoPipelineAsync(PrPipelineFilter filter);
    Task<List<ReorderAlertItem>>                     GetReorderAlertsAsync();
    Task<List<InventoryValuationItem>>               GetInventoryValuationAsync();
    Task<List<GrnVarianceItem>>                      GetGrnVarianceAsync(ReportDateFilter filter);
    Task<List<ShipmentTrackerItem>>                  GetShipmentTrackerAsync(string? status);
    Task<(List<InvoiceAgingItem> Items, List<InvoiceAgingBucketSummary> Buckets)> GetInvoiceAgingAsync();
    Task<PaymentSummaryModel>                        GetPaymentSummaryAsync(ReportDateFilter filter);
    Task<SupplierLedgerSummaryReport>                GetSupplierLedgerSummaryAsync(SupplierLedgerSummaryFilter filter);
    Task<SupplierOrdersReport>                       GetSupplierOrdersAsync(SupplierOrdersFilter filter);
    Task<SupplierComparisonResponse>                 GetSupplierComparisonAsync(List<Guid> supplierIds);
    Task<GrnQualityAnalysisResponse>                 GetGrnQualityAnalysisAsync(GrnQualityAnalysisFilter filter);
    Task<SupplierSpendAnalysisResponse>              GetSupplierSpendAnalysisAsync(SupplierSpendFilter filter);
    Task<DeliveryHeatmapResponse?>                   GetDeliveryPerformanceHeatmapAsync(Guid supplierId, int year);
    Task<List<BudgetUtilizationItem>>                GetBudgetUtilizationAsync(ReportDateFilter filter);
    Task<PaginatedResponse<AuditLogItemModel>>       GetAuditTrailAsync(AuditLogFilter filter);
    Task<List<UserActivityItem>>                     GetUserActivityAsync(ReportDateFilter filter);

    // ── Material Reports ──────────────────────────────────────────────────────
    Task<List<MaterialIssueRegisterItem>>     GetMaterialIssueRegisterAsync(MaterialIssueRegisterFilter filter);
    Task<List<MaterialConsumptionReportItem>> GetMaterialConsumptionReportAsync(MaterialConsumptionReportFilter filter);
    Task<List<ProjectConsumptionItem>>        GetProjectConsumptionAsync(ProjectConsumptionFilter filter);
    Task<List<DepartmentConsumptionItem>>     GetDepartmentConsumptionAsync(DepartmentConsumptionFilter filter);
    Task<List<StockMovementItem>>             GetStockMovementAsync(StockMovementFilter filter);
    Task<List<StockLedgerItem>>               GetStockLedgerAsync(StockLedgerFilter filter);
    Task<List<MaterialReturnReportItem>>      GetMaterialReturnReportAsync(MaterialReturnReportFilter filter);
    Task<List<WastageReportItem>>             GetWastageReportAsync(WastageReportFilter filter);
    Task<List<ReservedStockItem>>             GetReservedStockAsync(ReservedStockFilter filter);
}

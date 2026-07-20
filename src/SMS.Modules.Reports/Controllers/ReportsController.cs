using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Reports.Models;
using SMS.Modules.Reports.Services;
using SMS.Modules.Reports.Services.Exports;
using SMS.Shared.Authorization;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Reports.Controllers;

[ApiController]
[Route("api/reports")]
public class ReportsController : ControllerBase
{
    private readonly IReportsService _svc;
    public ReportsController(IReportsService svc) => _svc = svc;

    // ── KPI Dashboard ─────────────────────────────────────────────────────────

    [HttpGet("kpis")]
    public async Task<IActionResult> GetKpis()
    {
        var result = await _svc.GetKpiDashboardAsync();
        return Ok(ApiResponse<KpiDashboardModel>.Ok(result));
    }

    // ── Procurement ───────────────────────────────────────────────────────────

    [HttpGet("supplier-performance")]
    public async Task<IActionResult> GetSupplierPerformance([FromQuery] ReportDateFilter filter)
    {
        var result = await _svc.GetSupplierPerformanceAsync(filter);
        return Ok(ApiResponse<List<SupplierPerformanceItem>>.Ok(result));
    }

    [HttpGet("po-summary")]
    public async Task<IActionResult> GetPoSummary([FromQuery] ReportDateFilter filter)
    {
        var result = await _svc.GetPoSummaryAsync(filter);
        return Ok(ApiResponse<List<PoSummaryItem>>.Ok(result));
    }

    [HttpGet("spend-by-supplier")]
    public async Task<IActionResult> GetSpendBySupplier([FromQuery] ReportDateFilter filter)
    {
        var result = await _svc.GetSpendBySupplierAsync(filter);
        return Ok(ApiResponse<List<SpendBySupplierItem>>.Ok(result));
    }

    [HttpGet("pending-approvals")]
    public async Task<IActionResult> GetPendingApprovals()
    {
        var result = await _svc.GetPendingApprovalsAsync();
        return Ok(ApiResponse<List<PendingApprovalItem>>.Ok(result));
    }

    [HttpGet("pr-fulfillment-status")]
    public async Task<IActionResult> GetPrFulfillmentStatus([FromQuery] PrFulfillmentFilter filter)
    {
        var result = await _svc.GetPrFulfillmentStatusAsync(filter);
        return Ok(ApiResponse<List<PrFulfillmentItem>>.Ok(result));
    }

    [HttpGet("pr-to-po-pipeline")]
    public async Task<IActionResult> GetPrToPoPipeline([FromQuery] PrPipelineFilter filter)
    {
        var result = await _svc.GetPrToPoPipelineAsync(filter);
        return Ok(ApiResponse<PrPipelineReport>.Ok(result));
    }

    // ── Inventory ─────────────────────────────────────────────────────────────

    [HttpGet("stock-levels")]
    public async Task<IActionResult> GetStockLevels([FromQuery] ReportDateFilter filter)
    {
        var result = await _svc.GetStockLevelsAsync(filter);
        return Ok(ApiResponse<List<StockLevelItem>>.Ok(result));
    }

    [HttpGet("stock-level-summary")]
    public async Task<IActionResult> GetStockLevelSummary([FromQuery] StockLevelSummaryFilter filter)
    {
        var result = await _svc.GetStockLevelSummaryAsync(filter);
        return Ok(ApiResponse<StockLevelSummaryReport>.Ok(result));
    }

    [HttpGet("reorder-alerts")]
    public async Task<IActionResult> GetReorderAlerts()
    {
        var result = await _svc.GetReorderAlertsAsync();
        return Ok(ApiResponse<List<ReorderAlertItem>>.Ok(result));
    }

    [HttpGet("inventory-valuation")]
    public async Task<IActionResult> GetInventoryValuation()
    {
        var result = await _svc.GetInventoryValuationAsync();
        return Ok(ApiResponse<List<InventoryValuationItem>>.Ok(result));
    }

    // ── Logistics ─────────────────────────────────────────────────────────────

    [HttpGet("grn-variance")]
    public async Task<IActionResult> GetGrnVariance([FromQuery] ReportDateFilter filter)
    {
        var result = await _svc.GetGrnVarianceAsync(filter);
        return Ok(ApiResponse<List<GrnVarianceItem>>.Ok(result));
    }

    [HttpGet("shipment-tracker")]
    public async Task<IActionResult> GetShipmentTracker([FromQuery] string? status)
    {
        var result = await _svc.GetShipmentTrackerAsync(status);
        return Ok(ApiResponse<List<ShipmentTrackerItem>>.Ok(result));
    }

    // ── Finance ───────────────────────────────────────────────────────────────

    [HttpGet("invoice-aging")]
    public async Task<IActionResult> GetInvoiceAging()
    {
        var (items, buckets) = await _svc.GetInvoiceAgingAsync();
        return Ok(ApiResponse<object>.Ok(new { items, buckets }));
    }

    [HttpGet("payment-summary")]
    public async Task<IActionResult> GetPaymentSummary([FromQuery] ReportDateFilter filter)
    {
        var result = await _svc.GetPaymentSummaryAsync(filter);
        return Ok(ApiResponse<PaymentSummaryModel>.Ok(result));
    }

    // ── Supplier Ledger Summary (SC-001) ────────────────────────────────────────

    [HttpGet("supplier-ledger-summary")]
    [RequirePermission(PermissionCodes.REPORT_VIEW)]
    public async Task<IActionResult> GetSupplierLedgerSummary([FromQuery] SupplierLedgerSummaryFilter filter)
    {
        var result = await _svc.GetSupplierLedgerSummaryAsync(filter);
        return Ok(ApiResponse<SupplierLedgerSummaryReport>.Ok(result));
    }

    [HttpGet("supplier-ledger-summary/pdf")]
    [RequirePermission(PermissionCodes.REPORT_EXPORT)]
    public async Task<IActionResult> ExportSupplierLedgerSummaryPdf([FromQuery] SupplierLedgerSummaryFilter filter)
    {
        var result = await _svc.GetSupplierLedgerSummaryAsync(filter);
        var bytes  = SupplierLedgerSummaryPdfExporter.Export(result);
        return File(bytes, "application/pdf", $"supplier-ledger-summary-{DateTime.UtcNow:yyyyMMdd}.pdf");
    }

    [HttpGet("supplier-ledger-summary/excel")]
    [RequirePermission(PermissionCodes.REPORT_EXPORT)]
    public async Task<IActionResult> ExportSupplierLedgerSummaryExcel([FromQuery] SupplierLedgerSummaryFilter filter)
    {
        var result = await _svc.GetSupplierLedgerSummaryAsync(filter);
        var bytes  = SupplierLedgerSummaryExcelExporter.Export(result);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"supplier-ledger-summary-{DateTime.UtcNow:yyyyMMdd}.xlsx");
    }

    // ── Supplier Orders Report (SC-002, FSD Addendum 23) ────────────────────────

    [HttpGet("supplier-orders")]
    [RequirePermission(PermissionCodes.REPORT_VIEW)]
    public async Task<IActionResult> GetSupplierOrders([FromQuery] SupplierOrdersFilter filter)
    {
        var result = await _svc.GetSupplierOrdersAsync(filter);
        return Ok(ApiResponse<SupplierOrdersReport>.Ok(result));
    }

    // ── Supplier Comparison (SC-008) ────────────────────────────────────────────

    [HttpGet("supplier-comparison")]
    [RequirePermission(PermissionCodes.REPORT_VIEW)]
    public async Task<IActionResult> GetSupplierComparison([FromQuery] string ids)
    {
        var supplierIds = new List<Guid>();
        foreach (var raw in (ids ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Guid.TryParse(raw, out var id))
                return BadRequest(ApiResponse.Fail($"'{raw}' is not a valid supplier ID."));
            supplierIds.Add(id);
        }

        var result = await _svc.GetSupplierComparisonAsync(supplierIds);
        return Ok(ApiResponse<SupplierComparisonResponse>.Ok(result));
    }

    // ── GRN Quality Analysis (SC-008) ───────────────────────────────────────────

    [HttpGet("grn-quality-analysis")]
    [RequirePermission(PermissionCodes.REPORT_VIEW)]
    public async Task<IActionResult> GetGrnQualityAnalysis([FromQuery] GrnQualityAnalysisFilter filter)
    {
        var result = await _svc.GetGrnQualityAnalysisAsync(filter);
        return Ok(ApiResponse<GrnQualityAnalysisResponse>.Ok(result));
    }

    // ── Supplier Spend Analysis (SC-008) ────────────────────────────────────────

    [HttpGet("supplier-spend-analysis")]
    [RequirePermission(PermissionCodes.REPORT_VIEW)]
    public async Task<IActionResult> GetSupplierSpendAnalysis([FromQuery] SupplierSpendFilter filter)
    {
        var result = await _svc.GetSupplierSpendAnalysisAsync(filter);
        return Ok(ApiResponse<SupplierSpendAnalysisResponse>.Ok(result));
    }

    // ── Delivery Performance Heatmap (SC-008) ───────────────────────────────────

    [HttpGet("delivery-performance-heatmap")]
    [RequirePermission(PermissionCodes.REPORT_VIEW)]
    public async Task<IActionResult> GetDeliveryPerformanceHeatmap([FromQuery] Guid supplierId, [FromQuery] int year)
    {
        var result = await _svc.GetDeliveryPerformanceHeatmapAsync(supplierId, year);
        return result is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<DeliveryHeatmapResponse>.Ok(result));
    }

    [HttpGet("budget-utilization")]
    public async Task<IActionResult> GetBudgetUtilization([FromQuery] ReportDateFilter filter)
    {
        var result = await _svc.GetBudgetUtilizationAsync(filter);
        return Ok(ApiResponse<List<BudgetUtilizationItem>>.Ok(result));
    }

    // ── Audit & Activity ─────────────────────────────────────────────────────

    [HttpGet("audit-trail")]
    public async Task<IActionResult> GetAuditTrail([FromQuery] AuditLogFilter filter)
    {
        var result = await _svc.GetAuditTrailAsync(filter);
        return Ok(ApiResponse<object>.Ok(result));
    }

    [HttpGet("user-activity")]
    public async Task<IActionResult> GetUserActivity([FromQuery] ReportDateFilter filter)
    {
        var result = await _svc.GetUserActivityAsync(filter);
        return Ok(ApiResponse<List<UserActivityItem>>.Ok(result));
    }

    // ── Material Reports ──────────────────────────────────────────────────────

    [HttpGet("material-issue-register")]
    [RequirePermission(PermissionCodes.REPORT_VIEW)]
    public async Task<IActionResult> GetMaterialIssueRegister([FromQuery] MaterialIssueRegisterFilter filter)
    {
        var result = await _svc.GetMaterialIssueRegisterAsync(filter);
        return Ok(ApiResponse<List<MaterialIssueRegisterItem>>.Ok(result));
    }

    [HttpGet("material-consumption")]
    [RequirePermission(PermissionCodes.REPORT_VIEW)]
    public async Task<IActionResult> GetMaterialConsumptionReport([FromQuery] MaterialConsumptionReportFilter filter)
    {
        var result = await _svc.GetMaterialConsumptionReportAsync(filter);
        return Ok(ApiResponse<List<MaterialConsumptionReportItem>>.Ok(result));
    }

    [HttpGet("project-consumption")]
    [RequirePermission(PermissionCodes.REPORT_VIEW)]
    public async Task<IActionResult> GetProjectConsumption([FromQuery] ProjectConsumptionFilter filter)
    {
        var result = await _svc.GetProjectConsumptionAsync(filter);
        return Ok(ApiResponse<List<ProjectConsumptionItem>>.Ok(result));
    }

    [HttpGet("department-consumption")]
    [RequirePermission(PermissionCodes.REPORT_VIEW)]
    public async Task<IActionResult> GetDepartmentConsumption([FromQuery] DepartmentConsumptionFilter filter)
    {
        var result = await _svc.GetDepartmentConsumptionAsync(filter);
        return Ok(ApiResponse<List<DepartmentConsumptionItem>>.Ok(result));
    }

    [HttpGet("stock-movement")]
    [RequirePermission(PermissionCodes.INVENTORY_VIEW)]
    public async Task<IActionResult> GetStockMovement([FromQuery] StockMovementFilter filter)
    {
        var result = await _svc.GetStockMovementAsync(filter);
        return Ok(ApiResponse<List<StockMovementItem>>.Ok(result));
    }

    [HttpGet("stock-ledger")]
    [RequirePermission(PermissionCodes.INVENTORY_VIEW)]
    public async Task<IActionResult> GetStockLedger([FromQuery] StockLedgerFilter filter)
    {
        var result = await _svc.GetStockLedgerAsync(filter);
        return Ok(ApiResponse<List<StockLedgerItem>>.Ok(result));
    }

    [HttpGet("material-return")]
    [RequirePermission(PermissionCodes.REPORT_VIEW)]
    public async Task<IActionResult> GetMaterialReturnReport([FromQuery] MaterialReturnReportFilter filter)
    {
        var result = await _svc.GetMaterialReturnReportAsync(filter);
        return Ok(ApiResponse<List<MaterialReturnReportItem>>.Ok(result));
    }

    [HttpGet("material-wastage")]
    [RequirePermission(PermissionCodes.REPORT_VIEW)]
    public async Task<IActionResult> GetWastageReport([FromQuery] WastageReportFilter filter)
    {
        var result = await _svc.GetWastageReportAsync(filter);
        return Ok(ApiResponse<List<WastageReportItem>>.Ok(result));
    }

    [HttpGet("reserved-stock")]
    [RequirePermission(PermissionCodes.STOCK_MANAGE)]
    public async Task<IActionResult> GetReservedStock([FromQuery] ReservedStockFilter filter)
    {
        var result = await _svc.GetReservedStockAsync(filter);
        return Ok(ApiResponse<List<ReservedStockItem>>.Ok(result));
    }
}

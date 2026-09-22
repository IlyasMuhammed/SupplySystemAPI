using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Reports.Models;
using SMS.Modules.Reports.Services;
using SMS.Modules.Reports.Services.Exports;
using SMS.Shared.Authorization;
using SMS.Shared.Pagination;

namespace SMS.Modules.Reports.Controllers;

// A29-P9-05 §15 R7 — the margin sale orders are expected to make, under the same GET /api/reports/sales/{name}
// as the other sales reports, with a PDF and an Excel form.
//
// Two permissions on every endpoint, as with the others: REPORT_VIEW (REPORT_EXPORT for the documents) to be in
// the reports at all, and SALE_ORDER_VIEW, the one that opens the same orders elsewhere. The purchase price a
// margin is worked from is already shown, as the line's margin, to anyone who can read the order.

/// <summary>Margin analysis by product, customer or sale order.</summary>
[ApiController]
[Route("api/reports/sales")]
[RequiresFeature("MODULE_REPORTS")]
public class MarginAnalysisReportsController : ControllerBase
{
    private const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private readonly IMarginAnalysisReportService _svc;

    public MarginAnalysisReportsController(IMarginAnalysisReportService svc) => _svc = svc;

    /// <summary>
    /// Selling price against purchase price and the margin between them, by product, customer or sale order,
    /// highest margin first, per currency: a page of the rows and the totals of all of them. Only order lines
    /// with a purchase order behind them are costed; the totals count the rest. Filters:
    /// <c>dateFrom</c>/<c>dateTo</c> (the order date, inclusive whole days) and <c>groupBy</c> (PRODUCT,
    /// CUSTOMER or ORDER; PRODUCT when left out).
    /// </summary>
    [HttpGet("margin-analysis")]
    [RequirePermission(PermissionCodes.REPORT_VIEW)]
    [RequirePermission(PermissionCodes.SALE_ORDER_VIEW)]
    public async Task<IActionResult> GetMarginAnalysis([FromQuery] MarginAnalysisFilter filter)
    {
        var result = await _svc.GetMarginAnalysisAsync(filter);
        return Ok(ApiResponse<MarginAnalysisReport>.Ok(result));
    }

    /// <summary>The report as a PDF. Paging is ignored: every row is printed.</summary>
    [HttpGet("margin-analysis/pdf")]
    [RequirePermission(PermissionCodes.REPORT_EXPORT)]
    [RequirePermission(PermissionCodes.SALE_ORDER_VIEW)]
    public async Task<IActionResult> ExportMarginAnalysisPdf([FromQuery] MarginAnalysisFilter filter)
    {
        var result = await _svc.GetMarginAnalysisForExportAsync(filter);
        var bytes  = MarginAnalysisPdfExporter.Export(result);
        return File(bytes, "application/pdf", $"margin-analysis-{result.GeneratedAt:yyyyMMdd}.pdf");
    }

    /// <summary>The report as a workbook. Paging is ignored: every row is listed.</summary>
    [HttpGet("margin-analysis/excel")]
    [RequirePermission(PermissionCodes.REPORT_EXPORT)]
    [RequirePermission(PermissionCodes.SALE_ORDER_VIEW)]
    public async Task<IActionResult> ExportMarginAnalysisExcel([FromQuery] MarginAnalysisFilter filter)
    {
        var result = await _svc.GetMarginAnalysisForExportAsync(filter);
        var bytes  = MarginAnalysisExcelExporter.Export(result);
        return File(bytes, ExcelContentType, $"margin-analysis-{result.GeneratedAt:yyyyMMdd}.xlsx");
    }
}

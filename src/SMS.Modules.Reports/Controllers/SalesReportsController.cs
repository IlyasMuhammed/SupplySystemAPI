using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Reports.Models;
using SMS.Modules.Reports.Services;
using SMS.Modules.Reports.Services.Exports;
using SMS.Shared.Authorization;
using SMS.Shared.Pagination;

namespace SMS.Modules.Reports.Controllers;

// A29-P9-01 §15 — the sales reports, GET /api/reports/sales/{name}, each with a PDF and an Excel form.
//
// Every endpoint needs two permissions, not one: REPORT_VIEW (REPORT_EXPORT for the documents) to be
// in the reports at all, and SALE_ORDER_VIEW because what is in them is the sale orders. The register
// must not hand out through the reports what the sale order list withholds.

/// <summary>The sales reports.</summary>
[ApiController]
[Route("api/reports/sales")]
[RequiresFeature("MODULE_REPORTS")]
public class SalesReportsController : ControllerBase
{
    private const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private readonly ISalesReportService _svc;

    public SalesReportsController(ISalesReportService svc) => _svc = svc;

    // ── R1 Sales Order Register ──────────────────────────────────────────────

    /// <summary>
    /// Every sale order the filter matches, newest first, one page of them, with what all of them come to
    /// per currency. Filters: <c>dateFrom</c>/<c>dateTo</c> (inclusive, whole days), <c>status</c>,
    /// <c>partnerId</c> (the customer) and <c>deliveryMode</c>.
    /// </summary>
    [HttpGet("order-register")]
    [RequirePermission(PermissionCodes.REPORT_VIEW)]
    [RequirePermission(PermissionCodes.SALE_ORDER_VIEW)]
    public async Task<IActionResult> GetOrderRegister([FromQuery] SalesOrderRegisterFilter filter)
    {
        var result = await _svc.GetOrderRegisterAsync(filter);
        return Ok(ApiResponse<SalesOrderRegisterReport>.Ok(result));
    }

    /// <summary>The register as a PDF. Paging is ignored: every matching order is printed.</summary>
    [HttpGet("order-register/pdf")]
    [RequirePermission(PermissionCodes.REPORT_EXPORT)]
    [RequirePermission(PermissionCodes.SALE_ORDER_VIEW)]
    public async Task<IActionResult> ExportOrderRegisterPdf([FromQuery] SalesOrderRegisterFilter filter)
    {
        var result = await _svc.GetOrderRegisterForExportAsync(filter);
        var bytes  = SalesOrderRegisterPdfExporter.Export(result);
        return File(bytes, "application/pdf", $"sales-order-register-{result.GeneratedAt:yyyyMMdd}.pdf");
    }

    /// <summary>The register as a workbook. Paging is ignored: every matching order is listed.</summary>
    [HttpGet("order-register/excel")]
    [RequirePermission(PermissionCodes.REPORT_EXPORT)]
    [RequirePermission(PermissionCodes.SALE_ORDER_VIEW)]
    public async Task<IActionResult> ExportOrderRegisterExcel([FromQuery] SalesOrderRegisterFilter filter)
    {
        var result = await _svc.GetOrderRegisterForExportAsync(filter);
        var bytes  = SalesOrderRegisterExcelExporter.Export(result);
        return File(bytes, ExcelContentType, $"sales-order-register-{result.GeneratedAt:yyyyMMdd}.xlsx");
    }
}

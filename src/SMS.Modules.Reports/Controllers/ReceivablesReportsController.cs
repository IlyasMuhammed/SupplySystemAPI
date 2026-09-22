using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Reports.Models;
using SMS.Modules.Reports.Services;
using SMS.Modules.Reports.Services.Exports;
using SMS.Shared.Authorization;
using SMS.Shared.Pagination;

namespace SMS.Modules.Reports.Controllers;

// A29-P9-02 / P9-03 §15 — R2 the customer ledger and R3 aging receivables, under the same
// GET /api/reports/sales/{name} as R1, each with a PDF and an Excel form.
//
// As with the sales order register, every endpoint needs two permissions: REPORT_VIEW (REPORT_EXPORT for
// the documents) to be in the reports at all, and the one that opens the same books elsewhere —
// CUSTOMER_LEDGER_VIEW for the ledger, SALES_INVOICE_VIEW for the invoices aging is made of. A report must
// not hand out what the endpoint behind it withholds.

/// <summary>The receivables reports.</summary>
[ApiController]
[Route("api/reports/sales")]
[RequiresFeature("MODULE_REPORTS")]
public class ReceivablesReportsController : ControllerBase
{
    private const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private readonly IReceivablesReportService _svc;

    public ReceivablesReportsController(IReceivablesReportService svc) => _svc = svc;

    // ── R2 Customer ledger ───────────────────────────────────────────────────

    /// <summary>
    /// One customer's account over a range of days: the balance when it began, a page of the entries
    /// oldest first with the balance after each, and the balance when it ended. <c>partnerId</c> (the
    /// customer) is required; <c>dateFrom</c> and <c>dateTo</c> are inclusive whole days and optional.
    /// </summary>
    [HttpGet("customer-ledger")]
    [RequirePermission(PermissionCodes.REPORT_VIEW)]
    [RequirePermission(PermissionCodes.CUSTOMER_LEDGER_VIEW)]
    public async Task<IActionResult> GetCustomerLedger([FromQuery] CustomerLedgerReportFilter filter)
    {
        var result = await _svc.GetCustomerLedgerAsync(filter);
        return Ok(ApiResponse<CustomerLedgerReport>.Ok(result));
    }

    /// <summary>The account as a PDF statement. Paging is ignored: every entry is printed.</summary>
    [HttpGet("customer-ledger/pdf")]
    [RequirePermission(PermissionCodes.REPORT_EXPORT)]
    [RequirePermission(PermissionCodes.CUSTOMER_LEDGER_VIEW)]
    public async Task<IActionResult> ExportCustomerLedgerPdf([FromQuery] CustomerLedgerReportFilter filter)
    {
        var result = await _svc.GetCustomerLedgerForExportAsync(filter);
        var bytes  = CustomerLedgerPdfExporter.Export(result);
        return File(bytes, "application/pdf", $"customer-ledger-{result.GeneratedAt:yyyyMMdd}.pdf");
    }

    /// <summary>The account as a workbook. Paging is ignored: every entry is listed.</summary>
    [HttpGet("customer-ledger/excel")]
    [RequirePermission(PermissionCodes.REPORT_EXPORT)]
    [RequirePermission(PermissionCodes.CUSTOMER_LEDGER_VIEW)]
    public async Task<IActionResult> ExportCustomerLedgerExcel([FromQuery] CustomerLedgerReportFilter filter)
    {
        var result = await _svc.GetCustomerLedgerForExportAsync(filter);
        var bytes  = CustomerLedgerExcelExporter.Export(result);
        return File(bytes, ExcelContentType, $"customer-ledger-{result.GeneratedAt:yyyyMMdd}.xlsx");
    }

    // ── R3 Aging receivables ─────────────────────────────────────────────────

    /// <summary>
    /// The invoices still owed as of a day, aged by days past their due date into 0-30, 31-60, 61-90 and
    /// 90+, added up per currency and per customer, with a page of the invoices themselves. Filters:
    /// <c>asOf</c> (a whole day, default today) and <c>partnerId</c> (the customer, default all).
    /// </summary>
    [HttpGet("aging-receivables")]
    [RequirePermission(PermissionCodes.REPORT_VIEW)]
    [RequirePermission(PermissionCodes.SALES_INVOICE_VIEW)]
    public async Task<IActionResult> GetAgingReceivables([FromQuery] AgingReceivablesFilter filter)
    {
        var result = await _svc.GetAgingReceivablesAsync(filter);
        return Ok(ApiResponse<AgingReceivablesReport>.Ok(result));
    }

    /// <summary>The aging as a PDF. Paging is ignored: every outstanding invoice is printed.</summary>
    [HttpGet("aging-receivables/pdf")]
    [RequirePermission(PermissionCodes.REPORT_EXPORT)]
    [RequirePermission(PermissionCodes.SALES_INVOICE_VIEW)]
    public async Task<IActionResult> ExportAgingReceivablesPdf([FromQuery] AgingReceivablesFilter filter)
    {
        var result = await _svc.GetAgingReceivablesForExportAsync(filter);
        var bytes  = AgingReceivablesPdfExporter.Export(result);
        return File(bytes, "application/pdf", $"aging-receivables-{result.Criteria.AsOf:yyyyMMdd}.pdf");
    }

    /// <summary>The aging as a workbook. Paging is ignored: every outstanding invoice is listed.</summary>
    [HttpGet("aging-receivables/excel")]
    [RequirePermission(PermissionCodes.REPORT_EXPORT)]
    [RequirePermission(PermissionCodes.SALES_INVOICE_VIEW)]
    public async Task<IActionResult> ExportAgingReceivablesExcel([FromQuery] AgingReceivablesFilter filter)
    {
        var result = await _svc.GetAgingReceivablesForExportAsync(filter);
        var bytes  = AgingReceivablesExcelExporter.Export(result);
        return File(bytes, ExcelContentType, $"aging-receivables-{result.Criteria.AsOf:yyyyMMdd}.xlsx");
    }
}

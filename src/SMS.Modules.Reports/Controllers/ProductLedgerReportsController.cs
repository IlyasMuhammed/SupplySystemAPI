using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Reports.Models;
using SMS.Modules.Reports.Services;
using SMS.Modules.Reports.Services.Exports;
using SMS.Shared.Authorization;
using SMS.Shared.Pagination;

namespace SMS.Modules.Reports.Controllers;

// A29-P9-06 §15 R9 product ledger and R10 product profitability, under the same GET /api/reports/sales/{name}
// as the other sales reports, each with a PDF and an Excel form.
//
// As with the others, every endpoint needs REPORT_VIEW (REPORT_EXPORT for the documents) to be in the reports at
// all, and the permissions that open the same books elsewhere. Cost and margin are among the most sensitive
// numbers a company has, so both need PRODUCT_LEDGER_VIEW, as the product ledger's own endpoints do; R10 also
// needs SALES_INVOICE_VIEW, because its revenue is the invoices'. P8-05's JSON endpoint for the same ranking,
// GET /api/reports/product-profitability, is unchanged.

/// <summary>Product ledger and product profitability.</summary>
[ApiController]
[Route("api/reports/sales")]
[RequiresFeature("MODULE_REPORTS")]
public class ProductLedgerReportsController : ControllerBase
{
    private const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private readonly IProductLedgerReportService _svc;

    public ProductLedgerReportsController(IProductLedgerReportService svc) => _svc = svc;

    // ── R9 Product ledger ────────────────────────────────────────────────────

    /// <summary>
    /// Every movement of one product's variants, oldest first, with the quantity and value the product held after
    /// each: a page of the movements, and where the product stood when the range began and ended. <c>productId</c>
    /// is required; <c>variantId</c> narrows it to one variant. Filters: <c>dateFrom</c>/<c>dateTo</c> (the day of
    /// the movement, inclusive whole days).
    /// </summary>
    [HttpGet("product-ledger")]
    [RequirePermission(PermissionCodes.REPORT_VIEW)]
    [RequirePermission(PermissionCodes.PRODUCT_LEDGER_VIEW)]
    public async Task<IActionResult> GetProductLedger([FromQuery] ProductLedgerReportFilter filter)
    {
        var result = await _svc.GetProductLedgerAsync(filter);
        return Ok(ApiResponse<ProductLedgerReport>.Ok(result));
    }

    /// <summary>The report as a PDF. Paging is ignored: every movement is printed.</summary>
    [HttpGet("product-ledger/pdf")]
    [RequirePermission(PermissionCodes.REPORT_EXPORT)]
    [RequirePermission(PermissionCodes.PRODUCT_LEDGER_VIEW)]
    public async Task<IActionResult> ExportProductLedgerPdf([FromQuery] ProductLedgerReportFilter filter)
    {
        var result = await _svc.GetProductLedgerForExportAsync(filter);
        var bytes  = ProductLedgerPdfExporter.Export(result);
        return File(bytes, "application/pdf", $"product-ledger-{result.GeneratedAt:yyyyMMdd}.pdf");
    }

    /// <summary>The report as a workbook. Paging is ignored: every movement is listed.</summary>
    [HttpGet("product-ledger/excel")]
    [RequirePermission(PermissionCodes.REPORT_EXPORT)]
    [RequirePermission(PermissionCodes.PRODUCT_LEDGER_VIEW)]
    public async Task<IActionResult> ExportProductLedgerExcel([FromQuery] ProductLedgerReportFilter filter)
    {
        var result = await _svc.GetProductLedgerForExportAsync(filter);
        var bytes  = ProductLedgerExcelExporter.Export(result);
        return File(bytes, ExcelContentType, $"product-ledger-{result.GeneratedAt:yyyyMMdd}.xlsx");
    }

    // ── R10 Product profitability ────────────────────────────────────────────

    /// <summary>
    /// Revenue against cost of goods sold by product, most profitable first and ranked within each currency: a
    /// page of the products and the totals of all of them. Filters: <c>dateFrom</c>/<c>dateTo</c> (the day each
    /// sale's invoice was issued, inclusive whole days).
    /// </summary>
    [HttpGet("product-profitability")]
    [RequirePermission(PermissionCodes.REPORT_VIEW)]
    [RequirePermission(PermissionCodes.PRODUCT_LEDGER_VIEW)]
    [RequirePermission(PermissionCodes.SALES_INVOICE_VIEW)]
    public async Task<IActionResult> GetProductProfitability([FromQuery] ProfitabilityReportFilter filter)
    {
        var result = await _svc.GetProductProfitabilityAsync(filter);
        return Ok(ApiResponse<ProfitabilityReport>.Ok(result));
    }

    /// <summary>The report as a PDF. Paging is ignored: every product is printed.</summary>
    [HttpGet("product-profitability/pdf")]
    [RequirePermission(PermissionCodes.REPORT_EXPORT)]
    [RequirePermission(PermissionCodes.PRODUCT_LEDGER_VIEW)]
    [RequirePermission(PermissionCodes.SALES_INVOICE_VIEW)]
    public async Task<IActionResult> ExportProductProfitabilityPdf([FromQuery] ProfitabilityReportFilter filter)
    {
        var result = await _svc.GetProductProfitabilityForExportAsync(filter);
        var bytes  = ProductProfitabilityPdfExporter.Export(result);
        return File(bytes, "application/pdf", $"product-profitability-{result.GeneratedAt:yyyyMMdd}.pdf");
    }

    /// <summary>The report as a workbook. Paging is ignored: every product is listed.</summary>
    [HttpGet("product-profitability/excel")]
    [RequirePermission(PermissionCodes.REPORT_EXPORT)]
    [RequirePermission(PermissionCodes.PRODUCT_LEDGER_VIEW)]
    [RequirePermission(PermissionCodes.SALES_INVOICE_VIEW)]
    public async Task<IActionResult> ExportProductProfitabilityExcel([FromQuery] ProfitabilityReportFilter filter)
    {
        var result = await _svc.GetProductProfitabilityForExportAsync(filter);
        var bytes  = ProductProfitabilityExcelExporter.Export(result);
        return File(bytes, ExcelContentType, $"product-profitability-{result.GeneratedAt:yyyyMMdd}.xlsx");
    }
}

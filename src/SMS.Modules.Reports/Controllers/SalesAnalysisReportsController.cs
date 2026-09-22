using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Reports.Models;
using SMS.Modules.Reports.Services;
using SMS.Modules.Reports.Services.Exports;
using SMS.Shared.Authorization;
using SMS.Shared.Pagination;

namespace SMS.Modules.Reports.Controllers;

// A29-P9-04 §15 — R4 sales by product, R5 sales by customer and R6 fulfilment status (and, from A29-P9-05,
// R8 sales vs purchase, which reads the same invoices), under the same
// GET /api/reports/sales/{name} as the other sales reports, each with a PDF and an Excel form.
//
// As with the others, every endpoint needs two permissions: REPORT_VIEW (REPORT_EXPORT for the documents)
// to be in the reports at all, and the one that opens the same books elsewhere — SALES_INVOICE_VIEW for the
// invoices R4 and R5 are made of, DELIVERY_VIEW for the deliveries R6 lists. A report must not hand out
// what the endpoint behind it withholds.

/// <summary>Sales by product and sales by customer.</summary>
[ApiController]
[Route("api/reports/sales")]
[RequiresFeature("MODULE_REPORTS")]
public class SalesAnalysisReportsController : ControllerBase
{
    private const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private readonly ISalesAnalysisReportService _svc;

    public SalesAnalysisReportsController(ISalesAnalysisReportService svc) => _svc = svc;

    // ── R4 Sales by product ──────────────────────────────────────────────────

    /// <summary>
    /// Units and revenue by product, highest revenue first, per currency: a page of the products and the
    /// totals of all of them. Filters: <c>dateFrom</c>/<c>dateTo</c> (the day each invoice was issued,
    /// inclusive whole days) and <c>partnerId</c> (one customer's purchases).
    /// </summary>
    [HttpGet("sales-by-product")]
    [RequirePermission(PermissionCodes.REPORT_VIEW)]
    [RequirePermission(PermissionCodes.SALES_INVOICE_VIEW)]
    public async Task<IActionResult> GetSalesByProduct([FromQuery] SalesByProductFilter filter)
    {
        var result = await _svc.GetSalesByProductAsync(filter);
        return Ok(ApiResponse<SalesByProductReport>.Ok(result));
    }

    /// <summary>The report as a PDF. Paging is ignored: every product is printed.</summary>
    [HttpGet("sales-by-product/pdf")]
    [RequirePermission(PermissionCodes.REPORT_EXPORT)]
    [RequirePermission(PermissionCodes.SALES_INVOICE_VIEW)]
    public async Task<IActionResult> ExportSalesByProductPdf([FromQuery] SalesByProductFilter filter)
    {
        var result = await _svc.GetSalesByProductForExportAsync(filter);
        var bytes  = SalesByProductPdfExporter.Export(result);
        return File(bytes, "application/pdf", $"sales-by-product-{result.GeneratedAt:yyyyMMdd}.pdf");
    }

    /// <summary>The report as a workbook. Paging is ignored: every product is listed.</summary>
    [HttpGet("sales-by-product/excel")]
    [RequirePermission(PermissionCodes.REPORT_EXPORT)]
    [RequirePermission(PermissionCodes.SALES_INVOICE_VIEW)]
    public async Task<IActionResult> ExportSalesByProductExcel([FromQuery] SalesByProductFilter filter)
    {
        var result = await _svc.GetSalesByProductForExportAsync(filter);
        var bytes  = SalesByProductExcelExporter.Export(result);
        return File(bytes, ExcelContentType, $"sales-by-product-{result.GeneratedAt:yyyyMMdd}.xlsx");
    }

    // ── R5 Sales by customer ─────────────────────────────────────────────────

    /// <summary>
    /// Sale orders, invoices, revenue and average order value by customer, highest revenue first, per
    /// currency: a page of the customers and the totals of all of them. Filters: <c>dateFrom</c>/<c>dateTo</c>
    /// (the day each invoice was issued, inclusive whole days).
    /// </summary>
    [HttpGet("sales-by-customer")]
    [RequirePermission(PermissionCodes.REPORT_VIEW)]
    [RequirePermission(PermissionCodes.SALES_INVOICE_VIEW)]
    public async Task<IActionResult> GetSalesByCustomer([FromQuery] SalesByCustomerFilter filter)
    {
        var result = await _svc.GetSalesByCustomerAsync(filter);
        return Ok(ApiResponse<SalesByCustomerReport>.Ok(result));
    }

    /// <summary>The report as a PDF. Paging is ignored: every customer is printed.</summary>
    [HttpGet("sales-by-customer/pdf")]
    [RequirePermission(PermissionCodes.REPORT_EXPORT)]
    [RequirePermission(PermissionCodes.SALES_INVOICE_VIEW)]
    public async Task<IActionResult> ExportSalesByCustomerPdf([FromQuery] SalesByCustomerFilter filter)
    {
        var result = await _svc.GetSalesByCustomerForExportAsync(filter);
        var bytes  = SalesByCustomerPdfExporter.Export(result);
        return File(bytes, "application/pdf", $"sales-by-customer-{result.GeneratedAt:yyyyMMdd}.pdf");
    }

    /// <summary>The report as a workbook. Paging is ignored: every customer is listed.</summary>
    [HttpGet("sales-by-customer/excel")]
    [RequirePermission(PermissionCodes.REPORT_EXPORT)]
    [RequirePermission(PermissionCodes.SALES_INVOICE_VIEW)]
    public async Task<IActionResult> ExportSalesByCustomerExcel([FromQuery] SalesByCustomerFilter filter)
    {
        var result = await _svc.GetSalesByCustomerForExportAsync(filter);
        var bytes  = SalesByCustomerExcelExporter.Export(result);
        return File(bytes, ExcelContentType, $"sales-by-customer-{result.GeneratedAt:yyyyMMdd}.xlsx");
    }

    // ── R8 Sales vs purchase ─────────────────────────────────────────────────
    // Revenue is the invoices' and cost of goods sold the product ledger's, so it needs the permission that
    // opens each: SALES_INVOICE_VIEW and PRODUCT_LEDGER_VIEW.

    /// <summary>
    /// Revenue, cost of goods sold and gross margin by period, oldest first, per currency: a page of the
    /// periods and the totals of all of them. Filters: <c>dateFrom</c>/<c>dateTo</c> (the day each invoice was
    /// issued, inclusive whole days) and <c>period</c> (DAY, WEEK or MONTH; MONTH when left out).
    /// </summary>
    [HttpGet("sales-vs-purchase")]
    [RequirePermission(PermissionCodes.REPORT_VIEW)]
    [RequirePermission(PermissionCodes.SALES_INVOICE_VIEW)]
    [RequirePermission(PermissionCodes.PRODUCT_LEDGER_VIEW)]
    public async Task<IActionResult> GetSalesVsPurchase([FromQuery] SalesVsPurchaseFilter filter)
    {
        var result = await _svc.GetSalesVsPurchaseAsync(filter);
        return Ok(ApiResponse<SalesVsPurchaseReport>.Ok(result));
    }

    /// <summary>The report as a PDF. Paging is ignored: every period is printed.</summary>
    [HttpGet("sales-vs-purchase/pdf")]
    [RequirePermission(PermissionCodes.REPORT_EXPORT)]
    [RequirePermission(PermissionCodes.SALES_INVOICE_VIEW)]
    [RequirePermission(PermissionCodes.PRODUCT_LEDGER_VIEW)]
    public async Task<IActionResult> ExportSalesVsPurchasePdf([FromQuery] SalesVsPurchaseFilter filter)
    {
        var result = await _svc.GetSalesVsPurchaseForExportAsync(filter);
        var bytes  = SalesVsPurchasePdfExporter.Export(result);
        return File(bytes, "application/pdf", $"sales-vs-purchase-{result.GeneratedAt:yyyyMMdd}.pdf");
    }

    /// <summary>The report as a workbook. Paging is ignored: every period is listed.</summary>
    [HttpGet("sales-vs-purchase/excel")]
    [RequirePermission(PermissionCodes.REPORT_EXPORT)]
    [RequirePermission(PermissionCodes.SALES_INVOICE_VIEW)]
    [RequirePermission(PermissionCodes.PRODUCT_LEDGER_VIEW)]
    public async Task<IActionResult> ExportSalesVsPurchaseExcel([FromQuery] SalesVsPurchaseFilter filter)
    {
        var result = await _svc.GetSalesVsPurchaseForExportAsync(filter);
        var bytes  = SalesVsPurchaseExcelExporter.Export(result);
        return File(bytes, ExcelContentType, $"sales-vs-purchase-{result.GeneratedAt:yyyyMMdd}.xlsx");
    }
}

/// <summary>Open fulfilments by status, warehouse and delivery mode.</summary>
[ApiController]
[Route("api/reports/sales")]
[RequiresFeature("MODULE_REPORTS")]
public class FulfilmentReportsController : ControllerBase
{
    private const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private readonly IFulfilmentReportService _svc;

    public FulfilmentReportsController(IFulfilmentReportService svc) => _svc = svc;

    // ── R6 Fulfilment status ─────────────────────────────────────────────────

    /// <summary>
    /// The sale-order deliveries not yet delivered, counted by status, warehouse and delivery mode, with a
    /// page of them oldest first. Filters: <c>status</c> (an open delivery status), <c>warehouseId</c> (the
    /// warehouse the goods leave from) and <c>deliveryMode</c> (SHIP or SELF_PICKUP).
    /// </summary>
    [HttpGet("fulfillment-status")]
    [RequirePermission(PermissionCodes.REPORT_VIEW)]
    [RequirePermission(PermissionCodes.DELIVERY_VIEW)]
    public async Task<IActionResult> GetFulfilmentStatus([FromQuery] FulfilmentStatusFilter filter)
    {
        var result = await _svc.GetFulfilmentStatusAsync(filter);
        return Ok(ApiResponse<FulfilmentStatusReport>.Ok(result));
    }

    /// <summary>The report as a PDF. Paging is ignored: every open delivery is printed.</summary>
    [HttpGet("fulfillment-status/pdf")]
    [RequirePermission(PermissionCodes.REPORT_EXPORT)]
    [RequirePermission(PermissionCodes.DELIVERY_VIEW)]
    public async Task<IActionResult> ExportFulfilmentStatusPdf([FromQuery] FulfilmentStatusFilter filter)
    {
        var result = await _svc.GetFulfilmentStatusForExportAsync(filter);
        var bytes  = FulfilmentStatusPdfExporter.Export(result);
        return File(bytes, "application/pdf", $"fulfillment-status-{result.GeneratedAt:yyyyMMdd}.pdf");
    }

    /// <summary>The report as a workbook. Paging is ignored: every open delivery is listed.</summary>
    [HttpGet("fulfillment-status/excel")]
    [RequirePermission(PermissionCodes.REPORT_EXPORT)]
    [RequirePermission(PermissionCodes.DELIVERY_VIEW)]
    public async Task<IActionResult> ExportFulfilmentStatusExcel([FromQuery] FulfilmentStatusFilter filter)
    {
        var result = await _svc.GetFulfilmentStatusForExportAsync(filter);
        var bytes  = FulfilmentStatusExcelExporter.Export(result);
        return File(bytes, ExcelContentType, $"fulfillment-status-{result.GeneratedAt:yyyyMMdd}.xlsx");
    }
}

using SMS.Modules.Reports.Models;

namespace SMS.Modules.Reports.Services;

/// <summary>The sales analysis reports of A29 §15 — R4 and R5 — each with a whole-document form for the exports.</summary>
public interface ISalesAnalysisReportService
{
    /// <summary>
    /// R4 — what was sold, product by product: units and revenue, highest revenue first, per currency. One
    /// page of the products; the totals are over all of them.
    /// </summary>
    Task<SalesByProductReport> GetSalesByProductAsync(SalesByProductFilter filter);

    /// <summary>
    /// The same report with <b>every</b> product in it. Refused, and asked to be narrowed, above
    /// <see cref="SalesAnalysisReportService.MaxRows"/> products.
    /// </summary>
    Task<SalesByProductReport> GetSalesByProductForExportAsync(SalesByProductFilter filter);

    /// <summary>
    /// R5 — what each customer bought: sale orders, invoices, revenue and average order value, highest
    /// revenue first, per currency. One page of the customers; the totals are over all of them.
    /// </summary>
    Task<SalesByCustomerReport> GetSalesByCustomerAsync(SalesByCustomerFilter filter);

    /// <summary>
    /// The same report with <b>every</b> customer in it. Refused, and asked to be narrowed, above
    /// <see cref="SalesAnalysisReportService.MaxRows"/> customers.
    /// </summary>
    Task<SalesByCustomerReport> GetSalesByCustomerForExportAsync(SalesByCustomerFilter filter);

    /// <summary>
    /// R8 — what the sales that stand billed against what the goods cost, by day, week or month: revenue, the
    /// cost of goods sold the product ledger booked, and the gross margin between them, per currency. One page of
    /// the periods; the totals are over all of them.
    /// </summary>
    Task<SalesVsPurchaseReport> GetSalesVsPurchaseAsync(SalesVsPurchaseFilter filter);

    /// <summary>
    /// The same report with <b>every</b> period in it. Refused, and asked to be narrowed, above
    /// <see cref="SalesAnalysisReportService.MaxRows"/> periods.
    /// </summary>
    Task<SalesVsPurchaseReport> GetSalesVsPurchaseForExportAsync(SalesVsPurchaseFilter filter);
}

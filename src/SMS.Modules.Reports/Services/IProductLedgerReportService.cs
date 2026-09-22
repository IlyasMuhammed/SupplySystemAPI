using SMS.Modules.Reports.Models;

namespace SMS.Modules.Reports.Services;

/// <summary>The product reports of A29 §15 — R9 and R10 — each with a whole-document form for the exports.</summary>
public interface IProductLedgerReportService
{
    /// <summary>
    /// R9 — every movement of one product's variants, oldest first, with the quantity and value the product held
    /// after each and where it stood when the range began and ended. One page of the movements; the summary is
    /// over all of them, and every page's running figures follow from the whole range and not from the page.
    /// </summary>
    Task<ProductLedgerReport> GetProductLedgerAsync(ProductLedgerReportFilter filter);

    /// <summary>
    /// The same report with <b>every</b> movement in it. Refused, and asked to be narrowed, above
    /// <see cref="ProductLedgerReportService.MaxRows"/> movements, on the page form as well.
    /// </summary>
    Task<ProductLedgerReport> GetProductLedgerForExportAsync(ProductLedgerReportFilter filter);

    /// <summary>
    /// R10 — what each product earned against what it cost, most profitable first, per currency: the ranking of
    /// A29-P8-05, with a rank and totals. One page of the products; the totals are over all of them.
    /// </summary>
    Task<ProfitabilityReport> GetProductProfitabilityAsync(ProfitabilityReportFilter filter);

    /// <summary>
    /// The same report with <b>every</b> product in it. Refused, and asked to be narrowed, above
    /// <see cref="ProductLedgerReportService.MaxRows"/> products.
    /// </summary>
    Task<ProfitabilityReport> GetProductProfitabilityForExportAsync(ProfitabilityReportFilter filter);
}

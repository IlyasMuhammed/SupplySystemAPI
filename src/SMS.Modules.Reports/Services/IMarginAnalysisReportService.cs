using SMS.Modules.Reports.Models;

namespace SMS.Modules.Reports.Services;

/// <summary>R7 of A29 §15 — the margin sale orders are expected to make — with a whole-document form for the exports.</summary>
public interface IMarginAnalysisReportService
{
    /// <summary>
    /// What the order lines that have a purchase order behind them sell for against what that purchase order
    /// costs, by product, customer or sale order, highest margin first, per currency. One page of the rows; the
    /// totals are over all of them.
    /// </summary>
    Task<MarginAnalysisReport> GetMarginAnalysisAsync(MarginAnalysisFilter filter);

    /// <summary>
    /// The same report with <b>every</b> row in it. Refused, and asked to be narrowed, above
    /// <see cref="MarginAnalysisReportService.MaxRows"/> rows.
    /// </summary>
    Task<MarginAnalysisReport> GetMarginAnalysisForExportAsync(MarginAnalysisFilter filter);
}

using SMS.Modules.Reports.Models;

namespace SMS.Modules.Reports.Services;

/// <summary>The fulfilment report of A29 §15 — R6.</summary>
public interface IFulfilmentReportService
{
    /// <summary>
    /// R6 — the sale-order deliveries not yet delivered, counted by status, warehouse and delivery mode,
    /// with one page of them, oldest first. The counts are over every delivery the filters match.
    /// </summary>
    Task<FulfilmentStatusReport> GetFulfilmentStatusAsync(FulfilmentStatusFilter filter);

    /// <summary>
    /// The same report with <b>every</b> open delivery in it. Refused, and asked to be narrowed, above
    /// <see cref="FulfilmentReportService.MaxRows"/> deliveries.
    /// </summary>
    Task<FulfilmentStatusReport> GetFulfilmentStatusForExportAsync(FulfilmentStatusFilter filter);
}

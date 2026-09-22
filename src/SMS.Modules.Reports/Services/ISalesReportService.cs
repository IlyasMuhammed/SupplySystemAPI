using SMS.Modules.Reports.Models;

namespace SMS.Modules.Reports.Services;

/// <summary>The sales reports of A29 §15, one method to a report.</summary>
public interface ISalesReportService
{
    /// <summary>
    /// R1 — every sale order the filter matches, newest first, with its customer, status, delivery mode
    /// and totals, one page of them, plus what all of them come to.
    /// </summary>
    Task<SalesOrderRegisterReport> GetOrderRegisterAsync(SalesOrderRegisterFilter filter);

    /// <summary>
    /// The same register with <b>every</b> matching order in it, for a document that is printed or opened
    /// whole. Refused, and asked to be narrowed, above <see cref="SalesReportService.MaxExportRows"/>
    /// orders, since a register that size is not one a person reads and building it in memory is not free.
    /// </summary>
    Task<SalesOrderRegisterReport> GetOrderRegisterForExportAsync(SalesOrderRegisterFilter filter);
}

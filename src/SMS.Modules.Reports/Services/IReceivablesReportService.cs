using SMS.Modules.Reports.Models;

namespace SMS.Modules.Reports.Services;

/// <summary>The receivables reports of A29 §15, one method to a report, each with a whole-document form for the exports.</summary>
public interface IReceivablesReportService
{
    /// <summary>
    /// R2 — one customer's account over a range of days: what they owed when it began, every entry in
    /// it oldest first with the balance after each, and what they owed when it ended. One page of the
    /// entries; the opening, movement and closing figures are over all of them.
    /// </summary>
    Task<CustomerLedgerReport> GetCustomerLedgerAsync(CustomerLedgerReportFilter filter);

    /// <summary>
    /// The same account with <b>every</b> entry in it, for a document that is printed or opened whole.
    /// Refused, and asked to be narrowed, above <see cref="ReceivablesReportService.MaxRows"/> entries.
    /// </summary>
    Task<CustomerLedgerReport> GetCustomerLedgerForExportAsync(CustomerLedgerReportFilter filter);

    /// <summary>
    /// R3 — the invoices still owed as of a day, each aged by how far past its due date it was, added up
    /// by bucket per currency and per customer. One page of the invoices; the totals are over all of them.
    /// </summary>
    Task<AgingReceivablesReport> GetAgingReceivablesAsync(AgingReceivablesFilter filter);

    /// <summary>
    /// The same report with <b>every</b> outstanding invoice in it. Refused, and asked to be narrowed to
    /// a customer, above <see cref="ReceivablesReportService.MaxRows"/> invoices.
    /// </summary>
    Task<AgingReceivablesReport> GetAgingReceivablesForExportAsync(AgingReceivablesFilter filter);
}

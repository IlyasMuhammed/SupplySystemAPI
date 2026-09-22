using SMS.Modules.Finance.Models;
using SMS.Shared.Pagination;
using SMS.WorkflowEngine.Models;

namespace SMS.Modules.Finance.Services;

/// <param name="AlreadyExisted">
/// True when the delivery already had a live invoice and this call returned it rather than raising a
/// second — the only guarantee that matters, because billing the same goods twice looks exactly as
/// legitimate as billing them once.
/// </param>
public sealed record SalesInvoiceCreated(
    Guid    InvoiceUuid,
    string  InvoiceNumber,
    decimal GrandTotal,
    string  CurrencyCode,
    bool    AlreadyExisted);

/// <param name="PartnerBalance">What the customer owes after this invoice — the ledger's running balance.</param>
public sealed record SalesInvoiceIssued(
    Guid    InvoiceUuid,
    string  InvoiceNumber,
    string  Status,
    decimal GrandTotal,
    decimal PartnerBalance);

/// <summary>
/// Bills customers for what was delivered (A29 §9.5).
/// <para>
/// One invoice per delivery, and only for a delivery that has reached the customer. An order that
/// goes out in three deliveries is invoiced three times — that is what "multiple invoices per SO
/// (partial invoicing)" means here — and a delivery can never be billed twice.
/// </para>
/// </summary>
public interface ISalesInvoiceService
{
    /// <summary>
    /// Raises a DRAFT invoice for a delivered sale-order delivery: one line per delivered line, priced
    /// from the order. Refused unless the delivery is DELIVERED (or CLOSED), belongs to a sale order
    /// that can be billed, and every line's invoiced quantity stays within what was delivered.
    /// Idempotent on the delivery: a second call returns the invoice already raised.
    /// </summary>
    Task<SalesInvoiceCreated> CreateFromFulfillmentAsync(Guid deliveryUuid, int userId);

    /// <summary>
    /// DRAFT → ISSUED. The customer's ledger is debited with the grand total <b>in the same
    /// transaction</b> as the status change (§9.5), the order's per-line invoiced quantities are
    /// brought up to date, and <c>SO_INVOICED</c> is recorded on the order's timeline.
    /// </summary>
    Task<SalesInvoiceIssued> IssueAsync(Guid invoiceUuid, int userId);

    /// <summary>One invoice with its lines and the payments applied to it, or <c>null</c> if there is none.</summary>
    Task<SalesInvoiceDetailModel?> GetAsync(Guid invoiceUuid);

    /// <summary>A page of invoices, newest first.</summary>
    Task<PaginatedResponse<SalesInvoiceListItemModel>> ListAsync(SalesInvoiceFilter filter);

    /// <summary>
    /// Changes a DRAFT's due date and notes. Nothing else about an invoice is editable: its lines and
    /// amounts are what was delivered at what the order priced it. Refused once issued.
    /// </summary>
    Task UpdateAsync(Guid invoiceUuid, UpdateSalesInvoiceRequest request, int userId);

    /// <summary>
    /// Deletes a DRAFT, which frees its delivery to be invoiced again. The invoice number is not
    /// reused. An issued invoice has a receivable booked against it and cannot be deleted.
    /// </summary>
    Task DeleteAsync(Guid invoiceUuid, int userId);
}

/// <summary>
/// Keeps an invoice's PDF on file as an attachment on the invoice (A29 §9.1) — the copy that shows what
/// the customer was actually sent, for whoever opens the invoice later. Stored in the database and read
/// back only through the authenticated attachment endpoint, gated by <c>SALES_INVOICE_VIEW</c>.
/// </summary>
public interface ISalesInvoiceDocumentArchive
{
    /// <summary>
    /// Files the PDF of an invoice that has just been issued. <b>Never throws</b>: the invoice is
    /// issued and the receivable booked whether or not this works, so a failure is logged and
    /// reported as <c>null</c>, and the PDF can be filed afterwards with <see cref="FilePdfAsync"/>.
    /// </summary>
    Task<Guid?> TryFileIssuedPdfAsync(Guid invoiceUuid, int userId);

    /// <summary>
    /// Files the invoice's PDF as it stands now — after a payment, say, or for an invoice issued
    /// before filing existed. Refused for a draft, which is not yet what a customer is sent. Each
    /// filing is kept: a later one does not replace an earlier one.
    /// </summary>
    Task<StoredAttachment> FilePdfAsync(Guid invoiceUuid, int userId);
}

/// <summary>Renders an invoice as a PDF for the customer (A29 §9.1).</summary>
public interface ISalesInvoiceDocumentService
{
    /// <summary>The invoice as a PDF, named after its number. Any invoice that exists can be printed; a draft is marked as one.</summary>
    Task<SalesInvoicePdf> GeneratePdfAsync(Guid invoiceUuid);
}

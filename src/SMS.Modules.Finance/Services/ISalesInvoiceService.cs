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
}

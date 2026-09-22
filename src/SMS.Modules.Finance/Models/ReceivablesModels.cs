using SMS.Modules.Finance.Services;

namespace SMS.Modules.Finance.Models;

// A29-P7-08 §9.1–§9.5 — what the sales-invoice and customer-payment endpoints accept and return.
// Entities stay internal to Finance; these are the shapes callers see.

// ── Sales invoices ───────────────────────────────────────────────────────────

/// <summary>Raise an invoice for a delivered delivery. Everything else — lines, prices, the customer, the trace — comes from the order.</summary>
public class CreateSalesInvoiceRequest
{
    public Guid DeliveryUuid { get; set; }
}

/// <summary>
/// The two things about a draft invoice a person may decide. Lines and amounts are not among them:
/// they are what was delivered, at what the order priced it, and changing them by hand would let an
/// invoice drift from the goods it bills.
/// </summary>
public class UpdateSalesInvoiceRequest
{
    public DateTime DueDate { get; set; }
    public string?  Notes   { get; set; }
}

/// <summary>
/// Which invoices to list. <c>DateFrom</c>/<c>DateTo</c> bound the invoice date in whole days,
/// inclusive at both ends. <c>Search</c> matches the invoice, order or delivery number or the
/// customer's name. <c>Status</c> is one of DRAFT, ISSUED, PARTIALLY_PAID, PAID, OVERDUE, CANCELLED,
/// CREDIT_NOTE.
/// </summary>
public class SalesInvoiceFilter
{
    public Guid?     PartnerId     { get; set; }
    public Guid?     SaleOrderUuid { get; set; }
    public string?   Status        { get; set; }
    public DateTime? DateFrom      { get; set; }
    public DateTime? DateTo        { get; set; }
    public string?   Search        { get; set; }
    public int       Page          { get; set; } = 1;
    /// <summary>Clamped to 1–100.</summary>
    public int       PageSize      { get; set; } = 20;
}

public class SalesInvoiceListItemModel
{
    public Guid     Uuid            { get; set; }
    public string   InvoiceNumber   { get; set; } = string.Empty;
    public Guid     SaleOrderUuid   { get; set; }
    public string   SaleOrderNumber { get; set; } = string.Empty;
    public Guid?    DeliveryUuid    { get; set; }
    public string?  DeliveryNumber  { get; set; }
    public Guid     PartnerId       { get; set; }
    public string   PartnerName     { get; set; } = string.Empty;
    public DateTime InvoiceDate     { get; set; }
    public DateTime DueDate         { get; set; }
    public decimal  GrandTotal      { get; set; }
    public decimal  AmountPaid      { get; set; }
    public decimal  BalanceDue      { get; set; }
    public string   Status          { get; set; } = string.Empty;
    public string   CurrencyCode    { get; set; } = string.Empty;
}

public class SalesInvoiceLineModel
{
    public int     LineNo          { get; set; }
    public Guid    SoLineUuid      { get; set; }
    public Guid    VariantUuid     { get; set; }
    public string  Description     { get; set; } = string.Empty;
    public decimal Quantity        { get; set; }
    public decimal UnitPrice       { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal TaxPercent      { get; set; }
    public decimal LineTotal       { get; set; }
}

/// <summary>One customer payment applied to an invoice (§9.4).</summary>
public class SalesInvoicePaymentModel
{
    public Guid     AllocationUuid  { get; set; }
    public Guid     PaymentUuid     { get; set; }
    public string   PaymentNumber   { get; set; } = string.Empty;
    public DateTime PaymentDate     { get; set; }
    public string   PaymentMethod   { get; set; } = string.Empty;
    public string   PaymentStatus   { get; set; } = string.Empty;
    public decimal  AllocatedAmount { get; set; }
    public DateTime AllocatedAt     { get; set; }
    public int      AllocatedBy     { get; set; }
}

public class SalesInvoiceDetailModel : SalesInvoiceListItemModel
{
    public Guid      TraceId        { get; set; }
    public decimal   Subtotal       { get; set; }
    public decimal   DiscountAmount { get; set; }
    public decimal   TaxAmount      { get; set; }
    public string?   Notes          { get; set; }
    public int       CreatedBy      { get; set; }
    public DateTime  CreatedDate    { get; set; }
    public int?      ModifiedBy     { get; set; }
    public DateTime? ModifiedDate   { get; set; }

    public List<SalesInvoiceLineModel>    Lines    { get; set; } = [];
    public List<SalesInvoicePaymentModel> Payments { get; set; } = [];
}

/// <summary>A rendered invoice, named the way a person would file it.</summary>
public sealed record SalesInvoicePdf(string FileName, byte[] Content);

// ── Customer payments ────────────────────────────────────────────────────────

/// <summary>
/// Money received from a customer. <c>Allocations</c> left out applies it FIFO, oldest unpaid invoice
/// first; a list — even an empty one — is applied exactly as written and whatever it does not name
/// stays on the customer's account.
/// </summary>
public class RecordCustomerPaymentRequest
{
    public Guid      PartnerId     { get; set; }
    public decimal   Amount        { get; set; }
    /// <summary>CASH, CHEQUE, BANK_TRANSFER, CARD or ONLINE.</summary>
    public string    Method        { get; set; } = string.Empty;
    /// <summary>The currency the money arrived in, as the Lookups catalog spells it.</summary>
    public string    CurrencyCode  { get; set; } = string.Empty;
    public DateTime? PaymentDate   { get; set; }
    /// <summary>Required for CHEQUE.</summary>
    public string?   ChequeNumber  { get; set; }
    public string?   BankReference { get; set; }
    public string?   Notes         { get; set; }
    public List<ManualPaymentAllocation>? Allocations { get; set; }
}

/// <summary>
/// Apply what is left of a received payment. Same rule as at recording: no <c>Allocations</c> means
/// FIFO; a list is applied as written.
/// </summary>
public class AllocateCustomerPaymentRequest
{
    public List<ManualPaymentAllocation>? Allocations { get; set; }
}

/// <summary>
/// <c>DateFrom</c>/<c>DateTo</c> bound the payment date in whole days, inclusive. <c>Unallocated</c>
/// keeps only payments with money still on account — the ones worth applying. <c>Search</c> matches
/// the payment number, cheque number, bank reference or the customer's name.
/// </summary>
public class CustomerPaymentFilter
{
    public Guid?     PartnerId   { get; set; }
    public string?   Status      { get; set; }
    public string?   Method      { get; set; }
    public DateTime? DateFrom    { get; set; }
    public DateTime? DateTo      { get; set; }
    public bool?     Unallocated { get; set; }
    public string?   Search      { get; set; }
    public int       Page        { get; set; } = 1;
    /// <summary>Clamped to 1–100.</summary>
    public int       PageSize    { get; set; } = 20;
}

public class CustomerPaymentListItemModel
{
    public Guid     Uuid              { get; set; }
    public string   PaymentNumber     { get; set; } = string.Empty;
    public Guid     PartnerId         { get; set; }
    public string   PartnerName       { get; set; } = string.Empty;
    public DateTime PaymentDate       { get; set; }
    public decimal  Amount            { get; set; }
    public decimal  AllocatedAmount   { get; set; }
    /// <summary>Received but not applied to any invoice — held on the customer's account.</summary>
    public decimal  UnallocatedAmount { get; set; }
    public string   PaymentMethod     { get; set; } = string.Empty;
    public string?  ChequeNumber      { get; set; }
    public string?  BankReference     { get; set; }
    public string   CurrencyCode      { get; set; } = string.Empty;
    public string   Status            { get; set; } = string.Empty;
}

/// <summary>One invoice a payment was applied to, with where that invoice stands now.</summary>
public class CustomerPaymentAllocationModel
{
    public Guid     AllocationUuid     { get; set; }
    public Guid     InvoiceUuid        { get; set; }
    public string   InvoiceNumber      { get; set; } = string.Empty;
    public decimal  AllocatedAmount    { get; set; }
    public DateTime AllocatedAt        { get; set; }
    public int      AllocatedBy        { get; set; }
    public decimal  InvoiceBalanceDue  { get; set; }
    public string   InvoiceStatus      { get; set; } = string.Empty;
}

public class CustomerPaymentDetailModel : CustomerPaymentListItemModel
{
    public string?   Notes        { get; set; }
    public int       CreatedBy    { get; set; }
    public DateTime  CreatedDate  { get; set; }
    public int?      ModifiedBy   { get; set; }
    public DateTime? ModifiedDate { get; set; }

    public List<CustomerPaymentAllocationModel> Allocations { get; set; } = [];
}

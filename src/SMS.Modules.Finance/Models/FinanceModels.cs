namespace SMS.Modules.Finance.Models;

// ── Invoice line models ───────────────────────────────────────────────────────

public class InvoiceLineRequest
{
    public Guid?   GrnLineUuid     { get; set; }   // optional: links to a GRN line (3-way match)
    public Guid    PoLineUuid      { get; set; }   // required: links to PO line for QtyInvoiced tracking
    public string  ItemDescription { get; set; } = string.Empty;
    public string? UnitOfMeasure   { get; set; }
    public decimal QtyInvoiced     { get; set; }
    public decimal UnitPrice       { get; set; }
}

public class InvoiceLineModel
{
    public Guid    UUID            { get; set; }
    public Guid?   GrnLineUuid    { get; set; }
    public Guid    PoLineUuid     { get; set; }
    public int     LineNo         { get; set; }
    public string  ItemDescription { get; set; } = string.Empty;
    public string? UnitOfMeasure  { get; set; }
    public decimal QtyInvoiced    { get; set; }
    public decimal UnitPrice      { get; set; }
    public decimal LineTotal      { get; set; }
}

// ── Invoice request models ────────────────────────────────────────────────────

public class CreateInvoiceRequest
{
    public string? SupplierInvoiceNo { get; set; }
    public Guid    SupplierId        { get; set; }
    public Guid    PoUuid            { get; set; }
    public Guid?   GrnUuid           { get; set; }
    public DateTime InvoiceDate      { get; set; }
    public DateTime ReceivedDate     { get; set; }
    public DateTime DueDate          { get; set; }
    public string  Currency          { get; set; } = "PKR";
    // When Lines are provided, Subtotal is computed from them; otherwise enter manually.
    public decimal Subtotal          { get; set; }
    public decimal TaxAmount         { get; set; }
    public string? PaymentMethod     { get; set; }
    public string? Notes             { get; set; }
    public string? AttachmentUrl     { get; set; }
    public List<InvoiceLineRequest>? Lines { get; set; }
}

public class PatchInvoiceRequest
{
    public string?   SupplierInvoiceNo { get; set; }
    public DateTime? DueDate           { get; set; }
    public string?   PaymentMethod     { get; set; }
    public decimal?  TaxAmount         { get; set; }
    public string?   MatchStatus       { get; set; }
    public string?   PaymentStatus     { get; set; }
    public string?   Notes             { get; set; }
    public string?   AttachmentUrl     { get; set; }
}

public class ApproveInvoiceRequest
{
    public string? Notes { get; set; }
}

public class RejectInvoiceRequest
{
    public string Reason { get; set; } = string.Empty;
}

// ── Invoice response models ───────────────────────────────────────────────────

public class InvoiceListItemModel
{
    public Guid     UUID              { get; set; }
    public Guid     TraceId           { get; set; }
    public string   InvoiceNumber     { get; set; } = string.Empty;
    public string?  SupplierInvoiceNo { get; set; }
    public string   SupplierName      { get; set; } = string.Empty;
    public string   PoNumber          { get; set; } = string.Empty;
    public string?  GrnNumber         { get; set; }
    public DateTime InvoiceDate       { get; set; }
    public DateTime DueDate           { get; set; }
    public decimal  TotalAmount       { get; set; }
    public string   Currency          { get; set; } = string.Empty;
    public string   MatchStatus       { get; set; } = string.Empty;
    public string   PaymentStatus     { get; set; } = string.Empty;
}

public class InvoiceDetailModel
{
    public Guid     UUID              { get; set; }
    public Guid     TraceId           { get; set; }
    public string   InvoiceNumber     { get; set; } = string.Empty;
    public string?  SupplierInvoiceNo { get; set; }
    public Guid     SupplierId        { get; set; }
    public string   SupplierName      { get; set; } = string.Empty;
    public Guid     PoUuid            { get; set; }
    public string   PoNumber          { get; set; } = string.Empty;
    public Guid?    GrnUuid           { get; set; }
    public string?  GrnNumber         { get; set; }
    public DateTime InvoiceDate       { get; set; }
    public DateTime ReceivedDate      { get; set; }
    public DateTime DueDate           { get; set; }
    public string   Currency          { get; set; } = string.Empty;
    public decimal  Subtotal          { get; set; }
    public decimal  TaxAmount         { get; set; }
    public decimal  TotalAmount       { get; set; }
    public decimal  MatchedPoValue    { get; set; }
    public decimal  MatchedGrnValue   { get; set; }
    public decimal  VarianceAmount    { get; set; }
    public string   MatchStatus       { get; set; } = string.Empty;
    public string   PaymentStatus     { get; set; } = string.Empty;
    public decimal  PaidAmount        { get; set; }
    public string?  PaymentMethod     { get; set; }
    public int?     ApprovedBy        { get; set; }
    public DateTime? ApprovedAt       { get; set; }
    public string?  Notes             { get; set; }
    public string?  AttachmentUrl     { get; set; }
    public int      CreatedBy         { get; set; }
    public DateTime CreatedDate       { get; set; }
    public List<InvoiceLineModel>     Lines    { get; set; } = new();
    public List<PaymentListItemModel> Payments { get; set; } = new();
}

public class InvoiceFilter
{
    public string?   MatchStatus   { get; set; }
    public string?   PaymentStatus { get; set; }
    public Guid?     SupplierId    { get; set; }
    public string?   Search        { get; set; }
    public DateTime? DateFrom      { get; set; }
    public DateTime? DateTo        { get; set; }
    public int       Page          { get; set; } = 1;
    public int       PageSize      { get; set; } = 20;
}

// ── Payment request models ────────────────────────────────────────────────────

public class CreatePaymentRequest
{
    public Guid     InvoiceUuid    { get; set; }
    public DateTime PaymentDate    { get; set; }
    public decimal  AmountPaid     { get; set; }
    public string   PaymentMethod  { get; set; } = string.Empty;
    public string?  BankReference  { get; set; }
    public string?  ChequeNumber   { get; set; }
    public string?  AccountDebited { get; set; }
    public string?  Notes          { get; set; }
}

public class PatchPaymentRequest
{
    public string?   Status         { get; set; }
    public string?   BankReference  { get; set; }
    public string?   ChequeNumber   { get; set; }
    public string?   Notes          { get; set; }
}

// ── Payment response models ───────────────────────────────────────────────────

public class PaymentListItemModel
{
    public Guid     UUID           { get; set; }
    public string   PaymentNumber  { get; set; } = string.Empty;
    public string   InvoiceNumber  { get; set; } = string.Empty;
    public string   SupplierName   { get; set; } = string.Empty;
    public DateTime PaymentDate    { get; set; }
    public decimal  AmountPaid     { get; set; }
    public string   PaymentMethod  { get; set; } = string.Empty;
    public string   Status         { get; set; } = string.Empty;
}

public class PaymentDetailModel
{
    public Guid     UUID           { get; set; }
    public string   PaymentNumber  { get; set; } = string.Empty;
    public Guid     InvoiceUuid    { get; set; }
    public string   InvoiceNumber  { get; set; } = string.Empty;
    public Guid     SupplierId     { get; set; }
    public string   SupplierName   { get; set; } = string.Empty;
    public DateTime PaymentDate    { get; set; }
    public decimal  AmountPaid     { get; set; }
    public string   PaymentMethod  { get; set; } = string.Empty;
    public string?  BankReference  { get; set; }
    public string?  ChequeNumber   { get; set; }
    public string?  AccountDebited { get; set; }
    public string   Status         { get; set; } = string.Empty;
    public string?  Notes          { get; set; }
    public DateTime ProcessedAt    { get; set; }
    public DateTime CreatedDate    { get; set; }
}

public class PaymentFilter
{
    public string? Status      { get; set; }
    public Guid?   SupplierId  { get; set; }
    public string? Search      { get; set; }
    public int     Page        { get; set; } = 1;
    public int     PageSize    { get; set; } = 20;
}

// ── Debit Note models ─────────────────────────────────────────────────────────

public class CreateDebitNoteRequest
{
    public Guid    SroId               { get; set; }
    public string  DebitReason         { get; set; } = string.Empty;
    public string? DebitReasonDetail   { get; set; }
    public decimal DebitAmount         { get; set; }
    public string? Notes               { get; set; }
}

public class UpdateDebitNoteStatusRequest
{
    // ACKNOWLEDGED | DISPUTED | SETTLED | WRITTEN_OFF
    public string  NewStatus    { get; set; } = string.Empty;
    public string? DisputeNotes { get; set; }
    public string? Notes        { get; set; }
}

public class DebitNoteListFilter
{
    public Guid?   SupplierId { get; set; }
    public string? Status     { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo   { get; set; }
    public int     Page       { get; set; } = 1;
    public int     PageSize   { get; set; } = 20;
}

public class DebitNoteListItemModel
{
    public Guid     UUID            { get; set; }
    public string   DebitNoteNumber { get; set; } = string.Empty;
    public string   SroNumber       { get; set; } = string.Empty;
    public Guid     SupplierId      { get; set; }
    public string   SupplierName    { get; set; } = string.Empty;
    public string   DebitReason     { get; set; } = string.Empty;
    public decimal  DebitAmount     { get; set; }
    public string   Status          { get; set; } = string.Empty;
    public DateTime? IssuedAt       { get; set; }
    public DateTime CreatedDate     { get; set; }
}

public class DebitNoteDetailModel
{
    public Guid     UUID                 { get; set; }
    public string   DebitNoteNumber      { get; set; } = string.Empty;
    public Guid     SroUuid              { get; set; }
    public string   SroNumber            { get; set; } = string.Empty;
    public Guid     SupplierId           { get; set; }
    public string   SupplierName         { get; set; } = string.Empty;
    public string?  SupplierContactEmail { get; set; }
    public string   DebitReason          { get; set; } = string.Empty;
    public string?  DebitReasonDetail    { get; set; }
    public decimal  DebitAmount          { get; set; }
    public string   Status               { get; set; } = string.Empty;
    public DateTime? IssuedAt            { get; set; }
    public DateTime? AcknowledgedAt      { get; set; }
    public DateTime? DisputedAt          { get; set; }
    public DateTime? SettledAt           { get; set; }
    public string?  DisputeNotes         { get; set; }
    public string?  Notes                { get; set; }
    public DateTime CreatedDate          { get; set; }
}

// ── Supplier Ledger models ────────────────────────────────────────────────────

public class SupplierLedgerEntryModel
{
    public Guid     Uuid            { get; set; }
    public Guid     SupplierId      { get; set; }
    public int      SequenceNo      { get; set; }
    public string   TransactionType { get; set; } = string.Empty;
    public string   ReferenceType   { get; set; } = string.Empty;
    public Guid     ReferenceId     { get; set; }
    public string   ReferenceNo     { get; set; } = string.Empty;
    public DateTime EntryDate       { get; set; }
    public decimal  DebitAmount     { get; set; }
    public decimal  CreditAmount    { get; set; }
    public decimal  BalanceAfter    { get; set; }
    public string?  Narration       { get; set; }
    public int      CreatedBy       { get; set; }
    public DateTime CreatedDate     { get; set; }
}

public class SupplierLedgerFilter
{
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo   { get; set; }
    public int        Page     { get; set; } = 1;
    public int        PageSize { get; set; } = 20;
}

public class SupplierBalanceSummary
{
    public Guid    SupplierId             { get; set; }
    public decimal TotalDebits            { get; set; }
    public decimal TotalCredits           { get; set; }
    public decimal NetBalance             { get; set; }
    public decimal AvailableAdvanceCredit { get; set; }
}

// ── Supplier Payment models (SFM-003) ─────────────────────────────────────────

public class CreateSupplierPaymentLineRequest
{
    public Guid    InvoiceUuid     { get; set; }
    public decimal AllocatedAmount { get; set; }
    public string? Notes           { get; set; }
}

public class CreateSupplierPaymentRequest
{
    public Guid     SupplierId    { get; set; }
    public string   SupplierName  { get; set; } = string.Empty;
    public DateTime PaymentDate   { get; set; }
    public string   PaymentMethod { get; set; } = string.Empty;
    public decimal  TotalAmount   { get; set; }
    public string?  BankAccount   { get; set; }
    public string?  ChequeNo      { get; set; }
    public DateTime? ChequeDate   { get; set; }
    public string?  Notes         { get; set; }
    // PaymentType: STANDARD (default) | ADVANCE_PAYMENT | PURCHASE_RETURN_SETTLEMENT
    public string   PaymentType   { get; set; } = "STANDARD";
    // Required when PaymentType == PURCHASE_RETURN_SETTLEMENT.
    public Guid?    CreditNoteUuid { get; set; }
    public List<CreateSupplierPaymentLineRequest> Lines { get; set; } = [];
}

public class SupplierPaymentFilter
{
    public Guid?     SupplierId { get; set; }
    public string?   Status     { get; set; }
    public string?   Method     { get; set; }
    public DateTime? DateFrom   { get; set; }
    public DateTime? DateTo     { get; set; }
    public int        Page       { get; set; } = 1;
    public int        PageSize   { get; set; } = 20;
}

public class SupplierPaymentListItemModel
{
    public Guid     UUID          { get; set; }
    public string   PaymentNumber { get; set; } = string.Empty;
    public Guid     SupplierId    { get; set; }
    public string   SupplierName  { get; set; } = string.Empty;
    public DateTime PaymentDate   { get; set; }
    public string   PaymentMethod { get; set; } = string.Empty;
    public decimal  TotalAmount   { get; set; }
    public string   Status        { get; set; } = string.Empty;
    public string   PaymentType   { get; set; } = string.Empty;
    public int      LineCount     { get; set; }
}

public class SupplierPaymentLineModel
{
    public Guid    Uuid                        { get; set; }
    public Guid    InvoiceUuid                 { get; set; }
    public string  InvoiceNumber               { get; set; } = string.Empty;
    public decimal AllocatedAmount             { get; set; }
    public decimal OutstandingBeforeAllocation { get; set; }
    public string? Notes                       { get; set; }
}

public class SupplierPaymentDetailModel
{
    public Guid     UUID          { get; set; }
    public string   PaymentNumber { get; set; } = string.Empty;
    public Guid     SupplierId    { get; set; }
    public string   SupplierName  { get; set; } = string.Empty;
    public DateTime PaymentDate   { get; set; }
    public string   PaymentMethod { get; set; } = string.Empty;
    public decimal  TotalAmount   { get; set; }
    public string?  BankAccount   { get; set; }
    public string?  ChequeNo      { get; set; }
    public DateTime? ChequeDate   { get; set; }
    public string   Status        { get; set; } = string.Empty;
    public string?  Notes         { get; set; }
    public int      CreatedBy     { get; set; }
    public DateTime CreatedDate   { get; set; }
    public int?     ApprovedBy    { get; set; }
    public DateTime? ApprovedAt   { get; set; }
    public DateTime? PostedAt     { get; set; }
    public DateTime? BouncedAt    { get; set; }
    public string   PaymentType   { get; set; } = string.Empty;
    public Guid?    CreditNoteUuid { get; set; }
    public List<SupplierPaymentLineModel> Lines { get; set; } = [];
}

// ── Supplier Aging models (SFM-006) ───────────────────────────────────────────

public class AgingInvoiceItem
{
    public Guid     InvoiceUuid       { get; set; }
    public string   InvoiceNumber     { get; set; } = string.Empty;
    public DateTime DueDate           { get; set; }
    public int      DaysOverdue       { get; set; }
    public decimal  OutstandingAmount { get; set; }
}

public class AgingBucket
{
    public string BucketName { get; set; } = string.Empty;
    public decimal Total     { get; set; }
    public List<AgingInvoiceItem> Invoices { get; set; } = [];
}

public class SupplierAgingModel
{
    public Guid    SupplierId   { get; set; }
    public string  SupplierName { get; set; } = string.Empty;
    public List<AgingBucket> Buckets { get; set; } = [];
    public decimal GrandTotal   { get; set; }
}

public class SupplierAgingSummaryRow
{
    public Guid    SupplierId    { get; set; }
    public string  SupplierName  { get; set; } = string.Empty;
    public decimal Current       { get; set; }
    public decimal Bucket31To60  { get; set; }
    public decimal Bucket61To90  { get; set; }
    public decimal Bucket91To120 { get; set; }
    public decimal Bucket120Plus { get; set; }
    public decimal GrandTotal    { get; set; }
}

public class CrossSupplierAgingReport
{
    public List<SupplierAgingSummaryRow> Suppliers { get; set; } = [];
    public SupplierAgingSummaryRow       GrandTotalRow { get; set; } = new() { SupplierName = "Grand Total" };
}

// ── Supplier Payment Reports (SFM-007) ────────────────────────────────────────

public class PaymentRegisterFilter
{
    public Guid?     SupplierId    { get; set; }
    public string?   Status        { get; set; }
    public string?   Method        { get; set; }
    // Matches against either BankAccount or ChequeNo.
    public string?   BankReference { get; set; }
    public DateTime? DateFrom      { get; set; }
    public DateTime? DateTo        { get; set; }
    public int        Page          { get; set; } = 1;
    public int        PageSize      { get; set; } = 20;
}

public class PaymentRegisterItem
{
    public Guid     Uuid          { get; set; }
    public string   PaymentNumber { get; set; } = string.Empty;
    public Guid     SupplierId    { get; set; }
    public string   SupplierName  { get; set; } = string.Empty;
    public DateTime PaymentDate   { get; set; }
    public string   PaymentMethod { get; set; } = string.Empty;
    public string   Status        { get; set; } = string.Empty;
    public decimal  TotalAmount   { get; set; }
    public string?  BankAccount   { get; set; }
    public string?  ChequeNo      { get; set; }
}

public class OutstandingPayablesFilter
{
    public int Page     { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

public class OutstandingPayableInvoiceItem
{
    public Guid     InvoiceUuid       { get; set; }
    public string   InvoiceNumber     { get; set; } = string.Empty;
    public decimal  TotalAmount       { get; set; }
    public decimal  OutstandingAmount { get; set; }
    public DateTime DueDate           { get; set; }
    public int      DaysOverdue       { get; set; }
    public string   PaymentStatus     { get; set; } = string.Empty;
}

public class OutstandingPayablesSupplierGroup
{
    public Guid    SupplierId       { get; set; }
    public string  SupplierName     { get; set; } = string.Empty;
    public decimal TotalOutstanding { get; set; }
    public List<OutstandingPayableInvoiceItem> Invoices { get; set; } = [];
}

public class PaymentMethodBreakdownFilter
{
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo   { get; set; }
}

public class PaymentMethodBreakdownItem
{
    public string  Method      { get; set; } = string.Empty;
    public int     Count       { get; set; }
    public decimal TotalAmount { get; set; }
}

public class PaymentMethodBreakdownReport
{
    public List<PaymentMethodBreakdownItem> Methods { get; set; } = [];
    public decimal GrandTotal { get; set; }
    public int     TotalCount { get; set; }
}

public class OutstandingInvoiceModel
{
    public Guid     InvoiceUuid       { get; set; }
    public string   InvoiceNumber     { get; set; } = string.Empty;
    public decimal  TotalAmount       { get; set; }
    public decimal  OutstandingAmount { get; set; }
    public string   PaymentStatus     { get; set; } = string.Empty;
    public DateTime DueDate           { get; set; }
}

// ── Credit Note models ────────────────────────────────────────────────────────

public class CreateCreditNoteRequest
{
    public Guid     SroId                { get; set; }
    public string   SupplierCreditNoteNo { get; set; } = string.Empty;
    public DateTime CreditDate           { get; set; }
    public decimal  CreditAmount         { get; set; }
    // Optional override — if not provided, invoice is auto-resolved via SRO's GRN reference
    public Guid?    InvoiceUuid          { get; set; }
    public string?  Notes                { get; set; }
}

public class ApplyCreditNoteRequest
{
    public Guid InvoiceUuid { get; set; }
}

public class CreditNoteListFilter
{
    public Guid?   SupplierId         { get; set; }
    public string? ApplicationStatus  { get; set; }
    public DateTime? DateFrom         { get; set; }
    public DateTime? DateTo           { get; set; }
    public int     Page               { get; set; } = 1;
    public int     PageSize           { get; set; } = 20;
}

public class CreditNoteListItemModel
{
    public Guid     UUID                  { get; set; }
    public string   CreditNoteNumber      { get; set; } = string.Empty;
    public string   SupplierCreditNoteNo  { get; set; } = string.Empty;
    public string   SroNumber             { get; set; } = string.Empty;
    public Guid     SupplierId            { get; set; }
    public string   SupplierName          { get; set; } = string.Empty;
    public string?  InvoiceNumber         { get; set; }
    public DateTime CreditDate            { get; set; }
    public decimal  CreditAmount          { get; set; }
    public string   ApplicationStatus     { get; set; } = string.Empty;
    public string?  AppliedToInvoiceNumber { get; set; }
    public DateTime CreatedDate           { get; set; }
}

public class CreditNoteDetailModel
{
    public Guid     UUID                    { get; set; }
    public string   CreditNoteNumber        { get; set; } = string.Empty;
    public string   SupplierCreditNoteNo    { get; set; } = string.Empty;
    public Guid     SroUuid                 { get; set; }
    public string   SroNumber               { get; set; } = string.Empty;
    public Guid     SupplierId              { get; set; }
    public string   SupplierName            { get; set; } = string.Empty;
    public Guid?    InvoiceUuid             { get; set; }
    public string?  InvoiceNumber           { get; set; }
    public DateTime CreditDate              { get; set; }
    public decimal  CreditAmount            { get; set; }
    public string   ApplicationStatus       { get; set; } = string.Empty;
    public Guid?    AppliedToInvoiceUuid    { get; set; }
    public string?  AppliedToInvoiceNumber  { get; set; }
    public decimal? CarriedForwardAmount    { get; set; }
    public DateTime? AppliedAt              { get; set; }
    public string?  Notes                   { get; set; }
    public DateTime CreatedDate             { get; set; }
}

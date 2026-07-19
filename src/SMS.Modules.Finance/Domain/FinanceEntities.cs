namespace SMS.Modules.Finance.Domain;

internal class Invoice
{
    public int     Id                  { get; set; }
    public Guid    UUID                { get; set; }
    public Guid    TraceId             { get; set; }
    public string  InvoiceNumber       { get; set; } = string.Empty;
    public string? SupplierInvoiceNo   { get; set; }
    public Guid    SupplierId          { get; set; }
    public string  SupplierName        { get; set; } = string.Empty;
    public Guid    PoUuid              { get; set; }
    public string  PoNumber            { get; set; } = string.Empty;
    public Guid?   GrnUuid             { get; set; }
    public string? GrnNumber           { get; set; }
    public DateTime InvoiceDate        { get; set; }
    public DateTime ReceivedDate       { get; set; }
    public DateTime DueDate            { get; set; }
    public string  Currency            { get; set; } = "PKR";
    public decimal Subtotal            { get; set; }
    public decimal TaxAmount           { get; set; }
    public decimal TotalAmount         { get; set; }
    public decimal MatchedPoValue      { get; set; }
    public decimal MatchedGrnValue     { get; set; }
    public decimal VarianceAmount      { get; set; }

    // MatchStatus: Pending | Matched | Variance | Approved | Rejected
    public string MatchStatus    { get; set; } = "Pending";
    // PaymentStatus: Unpaid | Scheduled | Partial | Paid | Overdue (legacy single-invoice Payment
    // flow) — OR, once posted via SFM-004's SupplierPayment flow: UNPAID | PARTIALLY_PAID |
    // FULLY_PAID | OVERPAID, derived from PaidAmount. Both vocabularies can appear on this field
    // depending on which payment mechanism touched the invoice; "fully paid" checks must treat
    // "Paid" and "FULLY_PAID" as equivalent.
    public string PaymentStatus  { get; set; } = "Unpaid";
    // Running total paid via SupplierPayment postings (SFM-004). Not incremented by the legacy
    // single-invoice Payment flow.
    public decimal PaidAmount    { get; set; }
    public string? PaymentMethod { get; set; }

    public int?    ApprovedBy   { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? Notes        { get; set; }
    public string? AttachmentUrl { get; set; }

    public bool    IsActive      { get; set; } = true;
    public bool    IsDelete      { get; set; }
    public int     CreatedBy     { get; set; }
    public DateTime CreatedDate  { get; set; }
    public int?    ModifiedBy    { get; set; }
    public DateTime? ModifiedDate { get; set; }

    public ICollection<InvoiceLine> Lines    { get; set; } = new List<InvoiceLine>();
    public ICollection<Payment>     Payments { get; set; } = new List<Payment>();
}

internal class InvoiceLine
{
    public int     Id              { get; set; }
    public Guid    UUID            { get; set; }
    public int     InvoiceId       { get; set; }
    public Guid?   GrnLineUuid     { get; set; }  // UUID ref to warehouse.grn_lines — no FK
    public Guid    PoLineUuid      { get; set; }  // UUID ref to demand.purchase_order_lines — no FK
    public int     LineNo          { get; set; }
    public string  ItemDescription { get; set; } = string.Empty;
    public string? UnitOfMeasure   { get; set; }
    public decimal QtyInvoiced     { get; set; }
    public decimal UnitPrice       { get; set; }
    public decimal LineTotal       { get; set; }
    public Invoice Invoice         { get; set; } = null!;
}

internal class Payment
{
    public int     Id              { get; set; }
    public Guid    UUID            { get; set; }
    public string  PaymentNumber   { get; set; } = string.Empty;
    public int     InvoiceId       { get; set; }
    public Guid    InvoiceUuid     { get; set; }
    public Guid    SupplierId      { get; set; }
    public string  SupplierName    { get; set; } = string.Empty;
    public DateTime PaymentDate    { get; set; }
    public decimal AmountPaid      { get; set; }
    public string  PaymentMethod   { get; set; } = string.Empty;
    public string? BankReference   { get; set; }
    public string? ChequeNumber    { get; set; }
    public string? AccountDebited  { get; set; }

    // Status: Pending | Processed | Cleared | Reversed
    public string  Status          { get; set; } = "Pending";
    public string? Notes           { get; set; }
    public int     ProcessedBy     { get; set; }
    public DateTime ProcessedAt    { get; set; }

    public bool    IsActive        { get; set; } = true;
    public bool    IsDelete        { get; set; }
    public int     CreatedBy       { get; set; }
    public DateTime CreatedDate    { get; set; }

    public Invoice Invoice { get; set; } = null!;
}

internal class DebitNote
{
    public int    Id                  { get; set; }
    public Guid   UUID                { get; set; }
    public string DebitNoteNumber     { get; set; } = string.Empty;   // DN-YYYY-NNNNN

    // SRO reference
    public Guid   SroUuid   { get; set; }
    public string SroNumber { get; set; } = string.Empty;

    // Supplier
    public Guid    SupplierId          { get; set; }
    public string  SupplierName        { get; set; } = string.Empty;
    public string? SupplierContactEmail { get; set; }

    // Debit reason
    public string  DebitReason       { get; set; } = string.Empty;
    public string? DebitReasonDetail { get; set; }
    public decimal DebitAmount       { get; set; }

    // Status: DRAFT | ISSUED | ACKNOWLEDGED | DISPUTED | SETTLED | WRITTEN_OFF
    public string Status { get; set; } = "ISSUED";

    public DateTime? IssuedAt       { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public DateTime? DisputedAt     { get; set; }
    public DateTime? SettledAt      { get; set; }
    public string?   DisputeNotes   { get; set; }
    public string?   Notes          { get; set; }

    public bool      IsActive     { get; set; } = true;
    public bool      IsDelete     { get; set; }
    public int       CreatedBy    { get; set; }
    public DateTime  CreatedDate  { get; set; }
    public int?      ModifiedBy   { get; set; }
    public DateTime? ModifiedDate { get; set; }
}

// Append-only running-balance ledger per supplier. BalanceAfter is always derived from the
// previous entry (by SequenceNo) at post time — never stored/updated anywhere else. SequenceNo
// carries a unique (SupplierId, SequenceNo) index that doubles as the concurrency guard for
// PostEntryAsync: a losing concurrent writer hits a unique-index violation and retries.
internal class SupplierLedgerEntry
{
    public int      Id              { get; set; }
    public Guid     UUID            { get; set; }
    public Guid     SupplierId      { get; set; }
    public int      SequenceNo      { get; set; }

    // TransactionType: e.g. INVOICE | PAYMENT | DEBIT_NOTE | CREDIT_NOTE | ADJUSTMENT | OPENING_BALANCE
    public string   TransactionType { get; set; } = string.Empty;
    // ReferenceType: e.g. Invoice | Payment | DebitNote | CreditNote
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

// Multi-invoice supplier payment header (SFM-003). Distinct from the existing single-invoice
// Payment entity — a SupplierPayment can allocate its TotalAmount across several invoices via
// SupplierPaymentLines, or be created unallocated (an advance) with zero lines.
internal class SupplierPayment
{
    public int      Id            { get; set; }
    public Guid     UUID          { get; set; }
    public string   PaymentNumber { get; set; } = string.Empty;   // SPAY-YYYY-NNNNN

    public Guid     SupplierId    { get; set; }
    public string   SupplierName  { get; set; } = string.Empty;

    public DateTime PaymentDate   { get; set; }
    // PaymentMethod: BANK_TRANSFER | ONLINE_WIRE | CHEQUE | CASH
    public string   PaymentMethod { get; set; } = string.Empty;
    public decimal  TotalAmount   { get; set; }

    // Conditional fields — required per PaymentMethod, validated in the service layer.
    public string?  BankAccount   { get; set; }   // BANK_TRANSFER / ONLINE_WIRE
    public string?  ChequeNo      { get; set; }   // CHEQUE
    public DateTime? ChequeDate   { get; set; }   // CHEQUE

    // Status: DRAFT | APPROVED | POSTED | CANCELLED
    public string   Status        { get; set; } = "DRAFT";
    public string?  Notes         { get; set; }

    public int      CreatedBy     { get; set; }
    public DateTime CreatedDate   { get; set; }
    public int?     ModifiedBy    { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public int?     ApprovedBy    { get; set; }
    public DateTime? ApprovedAt   { get; set; }

    // SFM-004 posting fields.
    public DateTime? PostedAt     { get; set; }
    // SFM-005 — set when a CHEQUE payment bounces (Status becomes BOUNCED).
    public DateTime? BouncedAt    { get; set; }
    // PaymentType: STANDARD | ADVANCE_PAYMENT | PURCHASE_RETURN_SETTLEMENT
    public string   PaymentType   { get; set; } = "STANDARD";
    // Source credit note for PURCHASE_RETURN_SETTLEMENT — its remaining credit is reduced by
    // TotalAmount when this payment is posted.
    public Guid?    CreditNoteUuid { get; set; }

    public ICollection<SupplierPaymentLine> Lines { get; set; } = new List<SupplierPaymentLine>();
}

// Created when an ADVANCE_PAYMENT-type SupplierPayment is posted — tracks the unallocated
// balance available to offset against future invoices for the supplier.
internal class SupplierAdvancePayment
{
    public int      Id                   { get; set; }
    public Guid     UUID                 { get; set; }
    public Guid     SupplierId           { get; set; }
    public Guid     SupplierPaymentUuid  { get; set; }
    public decimal  OriginalAmount       { get; set; }
    public decimal  AvailableBalance     { get; set; }
    public DateTime CreatedDate          { get; set; }
}

internal class SupplierPaymentLine
{
    public int      Id                          { get; set; }
    public Guid     UUID                        { get; set; }
    public int      SupplierPaymentId            { get; set; }

    public Guid     InvoiceUuid                 { get; set; }
    public string   InvoiceNumber                { get; set; } = string.Empty;
    public decimal  AllocatedAmount              { get; set; }
    // Snapshot of the invoice's outstanding balance at the moment this allocation was made.
    public decimal  OutstandingBeforeAllocation  { get; set; }
    public string?  Notes                        { get; set; }

    public SupplierPayment SupplierPayment { get; set; } = null!;
}

internal class CreditNote
{
    public int    Id                     { get; set; }
    public Guid   UUID                   { get; set; }
    public string CreditNoteNumber       { get; set; } = string.Empty;   // CN-YYYY-NNNNN
    public string SupplierCreditNoteNo   { get; set; } = string.Empty;   // supplier's own number (required)

    // SRO reference
    public Guid   SroUuid   { get; set; }
    public string SroNumber { get; set; } = string.Empty;

    // Supplier
    public Guid   SupplierId   { get; set; }
    public string SupplierName { get; set; } = string.Empty;

    // Invoice the credit is applied against (auto-resolved from SRO's GRN)
    public Guid?   InvoiceUuid   { get; set; }
    public string? InvoiceNumber { get; set; }

    public DateTime CreditDate   { get; set; }
    public decimal  CreditAmount { get; set; }

    // ApplicationStatus: PENDING | APPLIED_TO_INVOICE | CARRIED_FORWARD | APPLIED
    public string ApplicationStatus { get; set; } = "PENDING";

    // Populated when carried-forward credit is later applied to a specific invoice
    public Guid?   AppliedToInvoiceUuid   { get; set; }
    public string? AppliedToInvoiceNumber { get; set; }
    public decimal? CarriedForwardAmount  { get; set; }
    public DateTime? AppliedAt            { get; set; }

    public string? Notes { get; set; }

    public bool     IsActive     { get; set; } = true;
    public bool     IsDelete     { get; set; }
    public int      CreatedBy    { get; set; }
    public DateTime CreatedDate  { get; set; }
    public int?     ModifiedBy   { get; set; }
    public DateTime? ModifiedDate { get; set; }
}

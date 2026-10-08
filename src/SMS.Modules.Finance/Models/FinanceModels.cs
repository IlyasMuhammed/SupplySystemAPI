namespace SMS.Modules.Finance.Models;

// ── Invoice line models ───────────────────────────────────────────────────────

public class InvoiceLineRequest
{
    public Guid?   GrnLineUuid     { get; set; }   // optional: links to a GRN line (3-way match)
    // Optional since G10: required for anything ordered, absent on a freight line.
    public Guid?   PoLineUuid      { get; set; }   // links to PO line for QtyInvoiced tracking
    public string  ItemDescription { get; set; } = string.Empty;
    public string? UnitOfMeasure   { get; set; }
    public decimal QtyInvoiced     { get; set; }
    public decimal UnitPrice       { get; set; }
}

public class InvoiceLineModel
{
    public Guid    UUID            { get; set; }
    public Guid?   GrnLineUuid    { get; set; }
    public Guid?   PoLineUuid     { get; set; }
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
    // Optional since G10. Absent means a payable with no purchase order behind it — a carrier's
    // freight bill is the first of them.
    public Guid?   PoUuid            { get; set; }
    public Guid?   GrnUuid           { get; set; }
    public DateTime InvoiceDate      { get; set; }
    public DateTime ReceivedDate     { get; set; }
    public DateTime DueDate          { get; set; }
    public string  Currency          { get; set; } = "PKR";
    /// <summary>
    /// A35 D-14 — the invoice's currency (global lookups.Currencies id). When given it wins over <see cref="Currency"/>.
    /// When absent, an invoice against a purchase order inherits the PO's currency; otherwise <see cref="Currency"/> is used.
    /// </summary>
    public Guid?   CurrencyId        { get; set; }
    // When Lines are provided, Subtotal is computed from them; otherwise enter manually.
    public decimal Subtotal          { get; set; }
    /// <summary>The tax as an amount — used only when no <see cref="TaxCodeUuid"/> is given.</summary>
    public decimal TaxAmount         { get; set; }
    /// <summary>
    /// SAP alignment (S-3): a purchase tax code (usage PURCHASE or BOTH, active). When given, the server
    /// computes TaxAmount = round(Subtotal × rate / 100, 2, away from zero), snapshots the code and its rate
    /// on the invoice, and <b>ignores</b> <see cref="TaxAmount"/>. Null = tax entered as an amount, as before.
    /// </summary>
    public Guid?   TaxCodeUuid       { get; set; }
    public string? PaymentMethod     { get; set; }
    public string? Notes             { get; set; }
    public string? AttachmentUrl     { get; set; }
    public List<InvoiceLineRequest>? Lines { get; set; }
    // Client-generated id so attachments uploaded before save can be linked via the same
    // DocumentId — becomes the Invoice's own UUID on save.
    public Guid? InvoiceUuid { get; set; }
}

/// <summary>
/// Null means "leave as it is". Once an invoice is Approved its tax, tax code and match status are
/// frozen (reverse it instead); once Reversed only Notes and AttachmentUrl may change. See
/// <c>InvoiceRepository.PatchAsync</c> for every rule.
/// </summary>
public class PatchInvoiceRequest
{
    /// <summary>What the bill is sent to QuickBooks under. Refused once a payment has been recorded against the invoice.</summary>
    public string?   SupplierInvoiceNo { get; set; }
    public DateTime? DueDate           { get; set; }
    public string?   PaymentMethod     { get; set; }
    /// <summary>A hand-entered tax amount. Refused while the invoice's tax comes from a tax code (pick another code, or remove it).</summary>
    public decimal?  TaxAmount         { get; set; }
    /// <summary>A purchase tax code to apply (TaxAmount is then computed and the one sent ignored); <c>Guid.Empty</c> removes the code.</summary>
    public Guid?     TaxCodeUuid       { get; set; }
    /// <summary>Pending, Matched or Variance only — approving, rejecting and reversing have their own actions.</summary>
    public string?   MatchStatus       { get; set; }
    /// <summary>Unpaid or Scheduled only, and only while no payment has been recorded; anything else follows the payments.</summary>
    public string?   PaymentStatus     { get; set; }
    public string?   Notes             { get; set; }
    public string?   AttachmentUrl     { get; set; }
}

/// <summary>SAP alignment (S-7): reverse an approved, unpaid supplier invoice.</summary>
public class ReverseInvoiceRequest
{
    public string Reason { get; set; } = string.Empty;
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
    /// <summary>Null on a payable with no purchase order behind it — a freight bill (G10).</summary>
    public string?  PoNumber          { get; set; }
    public string?  GrnNumber         { get; set; }
    public DateTime InvoiceDate       { get; set; }
    public DateTime DueDate           { get; set; }
    public decimal  TotalAmount       { get; set; }
    public string   Currency          { get; set; } = string.Empty;
    /// <summary>The purchase tax code snapshotted on the invoice; null when the tax was entered as an amount.</summary>
    public string?  TaxCode           { get; set; }
    public decimal? TaxPercent        { get; set; }
    /// <summary>Pending | Matched | Variance | Approved | Rejected | Reversed.</summary>
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
    /// <summary>Null on a payable with no purchase order behind it — a freight bill (G10).</summary>
    public Guid?    PoUuid            { get; set; }
    public string?  PoNumber          { get; set; }
    public Guid?    GrnUuid           { get; set; }
    public string?  GrnNumber         { get; set; }
    public DateTime InvoiceDate       { get; set; }
    public DateTime ReceivedDate      { get; set; }
    public DateTime DueDate           { get; set; }
    public string   Currency          { get; set; } = string.Empty;
    public decimal  Subtotal          { get; set; }
    public decimal  TaxAmount         { get; set; }
    public decimal  TotalAmount       { get; set; }
    /// <summary>SAP alignment (S-3) — the purchase tax code and its rate as snapshotted; null when the tax was entered as an amount.</summary>
    public Guid?    TaxCodeUuid       { get; set; }
    public string?  TaxCode           { get; set; }
    public decimal? TaxPercent        { get; set; }
    /// <summary>
    /// S-5 — snapshotted at approval: 1 <see cref="Currency"/> = ExchangeRate <see cref="BaseCurrencyCode"/>
    /// on the invoice date. ExchangeRate and BaseTotalAmount are null when no rate was on file (or before approval).
    /// </summary>
    public decimal? ExchangeRate      { get; set; }
    public string?  BaseCurrencyCode  { get; set; }
    public decimal? BaseTotalAmount   { get; set; }
    /// <summary>A35 D-10 — the invoice currency's id, the purchase base's id and when the rate was locked (at approval).</summary>
    public Guid?     CurrencyId           { get; set; }
    public Guid?     BaseCurrencyId       { get; set; }
    public DateTime? ExchangeRateLockedAt { get; set; }
    /// <summary>Three-way match against the PO and GRN values, on the net Subtotal (S-8).</summary>
    public decimal  MatchedPoValue    { get; set; }
    public decimal  MatchedGrnValue   { get; set; }
    /// <summary>Subtotal − PO value (both net of tax). Zero for an invoice with no purchase order.</summary>
    public decimal  VarianceAmount    { get; set; }
    /// <summary>Pending | Matched | Variance | Approved | Rejected | Reversed.</summary>
    public string   MatchStatus       { get; set; } = string.Empty;
    public string   PaymentStatus     { get; set; } = string.Empty;
    public decimal  PaidAmount        { get; set; }
    public string?  PaymentMethod     { get; set; }
    public int?     ApprovedBy        { get; set; }
    public DateTime? ApprovedAt       { get; set; }
    /// <summary>S-7 — set when the approved invoice was reversed (opposite ledger entry; nothing edited).</summary>
    public DateTime? ReversedAt       { get; set; }
    public int?     ReversedBy        { get; set; }
    public string?  ReversalReason    { get; set; }
    public string?  Notes             { get; set; }
    public string?  AttachmentUrl     { get; set; }
    public int      CreatedBy         { get; set; }
    public DateTime CreatedDate       { get; set; }
    public List<InvoiceLineModel>     Lines    { get; set; } = new();
    public List<PaymentListItemModel> Payments { get; set; } = new();
    /// <summary>A35 E-06 — the multi-invoice supplier payments' lines against this invoice, with their realized differences.</summary>
    public List<InvoiceSupplierPaymentModel> SupplierPayments { get; set; } = new();
    /// <summary>A35 E-06 — net realized exchange difference booked on this invoice (purchase base; bounced payments net out). Null before A35 / none booked.</summary>
    public decimal? RealizedExchangeDifference { get; set; }
    public List<DebitNoteListItemModel>  DebitNotes  { get; set; } = new();
    public List<CreditNoteListItemModel> CreditNotes { get; set; } = new();
}

public class InvoiceFilter
{
    /// <summary>Pending | Matched | Variance | Approved | Rejected | Reversed.</summary>
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
    /// <summary>
    /// A35 C7 — realized gain (+) / loss (−) in the purchase base. On an invoice's detail: the multi-invoice supplier payment
    /// line's difference; always null for a legacy single-invoice payment (no FX was ever booked on those).
    /// </summary>
    public decimal? ExchangeDifference { get; set; }
}

/// <summary>A35 E-06 — one supplier-payment line against an invoice, as the invoice's detail shows it.</summary>
public class InvoiceSupplierPaymentModel
{
    public Guid     PaymentUuid        { get; set; }
    public string   PaymentNumber      { get; set; } = string.Empty;
    public DateTime PaymentDate        { get; set; }
    public string   PaymentMethod      { get; set; } = string.Empty;
    public string   Status             { get; set; } = string.Empty;
    public string?  CurrencyCode       { get; set; }
    public decimal  AllocatedAmount    { get; set; }
    /// <summary>Realized gain (+) / loss (−) on this line in the purchase base; null until posted (or before A35).</summary>
    public decimal? ExchangeDifference { get; set; }
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
    // Optional override — if not provided, invoice is auto-resolved via SRO's GRN reference
    public Guid?   InvoiceUuid         { get; set; }
    public string? Notes               { get; set; }
}

public class ApplyDebitNoteRequest
{
    public Guid InvoiceUuid { get; set; }
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
    public string?  InvoiceNumber          { get; set; }
    public string   ApplicationStatus      { get; set; } = string.Empty;
    public string?  AppliedToInvoiceNumber { get; set; }
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
    public Guid?    InvoiceUuid             { get; set; }
    public string?  InvoiceNumber           { get; set; }
    public string   ApplicationStatus       { get; set; } = string.Empty;
    public Guid?    AppliedToInvoiceUuid    { get; set; }
    public string?  AppliedToInvoiceNumber  { get; set; }
    public decimal? CarriedForwardAmount    { get; set; }
    public DateTime? AppliedAt              { get; set; }
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

// ── Master Financial Ledger (FSD Addendum 24, ML-002) ─────────────────────────

public class MasterLedgerEntryModel
{
    public Guid     Uuid            { get; set; }
    public int       SequenceNo      { get; set; }
    public Guid      SupplierId      { get; set; }
    public string    SupplierName    { get; set; } = string.Empty;
    public string    TransactionType { get; set; } = string.Empty;
    public string    ReferenceType   { get; set; } = string.Empty;
    public Guid      ReferenceId     { get; set; }
    public string    ReferenceNo     { get; set; } = string.Empty;
    public DateTime  EntryDate       { get; set; }
    public decimal   DebitAmount     { get; set; }
    public decimal   CreditAmount    { get; set; }
    public decimal   BalanceAfter    { get; set; }
    public string?   Narration       { get; set; }
    public int       CreatedBy       { get; set; }
    public DateTime  CreatedDate     { get; set; }
}

public class MasterLedgerFilter
{
    public DateTime?     DateFrom         { get; set; }
    public DateTime?     DateTo           { get; set; }
    public Guid?         SupplierId       { get; set; }
    public List<string>? TransactionTypes { get; set; }
    public decimal?      MinAmount        { get; set; }
    public int           Page             { get; set; } = 1;
    public int           PageSize         { get; set; } = 20;
}

public class MasterLedgerSummaryModel
{
    // Current organization-wide balance — always the latest entry's BalanceAfter across the WHOLE
    // table, regardless of any filter applied (per ML-002 acceptance criteria). The other three
    // fields DO respect the filter, since they describe "the filtered period".
    public decimal TotalPayables { get; set; }
    public decimal TotalDebits   { get; set; }
    public decimal TotalCredits  { get; set; }
    public decimal NetMovement   { get; set; }
}

public class MasterLedgerBalanceModel
{
    public decimal  Balance { get; set; }
    public DateTime AsOf    { get; set; }
}

// ── Master Product Ledger (FSD Addendum 24, ML-004) ────────────────────────────

public class MasterProductLedgerEntryModel
{
    public Guid     LedgerId        { get; set; }
    public int      VariantId       { get; set; }
    public string   ProductCode     { get; set; } = string.Empty;
    public string   ProductName     { get; set; } = string.Empty;
    // PV-008 — distinct alongside ProductCode/ProductName (see MasterProductLedger entity).
    public string?  VariantName     { get; set; }
    public string?  Sku             { get; set; }
    public int?     CategoryId      { get; set; }
    public string?  CategoryName    { get; set; }
    public int      WarehouseId     { get; set; }
    public string   WarehouseName   { get; set; } = string.Empty;
    public DateTime TransactionDate { get; set; }
    public string   TransactionType { get; set; } = string.Empty;
    public string   ReferenceType   { get; set; } = string.Empty;
    public Guid     ReferenceId     { get; set; }
    public string   ReferenceNumber { get; set; } = string.Empty;
    public decimal? QuantityIn      { get; set; }
    public decimal? QuantityOut     { get; set; }
    public decimal  UnitCost        { get; set; }
    public decimal  TotalValue      { get; set; }
    public string   SourceType      { get; set; } = string.Empty;
    public string?  SourceName      { get; set; }
    public string   DestinationType { get; set; } = string.Empty;
    public string?  DestinationName { get; set; }
    public string?  Notes           { get; set; }
    public int      CreatedBy       { get; set; }
    public DateTime CreatedDate     { get; set; }
}

public class MasterProductLedgerFilter
{
    public DateTime? DateFrom        { get; set; }
    public DateTime? DateTo          { get; set; }
    public int?       VariantId       { get; set; }
    public int?       CategoryId      { get; set; }
    public int?       WarehouseId     { get; set; }
    public string?    TransactionType { get; set; }
    public string?    SourceType      { get; set; }
    public string?    DestinationType { get; set; }
    public int        Page            { get; set; } = 1;
    public int        PageSize        { get; set; } = 20;
}

public class MasterProductLedgerSummaryModel
{
    public decimal TotalReceiptsQty { get; set; }
    public decimal TotalIssuesQty   { get; set; }
    public decimal NetMovement      { get; set; }
    public decimal TotalValueMoved  { get; set; }
}

// ── ML-005: Opening Balance Import ──────────────────────────────────────────────

public class OpeningBalanceImportRequest
{
    public List<OpeningBalanceLineRequest> Suppliers { get; set; } = [];
}

public class OpeningBalanceLineRequest
{
    public Guid    SupplierId   { get; set; }
    public string  SupplierName { get; set; } = string.Empty;
    public decimal Amount       { get; set; }
}

public class OpeningBalanceImportResult
{
    public int      EntriesCreated  { get; set; }
    public decimal  TotalImported   { get; set; }
    public decimal  MasterBalanceAfter { get; set; }
}

// ── ML-005: Bad Debt Write-off ──────────────────────────────────────────────────

public class CreateWriteOffRequest
{
    public Guid    SupplierId   { get; set; }
    public string  SupplierName { get; set; } = string.Empty;
    public decimal Amount       { get; set; }
    public string  Reason       { get; set; } = string.Empty;
}

public class RejectWriteOffRequest
{
    public string Reason { get; set; } = string.Empty;
}

public class WriteOffModel
{
    public Guid      Uuid            { get; set; }
    public Guid      SupplierId      { get; set; }
    public string    SupplierName    { get; set; } = string.Empty;
    public decimal   Amount          { get; set; }
    public string    Reason          { get; set; } = string.Empty;
    public string    Status          { get; set; } = string.Empty;
    public int       CreatedBy       { get; set; }
    public DateTime  CreatedDate     { get; set; }
    public int?      ApprovedBy      { get; set; }
    public DateTime? ApprovedAt      { get; set; }
    public string?   RejectionReason { get; set; }
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
    // Client-generated id so payment-evidence attachments uploaded before save can be linked
    // to this payment via the same DocumentId (see supplier-payment-create.component.ts).
    public Guid?    PaymentUuid    { get; set; }
    /// <summary>
    /// A35 D-14 — the payment's currency: <see cref="CurrencyId"/> (global lookups id) or <see cref="CurrencyCode"/>. Both
    /// absent: the first allocated invoice's currency, else the supplier's default purchase currency, else the purchase base.
    /// Every line's invoice must be in it (400 otherwise).
    /// </summary>
    public Guid?    CurrencyId     { get; set; }
    public string?  CurrencyCode   { get; set; }
    public List<CreateSupplierPaymentLineRequest> Lines { get; set; } = [];
}

public class SupplierPaymentFilter
{
    public Guid?     SupplierId  { get; set; }
    public string?   Status      { get; set; }
    public string?   Method      { get; set; }
    public DateTime? DateFrom    { get; set; }
    public DateTime? DateTo      { get; set; }
    public Guid?     InvoiceUuid { get; set; }
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
    /// <summary>A35 — the payment's currency and, once posted, its amount in the purchase base.</summary>
    public string?  CurrencyCode  { get; set; }
    public decimal? AmountBase    { get; set; }
    public string   Status        { get; set; } = string.Empty;
    public string   PaymentType   { get; set; } = string.Empty;
    public int      LineCount     { get; set; }
    public int      AttachmentCount { get; set; }
}

public class SupplierPaymentLineModel
{
    public Guid    Uuid                        { get; set; }
    public Guid    InvoiceUuid                 { get; set; }
    public string  InvoiceNumber               { get; set; } = string.Empty;
    public decimal AllocatedAmount             { get; set; }
    public decimal OutstandingBeforeAllocation { get; set; }
    public string? Notes                       { get; set; }
    /// <summary>A35 C7 — realized gain (+) / loss (−) in the purchase base on this line, set when the payment posts.</summary>
    public decimal? ExchangeDifference         { get; set; }
}

public class SupplierPaymentDetailModel
{
    public Guid     UUID          { get; set; }
    public Guid     TraceId       { get; set; }
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
    /// <summary>A35 D-10 — currency, and (from posting) the locked rate, the purchase base, the base amount and Σ line differences.</summary>
    public string?  CurrencyCode       { get; set; }
    public Guid?    CurrencyId         { get; set; }
    public decimal? ExchangeRate       { get; set; }
    public Guid?    BaseCurrencyId     { get; set; }
    public string?  BaseCurrencyCode   { get; set; }
    public decimal? AmountBase         { get; set; }
    public decimal? ExchangeDifference { get; set; }
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

using SMS.Modules.Finance.ArAp;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.Domain;

// Addendum 48 — AR & AP sub-ledgers (docs/ar-ap-subledgers/ADDENDUM-48-ANALYSIS.md, API-CONTRACT.md §11).
// Owned by CORE: other agents read and write these rows through their own services but never change the shapes here.
// Conventions (D-2, D-CORE-2): int Id + Guid Uuid, Guid OrganizationId (tenant filter), partners are BusinessPartner
// uuids with no FK (they live in Suppliers), statuses are strings with CHECK constraints (ArApEnums), dates are DateOnly,
// money decimal(18,2) like the Finance documents, int? user ids + name snapshots. No A48 row references the GL (D-1).

/// <summary>
/// D-4 — one row per organization + partner + role (Customer / Vendor), created on first access. The permanent credit
/// limit is NOT here: it stays on BusinessPartner.CreditLimit (A-4, single source).
/// </summary>
internal class PartnerFinancialProfile : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     Uuid           { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId { get; set; }

    /// <summary>suppliers.BusinessPartners.UUID — no FK (another module).</summary>
    public Guid     PartnerUuid    { get; set; }
    /// <summary><see cref="ArApEnums.PartnerRole"/>.</summary>
    public string   PartnerRole    { get; set; } = ArApEnums.PartnerRole.Customer;

    // ── Credit (AR) ──
    public bool      CreditHold          { get; set; }
    public string?   CreditHoldReason    { get; set; }
    public DateTime? CreditHoldAt        { get; set; }
    public int?      CreditHoldBy        { get; set; }
    public string?   CreditHoldByName    { get; set; }
    public decimal?  TemporaryCreditLimit  { get; set; }
    public DateOnly? TemporaryCreditExpiry { get; set; }
    /// <summary><see cref="ArApEnums.RiskTier"/>; Medium by default (BR-CR-007).</summary>
    public string    RiskTier            { get; set; } = ArApEnums.RiskTier.Medium;

    // ── Dunning (AR) ──
    /// <summary>Null = the organization's default procedure.</summary>
    public int?      DunningProcedureId  { get; set; }
    public DunningProcedure? DunningProcedure { get; set; }
    public int       CurrentDunningLevel { get; set; }
    public DateOnly? LastDunningDate     { get; set; }
    /// <summary>BR-DN-005 — reaching the procedure's block-sales level puts the customer on hold only when this is on.</summary>
    public bool      BlockSalesAtDunning { get; set; }
    public bool      ExcludeFromDunning  { get; set; }
    /// <summary>A CustomerPaymentMethods code.</summary>
    public string?   DefaultPaymentMethod { get; set; }

    // ── AP ──
    /// <summary><see cref="ArApEnums.PaymentPriority"/>; Normal by default.</summary>
    public string    PaymentPriority          { get; set; } = ArApEnums.PaymentPriority.Normal;
    /// <summary>A SupplierPayment method code (BANK_TRANSFER, ONLINE_WIRE, CHEQUE, CASH).</summary>
    public string?   PreferredPaymentMethod   { get; set; }
    public bool      WithholdingTaxApplicable { get; set; }
    /// <summary>The vendor's default early-payment terms, copied onto new payment schedules.</summary>
    public int?      EarlyPaymentTermId       { get; set; }
    public EarlyPaymentTerm? EarlyPaymentTerm { get; set; }

    // ── Statistics (informational; queries compute the real figures live) ──
    public DateOnly? LastPaymentDate     { get; set; }
    public int?      AverageDaysToPay    { get; set; }
    public decimal?  TotalOverdueAmount  { get; set; }
    public DateTime? StatisticsUpdatedAt { get; set; }

    public string?   Notes { get; set; }

    public DateTime  CreatedAt      { get; set; } = DateTime.UtcNow;
    public int?      CreatedBy      { get; set; }
    public string?   CreatedByName  { get; set; }
    public DateTime? ModifiedAt     { get; set; }
    public int?      ModifiedBy     { get; set; }
    public string?   ModifiedByName { get; set; }
    public byte[]    RowVersion     { get; set; } = [];
}

/// <summary>BR-CR-006 — the audit trail of every credit action on a customer (hold, release, temporary limit, tier, override, dunning).</summary>
internal class CreditHoldRequest : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     Uuid           { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId { get; set; }

    public int      ProfileId      { get; set; }
    public PartnerFinancialProfile Profile { get; set; } = null!;
    public Guid     PartnerUuid    { get; set; }
    public string?  PartnerName    { get; set; }

    /// <summary><see cref="ArApEnums.CreditHoldAction"/>.</summary>
    public string   Action         { get; set; } = ArApEnums.CreditHoldAction.Hold;
    public string   Reason         { get; set; } = string.Empty;
    public string?  PreviousValue  { get; set; }
    public string?  NewValue       { get; set; }
    /// <summary>TempIncrease: the limit; Override: the document's base value.</summary>
    public decimal? Amount         { get; set; }
    public DateOnly? ExpiryDate    { get; set; }

    /// <summary>Override / dunning: what triggered it (SALE_ORDER, SALES_INVOICE, DUNNING_RUN …).</summary>
    public string?  SourceDocumentType   { get; set; }
    public Guid?    SourceDocumentUuid   { get; set; }
    public string?  SourceDocumentNumber { get; set; }

    public int?      RequestedBy     { get; set; }
    public string?   RequestedByName { get; set; }
    public DateTime  RequestedAt     { get; set; } = DateTime.UtcNow;
    public int?      ApprovedBy      { get; set; }
    public string?   ApprovedByName  { get; set; }
    public DateTime? ApprovedAt      { get; set; }
    /// <summary><see cref="ArApEnums.CreditHoldRequestStatus"/>.</summary>
    public string    Status          { get; set; } = ArApEnums.CreditHoldRequestStatus.Completed;
}

/// <summary>C4 / C13 — one row per statement generated (and its email). The PDF is generated on request, never stored (D-11).</summary>
internal class StatementLog : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     Uuid           { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId { get; set; }

    public Guid     PartnerUuid    { get; set; }
    public string?  PartnerName    { get; set; }
    public string   PartnerRole    { get; set; } = ArApEnums.PartnerRole.Customer;
    /// <summary><see cref="ArApEnums.StatementType"/>.</summary>
    public string   StatementType  { get; set; } = ArApEnums.StatementType.Activity;
    public DateOnly FromDate       { get; set; }
    public DateOnly ToDate         { get; set; }
    /// <summary>Null = every currency section.</summary>
    public string?  CurrencyCode   { get; set; }
    public decimal  OpeningBalance { get; set; }
    public decimal  ClosingBalance { get; set; }
    public int      LineCount      { get; set; }
    /// <summary>PDF / JSON.</summary>
    public string   Format         { get; set; } = "PDF";

    /// <summary><see cref="ArApEnums.StatementEmailStatus"/>.</summary>
    public string    EmailStatus { get; set; } = ArApEnums.StatementEmailStatus.NotSent;
    public string?   EmailedTo   { get; set; }
    public DateTime? EmailedAt   { get; set; }
    public string?   EmailError  { get; set; }
    /// <summary>The batch run that produced it (batch statements), null for a single one.</summary>
    public Guid?     BatchUuid   { get; set; }

    public DateTime  GeneratedAt     { get; set; } = DateTime.UtcNow;
    public int?      GeneratedBy     { get; set; }
    public string?   GeneratedByName { get; set; }
}

/// <summary>C5 — a dunning procedure (levels below). One default per organization; seeded STANDARD (D-16).</summary>
internal class DunningProcedure : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     Uuid           { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId { get; set; }

    public string   Code           { get; set; } = string.Empty;
    public string   Name           { get; set; } = string.Empty;
    public string?  Description    { get; set; }
    public bool     IsDefault      { get; set; }
    public bool     IsActive       { get; set; } = true;
    /// <summary>Days after the due date before level 1 (BR-DN-001).</summary>
    public int      GraceDays      { get; set; }
    /// <summary>Total overdue (base currency) below which a customer is skipped (BR-DN-002).</summary>
    public decimal  MinimumAmount  { get; set; }
    /// <summary>Null = never block (BR-DN-005).</summary>
    public int?     BlockSalesAtLevel { get; set; }

    public DateTime  CreatedAt      { get; set; } = DateTime.UtcNow;
    public int?      CreatedBy      { get; set; }
    public string?   CreatedByName  { get; set; }
    public DateTime? ModifiedAt     { get; set; }
    public int?      ModifiedBy     { get; set; }
    public string?   ModifiedByName { get; set; }

    public ICollection<DunningLevel> Levels { get; set; } = new List<DunningLevel>();
}

internal class DunningLevel : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     Uuid           { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId { get; set; }

    public int      DunningProcedureId { get; set; }
    public DunningProcedure DunningProcedure { get; set; } = null!;
    /// <summary>1, 2, 3, … — never skipped (BR-DN-004).</summary>
    public int      LevelNumber       { get; set; }
    /// <summary>Days after the previous level (level 1: after due date + grace).</summary>
    public int      DaysAfterPrevious { get; set; }
    public string   SubjectTemplate   { get; set; } = string.Empty;
    public string   BodyTemplate      { get; set; } = string.Empty;
    public bool     IncludeInvoiceList { get; set; } = true;
    public bool     IncludeStatement   { get; set; }
    /// <summary>In the organization's sale base currency (D-13); null = no charge. No GL account here — the charge is a customer document (D-10).</summary>
    public decimal? ChargeAmount      { get; set; }
    /// <summary><see cref="ArApEnums.DunningActionType"/>.</summary>
    public string   ActionType        { get; set; } = ArApEnums.DunningActionType.Email;
    public string?  EscalationNote    { get; set; }
}

internal class DunningRun : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     Uuid           { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId { get; set; }

    /// <summary>DUN-… from the document-number generator.</summary>
    public string   RunNumber      { get; set; } = string.Empty;
    /// <summary>The organization's day the run belongs to — one non-cancelled run per day (BR-DN-010, filtered unique index).</summary>
    public DateOnly RunDate        { get; set; }
    /// <summary>The date overdue days are measured at (usually RunDate).</summary>
    public DateOnly AsOfDate       { get; set; }
    /// <summary><see cref="ArApEnums.DunningRunStatus"/>.</summary>
    public string   Status         { get; set; } = ArApEnums.DunningRunStatus.Draft;
    /// <summary>The sale base currency the totals are in.</summary>
    public string?  CurrencyCode   { get; set; }
    public int      TotalCustomers { get; set; }
    public decimal  TotalAmount    { get; set; }
    public int      ItemsProcessed { get; set; }
    public int      ItemsSent      { get; set; }
    public int      ItemsSkipped   { get; set; }
    public int      ItemsFailed    { get; set; }

    public DateTime  CreatedAt       { get; set; } = DateTime.UtcNow;
    public int?      CreatedBy       { get; set; }
    public string?   CreatedByName   { get; set; }
    public DateTime? ExecutedAt      { get; set; }
    public int?      ExecutedBy      { get; set; }
    public string?   ExecutedByName  { get; set; }
    public DateTime? CompletedAt     { get; set; }
    public DateTime? CancelledAt     { get; set; }
    public int?      CancelledBy     { get; set; }
    public string?   CancelledByName { get; set; }
    public string?   CancelReason    { get; set; }

    public ICollection<DunningRunItem> Items { get; set; } = new List<DunningRunItem>();
}

internal class DunningRunItem : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     Uuid           { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId { get; set; }

    public int      DunningRunId   { get; set; }
    public DunningRun DunningRun   { get; set; } = null!;
    public Guid     PartnerUuid    { get; set; }
    public string   PartnerName    { get; set; } = string.Empty;
    public string?  PartnerCode    { get; set; }
    public int?     DunningProcedureId { get; set; }
    public int      DunningLevel   { get; set; }
    public int      PreviousLevel  { get; set; }
    /// <summary>Base currency (D-13).</summary>
    public decimal  OverdueAmount  { get; set; }
    /// <summary>Per-currency overdue text, e.g. "PKR 12,000.00; USD 150.00".</summary>
    public string?  CurrencySummary { get; set; }
    public int      OpenItemCount  { get; set; }
    public DateOnly OldestDueDate  { get; set; }
    public int      DaysOverdue    { get; set; }
    /// <summary><see cref="ArApEnums.DunningActionType"/>.</summary>
    public string   ActionType     { get; set; } = ArApEnums.DunningActionType.Email;
    public decimal? ChargeAmount   { get; set; }
    /// <summary>The customer document the charge was raised as (D-10) — a SalesInvoice.</summary>
    public Guid?    ChargeDocumentUuid   { get; set; }
    public string?  ChargeDocumentNumber { get; set; }
    public string?  SentTo         { get; set; }
    public DateTime? SentAt        { get; set; }
    /// <summary>Rendered notice, kept for the immutable record (BR-DN-008).</summary>
    public string?  Subject        { get; set; }
    public string?  Body           { get; set; }
    /// <summary>JSON array of the sales-invoice uuids dunned.</summary>
    public string?  InvoiceUuids   { get; set; }
    /// <summary><see cref="ArApEnums.DunningRunItemStatus"/>.</summary>
    public string   Status         { get; set; } = ArApEnums.DunningRunItemStatus.Pending;
    public string?  SkipReason     { get; set; }
    public string?  FailureReason  { get; set; }
}

/// <summary>C9 / D-7 — match tolerances: one organization default, optionally one active rule per vendor.</summary>
internal class MatchToleranceRule : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     Uuid           { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId { get; set; }

    public string   Name           { get; set; } = string.Empty;
    /// <summary>Null = an organization rule; else the vendor (BusinessPartner uuid) it applies to.</summary>
    public Guid?    VendorUuid     { get; set; }
    public string?  VendorName     { get; set; }
    /// <summary>2.00 = 2%.</summary>
    public decimal  PriceTolerancePercent    { get; set; }
    public decimal  PriceToleranceAmount     { get; set; }
    public decimal  QuantityTolerancePercent { get; set; }
    public decimal  TotalTolerancePercent    { get; set; }
    public decimal  TotalToleranceAmount     { get; set; }
    public bool     AutoApproveWithin        { get; set; }
    public bool     IsDefault      { get; set; }
    public bool     IsActive       { get; set; } = true;

    public DateTime  CreatedAt      { get; set; } = DateTime.UtcNow;
    public int?      CreatedBy      { get; set; }
    public string?   CreatedByName  { get; set; }
    public DateTime? ModifiedAt     { get; set; }
    public int?      ModifiedBy     { get; set; }
    public string?   ModifiedByName { get; set; }
}

/// <summary>C9 / D-7 — the per-line comparison of a supplier invoice (the invoice itself is the match header, D-CORE-5).</summary>
internal class InvoiceMatchLine : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     Uuid           { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId { get; set; }

    public int      InvoiceId      { get; set; }
    public Invoice  Invoice        { get; set; } = null!;
    /// <summary>The invoice line compared (no FK: invoice lines and match lines both cascade from the invoice).</summary>
    public Guid?    InvoiceLineUuid { get; set; }
    public Guid?    PoLineUuid     { get; set; }
    public Guid?    GrnLineUuid    { get; set; }
    public int      LineNo         { get; set; }
    public string   ItemDescription { get; set; } = string.Empty;

    public decimal? PoQuantity       { get; set; }
    public decimal? PoUnitPrice      { get; set; }
    public decimal? PoLineTotal      { get; set; }
    public decimal? GrnQuantity      { get; set; }
    public decimal  InvoiceQuantity  { get; set; }
    public decimal  InvoiceUnitPrice { get; set; }
    public decimal  InvoiceLineTotal { get; set; }
    /// <summary>Invoice qty − GRN qty (three-way) or − PO qty (two-way).</summary>
    public decimal? QuantityVariance     { get; set; }
    /// <summary>Invoice price − PO price.</summary>
    public decimal? PriceVariance        { get; set; }
    public decimal? PriceVariancePercent { get; set; }
    /// <summary>Invoice total − PO total.</summary>
    public decimal? TotalVariance        { get; set; }
    /// <summary><see cref="ArApEnums.MatchLineStatus"/>.</summary>
    public string   LineStatus     { get; set; } = ArApEnums.MatchLineStatus.Pending;
    public string?  Note           { get; set; }
    public DateTime CreatedAt      { get; set; } = DateTime.UtcNow;
}

/// <summary>C15 — "2/10 Net 30": 2% off when paid within 10 days, net due in 30.</summary>
internal class EarlyPaymentTerm : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     Uuid           { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId { get; set; }

    public string   Code           { get; set; } = string.Empty;
    public string   Name           { get; set; } = string.Empty;
    public decimal  DiscountPercent { get; set; }
    public int      DiscountDays   { get; set; }
    public int      NetDays        { get; set; }
    public bool     IsActive       { get; set; } = true;

    public DateTime  CreatedAt      { get; set; } = DateTime.UtcNow;
    public int?      CreatedBy      { get; set; }
    public string?   CreatedByName  { get; set; }
    public DateTime? ModifiedAt     { get; set; }
    public int?      ModifiedBy     { get; set; }
    public string?   ModifiedByName { get; set; }
}

/// <summary>C10 / D-8 — one row per approved supplier invoice (unique InvoiceId): when and how it is to be paid.</summary>
internal class PaymentSchedule : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     Uuid           { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId { get; set; }

    public int      InvoiceId      { get; set; }
    public Invoice  Invoice        { get; set; } = null!;
    public Guid     InvoiceUuid    { get; set; }
    public string   InvoiceNumber  { get; set; } = string.Empty;
    public string?  SupplierInvoiceNo { get; set; }
    /// <summary>The vendor (= Invoice.SupplierId, a BusinessPartner uuid).</summary>
    public Guid     VendorUuid     { get; set; }
    public string   VendorName     { get; set; } = string.Empty;
    public string   CurrencyCode   { get; set; } = "PKR";
    public DateOnly InvoiceDate    { get; set; }
    public DateOnly DueDate        { get; set; }
    public decimal  OriginalAmount  { get; set; }
    public decimal  AmountRemaining { get; set; }

    /// <summary><see cref="ArApEnums.PaymentPriority"/>.</summary>
    public string   PaymentPriority { get; set; } = ArApEnums.PaymentPriority.Normal;
    public string?  PaymentMethod   { get; set; }
    public bool     PaymentBlock    { get; set; }
    public string?  BlockReason     { get; set; }
    public int?     BlockedBy       { get; set; }
    public string?  BlockedByName   { get; set; }
    public DateTime? BlockedAt      { get; set; }

    public int?     EarlyPaymentTermId { get; set; }
    public EarlyPaymentTerm? EarlyPaymentTerm { get; set; }
    public DateOnly? DiscountDate    { get; set; }
    public decimal?  DiscountPercent { get; set; }
    public decimal?  DiscountAmount  { get; set; }

    /// <summary>The non-final payment run holding it (claimed at proposal, released on removal / cancel / failure).</summary>
    public int?     ReservedByRunId { get; set; }
    public PaymentRun? ReservedByRun { get; set; }
    /// <summary>The run that paid it.</summary>
    public int?     PaidInRunId     { get; set; }
    public PaymentRun? PaidInRun    { get; set; }
    /// <summary><see cref="ArApEnums.ScheduleStatus"/>.</summary>
    public string   Status          { get; set; } = ArApEnums.ScheduleStatus.Open;

    public DateTime  CreatedAt  { get; set; } = DateTime.UtcNow;
    public DateTime? ModifiedAt { get; set; }
    public byte[]    RowVersion { get; set; } = [];
}

/// <summary>C11 / D-9 — an automatic payment run.</summary>
internal class PaymentRun : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     Uuid           { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId { get; set; }

    /// <summary>PR-… from the document-number generator; unique per organization.</summary>
    public string   RunNumber      { get; set; } = string.Empty;
    public DateOnly RunDate        { get; set; }
    public DateOnly PaymentDate    { get; set; }
    public DateOnly DueDateCutoff  { get; set; }
    /// <summary><see cref="ArApEnums.PaymentRunStatus"/>.</summary>
    public string   Status         { get; set; } = ArApEnums.PaymentRunStatus.Draft;
    /// <summary>JSON of the proposal filters.</summary>
    public string?  SelectionCriteria { get; set; }
    public string?  PaymentMethod  { get; set; }
    /// <summary>Source bank account, free text (SupplierPayment.BankAccount is a string).</summary>
    public string?  BankAccount    { get; set; }
    public bool     IncludeDiscount { get; set; } = true;
    /// <summary>The purchase base currency the totals are in (D-13).</summary>
    public string?  BaseCurrencyCode { get; set; }
    public int      TotalVendors   { get; set; }
    public int      TotalItems     { get; set; }
    public decimal  TotalAmount    { get; set; }
    public decimal  TotalDiscount  { get; set; }
    public decimal  NetPaymentAmount { get; set; }
    public int      FailedBatchCount { get; set; }

    public DateTime  CreatedAt        { get; set; } = DateTime.UtcNow;
    public int?      ProposedBy       { get; set; }
    public string?   ProposedByName   { get; set; }
    public DateTime? ProposedAt       { get; set; }
    public int?      ApprovedBy       { get; set; }
    public string?   ApprovedByName   { get; set; }
    public DateTime? ApprovedAt       { get; set; }
    public int?      ExecutedBy       { get; set; }
    public string?   ExecutedByName   { get; set; }
    public DateTime? ExecutedAt       { get; set; }
    public DateTime? CompletedAt      { get; set; }
    public int?      CancelledBy      { get; set; }
    public string?   CancelledByName  { get; set; }
    public DateTime? CancelledAt      { get; set; }
    public string?   CancelReason     { get; set; }
    /// <summary>The last exported file's name (the file itself is generated on request, never stored).</summary>
    public string?   PaymentFileName  { get; set; }
    public DateTime? ExportedAt       { get; set; }
    public byte[]    RowVersion       { get; set; } = [];

    public ICollection<PaymentRunBatch> Batches { get; set; } = new List<PaymentRunBatch>();
    public ICollection<PaymentRunItem>  Items   { get; set; } = new List<PaymentRunItem>();
}

/// <summary>One vendor × currency × method of a run — executed as one SupplierPayment (D-9).</summary>
internal class PaymentRunBatch : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     Uuid           { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId { get; set; }

    public int      PaymentRunId   { get; set; }
    public PaymentRun PaymentRun   { get; set; } = null!;
    public Guid     VendorUuid     { get; set; }
    public string   VendorName     { get; set; } = string.Empty;
    public string   CurrencyCode   { get; set; } = "PKR";
    public string   PaymentMethod  { get; set; } = string.Empty;
    /// <summary>Masked only (e.g. "HBL ••••1234"). The real details are read at export time and never stored.</summary>
    public string?  BankDetailsMasked { get; set; }
    public int      ItemCount      { get; set; }
    public decimal  TotalAmount    { get; set; }
    public decimal  TotalDiscount  { get; set; }
    public decimal  NetAmount      { get; set; }
    public string?  PaymentReference { get; set; }
    public Guid?    SupplierPaymentUuid   { get; set; }
    public string?  SupplierPaymentNumber { get; set; }
    /// <summary><see cref="ArApEnums.PaymentRunBatchStatus"/>.</summary>
    public string   Status         { get; set; } = ArApEnums.PaymentRunBatchStatus.Pending;
    public string?  FailureReason  { get; set; }
    public DateTime? ExecutedAt    { get; set; }

    public ICollection<PaymentRunItem> Items { get; set; } = new List<PaymentRunItem>();
}

internal class PaymentRunItem : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     Uuid           { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId { get; set; }

    public int      PaymentRunId   { get; set; }
    public PaymentRun PaymentRun   { get; set; } = null!;
    public int?     BatchId        { get; set; }
    public PaymentRunBatch? Batch  { get; set; }
    public int      PaymentScheduleId { get; set; }
    public PaymentSchedule PaymentSchedule { get; set; } = null!;
    public Guid     InvoiceUuid    { get; set; }
    public string   InvoiceNumber  { get; set; } = string.Empty;
    public Guid     VendorUuid     { get; set; }
    public string   VendorName     { get; set; } = string.Empty;
    public string   CurrencyCode   { get; set; } = "PKR";
    public DateOnly DueDate        { get; set; }
    /// <summary>The amount still owed at proposal.</summary>
    public decimal  OriginalAmount { get; set; }
    public decimal  DiscountAmount { get; set; }
    public decimal  NetPaymentAmount { get; set; }
    public string?  PaymentMethod  { get; set; }
    /// <summary><see cref="ArApEnums.PaymentRunItemStatus"/>.</summary>
    public string   Status         { get; set; } = ArApEnums.PaymentRunItemStatus.Proposed;
    public string?  RemovedReason  { get; set; }
    public int?     RemovedBy      { get; set; }
    public string?  RemovedByName  { get; set; }
    public DateTime? RemovedAt     { get; set; }
    public DateTime? PaidAt        { get; set; }
}

/// <summary>D-6 (AR) — part of a customer credit note (a CREDIT_NOTE SalesInvoice) applied to an open invoice of the same customer.</summary>
internal class CustomerCreditApplication : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     Uuid           { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId { get; set; }

    public int      CreditNoteId   { get; set; }
    public SalesInvoice CreditNote { get; set; } = null!;
    public Guid     CreditNoteUuid { get; set; }
    public string   CreditNoteNumber { get; set; } = string.Empty;
    public int      InvoiceId      { get; set; }
    public SalesInvoice Invoice    { get; set; } = null!;
    public Guid     InvoiceUuid    { get; set; }
    public string   InvoiceNumber  { get; set; } = string.Empty;
    public Guid     PartnerUuid    { get; set; }
    public decimal  Amount         { get; set; }
    public string?  Notes          { get; set; }

    public DateTime  AppliedAt      { get; set; } = DateTime.UtcNow;
    public int?      AppliedBy      { get; set; }
    public string?   AppliedByName  { get; set; }
    public DateTime? ReversedAt     { get; set; }
    public int?      ReversedBy     { get; set; }
    public string?   ReversalReason { get; set; }
}

/// <summary>D-6 (AP) — part of a supplier debit note (or credit note) applied to a supplier invoice; one note, several invoices.</summary>
internal class SupplierNoteApplication : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     Uuid           { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId { get; set; }

    /// <summary><see cref="ArApEnums.SupplierNoteType"/>.</summary>
    public string   NoteType       { get; set; } = ArApEnums.SupplierNoteType.DebitNote;
    public Guid     NoteUuid       { get; set; }
    public string   NoteNumber     { get; set; } = string.Empty;
    public int      InvoiceId      { get; set; }
    public Invoice  Invoice        { get; set; } = null!;
    public Guid     InvoiceUuid    { get; set; }
    public string   InvoiceNumber  { get; set; } = string.Empty;
    public Guid     VendorUuid     { get; set; }
    public decimal  Amount         { get; set; }
    public string?  Notes          { get; set; }

    public DateTime  AppliedAt      { get; set; } = DateTime.UtcNow;
    public int?      AppliedBy      { get; set; }
    public string?   AppliedByName  { get; set; }
    public DateTime? ReversedAt     { get; set; }
    public int?      ReversedBy     { get; set; }
    public string?   ReversalReason { get; set; }
}

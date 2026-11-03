namespace SMS.Modules.Finance.ArAp.Reporting;

// A48 RPT — the read side of the AR / AP sub-ledgers (C3, C4, C6, C8, C12, C13). Partners are BusinessPartner uuids,
// amounts are decimal, "Base" amounts are in the organization's sale base (AR) or purchase base (AP) converted at each
// document's own locked rate (D-13). Every figure is computed from the SCM Finance documents (D-1).

// ── Aging (C3 / C8) ──────────────────────────────────────────────────────────

/// <summary>Query of <c>GET api/finance/ar/aging</c> / <c>ap/aging</c>.</summary>
public sealed class AgingReportRequest
{
    /// <summary>End of this day (UTC date). Default today.</summary>
    public DateOnly? AsOfDate { get; set; }
    /// <summary>DueDate (default — Current = not yet due) or DocumentDate.</summary>
    public string?   Basis         { get; set; }
    /// <summary>Positive ascending bucket boundaries in days, comma separated. Default 30,60,90,120. At least 2.</summary>
    public string?   Buckets       { get; set; }
    /// <summary>AR: one customer.</summary>
    public Guid?     CustomerUuid  { get; set; }
    /// <summary>AP: one vendor.</summary>
    public Guid?     VendorUuid    { get; set; }
    /// <summary>Only documents in this currency (amounts are still also given in base).</summary>
    public string?   CurrencyCode  { get; set; }
    /// <summary>Include the open documents behind each partner line.</summary>
    public bool      IncludeDetails { get; set; }
    /// <summary>Partner, TotalOutstanding (default), TotalOverdue, OldestBucket, OldestDate.</summary>
    public string?   SortBy        { get; set; }
    /// <summary>Asc / Desc (default).</summary>
    public string?   SortDirection { get; set; }
    public int       Page          { get; set; } = 1;
    public int       PageSize      { get; set; } = 25;
}

public sealed class AgingReport
{
    /// <summary>Receivable / Payable.</summary>
    public string           ReportType       { get; set; } = string.Empty;
    public DateOnly         AsOfDate             { get; set; }
    public string           Basis            { get; set; } = string.Empty;
    public int[]            BucketDays       { get; set; } = [];
    public string[]         BucketLabels     { get; set; } = [];
    public string           BaseCurrencyCode { get; set; } = string.Empty;
    public List<AgingPartnerLine> Lines      { get; set; } = [];
    public AgingTotals      Totals           { get; set; } = new();
    /// <summary>The same buckets per document currency, in that currency.</summary>
    public List<AgingCurrencyTotal> ByCurrency { get; set; } = [];
    /// <summary>DSO (receivables) or DPO (payables) as of the day, 365-day period.</summary>
    public decimal          DsoOrDpo         { get; set; }
    /// <summary>Σ open debit items, base.</summary>
    public decimal          TotalOutstanding { get; set; }
    public int              Page             { get; set; }
    public int              PageSize         { get; set; }
    public int              TotalRecords     { get; set; }
    public int              TotalPages       { get; set; }
    public DateTime         GeneratedAt      { get; set; }
}

public sealed class AgingPartnerLine
{
    public Guid      PartnerId        { get; set; }
    public string?   PartnerName      { get; set; }
    public string?   PartnerCode      { get; set; }
    /// <summary>One amount per bucket (base). Debit items only.</summary>
    public decimal[] BucketAmounts    { get; set; } = [];
    /// <summary>Σ BucketAmounts.</summary>
    public decimal   TotalOutstanding { get; set; }
    /// <summary>The part past its due date (base) — whatever the basis.</summary>
    public decimal   TotalOverdue     { get; set; }
    /// <summary>Unapplied payments, open credit notes, advances (base, positive).</summary>
    public decimal   UnappliedCredits { get; set; }
    /// <summary>TotalOutstanding − UnappliedCredits.</summary>
    public decimal   NetBalance       { get; set; }
    public int       OpenItemCount    { get; set; }
    public DateOnly? OldestOpenDate   { get; set; }
    /// <summary>Days the oldest overdue item is past its due date (null when nothing is overdue).</summary>
    public int?      DaysBeyondTerms  { get; set; }
    /// <summary>Index of the oldest bucket holding an amount (−1 none).</summary>
    public int       OldestBucket     { get; set; } = -1;
    public List<AgingDetailLine>? Details { get; set; }
}

public sealed class AgingDetailLine
{
    public string   DocumentType   { get; set; } = string.Empty;
    public Guid     DocumentUuid   { get; set; }
    public string   DocumentNumber { get; set; } = string.Empty;
    public string?  Reference      { get; set; }
    public Guid     PartnerId      { get; set; }
    public string?  PartnerName    { get; set; }
    public DateOnly DocumentDate   { get; set; }
    public DateOnly DueDate        { get; set; }
    public string   CurrencyCode   { get; set; } = string.Empty;
    public decimal  OriginalAmount { get; set; }
    /// <summary>What is still open on it (amount_residual), document currency.</summary>
    public decimal  OpenAmount     { get; set; }
    public decimal  OpenAmountBase { get; set; }
    public decimal  ExchangeRate   { get; set; }
    /// <summary>Days past the due date (0 when not due).</summary>
    public int      DaysOverdue    { get; set; }
    /// <summary>The age the bucket was chosen by (basis-dependent; negative = not due yet).</summary>
    public int      AgeDays        { get; set; }
    public int      BucketIndex    { get; set; }
    public string   BucketLabel    { get; set; } = string.Empty;
    public string?  Status         { get; set; }
    /// <summary>No locked rate and no current rate either: counted 1:1 (contract §0).</summary>
    public bool     RateMissing    { get; set; }
}

public sealed class AgingTotals
{
    public decimal[] BucketTotals     { get; set; } = [];
    public decimal   GrandTotal       { get; set; }
    public decimal   TotalOverdue     { get; set; }
    public decimal   UnappliedCredits { get; set; }
    public decimal   NetBalance       { get; set; }
    public int       TotalPartners    { get; set; }
    public int       TotalOpenItems   { get; set; }
}

public sealed class AgingCurrencyTotal
{
    public string    CurrencyCode  { get; set; } = string.Empty;
    public decimal[] BucketAmounts { get; set; } = [];
    public decimal   Total         { get; set; }
    public decimal   TotalBase     { get; set; }
    public int       OpenItemCount { get; set; }
}

/// <summary>Query of the aging drill-down (<c>aging/details</c>): one partner and/or one bucket, paged.</summary>
public sealed class AgingDetailsRequest
{
    public DateOnly? AsOfDate { get; set; }
    public string?   Basis       { get; set; }
    public string?   Buckets     { get; set; }
    public Guid?     CustomerUuid { get; set; }
    public Guid?     VendorUuid  { get; set; }
    /// <summary>0-based bucket index; null = every bucket.</summary>
    public int?      BucketIndex { get; set; }
    public string?   CurrencyCode { get; set; }
    public int       Page        { get; set; } = 1;
    public int       PageSize    { get; set; } = 25;
}


// ── DSO / DPO (D-12) ─────────────────────────────────────────────────────────

public sealed class DaysOutstandingResult
{
    /// <summary>DSO / DPO.</summary>
    public string   Measure          { get; set; } = string.Empty;
    public DateOnly AsOfDate             { get; set; }
    public int      PeriodDays       { get; set; }
    public string   BaseCurrencyCode { get; set; } = string.Empty;
    /// <summary>Net AR (AP) balance as of the day, base.</summary>
    public decimal  Balance          { get; set; }
    /// <summary>Credit sales (purchases = supplier invoices) in the period, base, net of credit notes.</summary>
    public decimal  PeriodAmount     { get; set; }
    /// <summary>Balance / PeriodAmount × PeriodDays, 1 dp; 0 when nothing was invoiced in the period.</summary>
    public decimal  Days             { get; set; }
}

// ── Balance & open items (C6 / C12) ──────────────────────────────────────────

public sealed class PartnerBalanceResult
{
    public Guid      PartnerId        { get; set; }
    public string?   PartnerName      { get; set; }
    public string    PartnerRole      { get; set; } = string.Empty;
    public DateOnly  AsOfDate             { get; set; }
    public string    BaseCurrencyCode { get; set; } = string.Empty;
    /// <summary>Σ open debit items (invoices), base.</summary>
    public decimal   TotalOpen        { get; set; }
    /// <summary>Σ open credit items (unapplied payments, credit notes, advances), base, positive.</summary>
    public decimal   TotalCredits     { get; set; }
    public decimal   NetBalance       { get; set; }
    public decimal   TotalOverdue     { get; set; }
    public int       OpenItemCount    { get; set; }
    public DateOnly? OldestOpenDate   { get; set; }
    public int?      DaysOldest       { get; set; }
    public List<PartnerCurrencyBalance> ByCurrency { get; set; } = [];
}

public sealed class PartnerCurrencyBalance
{
    public string  CurrencyCode { get; set; } = string.Empty;
    public decimal TotalOpen    { get; set; }
    public decimal TotalCredits { get; set; }
    public decimal NetBalance   { get; set; }
    public decimal NetBalanceBase { get; set; }
}

public sealed class OpenItemsFilter
{
    public DateOnly? AsOfDate { get; set; }
    public string?   CurrencyCode  { get; set; }
    public bool      OverdueOnly   { get; set; }
    /// <summary>Include credit items (unapplied payments, credit notes, advances). Default true.</summary>
    public bool      IncludeCredits { get; set; } = true;
    /// <summary>DueDate (default), DocumentDate, Amount, Number.</summary>
    public string?   SortBy        { get; set; }
    public string?   SortDirection { get; set; }
    public int       Page          { get; set; } = 1;
    public int       PageSize      { get; set; } = 25;
}

public sealed class OpenItemDto
{
    public string   DocumentType     { get; set; } = string.Empty;
    public Guid     DocumentUuid     { get; set; }
    public string   DocumentNumber   { get; set; } = string.Empty;
    public string?  Reference        { get; set; }
    public DateOnly DocumentDate     { get; set; }
    public DateOnly DueDate          { get; set; }
    public string   CurrencyCode     { get; set; } = string.Empty;
    public decimal  OriginalAmount   { get; set; }
    /// <summary>Signed: credit items are negative.</summary>
    public decimal  OpenAmount       { get; set; }
    public decimal  OpenAmountBase   { get; set; }
    public decimal  ExchangeRate     { get; set; }
    public int      DaysOutstanding  { get; set; }
    public int      DaysOverdue      { get; set; }
    public bool     IsOverdue        { get; set; }
    public bool     IsCredit         { get; set; }
    public string?  Status           { get; set; }
    public bool     RateMissing      { get; set; }
}

public sealed class PartnerTransactionFilter
{
    public DateOnly? From     { get; set; }
    public DateOnly? To       { get; set; }
    public string?   CurrencyCode { get; set; }
    public int       Page     { get; set; } = 1;
    public int       PageSize { get; set; } = 25;
}

public sealed class PartnerTransactionDto
{
    public int      SequenceNo      { get; set; }
    public DateTime EntryDate       { get; set; }
    public string   EntryType       { get; set; } = string.Empty;
    public string   ReferenceType   { get; set; } = string.Empty;
    public Guid     ReferenceId     { get; set; }
    public string   ReferenceNumber { get; set; } = string.Empty;
    public string?  Narration       { get; set; }
    public string   CurrencyCode    { get; set; } = string.Empty;
    public decimal  Debit           { get; set; }
    public decimal  Credit          { get; set; }
    /// <summary>Running balance in the entry's currency (opening before the filter + entries up to this one).</summary>
    public decimal  RunningBalance  { get; set; }
}


// ── Summary KPIs (C6 / C12) ──────────────────────────────────────────────────

public sealed class ArSummaryKpis
{
    public DateOnly AsOfDate                   { get; set; }
    public string   BaseCurrencyCode       { get; set; } = string.Empty;
    public int      PeriodDays             { get; set; }
    public decimal  TotalOutstanding       { get; set; }
    public decimal  TotalOverdue           { get; set; }
    public decimal  TotalCurrent           { get; set; }
    public decimal  UnappliedCredits       { get; set; }
    public decimal  NetReceivable          { get; set; }
    public int      TotalCustomersWithBalance { get; set; }
    public int      TotalCustomersOverdue  { get; set; }
    public decimal  Dso                    { get; set; }
    /// <summary>Amount-weighted average days past due of the overdue items.</summary>
    public decimal  DaysBeyondTerms        { get; set; }
    /// <summary>Payments received in the period ÷ invoiced in the period × 100 (0 when nothing invoiced).</summary>
    public decimal  CollectionRate         { get; set; }
    public decimal  InvoicedInPeriod       { get; set; }
    public decimal  CollectedInPeriod      { get; set; }
    public decimal[] AgingBuckets          { get; set; } = [];
    public string[]  AgingLabels           { get; set; } = [];
}

public sealed class ApSummaryKpis
{
    public DateOnly AsOfDate                   { get; set; }
    public string   BaseCurrencyCode       { get; set; } = string.Empty;
    public int      PeriodDays             { get; set; }
    public decimal  TotalOutstanding       { get; set; }
    public decimal  TotalOverdue           { get; set; }
    public decimal  TotalCurrent           { get; set; }
    /// <summary>Not overdue and due within 7 days of the as-of day.</summary>
    public decimal  TotalDueThisWeek       { get; set; }
    /// <summary>Not overdue and due within 30 days of the as-of day.</summary>
    public decimal  TotalDueThisMonth      { get; set; }
    public decimal  UnappliedCredits       { get; set; }
    public decimal  NetPayable             { get; set; }
    public int      TotalVendorsWithBalance { get; set; }
    public int      TotalVendorsOverdue    { get; set; }
    public decimal  Dpo                    { get; set; }
    /// <summary>Early-payment discounts still takeable (discount date ≥ as-of), base — from AP's discount source when registered.</summary>
    public decimal  DiscountOpportunityAmount { get; set; }
    public decimal[] AgingBuckets          { get; set; } = [];
    public string[]  AgingLabels           { get; set; } = [];
}

// ── Statements (C4 / C13) ────────────────────────────────────────────────────

public static class StatementTypes
{
    public const string Activity  = "Activity";
    public const string OpenItems = "OpenItems";
}

public sealed class StatementRequest
{
    /// <summary>Default: the first day of the To month.</summary>
    public DateOnly? From         { get; set; }
    /// <summary>Default: today.</summary>
    public DateOnly? To           { get; set; }
    /// <summary>Activity (default) or OpenItems.</summary>
    public string?   Type         { get; set; }
    /// <summary>Only this currency's section.</summary>
    public string?   CurrencyCode { get; set; }
    public bool      IncludeAging { get; set; } = true;
    /// <summary>Aging boundaries for the summary (default 30,60,90,120).</summary>
    public string?   Buckets      { get; set; }
    /// <summary>json (default) or pdf.</summary>
    public string?   Format       { get; set; }
}

public sealed class EmailStatementRequest
{
    public DateOnly? From         { get; set; }
    public DateOnly? To           { get; set; }
    public string?   Type         { get; set; }
    public string?   CurrencyCode { get; set; }
    public bool      IncludeAging { get; set; } = true;
    /// <summary>Overrides the partner's email on file.</summary>
    public string?   EmailTo      { get; set; }
    public string?   Subject      { get; set; }
    public string?   Message      { get; set; }
}

public sealed class BatchStatementRequest
{
    public DateOnly? From           { get; set; }
    public DateOnly? To             { get; set; }
    public string?   Type           { get; set; }
    /// <summary>Only partners whose closing balance (base) is at least this (BR-ST-005). Null = any non-zero balance.</summary>
    public decimal?  MinimumBalance { get; set; }
    public bool      SendEmail      { get; set; }
    public bool      IncludeAging   { get; set; } = true;
    /// <summary>Limit to these partners (else every partner with a balance or activity).</summary>
    public List<Guid>? PartnerIds   { get; set; }
}

public sealed class StatementResult
{
    /// <summary>The StatementLog row written for this generation (null when the log is unavailable).</summary>
    public Guid?     StatementId      { get; set; }
    /// <summary>Customer / Vendor.</summary>
    public string    PartnerType      { get; set; } = string.Empty;
    public Guid      PartnerId        { get; set; }
    public string?   PartnerName      { get; set; }
    public string?   PartnerCode      { get; set; }
    public string?   PartnerEmail     { get; set; }
    public string?   PartnerAddress   { get; set; }
    public string?   CompanyName      { get; set; }
    public DateOnly  FromDate         { get; set; }
    public DateOnly  ToDate           { get; set; }
    public string    Type             { get; set; } = string.Empty;
    public string    BaseCurrencyCode { get; set; } = string.Empty;
    public List<StatementCurrencySection> Sections { get; set; } = [];
    /// <summary>Net open items (debits − credits) as of the day before From, base at the documents' rates. 0 for OpenItems.</summary>
    public decimal   OpeningBalanceBase { get; set; }
    /// <summary>Net open items as of To, base at the documents' rates — what the batch minimum is compared with.</summary>
    public decimal   ClosingBalanceBase { get; set; }
    public int       LineCount        { get; set; }
    public string[]  AgingLabels      { get; set; } = [];
    public DateTime  GeneratedAt      { get; set; }
}

public sealed class StatementCurrencySection
{
    public string   CurrencyCode   { get; set; } = string.Empty;
    public decimal  OpeningBalance { get; set; }
    public decimal  TotalDebits    { get; set; }
    public decimal  TotalCredits   { get; set; }
    public decimal  ClosingBalance { get; set; }
    public List<StatementLine> Lines { get; set; } = [];
    /// <summary>The open items as of ToDate per bucket (due-date basis), in this currency. Null when not asked.</summary>
    public decimal[]? Aging        { get; set; }
}

public sealed class StatementLine
{
    public DateOnly  Date           { get; set; }
    public string    DocumentType   { get; set; } = string.Empty;
    public string    EntryNumber    { get; set; } = string.Empty;
    public string?   Reference      { get; set; }
    public string    Description    { get; set; } = string.Empty;
    public DateOnly? DueDate        { get; set; }
    public decimal?  Debit          { get; set; }
    public decimal?  Credit         { get; set; }
    public decimal   RunningBalance { get; set; }
    /// <summary>Open / Partially Paid / Paid (documents), Applied / Unapplied (credits), or null for other entries.</summary>
    public string?   Status         { get; set; }
}

public sealed class StatementEmailResult
{
    public Guid?    StatementId { get; set; }
    public string   EmailedTo   { get; set; } = string.Empty;
    public DateTime EmailedAt   { get; set; }
    public decimal  ClosingBalanceBase { get; set; }
}

public sealed class BatchStatementResult
{
    public int Considered { get; set; }
    public int Generated  { get; set; }
    public int Emailed    { get; set; }
    /// <summary>Below the minimum balance.</summary>
    public int Skipped    { get; set; }
    /// <summary>Asked to email but no address on file.</summary>
    public int NoEmail    { get; set; }
    public int Failed     { get; set; }
    public List<BatchStatementItem> Items { get; set; } = [];
}

public sealed class BatchStatementItem
{
    public Guid     PartnerId          { get; set; }
    public string?  PartnerName        { get; set; }
    public Guid?    StatementId        { get; set; }
    public decimal  ClosingBalanceBase { get; set; }
    public bool     Emailed            { get; set; }
    public string?  EmailedTo          { get; set; }
    /// <summary>GENERATED / EMAILED / NO_EMAIL / FAILED.</summary>
    public string   Outcome            { get; set; } = string.Empty;
    public string?  Error              { get; set; }
}

/// <summary>One <c>statement_log</c> row as <c>GET api/finance/statements/log</c> lists it.</summary>
public sealed class StatementLogModel
{
    public Guid      Uuid           { get; set; }
    public Guid      PartnerUuid    { get; set; }
    public string?   PartnerName    { get; set; }
    public string    PartnerRole    { get; set; } = string.Empty;
    public string    StatementType  { get; set; } = string.Empty;
    public DateOnly  FromDate       { get; set; }
    public DateOnly  ToDate         { get; set; }
    public string?   CurrencyCode   { get; set; }
    public decimal   OpeningBalance { get; set; }
    public decimal   ClosingBalance { get; set; }
    public int       LineCount      { get; set; }
    public string?   Format         { get; set; }
    public string    EmailStatus    { get; set; } = string.Empty;
    public string?   EmailedTo      { get; set; }
    public DateTime? EmailedAt      { get; set; }
    public string?   EmailError     { get; set; }
    public Guid?     BatchUuid      { get; set; }
    public DateTime  GeneratedAt    { get; set; }
    public string?   GeneratedByName { get; set; }
}

public sealed class StatementLogFilter
{
    /// <summary>Customer / Vendor (required).</summary>
    public string?   Role        { get; set; }
    public Guid?     PartnerUuid { get; set; }
    public int       Page        { get; set; } = 1;
    public int       PageSize    { get; set; } = 25;
}

// ── Vendor reconciliation (C13) ──────────────────────────────────────────────

public sealed class VendorReconciliationRequest
{
    public DateOnly? AsOfDate { get; set; }
    public decimal   VendorClaimedBalance { get; set; }
    /// <summary>The currency the vendor states in (default: the vendor's only open currency, else the purchase base).</summary>
    public string?   CurrencyCode         { get; set; }
    /// <summary>The open items on the vendor's statement (optional). Credits (payments, credit notes) negative.</summary>
    public List<VendorClaimedItem>? VendorItems { get; set; }
    /// <summary>Differences up to this are treated as equal (default 0.01).</summary>
    public decimal?  Tolerance            { get; set; }
}

public sealed class VendorClaimedItem
{
    public string    Reference { get; set; } = string.Empty;
    public DateOnly? Date      { get; set; }
    public decimal   Amount    { get; set; }
}

public sealed class VendorReconciliationResult
{
    public Guid      VendorId             { get; set; }
    public string?   VendorName           { get; set; }
    public DateOnly  AsOfDate                 { get; set; }
    public string    CurrencyCode         { get; set; } = string.Empty;
    public decimal   SmsBalance           { get; set; }
    public decimal   VendorClaimedBalance { get; set; }
    /// <summary>SmsBalance − VendorClaimedBalance.</summary>
    public decimal   Difference           { get; set; }
    public int       MatchedCount         { get; set; }
    public List<ReconciliationDifference> Differences { get; set; } = [];
    public bool      IsReconciled         { get; set; }
}

public static class ReconciliationDifferenceTypes
{
    public const string InSmsNotVendor   = "InSMSNotVendor";
    public const string InVendorNotSms   = "InVendorNotSMS";
    public const string AmountDifference = "AmountDifference";
}

public sealed class ReconciliationDifference
{
    public string    Type         { get; set; } = string.Empty;
    public string?   DocumentType { get; set; }
    public string?   EntryNumber  { get; set; }
    public string?   Reference    { get; set; }
    public DateOnly? Date         { get; set; }
    public decimal?  SmsAmount    { get; set; }
    public decimal?  VendorAmount { get; set; }
    public decimal?  Difference   { get; set; }
}


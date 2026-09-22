namespace SMS.Modules.Reports.Models;

// A29-P9-02 / P9-03 §15 — the receivables reports: R2 the customer ledger and R3 aging receivables.
// Both read Finance's receivables books (A29 §9 and §10) and both keep every figure per currency,
// because there is no exchange rate to add one currency to another with.

// ── R2 Customer ledger ───────────────────────────────────────────────────────

/// <summary>
/// Whose ledger, and over what days. The customer is required: a ledger is one customer's account, and
/// a running balance is not meaningful across customers. The range is in whole days and inclusive at
/// both ends, as in the sales order register; leave an end out to leave it open.
/// </summary>
public class CustomerLedgerReportFilter
{
    /// <summary>The customer — a business partner's UUID.</summary>
    public Guid?     PartnerId { get; set; }
    public DateTime? DateFrom  { get; set; }
    public DateTime? DateTo    { get; set; }
    public int       Page      { get; set; } = 1;
    /// <summary>Clamped to 1–100. The PDF and Excel exports ignore paging and carry every entry.</summary>
    public int       PageSize  { get; set; } = 20;
}

/// <summary>One line of the account. <see cref="Balance"/> is what the customer owed in this currency after it.</summary>
public class CustomerLedgerReportEntry
{
    /// <summary>The entry's place in the customer's ledger, in the order it was posted.</summary>
    public int      SequenceNo      { get; set; }
    /// <summary>The business date: for a back-dated receipt, the day the money came in.</summary>
    public DateTime EntryDate       { get; set; }
    /// <summary>INVOICE, PAYMENT, CREDIT_NOTE, DEBIT_NOTE, ADVANCE, REFUND or OPENING_BAL.</summary>
    public string   EntryType       { get; set; } = string.Empty;
    public string   ReferenceType   { get; set; } = string.Empty;
    public Guid     ReferenceId     { get; set; }
    public string   ReferenceNumber { get; set; } = string.Empty;
    public string?  Narration       { get; set; }
    public string   CurrencyCode    { get; set; } = string.Empty;
    /// <summary>Increases what the customer owes.</summary>
    public decimal  DebitAmount     { get; set; }
    /// <summary>Decreases it.</summary>
    public decimal  CreditAmount    { get; set; }
    /// <summary>
    /// The account's balance in this entry's currency after it, counting the customer's entries by
    /// business date: the opening balance, plus every debit and less every credit up to and including
    /// this line. Negative means the customer is in credit.
    /// </summary>
    public decimal  Balance         { get; set; }
}

/// <summary>What one currency of the account did over the range. <c>Opening + TotalDebit − TotalCredit = Closing</c>.</summary>
public class CustomerLedgerReportSummary
{
    public string  CurrencyCode   { get; set; } = string.Empty;
    /// <summary>What the customer owed at the start of the first day of the range; nothing when the range has no start.</summary>
    public decimal OpeningBalance { get; set; }
    public decimal TotalDebit     { get; set; }
    public decimal TotalCredit    { get; set; }
    public decimal ClosingBalance { get; set; }
    public int     EntryCount     { get; set; }
}

public class CustomerLedgerReportCriteria
{
    public Guid?     PartnerId    { get; set; }
    public string?   CustomerName { get; set; }
    public DateTime? DateFrom     { get; set; }
    public DateTime? DateTo       { get; set; }
}

public class CustomerLedgerReport
{
    public string?   CompanyName { get; set; }
    public DateTime  GeneratedAt { get; set; }
    public CustomerLedgerReportCriteria Criteria { get; set; } = new();

    /// <summary>Per currency, over every entry in the range and not only this page.</summary>
    public List<CustomerLedgerReportSummary> Summaries { get; set; } = [];

    /// <summary>Oldest first, by business date and then by posting order — an account statement, read downwards.</summary>
    public List<CustomerLedgerReportEntry> Entries { get; set; } = [];

    /// <summary>Every entry in the range, not only those on this page.</summary>
    public int TotalRecords { get; set; }
    public int Page         { get; set; }
    public int PageSize     { get; set; }
    public int TotalPages   { get; set; }
}

// ── R3 Aging receivables ─────────────────────────────────────────────────────

/// <summary>
/// Which day to age the receivables as of, and whose. <c>AsOf</c> defaults to today and is a whole day:
/// the books are read as they stood at the end of it, so an invoice issued or a payment received on that
/// day counts. Leave the customer out for every customer.
/// </summary>
public class AgingReceivablesFilter
{
    public DateTime? AsOf      { get; set; }
    /// <summary>The customer — a business partner's UUID.</summary>
    public Guid?     PartnerId { get; set; }
    public int       Page      { get; set; } = 1;
    /// <summary>Clamped to 1–100. The PDF and Excel exports ignore paging and carry every invoice.</summary>
    public int       PageSize  { get; set; } = 20;
}

/// <summary>The four age buckets, by days past the invoice's due date.</summary>
public static class AgingBuckets
{
    /// <summary>Not yet due, or up to 30 days past due.</summary>
    public const string Days0To30   = "0-30";
    public const string Days31To60  = "31-60";
    public const string Days61To90  = "61-90";
    /// <summary>More than 90 days past due.</summary>
    public const string Over90      = "90+";

    public static readonly IReadOnlyList<string> All = [Days0To30, Days31To60, Days61To90, Over90];

    public static string For(int daysPastDue) => daysPastDue switch
    {
        <= 30 => Days0To30,
        <= 60 => Days31To60,
        <= 90 => Days61To90,
        _     => Over90
    };
}

/// <summary>One invoice that was still owed at the as-of date.</summary>
public class AgingReceivablesInvoice
{
    public Guid     InvoiceUuid     { get; set; }
    public string   InvoiceNumber   { get; set; } = string.Empty;
    public string   SaleOrderNumber { get; set; } = string.Empty;
    public Guid     PartnerId       { get; set; }
    /// <summary>Null when the customer's record cannot be found.</summary>
    public string?  CustomerName    { get; set; }
    public string   CurrencyCode    { get; set; } = string.Empty;
    public DateTime InvoiceDate     { get; set; }
    public DateTime DueDate         { get; set; }
    /// <summary>Whole days from the due date to the as-of date; 0 for an invoice that is not yet due.</summary>
    public int      DaysPastDue     { get; set; }
    /// <summary>See <see cref="AgingBuckets"/>.</summary>
    public string   Bucket          { get; set; } = string.Empty;
    public decimal  GrandTotal      { get; set; }
    /// <summary>What had been applied to it by the as-of date.</summary>
    public decimal  AmountPaid      { get; set; }
    /// <summary><c>GrandTotal − AmountPaid</c>: what was owed on it at the as-of date.</summary>
    public decimal  Outstanding     { get; set; }
}

/// <summary>What is owed in one currency, by bucket. <c>Total</c> is the four buckets added up.</summary>
public class AgingReceivablesTotal
{
    public string  CurrencyCode  { get; set; } = string.Empty;
    public int     InvoiceCount  { get; set; }
    public decimal Days0To30     { get; set; }
    public decimal Days31To60    { get; set; }
    public decimal Days61To90    { get; set; }
    public decimal Over90        { get; set; }
    public decimal Total         { get; set; }
}

/// <summary>What one customer owes in one currency, by bucket.</summary>
public class AgingReceivablesCustomer
{
    public Guid    PartnerId     { get; set; }
    public string? CustomerName  { get; set; }
    public string  CurrencyCode  { get; set; } = string.Empty;
    public int     InvoiceCount  { get; set; }
    public decimal Days0To30     { get; set; }
    public decimal Days31To60    { get; set; }
    public decimal Days61To90    { get; set; }
    public decimal Over90        { get; set; }
    public decimal Total         { get; set; }
}

public class AgingReceivablesCriteria
{
    public DateTime AsOf         { get; set; }
    public Guid?    PartnerId    { get; set; }
    public string?  CustomerName { get; set; }
}

public class AgingReceivablesReport
{
    public string?   CompanyName { get; set; }
    public DateTime  GeneratedAt { get; set; }
    public AgingReceivablesCriteria Criteria { get; set; } = new();

    /// <summary>One row per currency, over every outstanding invoice and not only this page.</summary>
    public List<AgingReceivablesTotal> Totals { get; set; } = [];

    /// <summary>One row per customer and currency, over every outstanding invoice; customers by name.</summary>
    public List<AgingReceivablesCustomer> Customers { get; set; } = [];

    /// <summary>The outstanding invoices, a customer at a time and the longest overdue first within each.</summary>
    public List<AgingReceivablesInvoice> Invoices { get; set; } = [];

    /// <summary>Every outstanding invoice, not only those on this page.</summary>
    public int TotalRecords { get; set; }
    public int Page         { get; set; }
    public int PageSize     { get; set; }
    public int TotalPages   { get; set; }
}

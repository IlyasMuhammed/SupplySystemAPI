using SMS.Shared.Pagination;

namespace SMS.Modules.Accounting.Models;

// A47 C9 — trial balance, account balance, account ledger (docs/journal-engine/API-CONTRACT.md §5). Every amount is in the
// base (reporting) currency (A-9: JournalEntryLines.DebitAmount/CreditAmount). Balances are signed debit − credit
// (positive = debit balance); the chart's CurrentBalance is in the account's normal direction — not mixed here.

/// <summary>GET api/finance/trial-balance (query string) — dates, or a fiscal year (or, RPT extra, a fiscal period).</summary>
public sealed class TrialBalanceQuery
{
    public DateOnly? FromDate               { get; set; }
    public DateOnly? ToDate                 { get; set; }
    /// <summary>The year's dates when no dates are given.</summary>
    public Guid?     FiscalYearUuid         { get; set; }
    /// <summary>RPT extra: the period's dates when no dates are given (wins over the year).</summary>
    public Guid?     FiscalPeriodUuid       { get; set; }
    /// <summary>BR-TB-003 — opening = Σ before fromDate. False: opening 0 and closing = period only.</summary>
    public bool      IncludeOpeningBalances { get; set; } = true;
    /// <summary>BR-TB-004 — closing entries (isClosingEntry) are left out unless true.</summary>
    public bool      IncludeClosingEntries  { get; set; }
    /// <summary>BR-TB-005 — accounts without activity are hidden unless true (then every active leaf account is listed).</summary>
    public bool      IncludeZeroBalances    { get; set; }
    /// <summary>Only lines with this cost center / project (task 332).</summary>
    public string?   CostCenter             { get; set; }
    public string?   Project                { get; set; }
}

public sealed class TrialBalanceReportModel
{
    public DateOnly  FromDate               { get; set; }
    public DateOnly  ToDate                 { get; set; }
    /// <summary>The fiscal year asked for (or the one both dates fall in).</summary>
    public FiscalYearRefModel? FiscalYear   { get; set; }
    public DateTime  GeneratedAt            { get; set; }
    public string?   CurrencyCode           { get; set; }
    public bool      IncludeOpeningBalances { get; set; }
    public bool      IncludeClosingEntries  { get; set; }
    public List<TrialBalanceLineModel> Lines { get; set; } = [];
    public TrialBalanceTotalsModel Totals   { get; set; } = new();
    /// <summary>= totals.closingDebit / closingCredit.</summary>
    public decimal   TotalDebit             { get; set; }
    public decimal   TotalCredit            { get; set; }
    /// <summary>BR-TB-002 — |totalDebit − totalCredit| &lt; 0.01 (an assertion: an unbalanced report is still returned).</summary>
    public bool      IsBalanced             { get; set; }
}

public sealed class TrialBalanceTotalsModel
{
    public decimal OpeningDebit  { get; set; }
    public decimal OpeningCredit { get; set; }
    public decimal PeriodDebit   { get; set; }
    public decimal PeriodCredit  { get; set; }
    public decimal ClosingDebit  { get; set; }
    public decimal ClosingCredit { get; set; }
}

public sealed class TrialBalanceLineModel
{
    public AccountRefModel Account  { get; set; } = new();
    public string    RootType       { get; set; } = string.Empty;
    public string?   AccountType    { get; set; }
    public int       Level          { get; set; }
    public decimal   OpeningDebit   { get; set; }
    public decimal   OpeningCredit  { get; set; }
    public decimal   PeriodDebit    { get; set; }
    public decimal   PeriodCredit   { get; set; }
    /// <summary>Opening + period (gross).</summary>
    public decimal   ClosingDebit   { get; set; }
    public decimal   ClosingCredit  { get; set; }
    /// <summary>closingDebit − closingCredit (+ = debit).</summary>
    public decimal   Balance        { get; set; }
    /// <summary>RPT extra: an inactive account with activity is still listed (else the report would not balance).</summary>
    public bool      IsActive       { get; set; }
}

/// <summary>GET …/accounts/{accountUuid}/balance — TS-TB-004.</summary>
public sealed class AccountBalanceModel
{
    public AccountRefModel Account  { get; set; } = new();
    public DateOnly  AsOfDate       { get; set; }
    /// <summary>Σ posted debits / credits dated on or before the date.</summary>
    public decimal   Debit          { get; set; }
    public decimal   Credit         { get; set; }
    /// <summary>debit − credit (+ = debit).</summary>
    public decimal   Balance        { get; set; }
}

/// <summary>GET …/accounts/{accountUuid}/ledger (query string).</summary>
public sealed class AccountLedgerQuery
{
    public DateOnly? FromDate              { get; set; }
    public DateOnly? ToDate                { get; set; }
    /// <summary>RPT extra — default true (every posting); the trial balance drill-down passes its own flag.</summary>
    public bool      IncludeClosingEntries { get; set; } = true;
    public int       Page                  { get; set; } = 1;
    /// <summary>1–500, default 50.</summary>
    public int       PageSize              { get; set; } = 50;
}

public sealed class AccountLedgerModel
{
    public AccountRefModel Account  { get; set; } = new();
    public DateOnly  FromDate       { get; set; }
    public DateOnly  ToDate         { get; set; }
    /// <summary>debit − credit of everything before fromDate.</summary>
    public decimal   OpeningBalance { get; set; }
    public decimal   ClosingBalance { get; set; }
    /// <summary>Σ of the whole range (all pages).</summary>
    public decimal   TotalDebit     { get; set; }
    public decimal   TotalCredit    { get; set; }
    public PaginatedResponse<LedgerLineModel> Page { get; set; } = new();
}

public sealed class LedgerLineModel
{
    public Guid      LineUuid        { get; set; }
    public JournalEntryRefModel Entry { get; set; } = new();
    public DateOnly  EntryDate       { get; set; }
    public string    JournalTypeCode { get; set; } = string.Empty;
    /// <summary>Posted | Reversed.</summary>
    public string    Status          { get; set; } = string.Empty;
    public string?   Reference       { get; set; }
    /// <summary>The line's memo, else the entry's description.</summary>
    public string?   Description     { get; set; }
    public int       LineNumber      { get; set; }
    public string?   PartnerName     { get; set; }
    public decimal   Debit           { get; set; }
    public decimal   Credit          { get; set; }
    /// <summary>openingBalance + Σ up to and including this line (debit − credit).</summary>
    public decimal   RunningBalance  { get; set; }
    public bool      Reconciled      { get; set; }
    /// <summary>RPT extra.</summary>
    public bool      IsClosingEntry  { get; set; }
}

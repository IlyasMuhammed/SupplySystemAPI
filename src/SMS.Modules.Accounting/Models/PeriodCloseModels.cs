namespace SMS.Modules.Accounting.Models;

// A47 C10 — period close and year-end close (docs/journal-engine/API-CONTRACT.md §6). Uuids only; amounts in the base currency.

public sealed class PeriodCloseFiscalYearModel
{
    public Guid      Uuid           { get; set; }
    public string    FiscalYearName { get; set; } = string.Empty;
    public DateOnly  StartDate      { get; set; }
    public DateOnly  EndDate        { get; set; }
    public bool      IsClosed       { get; set; }
    public DateTime? ClosedAt       { get; set; }
}

/// <summary>GET api/finance/period-close/{fiscalYearUuid}/status.</summary>
public sealed class PeriodCloseOverviewModel
{
    public PeriodCloseFiscalYearModel FiscalYear { get; set; } = new();
    public List<PeriodCloseStatusModel> Periods { get; set; } = [];
    /// <summary>The year-end closing entry, once the year-end close ran.</summary>
    public JournalEntryRefModel? ClosingEntry { get; set; }
    /// <summary>The year-end close's pre-checks pass (permissions aside).</summary>
    public bool      CanCloseYear       { get; set; }
    /// <summary>The PC texts execute would answer (empty when it can run).</summary>
    public List<string> YearCloseBlockers { get; set; } = [];
    /// <summary>RPT extra: the DEFAULT_RETAINED_EARNINGS account (the year-end close's default), if assigned.</summary>
    public AccountRefModel? DefaultRetainedEarningsAccount { get; set; }
}

public sealed class PeriodCloseStatusModel
{
    public Guid      Uuid             { get; set; }
    public int       PeriodNumber     { get; set; }
    public string    PeriodName       { get; set; } = string.Empty;
    public DateOnly  StartDate        { get; set; }
    public DateOnly  EndDate          { get; set; }
    /// <summary>Open | AdjustmentOnly | Locked | Closed.</summary>
    public string    Status           { get; set; } = string.Empty;
    /// <summary>Posted + Reversed entries of the period.</summary>
    public int       PostedEntryCount { get; set; }
    public int       DraftEntryCount  { get; set; }
    public bool      HasUnpostedDrafts { get; set; }
    /// <summary>Lock | AdjustmentOnly | Reopen | FinalClose — by the period's state and the caller's permissions.</summary>
    public List<string> AllowedActions { get; set; } = [];
}

/// <summary>The actions of <see cref="PeriodCloseStatusModel.AllowedActions"/>.</summary>
public static class PeriodCloseActions
{
    public const string Lock = "Lock", AdjustmentOnly = "AdjustmentOnly", Reopen = "Reopen", FinalClose = "FinalClose";
}

/// <summary>POST api/finance/period-close/{periodUuid}/close.</summary>
public sealed class ClosePeriodRequest
{
    /// <summary>Locked | AdjustmentOnly (default Locked).</summary>
    public string? TargetStatus { get; set; }
}

public sealed class PeriodCloseResultModel
{
    public PeriodCloseStatusModel Period { get; set; } = new();
    public string   OldStatus { get; set; } = string.Empty;
    public string   NewStatus { get; set; } = string.Empty;
}

/// <summary>POST api/finance/year-end-close/{fiscalYearUuid}/execute.</summary>
public sealed class YearEndCloseRequest
{
    /// <summary>Null = DEFAULT_RETAINED_EARNINGS. An active level-4 Equity account (BR-PC-008).</summary>
    public Guid? RetainedEarningsAccountUuid { get; set; }
}

/// <summary>GET api/finance/year-end-close/{fiscalYearUuid}/preview — TS-PC-009.</summary>
public sealed class YearEndPreviewModel
{
    public PeriodCloseFiscalYearModel FiscalYear { get; set; } = new();
    public AccountRefModel? RetainedEarningsAccount { get; set; }
    /// <summary>The closing entry's date (the year's last day).</summary>
    public DateOnly  EntryDate        { get; set; }
    /// <summary>13, or 12 when the year has no period 13.</summary>
    public int       PeriodNumber     { get; set; }
    public decimal   TotalRevenue     { get; set; }
    public decimal   TotalExpenses    { get; set; }
    /// <summary>Revenue − expenses (&gt; 0 profit: retained earnings credited; &lt; 0 loss: debited).</summary>
    public decimal   NetIncome        { get; set; }
    public List<YearEndPreviewLineModel> Lines { get; set; } = [];
    public bool      CanExecute       { get; set; }
    public List<string> Blockers      { get; set; } = [];
    /// <summary>RPT extra: the existing closing entry when the year was closed by the year-end close already.</summary>
    public JournalEntryRefModel? ExistingClosingEntry { get; set; }
}

public sealed class YearEndPreviewLineModel
{
    public AccountRefModel Account   { get; set; } = new();
    /// <summary>Revenue | Expense | Equity (the retained earnings line).</summary>
    public string    RootType        { get; set; } = string.Empty;
    /// <summary>Natural balance in the year: revenue credit − debit, expense debit − credit; 0 on the retained earnings line.</summary>
    public decimal   Balance         { get; set; }
    public decimal   ClosingDebit    { get; set; }
    public decimal   ClosingCredit   { get; set; }
}

public sealed class YearEndCloseResultModel
{
    public PeriodCloseFiscalYearModel FiscalYear { get; set; } = new();
    /// <summary>Null when the year had no Revenue/Expense balance (nothing to transfer).</summary>
    public JournalEntryRefModel? ClosingEntry { get; set; }
    public decimal   TotalRevenueClosing  { get; set; }
    public decimal   TotalExpenseClosing  { get; set; }
    /// <summary>Revenue − expenses transferred to retained earnings.</summary>
    public decimal   NetIncomeTransferred { get; set; }
    /// <summary>TS-PC-008 — the year was closed already: this is the existing result.</summary>
    public bool      AlreadyClosed        { get; set; }
    /// <summary>RPT extra: every closing entry (more than one only past 499 Revenue/Expense accounts, D-RPT-7).</summary>
    public List<JournalEntryRefModel> ClosingEntries { get; set; } = [];
    /// <summary>RPT extra.</summary>
    public AccountRefModel? RetainedEarningsAccount { get; set; }
}

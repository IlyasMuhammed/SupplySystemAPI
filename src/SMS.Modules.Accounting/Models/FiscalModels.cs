namespace SMS.Modules.Accounting.Models;

// A42 API-CONTRACT "Fiscal years & periods" (BE2). Dates are DateOnly (yyyy-MM-dd in JSON).

public sealed class FiscalYearModel
{
    public Guid     Uuid                  { get; set; }
    public string   FiscalYearName        { get; set; } = string.Empty;
    public DateOnly StartDate             { get; set; }
    public DateOnly EndDate               { get; set; }
    public bool     IsClosed              { get; set; }
    public DateTime? ClosedAt             { get; set; }
    public string   ReportingCurrencyCode { get; set; } = string.Empty;
    /// <summary>A posted journal entry is dated inside the year (D-50: dates are then frozen).</summary>
    public bool     HasPostings           { get; set; }
    public List<FiscalPeriodModel> Periods { get; set; } = [];
}

public sealed class FiscalPeriodModel
{
    public Guid     Uuid         { get; set; }
    public int      PeriodNumber { get; set; }
    public string   PeriodName   { get; set; } = string.Empty;
    public DateOnly StartDate    { get; set; }
    public DateOnly EndDate      { get; set; }
    public string   Status       { get; set; } = string.Empty;
    /// <summary>Period 13 — the year-end adjustment period (shares the last day of the year).</summary>
    public bool     IsAdjustment { get; set; }
}

public sealed class CreateFiscalYearRequest
{
    public string?   FiscalYearName        { get; set; }
    public DateOnly  StartDate             { get; set; }
    public DateOnly  EndDate               { get; set; }
    /// <summary>Null/empty = the organization's (sale) base currency (D-13).</summary>
    public string?   ReportingCurrencyCode { get; set; }
}

public sealed class UpdateFiscalYearRequest
{
    public string?   FiscalYearName        { get; set; }
    /// <summary>Null/empty = keep.</summary>
    public string?   ReportingCurrencyCode { get; set; }
    public DateOnly? StartDate             { get; set; }
    public DateOnly? EndDate               { get; set; }
}

public sealed class UpdateFiscalPeriodRequest
{
    public string?   Status     { get; set; }
    public string?   PeriodName { get; set; }
    public DateOnly? StartDate  { get; set; }
    public DateOnly? EndDate    { get; set; }
}

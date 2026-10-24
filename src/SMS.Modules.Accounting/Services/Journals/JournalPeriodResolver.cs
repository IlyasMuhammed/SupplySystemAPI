using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;

namespace SMS.Modules.Accounting.Services;

/// <summary>Where a journal dated <c>date</c> goes, or why it may not (error code + the spec's text).</summary>
internal sealed record PeriodResolution(int? FiscalYearId, int? FiscalPeriodId, string? ErrorCode, string? Message)
{
    public bool Allowed => ErrorCode is null;
}

/// <summary>
/// A47 D-11 — the posting-period rule of the journal engine and of <see cref="SMS.Shared.Common.IFiscalPeriodGuard"/>:
/// <list type="bullet">
/// <item>the date must fall in a period (JE-005 "No fiscal period covers this date"), of a fiscal year that is not closed
/// (JE-006 "Fiscal year is closed", BR-PC-007);</item>
/// <item>Open accepts every entry; AdjustmentOnly only an adjustment entry — <c>IsOpeningEntry</c> or <c>IsClosingEntry</c>
/// (JE-008, BR-JE-005); Locked (JE-007) and Closed (JE-006) nothing.</item>
/// </list>
/// The year's last day lies in period 12 and period 13 (A42): the entry goes to the first period that accepts it — period
/// 1–12 first for a regular entry, period 13 first for a closing entry (D-18) — and the refusal names the preferred one.
/// </summary>
internal sealed class JournalPeriodResolver
{
    private readonly AccountingDbContext _db;

    public JournalPeriodResolver(AccountingDbContext db) => _db = db;

    public async Task<PeriodResolution> ResolveAsync(Guid organizationId, DateOnly date, bool isOpeningEntry, bool isClosingEntry,
        CancellationToken ct = default)
    {
        var periods = await _db.FiscalPeriods.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.OrganizationId == organizationId && p.StartDate <= date && date <= p.EndDate)
            .Select(p => new { p.Id, p.FiscalYearId, p.PeriodNumber, p.Status, YearClosed = p.FiscalYear.IsClosed })
            .ToListAsync(ct);
        if (periods.Count == 0) return new(null, null, JournalErrorCodes.NoPeriod, JournalMessages.NoPeriod);

        var adjustment = isOpeningEntry || isClosingEntry;
        // REV-4 — period 13 takes adjustment (opening/closing) entries only: a regular entry on the year's last day never
        // falls through to it when period 12 is locked.
        if (!adjustment && periods.All(p => p.PeriodNumber == FiscalYearService.AdjustmentPeriodNumber))
            return new(periods[0].FiscalYearId, periods[0].Id, JournalErrorCodes.AdjustmentOnly, JournalMessages.AdjustmentOnly);
        if (!adjustment) periods = periods.Where(p => p.PeriodNumber != FiscalYearService.AdjustmentPeriodNumber).ToList();
        var ordered = periods
            .OrderBy(p => isClosingEntry ? (p.PeriodNumber == FiscalYearService.AdjustmentPeriodNumber ? 0 : 1)
                                         : (p.PeriodNumber == FiscalYearService.AdjustmentPeriodNumber ? 1 : 0))
            .ThenBy(p => p.PeriodNumber)
            .ToList();
        foreach (var p in ordered)
            if (!p.YearClosed && Accepts(p.Status, adjustment))
                return new(p.FiscalYearId, p.Id, null, null);

        var first = ordered[0];
        var (code, message) = first.YearClosed ? (JournalErrorCodes.PeriodClosed, JournalMessages.YearClosed) : Refusal(first.Status);
        return new(first.FiscalYearId, first.Id, code, message);
    }

    /// <summary>D-11 — Open accepts all; AdjustmentOnly only opening/closing entries; Locked/Closed nothing.</summary>
    internal static bool Accepts(string status, bool adjustmentEntry) =>
        status == PeriodStatuses.Open || (status == PeriodStatuses.AdjustmentOnly && adjustmentEntry);

    internal static (string Code, string Message) Refusal(string status) => status switch
    {
        PeriodStatuses.Locked         => (JournalErrorCodes.PeriodLocked, JournalMessages.PeriodLocked),
        PeriodStatuses.AdjustmentOnly => (JournalErrorCodes.AdjustmentOnly, JournalMessages.AdjustmentOnly),
        _                             => (JournalErrorCodes.PeriodClosed, JournalMessages.PeriodClosed),
    };
}

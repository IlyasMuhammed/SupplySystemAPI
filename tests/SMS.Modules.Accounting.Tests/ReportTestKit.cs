using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Modules.Accounting.Services;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Accounting.Tests;

/// <summary>
/// A47 RPT — a chart (standard leaves + a second revenue and expense account), FY 2026 with its 12 monthly periods and
/// period 13 (as A42 creates it), the system journal types, the required defaults (retained earnings = 3111), and helpers
/// that post journals through the real journal engine.
/// </summary>
internal sealed class ReportTestKit : IDisposable
{
    public AccountingHarness H { get; } = new();
    public Dictionary<string, ChartOfAccount> Chart { get; private set; } = null!;
    public FiscalYear Year { get; private set; } = null!;

    public AccountingCaller Admin => new(H.Org, 7, AccountingCaller.AllGlCodes, "Ada Admin");
    /// <summary>GL_PERIOD_CLOSE_EXECUTE only (+ view).</summary>
    public AccountingCaller Closer => new(H.Org, 8, [P.GL_PERIOD_CLOSE_VIEW, P.GL_PERIOD_CLOSE_EXECUTE], "Carl Closer");
    public AccountingCaller CloseViewer => new(H.Org, 9, [P.GL_PERIOD_CLOSE_VIEW, P.GL_REPORTS_VIEW], "Vera Viewer");
    public AccountingCaller GlViewerOnly => new(H.Org, 10, [P.GL_VIEW], "Gil Viewer");

    public ITrialBalanceService Reports => H.Get<ITrialBalanceService>();
    public IPeriodCloseService Close => H.Get<IPeriodCloseService>();
    public IJournalEntryService Entries => H.Get<IJournalEntryService>();

    public static readonly (string, string, string)[] Leaves =
    [
        .. AccountingHarness.StandardLeaves,
        ("4112", RootTypes.Revenue, AccountTypes.OtherIncome), ("5112", RootTypes.Expense, AccountTypes.Expense),
    ];

    public async Task<ReportTestKit> SetupAsync(bool withPeriod13 = true)
    {
        Chart = await H.SeedChartAsync(null, Leaves);
        var year = new FiscalYear { OrganizationId = H.Org, FiscalYearName = "FY 2026", StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 12, 31), ReportingCurrencyCode = "PKR" };
        foreach (var p in FiscalYearService.GeneratePeriods(H.Org, year.StartDate, year.EndDate))
            if (withPeriod13 || p.PeriodNumber != FiscalYearService.AdjustmentPeriodNumber) year.Periods.Add(p);
        H.Db.FiscalYears.Add(year);
        await H.Db.SaveChangesAsync();
        Year = year;
        await H.AssignRequiredDefaultsAsync(Chart);
        await H.Get<JournalTypeSeeder>().EnsureAsync(H.Org);
        return this;
    }

    public async Task<Guid> TypeAsync(string code) =>
        (await H.Db.JournalTypes.IgnoreQueryFilters().SingleAsync(t => t.OrganizationId == H.Org && t.Code == code)).Uuid;

    public JournalEntryLineRequest L(string code, decimal debit, decimal credit, string? costCenter = null) =>
        new() { AccountUuid = Chart[code].Uuid, Debit = debit, Credit = credit, CostCenter = costCenter };

    /// <summary>A MISC entry (posted unless <paramref name="post"/> is false) through the engine.</summary>
    public async Task<JournalEntryDetailModel> PostAsync(DateOnly date, params JournalEntryLineRequest[] lines) => await EntryAsync(date, true, lines);

    public async Task<JournalEntryDetailModel> EntryAsync(DateOnly date, bool post, JournalEntryLineRequest[] lines, string type = SystemJournalTypes.Misc,
        bool isClosingEntry = false) =>
        await Entries.CreateAsync(Admin, new CreateJournalEntryRequest
        {
            JournalTypeUuid = await TypeAsync(type),
            EntryDate       = date,
            Description     = "Report test",
            PostImmediately = post,
            IsClosingEntry  = isClosingEntry,
            Lines           = [.. lines],
        });

    public async Task SetPeriodAsync(int number, string status)
    {
        var p = await H.Db.FiscalPeriods.IgnoreQueryFilters().SingleAsync(x => x.OrganizationId == H.Org && x.FiscalYearId == Year.Id && x.PeriodNumber == number);
        p.Status = status;
        await H.Db.SaveChangesAsync();
    }

    /// <summary>Periods 1–12 Locked (period 13 untouched).</summary>
    public async Task LockMonthlyPeriodsAsync()
    {
        foreach (var p in await H.Db.FiscalPeriods.IgnoreQueryFilters().Where(x => x.FiscalYearId == Year.Id && x.PeriodNumber <= 12).ToListAsync())
            p.Status = PeriodStatuses.Locked;
        await H.Db.SaveChangesAsync();
    }

    public async Task<Guid> PeriodUuidAsync(int number) =>
        (await H.Db.FiscalPeriods.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.FiscalYearId == Year.Id && x.PeriodNumber == number)).Uuid;

    public async Task<decimal> CurrentBalanceAsync(string code) =>
        (await H.Db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == Chart[code].Id)).CurrentBalance;

    public static DateOnly D(int month, int day) => new(2026, month, day);

    public void Dispose() => H.Dispose();
}

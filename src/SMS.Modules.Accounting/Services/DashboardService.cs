using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;

namespace SMS.Modules.Accounting.Services;

/// <summary>
/// A42 0641–0643 — the GL dashboard (the endpoint is gated by [RequiresGlSetup], D-4): Σ current balance of the leaf
/// accounts per root type (each in its normal direction), the number of active accounts and the current fiscal year
/// (the one containing today, else the latest that started, else the earliest) with its reporting currency.
/// </summary>
public interface IDashboardService
{
    Task<GlDashboardModel> GetAsync(AccountingCaller caller, CancellationToken ct = default);
}

internal sealed class DashboardService : IDashboardService
{
    private readonly AccountingDbContext _db;
    private readonly TimeProvider _clock;

    public DashboardService(AccountingDbContext db, TimeProvider? clock = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<GlDashboardModel> GetAsync(AccountingCaller caller, CancellationToken ct = default)
    {
        caller.Require(caller.CanView);
        var org = caller.OrganizationId;

        var totals = await _db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.OrganizationId == org && !a.IsGroup)
            .GroupBy(a => a.RootType)
            .Select(g => new { RootType = g.Key, Total = g.Sum(a => a.CurrentBalance) })
            .ToListAsync(ct);
        decimal Total(string root) => totals.FirstOrDefault(t => t.RootType == root)?.Total ?? 0m;

        var accountCount = await _db.ChartOfAccounts.IgnoreQueryFilters().CountAsync(a => a.OrganizationId == org && a.IsActive, ct);

        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        var years = await _db.FiscalYears.IgnoreQueryFilters().AsNoTracking()
            .Where(y => y.OrganizationId == org).OrderBy(y => y.StartDate).ToListAsync(ct);
        var year = years.FirstOrDefault(y => y.StartDate <= today && today <= y.EndDate)
            ?? years.LastOrDefault(y => y.StartDate <= today)
            ?? years.FirstOrDefault();

        return new GlDashboardModel
        {
            TotalAssets      = Total(RootTypes.Asset),
            TotalLiabilities = Total(RootTypes.Liability),
            TotalEquity      = Total(RootTypes.Equity),
            TotalRevenue     = Total(RootTypes.Revenue),
            TotalExpenses    = Total(RootTypes.Expense),
            CurrencyCode     = year?.ReportingCurrencyCode,
            AccountCount     = accountCount,
            FiscalYear       = year is null ? null : new GlDashboardFiscalYear { Name = year.FiscalYearName, StartDate = year.StartDate, EndDate = year.EndDate },
        };
    }
}

using Microsoft.Extensions.DependencyInjection;

namespace SMS.Modules.Accounting.Services;

/// <summary>A47 C9/C10 (RPT) — trial balance, account balance/ledger, period close, year-end close.</summary>
internal static class ReportServicesRegistration
{
    internal static void AddAccountingReportServices(this IServiceCollection services)
    {
        services.AddScoped<ITrialBalanceService, TrialBalanceService>();
        services.AddScoped<IPeriodCloseService, PeriodCloseService>();
    }
}

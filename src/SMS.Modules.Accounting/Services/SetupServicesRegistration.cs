using Microsoft.Extensions.DependencyInjection;
using SMS.Shared.Common;

namespace SMS.Modules.Accounting.Services;

/// <summary>A42 BE2 — fiscal years/periods, accounting defaults, GL opening balances, external mappings, GL dashboard.</summary>
internal static class SetupServicesRegistration
{
    internal static void AddAccountingSetupServices(this IServiceCollection services)
    {
        services.AddScoped<FiscalBaseCurrency>();
        services.AddScoped<IFiscalYearService, FiscalYearService>();
        services.AddScoped<IFiscalPeriodGuard, FiscalPeriodGuard>();                 // D-18 seam for A43
        services.AddScoped<IDefaultsService, DefaultsService>();
        services.AddScoped<JournalNumbers>();
        services.AddScoped<IOpeningBalanceService, OpeningBalanceService>();
        services.AddScoped<IOpeningDateLockSource, GlOpeningDateLockSource>();       // D-11 / D-44 — reads AccountingDbContext only
        services.AddScoped<IExternalMappingService, ExternalMappingService>();
        services.AddScoped<IDashboardService, DashboardService>();
    }
}

using Microsoft.Extensions.DependencyInjection;

namespace SMS.Modules.Accounting.Services;

/// <summary>A42 BE1 phase 2 — templates and the chart of accounts. Called once from AccountingModuleExtensions.AddAccountingServices.</summary>
internal static class ChartServicesRegistration
{
    internal static IServiceCollection AddAccountingChartServices(this IServiceCollection services)
    {
        services.AddScoped<ITemplateSeeder, TemplateSeeder>();
        services.AddScoped<ITemplateService, TemplateService>();
        services.AddScoped<IChartOfAccountsService, ChartOfAccountsService>();
        return services;
    }
}

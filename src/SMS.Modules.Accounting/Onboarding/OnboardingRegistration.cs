using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Common;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Accounting.Onboarding;

/// <summary>A50 (SEED-FIN) — the COA and FISCAL setup seeders and the GL_OPENING_BALANCE import target (D-12, D-24).</summary>
internal static class OnboardingRegistration
{
    internal static IServiceCollection AddAccountingOnboarding(this IServiceCollection services)
    {
        services.AddScoped<ChartOfAccountsService>();
        services.AddScoped<ISetupCategorySeeder, CoaSetupSeeder>();
        services.AddScoped<ISetupCategorySeeder, FiscalSetupSeeder>();
        services.AddScoped<IImportTarget, GlOpeningBalanceImportTarget>();
        return services;
    }
}

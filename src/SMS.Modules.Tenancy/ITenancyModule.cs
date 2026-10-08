using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SMS.Modules.Tenancy.Data;
using SMS.Modules.Tenancy.Repositories;
using SMS.Modules.Tenancy.Services;
using SMS.Shared.Common;

namespace SMS.Modules.Tenancy;

public interface ITenancyModule { }

public static class TenancyModuleExtensions
{
    public static IServiceCollection AddTenancyModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connString = configuration["Data:mainOrg"]!;

        services.AddDbContext<TenancyDbContext>(options =>
            options.UseSqlServer(connString, sql => sql.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(500), null)));

        // TenantSnapshotProvider's cache — AddMemoryCache is safe to call more than once (TryAdd
        // semantics), so the module owns its own dependency rather than relying on Program.cs.
        services.AddMemoryCache();

        services.AddScoped<ITenancyRepository, TenancyRepository>();
        services.AddScoped<ITenancyService, TenancyService>();
        services.AddScoped<IOrganizationStatusService, OrganizationStatusService>();
        services.AddScoped<ISuperAdminService, SuperAdminService>();
        services.AddScoped<ITenantSnapshotProvider, TenantSnapshotProvider>();
        services.AddScoped<IOrganizationSettingsService, OrganizationSettingsService>();
        services.AddScoped<IOrganizationCurrencyService, OrganizationCurrencyService>();
        // A32 — other modules' startup backfill of per-organization data (Demand's rejection reasons).
        services.AddScoped<IOrganizationDirectory, OrganizationDirectory>();
        // Lookups asks every checker before it deletes a currency or changes its code.
        services.AddScoped<ILookupReferenceChecker, TenancyCurrencyReferenceChecker>();
        services.AddScoped<TenancyDataSeeder>();
        // A35 D-7 — organization currency settings (API + provisioning row).
        services.AddScoped<IOrganizationCurrencySettingsService, OrganizationCurrencySettingsService>();
        services.AddScoped<OrganizationCurrencySettingsProvisioningHandler>();
        services.AddScoped<IOrganizationProvisionedHandler, OrganizationCurrencySettingsProvisioningHandler>();

        return services;
    }

    public static IApplicationBuilder UseTenancyModule(this IApplicationBuilder app)
    {
        using var scope = app.ApplicationServices.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        db.Database.Migrate();

        var seeder = scope.ServiceProvider.GetRequiredService<TenancyDataSeeder>();
        seeder.SeedAsync().GetAwaiter().GetResult();

        // A35 P1-14 — repairs a missed provisioning call: every organization with a base currency gets its settings row.
        // Idempotent; a failure here must not stop the API (reads fall back to Organization.BaseCurrency ?? PKR).
        try
        {
            scope.ServiceProvider.GetRequiredService<OrganizationCurrencySettingsProvisioningHandler>()
                .BackfillAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            scope.ServiceProvider.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()?
                .CreateLogger("SMS.Modules.Tenancy").LogError(ex, "Backfilling organization currency settings failed; retried at the next start.");
        }

        return app;
    }
}

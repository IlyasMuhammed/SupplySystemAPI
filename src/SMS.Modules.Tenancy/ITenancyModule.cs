using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        services.AddScoped<TenancyDataSeeder>();

        return services;
    }

    public static IApplicationBuilder UseTenancyModule(this IApplicationBuilder app)
    {
        using var scope = app.ApplicationServices.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        db.Database.Migrate();

        var seeder = scope.ServiceProvider.GetRequiredService<TenancyDataSeeder>();
        seeder.SeedAsync().GetAwaiter().GetResult();

        return app;
    }
}

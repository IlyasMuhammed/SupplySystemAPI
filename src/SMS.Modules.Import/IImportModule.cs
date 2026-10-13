using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SMS.Modules.Import.Data;
using SMS.Modules.Import.Hubs;
using SMS.Modules.Import.Services;

namespace SMS.Modules.Import;

public interface IImportModule { }

/// <summary>A40 — Data Import & Upload Framework (docs/data-import/ADDENDUM-40-ANALYSIS.md, API-CONTRACT.md).</summary>
public static class ImportModuleExtensions
{
    public const string HubPath = "/hubs/import";

    public static IServiceCollection AddImportModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connString = configuration["Data:mainOrg"]!;
        services.AddDbContext<ImportDbContext>(options =>
            options.UseSqlServer(connString, sql => sql.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(500), null))
                   .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));

        // Targets (IImportTarget) are registered by their owning modules; IOpeningBalanceDateProvider (Finance) and
        // IModuleGate (Tenancy) are optional constructor parameters.
        services.AddScoped<ImportTargetRegistry>();
        services.AddScoped<ImportOpeningGate>();
        services.AddScoped<IImportProfileService, ImportProfileService>();
        services.AddScoped<ImportSessionService>();
        services.AddScoped<IImportSessionService>(sp => sp.GetRequiredService<ImportSessionService>());
        services.AddScoped<IImportExecutionService, ImportExecutionService>();
        services.AddScoped<IImportWorkbookService, ImportWorkbookService>();
        services.AddScoped<IImportJobScheduler, HangfireImportJobScheduler>();
        services.AddScoped<IImportHubNotifier, ImportHubNotifier>();
        services.AddScoped<ImportExecutionJob>();
        services.AddScoped<ImportPurgeJob>();
        services.AddScoped<ImportRecoveryJob>();
        return services;
    }

    public static IApplicationBuilder UseImportModule(this IApplicationBuilder app)
    {
        using var scope = app.ApplicationServices.CreateScope();
        scope.ServiceProvider.GetRequiredService<ImportDbContext>().Database.Migrate();
        try
        {
            // D-3 — mirror the registered targets into import.profiles / profile_fields.
            scope.ServiceProvider.GetRequiredService<IImportProfileService>().ReconcileAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("SMS.Modules.Import")
                .LogError(ex, "Reconciling import profiles failed; retried at the next start.");
        }
        try
        {
            // D-38 — sessions left IMPORTING by a crash (stale, no live job) become FAILED / interrupted.
            scope.ServiceProvider.GetRequiredService<ImportRecoveryJob>().RunAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("SMS.Modules.Import")
                .LogError(ex, "Recovering interrupted imports failed; the recurring job retries.");
        }

        // D-10 — daily purge of rows of sessions older than 90 days.
        Hangfire.RecurringJob.AddOrUpdate<ImportPurgeJob>(ImportPurgeJob.RecurringJobId, job => job.RunAsync(), ImportPurgeJob.Cron);
        // D-38 — every 10 minutes: interrupted imports.
        Hangfire.RecurringJob.AddOrUpdate<ImportRecoveryJob>(ImportRecoveryJob.RecurringJobId, job => job.RunAsync(), ImportRecoveryJob.Cron);
        return app;
    }

    /// <summary>C8 — <c>/hubs/import</c> (JWT via access_token query, as for /hubs/notifications).</summary>
    public static IEndpointRouteBuilder MapImportHub(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<ImportHub>(HubPath);
        return endpoints;
    }
}

using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Services;

namespace SMS.Modules.Accounting;

public interface IAccountingModule { }

/// <summary>A42 — general ledger setup: chart of accounts, fiscal periods, accounting defaults, GL opening balances,
/// external mappings (docs/general-ledger/ADDENDUM-42-ANALYSIS.md, API-CONTRACT.md).</summary>
public static class AccountingModuleExtensions
{
    public static IServiceCollection AddAccountingModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connString = configuration["Data:mainOrg"]!;

        services.AddDbContext<AccountingDbContext>(options =>
            options.UseSqlServer(connString, sql => sql.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(500), null))
                   .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));

        AddAccountingServices(services);
        return services;
    }

    /// <summary>Everything but the DbContext (the tests bring an in-memory one).</summary>
    internal static void AddAccountingServices(IServiceCollection services)
    {
        // Core plumbing (BE1 phase 1).
        services.AddScoped<IAccountingEventPublisher, AccountingEventPublisher>();
        GlSetupHistoryWriter.Register(services);
        services.AddScoped<IAccountingInitializer, AccountingInitializer>();
        services.AddScoped<ISetupStatusService, SetupStatusService>();

        // Templates / chart of accounts (BE1 phase 2) — Services/ChartServicesRegistration.cs.
        services.AddAccountingChartServices();

        // Fiscal years, defaults, opening balances, mappings, dashboard (BE2) — Services/SetupServicesRegistration.cs.
        services.AddAccountingSetupServices();
    }

    /// <summary>Applies the A42 tables in schema finance (guarded/idempotent — shared database), then seeds the system
    /// chart of accounts templates (D-6, best effort: a seeding failure is logged and never stops the API).</summary>
    public static IApplicationBuilder UseAccountingModule(this IApplicationBuilder app)
    {
        using var scope = app.ApplicationServices.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccountingDbContext>().Database.Migrate();
        try
        {
            scope.ServiceProvider.GetRequiredService<ITemplateSeeder>().SeedAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            scope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("SMS.Modules.Accounting")
                .LogError(ex, "Chart of accounts templates could not be seeded at startup.");
        }
        return app;
    }
}

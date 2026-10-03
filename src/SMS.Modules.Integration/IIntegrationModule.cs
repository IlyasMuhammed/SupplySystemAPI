using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SMS.Modules.Integration.Auth;
using SMS.Modules.Integration.Configuration;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Settings;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Gateway;
using SMS.Modules.Integration.Providers.QuickBooks;
using SMS.Shared.Common;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration;

public interface IIntegrationModule { }

/// <summary>
/// The QuickBooks gateway. See docs/quickbooks/QUICKBOOKS-INTEGRATION-PLAN.md.
/// <para>
/// Callers send complete payloads (SCM in-process through <see cref="IQuickBooksGateway"/>, other
/// systems over HTTP with an API key); this module owns OAuth, tokens, mapping, validation, retries
/// and every call to Intuit. It references nothing but SMS.Shared.
/// </para>
/// </summary>
public static class IntegrationModuleExtensions
{
    public static IServiceCollection AddIntegrationModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connString = configuration["Data:mainOrg"]!;

        services.AddDbContext<IntegrationDbContext>(options =>
            options.UseSqlServer(connString, sql =>
                sql.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(500), null)));

        services.Configure<QuickBooksOptions>(configuration.GetSection(QuickBooksOptions.SectionName));
        services.Configure<IntegrationJobOptions>(configuration.GetSection(IntegrationJobOptions.SectionName));

        // TryAdd because Suppliers and Logistics register the same pair.
        services.TryAddScoped<IEncryptionService, AesEncryptionService>();

        services.AddScoped<IConnectionAccessor, ConnectionAccessor>();
        // Lookups asks every checker before it deletes a currency or changes its code.
        services.AddScoped<ILookupReferenceChecker, IntegrationCurrencyReferenceChecker>();
        // Finance asks every checker before it renames a tax code (a code mapping finds the code by its text).
        services.AddScoped<ITaxCodeReferenceChecker, IntegrationTaxCodeReferenceChecker>();

        // Each area registers its own services — one file per area, so they can be built independently.
        services.AddIntegrationApiKeyAuth(configuration);      // Auth/
        services.AddIntegrationConnections(configuration);     // Core/Connections, Reference, Settings, admin setup controllers
        services.AddQuickBooksProvider(configuration);         // Providers/QuickBooks
        services.AddIntegrationGateway(configuration);         // Gateway/, Core/Sync, Core/Matching

        // The real gateway replaces the NullQuickBooksGateway that the calling modules TryAdd.
        services.RemoveAll<IQuickBooksGateway>();
        services.AddScoped<IQuickBooksGateway, QuickBooksGateway>();

        return services;
    }

    /// <summary>
    /// Applies this module's migrations and registers its recurring jobs. Migrate() reaches the shared
    /// database on API start, so every migration here must be idempotent.
    /// </summary>
    public static IApplicationBuilder UseIntegrationModule(this IApplicationBuilder app)
    {
        using (var scope = app.ApplicationServices.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            db.Database.Migrate();
        }

        IntegrationConnectionJobs.Register(app.ApplicationServices);
        IntegrationSyncJobs.Register(app.ApplicationServices);

        return app;
    }
}

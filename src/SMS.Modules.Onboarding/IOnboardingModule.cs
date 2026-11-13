using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SMS.Modules.Onboarding.Checklist;
using SMS.Modules.Onboarding.Data;
using SMS.Modules.Onboarding.SampleData;
using SMS.Modules.Onboarding.Services.Defaults;
using SMS.Modules.Onboarding.Services.Setup;
using SMS.Modules.Onboarding.Templates;
using SMS.Shared.Common;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Onboarding;

public interface IOnboardingModule { }

/// <summary>A50 — tenant onboarding: setup defaults, setup wizard, default data, document / email templates, sample data and
/// the onboarding checklist (docs/onboarding/ADDENDUM-50-ANALYSIS.md, API-CONTRACT.md). The data stays in the owning modules;
/// each registers its <see cref="ISetupCategorySeeder"/> / <see cref="ISampleDataContributor"/> / <see cref="IOnboardingSignal"/>.</summary>
public static class OnboardingModuleExtensions
{
    public static IServiceCollection AddOnboardingModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connString = configuration["Data:mainOrg"]!;

        services.AddDbContext<OnboardingDbContext>(options =>
            options.UseSqlServer(connString, sql => sql.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(500), null))
                   .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));

        AddOnboardingServices(services);
        return services;
    }

    /// <summary>Everything but the DbContext (the tests bring an in-memory one).</summary>
    internal static void AddOnboardingServices(IServiceCollection services)
    {
        // CORE — setup defaults (D-4), wizard + dashboard (D-10, D-11), provisioning (D-6).
        services.AddSingleton<PackCatalog>();
        services.AddScoped<SetupPayloadValidator>();
        services.AddScoped<ISetupDefaultsService, SetupDefaultsService>();
        services.AddScoped<SetupPackSeeder>();
        services.AddScoped<ISetupUserInviter, SetupUserInviter>();
        services.AddScoped<ITenantSetupService, TenantSetupService>();
        services.AddScoped<SetupProvisioningHandler>();
        services.AddScoped<IOrganizationProvisionedHandler>(sp => sp.GetRequiredService<SetupProvisioningHandler>());

        // TPL — document / email templates (Templates/), DEMO — sample data (SampleData/) and checklist (Checklist/).
        services.AddOnboardingTemplates();
        services.AddOnboardingSampleData();
        services.AddOnboardingChecklist();

        // Fallbacks only: the registrations above replace them.
        services.TryAddScoped<IOnboardingProgress, NullOnboardingProgress>();
        services.TryAddScoped<IDocumentBrandingProvider, NullDocumentBrandingProvider>();
        services.TryAddScoped<IEmailTemplateRenderer, NullEmailTemplateRenderer>();
    }

    /// <summary>
    /// Applies the onboarding schema migration (guarded/idempotent — shared database; it backfills existing organizations as
    /// Completed / LEGACY), inserts the missing built-in setup packs (never updates a row, BR-50-015), gives any organization
    /// still without a setup row a NotStarted one, and schedules the checklist job. Everything after the migration is best effort.
    /// </summary>
    public static IApplicationBuilder UseOnboardingModule(this IApplicationBuilder app)
    {
        using var scope = app.ApplicationServices.CreateScope();
        scope.ServiceProvider.GetRequiredService<OnboardingDbContext>().Database.Migrate();
        var logger = scope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("SMS.Modules.Onboarding");

        try
        {
            var added = scope.ServiceProvider.GetRequiredService<SetupPackSeeder>().SeedAsync().GetAwaiter().GetResult();
            if (added > 0) logger?.LogInformation("Seeded {Count} built-in setup default(s).", added);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Built-in setup defaults could not be seeded at startup.");
        }

        try
        {
            if (scope.ServiceProvider.GetService<IOrganizationDirectory>() is { } organizations)
            {
                var ids = organizations.GetOrganizationIdsAsync().GetAwaiter().GetResult();
                scope.ServiceProvider.GetRequiredService<SetupProvisioningHandler>().EnsureMissingAsync(ids).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Organization setup rows could not be backfilled at startup; each is created on its first wizard call.");
        }

        try
        {
            OnboardingChecklistJob.Schedule();
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "The onboarding checklist job could not be scheduled.");
        }
        return app;
    }
}

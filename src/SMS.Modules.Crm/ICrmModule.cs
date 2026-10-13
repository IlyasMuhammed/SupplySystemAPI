using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SMS.Modules.Crm.Data;
using SMS.Modules.Crm.Services;
using SMS.Shared.Common;

namespace SMS.Modules.Crm;

public interface ICrmModule { }

/// <summary>A41 — CRM: leads, pipeline, opportunities, activities (docs/crm/ADDENDUM-41-ANALYSIS.md, API-CONTRACT.md).</summary>
public static class CrmModuleExtensions
{
    public static IServiceCollection AddCrmModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connString = configuration["Data:mainOrg"]!;

        services.AddDbContext<CrmDbContext>(options =>
            options.UseSqlServer(connString, sql => sql.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(500), null))
                   .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));

        AddCrmServices(services);
        return services;
    }

    /// <summary>Everything but the DbContext (the tests bring an in-memory one). Optional seams (IOrgUserDirectory — Auth,
    /// ICrmAccountGateway — Suppliers, ICrmSalesDocumentGateway — Demand, IOrgCurrencyLookup — Finance, IModuleGate —
    /// Tenancy, INotificationService) are constructor parameters with defaults: without one the feature it serves is off.</summary>
    internal static void AddCrmServices(IServiceCollection services)
    {
        services.AddScoped<CrmDirectory>();
        services.AddScoped<CrmCurrency>();
        services.AddScoped<CrmSeeder>();
        services.AddScoped<ICrmSeeder>(sp => sp.GetRequiredService<CrmSeeder>());
        services.AddScoped<IOrganizationProvisionedHandler>(sp => sp.GetRequiredService<CrmSeeder>());
        services.AddScoped<ICrmEventPublisher, CrmEventPublisher>();
        services.AddScoped<ICrmEventHandler<LeadAssignedEvent>, LeadAssignedNotifier>();
        services.AddSingleton<CrmApiRateLimiter>();

        services.AddScoped<ILeadSourceService, LeadSourceService>();
        services.AddScoped<ICampaignService, CampaignService>();
        services.AddScoped<ISalesTeamService, SalesTeamService>();
        services.AddScoped<IPipelineStageService, PipelineStageService>();
        services.AddScoped<ILeadStateMachineService, LeadStateMachineService>();
        services.AddScoped<LeadService>();
        services.AddScoped<ILeadService>(sp => sp.GetRequiredService<LeadService>());
        services.AddScoped<ILeadConversionService, LeadConversionService>();
        services.AddScoped<IOpportunityService, OpportunityService>();
        services.AddScoped<ActivityService>();
        services.AddScoped<IActivityService>(sp => sp.GetRequiredService<ActivityService>());
        services.AddScoped<TimelineService>();
        services.AddScoped<ITimelineService>(sp => sp.GetRequiredService<TimelineService>());
        services.AddScoped<ITimelineExportService>(sp => sp.GetRequiredService<TimelineService>());
        services.AddScoped<LeadApiKeyService>();
        services.AddScoped<ILeadApiKeyService>(sp => sp.GetRequiredService<LeadApiKeyService>());
        services.AddScoped<IPublicLeadService>(sp => sp.GetRequiredService<LeadApiKeyService>());

        // A37 D-18 — open leads / opportunities for Tenancy's pre-disable impact check.
        services.AddScoped<IModuleImpactProvider, CrmImpactProvider>();
    }

    /// <summary>Applies the crm schema migrations (guarded/idempotent — shared database) and seeds every organization's
    /// lead sources and pipeline stages (D-7, best effort: the CRM also seeds an organization on its first use).</summary>
    public static IApplicationBuilder UseCrmModule(this IApplicationBuilder app)
    {
        using var scope = app.ApplicationServices.CreateScope();
        scope.ServiceProvider.GetRequiredService<CrmDbContext>().Database.Migrate();
        try
        {
            if (scope.ServiceProvider.GetService<IOrganizationDirectory>() is { } organizations)
            {
                var seeder = scope.ServiceProvider.GetRequiredService<ICrmSeeder>();
                foreach (var org in organizations.GetOrganizationIdsAsync().GetAwaiter().GetResult())
                    seeder.EnsureAsync(org).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            scope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("SMS.Modules.Crm")
                .LogError(ex, "CRM seed data could not be created at startup; each organization is seeded on its first CRM call.");
        }
        return app;
    }
}

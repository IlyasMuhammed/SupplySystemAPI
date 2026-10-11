using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Pos.Data;
using SMS.Modules.Pos.Hubs;
using SMS.Modules.Pos.Services;
using SMS.Shared.Common;

namespace SMS.Modules.Pos;

public interface IPosModule { }

/// <summary>A38 — Point of Sale (docs/pos/ADDENDUM-38-ANALYSIS.md, API-CONTRACT.md).</summary>
public static class PosModuleExtensions
{
    public const string HubPath = "/hubs/pos";

    public static IServiceCollection AddPosModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connString = configuration["Data:mainOrg"]!;

        services.AddDbContext<PosDbContext>(options =>
            options.UseSqlServer(connString, sql => sql.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(500), null))
                   .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));

        // Optional seams (IPosPinService — XMOD/Auth, IWarehouseDirectory, ITaxCodeLookup, IModuleGate, currency) are
        // constructor parameters with defaults: a host without them still starts, the feature answers 400.
        services.AddScoped<PosReadModel>();
        services.AddScoped<IPosNumberSequence, PosNumberSequenceService>();
        services.AddScoped<IPosShiftTotals, PosShiftTotals>();
        services.AddScoped<IPosHubNotifier, PosHubNotifier>();
        services.AddScoped<IPosStoreService, PosStoreService>();
        services.AddScoped<IPosSessionService, PosSessionService>();
        services.AddScoped<IPosShiftService, PosShiftService>();
        services.AddScoped<StaleShiftAlertJob>();

        // POS-B — orders, postings, settings, PWA sync, change pushes (IPosStockPoster / IPosCatalogSource — Inventory,
        // ICounterSaleService — Finance, IPosCustomerSource — Suppliers, ICustomerBalanceLookup are optional parameters).
        services.AddScoped<IPosPostingService, PosPostingService>();
        services.AddScoped<IPosOrderService, PosOrderService>();
        services.AddScoped<IPosSettingsService, PosSettingsService>();
        services.AddScoped<IPosSyncService, PosSyncService>();
        services.AddScoped<IPosChangeListener, PosChangeListener>();
        services.AddScoped<PosPostingRetryJob>();
        services.AddScoped<NegativeStockReportJob>();

        // A39 — returns, exchanges & store credit (docs/pos-returns/).
        services.AddScoped<IPosReturnPolicyService, PosReturnPolicyService>();
        services.AddScoped<IPosStoreCreditService, PosStoreCreditService>();
        services.AddScoped<StoreCreditExpiryJob>();
        services.AddScoped<CrossStoreReconciliationReport>();

        // A37 D-18 — open shifts for Tenancy's pre-disable impact check.
        services.AddScoped<IModuleImpactProvider, PosShiftImpactProvider>();
        return services;
    }

    public static IApplicationBuilder UsePosModule(this IApplicationBuilder app)
    {
        using var scope = app.ApplicationServices.CreateScope();
        scope.ServiceProvider.GetRequiredService<PosDbContext>().Database.Migrate();

        // A38 E4 — hourly stale-shift alert to the store supervisors.
        Hangfire.RecurringJob.AddOrUpdate<StaleShiftAlertJob>(StaleShiftAlertJob.RecurringJobId, job => job.RunAsync(), StaleShiftAlertJob.Cron);
        // A38 E4 (POS-B) — pending stock/finance postings every 15 min; daily negative-stock report to store supervisors.
        Hangfire.RecurringJob.AddOrUpdate<PosPostingRetryJob>(PosPostingRetryJob.RecurringJobId, job => job.RunAsync(), PosPostingRetryJob.Cron);
        Hangfire.RecurringJob.AddOrUpdate<NegativeStockReportJob>(NegativeStockReportJob.RecurringJobId, job => job.RunAsync(), NegativeStockReportJob.Cron);
        // A39 D-11 — store credit expiry daily 00:30 UTC; cross-store returns report Mondays 06:00 UTC.
        Hangfire.RecurringJob.AddOrUpdate<StoreCreditExpiryJob>(StoreCreditExpiryJob.RecurringJobId, job => job.RunAsync(), StoreCreditExpiryJob.Cron);
        Hangfire.RecurringJob.AddOrUpdate<CrossStoreReconciliationReport>(CrossStoreReconciliationReport.RecurringJobId, job => job.RunAsync(),
            CrossStoreReconciliationReport.Cron);
        return app;
    }

    /// <summary>A38 D-15 — <c>/hubs/pos</c> (JWT via access_token query, as for /hubs/notifications).</summary>
    public static IEndpointRouteBuilder MapPosHub(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<PosHub>(HubPath);
        return endpoints;
    }
}

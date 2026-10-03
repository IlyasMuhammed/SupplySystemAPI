using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SMS.Modules.Integration.Configuration;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Matching;
using SMS.Modules.Integration.Core.Sync;
using SMS.Modules.Integration.Gateway.Validation;
using SMS.Modules.Integration.Jobs;

namespace SMS.Modules.Integration.Gateway;

// OWNER: gateway & sync work package (QBI-14–17, 19, 22, 23). Registers validation, the object
// builder, outbox, ledger, executor, dependency resolver, matching, IOutboxControl and the jobs.
// QuickBooksGateway itself is registered by IIntegrationModule.
internal static class GatewayRegistration
{
    public static IServiceCollection AddIntegrationGateway(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);

        // Gateway pipeline
        services.AddScoped<IGatewayCallerContext, HttpGatewayCallerContext>();
        services.AddScoped<IPayloadValidator, PayloadValidator>();
        services.AddScoped<IRemoteNameResolver, RemoteNameResolver>();
        // Currency facts + exchange rates (plan S-10). IExchangeRateProvider (Finance) is resolved optional-safe.
        services.AddScoped<ICurrencyRules, CurrencyRules>();

        // Sync engine
        services.AddScoped<IQboObjectBuilder, QboObjectBuilder>();
        services.AddScoped<ISyncOutbox, SyncOutbox>();
        services.AddScoped<ISyncLedger, SyncLedger>();
        services.AddScoped<ISyncDependencies, SyncDependencies>();
        // After validation, for the gateway and for re-validated Blocked records alike.
        services.AddScoped<ISyncAdmission, SyncAdmission>();
        // Blocked records re-checked after settings/mapping/reference changes and by the outbox job.
        services.AddScoped<IBlockedRecordRevalidator, BlockedRecordRevalidator>();
        services.TryAddSingleton<RevalidationCursor>();
        services.AddScoped<ISyncExecutor, SyncExecutor>();
        services.AddScoped<IOutboxControl, OutboxControl>();
        services.AddScoped<IQuickBooksSyncAdminService, SyncAdminService>();

        // Matching
        services.AddScoped<IQuickBooksMatchingService, MatchingService>();

        // Jobs (resolved by Hangfire per run)
        services.AddScoped<ConnectionJobRunner>();
        services.AddScoped<SyncOutboxJob>();
        services.AddScoped<SyncSweepJob>();
        services.AddScoped<ReconciliationJob>();
        services.AddScoped<DependencyJob>();
        services.AddScoped<SyncLogRetentionJob>();

        return services;
    }
}

// OWNER: gateway & sync work package. Outbox, sweep, reconciliation, dependency expiry, log retention.
internal static class IntegrationSyncJobs
{
    public static void Register(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<IntegrationJobOptions>>().Value;

        RecurringJob.AddOrUpdate<SyncOutboxJob>(SyncOutboxJob.RecurringJobId, job => job.RunAsync(), options.OutboxCron);
        RecurringJob.AddOrUpdate<SyncSweepJob>(SyncSweepJob.RecurringJobId, job => job.RunAsync(), options.SweepCron);
        RecurringJob.AddOrUpdate<ReconciliationJob>(ReconciliationJob.RecurringJobId, job => job.RunAsync(), options.ReconciliationCron);
        // Dependency pulls gate how fast a waiting invoice goes out, so they run as often as the outbox.
        RecurringJob.AddOrUpdate<DependencyJob>(DependencyJob.RecurringJobId, job => job.RunAsync(), options.OutboxCron);
        RecurringJob.AddOrUpdate<SyncLogRetentionJob>(SyncLogRetentionJob.RecurringJobId, job => job.RunAsync(), options.CleanupCron);
    }
}

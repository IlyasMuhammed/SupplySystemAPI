using System.Threading.RateLimiting;
using Hangfire;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SMS.Modules.Integration.Configuration;
using SMS.Modules.Integration.Controllers.Admin;
using SMS.Modules.Integration.Core.ApiClients;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Core.Reference;
using SMS.Modules.Integration.Core.Settings;
using SMS.Modules.Integration.Jobs;
using SMS.Modules.Integration.Providers.QuickBooks;

namespace SMS.Modules.Integration.Core.Connections;

// OWNER: connection & auth work package (QBI-05, 07–13). Registers the credential vault, OAuth
// state service, connection service, token manager, connection health, reference data, preflight,
// settings/mapping services and API-client admin service.
internal static class ConnectionsRegistration
{
    public static IServiceCollection AddIntegrationConnections(this IServiceCollection services, IConfiguration configuration)
    {
        // The only code that touches plaintext tokens, and the state token behind the anonymous callback.
        services.AddScoped<ICredentialVault, CredentialVault>();
        services.AddScoped<IOAuthStateService, OAuthStateService>();

        // One instance serves the contract and the job's ForceRefreshAsync.
        services.AddScoped<TokenManager>();
        services.AddScoped<ITokenManager>(sp => sp.GetRequiredService<TokenManager>());
        services.AddScoped<IConnectionHealth, ConnectionHealth>();

        services.AddScoped<IQuickBooksConnectionService, ConnectionService>();
        services.AddScoped<IQuickBooksCallbackService, OAuthCallbackService>();

        // Reference data: one instance behind the admin contract, the store the callback and job use,
        // and the reader the sync engine uses.
        services.AddScoped<ReferenceDataService>();
        services.AddScoped<IReferenceDataService>(sp => sp.GetRequiredService<ReferenceDataService>());
        services.AddScoped<IReferenceDataStore>(sp   => sp.GetRequiredService<ReferenceDataService>());
        services.AddScoped<IReferenceDataReader>(sp  => sp.GetRequiredService<ReferenceDataService>());
        services.AddScoped<IBaseCurrencyResolver, BaseCurrencyResolver>();
        services.AddScoped<IPreflightService, PreflightService>();

        services.AddScoped<IIntegrationSettingsService, IntegrationSettingsService>();
        services.AddScoped<IApiClientService, ApiClientService>();

        // QuickBooks' OAuth and per-call context (Providers/QuickBooks, owned here — see QuickBooksRegistration).
        // Singletons: stateless apart from options, and an OAuth2Client is built per operation anyway.
        services.AddSingleton<IIntuitOAuthClient, SdkIntuitOAuthClient>();
        services.AddSingleton<IAccountingAuthProvider, QuickBooksAuthProvider>();
        services.AddScoped<IQboServiceContextFactory, QboServiceContextFactory>();

        services.AddScoped<TokenRefreshJob>();
        services.AddScoped<ReferenceRefreshJob>();
        services.AddScoped<StateTokenCleanupJob>();

        // The callback is anonymous and reachable by anyone, so it is throttled per IP. Generous for a
        // person (a connect is one redirect), tight for anything grinding through state values.
        services.Configure<RateLimiterOptions>(options =>
            options.AddPolicy(CallbackController.RateLimitPolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit          = 30,
                        Window               = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit           = 0
                    })));

        return services;
    }
}

// OWNER: connection & auth work package. Token refresh, reference refresh, state-token cleanup.
internal static class IntegrationConnectionJobs
{
    public static void Register(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<IntegrationJobOptions>>().Value;

        // Daily, whatever was synced: keeps every grant alive and warns before one runs out.
        RecurringJob.AddOrUpdate<TokenRefreshJob>(
            TokenRefreshJob.RecurringJobId, job => job.RunAsync(), options.TokenRefreshCron);

        // Daily, after the token refresh, so it runs on fresh tokens.
        RecurringJob.AddOrUpdate<ReferenceRefreshJob>(
            ReferenceRefreshJob.RecurringJobId, job => job.RunAsync(), options.ReferenceRefreshCron);

        RecurringJob.AddOrUpdate<StateTokenCleanupJob>(
            StateTokenCleanupJob.RecurringJobId, job => job.RunAsync(), options.CleanupCron);
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SMS.Shared.Finance.Posting;

namespace SMS.Modules.Accounting.Posting;

/// <summary>A44 — the financial posting gateway, the entity sync gateway, the internal GL target, the posting log and
/// settings, the account map reader and the retry job.</summary>
internal static class PostingRegistration
{
    internal static void AddAccountingPostingServices(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<GlReadiness>();
        services.AddScoped<ITenantFinanceConfigService, TenantFinanceConfigService>();
        services.AddScoped<FinancialPostingGateway>();
        services.AddScoped<EntitySyncGateway>();
        services.AddScoped<ExternalAccountMapReader>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IFinancialPostingTarget, InternalGlPostingTarget>());
        services.AddScoped<QuickBooksStatusRefresher>();
        services.AddScoped<IPostingLogService, PostingLogService>();
        services.AddScoped<PostingRetryJob>();
        services.UseAccountingPostingSeams();
    }

    /// <summary>
    /// The Shared seams callers resolve. Callers register the Null* implementations with TryAdd so they run in a host
    /// without this module; these replace whatever is registered (also a Null* added by a module registered later —
    /// Program.cs calls this again after the last module).
    /// </summary>
    internal static IServiceCollection UseAccountingPostingSeams(this IServiceCollection services)
    {
        services.RemoveAll<IFinancialPostingGateway>();
        services.AddScoped<IFinancialPostingGateway>(sp => sp.GetRequiredService<FinancialPostingGateway>());
        services.RemoveAll<IEntitySyncGateway>();
        services.AddScoped<IEntitySyncGateway>(sp => sp.GetRequiredService<EntitySyncGateway>());
        services.RemoveAll<IExternalAccountMapReader>();
        services.AddScoped<IExternalAccountMapReader>(sp => sp.GetRequiredService<ExternalAccountMapReader>());
        // A48 (AP, D-AP-12) — are the roles a posting will name mapped for the active internal GL?
        services.TryAddScoped<InternalGlPostingTarget>();
        services.TryAddScoped<GlPostingRoleReadiness>();
        services.RemoveAll<IPostingRoleReadiness>();
        services.AddScoped<IPostingRoleReadiness>(sp => sp.GetRequiredService<GlPostingRoleReadiness>());
        return services;
    }
}

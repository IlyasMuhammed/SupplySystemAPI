using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SMS.Modules.Integration.Core.Settings;

// OWNER: CTRL (A44). The readers the gateway side consumes — sync control (D-14), provider defaults (D-15) and the
// effective settings accounts (D-16). Called from AddIntegrationConnections; safe to call again from the gateway's
// registration (and test harnesses that only build the gateway): every line is idempotent, and these replace the
// gateway's TryAdd fallbacks (LegacySyncControlReader, EmptyProviderDefaultsReader) whichever order the two run in.
internal static class SettingsReadersRegistration
{
    public static IServiceCollection AddIntegrationSettingsReaders(this IServiceCollection services)
    {
        services.TryAddScoped<SyncControlService>();
        services.RemoveAll<ISyncControlReader>();
        services.AddScoped<ISyncControlReader>(sp => sp.GetRequiredService<SyncControlService>());
        services.TryAddScoped<IQuickBooksSyncControlService>(sp => sp.GetRequiredService<SyncControlService>());

        services.TryAddScoped<ProviderDefaultsService>();
        services.RemoveAll<IProviderDefaultsReader>();
        services.AddScoped<IProviderDefaultsReader>(sp => sp.GetRequiredService<ProviderDefaultsService>());
        services.TryAddScoped<IQuickBooksProviderDefaultsService>(sp => sp.GetRequiredService<ProviderDefaultsService>());

        services.TryAddScoped<IEffectiveAccountsResolver, EffectiveAccountsResolver>();
        return services;
    }
}

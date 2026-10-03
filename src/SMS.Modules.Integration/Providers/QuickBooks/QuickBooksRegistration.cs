using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SMS.Modules.Integration.Core.Providers;

namespace SMS.Modules.Integration.Providers.QuickBooks;

// OWNER: QuickBooks provider work package (QBI-11, QBI-18). Registers QuickBooksAccountingProvider as
// IAccountingProvider, the provider registry, and the error translator.
// NOTE: QboServiceContextFactory and QuickBooksAuthProvider are owned by the connection work package
// and registered in ConnectionsRegistration.
internal static class QuickBooksRegistration
{
    /// <summary>
    /// Lifetimes:
    /// <list type="bullet">
    /// <item><see cref="QboErrorTranslator"/>, <see cref="QboQueryBuilder"/>, <see cref="IQboClientFactory"/> — singletons (stateless).</item>
    /// <item><see cref="IAccountingProvider"/> → <see cref="QuickBooksAccountingProvider"/> — scoped, because the
    /// <see cref="IQboServiceContextFactory"/> it uses is scoped (token manager → DbContext). Added with
    /// TryAddEnumerable so calling this twice cannot register it twice (the registry would refuse that).</item>
    /// <item><see cref="IAccountingProviderRegistry"/> → <see cref="AccountingProviderRegistry"/> — scoped, as it holds the scoped providers.</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddQuickBooksProvider(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton<QboErrorTranslator>();
        services.TryAddSingleton<QboQueryBuilder>();
        services.TryAddSingleton<IQboClientFactory, SdkQboClientFactory>();

        services.TryAddEnumerable(ServiceDescriptor.Scoped<IAccountingProvider, QuickBooksAccountingProvider>());
        services.TryAddScoped<IAccountingProviderRegistry, AccountingProviderRegistry>();

        return services;
    }
}

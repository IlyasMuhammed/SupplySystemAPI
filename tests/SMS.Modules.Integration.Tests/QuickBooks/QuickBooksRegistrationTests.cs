using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Providers.QuickBooks;
using SMS.Modules.Integration.Tests.QuickBooks.Support;

namespace SMS.Modules.Integration.Tests.QuickBooks;

public class QuickBooksRegistrationTests
{
    private static ServiceProvider Build(Action<IServiceCollection>? extra = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // Owned by the connection package; stubbed here.
        services.AddScoped<IQboServiceContextFactory>(_ => new StubContextFactory());
        services.AddQuickBooksProvider(new ConfigurationBuilder().Build());
        extra?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    [Fact]
    public void Registry_resolves_the_QuickBooks_provider()
    {
        using var provider = Build();
        using var scope = provider.CreateScope();

        var registry = scope.ServiceProvider.GetRequiredService<IAccountingProviderRegistry>();

        registry.Get(ProviderKeys.QuickBooksOnline).Should().BeOfType<QuickBooksAccountingProvider>();
        scope.ServiceProvider.GetServices<IAccountingProvider>().Should().ContainSingle();
    }

    [Fact]
    public void Calling_the_registration_twice_does_not_register_the_provider_twice()
    {
        using var provider = Build(s => s.AddQuickBooksProvider(new ConfigurationBuilder().Build()));
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetServices<IAccountingProvider>().Should().ContainSingle();
        var act = () => scope.ServiceProvider.GetRequiredService<IAccountingProviderRegistry>();
        act.Should().NotThrow();
    }

    [Fact]
    public void Lifetimes()
    {
        var services = new ServiceCollection();
        services.AddQuickBooksProvider(new ConfigurationBuilder().Build());

        services.Single(d => d.ServiceType == typeof(IAccountingProvider)).Lifetime.Should().Be(ServiceLifetime.Scoped);
        services.Single(d => d.ServiceType == typeof(IAccountingProviderRegistry)).Lifetime.Should().Be(ServiceLifetime.Scoped);
        services.Single(d => d.ServiceType == typeof(QboErrorTranslator)).Lifetime.Should().Be(ServiceLifetime.Singleton);
        services.Single(d => d.ServiceType == typeof(QboQueryBuilder)).Lifetime.Should().Be(ServiceLifetime.Singleton);
        services.Single(d => d.ServiceType == typeof(IQboClientFactory)).Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void The_real_client_factory_is_the_sdk_one()
    {
        using var provider = Build();

        provider.GetRequiredService<IQboClientFactory>().Should().BeOfType<SdkQboClientFactory>();
    }
}

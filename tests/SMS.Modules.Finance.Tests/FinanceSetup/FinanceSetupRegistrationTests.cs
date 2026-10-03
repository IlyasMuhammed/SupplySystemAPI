using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Finance.Controllers;
using SMS.Modules.Finance.Services;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Finance.Tests.FinanceSetup;

/// <summary>
/// The tax-code and exchange-rate services, the two SMS.Shared contracts and the currency reference checker
/// must be built by the real container from the module's own registration — a constructor asking for
/// something nobody registered passes every unit test and fails on its first request.
/// </summary>
public class FinanceSetupRegistrationTests
{
    private static ServiceProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Data:mainOrg"] = "Server=none;Database=none;Trusted_Connection=True;" })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(new StaticTenantContext());
        services.AddFinanceModule(configuration);

        // What the rest of Finance needs from the modules around it (the same list SalesSideRegistrationTests uses).
        services.AddDbContext<DemandDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddSingleton(Mock.Of<IDeliveryFulfillmentReader>());
        services.AddSingleton(Mock.Of<ISupplierNameLookupService>());
        services.AddSingleton(Mock.Of<ILookupsService>());
        services.AddSingleton(Mock.Of<IBackgroundJobClient>());
        services.AddSingleton(Mock.Of<IProductVariantResolver>());

        return services.BuildServiceProvider();
    }

    [Fact]
    public void The_services_and_the_shared_contracts_resolve_one_per_scope()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        sp.GetRequiredService<ITaxCodeService>().Should().BeOfType<TaxCodeService>();
        sp.GetRequiredService<IExchangeRateService>().Should().BeOfType<ExchangeRateService>();
        sp.GetRequiredService<ITaxCodeLookup>().Should().BeOfType<TaxCodeLookup>();
        sp.GetRequiredService<IExchangeRateProvider>().Should().BeOfType<ExchangeRateProvider>();

        sp.GetRequiredService<IExchangeRateProvider>().Should().BeSameAs(sp.GetRequiredService<IExchangeRateProvider>(), "scoped");
    }

    [Fact]
    public void Finance_offers_lookups_a_currency_reference_checker()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetServices<ILookupReferenceChecker>()
            .Should().ContainSingle(c => c is FinanceCurrencyReferenceChecker);
    }

    [Theory]
    [InlineData(typeof(TaxCodesController))]
    [InlineData(typeof(ExchangeRatesController))]
    public void Each_controller_can_be_built_the_way_mvc_builds_it(Type controller)
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        ActivatorUtilities.CreateInstance(scope.ServiceProvider, controller).Should().BeOfType(controller);
    }

    [Fact]
    public void An_optional_resolve_finds_the_contracts_and_a_host_without_finance_gets_null_not_an_error()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetService<IExchangeRateProvider>().Should().NotBeNull();

        using var bare = new ServiceCollection().BuildServiceProvider();
        bare.GetService<IExchangeRateProvider>().Should().BeNull();
        bare.GetService<ITaxCodeLookup>().Should().BeNull();
    }
}

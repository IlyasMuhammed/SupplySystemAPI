using System.Reflection;
using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Finance.Integration;
using SMS.Modules.Finance.Repositories;
using SMS.Modules.Finance.Services;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Finance.Tests.SupplierInvoices;

/// <summary>
/// The supplier invoice code takes the SMS.Shared lookups as optional constructor parameters (so hand-built
/// rigs need nothing). This proves the module's own registration does hand them over — an optional parameter
/// that silently stays null would switch tax codes and the rate snapshot off in production.
/// </summary>
public class SupplierInvoiceRegistrationTests
{
    private static ServiceProvider Provider(bool withTenancyAndLookups)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Data:mainOrg"] = "Server=none;Database=none;Trusted_Connection=True;" })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(new StaticTenantContext());
        services.AddFinanceModule(configuration);

        services.AddDbContext<DemandDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddDbContext<SMS.Modules.Warehouse.Data.WarehouseDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddSingleton(Mock.Of<IDeliveryFulfillmentReader>());
        services.AddSingleton(Mock.Of<ISupplierNameLookupService>());
        services.AddSingleton(Mock.Of<ILookupsService>());
        services.AddSingleton(Mock.Of<IBackgroundJobClient>());
        services.AddSingleton(Mock.Of<IProductVariantResolver>());

        if (withTenancyAndLookups)
        {
            // What SMS.Modules.Tenancy and SMS.Modules.Lookups register in the real host.
            var currency = new FakeBaseCurrency();
            services.AddSingleton<IOrganizationCurrencyService>(currency);
            services.AddSingleton<ICurrencyCodeLookup>(currency);
        }

        return services.BuildServiceProvider();
    }

    private static object? Field(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target);

    [Fact]
    public void The_invoice_repository_gets_the_tax_codes_rates_and_base_currency_the_host_registers()
    {
        using var provider = Provider(withTenancyAndLookups: true);
        using var scope = provider.CreateScope();

        var repo = scope.ServiceProvider.GetRequiredService<IInvoiceRepository>();
        repo.Should().BeOfType<InvoiceRepository>();
        Field(repo, "_taxCodes").Should().BeAssignableTo<ITaxCodeLookup>();
        Field(repo, "_rates").Should().BeAssignableTo<IExchangeRateProvider>();
        Field(repo, "_orgCurrency").Should().BeAssignableTo<IOrganizationCurrencyService>();
        Field(repo, "_currencyCodes").Should().BeAssignableTo<ICurrencyCodeLookup>();

        var autoCreation = scope.ServiceProvider.GetRequiredService<IInvoiceAutoCreationService>();
        Field(autoCreation, "_taxCodes").Should().BeAssignableTo<ITaxCodeLookup>();
        Field(autoCreation, "_orgCurrency").Should().NotBeNull();

        Field(scope.ServiceProvider.GetRequiredService<BillQuickBooksPublisher>(), "_gateway").Should().NotBeNull();
    }

    [Fact]
    public void Without_Tenancy_and_Lookups_the_repository_still_builds_and_simply_takes_no_snapshot()
    {
        using var provider = Provider(withTenancyAndLookups: false);
        using var scope = provider.CreateScope();

        var repo = scope.ServiceProvider.GetRequiredService<IInvoiceRepository>();
        Field(repo, "_orgCurrency").Should().BeNull();
        Field(repo, "_currencyCodes").Should().BeNull();
        Field(repo, "_taxCodes").Should().NotBeNull("Finance registers its own tax codes");
    }
}

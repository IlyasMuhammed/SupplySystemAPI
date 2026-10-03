using System.Reflection;
using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Finance.Integration;
using SMS.Modules.Finance.Services;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;
using SMS.Shared.Integration.QuickBooks;
using Xunit;

namespace SMS.Modules.Finance.Tests.QuickBooks;

/// <summary>The module's own registration: buildable, Null gateway only by default, and both invoice services get their publishers.</summary>
public class FinanceQuickBooksRegistrationTests
{
    private static ServiceCollection Services(Action<IServiceCollection>? before = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Data:mainOrg"] = "Server=none;Database=none;Trusted_Connection=True;" })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(new StaticTenantContext());
        before?.Invoke(services);
        services.AddFinanceModule(configuration);

        // What the invoice services need from the modules around Finance (as SalesSideRegistrationTests).
        services.AddDbContext<DemandDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddDbContext<SMS.Modules.Warehouse.Data.WarehouseDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddSingleton(Mock.Of<IDeliveryFulfillmentReader>());
        services.AddSingleton(Mock.Of<ISupplierNameLookupService>());
        services.AddSingleton(Mock.Of<ILookupsService>());
        services.AddSingleton(Mock.Of<IBackgroundJobClient>());
        services.AddSingleton(Mock.Of<IProductVariantResolver>());
        return services;
    }

    [Fact]
    public void Without_the_Integration_module_the_gateway_is_the_null_one_and_a_replacement_wins()
    {
        using (var provider = Services().BuildServiceProvider())
        using (var scope = provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<IQuickBooksGateway>().Should().BeOfType<NullQuickBooksGateway>();

        using (var provider = Services(s => s.AddScoped<IQuickBooksGateway, RecordingQuickBooksGateway>()).BuildServiceProvider())
        using (var scope = provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<IQuickBooksGateway>().Should().BeOfType<RecordingQuickBooksGateway>();

        var replaced = Services();
        replaced.Replace(ServiceDescriptor.Scoped<IQuickBooksGateway, RecordingQuickBooksGateway>());
        using (var provider = replaced.BuildServiceProvider())
        using (var scope = provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<IQuickBooksGateway>().Should().BeOfType<RecordingQuickBooksGateway>();
    }

    [Fact]
    public void Both_sources_are_registered_once_per_scope_whichever_way_they_are_asked_for()
    {
        using var provider = Services().BuildServiceProvider();
        using var scope = provider.CreateScope();

        var sources = scope.ServiceProvider.GetServices<IQuickBooksSource>().ToList();
        sources.OfType<SalesInvoiceQuickBooksSource>().Should().ContainSingle()
            .Which.Should().BeSameAs(scope.ServiceProvider.GetRequiredService<SalesInvoiceQuickBooksSource>());
        sources.OfType<BillQuickBooksSource>().Should().ContainSingle()
            .Which.Should().BeSameAs(scope.ServiceProvider.GetRequiredService<BillQuickBooksSource>());
        sources.SelectMany(s => s.Kinds).Should().BeEquivalentTo([SyncKind.SalesInvoice, SyncKind.Bill]);
    }

    [Fact]
    public void The_sales_and_supplier_invoice_services_are_built_with_their_publishers()
    {
        using var provider = Services().BuildServiceProvider();
        using var scope = provider.CreateScope();

        object? Publisher(object service) =>
            service.GetType().GetField("_quickBooks", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service);

        Publisher(scope.ServiceProvider.GetRequiredService<ISalesInvoiceService>()).Should().BeOfType<SalesInvoiceQuickBooksPublisher>();
        Publisher(scope.ServiceProvider.GetRequiredService<IInvoiceService>()).Should().BeOfType<BillQuickBooksPublisher>();
        scope.ServiceProvider.GetRequiredService<IPurchaseOrderLineVariants>().Should().BeOfType<DemandPurchaseOrderLineVariants>();
    }
}

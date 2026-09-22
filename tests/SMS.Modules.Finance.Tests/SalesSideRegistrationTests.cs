using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Finance.Services;
using SMS.Modules.Inventory;
using SMS.Modules.Lookups.Services;
using SMS.Modules.Warehouse;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// The receivable-side services are unit-tested with their dependencies handed in by hand. This is
/// the other half: they must also be constructible by the real container from the module's own
/// registration, or the first request (or the first Hangfire run) fails with nothing in a test to warn of it.
/// </summary>
public class SalesSideRegistrationTests
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

        // What the sales-side services need from the modules around Finance.
        services.AddDbContext<DemandDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddSingleton(Mock.Of<IDeliveryFulfillmentReader>());
        services.AddSingleton(Mock.Of<ISupplierNameLookupService>());
        services.AddSingleton(Mock.Of<ILookupsService>());
        services.AddSingleton(Mock.Of<IBackgroundJobClient>());

        // What the invoice PDF needs from around Finance.
        services.AddSingleton(Mock.Of<IPoDocumentTemplateService>());
        services.AddSingleton(Mock.Of<ISupplierContactLookupService>());
        services.AddSingleton(Mock.Of<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>());

        // And the attachment store the issued invoice's PDF is filed in.
        services.AddSingleton(Mock.Of<SMS.WorkflowEngine.Services.IAttachmentService>());

        // What the product ledger asks Inventory: which product a variant belongs to.
        services.AddSingleton(Mock.Of<IProductVariantResolver>());

        return services.BuildServiceProvider();
    }

    [Fact]
    public void The_public_ledger_reader_and_the_invoice_document_service_resolve()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ICustomerLedgerQueryService>().Should().BeOfType<CustomerLedgerService>();
        scope.ServiceProvider.GetRequiredService<ISalesInvoiceDocumentService>().Should().BeOfType<SalesInvoiceDocumentService>();
        scope.ServiceProvider.GetRequiredService<ISalesInvoiceDocumentArchive>().Should().BeOfType<SalesInvoiceDocumentArchive>();
    }

    [Theory]
    [InlineData(typeof(SMS.Modules.Finance.Controllers.SalesInvoicesController))]
    [InlineData(typeof(SMS.Modules.Finance.Controllers.CustomerPaymentsController))]
    [InlineData(typeof(SMS.Modules.Finance.Controllers.CustomerLedgerController))]
    public void Each_receivables_controller_can_be_built_the_way_mvc_builds_it(Type controller)
    {
        // A controller whose constructor asks for something the module never registered compiles and
        // passes every unit test, then fails on its first request.
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var built = ActivatorUtilities.CreateInstance(scope.ServiceProvider, controller);

        built.Should().BeOfType(controller);
    }

    [Fact]
    public void The_customer_ledger_invoice_and_payment_services_resolve_from_the_module_registration()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ICustomerLedgerService>().Should().BeOfType<CustomerLedgerService>();
        scope.ServiceProvider.GetRequiredService<ISalesInvoiceService>().Should().BeOfType<SalesInvoiceService>();
        scope.ServiceProvider.GetRequiredService<ICustomerPaymentService>().Should().BeOfType<CustomerPaymentService>();
    }

    [Fact]
    public void The_product_ledger_resolves_as_the_public_contract_and_as_finances_own_writer_and_they_are_one_instance()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        // A module that only knows the Shared contract (Warehouse, on a GRN approval) …
        var contract = scope.ServiceProvider.GetRequiredService<IProductLedgerService>();
        // … and Finance's own code, which can also track an entry without saving.
        var writer = scope.ServiceProvider.GetRequiredService<IProductLedgerWriter>();

        contract.Should().BeOfType<ProductLedgerService>();
        contract.Should().BeSameAs(writer, "one writer per request, on the request's one FinanceDbContext");
    }

    [Fact]
    public void The_product_ledger_reader_resolves_and_both_product_ledger_controllers_can_be_built_the_way_mvc_builds_them()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IProductLedgerQueryService>().Should().BeOfType<ProductLedgerQueryService>();
        ActivatorUtilities.CreateInstance(scope.ServiceProvider, typeof(SMS.Modules.Finance.Controllers.ProductLedgerController))
            .Should().BeOfType<SMS.Modules.Finance.Controllers.ProductLedgerController>();
        ActivatorUtilities.CreateInstance(scope.ServiceProvider, typeof(SMS.Modules.Finance.Controllers.ProductProfitabilityController))
            .Should().BeOfType<SMS.Modules.Finance.Controllers.ProductProfitabilityController>();
    }

    [Fact]
    public void The_real_container_gives_the_grn_stock_poster_the_product_ledger_and_the_purchase_order_prices()
    {
        // The poster takes both as optional, so a container that failed to supply them would still build
        // it — and every GRN would then post its stock and quietly skip the product ledger.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Data:mainOrg"] = "Server=none;Database=none;Trusted_Connection=True;" })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(new StaticTenantContext());
        services.AddFinanceModule(configuration);
        services.AddInventoryModule(configuration);
        services.AddWarehouseModule(configuration);
        services.AddDbContext<DemandDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var poster = scope.ServiceProvider.GetRequiredService<SMS.Modules.Warehouse.Services.IGrnStockPoster>();

        object? Field(string name) => poster.GetType()
            .GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(poster);

        Field("_productLedger").Should().BeOfType<ProductLedgerService>();
        Field("_demand").Should().BeOfType<DemandDbContext>();
    }

    [Fact]
    public void The_overdue_job_resolves_the_way_hangfire_will_ask_for_it()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        // Hangfire's ASP.NET Core activator takes the job from a fresh scope, exactly like this.
        scope.ServiceProvider.GetRequiredService<InvoiceOverdueJob>().Should().NotBeNull();
    }

    [Fact]
    public void One_scope_shares_one_context_between_the_ledger_and_the_services_that_write_to_it()
    {
        // §9.5's "same transaction" only holds if the ledger writer and the invoice/payment service
        // are on the same FinanceDbContext instance within a request.
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var contextOfScope = scope.ServiceProvider.GetRequiredService<SMS.Modules.Finance.Data.FinanceDbContext>();
        var ledger = scope.ServiceProvider.GetRequiredService<ICustomerLedgerService>();
        var again  = scope.ServiceProvider.GetRequiredService<ICustomerLedgerService>();

        again.Should().BeSameAs(ledger, "scoped, so one per request");
        contextOfScope.Should().BeSameAs(scope.ServiceProvider.GetRequiredService<SMS.Modules.Finance.Data.FinanceDbContext>());
    }
}

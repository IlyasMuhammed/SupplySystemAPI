using System.Reflection;
using FluentAssertions;
using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using SMS.Modules.Inventory.Integration;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Common;
using SMS.Shared.Integration.QuickBooks;
using Xunit;

namespace SMS.Modules.Inventory.Tests.QuickBooks;

/// <summary>The module's own registration: buildable, Null gateway only by default, and the service really gets the publisher.</summary>
public class VariantQuickBooksRegistrationTests
{
    private static ServiceCollection Services(Action<IServiceCollection>? before = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Data:mainOrg"] = "Server=none;Database=none;Trusted_Connection=True;" })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(new StaticTenantContext());
        services.AddSingleton(Mock.Of<IBackgroundJobClient>());
        before?.Invoke(services);
        services.AddInventoryModule(configuration);
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
    public void The_variant_source_is_one_instance_per_scope_and_the_inventory_service_gets_the_publisher()
    {
        using var provider = Services().BuildServiceProvider();
        using var scope = provider.CreateScope();

        var source = scope.ServiceProvider.GetServices<IQuickBooksSource>().OfType<VariantQuickBooksSource>().Should().ContainSingle().Subject;
        source.Should().BeSameAs(scope.ServiceProvider.GetRequiredService<VariantQuickBooksSource>());

        var service = scope.ServiceProvider.GetRequiredService<IInventoryService>();
        service.GetType().GetField("_quickBooks", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)
            .Should().BeOfType<VariantQuickBooksPublisher>();
    }
}

using System.Reflection;
using AutoMapper;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Modules.Suppliers.Integration;
using SMS.Modules.Suppliers.Models;
using SMS.Modules.Suppliers.Services;
using SMS.Shared.Common;
using SMS.Shared.Integration.QuickBooks;
using Xunit;

namespace SMS.Modules.Suppliers.Tests.QuickBooks;

file sealed class OpenAccess : IUserSupplierAccessService
{
    public Task<bool> IsRestrictedAsync() => Task.FromResult(false);
    public Task<IReadOnlySet<Guid>> GetAllowedSupplierIdsAsync() => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
    public Task<bool> CanAccessSupplierAsync(Guid supplierUuid) => Task.FromResult(true);
}

file sealed class PlainEncryption : IEncryptionService
{
    public string Encrypt(string plaintext) => plaintext;
    public string Decrypt(string ciphertext) => ciphertext;
}

/// <summary>Answers <see cref="ILookupsService.GetCurrencies"/> and nothing else.</summary>
public class CurrencyOnlyLookups : DispatchProxy
{
    internal List<CurrencyModel> Currencies { get; set; } = [];

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        targetMethod?.Name == nameof(ILookupsService.GetCurrencies)
            ? Currencies
            : throw new NotSupportedException(targetMethod?.Name);

    internal static ILookupsService With(params CurrencyModel[] currencies)
    {
        var proxy = Create<ILookupsService, CurrencyOnlyLookups>();
        ((CurrencyOnlyLookups)(object)proxy).Currencies = [.. currencies];
        return proxy;
    }
}

/// <summary>
/// The module's own registration — the half the hand-built unit tests cannot prove: that the container can
/// build every piece, that the Null gateway is only a default, and that the services really get a publisher.
/// </summary>
public class PartnerQuickBooksRegistrationTests
{
    private static ServiceCollection Services(Action<IServiceCollection>? before = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Data:mainOrg"] = "Server=none;Database=none;Trusted_Connection=True;" })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(new StaticTenantContext());
        services.AddSingleton<IUserSupplierAccessService, OpenAccess>();
        services.AddSingleton<IEncryptionService, PlainEncryption>();
        services.AddSingleton<IMapper>(new MapperConfiguration(cfg => cfg.AddProfile<BusinessPartnerMappingProfile>()).CreateMapper());
        before?.Invoke(services);
        services.AddSuppliersModule(configuration);
        return services;
    }

    [Fact]
    public void Without_the_Integration_module_the_gateway_is_the_null_one()
    {
        using var provider = Services().BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IQuickBooksGateway>().Should().BeOfType<NullQuickBooksGateway>();
    }

    [Fact]
    public void A_gateway_registered_first_is_kept_and_one_that_replaces_later_wins()
    {
        using (var provider = Services(s => s.AddScoped<IQuickBooksGateway, RecordingQuickBooksGateway>()).BuildServiceProvider())
        using (var scope = provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<IQuickBooksGateway>().Should().BeOfType<RecordingQuickBooksGateway>();

        var services = Services();
        services.Replace(ServiceDescriptor.Scoped<IQuickBooksGateway, RecordingQuickBooksGateway>());
        using (var provider = services.BuildServiceProvider())
        using (var scope = provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<IQuickBooksGateway>().Should().BeOfType<RecordingQuickBooksGateway>();
    }

    [Fact]
    public void The_partner_source_is_one_instance_per_scope_whichever_way_it_is_asked_for()
    {
        using var provider = Services().BuildServiceProvider();
        using var scope = provider.CreateScope();

        var sources = scope.ServiceProvider.GetServices<IQuickBooksSource>().ToList();
        var partnerSource = sources.OfType<PartnerQuickBooksSource>().Should().ContainSingle().Subject;

        partnerSource.Should().BeSameAs(scope.ServiceProvider.GetRequiredService<PartnerQuickBooksSource>());
        partnerSource.Kinds.Should().BeEquivalentTo([SyncKind.Customer, SyncKind.Vendor]);
        scope.ServiceProvider.GetRequiredService<PartnerQuickBooksPublisher>().Should().NotBeNull();
    }

    [Fact]
    public void Both_partner_services_are_built_with_the_publisher()
    {
        using var provider = Services().BuildServiceProvider();
        using var scope = provider.CreateScope();

        object? Publisher(object service) =>
            service.GetType().GetField("_quickBooks", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service);

        Publisher(scope.ServiceProvider.GetRequiredService<ISuppliersService>()).Should().BeOfType<PartnerQuickBooksPublisher>();
        Publisher(scope.ServiceProvider.GetRequiredService<IBusinessPartnerService>()).Should().BeOfType<PartnerQuickBooksPublisher>();
    }

    [Fact]
    public void Currency_codes_come_from_the_lookups_catalog_when_the_host_has_one()
    {
        var eur = Guid.NewGuid();
        using var provider = Services(s => s.AddSingleton(CurrencyOnlyLookups.With(new CurrencyModel { Id = eur, Name = "Euro", Code = "EUR" })))
            .BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IPartnerCurrencyCodes>().Load().Should().Equal(new Dictionary<Guid, string> { [eur] = "EUR" });
    }

    [Fact]
    public void A_host_without_lookups_still_builds_and_sends_no_currency()
    {
        using var provider = Services().BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IPartnerCurrencyCodes>().Load().Should().BeEmpty();
    }
}

public class LookupsPartnerCurrencyCodesTests
{
    [Fact]
    public void Codes_are_trimmed_and_upper_cased_and_currencies_without_one_are_left_out()
    {
        Guid pkr = Guid.NewGuid(), usd = Guid.NewGuid(), blank = Guid.NewGuid(), none = Guid.NewGuid();
        var lookups = CurrencyOnlyLookups.With(
            new CurrencyModel { Id = pkr,   Name = "Rupee",  Code = " pkr " },
            new CurrencyModel { Id = usd,   Name = "Dollar", Code = "USD" },
            new CurrencyModel { Id = blank, Name = "Blank",  Code = "  " },
            new CurrencyModel { Id = none,  Name = "None",   Code = null },
            new CurrencyModel { Id = usd,   Name = "Dupe",   Code = "XXX" });

        var codes = new LookupsPartnerCurrencyCodes(lookups).Load();

        codes.Should().Equal(new Dictionary<Guid, string> { [pkr] = "PKR", [usd] = "USD" }, "the first answer wins for a duplicated id");
    }
}

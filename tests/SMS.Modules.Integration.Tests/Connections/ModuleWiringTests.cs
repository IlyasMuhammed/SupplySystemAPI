using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Auth;
using SMS.Modules.Integration.Controllers.Admin;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Jobs;
using SMS.Modules.Integration.Providers.QuickBooks;
using SMS.Shared.Common;

namespace SMS.Modules.Integration.Tests.Connections;

/// <summary>
/// The module's real registration (<c>AddIntegrationModule</c>) resolves everything this work package
/// owns — controllers, services, jobs — together with what they need from the other packages. The
/// connection string is a LocalDB name that is never opened: resolving a DbContext does not connect.
/// </summary>
public class ModuleWiringTests
{
    private static ServiceProvider Build()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Data:mainOrg"] = @"Server=(localdb)\MSSQLLocalDB;Database=SMS_QBI_never_opened;Trusted_Connection=True"
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTenantContext();
        services.Configure<AppSettings>(o => o.AesEncryptionKey = "wiring-test-key-0123456789abcdef");
        services.AddSingleton<ITenantSnapshotProvider>(new FakeSnapshots());
        services.AddIntegrationModule(configuration);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    [Theory]
    [InlineData(typeof(ConnectionController))]
    [InlineData(typeof(CallbackController))]
    [InlineData(typeof(SetupController))]
    [InlineData(typeof(ApiClientsController))]
    public void Every_admin_controller_of_this_package_can_be_built(Type controller)
    {
        using var root  = Build();
        using var scope = root.CreateScope();

        ActivatorUtilities.CreateInstance(scope.ServiceProvider, controller).Should().NotBeNull();
    }

    [Fact]
    public void Services_and_jobs_resolve_with_the_real_providers()
    {
        using var root  = Build();
        using var scope = root.CreateScope();
        var sp = scope.ServiceProvider;

        sp.GetRequiredService<ITokenManager>().Should().BeOfType<TokenManager>();
        sp.GetRequiredService<ITokenManager>().Should().BeSameAs(sp.GetRequiredService<TokenManager>());
        sp.GetRequiredService<IConnectionHealth>().Should().BeOfType<ConnectionHealth>();
        sp.GetRequiredService<IReferenceDataReader>().Should().NotBeNull();
        sp.GetRequiredService<IQboServiceContextFactory>().Should().BeOfType<QboServiceContextFactory>();
        sp.GetServices<IAccountingAuthProvider>().Should().ContainSingle().Which.Should().BeOfType<QuickBooksAuthProvider>();
        sp.GetRequiredService<IAccountingProviderRegistry>().Get("QBO").ProviderKey.Should().Be("QBO");
        sp.GetService<IOutboxControl>().Should().NotBeNull("the sync engine registers it; the connection lifecycle suspends and resumes through it");

        sp.GetRequiredService<TokenRefreshJob>().Should().NotBeNull();
        sp.GetRequiredService<ReferenceRefreshJob>().Should().NotBeNull();
        sp.GetRequiredService<StateTokenCleanupJob>().Should().NotBeNull();
    }

    [Fact]
    public async Task The_api_key_scheme_is_added_without_touching_the_default_scheme()
    {
        // Program.cs order: the module first, then JWT registered as the default.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Data:mainOrg"] = @"Server=(localdb)\MSSQLLocalDB;Database=SMS_QBI_never_opened;Trusted_Connection=True"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTenantContext();
        services.AddIntegrationModule(configuration);
        services.AddAuthentication(o =>
            {
                o.DefaultAuthenticateScheme = Auth.FakeJwtHandler.SchemeName;
                o.DefaultChallengeScheme    = Auth.FakeJwtHandler.SchemeName;
            })
            .AddScheme<AuthenticationSchemeOptions, Auth.FakeJwtHandler>(Auth.FakeJwtHandler.SchemeName, _ => { });
        using var root = services.BuildServiceProvider();
        var schemes    = root.GetRequiredService<IAuthenticationSchemeProvider>();

        (await schemes.GetSchemeAsync(ApiKeyDefaults.Scheme))!.HandlerType.Should().Be(typeof(ApiKeyAuthenticationHandler));
        (await schemes.GetDefaultAuthenticateSchemeAsync())!.Name.Should().Be("Bearer", "JWT stays SMS.API's default");
        (await schemes.GetDefaultChallengeSchemeAsync())!.Name.Should().Be("Bearer");
    }
}

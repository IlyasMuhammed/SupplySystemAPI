using FluentAssertions;
using Intuit.Ipp.Core;
using Intuit.Ipp.Core.Configuration;
using Intuit.Ipp.Security;
using Microsoft.Extensions.Options;
using Moq;
using SMS.Modules.Integration.Configuration;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Providers.QuickBooks;

namespace SMS.Modules.Integration.Tests.Connections;

public class QboServiceContextFactoryTests
{
    private static ProviderContext Ctx(IntegrationEnvironment environment = IntegrationEnvironment.Sandbox, string realm = "9130000000000001") =>
        new(12, Guid.NewGuid(), realm, environment);

    private static QboServiceContextFactory Factory(Mock<ITokenManager> tokens, string minorVersion = "75") =>
        new(tokens.Object, Options.Create(new QuickBooksOptions { MinorVersion = minorVersion }));

    [Fact]
    public async Task A_sandbox_company_gets_the_sandbox_url_the_pinned_version_and_a_fresh_token()
    {
        var tokens = new Mock<ITokenManager>();
        tokens.Setup(t => t.GetValidAccessTokenAsync(12, It.IsAny<CancellationToken>())).ReturnsAsync("AT-fresh");

        var context = await Factory(tokens, "75").CreateAsync(Ctx());

        context.RealmId.Should().Be("9130000000000001");
        context.ServiceType.Should().Be(IntuitServicesType.QBO);
        context.BaseUrl.Should().Be("https://sandbox-quickbooks.api.intuit.com/");
        context.MinorVersion.Should().Be("75");
        context.IppConfiguration.Security.Should().BeOfType<OAuth2RequestValidator>()
               .Which.AccessToken.Should().Be("AT-fresh");
        tokens.Verify(t => t.GetValidAccessTokenAsync(12, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void A_production_company_gets_the_production_url()
    {
        var context = QboServiceContextFactory.Build("1", IntegrationEnvironment.Production, "75", "AT");

        context.BaseUrl.Should().Be("https://quickbooks.api.intuit.com/");
    }

    [Fact]
    public void Sdk_retries_and_logging_are_off_and_json_is_used_both_ways()
    {
        var context = QboServiceContextFactory.Build("1", IntegrationEnvironment.Sandbox, "75", "AT");
        var config  = context.IppConfiguration;

        config.RetryPolicy.Should().BeNull("an SDK retry bypasses the sync ledger and can create a record twice");
        config.Logger.RequestLog.EnableRequestResponseLogging.Should().BeFalse("it writes request and response bodies to disk");
        config.Logger.CustomLogger.Should().NotBeNull("the SDK logs through it unconditionally");
        config.AdvancedLogger.RequestAdvancedLog.EnableSerilogRequestResponseLoggingForDebug.Should().BeFalse();
        config.AdvancedLogger.RequestAdvancedLog.EnableSerilogRequestResponseLoggingForTrace.Should().BeFalse();
        config.AdvancedLogger.RequestAdvancedLog.EnableSerilogRequestResponseLoggingForConsole.Should().BeFalse();
        config.AdvancedLogger.RequestAdvancedLog.EnableSerilogRequestResponseLoggingForFile.Should().BeFalse();
        config.Message.Request.SerializationFormat.Should().Be(SerializationFormat.Json);
        config.Message.Response.SerializationFormat.Should().Be(SerializationFormat.Json);
        context.Timeout.Should().Be(QboServiceContextFactory.RequestTimeoutMs);
    }

    [Fact]
    public void Two_contexts_do_not_share_configuration()
    {
        var a = QboServiceContextFactory.Build("1", IntegrationEnvironment.Sandbox, "75", "AT-1");
        var b = QboServiceContextFactory.Build("2", IntegrationEnvironment.Production, "70", "AT-2");

        a.IppConfiguration.Should().NotBeSameAs(b.IppConfiguration);
        a.BaseUrl.Should().Contain("sandbox");
        a.MinorVersion.Should().Be("75");
        ((OAuth2RequestValidator)a.IppConfiguration.Security).AccessToken.Should().Be("AT-1");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task A_connection_without_a_realm_is_unavailable_and_no_token_is_fetched(string realm)
    {
        var tokens = new Mock<ITokenManager>();

        var act = () => Factory(tokens).CreateAsync(Ctx(realm: realm));

        (await act.Should().ThrowAsync<ConnectionUnavailableException>()).Which.Status.Should().Be(ConnectionStatus.NotConnected);
        tokens.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task An_unusable_connection_propagates_from_the_token_manager()
    {
        var tokens = new Mock<ITokenManager>();
        tokens.Setup(t => t.GetValidAccessTokenAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new ConnectionUnavailableException(ConnectionStatus.Revoked, "revoked"));

        var act = () => Factory(tokens).CreateAsync(Ctx());

        (await act.Should().ThrowAsync<ConnectionUnavailableException>()).Which.Status.Should().Be(ConnectionStatus.Revoked);
    }
}

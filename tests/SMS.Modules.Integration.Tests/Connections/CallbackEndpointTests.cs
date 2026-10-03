using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SMS.Modules.Integration.Controllers.Admin;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Tests.Auth;
using SMS.Shared.Common;

namespace SMS.Modules.Integration.Tests.Connections;

/// <summary>
/// The callback over HTTP, in a host with SMS.API's global "authenticated user required" filter: the
/// anonymous endpoint must still answer (with a redirect, never a 401 or an error page), and it is
/// rate limited.
/// </summary>
public class CallbackEndpointTests
{
    private static Task<Microsoft.Extensions.Hosting.IHost> StartAsync(IQuickBooksCallbackService callback) =>
        ApiHostKit.StartAsync([typeof(CallbackController)], services =>
        {
            // The rate-limit policy is registered by the module's own registration; the service behind
            // the controller is replaced afterwards.
            services.AddIntegrationConnections(ApiHostKit.Configuration());
            services.AddSingleton(callback);
            services.AddSingleton<ITenantSnapshotProvider>(new FakeSnapshots());
        });

    [Fact]
    public async Task An_anonymous_callback_gets_a_302_to_the_screen_not_a_401()
    {
        var callback = new Mock<IQuickBooksCallbackService>();
        callback.Setup(c => c.HandleCallbackAsync("the-code", "the-state", "123", null, It.IsAny<CancellationToken>()))
                .ReturnsAsync("http://localhost:4200/portal/pages/integrations/quickbooks?result=connected");

        using var host   = await StartAsync(callback.Object);
        using var client = host.GetTestClient();

        var response = await client.GetAsync("/api/integrations/quickbooks/callback?code=the-code&state=the-state&realmId=123");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Be("http://localhost:4200/portal/pages/integrations/quickbooks?result=connected");
        await host.StopAsync();
    }

    [Fact]
    public async Task Intuits_error_parameter_is_passed_through()
    {
        var callback = new Mock<IQuickBooksCallbackService>();
        callback.Setup(c => c.HandleCallbackAsync(null, "s", null, "access_denied", It.IsAny<CancellationToken>()))
                .ReturnsAsync("http://x/?result=error&reason=access_denied");

        using var host   = await StartAsync(callback.Object);
        using var client = host.GetTestClient();

        var response = await client.GetAsync("/api/integrations/quickbooks/callback?state=s&error=access_denied");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().EndWith("reason=access_denied");
        await host.StopAsync();
    }

    [Fact]
    public async Task The_callback_is_rate_limited()
    {
        var callback = new Mock<IQuickBooksCallbackService>();
        callback.Setup(c => c.HandleCallbackAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("http://x/?result=error&reason=state_invalid");

        using var host   = await StartAsync(callback.Object);
        using var client = host.GetTestClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 31; i++)
            statuses.Add((await client.GetAsync($"/api/integrations/quickbooks/callback?state=guess-{i}")).StatusCode);

        statuses.Take(30).Should().OnlyContain(s => s == HttpStatusCode.Redirect);
        statuses[30].Should().Be(HttpStatusCode.TooManyRequests);
        await host.StopAsync();
    }
}

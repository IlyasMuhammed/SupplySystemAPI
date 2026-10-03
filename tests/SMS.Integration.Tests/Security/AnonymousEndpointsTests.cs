using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using SMS.Integration.Tests.SapAlignment;
using Xunit;

namespace SMS.Integration.Tests.Security;

/// <summary>
/// Which endpoints answer without a signed-in user. An anonymous request has no organization, and
/// <c>TenantContext</c> treats it as unscoped — the tenant filter is bypassed — so an anonymous endpoint that
/// lists or changes records by anything but a single secret key exposes every organization's data. Until
/// 2026-10-02 the whole warehouses controller (list, create, update, delete, stock) and the GRN list and detail
/// were anonymous. Every anonymous action in the real host must be on the reviewed list below.
/// </summary>
public sealed class AnonymousEndpointsTests : IClassFixture<SapWebApplicationFactory>
{
    /// <summary>Controller → its anonymous actions ("*" = the whole controller), each reviewed: it reads by a single secret key or is a public portal/webhook with its own checks.</summary>
    private static readonly Dictionary<string, string[]> Reviewed = new(StringComparer.Ordinal)
    {
        // Sign-in and account recovery: by credentials, refresh token, or a one-time token.
        ["AuthController"]            = ["Login", "Refresh", "Logout", "CreateAccount", "ActivateAccount", "AcceptInvite", "ForgotPassword", "ResetPassword"],
        // Public portals and webhooks: per-link tokens or signed callbacks, rate limited.
        ["RfqPortalController"]       = ["*"],
        ["SroPortalController"]       = ["*"],
        ["PublicTrackingController"]  = ["*"],
        ["CarrierWebhooksController"] = ["*"],
        ["WhatsAppWebhookController"] = ["*"],
        // Intuit's OAuth redirect: bound to a single-use, hashed state token.
        ["CallbackController"]        = ["*"],
    };

    private readonly SapWebApplicationFactory _f;

    public AnonymousEndpointsTests(SapWebApplicationFactory factory) => _f = factory;

    [Fact]
    public void Every_anonymous_action_in_the_host_is_a_reviewed_one()
    {
        var actions = _f.Services.GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items.OfType<ControllerActionDescriptor>().ToList();
        actions.Should().NotBeEmpty();

        var anonymous = actions
            .Where(a => a.EndpointMetadata.OfType<IAllowAnonymous>().Any())
            .Select(a => (Controller: a.ControllerTypeInfo.Name, Action: a.ActionName))
            .Distinct()
            .ToList();

        var unreviewed = anonymous
            .Where(a => !Reviewed.TryGetValue(a.Controller, out var allowed) || !(allowed.Contains("*") || allowed.Contains(a.Action)))
            .Select(a => $"{a.Controller}.{a.Action}")
            .ToList();

        unreviewed.Should().BeEmpty("an anonymous request bypasses the tenant filter; review the endpoint and add it here only if it is safe");
        anonymous.Should().Contain(("AuthController", "Login"), "the scan does see anonymous actions");
    }

    [Theory]
    [InlineData("GET",    "api/grns")]
    [InlineData("GET",    "api/grns/6b0e7c9e-0d1f-4a51-9b2a-3c1d2e4f5a6b")]
    [InlineData("GET",    "api/warehouses")]
    [InlineData("GET",    "api/warehouses/1/structure")]
    [InlineData("GET",    "api/warehouses/1/stock")]
    [InlineData("POST",   "api/warehouses")]
    [InlineData("PATCH",  "api/warehouses/1")]
    [InlineData("DELETE", "api/warehouses/1")]
    [InlineData("POST",   "api/warehouses/1/zones")]
    [InlineData("PATCH",  "api/bins/1/deactivate")]
    public async Task Warehouses_and_GRNs_refuse_a_caller_with_no_token(string method, string url)
    {
        using var client  = _f.CreateAnonymousClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (method is "POST" or "PATCH") request.Content = JsonContent.Create(new { name = "Anonymous" });

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_signed_in_user_still_reaches_them()
    {
        using var client = _f.CreateAdminClient();

        (await client.GetAsync("api/warehouses")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("api/grns")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

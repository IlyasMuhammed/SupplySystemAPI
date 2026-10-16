using System.Net;
using FluentAssertions;
using SMS.Integration.Tests.RouteClassification;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using Xunit;

namespace SMS.Integration.Tests.QuickBooks;

/// <summary>
/// docs/quickbooks/APP-CREDENTIALS.md on the real host (LocalDB): the super admin's platform keys, an organization's own
/// Intuit app behind FEATURE_QBO_OWN_APP, connect → Intuit → callback with the right app, and write-only secrets.
/// Intuit itself is <see cref="FakeQuickBooksAuth"/>, which records which app every call was made as.
/// <para>Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~QuickBooksAppKeysE2ETests</c>.</para>
/// </summary>
public sealed class QuickBooksAppKeysE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private const string Platform = "/api/system/integrations/quickbooks/app";
    private const string OwnKeys  = "/api/integrations/quickbooks/app-keys";
    private const string Qbo      = "/api/integrations/quickbooks";

    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _root;

    public QuickBooksAppKeysE2ETests(SapWebApplicationFactory factory)
    {
        _f    = factory;
        _root = new SapKit(factory, "QAK");
    }

    private static object PlatformKeys(string clientId, string? secret, bool confirm = false) => new
    {
        clientId, clientSecret = secret, environment = "Sandbox",
        redirectUri = "http://localhost/api/integrations/quickbooks/callback",
        frontendReturnUrl = SapWebApplicationFactory.ReturnUrl, confirmBreakingChange = confirm
    };

    /// <summary>Connect → the consent URL (whose client_id is the app used) → Intuit's redirect back.</summary>
    private async Task<(string ClientIdInConsent, string Location)> ConnectAsync(SapKit k, string realm)
    {
        var consent = (await k.Ok(k.Post($"{Qbo}/connect"), "connect")).S("consentUrl")!;
        var query = new Uri(consent).Query.TrimStart('?').Split('&')
            .ToDictionary(p => p.Split('=')[0], p => Uri.UnescapeDataString(p.Split('=')[1]));

        using var anonymous = _f.CreateAnonymousClient();
        var response = await anonymous.GetAsync(
            $"{Qbo}/callback?code=code-{realm}&state={Uri.EscapeDataString(query["state"])}&realmId={realm}");
        return (query["client_id"], response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Platform_keys_are_super_admin_only_write_only_and_used_by_every_organization_without_its_own()
    {
        // Nothing saved yet: the configuration fallback (the factory's QuickBooks__* environment variables).
        var before = await _root.Ok(_root.Get(Platform), "read platform keys");
        before.S("source").Should().Be("Configuration");
        before.B("savedInDatabase").Should().BeFalse();

        var (_, org, _) = await _root.SecondOrganizationAsync("QAKP");
        (await org.Get(Platform)).Status.Should().Be(HttpStatusCode.Forbidden, "an organization admin never sees the platform keys");
        (await org.Put(Platform, PlatformKeys("x", "y"))).Status.Should().Be(HttpStatusCode.Forbidden);

        var saved = await _root.Ok(_root.Put(Platform, PlatformKeys("sap-e2e-client-id", "db-secret-123")), "save platform keys");
        saved.S("source").Should().Be("Platform");
        saved.B("hasSecret").Should().BeTrue();
        saved.Has("clientSecret").Should().BeFalse("D-4: never returned");
        (await _root.Get(Platform)).Raw.Should().NotContain("db-secret-123");

        var stored = (await _f.QueryAsync("SELECT EncryptedClientSecret FROM integration.QuickBooksAppCredentials WHERE OrganizationId IS NULL"))
            .Should().ContainSingle().Subject;
        ((string)stored["EncryptedClientSecret"]!).Should().NotContain("db-secret-123", "stored encrypted");

        var status = await org.Ok(org.Get($"{Qbo}/connection"), "org status");
        status.B("appConfigured").Should().BeTrue();
        status.S("appSource").Should().Be("Platform");

        var (clientId, location) = await ConnectAsync(org, "9130000000000101");
        clientId.Should().Be("sap-e2e-client-id");
        location.Should().EndWith("result=connected");
        lock (_f.Auth.AppsUsed) _f.Auth.AppsUsed.Should().Contain("Exchange:sap-e2e-client-id:Platform:Sandbox");

        // Replacing the platform Client ID under a live connection needs confirmation (D-8).
        var refused = await _root.Put(Platform, PlatformKeys("another-platform-id", null));
        refused.Status.Should().Be(HttpStatusCode.Conflict);
        refused.Message.Should().Contain("must reconnect");
    }

    [Fact]
    public async Task An_organization_uses_its_own_Intuit_app_only_once_the_super_admin_enables_it()
    {
        var (orgId, k, _) = await _root.SecondOrganizationAsync("QAKT");

        var card = await k.Ok(k.Get(OwnKeys), "own keys card");
        card.B("featureEnabled").Should().BeFalse("in no plan — licensed by hand (D-9)");
        card.S("redirectUriToRegister").Should().NotBeNullOrWhiteSpace();
        (await k.Put(OwnKeys, new { clientId = "own-e2e-id", clientSecret = "own-e2e-secret", environment = "Production" }))
            .Status.Should().Be(HttpStatusCode.Forbidden, "FEATURE_QBO_OWN_APP is off");

        await _root.SetFeatureAsync(orgId, "FEATURE_QBO_OWN_APP", true);

        var saved = await k.Ok(k.Put(OwnKeys, new { clientId = "own-e2e-id", clientSecret = "own-e2e-secret", environment = "Production" }),
            "save own keys");
        saved.B("hasOwnKeys").Should().BeTrue();
        saved.S("newConnectionsUse").Should().Be("Tenant");
        saved.Has("clientSecret").Should().BeFalse();
        (await k.Get(OwnKeys)).Raw.Should().NotContain("own-e2e-secret");

        var (clientId, location) = await ConnectAsync(k, "9130000000000202");
        clientId.Should().Be("own-e2e-id");
        location.Should().EndWith("result=connected");
        lock (_f.Auth.AppsUsed) _f.Auth.AppsUsed.Should().Contain("Exchange:own-e2e-id:Tenant:Production");

        var connection = (await _f.QueryAsync(
                "SELECT AppClientId, AppSource, Environment FROM integration.Connections WHERE OrganizationId = @o", ("@o", orgId)))
            .Should().ContainSingle().Subject;
        connection["AppClientId"].Should().Be("own-e2e-id");
        connection["AppSource"].Should().Be("Tenant");
        connection["Environment"].Should().Be("Production");
        (await k.Ok(k.Get($"{Qbo}/connection"), "status")).S("connectedWithAppSource").Should().Be("Tenant");

        // D-8: connected through them — a new Client ID or removing them is refused; a secret rotation is fine.
        (await k.Put(OwnKeys, new { clientId = "other-id", environment = "Production" })).Status.Should().Be(HttpStatusCode.Conflict);
        (await k.Delete(OwnKeys)).Status.Should().Be(HttpStatusCode.Conflict);
        await k.Ok(k.Put(OwnKeys, new { clientId = "own-e2e-id", clientSecret = "rotated", environment = "Production" }), "rotate secret");

        // Another organization never sees them.
        var (_, other, _) = await _root.SecondOrganizationAsync("QAKO");
        (await other.Ok(other.Get(OwnKeys), "other org card")).B("hasOwnKeys").Should().BeFalse();

        // After a disconnect the keys can go; new connections then use the platform's app.
        await k.Ok(k.Delete($"{Qbo}/connection"), "disconnect");
        lock (_f.Auth.AppsUsed) _f.Auth.AppsUsed.Should().Contain("Revoke:own-e2e-id:Tenant:Production");
        var removed = await k.Ok(k.Delete(OwnKeys), "remove own keys");
        removed.B("hasOwnKeys").Should().BeFalse();
        removed.S("newConnectionsUse").Should().NotBe("Tenant");
    }
}

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Integration.Tests.Connections;

/// <summary>
/// docs/quickbooks/APP-CREDENTIALS.md — the platform's and an organization's own Intuit app keys: which keys a new
/// connection uses (D-2), the pinning of a connection to the app that issued its tokens (D-3), write-only secrets (D-4),
/// the feature switch (D-7/D-9) and the guards against stranding a live connection (D-8).
/// </summary>
public class AppCredentialsTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();
    private const string Realm = "9130000000000001";

    private static ConnectionsHarness Harness(bool ownAppFor = false, bool configured = true)
    {
        var h = ConnectionsHarness.Create(realTenant: true, qbo: o => { if (!configured) o.ClientId = ""; });
        if (ownAppFor) h.Snapshots.Set(OrgA, FakeSnapshots.Active("MODULE_INTEGRATION", ModuleCodes.QuickBooksOwnApp));
        return h;
    }

    private static async Task<T> As<T>(ConnectionsHarness h, Guid org, Func<IServiceProvider, Task<T>> act)
    {
        await using var scope = h.Scope(org);
        return await act(scope.ServiceProvider);
    }

    private static Task<T> Keys<T>(ConnectionsHarness h, Guid org, Func<IQuickBooksAppSettingsService, Task<T>> act) =>
        As(h, org, sp => act(sp.GetRequiredService<IQuickBooksAppSettingsService>()));

    /// <summary>The call must throw <typeparamref name="TEx"/>; returns its message.</summary>
    private static async Task<string> Refused<TEx>(ConnectionsHarness h, Guid org, Func<IQuickBooksAppSettingsService, Task> act)
        where TEx : Exception
    {
        await using var scope = h.Scope(org);
        var service = scope.ServiceProvider.GetRequiredService<IQuickBooksAppSettingsService>();
        return (await service.Awaiting(act).Should().ThrowAsync<TEx>()).Which.Message;
    }

    private static Task<OAuthAppView?> NewConnectionApp(ConnectionsHarness h, Guid org) =>
        As(h, org, async sp =>
        {
            var app = await sp.GetRequiredService<IQuickBooksAppResolver>().ForNewConnectionAsync(org);
            return app is null ? null : new OAuthAppView(app.ClientId, app.ClientSecret, app.Source, app.Environment, app.RedirectUri);
        });

    private sealed record OAuthAppView(string ClientId, string Secret, AppCredentialSource Source, IntegrationEnvironment Environment, string RedirectUri);

    private static SavePlatformAppKeysRequest Platform(string clientId = "platform-id", string? secret = "platform-secret",
        string environment = "Production", bool confirm = false) => new()
    {
        ClientId = clientId, ClientSecret = secret, Environment = environment,
        RedirectUri = "https://api.example.com/api/integrations/quickbooks/callback",
        FrontendReturnUrl = "https://app.example.com/portal/pages/integrations/quickbooks", ConfirmBreakingChange = confirm
    };

    private static SaveTenantAppKeysRequest Own(string clientId = "own-id", string? secret = "own-secret", string environment = "Sandbox") =>
        new() { ClientId = clientId, ClientSecret = secret, Environment = environment };

    private static async Task ConnectThroughCallbackAsync(ConnectionsHarness h, Guid org)
    {
        await As(h, org, sp => sp.GetRequiredService<IQuickBooksConnectionService>().ConnectAsync(7));
        var state = h.Auth.ConsentStates.Last();
        await using var scope = h.AnonymousScope();
        var redirect = await scope.ServiceProvider.GetRequiredService<IQuickBooksCallbackService>()
            .HandleCallbackAsync("code-" + org.ToString("N")[..6], state, Realm, null);
        redirect.Should().Contain("result=connected");
    }

    private static async Task<IntegrationConnection> ConnectionOf(ConnectionsHarness h, Guid org)
    {
        await using var db = h.OpenAll();
        return await db.Connections.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.OrganizationId == org);
    }

    // ── D-2: which keys a new connection uses ────────────────────────────────────────────────

    [Fact]
    public async Task With_nothing_saved_the_configuration_is_the_fallback()
    {
        await using var h = Harness();

        var app = await NewConnectionApp(h, OrgA);

        app!.Source.Should().Be(AppCredentialSource.Configuration);
        app.ClientId.Should().Be("test-client-id");
    }

    [Fact]
    public async Task With_nothing_saved_and_no_configuration_there_is_no_app_and_Connect_says_where_to_set_it()
    {
        await using var h = Harness(configured: false);

        (await NewConnectionApp(h, OrgA)).Should().BeNull();
        var status = await As(h, OrgA, sp => sp.GetRequiredService<IQuickBooksConnectionService>().GetStatusAsync());
        status.AppConfigured.Should().BeFalse();
        status.AppSource.Should().BeNull();
    }

    [Fact]
    public async Task Saved_platform_keys_win_over_the_configuration_and_the_secret_is_stored_encrypted()
    {
        await using var h = Harness();

        var model = await Keys(h, OrgA, k => k.SavePlatformAsync(Platform(), userId: 1));

        model.Source.Should().Be("Platform");
        model.SavedInDatabase.Should().BeTrue();
        model.ClientId.Should().Be("platform-id");
        model.HasSecret.Should().BeTrue();
        model.Environment.Should().Be("Production");
        model.ConfigurationClientId.Should().Be("test-client-id");

        var app = await NewConnectionApp(h, OrgB);
        app!.Source.Should().Be(AppCredentialSource.Platform);
        app.Secret.Should().Be("platform-secret");
        app.Environment.Should().Be(IntegrationEnvironment.Production);
        app.RedirectUri.Should().Be("https://api.example.com/api/integrations/quickbooks/callback");

        await using var db = h.OpenAll();
        var row = await db.AppCredentials.SingleAsync();
        row.OrganizationId.Should().BeNull();
        row.EncryptedClientSecret.Should().NotContain("platform-secret");
    }

    [Fact]
    public async Task A_blank_secret_keeps_the_stored_one_and_a_first_save_needs_one()
    {
        await using var h = Harness();

        (await Refused<BadRequestException>(h, OrgA, k => k.SavePlatformAsync(Platform(secret: null), 1)))
            .Should().Contain("secret is required");

        await Keys(h, OrgA, k => k.SavePlatformAsync(Platform(), 1));
        await Keys(h, OrgA, k => k.SavePlatformAsync(Platform(secret: "  "), 1));

        (await NewConnectionApp(h, OrgA))!.Secret.Should().Be("platform-secret");
    }

    [Theory]
    [InlineData("", "Production", "https://x/cb", "Client ID is required")]
    [InlineData("has space", "Production", "https://x/cb", "cannot contain spaces")]
    [InlineData("id", "Staging", "https://x/cb", "Sandbox or Production")]
    [InlineData("id", "Production", "not-a-url", "full http(s) address")]
    [InlineData("id", "Production", "", "Callback URL is required")]
    public async Task Platform_keys_are_validated(string clientId, string environment, string redirect, string message)
    {
        await using var h = Harness();
        var request = Platform(clientId: clientId, environment: environment);
        request.RedirectUri = redirect;

        (await Refused<BadRequestException>(h, OrgA, k => k.SavePlatformAsync(request, 1))).Should().Contain(message);
    }

    // ── D-7/D-9: the organization's own app ──────────────────────────────────────────────────

    [Fact]
    public async Task Own_keys_cannot_be_saved_without_the_feature()
    {
        await using var h = Harness(ownAppFor: false);

        (await Refused<ForbiddenException>(h, OrgA, k => k.SaveTenantAsync(Own(), 7))).Should().Contain("not enabled");
        (await Keys(h, OrgA, k => k.GetTenantAsync())).FeatureEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Own_keys_are_used_for_that_organization_only()
    {
        await using var h = Harness(ownAppFor: true);
        await Keys(h, OrgA, k => k.SavePlatformAsync(Platform(), 1));

        var mine = await Keys(h, OrgA, k => k.SaveTenantAsync(Own(), 7));

        mine.FeatureEnabled.Should().BeTrue();
        mine.HasOwnKeys.Should().BeTrue();
        mine.ClientId.Should().Be("own-id");
        mine.NewConnectionsUse.Should().Be("Tenant");
        mine.RedirectUriToRegister.Should().Be("https://api.example.com/api/integrations/quickbooks/callback", "D-5: the platform's callback URL");

        (await NewConnectionApp(h, OrgA))!.Source.Should().Be(AppCredentialSource.Tenant);
        (await NewConnectionApp(h, OrgB))!.Source.Should().Be(AppCredentialSource.Platform, "another organization never sees OrgA's keys");
        (await Keys(h, OrgB, k => k.GetTenantAsync())).HasOwnKeys.Should().BeFalse();
    }

    [Fact]
    public async Task Switching_the_feature_off_stops_new_connections_using_own_keys()
    {
        await using var h = Harness(ownAppFor: true);
        await Keys(h, OrgA, k => k.SaveTenantAsync(Own(), 7));

        h.Snapshots.Set(OrgA, FakeSnapshots.Active());

        (await NewConnectionApp(h, OrgA))!.Source.Should().Be(AppCredentialSource.Configuration);
    }

    [Fact]
    public async Task The_platforms_own_keys_cannot_be_saved_as_an_organizations()
    {
        await using var h = Harness(ownAppFor: true);
        await Keys(h, OrgA, k => k.SavePlatformAsync(Platform(), 1));

        (await Refused<BadRequestException>(h, OrgA, k => k.SaveTenantAsync(Own(clientId: "platform-id"), 7)))
            .Should().Contain("platform's QuickBooks keys");
    }

    // ── D-3: a connection is pinned to the app that issued its tokens ───────────────────────

    [Fact]
    public async Task Connect_callback_and_refresh_all_use_the_organizations_own_app()
    {
        await using var h = Harness(ownAppFor: true);
        await Keys(h, OrgA, k => k.SaveTenantAsync(Own(environment: "Production"), 7));

        await ConnectThroughCallbackAsync(h, OrgA);

        var c = await ConnectionOf(h, OrgA);
        c.AppClientId.Should().Be("own-id");
        c.AppSource.Should().Be(AppCredentialSource.Tenant);
        c.Environment.Should().Be(IntegrationEnvironment.Production);

        await As(h, OrgA, sp => sp.GetRequiredService<TokenManager>().ForceRefreshAsync(c.Id));

        h.Auth.AppsUsed.Should().Equal("Consent:own-id:Tenant", "Exchange:own-id:Tenant", "Refresh:own-id:Tenant");
        var status = await As(h, OrgA, sp => sp.GetRequiredService<IQuickBooksConnectionService>().GetStatusAsync());
        status.ConnectedWithAppSource.Should().Be("Tenant");
    }

    [Fact]
    public async Task A_connection_keeps_its_app_after_the_feature_is_switched_off()
    {
        await using var h = Harness(ownAppFor: true);
        await Keys(h, OrgA, k => k.SaveTenantAsync(Own(), 7));
        await ConnectThroughCallbackAsync(h, OrgA);
        var c = await ConnectionOf(h, OrgA);

        h.Snapshots.Set(OrgA, FakeSnapshots.Active());
        await As(h, OrgA, sp => sp.GetRequiredService<TokenManager>().ForceRefreshAsync(c.Id));

        h.Auth.AppsUsed.Last().Should().Be("Refresh:own-id:Tenant", "the tokens were issued to the organization's app");
    }

    [Fact]
    public async Task A_connection_from_before_the_change_refreshes_with_the_platform_keys()
    {
        await using var h = Harness();
        var c = await h.SeedConnectionAsync(OrgA);           // AppClientId null
        await Keys(h, OrgA, k => k.SavePlatformAsync(Platform(clientId: "test-client-id", environment: "Sandbox"), 1));

        await As(h, OrgA, sp => sp.GetRequiredService<TokenManager>().ForceRefreshAsync(c.Id));

        h.Auth.AppsUsed.Should().Equal("Refresh:test-client-id:Platform");
    }

    [Fact]
    public async Task The_callback_exchanges_with_the_app_the_consent_was_built_with_even_if_the_keys_change_meanwhile()
    {
        await using var h = Harness(ownAppFor: true);
        await Keys(h, OrgA, k => k.SaveTenantAsync(Own(), 7));
        await As(h, OrgA, sp => sp.GetRequiredService<IQuickBooksConnectionService>().ConnectAsync(7));
        var state = h.Auth.ConsentStates.Last();

        // Own keys removed between Connect and Intuit's redirect: the code belongs to an app we no longer hold.
        await Keys(h, OrgA, k => k.DeleteTenantAsync(7));

        await using var scope = h.AnonymousScope();
        var redirect = await scope.ServiceProvider.GetRequiredService<IQuickBooksCallbackService>()
            .HandleCallbackAsync("code", state, Realm, null);

        redirect.Should().Contain("reason=not_configured");
        h.Auth.ExchangedCodes.Should().BeEmpty("never exchanged with a different app");
    }

    // ── D-8: no silent stranding ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Own_keys_cannot_be_changed_or_removed_while_connected_through_them_but_the_secret_can_be_rotated()
    {
        await using var h = Harness(ownAppFor: true);
        await Keys(h, OrgA, k => k.SaveTenantAsync(Own(), 7));
        await ConnectThroughCallbackAsync(h, OrgA);

        (await Refused<ConflictException>(h, OrgA, k => k.SaveTenantAsync(Own(clientId: "other-id"), 7)))
            .Should().Contain("Disconnect QuickBooks first");
        await Refused<ConflictException>(h, OrgA, k => k.SaveTenantAsync(Own(environment: "Production"), 7));
        await Refused<ConflictException>(h, OrgA, k => k.DeleteTenantAsync(7));

        await Keys(h, OrgA, k => k.SaveTenantAsync(Own(secret: "rotated-secret"), 7));
        (await NewConnectionApp(h, OrgA))!.Secret.Should().Be("rotated-secret");

        await using var db = h.OpenAll();
        var audit = await db.SettingsAudit.IgnoreQueryFilters().Where(a => a.Area == "AppKeys").ToListAsync();
        audit.Should().ContainSingle(a => a.Action == "OwnAppKeysSaved");
        audit.Should().OnlyContain(a => !(a.AfterJson ?? "").Contains("rotated-secret") && !(a.BeforeJson ?? "").Contains("own-secret"));
    }

    [Fact]
    public async Task After_a_disconnect_own_keys_can_be_changed()
    {
        await using var h = Harness(ownAppFor: true);
        await Keys(h, OrgA, k => k.SaveTenantAsync(Own(), 7));
        await ConnectThroughCallbackAsync(h, OrgA);
        await As(h, OrgA, sp => sp.GetRequiredService<IQuickBooksConnectionService>().DisconnectAsync(7));

        (await Keys(h, OrgA, k => k.SaveTenantAsync(Own(clientId: "other-id"), 7))).ClientId.Should().Be("other-id");
        h.Auth.AppsUsed.Should().Contain("Revoke:own-id:Tenant", "revoked as the app that issued the token");
    }

    [Fact]
    public async Task Changing_the_platform_Client_ID_under_live_connections_needs_confirmation_and_pins_them_to_the_old_keys()
    {
        await using var h = Harness();
        var c = await h.SeedConnectionAsync(OrgA);           // made with the configuration keys, unpinned

        (await Refused<ConflictException>(h, OrgA, k => k.SavePlatformAsync(Platform(clientId: "new-id"), 1)))
            .Should().Contain("1 organization(s)").And.Contain("must reconnect");

        var model = await Keys(h, OrgA, k => k.SavePlatformAsync(Platform(clientId: "new-id", confirm: true), 1));
        model.ClientId.Should().Be("new-id");

        var pinned = await ConnectionOf(h, OrgA);
        pinned.AppClientId.Should().Be("test-client-id", "pinned to the keys its tokens came from");
        pinned.AppSource.Should().Be(AppCredentialSource.Configuration);

        // The old keys are still the configuration fallback, so it keeps refreshing with them.
        await As(h, OrgA, sp => sp.GetRequiredService<TokenManager>().ForceRefreshAsync(c.Id));
        h.Auth.AppsUsed.Last().Should().Be("Refresh:test-client-id:Configuration");
    }

    [Fact]
    public async Task A_connection_whose_keys_are_gone_fails_to_refresh_with_a_clear_message_and_stays_connected()
    {
        await using var h = Harness(configured: false);
        var c = await h.SeedConnectionAsync(OrgA, accessExpiresAt: DateTime.UtcNow.AddMinutes(-5));
        await using (var db = h.OpenAs(OrgA))
        {
            var row = await db.Connections.SingleAsync();
            row.AppClientId = "vanished-id";
            row.AppSource   = AppCredentialSource.Tenant;
            await db.SaveChangesAsync();
        }

        var act = () => As(h, OrgA, sp => sp.GetRequiredService<ITokenManager>().GetValidAccessTokenAsync(c.Id));

        await act.Should().ThrowAsync<TokenRefreshFailedException>().WithMessage("*no longer stored*");
        var after = await ConnectionOf(h, OrgA);
        after.Status.Should().Be(ConnectionStatus.Live, "missing keys are not a revoked grant");
        after.LastError.Should().Contain("no longer stored");
        h.Auth.RefreshedWith.Should().BeEmpty();
    }

    [Fact]
    public async Task The_platform_screen_counts_connections_using_its_keys_and_organizations_with_their_own()
    {
        await using var h = Harness(ownAppFor: true);
        await h.SeedConnectionAsync(OrgB);
        await Keys(h, OrgA, k => k.SaveTenantAsync(Own(), 7));

        var model = await Keys(h, OrgA, k => k.GetPlatformAsync());

        model.Source.Should().Be("Configuration");
        model.SavedInDatabase.Should().BeFalse();
        model.ConnectionsUsingPlatformKeys.Should().Be(1);
        model.OrganizationsWithOwnKeys.Should().Be(1);
    }
}

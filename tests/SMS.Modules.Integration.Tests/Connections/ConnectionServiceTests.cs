using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Core.Settings;
using SMS.Modules.Integration.Domain;
using SMS.Shared.Exceptions;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Tests.Connections;

public class ConnectionServiceTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private static async Task<T> WithService<T>(ConnectionsHarness h, Guid org, Func<IQuickBooksConnectionService, Task<T>> act)
    {
        await using var scope = h.Scope(org);
        return await act(scope.ServiceProvider.GetRequiredService<IQuickBooksConnectionService>());
    }

    // ── Status ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Status_without_a_connection_is_NotConnected_and_says_whether_the_app_is_configured()
    {
        await using var h = ConnectionsHarness.Create(qbo: o => o.ClientSecret = "");

        var status = await WithService(h, OrgA, s => s.GetStatusAsync());

        status.Status.Should().Be("NotConnected");
        status.IsConnected.Should().BeFalse();
        status.AppConfigured.Should().BeFalse();
        status.Environment.Should().Be("Sandbox");
        status.Mode.Should().Be("DryRun");
    }

    [Fact]
    public async Task Status_reports_the_company_expiries_mode_and_reconnect_warning()
    {
        await using var h = ConnectionsHarness.Create(jobs: j => j.ReconnectWarningDays = 30);
        var c = await h.SeedConnectionAsync(OrgA, refreshExpiresAt: DateTime.UtcNow.AddDays(20));
        await h.SeedSettingsAsync(c, s => s.Mode = SyncMode.Live);

        var status = await WithService(h, OrgA, s => s.GetStatusAsync());

        status.Status.Should().Be("Live");
        status.IsConnected.Should().BeTrue();
        status.RealmId.Should().Be("9130000000000001");
        status.CompanyName.Should().Be("Seeded Company");
        status.Mode.Should().Be("Live");
        status.ReconnectSoon.Should().BeTrue();
        status.RefreshTokenExpiresAt.Should().NotBeNull();
        status.AppConfigured.Should().BeTrue();
    }

    [Fact]
    public async Task Status_is_tenant_scoped()
    {
        await using var h = ConnectionsHarness.Create();
        await h.SeedConnectionAsync(OrgA);

        (await WithService(h, OrgB, s => s.GetStatusAsync())).Status.Should().Be("NotConnected");
    }

    [Fact]
    public async Task An_abandoned_Connecting_shows_as_NotConnected_once_its_state_token_is_gone()
    {
        await using var h = ConnectionsHarness.Create();
        var c = await h.SeedConnectionAsync(OrgA, ConnectionStatus.Connecting, realmId: null, withTokens: false);
        await using (var db = h.OpenAs(OrgA))
        {
            db.OAuthStateTokens.Add(new OAuthStateToken
            {
                OrganizationId = OrgA, TokenHash = new string('a', 64), UserId = 7, ExpiresAt = DateTime.UtcNow.AddMinutes(-1)
            });
            await db.SaveChangesAsync();
        }

        (await WithService(h, OrgA, s => s.GetStatusAsync())).Status.Should().Be("NotConnected");
        (await h.ReloadConnectionAsync(c.Id)).Status.Should().Be(ConnectionStatus.NotConnected);
    }

    [Fact]
    public async Task A_Connecting_with_a_live_state_token_stays_Connecting()
    {
        await using var h = ConnectionsHarness.Create();
        await WithService(h, OrgA, s => s.ConnectAsync(7));

        (await WithService(h, OrgA, s => s.GetStatusAsync())).Status.Should().Be("Connecting");
    }

    // ── Connect ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Connect_returns_the_consent_url_and_records_a_hashed_state_for_this_org()
    {
        await using var h = ConnectionsHarness.Create();

        var response = await WithService(h, OrgA, s => s.ConnectAsync(7));

        h.Auth.ConsentStates.Should().ContainSingle();
        var state = h.Auth.ConsentStates.Single();
        response.ConsentUrl.Should().Contain(state);

        await using var db = h.OpenAs(OrgA);
        var token = await db.OAuthStateTokens.SingleAsync();
        token.TokenHash.Should().Be(OAuthStateService.Hash(state));
        token.OrganizationId.Should().Be(OrgA);
        token.UserId.Should().Be(7);

        var connection = await db.Connections.SingleAsync();
        connection.Status.Should().Be(ConnectionStatus.Connecting);
        connection.OrganizationId.Should().Be(OrgA);
        connection.EncryptedAccessToken.Should().BeNull();

        (await db.SettingsAudit.SingleAsync()).Action.Should().Be("ConnectStarted");
    }

    [Fact]
    public async Task Connect_is_refused_when_the_server_has_no_Intuit_keys()
    {
        await using var h = ConnectionsHarness.Create(qbo: o => o.ClientId = "");

        var act = () => WithService(h, OrgA, s => s.ConnectAsync(7));

        await act.Should().ThrowAsync<ConflictException>().WithMessage("*not configured*");
        h.Auth.ConsentStates.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Connected")]
    [InlineData("NeedsSetup")]
    [InlineData("Live")]
    public async Task Connect_is_refused_while_a_company_is_connected(string statusName)
    {
        var status = Enum.Parse<ConnectionStatus>(statusName);
        await using var h = ConnectionsHarness.Create();
        await h.SeedConnectionAsync(OrgA, status);

        var act = () => WithService(h, OrgA, s => s.ConnectAsync(7));

        await act.Should().ThrowAsync<ConflictException>().WithMessage("*Disconnect first*");
    }

    [Theory]
    [InlineData("Revoked")]
    [InlineData("Expired")]
    public async Task Reconnecting_keeps_the_Revoked_or_Expired_banner_until_it_completes(string statusName)
    {
        var status = Enum.Parse<ConnectionStatus>(statusName);
        await using var h = ConnectionsHarness.Create();
        var c = await h.SeedConnectionAsync(OrgA, status);

        await WithService(h, OrgA, s => s.ConnectAsync(7));

        (await h.ReloadConnectionAsync(c.Id)).Status.Should().Be(status);
    }

    [Fact]
    public async Task When_Intuit_cannot_be_reached_nothing_is_saved()
    {
        await using var h = ConnectionsHarness.Create();
        h.Auth.ConsentUrl = _ => throw new AccountingAuthException("Could not reach Intuit to start the connection (HttpRequestException).");

        var act = () => WithService(h, OrgA, s => s.ConnectAsync(7));

        await act.Should().ThrowAsync<ConflictException>().WithMessage("Could not reach Intuit*");
        await using var db = h.OpenAs(OrgA);
        (await db.OAuthStateTokens.CountAsync()).Should().Be(0);
        (await db.Connections.CountAsync()).Should().Be(0);
    }

    // ── Test ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Test_without_a_usable_connection_answers_not_ok_without_calling_QuickBooks()
    {
        await using var h = ConnectionsHarness.Create();
        await h.SeedConnectionAsync(OrgA, ConnectionStatus.Revoked);

        var result = await WithService(h, OrgA, s => s.TestAsync());

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("revoked");
        h.Provider.Verify(p => p.GetCompanyInfoAsync(It.IsAny<ProviderContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_successful_test_refreshes_the_company_name_and_clears_the_last_error()
    {
        await using var h = ConnectionsHarness.Create();
        var c = await h.SeedConnectionAsync(OrgA);
        await using (var db = h.OpenAs(OrgA))
        {
            (await db.Connections.SingleAsync()).LastError = "old problem";
            await db.SaveChangesAsync();
        }
        h.Provider.Setup(p => p.GetCompanyInfoAsync(It.IsAny<ProviderContext>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(ProviderResult<RemoteCompanyInfo>.Ok(new RemoteCompanyInfo("Renamed Co", null, "PK", null)));

        var result = await WithService(h, OrgA, s => s.TestAsync());

        result.Ok.Should().BeTrue();
        result.CompanyName.Should().Be("Renamed Co");
        var stored = await h.ReloadConnectionAsync(c.Id);
        stored.CompanyName.Should().Be("Renamed Co");
        stored.LastError.Should().BeNull();
        h.Provider.Verify(p => p.GetCompanyInfoAsync(
            It.Is<ProviderContext>(x => x.ConnectionId == c.Id && x.OrganizationId == OrgA && x.RealmId == "9130000000000001"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_test_answered_with_auth_revoked_marks_the_connection_Revoked()
    {
        await using var h = ConnectionsHarness.Create();
        var c = await h.SeedConnectionAsync(OrgA);
        h.Provider.Setup(p => p.GetCompanyInfoAsync(It.IsAny<ProviderContext>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(ProviderResult<RemoteCompanyInfo>.Fail(ProviderOutcomeKind.AuthRevoked, "401", "AuthenticationFailed"));

        var result = await WithService(h, OrgA, s => s.TestAsync());

        result.Ok.Should().BeFalse();
        result.Message.Should().Be("AuthenticationFailed");
        (await h.ReloadConnectionAsync(c.Id)).Status.Should().Be(ConnectionStatus.Revoked);
        h.Outbox.Suspended.Should().ContainSingle();
    }

    [Fact]
    public async Task A_test_that_hits_a_transient_failure_does_not_change_the_connection()
    {
        await using var h = ConnectionsHarness.Create();
        var c = await h.SeedConnectionAsync(OrgA);
        h.Provider.Setup(p => p.GetCompanyInfoAsync(It.IsAny<ProviderContext>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(ProviderResult<RemoteCompanyInfo>.Fail(ProviderOutcomeKind.Transient, "503", "Service unavailable"));

        var result = await WithService(h, OrgA, s => s.TestAsync());

        result.Ok.Should().BeFalse();
        (await h.ReloadConnectionAsync(c.Id)).Status.Should().Be(ConnectionStatus.Live);
    }

    [Fact]
    public async Task A_test_that_finds_the_connection_unusable_reports_it()
    {
        await using var h = ConnectionsHarness.Create();
        await h.SeedConnectionAsync(OrgA);
        h.Provider.Setup(p => p.GetCompanyInfoAsync(It.IsAny<ProviderContext>(), It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new ConnectionUnavailableException(ConnectionStatus.Expired, "The QuickBooks authorization expired."));

        var result = await WithService(h, OrgA, s => s.TestAsync());

        result.Ok.Should().BeFalse();
        result.Message.Should().Be("The QuickBooks authorization expired.");
    }

    // ── Disconnect ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Disconnect_revokes_at_Intuit_forgets_tokens_and_keeps_realm_and_maps()
    {
        await using var h = ConnectionsHarness.Create();
        var c = await h.SeedConnectionAsync(OrgA, refresh: "RT-to-revoke");
        await using (var db = h.OpenAs(OrgA))
        {
            db.EntityMaps.Add(new EntityMap
            {
                OrganizationId = OrgA, ConnectionId = c.Id, Kind = SyncKind.Customer, ExternalId = "cust-1",
                RemoteId = "58", State = SyncState.Synced
            });
            await db.SaveChangesAsync();
        }

        var status = await WithService(h, OrgA, s => s.DisconnectAsync(7));

        status.Status.Should().Be("NotConnected");
        status.IsConnected.Should().BeFalse();
        status.RealmId.Should().Be("9130000000000001");
        h.Auth.RevokedTokens.Should().Equal("RT-to-revoke");

        var stored = await h.ReloadConnectionAsync(c.Id);
        stored.Status.Should().Be(ConnectionStatus.NotConnected);
        stored.EncryptedAccessToken.Should().BeNull();
        stored.EncryptedRefreshToken.Should().BeNull();
        stored.AccessTokenExpiresAt.Should().BeNull();
        stored.RefreshTokenExpiresAt.Should().BeNull();
        stored.RealmId.Should().Be("9130000000000001");

        await using var check = h.OpenAs(OrgA);
        (await check.EntityMaps.SingleAsync()).RemoteId.Should().Be("58", "reconnecting the same company re-links instead of re-creating");
        (await check.SettingsAudit.SingleAsync()).Action.Should().Be("Disconnected");
        h.Outbox.Suspended.Should().ContainSingle().Which.ConnectionId.Should().Be(c.Id);
    }

    [Fact]
    public async Task Disconnect_goes_ahead_when_Intuit_refuses_the_revocation()
    {
        await using var h = ConnectionsHarness.Create();
        var c = await h.SeedConnectionAsync(OrgA);
        h.Auth.Revoke = (_, _) => throw new AccountingAuthException("Intuit did not confirm the revocation.");

        await WithService(h, OrgA, s => s.DisconnectAsync(7));

        (await h.ReloadConnectionAsync(c.Id)).Status.Should().Be(ConnectionStatus.NotConnected);
    }

    [Fact]
    public async Task Disconnect_goes_ahead_when_the_stored_token_cannot_be_decrypted()
    {
        await using var h = ConnectionsHarness.Create();
        var c = await h.SeedConnectionAsync(OrgA);
        await using (var db = h.OpenAs(OrgA))
        {
            (await db.Connections.SingleAsync()).EncryptedRefreshToken = "v2:garbage";
            await db.SaveChangesAsync();
        }

        await WithService(h, OrgA, s => s.DisconnectAsync(7));

        h.Auth.RevokedTokens.Should().BeEmpty();
        (await h.ReloadConnectionAsync(c.Id)).Status.Should().Be(ConnectionStatus.NotConnected);
    }

    [Fact]
    public async Task Disconnect_of_nothing_is_a_no_op()
    {
        await using var h = ConnectionsHarness.Create();

        (await WithService(h, OrgA, s => s.DisconnectAsync(7))).Status.Should().Be("NotConnected");
        h.Auth.RevokedTokens.Should().BeEmpty();
        h.Outbox.Suspended.Should().BeEmpty();
    }

    [Fact]
    public async Task Disconnecting_one_organization_leaves_another_connected()
    {
        await using var h = ConnectionsHarness.Create();
        var a = await h.SeedConnectionAsync(OrgA);
        var b = await h.SeedConnectionAsync(OrgB, realmId: "9130000000000002");

        await WithService(h, OrgB, s => s.DisconnectAsync(7));

        (await h.ReloadConnectionAsync(a.Id)).Status.Should().Be(ConnectionStatus.Live);
        (await h.ReloadConnectionAsync(b.Id)).Status.Should().Be(ConnectionStatus.NotConnected);
    }

    [Fact]
    public async Task Nothing_in_the_status_model_carries_a_token()
    {
        await using var h = ConnectionsHarness.Create();
        await h.SeedConnectionAsync(OrgA, access: "AT-SECRET-TOKEN", refresh: "RT-SECRET-TOKEN");

        var status = await WithService(h, OrgA, s => s.GetStatusAsync());

        System.Text.Json.JsonSerializer.Serialize(status).Should().NotContain("SECRET").And.NotContain("v2:");
    }
}

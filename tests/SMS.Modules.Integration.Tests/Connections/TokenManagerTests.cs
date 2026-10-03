using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Domain;

namespace SMS.Modules.Integration.Tests.Connections;

public class TokenManagerTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private static async Task<string> GetTokenAsync(ConnectionsHarness h, Guid org, int id)
    {
        await using var scope = h.Scope(org);
        return await scope.ServiceProvider.GetRequiredService<ITokenManager>().GetValidAccessTokenAsync(id);
    }

    [Fact]
    public async Task A_fresh_token_is_returned_without_calling_Intuit()
    {
        await using var h = ConnectionsHarness.Create();
        var c = await h.SeedConnectionAsync(OrgA, access: "AT-1", accessExpiresAt: DateTime.UtcNow.AddMinutes(30));

        (await GetTokenAsync(h, OrgA, c.Id)).Should().Be("AT-1");
        h.Auth.RefreshedWith.Should().BeEmpty();
    }

    [Fact]
    public async Task A_token_inside_the_skew_window_is_refreshed_and_the_rotation_persisted()
    {
        await using var h = ConnectionsHarness.Create(jobs: j => j.AccessTokenRefreshSkewMinutes = 5);
        var c = await h.SeedConnectionAsync(OrgA, access: "AT-1", refresh: "RT-1", accessExpiresAt: DateTime.UtcNow.AddMinutes(4));
        h.Auth.Refresh = (_, _) => Task.FromResult(FakeAuthProvider.Grant("AT-2", "RT-2", TimeSpan.FromHours(1), TimeSpan.FromDays(101)));

        var token = await GetTokenAsync(h, OrgA, c.Id);

        token.Should().Be("AT-2");
        h.Auth.RefreshedWith.Should().Equal("RT-1");

        var stored = await h.ReloadConnectionAsync(c.Id);
        var vault  = new CredentialVault(TestEncryption.Instance);
        vault.ReadAccessToken(stored).Should().Be("AT-2");
        vault.ReadRefreshToken(stored).Should().Be("RT-2", "the rotated refresh token must be kept, or the grant is lost");
        stored.AccessTokenExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddHours(1), TimeSpan.FromMinutes(1));
        stored.RefreshTokenExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddDays(101), TimeSpan.FromMinutes(1));
        stored.LastRefreshAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        stored.LastError.Should().BeNull();
    }

    [Fact]
    public async Task ForceRefresh_refreshes_even_a_fresh_token()
    {
        await using var h = ConnectionsHarness.Create();
        var c = await h.SeedConnectionAsync(OrgA, refresh: "RT-1", accessExpiresAt: DateTime.UtcNow.AddMinutes(55));

        await using var scope = h.Scope(OrgA);
        var token = await scope.ServiceProvider.GetRequiredService<TokenManager>().ForceRefreshAsync(c.Id);

        token.Should().Be("AT-refreshed");
        h.Auth.RefreshedWith.Should().Equal("RT-1");
    }

    [Theory]
    [InlineData("NotConnected")]
    [InlineData("Connecting")]
    [InlineData("Revoked")]
    [InlineData("Expired")]
    public async Task An_unusable_connection_is_refused_with_its_status(string statusName)
    {
        var status = Enum.Parse<ConnectionStatus>(statusName);
        await using var h = ConnectionsHarness.Create();
        var c = await h.SeedConnectionAsync(OrgA, status);

        var act = () => GetTokenAsync(h, OrgA, c.Id);

        (await act.Should().ThrowAsync<ConnectionUnavailableException>()).Which.Status.Should().Be(status);
        h.Auth.RefreshedWith.Should().BeEmpty();
    }

    [Fact]
    public async Task A_usable_status_without_tokens_is_refused()
    {
        await using var h = ConnectionsHarness.Create();
        var c = await h.SeedConnectionAsync(OrgA, withTokens: false);

        var act = () => GetTokenAsync(h, OrgA, c.Id);

        (await act.Should().ThrowAsync<ConnectionUnavailableException>()).Which.Status.Should().Be(ConnectionStatus.NotConnected);
    }

    [Fact]
    public async Task Another_organizations_connection_is_not_found()
    {
        await using var h = ConnectionsHarness.Create();
        var c = await h.SeedConnectionAsync(OrgA, access: "AT-A");

        var act = () => GetTokenAsync(h, OrgB, c.Id);

        await act.Should().ThrowAsync<ConnectionUnavailableException>().WithMessage("No QuickBooks connection*");
    }

    [Fact]
    public async Task A_revoked_grant_marks_the_connection_Revoked_suspends_the_outbox_and_forgets_the_tokens()
    {
        await using var h = ConnectionsHarness.Create();
        var c = await h.SeedConnectionAsync(OrgA, accessExpiresAt: DateTime.UtcNow.AddMinutes(-1), refreshExpiresAt: DateTime.UtcNow.AddDays(40));
        h.Auth.Refresh = (_, _) => throw new AuthorizationRevokedException("Intuit refused the refresh token: invalid_grant.");

        var act = () => GetTokenAsync(h, OrgA, c.Id);

        var ex = (await act.Should().ThrowAsync<ConnectionUnavailableException>()).Which;
        ex.Status.Should().Be(ConnectionStatus.Revoked);

        var stored = await h.ReloadConnectionAsync(c.Id);
        stored.Status.Should().Be(ConnectionStatus.Revoked);
        stored.LastError.Should().Contain("Reconnect");
        stored.EncryptedAccessToken.Should().BeNull();
        stored.EncryptedRefreshToken.Should().BeNull();
        stored.RefreshTokenExpiresAt.Should().NotBeNull("the screen still says when the grant would have run out");
        stored.RealmId.Should().Be("9130000000000001");
        h.Outbox.Suspended.Should().ContainSingle().Which.ConnectionId.Should().Be(c.Id);
    }

    [Fact]
    public async Task A_refused_grant_past_its_lifetime_is_Expired()
    {
        await using var h = ConnectionsHarness.Create();
        var c = await h.SeedConnectionAsync(OrgA, accessExpiresAt: DateTime.UtcNow.AddMinutes(-1), refreshExpiresAt: DateTime.UtcNow.AddDays(-1));
        h.Auth.Refresh = (_, _) => throw new AuthorizationRevokedException("invalid_grant");

        var act = () => GetTokenAsync(h, OrgA, c.Id);

        (await act.Should().ThrowAsync<ConnectionUnavailableException>()).Which.Status.Should().Be(ConnectionStatus.Expired);
        (await h.ReloadConnectionAsync(c.Id)).Status.Should().Be(ConnectionStatus.Expired);
        h.Outbox.Suspended.Should().ContainSingle();
    }

    [Fact]
    public async Task Revocation_works_without_a_sync_engine_registered()
    {
        await using var h = ConnectionsHarness.Create(registerOutbox: false);
        var c = await h.SeedConnectionAsync(OrgA, accessExpiresAt: DateTime.UtcNow.AddMinutes(-1));
        h.Auth.Refresh = (_, _) => throw new AuthorizationRevokedException("invalid_grant");

        var act = () => GetTokenAsync(h, OrgA, c.Id);

        await act.Should().ThrowAsync<ConnectionUnavailableException>();
        (await h.ReloadConnectionAsync(c.Id)).Status.Should().Be(ConnectionStatus.Revoked);
    }

    [Fact]
    public async Task A_transient_failure_inside_the_window_keeps_using_the_current_token()
    {
        await using var h = ConnectionsHarness.Create();
        var c = await h.SeedConnectionAsync(OrgA, access: "AT-still-good", accessExpiresAt: DateTime.UtcNow.AddMinutes(3));
        h.Auth.Refresh = (_, _) => throw new HttpRequestException("No such host is known.");

        var token = await GetTokenAsync(h, OrgA, c.Id);

        token.Should().Be("AT-still-good");
        var stored = await h.ReloadConnectionAsync(c.Id);
        stored.Status.Should().Be(ConnectionStatus.Live, "a network failure says nothing about the grant");
        stored.LastError.Should().Contain("HttpRequestException");
        h.Outbox.Suspended.Should().BeEmpty();
    }

    [Fact]
    public async Task A_transient_failure_after_expiry_is_retryable_not_a_revocation()
    {
        await using var h = ConnectionsHarness.Create();
        var c = await h.SeedConnectionAsync(OrgA, accessExpiresAt: DateTime.UtcNow.AddMinutes(-2));
        h.Auth.Refresh = (_, _) => throw new AccountingAuthException("Intuit did not refresh the connection: HTTP 503.");

        var act = () => GetTokenAsync(h, OrgA, c.Id);

        (await act.Should().ThrowAsync<TokenRefreshFailedException>()).Which.Message.Should().Contain("HTTP 503");
        (await h.ReloadConnectionAsync(c.Id)).Status.Should().Be(ConnectionStatus.Live);
        h.Outbox.Suspended.Should().BeEmpty();
    }

    [Fact]
    public async Task No_token_value_appears_in_errors_or_the_stored_LastError()
    {
        await using var h = ConnectionsHarness.Create();
        var c = await h.SeedConnectionAsync(OrgA, access: "AT-SECRET-123", refresh: "RT-SECRET-456", accessExpiresAt: DateTime.UtcNow.AddMinutes(-1));
        h.Auth.Refresh = (_, _) => throw new AuthorizationRevokedException("invalid_grant");

        var act = () => GetTokenAsync(h, OrgA, c.Id);
        var ex  = (await act.Should().ThrowAsync<ConnectionUnavailableException>()).Which;

        ex.ToString().Should().NotContain("SECRET");
        (await h.ReloadConnectionAsync(c.Id)).LastError.Should().NotContain("SECRET");
    }

    [Fact]
    public async Task One_organizations_refresh_leaves_another_untouched()
    {
        await using var h = ConnectionsHarness.Create();
        var a = await h.SeedConnectionAsync(OrgA, refresh: "RT-A", accessExpiresAt: DateTime.UtcNow.AddMinutes(-1));
        var b = await h.SeedConnectionAsync(OrgB, access: "AT-B", refresh: "RT-B", accessExpiresAt: DateTime.UtcNow.AddMinutes(-1));

        await GetTokenAsync(h, OrgA, a.Id);

        var storedB = await h.ReloadConnectionAsync(b.Id);
        new CredentialVault(TestEncryption.Instance).ReadRefreshToken(storedB).Should().Be("RT-B");
        h.Auth.RefreshedWith.Should().Equal("RT-A");
    }
}

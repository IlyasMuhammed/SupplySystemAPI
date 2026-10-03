using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using SMS.Modules.Integration.Configuration;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Providers.QuickBooks;

namespace SMS.Modules.Integration.Tests.Connections;

public class QuickBooksAuthProviderTests
{
    private readonly Mock<IIntuitOAuthClient> _client = new();

    private QuickBooksAuthProvider Provider(bool configured = true) => new(_client.Object, Options.Create(new QuickBooksOptions
    {
        ClientId     = configured ? "id" : "",
        ClientSecret = "secret",
        RedirectUri  = "https://localhost/callback"
    }));

    private static IntuitTokenResponse Ok(string access = "AT", string refresh = "RT", long expiresIn = 3600, long refreshIn = 8_726_400, long hard = 0) =>
        new(false, null, null, 200, access, refresh, expiresIn, refreshIn, hard);

    private static IntuitTokenResponse Error(string? error, string? description = null, int? status = 400, string? exceptionType = null) =>
        new(true, error, description, status, null, null, 0, 0, 0, exceptionType);

    [Fact]
    public void Consent_url_carries_the_state_verbatim()
    {
        _client.Setup(c => c.GetAuthorizationUrl("state-123")).Returns("https://appcenter.intuit.com/connect/oauth2?state=state-123");

        Provider().BuildConsentUrl("state-123").Should().EndWith("state=state-123");
    }

    [Fact]
    public void A_discovery_failure_building_the_consent_url_is_an_auth_error()
    {
        _client.Setup(c => c.GetAuthorizationUrl(It.IsAny<string>())).Throws(new Exception("Discovery Call failed. Authorize Endpoint is empty."));

        var act = () => Provider().BuildConsentUrl("s");

        act.Should().Throw<AccountingAuthException>().WithMessage("Could not reach Intuit*");
    }

    [Fact]
    public async Task Nothing_is_attempted_without_app_keys()
    {
        var provider = Provider(configured: false);

        provider.Invoking(p => p.BuildConsentUrl("s")).Should().Throw<AccountingAuthException>().WithMessage("*not configured*");
        await provider.Awaiting(p => p.ExchangeCodeAsync("c")).Should().ThrowAsync<AccountingAuthException>();
        await provider.Awaiting(p => p.RefreshAsync("r")).Should().ThrowAsync<AccountingAuthException>();
        _client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Exchange_turns_Intuit_lifetimes_into_absolute_expiries()
    {
        _client.Setup(c => c.ExchangeCodeAsync("code", It.IsAny<CancellationToken>())).ReturnsAsync(Ok(refreshIn: 8_726_400));

        var grant = await Provider().ExchangeCodeAsync("code");

        grant.AccessToken.Should().Be("AT");
        grant.RefreshToken.Should().Be("RT");
        grant.AccessTokenExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddHours(1), TimeSpan.FromSeconds(5));
        grant.RefreshTokenExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddSeconds(8_726_400), TimeSpan.FromSeconds(5),
            "x_refresh_token_expires_in, never a hard-coded lifetime");
    }

    [Fact]
    public void A_hard_maximum_lifetime_wins_when_it_is_sooner()
    {
        var now   = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc);
        var grant = QuickBooksAuthProvider.ToGrant(Ok(refreshIn: 8_726_400, hard: 86_400), now);

        grant.RefreshTokenExpiresAt.Should().Be(now.AddDays(1));
    }

    [Fact]
    public async Task A_failed_exchange_is_an_auth_error_with_the_oauth_code()
    {
        _client.Setup(c => c.ExchangeCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(Error("invalid_grant", "Authorization code is invalid"));

        var ex = (await Provider().Awaiting(p => p.ExchangeCodeAsync("bad")).Should().ThrowAsync<AccountingAuthException>()).Which;

        ex.ErrorCode.Should().Be("invalid_grant");
        ex.Message.Should().Contain("invalid_grant").And.Contain("Authorization code is invalid");
    }

    [Fact]
    public async Task A_success_without_tokens_is_still_a_failed_exchange()
    {
        _client.Setup(c => c.ExchangeCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok(access: ""));

        await Provider().Awaiting(p => p.ExchangeCodeAsync("c")).Should().ThrowAsync<AccountingAuthException>();
    }

    [Theory]
    [InlineData("invalid_grant", null)]
    [InlineData("INVALID_GRANT", null)]
    [InlineData("bad_request", "The token is invalid_grant for this client")]
    public async Task A_refused_refresh_token_means_the_grant_is_gone(string error, string? description)
    {
        _client.Setup(c => c.RefreshAsync("RT", It.IsAny<CancellationToken>())).ReturnsAsync(Error(error, description));

        await Provider().Awaiting(p => p.RefreshAsync("RT")).Should().ThrowAsync<AuthorizationRevokedException>();
    }

    [Theory]
    [InlineData("invalid_client", null, 401, null)]
    [InlineData("Unauthorized", null, 401, null)]
    [InlineData(null, null, null, "HttpRequestException")]
    [InlineData("server_error", null, 503, null)]
    public async Task Other_refresh_failures_leave_the_grant_alone(string? error, string? description, int? status, string? exceptionType)
    {
        _client.Setup(c => c.RefreshAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(Error(error, description, status, exceptionType));

        var ex = (await Provider().Awaiting(p => p.RefreshAsync("RT")).Should().ThrowAsync<AccountingAuthException>()).Which;
        ex.Should().NotBeOfType<AuthorizationRevokedException>();
    }

    [Fact]
    public async Task A_successful_refresh_returns_the_rotated_pair()
    {
        _client.Setup(c => c.RefreshAsync("RT-old", It.IsAny<CancellationToken>())).ReturnsAsync(Ok("AT-new", "RT-new"));

        var grant = await Provider().RefreshAsync("RT-old");

        grant.RefreshToken.Should().Be("RT-new");
        grant.AccessToken.Should().Be("AT-new");
    }

    [Fact]
    public async Task Error_text_is_redacted_before_it_can_reach_a_message()
    {
        _client.Setup(c => c.RefreshAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(Error("server_error", "echo: refresh_token=RT-SECRET-99 access_token=AT-SECRET-77"));

        var ex = (await Provider().Awaiting(p => p.RefreshAsync("RT-SECRET-99")).Should().ThrowAsync<AccountingAuthException>()).Which;

        ex.Message.Should().NotContain("RT-SECRET-99").And.NotContain("AT-SECRET-77");
    }

    [Fact]
    public async Task A_refused_revocation_is_reported()
    {
        _client.Setup(c => c.RevokeAsync("RT", It.IsAny<CancellationToken>())).ReturnsAsync(new IntuitRevokeResponse(true, "invalid_token", 400));

        await Provider().Awaiting(p => p.RevokeAsync("RT")).Should().ThrowAsync<AccountingAuthException>().WithMessage("*HTTP 400*");
    }

    [Fact]
    public async Task A_confirmed_revocation_is_quiet()
    {
        _client.Setup(c => c.RevokeAsync("RT", It.IsAny<CancellationToken>())).ReturnsAsync(new IntuitRevokeResponse(false, null, 200));

        await Provider().Awaiting(p => p.RevokeAsync("RT")).Should().NotThrowAsync();
    }

    [Fact]
    public void It_is_the_QuickBooks_provider() => Provider().ProviderKey.Should().Be("QBO");
}

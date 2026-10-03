using System.Net;
using System.Threading.RateLimiting;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using SMS.Modules.Auth.Infrastructure;
using Xunit;

namespace SMS.Modules.Auth.Tests.Security;

/// <summary>
/// The per-IP limits on the anonymous sign-in and account-recovery endpoints. These sit in front of the
/// per-account defences (login lockout, the reset code's attempt limit) and bound how fast one client can
/// spray many accounts or addresses.
/// </summary>
public class AuthRateLimitsTests
{
    private static RateLimiter LimiterFor(string? ip, int permitsPerMinute)
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = ip is null ? null : IPAddress.Parse(ip);
        var partition = AuthRateLimits.PerIp(http, permitsPerMinute);
        return partition.Factory(partition.PartitionKey);
    }

    private static int Granted(RateLimiter limiter, int requests) =>
        Enumerable.Range(0, requests).Count(_ => limiter.AttemptAcquire().IsAcquired);

    [Theory]
    [InlineData(AuthRateLimits.AccountRecoveryPermitsPerMinute)]
    [InlineData(AuthRateLimits.SignInPermitsPerMinute)]
    public void A_client_gets_its_quota_a_minute_and_no_more(int permits)
    {
        var limiter = LimiterFor("203.0.113.7", permits);

        Granted(limiter, permits + 5).Should().Be(permits);
    }

    [Fact]
    public void Each_client_address_has_its_own_quota()
    {
        var http1 = new DefaultHttpContext { Connection = { RemoteIpAddress = IPAddress.Parse("203.0.113.7") } };
        var http2 = new DefaultHttpContext { Connection = { RemoteIpAddress = IPAddress.Parse("198.51.100.9") } };

        AuthRateLimits.PerIp(http1, 10).PartitionKey.Should().NotBe(AuthRateLimits.PerIp(http2, 10).PartitionKey);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData(null)]
    public void An_in_process_or_local_caller_is_not_limited(string? ip)
    {
        // TestServer hosts have no remote address, and the integration suites sign in hundreds of times;
        // a request over a real connection always has one.
        var limiter = LimiterFor(ip, AuthRateLimits.AccountRecoveryPermitsPerMinute);

        Granted(limiter, 1000).Should().Be(1000);
    }

    // That AddAuthModule registers both policies is proven in the real host:
    // SMS.Integration.Tests/Security/AuthHardeningEndpointTests (an unregistered policy is a 500 on first use).
}

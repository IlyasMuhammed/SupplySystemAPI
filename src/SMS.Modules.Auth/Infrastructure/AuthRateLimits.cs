using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace SMS.Modules.Auth.Infrastructure;

/// <summary>
/// Per-client-IP limits on the anonymous <c>api/auth</c> endpoints (see the [EnableRateLimiting] on each
/// AuthController action).
/// <para>
/// <b>Why.</b> They are the only endpoints anybody on the internet can call, and each takes something guessable
/// or sprayable — a password, an e-mail address, a six-digit reset code. The per-account defences (the login
/// lockout, <see cref="Services.PasswordResetThrottle"/>) stop one account being ground through; these stop one
/// client spraying many accounts, enumerating addresses or flooding mailboxes with codes.
/// </para>
/// <para>
/// <b>Sign-in is the generous one</b>, because an office behind a single NAT address signs in together at nine
/// o'clock. <b>Refresh and logout carry no limit</b>: they take a 128-bit refresh token (nothing to guess) and
/// every open tab refreshes.
/// </para>
/// <para>
/// <b>Loopback and address-less requests are not limited.</b> A request over a real connection always has a
/// remote address; only in-process callers (TestServer hosts, whose suites sign in hundreds of times) and the
/// machine itself have none or loopback. The other per-IP policies in Program.cs put those into one shared
/// "anonymous" bucket instead, which would make every integration suite race for ten permits.
/// </para>
/// </summary>
internal static class AuthRateLimits
{
    public const string SignIn = "auth-sign-in-per-ip";
    public const string AccountRecovery = "auth-account-recovery-per-ip";

    public const int SignInPermitsPerMinute = 30;
    public const int AccountRecoveryPermitsPerMinute = 10;

    /// <summary>Registers both policies. Program.cs's AddRateLimiter/UseRateLimiter does the rest, as for the Integration module's.</summary>
    public static IServiceCollection AddAuthRateLimits(this IServiceCollection services) =>
        services.Configure<RateLimiterOptions>(options =>
        {
            options.AddPolicy(SignIn, httpContext => PerIp(httpContext, SignInPermitsPerMinute));
            options.AddPolicy(AccountRecovery, httpContext => PerIp(httpContext, AccountRecoveryPermitsPerMinute));
        });

    public static RateLimitPartition<string> PerIp(HttpContext httpContext, int permitsPerMinute)
    {
        var address = httpContext.Connection.RemoteIpAddress;
        if (address is null || IPAddress.IsLoopback(address))
            return RateLimitPartition.GetNoLimiter("local");

        return RateLimitPartition.GetFixedWindowLimiter(address.ToString(), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit          = permitsPerMinute,
            Window               = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit           = 0
        });
    }
}

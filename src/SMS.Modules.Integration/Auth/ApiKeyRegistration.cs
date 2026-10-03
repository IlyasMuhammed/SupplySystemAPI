using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SMS.Modules.Integration.Auth;

// OWNER: connection & auth work package (QBI-06). Registers the API-key authentication scheme,
// ApiKeyService and the per-client rate limit.
internal static class ApiKeyRegistration
{
    /// <summary>Requests per minute per API key unless <c>Integration:ApiKeys:RequestsPerMinute</c> says otherwise.</summary>
    public const int DefaultRequestsPerMinute = 300;

    public static IServiceCollection AddIntegrationApiKeyAuth(this IServiceCollection services, IConfiguration configuration)
    {
        // A named scheme alongside JWT, never the default: the admin screens stay on JWT, and only the
        // data controllers opt in with [Authorize(AuthenticationSchemes = ApiKeyDefaults.Scheme)].
        // AddAuthentication() with no arguments leaves SMS.API's default-scheme choice alone.
        services.AddAuthentication()
            .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(ApiKeyDefaults.Scheme, _ => { });

        var perMinute = Math.Max(1, configuration.GetValue("Integration:ApiKeys:RequestsPerMinute", DefaultRequestsPerMinute));

        // Partitioned by the key's public prefix, not by IP: one caller's burst stays its own problem,
        // and many callers behind one gateway address do not throttle each other. A request with no
        // (or a malformed) key shares one small bucket — it is going to be refused anyway.
        services.Configure<RateLimiterOptions>(options =>
            options.AddPolicy(ApiKeyDefaults.RateLimitPolicy, httpContext =>
            {
                var key = httpContext.Request.Headers[ApiKeyDefaults.KeyHeader].ToString().Trim();
                var partition = ApiKeyGenerator.IsWellFormed(key) ? ApiKeyGenerator.PrefixOf(key) : "no-key";

                return RateLimitPartition.GetFixedWindowLimiter(partition, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit          = perMinute,
                    Window               = TimeSpan.FromMinutes(1),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit           = 0
                });
            }));

        return services;
    }
}

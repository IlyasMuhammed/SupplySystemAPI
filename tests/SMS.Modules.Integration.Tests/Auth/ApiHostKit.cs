using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Middleware;

namespace SMS.Modules.Integration.Tests.Auth;

/// <summary>
/// A minimal host that mirrors SMS.API's Program.cs where it matters for authentication: the global
/// <c>AuthorizeFilter(RequireAuthenticatedUser)</c>, a bearer default scheme standing in for JWT, the
/// feature filter, the real <c>TenantContext</c>, and the middleware order
/// (routing → rate limiter → authentication → authorization → TenantMiddleware → controllers).
/// Only the named controllers are mapped.
/// </summary>
internal static class ApiHostKit
{
    public static async Task<IHost> StartAsync(
        Type[] controllers, Action<IServiceCollection> configureServices, ILoggerProvider? logs = null)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging(b =>
                    {
                        b.SetMinimumLevel(logs is null ? LogLevel.Warning : LogLevel.Trace);
                        if (logs is not null) b.AddProvider(logs);
                    });
                    services.AddTenantContext();

                    services.AddControllers(options =>
                        {
                            options.Filters.Add(new AuthorizeFilter(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build()));
                            options.Filters.Add<FeatureAuthorizationFilter>();
                        })
                        .ConfigureApplicationPartManager(apm =>
                        {
                            foreach (var provider in apm.FeatureProviders.OfType<ControllerFeatureProvider>().ToList())
                                apm.FeatureProviders.Remove(provider);
                            apm.FeatureProviders.Add(new SelectedControllers(controllers));
                        });

                    // Program.cs: JWT is the default scheme for authenticate and challenge.
                    services.AddAuthentication(o =>
                        {
                            o.DefaultAuthenticateScheme = FakeJwtHandler.SchemeName;
                            o.DefaultChallengeScheme    = FakeJwtHandler.SchemeName;
                        })
                        .AddScheme<AuthenticationSchemeOptions, FakeJwtHandler>(FakeJwtHandler.SchemeName, _ => { });

                    services.AddRateLimiter(o => o.RejectionStatusCode = 429);

                    configureServices(services);
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseRateLimiter();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseMiddleware<TenantMiddleware>();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    public static IConfiguration Configuration(IDictionary<string, string?>? values = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(values ?? new Dictionary<string, string?>()).Build();

    private sealed class SelectedControllers : IApplicationFeatureProvider<ControllerFeature>
    {
        private readonly Type[] _types;
        public SelectedControllers(Type[] types) => _types = types;

        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            foreach (var type in _types) feature.Controllers.Add(type.GetTypeInfo());
        }
    }
}

/// <summary>
/// Stands in for SMS.API's JWT bearer scheme: <c>Authorization: Bearer user:{orgId}</c> signs in an
/// ordinary user of that organization, <c>Bearer super:{orgId}</c> a super admin. Anything else is no result.
/// </summary>
internal sealed class FakeJwtHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Bearer";

    public FakeJwtHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal)) return Task.FromResult(AuthenticateResult.NoResult());

        var parts = header["Bearer ".Length..].Split(':');
        if (parts.Length != 2 || !Guid.TryParse(parts[1], out var org)) return Task.FromResult(AuthenticateResult.Fail("bad token"));

        var claims = new List<Claim>
        {
            new("sub", "7"),
            new("organizationId", org.ToString()),
            new("permission", "INTEGRATION_VIEW"),
            new("permission", "INTEGRATION_MANAGE")
        };
        if (parts[0] == "super") claims.Add(new Claim("is_super_admin", "true"));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}

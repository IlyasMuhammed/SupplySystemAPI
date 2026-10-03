using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SMS.Modules.Integration.Auth;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Tests.Connections;
using SMS.Shared.Common;

namespace SMS.Modules.Integration.Tests.Auth;

/// <summary>
/// Decorated exactly like a data endpoint: API-key scheme only, a scope per action. Returns what the
/// tenant machinery resolved for the request, and how many connections the tenant filter lets it see.
/// </summary>
[ApiController]
[Route("probe")]
[Authorize(AuthenticationSchemes = ApiKeyDefaults.Scheme)]
[EnableRateLimiting(ApiKeyDefaults.RateLimitPolicy)]
public class ApiKeyProbeController : ControllerBase
{
    [HttpGet("status")]
    [RequireApiScope(ApiScopes.StatusRead)]
    public IActionResult Status() => Ok(Describe());

    [HttpPut("customers")]
    [RequireApiScope(ApiScopes.CustomersWrite)]
    public IActionResult Customers() => Ok(Describe());

    private ProbeResult Describe()
    {
        var tenant = HttpContext.RequestServices.GetRequiredService<ITenantContext>();
        var db     = HttpContext.RequestServices.GetRequiredService<IntegrationDbContext>();
        return new ProbeResult(
            tenant.OrganizationId,
            tenant.IsSuperAdmin,
            db.Connections.Count(),
            User.FindFirst(IntegrationClaims.ApiClientName)?.Value,
            User.HasClaim(c => c.Type == "is_super_admin"));
    }
}

public sealed record ProbeResult(Guid OrganizationId, bool IsSuperAdmin, int VisibleConnections, string? ClientName, bool HasSuperAdminClaim);

/// <summary>
/// The plan's mandatory security test (QBI-06): an API-key endpoint is never reachable without a valid
/// key for the named tenant, and a request that gets through is scoped to the key's organization —
/// never the tenant-filter bypass an unauthenticated principal would get.
/// </summary>
public class ApiKeyAuthenticationTests : IAsyncLifetime
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private readonly string        _dbName    = Guid.NewGuid().ToString();
    private readonly FakeSnapshots _snapshots = new();
    private IHost       _host   = null!;
    private HttpClient  _client = null!;

    private string _keyA        = null!;   // org A, status:read + customers:write
    private string _keyOnlyCust = null!;   // org A, customers:write only
    private string _keyB        = null!;   // org B, status:read
    private string _keyRevoked  = null!;
    private string _keyExpired  = null!;
    private string _keyInactive = null!;

    public async Task InitializeAsync()
    {
        await using (var db = TestDb.OpenAs(_dbName, Guid.Empty, superAdmin: true))
        {
            db.Connections.Add(new IntegrationConnection { OrganizationId = OrgA, RealmId = "A", Status = ConnectionStatus.Live });
            db.Connections.Add(new IntegrationConnection { OrganizationId = OrgB, RealmId = "B", Status = ConnectionStatus.Live });

            _keyA        = AddKey(db, OrgA, "Point of sale", "status:read,customers:write");
            _keyOnlyCust = AddKey(db, OrgA, "Web shop",      "customers:write");
            _keyB        = AddKey(db, OrgB, "B system",      "status:read");
            _keyRevoked  = AddKey(db, OrgA, "Old system",    "status:read", revoked: true);
            _keyExpired  = AddKey(db, OrgA, "Temp system",   "status:read", expiresAt: DateTime.UtcNow.AddMinutes(-1));
            _keyInactive = AddKey(db, OrgA, "Retired",       "status:read", clientActive: false);
            await db.SaveChangesAsync();
        }

        _host   = await StartHostAsync(requestsPerMinute: null);
        _client = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    private Task<IHost> StartHostAsync(int? requestsPerMinute) =>
        ApiHostKit.StartAsync([typeof(ApiKeyProbeController)], services =>
        {
            services.AddDbContext<IntegrationDbContext>(o => o.UseInMemoryDatabase(_dbName, TestDb.Root));
            services.AddSingleton<ITenantSnapshotProvider>(_snapshots);
            services.AddIntegrationApiKeyAuth(ApiHostKit.Configuration(requestsPerMinute is null
                ? null
                : new Dictionary<string, string?> { ["Integration:ApiKeys:RequestsPerMinute"] = requestsPerMinute.ToString() }));
        });

    private static string AddKey(
        IntegrationDbContext db, Guid org, string clientName, string scopes,
        bool revoked = false, DateTime? expiresAt = null, bool clientActive = true)
    {
        var key    = ApiKeyGenerator.NewKey();
        var client = new ApiClient { OrganizationId = org, Name = clientName, Scopes = scopes, IsActive = clientActive };
        client.Keys.Add(new ApiClientKey
        {
            OrganizationId = org,
            KeyPrefix      = ApiKeyGenerator.PrefixOf(key),
            KeyHash        = ApiKeyGenerator.Hash(key),
            RevokedAt      = revoked ? DateTime.UtcNow.AddDays(-1) : null,
            ExpiresAt      = expiresAt
        });
        db.ApiClients.Add(client);
        return key;
    }

    private static HttpRequestMessage Get(string path, string? key = null, Guid? tenant = null, string? tenantRaw = null, string? bearer = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (key is not null)       request.Headers.Add(ApiKeyDefaults.KeyHeader, key);
        if (tenant is not null)    request.Headers.Add(ApiKeyDefaults.TenantHeader, tenant.Value.ToString());
        if (tenantRaw is not null) request.Headers.Add(ApiKeyDefaults.TenantHeader, tenantRaw);
        if (bearer is not null)    request.Headers.Add("Authorization", "Bearer " + bearer);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) => await _client.SendAsync(request);

    // ── 401s ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task No_headers_is_401_and_no_data()
    {
        var response = await SendAsync(Get("/probe/status"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().NotContain(OrgA.ToString()).And.Contain("X-Api-Key");
    }

    [Fact]
    public async Task A_tenant_header_without_a_key_is_401()
    {
        (await SendAsync(Get("/probe/status", tenant: OrgA))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("not-a-key")]
    [InlineData("sqb_short")]
    [InlineData("xyz_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("sqb_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA!")]
    public async Task A_malformed_key_is_401(string key)
    {
        (await SendAsync(Get("/probe/status", key, OrgA))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_well_formed_unknown_key_is_401()
    {
        (await SendAsync(Get("/probe/status", ApiKeyGenerator.NewKey(), OrgA))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_known_prefix_with_the_wrong_secret_is_401()
    {
        var forged = _keyA[..^1] + (_keyA[^1] == 'A' ? 'B' : 'A');

        (await SendAsync(Get("/probe/status", forged, OrgA))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_revoked_key_is_401()
    {
        (await SendAsync(Get("/probe/status", _keyRevoked, OrgA))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_expired_key_is_401()
    {
        (await SendAsync(Get("/probe/status", _keyExpired, OrgA))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_deactivated_clients_key_is_401()
    {
        (await SendAsync(Get("/probe/status", _keyInactive, OrgA))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_valid_key_without_a_tenant_header_is_401()
    {
        (await SendAsync(Get("/probe/status", _keyA))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_malformed_tenant_header_is_401()
    {
        (await SendAsync(Get("/probe/status", _keyA, tenantRaw: "org-a"))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_valid_key_naming_another_tenant_is_401()
    {
        // Org A's key presented for org B: a key never speaks for another organization.
        (await SendAsync(Get("/probe/status", _keyA, OrgB))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_deactivated_organization_is_401()
    {
        _snapshots.Set(OrgA, new TenantSnapshot(false, new HashSet<string> { "MODULE_INTEGRATION" }));

        (await SendAsync(Get("/probe/status", _keyA, OrgA))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_unknown_organization_is_401()
    {
        _snapshots.Set(OrgA, null);

        (await SendAsync(Get("/probe/status", _keyA, OrgA))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_integration_feature_switched_off_is_401()
    {
        _snapshots.Set(OrgA, FakeSnapshots.Active("MODULE_LOGISTICS"));

        (await SendAsync(Get("/probe/status", _keyA, OrgA))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Every_refusal_reads_the_same()
    {
        var bodies = new List<string>();
        foreach (var request in new[]
                 {
                     Get("/probe/status", _keyRevoked, OrgA), Get("/probe/status", _keyA, OrgB), Get("/probe/status", ApiKeyGenerator.NewKey(), OrgA)
                 })
            bodies.Add(await (await SendAsync(request)).Content.ReadAsStringAsync());

        bodies.Distinct().Should().ContainSingle("a caller must not learn which part of its credentials was wrong");
    }

    // ── JWT does not open an API-key endpoint ──────────────────────────────────────────────

    [Fact]
    public async Task A_valid_JWT_alone_is_401()
    {
        (await SendAsync(Get("/probe/status", bearer: $"user:{OrgA}"))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_super_admin_JWT_alone_is_401()
    {
        (await SendAsync(Get("/probe/status", bearer: $"super:{OrgA}"))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task With_a_JWT_and_a_valid_key_the_key_decides_the_tenant_and_there_is_no_bypass()
    {
        var response = await SendAsync(Get("/probe/status", _keyB, OrgB, bearer: $"super:{OrgA}"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var probe = await response.Content.ReadFromJsonAsync<ProbeResult>();
        probe!.OrganizationId.Should().Be(OrgB);
        probe.IsSuperAdmin.Should().BeFalse();
        probe.HasSuperAdminClaim.Should().BeFalse();
        probe.VisibleConnections.Should().Be(1);
    }

    // ── Scopes ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_key_without_the_scope_is_403()
    {
        var response = await SendAsync(Get("/probe/status", _keyOnlyCust, OrgA));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("insufficient_scope");
    }

    [Fact]
    public async Task A_key_with_the_scope_passes_that_action()
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/probe/customers");
        request.Headers.Add(ApiKeyDefaults.KeyHeader, _keyOnlyCust);
        request.Headers.Add(ApiKeyDefaults.TenantHeader, OrgA.ToString());

        (await SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Success ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_valid_key_is_200_scoped_to_its_organization_and_never_a_super_admin()
    {
        var response = await SendAsync(Get("/probe/status", _keyA, OrgA));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var probe = await response.Content.ReadFromJsonAsync<ProbeResult>();
        probe!.OrganizationId.Should().Be(OrgA);
        probe.IsSuperAdmin.Should().BeFalse();
        probe.HasSuperAdminClaim.Should().BeFalse();
        probe.VisibleConnections.Should().Be(1, "the tenant filter is on: org B's connection is invisible");
        probe.ClientName.Should().Be("Point of sale");
    }

    [Fact]
    public async Task Last_used_is_recorded_at_most_once_a_minute()
    {
        (await SendAsync(Get("/probe/status", _keyA, OrgA))).EnsureSuccessStatusCode();
        DateTime? first;
        await using (var db = TestDb.OpenAs(_dbName, OrgA))
            first = (await db.ApiClientKeys.SingleAsync(k => k.KeyPrefix == ApiKeyGenerator.PrefixOf(_keyA))).LastUsedAt;

        (await SendAsync(Get("/probe/status", _keyA, OrgA))).EnsureSuccessStatusCode();

        await using var check = TestDb.OpenAs(_dbName, OrgA);
        var second = (await check.ApiClientKeys.SingleAsync(k => k.KeyPrefix == ApiKeyGenerator.PrefixOf(_keyA))).LastUsedAt;
        first.Should().NotBeNull().And.BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        second.Should().Be(first);
    }

    // ── Logs ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_key_never_reaches_the_logs_accepted_or_refused()
    {
        var logs = new CapturingLoggerProvider();
        using var host = await ApiHostKit.StartAsync([typeof(ApiKeyProbeController)], services =>
        {
            services.AddDbContext<IntegrationDbContext>(o => o.UseInMemoryDatabase(_dbName, TestDb.Root));
            services.AddSingleton<ITenantSnapshotProvider>(_snapshots);
            services.AddIntegrationApiKeyAuth(ApiHostKit.Configuration());
        }, logs);
        using var client = host.GetTestClient();

        var forged = _keyA[..^1] + (_keyA[^1] == 'A' ? 'B' : 'A');
        (await client.SendAsync(Get("/probe/status", _keyA, OrgA))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(Get("/probe/status", _keyA, OrgB))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.SendAsync(Get("/probe/status", forged, OrgA))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.SendAsync(Get("/probe/status", _keyRevoked, OrgA))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        logs.All.Should().Contain("refused", "refusals are logged — by prefix");
        foreach (var key in new[] { _keyA, forged, _keyRevoked })
        {
            logs.All.Should().NotContain(key);
            logs.All.Should().NotContain(key[ApiKeyDefaults.PrefixLength..], "not even the secret part after the prefix");
        }

        await host.StopAsync();
    }

    // ── Rate limit ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Each_key_has_its_own_rate_limit()
    {
        using var host   = await StartHostAsync(requestsPerMinute: 2);
        using var client = host.GetTestClient();

        async Task<HttpStatusCode> Call(string key, Guid org) => (await client.SendAsync(Get("/probe/status", key, org))).StatusCode;

        (await Call(_keyA, OrgA)).Should().Be(HttpStatusCode.OK);
        (await Call(_keyA, OrgA)).Should().Be(HttpStatusCode.OK);
        (await Call(_keyA, OrgA)).Should().Be(HttpStatusCode.TooManyRequests);
        (await Call(_keyB, OrgB)).Should().Be(HttpStatusCode.OK, "another key is another partition");

        await host.StopAsync();
    }
}

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Integration.Tests;

// Covers the ticket's "Non-Super-Admin calling any /api/system/* endpoint returns 403" acceptance
// criterion. Permission claims are baked into the JWT at issuance (see TokenService), so the
// authorization handler never touches the database — no Testcontainers/seeding needed here,
// matching the plain-factory style already used by AuthIntegrationTests.cs.
public class SystemAdminAuthorizationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public SystemAdminAuthorizationTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private string BuildToken(params string[] permissions)
    {
        var secret = _factory.Services.GetRequiredService<IOptions<AppSettings>>().Value.Secret;
        var key = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new("sub", "999"),
            new("email", "nonadmin@test.com"),
            new("roleId", "1")
        };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));

        var token = new JwtSecurityToken(
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private HttpClient CreateClientWithPermissions(params string[] permissions)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", BuildToken(permissions));
        return client;
    }

    [Fact]
    public async Task GetOrganizations_WithoutSuperAdminPermission_Returns403()
    {
        var client = CreateClientWithPermissions(PermissionCodes.USER_MANAGE);

        var response = await client.GetAsync("/api/system/organizations");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetFeatures_WithoutSuperAdminPermission_Returns403()
    {
        var client = CreateClientWithPermissions(PermissionCodes.USER_MANAGE);

        var response = await client.GetAsync("/api/system/features");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetOrganizations_WithNoPermissionsAtAll_Returns403()
    {
        var client = CreateClientWithPermissions();

        var response = await client.GetAsync("/api/system/organizations");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetOrganizations_WithSuperAdminPermission_DoesNotReturn403()
    {
        var client = CreateClientWithPermissions(PermissionCodes.PLATFORM_SUPER_ADMIN);

        var response = await client.GetAsync("/api/system/organizations");

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }
}

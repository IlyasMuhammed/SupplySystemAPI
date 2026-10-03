using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SMS.Integration.Tests.SapAlignment;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Integration.Tests.Security;

/// <summary>
/// The endpoints that reach every organization — managing organizations and their features, and the WhatsApp
/// dispatch log, which has no organization column — answer only a platform super admin: a SuperAdminUsers
/// member, stamped into the <c>is_super_admin</c> claim. The PLATFORM_SUPER_ADMIN permission alone is not
/// enough: the global System Admin role carries it, and so can a custom role, so a user of any one
/// organization holding such a role would otherwise create, deactivate and re-plan every other organization.
/// </summary>
public sealed class PlatformEndpointsTests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;

    public PlatformEndpointsTests(SapWebApplicationFactory factory) => _f = factory;

    /// <summary>A token as TokenService issues it: the organization, the role's permission codes, and whether the user is in SuperAdminUsers.</summary>
    private HttpClient ClientFor(bool superAdmin, params string[] permissions)
    {
        var secret = _f.Services.GetRequiredService<IOptions<AppSettings>>().Value.Secret;
        var claims = new List<Claim>
        {
            new("sub", "424242"),
            new("email", "someone@org.example"),
            new("organizationId", _f.OrganizationId.ToString()),
            new("is_super_admin", superAdmin ? "true" : "false")
        };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));

        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.ASCII.GetBytes(secret)), SecurityAlgorithms.HmacSha256)));

        return _f.CreateBearerClient(token);
    }

    public static TheoryData<string> PlatformUrls => new()
    {
        "api/system/organizations",
        "api/system/features",
        "api/notifications/whatsapp-logs"
    };

    [Theory]
    [MemberData(nameof(PlatformUrls))]
    public async Task A_role_carrying_the_platform_code_is_not_enough(string url)
    {
        using var client = ClientFor(superAdmin: false, PermissionCodes.PLATFORM_SUPER_ADMIN, PermissionCodes.SYSTEM_CONFIGURE);

        var response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Creating_an_organization_is_refused_too()
    {
        using var client = ClientFor(superAdmin: false, PermissionCodes.PLATFORM_SUPER_ADMIN);

        var response = await client.PostAsync("api/system/organizations",
            new StringContent("{\"name\":\"Hostile Org\",\"code\":\"HOSTILE\"}", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("api/system/organizations")]
    [InlineData("api/system/features")]
    public async Task A_platform_super_admin_still_gets_in(string url)
    {
        using var client = ClientFor(superAdmin: true, PermissionCodes.PLATFORM_SUPER_ADMIN);

        var response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_super_admin_claim_without_the_code_is_not_enough_either()
    {
        using var client = ClientFor(superAdmin: true);

        (await client.GetAsync("api/system/organizations")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}

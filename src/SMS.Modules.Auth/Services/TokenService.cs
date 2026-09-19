using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SMS.Modules.Auth.Domain;
using SMS.Shared.Common;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace SMS.Modules.Auth.Services;

internal sealed class TokenService : ITokenService
{
    private const int AccessTokenMinutes = 60;

    /// <summary>
    /// How long an access token is good for, in seconds — the value <c>expires_in</c> must carry.
    /// <para>
    /// Exposed because the login and refresh responses each used to hard-code their own number,
    /// and they disagreed: login said 3600 while refresh said 900 for the very same 60-minute
    /// token. A client trusts <c>expires_in</c> to decide when to renew, so one of them was always
    /// lying. Derived from the lifetime here, the two cannot drift apart again.
    /// </para>
    /// </summary>
    internal const int AccessTokenSeconds = AccessTokenMinutes * 60;

    private readonly AppSettings _settings;

    public TokenService(IOptions<AppSettings> settings) => _settings = settings.Value;

    public string GenerateAccessToken(UserAccount user, string roleName, IList<string> allowedPermissions, bool isSuperAdmin)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.Secret));
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.UserID.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new("roleId", user.RoleID.ToString()),
            new("roleName", roleName),
            new("organizationId", user.OrganizationId.ToString()),
            // MT-007: SuperAdminUsers table membership (checked by the caller) is now the sole
            // authoritative source — no permission-based derivation anymore.
            new("is_super_admin", isSuperAdmin ? "true" : "false"),
        };
        claims.AddRange(allowedPermissions.Select(p => new Claim("permission", p)));

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddMinutes(AccessTokenMinutes),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        };

        var handler = new JwtSecurityTokenHandler();
        return handler.WriteToken(handler.CreateToken(descriptor));
    }

    public (string raw, string hash) GenerateRefreshToken()
    {
        var raw = Guid.NewGuid().ToString("N");
        return (raw, ComputeSha256(raw));
    }

    internal static string ComputeSha256(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

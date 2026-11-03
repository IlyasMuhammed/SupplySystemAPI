using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Hrm.Security;

/// <summary>
/// Who is calling (API-CONTRACT §1.1): the organization (always explicit in every query — a super admin bypasses the tenant
/// filter), the user and their permissions. §3.3.4 — a platform super admin never holds the codes that reveal personal money and
/// identity data, whatever the token says; exceptional access is the deferred support-access workflow.
/// </summary>
public sealed class HrmCaller
{
    private readonly HashSet<string> _permissions;

    public HrmCaller(Guid organizationId, int userId, IEnumerable<string> permissions, string? userName = null, bool isSuperAdmin = false)
    {
        OrganizationId = organizationId;
        UserId = userId;
        UserName = userName;
        IsSuperAdmin = isSuperAdmin;
        _permissions = new HashSet<string>(permissions, StringComparer.Ordinal);
        if (isSuperAdmin) _permissions.ExceptWith(PermissionCodes.HrmSensitiveCodes);
    }

    public Guid    OrganizationId { get; }
    public int     UserId         { get; }
    public string? UserName       { get; }
    public bool    IsSuperAdmin   { get; }
    public int?    UserIdOrNull   => UserId > 0 ? UserId : null;

    public bool Has(string permission) => _permissions.Contains(permission);
    public bool HasAny(params string[] permissions) => permissions.Any(_permissions.Contains);

    public void Require(string permission, string? message = null)
    {
        if (!Has(permission)) throw new ForbiddenException(message ?? "You do not have permission to perform this action.");
    }

    /// <summary>HttpContext.Items key of the name the seed filter resolved for this request (<see cref="HrmUserNames"/>).</summary>
    public const string ResolvedNameItem = "Hrm.ResolvedUserName";

    /// <summary>The token's name claim. The SMS token issues none today, so this is normally null: pass <paramref name="resolvedName"/>.</summary>
    public static string? ClaimName(ClaimsPrincipal user) => user.FindFirst("name")?.Value ?? user.FindFirst(ClaimTypes.Name)?.Value;

    public static HrmCaller From(ClaimsPrincipal user, ITenantContext tenant, string? resolvedName = null) =>
        new(tenant.OrganizationId, user.GetUserId(), user.GetPermissions(), resolvedName ?? ClaimName(user),
            user.FindFirst("is_super_admin")?.Value == "true");
}

/// <summary>Coded business errors (API-CONTRACT §0): the message starts with "CODE: ", so a screen can branch on it.</summary>
public static class HrmErrors
{
    public static BadRequestException Bad(string code, string message) => new($"{code}: {message}");
    public static ConflictException Conflict(string code, string message) => new($"{code}: {message}");
    public static UnprocessableEntityException Unprocessable(string code, string message) => new($"{code}: {message}");
}

/// <summary>An encrypted value: what the columns of an S3 field hold.</summary>
public sealed record ProtectedValue(string Cipher, string Hash, string Tail);

/// <summary>
/// S3 handling (§5): AES (the platform's <see cref="IEncryptionService"/>, key AppSettings:AesEncryptionKey), a keyed hash for
/// uniqueness and exact search without decrypting, and the masks. Keys come from configuration only; nothing here is logged.
/// </summary>
public interface IHrmSensitiveData
{
    /// <summary>13 digits, dashes ignored; else 400 EMPLOYEE_CNIC_INVALID. Null/blank = null.</summary>
    string? NormalizeCnic(string? cnic);
    ProtectedValue Protect(Guid organizationId, string plain, int tailLength);
    string Hash(Guid organizationId, string plain);
    string Reveal(string cipher);
    string? MaskCnic(string? lastDigit);
    string? MaskTail(string? tail);
    /// <summary>12345-1234567-1.</summary>
    string FormatCnic(string digits);
}

internal sealed class HrmSensitiveData : IHrmSensitiveData
{
    private readonly IEncryptionService _encryption;
    private readonly byte[] _hashKey;

    public HrmSensitiveData(IEncryptionService encryption, IOptions<AppSettings> settings)
    {
        _encryption = encryption;
        // Purpose-bound: never the AES key itself. Changing AesEncryptionKey would orphan the hashes as it orphans the ciphertext.
        _hashKey = SHA256.HashData(Encoding.UTF8.GetBytes("hrm-keyed-hash-v1:" + settings.Value.AesEncryptionKey));
    }

    public string? NormalizeCnic(string? cnic)
    {
        if (string.IsNullOrWhiteSpace(cnic)) return null;
        var digits = cnic.Replace("-", "").Replace(" ", "");
        if (digits.Length != 13 || !digits.All(char.IsAsciiDigit))
            throw HrmErrors.Bad("EMPLOYEE_CNIC_INVALID", "A CNIC has 13 digits, for example 12345-1234567-1.");
        return digits;
    }

    public ProtectedValue Protect(Guid organizationId, string plain, int tailLength) =>
        new(_encryption.Encrypt(plain), Hash(organizationId, plain), plain.Length <= tailLength ? plain : plain[^tailLength..]);

    /// <summary>Per organization, so the same CNIC in two tenants hashes differently and nothing can be correlated across them.</summary>
    public string Hash(Guid organizationId, string plain)
    {
        using var hmac = new HMACSHA256(_hashKey);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{organizationId:N}|{plain}")));
    }

    public string Reveal(string cipher) => _encryption.Decrypt(cipher);
    public string? MaskCnic(string? lastDigit) => lastDigit is null ? null : $"*****-*******-{lastDigit}";
    public string? MaskTail(string? tail) => tail is null ? null : $"****{tail}";
    public string FormatCnic(string digits) => digits.Length == 13 ? $"{digits[..5]}-{digits[5..12]}-{digits[12]}" : digits;
}

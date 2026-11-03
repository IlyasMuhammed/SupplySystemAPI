using System.Security.Claims;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Finance.ArAp;

/// <summary>
/// A48 (CORE, API-CONTRACT §10.3) — who is calling an A48 service: the organization (always explicit in every query — a super
/// admin bypasses the tenant filter), the user (int id + a display-name snapshot from the org user directory) and the
/// permissions. Every A48 service takes one of these instead of reading the HTTP context.
/// </summary>
public sealed class ArApCaller
{
    private readonly HashSet<string> _permissions;

    public ArApCaller(Guid organizationId, int userId, IEnumerable<string> permissions, string? userName = null)
    {
        OrganizationId = organizationId;
        UserId         = userId;
        UserName       = userName;
        _permissions   = new HashSet<string>(permissions, StringComparer.Ordinal);
    }

    public Guid    OrganizationId { get; }
    public int     UserId         { get; }
    public string? UserName       { get; }

    /// <summary>For the audit columns: null for a system caller (user id 0).</summary>
    public int? UserIdOrNull => UserId > 0 ? UserId : null;

    public bool Has(string permission) => _permissions.Contains(permission);

    public bool HasAny(params string[] permissions) => permissions.Any(_permissions.Contains);

    /// <summary>403 with <paramref name="message"/> unless <paramref name="allowed"/>.</summary>
    public void Require(bool allowed, string message = "You do not have permission to perform this action.")
    {
        if (!allowed) throw new ForbiddenException(message);
    }

    /// <summary>403 unless the caller holds one of <paramref name="permissions"/>.</summary>
    public void RequireAny(params string[] permissions) => Require(HasAny(permissions));

    /// <summary>
    /// The caller of this request. The name comes from <paramref name="users"/> by user id (the SMS token carries no name
    /// claim — A47 QA-1), else the name claim; a missing or failing directory never fails the request.
    /// </summary>
    public static async Task<ArApCaller> FromAsync(
        ClaimsPrincipal user, ITenantContext tenant, IOrgUserDirectory? users, CancellationToken ct = default)
    {
        var id   = user.GetUserId();
        var org  = tenant.OrganizationId;
        string? name = null;
        if (users is not null && id > 0 && org != Guid.Empty)
        {
            try
            {
                name = (await users.GetUsersAsync(org, [id], ct)).FirstOrDefault(u => u.UserId == id)?.Name?.Trim();
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // display only
            }
        }
        name ??= user.FindFirst("name")?.Value ?? user.FindFirst(ClaimTypes.Name)?.Value;
        return new ArApCaller(org, id, user.GetPermissions(), string.IsNullOrWhiteSpace(name) ? null : name);
    }

    /// <summary>A system caller (jobs, seeders, tests): every A48 code, no user.</summary>
    public static ArApCaller System(Guid organizationId) => new(organizationId, 0, PermissionCodes.ArApCodes, "System");
}

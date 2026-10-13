using System.Security.Claims;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Crm.Services;

/// <summary>
/// Who is calling: the organization (always explicit in every query — a super admin bypasses the tenant filter), the user
/// and their permissions. <see cref="IsManager"/> (CRM_ASSIGN_LEAD) lifts the own-records rule (D-5).
/// </summary>
public sealed class CrmCaller
{
    private readonly HashSet<string> _permissions;

    public CrmCaller(Guid organizationId, int userId, IEnumerable<string> permissions, string? userName = null)
    {
        OrganizationId = organizationId;
        UserId = userId;
        UserName = userName;
        _permissions = new HashSet<string>(permissions, StringComparer.Ordinal);
    }

    public Guid    OrganizationId { get; }
    public int     UserId         { get; }
    public string? UserName       { get; }

    public bool Has(string permission) => _permissions.Contains(permission);
    public bool IsManager => Has(PermissionCodes.CRM_ASSIGN_LEAD);

    public void Require(string permission, string message)
    {
        if (!Has(permission)) throw new ForbiddenException(message);
    }

    public static CrmCaller From(ClaimsPrincipal user, ITenantContext tenant) =>
        new(tenant.OrganizationId, user.GetUserId(), user.GetPermissions(),
            user.FindFirst("name")?.Value ?? user.FindFirst(ClaimTypes.Name)?.Value);
}

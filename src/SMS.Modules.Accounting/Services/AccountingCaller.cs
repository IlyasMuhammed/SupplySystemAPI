using System.Security.Claims;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Accounting.Services;

/// <summary>
/// Who is calling: the organization (always explicit in every query — a super admin bypasses the tenant filter), the user
/// and their permissions (D-14). Services take one of these instead of reading the HTTP context.
/// </summary>
public sealed class AccountingCaller
{
    private readonly HashSet<string> _permissions;

    public AccountingCaller(Guid organizationId, int userId, IEnumerable<string> permissions, string? userName = null)
    {
        OrganizationId = organizationId;
        UserId = userId;
        UserName = userName;
        _permissions = new HashSet<string>(permissions, StringComparer.Ordinal);
    }

    public Guid    OrganizationId { get; }
    public int     UserId         { get; }
    public string? UserName       { get; }

    /// <summary>For audit columns (CreatedBy …): null for a system caller (user id 0).</summary>
    public int? UserIdOrNull => UserId > 0 ? UserId : null;

    public bool Has(string permission) => _permissions.Contains(permission);

    /// <summary>GL_VIEW, or either manage code (they imply reading).</summary>
    public bool CanView => Has(PermissionCodes.GL_VIEW) || CanManageAccounts;

    /// <summary>D-14 — chart of accounts create/update/delete: GL_ACCOUNT_MANAGE or GL_SETUP_MANAGE.</summary>
    public bool CanManageAccounts => Has(PermissionCodes.GL_ACCOUNT_MANAGE) || CanManageSetup;

    /// <summary>D-14 — apply/upload, fiscal years, defaults, opening balances, complete/reset, mappings.</summary>
    public bool CanManageSetup => Has(PermissionCodes.GL_SETUP_MANAGE);

    public void Require(bool allowed, string message = "You do not have permission to perform this action.")
    {
        if (!allowed) throw new ForbiddenException(message);
    }

    public static AccountingCaller From(ClaimsPrincipal user, ITenantContext tenant) =>
        new(tenant.OrganizationId, user.GetUserId(), user.GetPermissions(),
            user.FindFirst("name")?.Value ?? user.FindFirst(ClaimTypes.Name)?.Value);

    /// <summary>A system caller (startup jobs, tests): every GL permission, no user.</summary>
    public static AccountingCaller System(Guid organizationId) =>
        new(organizationId, 0, [PermissionCodes.GL_VIEW, PermissionCodes.GL_ACCOUNT_MANAGE, PermissionCodes.GL_SETUP_MANAGE], "System");
}

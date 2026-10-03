using System.Security.Claims;
using SMS.Shared.Authorization;

namespace SMS.Modules.Auth.Models;

/// <summary>
/// Who an administrative call is made by, read from their access token: the user, their organization,
/// whether they are a platform super admin, and the permission codes they hold.
/// <para>
/// <b>Why it is passed in explicitly.</b> The guards in <c>AuthService</c> (you cannot change your own role,
/// cannot touch a super admin, cannot hand out a permission you do not hold, must stay inside your own
/// organization) all need to know who is asking. The tenant query filter alone cannot answer that: a super
/// admin bypasses it and so does an anonymous request, so "the row came back" never proves "the caller may
/// change it". Until 2026-10-02 several of these calls trusted exactly that, and any signed-in user could
/// grant themselves every permission through <c>PUT api/auth/users/{id}/permissions</c>.
/// </para>
/// <para>
/// <b>Super admin</b> is the <c>is_super_admin</c> claim, stamped at login from SuperAdminUsers membership
/// (MT-007) — the same source <c>TenantContext.IsSuperAdmin</c> reads. It is deliberately NOT the
/// PLATFORM_SUPER_ADMIN permission: that code also comes with the global System Admin role, so holding it
/// does not make someone the platform operator.
/// </para>
/// </summary>
public sealed record AuthCaller(int UserId, Guid OrganizationId, bool IsSuperAdmin, IReadOnlySet<string> Permissions)
{
    public static AuthCaller From(ClaimsPrincipal user) => new(
        user.GetUserId(),
        Guid.TryParse(user.FindFirst("organizationId")?.Value, out var organizationId) ? organizationId : Guid.Empty,
        user.FindFirst("is_super_admin")?.Value == "true",
        user.GetPermissions().ToHashSet(StringComparer.Ordinal));

    /// <summary>Whether the caller may pass this permission on to someone else: a super admin may pass any.</summary>
    public bool Holds(string permissionCode) => IsSuperAdmin || Permissions.Contains(permissionCode);
}

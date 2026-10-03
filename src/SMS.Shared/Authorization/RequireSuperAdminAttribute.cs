using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SMS.Shared.Pagination;

namespace SMS.Shared.Authorization;

/// <summary>
/// The caller must be a platform super admin: a member of SuperAdminUsers, which login stamps into the
/// <c>is_super_admin</c> claim (MT-007 — the sole authoritative source). For the endpoints that manage every
/// organization, a permission code is not enough on its own: PLATFORM_SUPER_ADMIN is also carried by the
/// global System Admin role, and any custom role it was ever put on, so holding it would let one tenant's
/// user create, deactivate and re-plan every other tenant. Used alongside [RequirePermission]; an
/// unauthenticated caller has already been answered 401 by the authorization policy.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequireSuperAdminAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var user = context.HttpContext.User;
        if (user.Identity?.IsAuthenticated != true) return;
        if (user.FindFirst("is_super_admin")?.Value == "true") return;

        context.Result = new ObjectResult(ApiResponse.Fail("Only a platform super admin can manage organizations."))
        {
            StatusCode = 403
        };
    }
}

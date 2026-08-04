using System.Text.Json;
using Microsoft.AspNetCore.Http;
using SMS.Shared.Common;
using SMS.Shared.Pagination;

namespace SMS.Shared.Middleware;

/// <summary>
/// Resolves the current request's tenant from the JWT's organizationId claim and blocks it with
/// 401 if the organization has been deactivated — checked on every request, not just at login, so
/// an org disabled mid-session is cut off within the snapshot cache's TTL rather than only once
/// its (long-lived) refresh token expires. Registered after UseAuthentication/UseAuthorization
/// (SMS.API/Program.cs) so HttpContext.User is already populated.
///
/// Unauthenticated requests (anonymous endpoints, or none at all) and Super Admin requests are
/// bypassed entirely — same rationale as ITenantContext.IsSuperAdmin/TenantContext: there is no
/// tenant to resolve yet for login/refresh/forgot-password/RFQ-portal flows, and Super Admin
/// requests are explicitly meant to cross tenant boundaries.
/// </summary>
public sealed class TenantMiddleware
{
    // Shared with FeatureAuthorizationFilter so it can reuse the snapshot already resolved here
    // instead of hitting the cache/DB a second time in the same request.
    public const string SnapshotItemKey = "TenantMiddleware.Snapshot";

    private readonly RequestDelegate _next;

    public TenantMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext, ITenantSnapshotProvider snapshots)
    {
        if (context.User.Identity?.IsAuthenticated != true || tenantContext.IsSuperAdmin)
        {
            await _next(context);
            return;
        }

        var snapshot = await snapshots.GetSnapshotAsync(tenantContext.OrganizationId);
        if (snapshot is null || !snapshot.IsActive)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(
                ApiResponse.Fail("This organization has been deactivated."),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            return;
        }

        context.Items[SnapshotItemKey] = snapshot;
        await _next(context);
    }
}

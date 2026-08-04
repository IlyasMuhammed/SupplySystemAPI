using Microsoft.AspNetCore.Mvc.Filters;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Middleware;

namespace SMS.Shared.Authorization;

/// <summary>
/// Registered globally (SMS.API/Program.cs: options.Filters.Add&lt;FeatureAuthorizationFilter&gt;()).
/// Enforces [RequiresFeature] at the API level — a disabled feature is a 403, not just a hidden
/// sidebar item. Super Admin requests bypass this exactly like they bypass tenant query filters.
/// </summary>
public sealed class FeatureAuthorizationFilter : IAsyncActionFilter
{
    private readonly ITenantContext _tenantContext;
    private readonly ITenantSnapshotProvider _snapshots;

    public FeatureAuthorizationFilter(ITenantContext tenantContext, ITenantSnapshotProvider snapshots)
    {
        _tenantContext = tenantContext;
        _snapshots = snapshots;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var attribute = context.ActionDescriptor.EndpointMetadata
            .OfType<RequiresFeatureAttribute>()
            .FirstOrDefault();

        if (attribute is not null && !_tenantContext.IsSuperAdmin)
        {
            // Reuse the snapshot TenantMiddleware already resolved for this request when present,
            // instead of hitting the cache/DB a second time.
            var snapshot = context.HttpContext.Items[TenantMiddleware.SnapshotItemKey] as TenantSnapshot
                ?? await _snapshots.GetSnapshotAsync(_tenantContext.OrganizationId);

            if (snapshot is null || !snapshot.EnabledFeatureCodes.Contains(attribute.FeatureCode))
                throw new ForbiddenException($"Feature '{attribute.FeatureCode}' is not enabled for this organization.");
        }

        await next();
    }
}

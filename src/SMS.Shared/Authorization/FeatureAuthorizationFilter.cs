using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SMS.Shared.Common;
using SMS.Shared.Middleware;

namespace SMS.Shared.Authorization;

/// <summary>
/// Registered globally (SMS.API/Program.cs: options.Filters.Add&lt;FeatureAuthorizationFilter&gt;()).
/// Enforces [RequiresFeature] at the API level — a disabled feature is a 403, not just a hidden
/// sidebar item. Super Admin requests bypass this exactly like they bypass tenant query filters.
/// <para>
/// A37 D-6 — every [RequiresFeature] on the endpoint must pass (each is any-of). A code passes by its level in the
/// tenant snapshot: Full → always; Grace (module switched off, grace running) → reads, and writes on a route that names
/// an existing record (a route parameter such as <c>{uuid}</c>), never a create; ReadOnly (switched off after grace, or
/// unlicensed, but the org once had it) → reads only; None → nothing. The refusal is the MODULE_NOT_LICENSED body of
/// API-CONTRACT §1.4, written here as the action result so every other 403 keeps its usual shape.
/// </para>
/// </summary>
public sealed class FeatureAuthorizationFilter : IAsyncActionFilter
{
    public const string ModuleNotLicensedErrorCode = "MODULE_NOT_LICENSED";

    private static readonly HashSet<string> RouteValuesThatAreNotRecords = new(StringComparer.OrdinalIgnoreCase)
    {
        "controller", "action", "area", "page"
    };

    private readonly ITenantContext _tenantContext;
    private readonly ITenantSnapshotProvider _snapshots;
    private readonly TimeProvider _clock;

    public FeatureAuthorizationFilter(ITenantContext tenantContext, ITenantSnapshotProvider snapshots, TimeProvider? clock = null)
    {
        _tenantContext = tenantContext;
        _snapshots = snapshots;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var attributes = context.ActionDescriptor.EndpointMetadata.OfType<RequiresFeatureAttribute>().ToList();

        if (attributes.Count > 0 && !_tenantContext.IsSuperAdmin)
        {
            // Reuse the snapshot TenantMiddleware already resolved for this request when present,
            // instead of hitting the cache/DB a second time.
            var snapshot = context.HttpContext.Items[TenantMiddleware.SnapshotItemKey] as TenantSnapshot
                ?? await _snapshots.GetSnapshotAsync(_tenantContext.OrganizationId);

            var refusal = Evaluate(snapshot, attributes, context.HttpContext.Request.Method,
                context.RouteData.Values.Keys.Any(k => !RouteValuesThatAreNotRecords.Contains(k)), _clock.GetUtcNow().UtcDateTime);

            if (refusal is not null)
            {
                context.Result = new ObjectResult(ModuleNotEnabledResponse.For(refusal)) { StatusCode = StatusCodes.Status403Forbidden };
                return;
            }
        }

        await next();
    }

    /// <summary>The access that refuses the request, or null when every attribute passes.</summary>
    public static FeatureAccess? Evaluate(
        TenantSnapshot? snapshot, IReadOnlyList<RequiresFeatureAttribute> attributes, string method, bool actsOnRecord, DateTime utcNow)
    {
        var isRead = HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

        foreach (var attribute in attributes)
        {
            if (snapshot is null)
                return new FeatureAccess(attribute.FeatureCode, attribute.FeatureCode, attribute.FeatureCode, FeatureAccessLevel.None, null);

            // Any-of: the most open of the codes decides; a refusal names that one's blocking code.
            var best = attribute.AnyOfFeatureCodes.Select(c => snapshot.AccessFor(c, utcNow)).MaxBy(a => a.Level)!;
            var allowed = best.Level switch
            {
                FeatureAccessLevel.Full     => true,
                FeatureAccessLevel.Grace    => isRead || actsOnRecord,
                FeatureAccessLevel.ReadOnly => isRead,
                _                           => false
            };
            if (!allowed) return best;
        }

        return null;
    }
}

/// <summary>A37 API-CONTRACT §1.4 — the 403 body for a module / feature that is not enabled.</summary>
public sealed class ModuleNotEnabledResponse
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string ErrorCode { get; init; } = FeatureAuthorizationFilter.ModuleNotLicensedErrorCode;
    public ModuleNotEnabledResult Result { get; init; } = new();

    public static ModuleNotEnabledResponse For(FeatureAccess access) => new()
    {
        Message = $"The {access.BlockingName} module is not enabled for your organization.",
        Result  = new ModuleNotEnabledResult { Module = access.BlockingCode, GraceEndsAt = access.GraceEndsAt }
    };
}

public sealed class ModuleNotEnabledResult
{
    public string Module { get; init; } = string.Empty;
    public DateTime? GraceEndsAt { get; init; }
}

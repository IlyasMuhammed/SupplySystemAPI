using Microsoft.AspNetCore.Mvc.Filters;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Shared.Authorization;

/// <summary>
/// Registered globally (SMS.API/Program.cs: options.Filters.Add&lt;SupplierAccessAuthorizationFilter&gt;()).
/// Enforces [RequiresSupplierAccess] at the API level — direct-ID/URL access to a supplier the
/// current user isn't mapped to is a 403, not just a hidden picker entry. Mirrors
/// FeatureAuthorizationFilter's shape exactly.
/// </summary>
public sealed class SupplierAccessAuthorizationFilter : IAsyncActionFilter
{
    private readonly IUserSupplierAccessService _access;

    public SupplierAccessAuthorizationFilter(IUserSupplierAccessService access) => _access = access;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var attribute = context.ActionDescriptor.EndpointMetadata
            .OfType<RequiresSupplierAccessAttribute>()
            .FirstOrDefault();

        if (attribute is not null)
        {
            // Most actions carry the id as a route segment ({supplierId:guid}); a few (e.g. report
            // endpoints) take it as a query parameter instead — check both.
            var raw = context.RouteData.Values[attribute.RouteParamName]?.ToString()
                ?? context.HttpContext.Request.Query[attribute.RouteParamName].FirstOrDefault();

            if (Guid.TryParse(raw, out var supplierUuid) &&
                !await _access.CanAccessSupplierAsync(supplierUuid))
            {
                throw new ForbiddenException("You do not have access to this supplier.");
            }
        }

        await next();
    }
}

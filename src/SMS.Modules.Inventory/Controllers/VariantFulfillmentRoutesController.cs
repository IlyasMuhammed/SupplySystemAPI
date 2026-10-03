using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Pagination;

namespace SMS.Modules.Inventory.Controllers;

/// <summary>
/// A33 C2 — assigning fulfillment routes to variants (docs/fulfillment-routes/API-CONTRACT.md §4). Its own controller
/// because the variant PATCH is ungated: route assignment needs FULFILLMENT_ROUTE_ASSIGN. No class-level
/// [RequiresFeature]: FeatureAuthorizationFilter reads only the first one it finds, so each action names its own —
/// MODULE_INVENTORY for the variant endpoint, MODULE_LOGISTICS for the one under api/fulfillment-routes (contract §1).
/// Errors surface through GlobalExceptionMiddleware: 404 for another organization's variant or route, 400 for an
/// inactive, unknown or foreign route and for a bad category.
/// </summary>
[ApiController]
public class VariantFulfillmentRoutesController : ControllerBase
{
    private readonly IVariantFulfillmentRouteService _service;

    public VariantFulfillmentRoutesController(IVariantFulfillmentRouteService service) => _service = service;

    /// <summary>Sets the variant's default route, or clears it with a null (T-C2-01, T-C2-04).</summary>
    [HttpPut("api/variants/{uuid:guid}/fulfillment-route")]
    [RequiresFeature("MODULE_INVENTORY")]
    [RequirePermission(PermissionCodes.FULFILLMENT_ROUTE_ASSIGN)]
    public async Task<IActionResult> SetVariantFulfillmentRoute(
        Guid uuid, [FromBody] SetVariantFulfillmentRouteRequest request, CancellationToken ct)
    {
        var result = await _service.SetRouteAsync(uuid, request.FulfillmentRouteUuid, ct);
        return Ok(ApiResponse<VariantFulfillmentRouteModel>.Ok(result,
            result.FulfillmentRouteUuid is null ? "Fulfillment route cleared." : "Fulfillment route assigned."));
    }

    /// <summary>D-14 / BR-C2-02 — sets the route on every active variant with no route in the category (T-C2-03).</summary>
    [HttpPost("api/fulfillment-routes/{uuid:guid}/assign-by-category")]
    [RequiresFeature("MODULE_LOGISTICS")]
    [RequirePermission(PermissionCodes.FULFILLMENT_ROUTE_ASSIGN)]
    public async Task<IActionResult> AssignByCategory(
        Guid uuid, [FromBody] AssignRouteByCategoryRequest request, CancellationToken ct)
    {
        var result = await _service.AssignByCategoryAsync(uuid, request, ct);
        return Ok(ApiResponse<FulfillmentRouteBulkAssignResult>.Ok(result,
            $"Route assigned to {result.Updated} variant(s); {result.Skipped} already had a route."));
    }
}

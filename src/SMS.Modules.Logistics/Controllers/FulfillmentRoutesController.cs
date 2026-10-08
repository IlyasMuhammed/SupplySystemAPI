using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Logistics.Controllers;

/// <summary>
/// A33 PA-05 — the organization's fulfillment routes (docs/fulfillment-routes/API-CONTRACT.md §3). Reading is for every
/// screen that shows or picks a route (the spec's "authenticated"; a bare sign-in is not allowed here); every change is
/// FULFILLMENT_ROUTE_MANAGE. Bulk assignment by category (<c>…/{uuid}/assign-by-category</c>) is mounted by Inventory.
/// </summary>
[ApiController]
[Route("api/fulfillment-routes")]
[RequiresFeature("MODULE_LOGISTICS")]
public class FulfillmentRoutesController : ControllerBase
{
    private readonly IFulfillmentRouteService _service;

    public FulfillmentRoutesController(IFulfillmentRouteService service) => _service = service;

    /// <summary>
    /// Active routes by display order, then code; <paramref name="includeInactive"/> for the settings screen. A34:
    /// <paramref name="category"/> (STOCK | MANUFACTURE | …) narrows the list; an unknown one is a 400.
    /// </summary>
    [HttpGet]
    [RequirePermission(PermissionCodes.FULFILLMENT_ROUTE_VIEW, PermissionCodes.FULFILLMENT_ROUTE_MANAGE,
        PermissionCodes.FULFILLMENT_ROUTE_ASSIGN, PermissionCodes.SALE_ORDER_VIEW, PermissionCodes.INVENTORY_VIEW,
        PermissionCodes.DELIVERY_VIEW)]
    public async Task<IActionResult> GetList([FromQuery] bool includeInactive = false, [FromQuery] string? category = null) =>
        Ok(ApiResponse<IReadOnlyList<FulfillmentRouteModel>>.Ok(await _service.GetListAsync(includeInactive, category)));

    [HttpGet("{uuid:guid}")]
    [RequirePermission(PermissionCodes.FULFILLMENT_ROUTE_VIEW, PermissionCodes.FULFILLMENT_ROUTE_MANAGE,
        PermissionCodes.FULFILLMENT_ROUTE_ASSIGN, PermissionCodes.SALE_ORDER_VIEW, PermissionCodes.INVENTORY_VIEW,
        PermissionCodes.DELIVERY_VIEW)]
    public async Task<IActionResult> GetById(Guid uuid) =>
        Result(await _service.GetByUuidAsync(uuid), string.Empty);

    [HttpPost]
    [RequirePermission(PermissionCodes.FULFILLMENT_ROUTE_MANAGE)]
    public async Task<IActionResult> Create([FromBody] CreateFulfillmentRouteRequest req) =>
        Ok(ApiResponse<FulfillmentRouteModel>.Ok(await _service.CreateAsync(req, User.GetUserId()), StaticResponseMessage.recordCreatedSuccessfully));

    /// <summary>Name, description, display order and — custom routes only — the steps. The code never changes.</summary>
    [HttpPut("{uuid:guid}")]
    [RequirePermission(PermissionCodes.FULFILLMENT_ROUTE_MANAGE)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] UpdateFulfillmentRouteRequest req) =>
        Result(await _service.UpdateAsync(uuid, req, User.GetUserId()), StaticResponseMessage.recordUpdatedSuccessfully);

    /// <summary>409 while the route is a default or still used by active variants or open sale order lines (BR-C1-07).</summary>
    [HttpPatch("{uuid:guid}/deactivate")]
    [RequirePermission(PermissionCodes.FULFILLMENT_ROUTE_MANAGE)]
    public async Task<IActionResult> Deactivate(Guid uuid) =>
        Result(await _service.SetActiveAsync(uuid, false, User.GetUserId()), "Fulfillment route deactivated.");

    [HttpPatch("{uuid:guid}/activate")]
    [RequirePermission(PermissionCodes.FULFILLMENT_ROUTE_MANAGE)]
    public async Task<IActionResult> Activate(Guid uuid) =>
        Result(await _service.SetActiveAsync(uuid, true, User.GetUserId()), "Fulfillment route activated.");

    /// <summary>The default for the route's class (SHIP / SELF_PICKUP orders); the previous one of that class is cleared.</summary>
    [HttpPatch("{uuid:guid}/set-default")]
    [RequirePermission(PermissionCodes.FULFILLMENT_ROUTE_MANAGE)]
    public async Task<IActionResult> SetDefault(Guid uuid) =>
        Result(await _service.SetDefaultAsync(uuid, User.GetUserId()), "Default route set.");

    [HttpPatch("{uuid:guid}/clear-default")]
    [RequirePermission(PermissionCodes.FULFILLMENT_ROUTE_MANAGE)]
    public async Task<IActionResult> ClearDefault(Guid uuid) =>
        Result(await _service.ClearDefaultAsync(uuid, User.GetUserId()), "Default route cleared.");

    /// <summary>A custom route nothing refers to; a system, default or used route is a 409 (deactivate it instead).</summary>
    [HttpDelete("{uuid:guid}")]
    [RequirePermission(PermissionCodes.FULFILLMENT_ROUTE_MANAGE)]
    public async Task<IActionResult> Delete(Guid uuid) =>
        await _service.DeleteAsync(uuid, User.GetUserId())
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordDeletedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));

    private IActionResult Result(FulfillmentRouteModel? model, string message) =>
        model is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<FulfillmentRouteModel>.Ok(model, message));
}

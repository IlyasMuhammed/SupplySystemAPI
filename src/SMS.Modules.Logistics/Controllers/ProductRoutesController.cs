using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Logistics.Controllers;

/// <summary>
/// A37 D-12 — a product's variants with their configured and effective fulfillment routes (API-CONTRACT §4). Served by
/// Logistics, which owns routes; the variants come from Inventory through <c>IProductVariantRoutes</c>. Both the int id
/// (as the product page's other <c>api/products/{id}</c> routes) and the uuid are accepted.
/// </summary>
[ApiController]
[RequiresFeature("MODULE_LOGISTICS")]
public class ProductRoutesController : ControllerBase
{
    private readonly IFulfillmentRouteService _service;

    public ProductRoutesController(IFulfillmentRouteService service) => _service = service;

    [HttpGet("api/products/{id:int}/routes")]
    [RequirePermission(PermissionCodes.FULFILLMENT_ROUTE_VIEW, PermissionCodes.FULFILLMENT_ROUTE_MANAGE, PermissionCodes.FULFILLMENT_ROUTE_ASSIGN)]
    public async Task<IActionResult> GetById(int id) => Result(await _service.GetProductRoutesAsync(id, null));

    [HttpGet("api/products/{uuid:guid}/routes")]
    [RequirePermission(PermissionCodes.FULFILLMENT_ROUTE_VIEW, PermissionCodes.FULFILLMENT_ROUTE_MANAGE, PermissionCodes.FULFILLMENT_ROUTE_ASSIGN)]
    public async Task<IActionResult> GetByUuid(Guid uuid) => Result(await _service.GetProductRoutesAsync(null, uuid));

    private IActionResult Result(IReadOnlyList<ProductVariantRouteModel>? rows) =>
        rows is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<IReadOnlyList<ProductVariantRouteModel>>.Ok(rows));
}

using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Logistics.Controllers;

/// <summary>
/// Fulfilment seen from the sale order (A29 §7.8): raise a delivery for an order, and list the
/// deliveries it has. Everything that happens to a delivery afterwards — release, pick, pack,
/// goods issue, collection, shipping, tracking — is <c>/api/logistics/deliveries/{uuid}/…</c>.
/// </summary>
/// <remarks>
/// Under <c>/api/sale-orders</c> like Demand's own endpoints, but gated as fulfilment work:
/// <c>MODULE_LOGISTICS</c> and the delivery permissions, because that is what they do.
/// </remarks>
[ApiController]
[RequiresFeature("MODULE_LOGISTICS")]
public class SaleOrderDeliveriesController : ControllerBase
{
    private readonly ISaleOrderDeliveryService _svc;

    public SaleOrderDeliveriesController(ISaleOrderDeliveryService svc) => _svc = svc;

    /// <summary>
    /// Raises a delivery for a confirmed sale order — every outstanding line in full, or the lines
    /// and quantities the body picks. The order's mode, address and reserved warehouse are the
    /// defaults; each may be overridden per delivery.
    /// </summary>
    /// <remarks>
    /// An order goes out in as many deliveries as it needs (§7.6); each one is independent from
    /// here on. Quantities already delivered, or on a delivery not yet issued, cannot be raised again.
    /// </remarks>
    [RequirePermission(PermissionCodes.DELIVERY_CREATE)]
    [HttpPost("api/sale-orders/{uuid:guid}/create-delivery")]
    public async Task<IActionResult> CreateDelivery(Guid uuid, [FromBody] CreateSaleOrderDeliveryRequest? req)
    {
        var deliveryUuid = await _svc.CreateAsync(uuid, req, User.GetUserId());
        return Ok(ApiResponse<Guid>.Ok(deliveryUuid, StaticResponseMessage.recordCreatedSuccessfully));
    }

    /// <summary>Every delivery raised for this sale order, oldest first.</summary>
    [RequirePermission(PermissionCodes.DELIVERY_VIEW)]
    [HttpGet("api/sale-orders/{uuid:guid}/deliveries")]
    public async Task<IActionResult> GetDeliveries(Guid uuid)
    {
        var deliveries = await _svc.GetDeliveriesAsync(uuid);
        return deliveries is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<IReadOnlyList<DeliveryListItemModel>>.Ok(deliveries));
    }

    /// <summary>
    /// A33 recovery (D-12 "Create deliveries"): the DRAFT deliveries a confirmed order's lines should have, one per
    /// route × ship-from warehouse, from the routes the lines were confirmed with. Idempotent — only what is still
    /// outstanding gets a delivery, and each line put on none is listed with the reason.
    /// </summary>
    /// <remarks>
    /// 400 unless the order is CONFIRMED or PARTIALLY_FULFILLED; 404 when it is not the caller organization's. Lines
    /// confirmed before routes are skipped: use <c>create-delivery</c> for them.
    /// </remarks>
    [RequirePermission(PermissionCodes.DELIVERY_CREATE)]
    [HttpPost("api/sale-orders/{uuid:guid}/create-deliveries")]
    public async Task<IActionResult> CreateDeliveries(Guid uuid)
    {
        var result = await _svc.CreateRouteDeliveriesAsync(uuid, User.GetUserId());
        return result is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<SMS.Shared.Common.SaleOrderDeliveryCreationResult>.Ok(
                result,
                result.Created.Count == 0
                    ? "No delivery was needed: every line is already on a delivery, fulfilled, or listed with the reason it was skipped."
                    : $"{result.Created.Count} delivery(ies) created."));
    }
}

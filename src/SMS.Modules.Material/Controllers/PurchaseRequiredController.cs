using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Material.Models;
using SMS.Modules.Material.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Pagination;

namespace SMS.Modules.Material.Controllers;

/// <summary>A31 C9 §11 — the Consolidated Purchase Required dashboard.</summary>
[ApiController]
[Route("api/purchase-required")]
[RequiresFeature("MODULE_MANUFACTURING")]
public class PurchaseRequiredController : ControllerBase
{
    private readonly IPurchaseRequiredService _service;

    public PurchaseRequiredController(IPurchaseRequiredService service) => _service = service;

    [HttpGet]
    [RequirePermission(PermissionCodes.SUPPLY_VIEW)]
    public async Task<IActionResult> GetList([FromQuery] Guid? supplierId, [FromQuery] decimal? minShortageQty, [FromQuery] string? sortBy) =>
        Ok(ApiResponse<IReadOnlyList<PurchaseRequiredLineModel>>.Ok(
            await _service.GetListAsync(new PurchaseRequiredListFilter { SupplierId = supplierId, MinShortageQty = minShortageQty, SortBy = sortBy })));

    [HttpGet("{variantUuid:guid}/affected-orders")]
    [RequirePermission(PermissionCodes.SUPPLY_VIEW)]
    public async Task<IActionResult> GetAffectedOrders(Guid variantUuid) =>
        Ok(ApiResponse<IReadOnlyList<PurchaseRequiredAffectedOrderModel>>.Ok(await _service.GetAffectedOrdersAsync(variantUuid)));

    [HttpPost("{variantUuid:guid}/acknowledge")]
    [RequirePermission(PermissionCodes.SUPPLY_CREATE)]
    public async Task<IActionResult> Acknowledge(Guid variantUuid, [FromBody] AcknowledgePurchaseRequiredRequest req)
    {
        await _service.AcknowledgeAsync(variantUuid, req, User.GetUserId());
        return Ok(ApiResponse.Ok("Marked as purchased manually."));
    }

    [HttpPost("{variantUuid:guid}/acknowledge/clear")]
    [RequirePermission(PermissionCodes.SUPPLY_CREATE)]
    public async Task<IActionResult> ClearAcknowledgement(Guid variantUuid)
    {
        await _service.ClearAcknowledgementAsync(variantUuid);
        return Ok(ApiResponse.Ok("Marker cleared."));
    }

    [HttpPost("{variantUuid:guid}/create-po")]
    [RequirePermission(PermissionCodes.PO_CREATE)]
    public async Task<IActionResult> CreatePurchaseOrder(Guid variantUuid, [FromBody] CreatePurchaseRequiredPoRequest req)
    {
        var poUuid = await _service.CreatePurchaseOrderAsync(variantUuid, req, User.GetUserId());
        return Ok(ApiResponse<Guid>.Ok(poUuid, "Purchase order created."));
    }
}

using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Material.Models;
using SMS.Modules.Material.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Material.Controllers;

/// <summary>A30 §28.6 — finished goods receipts.</summary>
[ApiController]
[Route("api")]
[RequiresFeature("MODULE_MANUFACTURING")]
public class FinishedGoodsReceiptsController : ControllerBase
{
    private readonly IFinishedGoodsReceiptService _service;

    public FinishedGoodsReceiptsController(IFinishedGoodsReceiptService service) => _service = service;

    [HttpPost("production-orders/{uuid:guid}/fgr")]
    [RequirePermission(PermissionCodes.FGR_CREATE)]
    public async Task<IActionResult> Create(Guid uuid, [FromBody] CreateFinishedGoodsReceiptRequest req)
    {
        var fgrUuid = await _service.CreateAsync(uuid, req, User.GetUserId());
        return Ok(ApiResponse<Guid>.Ok(fgrUuid, "Finished goods receipt created."));
    }

    [HttpGet("fgr/{uuid:guid}")]
    [RequirePermission(PermissionCodes.FGR_CREATE)]
    public async Task<IActionResult> GetById(Guid uuid)
    {
        var fgr = await _service.GetAsync(uuid) ?? throw new NotFoundException("Finished goods receipt", uuid);
        return Ok(ApiResponse<FinishedGoodsReceiptModel>.Ok(fgr));
    }

    [HttpGet("production-orders/{uuid:guid}/fgr")]
    [RequirePermission(PermissionCodes.FGR_CREATE)]
    public async Task<IActionResult> GetForOrder(Guid uuid) =>
        Ok(ApiResponse<IReadOnlyList<FinishedGoodsReceiptModel>>.Ok(await _service.GetForOrderAsync(uuid)));

    [HttpPost("fgr/{uuid:guid}/confirm")]
    [RequirePermission(PermissionCodes.FGR_CONFIRM)]
    public async Task<IActionResult> Confirm(Guid uuid)
    {
        await _service.ConfirmAsync(uuid, User.GetUserId());
        return Ok(ApiResponse.Ok("Finished goods receipt confirmed."));
    }
}

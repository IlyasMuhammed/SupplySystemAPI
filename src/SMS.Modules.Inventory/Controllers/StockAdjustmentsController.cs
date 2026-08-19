using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Constants;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Inventory.Controllers;

[ApiController]
//[Authorize]
[RequiresFeature("MODULE_INVENTORY")]
public class StockAdjustmentsController : ControllerBase
{
    private readonly IInventoryService _service;
    public StockAdjustmentsController(IInventoryService service) => _service = service;

    [HttpGet("api/stock-adjustments")]
    public async Task<IActionResult> GetAdjustments([FromQuery] AdjustmentListFilter filter)
    {
        var result = await _service.GetAdjustmentsAsync(filter);
        return Ok(ApiResponse<object>.Ok(result));
    }

    [HttpPost("api/stock-adjustments")]
  //  [RequirePermission(PermissionCodes.STOCK_ADJUST)]
    public async Task<IActionResult> CreateAdjustment([FromBody] CreateAdjustmentRequest req)
    {
        if (req.VariantId <= 0 || req.WarehouseId <= 0)
            return BadRequest(ApiResponse.Fail("VariantId and WarehouseId are required."));
        if (req.QtyAdjusted == 0)
            return BadRequest(ApiResponse.Fail("QtyAdjusted must be non-zero."));
        try
        {
            var result = await _service.CreateAdjustmentAsync(req, User.GetUserId());
            return Ok(ApiResponse<StockAdjustmentResult>.Ok(result, StaticResponseMessage.recordCreatedSuccessfully));
        }
        catch (BadRequestException ex)
        {
            return BadRequest(ApiResponse.Fail(ex.Message));
        }
    }

    [HttpPost("api/stock-adjustments/{uuid:guid}/approve")]
    //[RequirePermission(PermissionCodes.STOCK_MANAGE)]
    public async Task<IActionResult> ApproveAdjustment(Guid uuid)
    {
        try
        {
            var approved = await _service.ApproveAdjustmentAsync(uuid, User.GetUserId());
            if (!approved) return NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
            return Ok(ApiResponse.Ok("Adjustment approved and stock updated."));
        }
        catch (BadRequestException ex)
        {
            return BadRequest(ApiResponse.Fail(ex.Message));
        }
    }

    [HttpPost("api/stock-adjustments/{uuid:guid}/reject")]
    //[RequirePermission(PermissionCodes.STOCK_MANAGE)]
    public async Task<IActionResult> RejectAdjustment(Guid uuid, [FromBody] RejectAdjustmentRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Reason))
            return BadRequest(ApiResponse.Fail("Rejection reason is required."));
        try
        {
            var rejected = await _service.RejectAdjustmentAsync(uuid, req.Reason, User.GetUserId());
            if (!rejected) return NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
            return Ok(ApiResponse.Ok("Adjustment rejected."));
        }
        catch (BadRequestException ex)
        {
            return BadRequest(ApiResponse.Fail(ex.Message));
        }
    }
}

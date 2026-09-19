using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;
using SMS.WorkflowEngine.Models;

namespace SMS.Modules.Demand.Controllers;

// A29-P3-07 §4.5. /confirm and /cancel are bare status transitions — §4.3's availability check,
// reservation and back-to-back PO creation are not implemented here (see SaleOrderService.ConfirmAsync's
// own remarks); TC-04/TC-05, which exercise that behaviour, cannot be fully verified against this
// controller alone yet.
[ApiController]
[Route("api/sale-orders")]
[RequiresFeature("MODULE_DEMAND")]
public class SaleOrdersController : ControllerBase
{
    private readonly ISaleOrderService _service;

    public SaleOrdersController(ISaleOrderService service) => _service = service;

    [HttpPost]
    [RequirePermission(PermissionCodes.SALE_ORDER_CREATE)]
    public async Task<IActionResult> Create([FromBody] CreateSaleOrderRequest req)
    {
        var uuid = await _service.CreateAsync(req, User.GetUserId());
        return Ok(ApiResponse<Guid>.Ok(uuid, StaticResponseMessage.recordCreatedSuccessfully));
    }

    [HttpPut("{uuid:guid}")]
    [RequirePermission(PermissionCodes.SALE_ORDER_EDIT)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] UpdateSaleOrderRequest req)
    {
        var updated = await _service.UpdateAsync(uuid, req, User.GetUserId());
        return updated
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    [HttpGet("{uuid:guid}")]
    [RequirePermission(PermissionCodes.SALE_ORDER_VIEW)]
    public async Task<IActionResult> GetById(Guid uuid)
    {
        var result = await _service.GetByIdAsync(uuid);
        return result is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<SaleOrderModel>.Ok(result));
    }

    [HttpGet]
    [RequirePermission(PermissionCodes.SALE_ORDER_VIEW)]
    public async Task<IActionResult> GetList([FromQuery] SaleOrderListFilter filter)
    {
        var result = await _service.GetListAsync(filter);
        return Ok(ApiResponse<PaginatedResponse<SaleOrderModel>>.Ok(result));
    }

    [HttpPost("{uuid:guid}/confirm")]
    [RequirePermission(PermissionCodes.SALE_ORDER_CONFIRM)]
    public async Task<IActionResult> Confirm(Guid uuid)
    {
        var confirmed = await _service.ConfirmAsync(uuid, User.GetUserId());
        return confirmed
            ? Ok(ApiResponse.Ok("Sale order confirmed."))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    [HttpPost("{uuid:guid}/cancel")]
    [RequirePermission(PermissionCodes.SALE_ORDER_CANCEL)]
    public async Task<IActionResult> Cancel(Guid uuid, [FromBody] CancelSaleOrderRequest? req)
    {
        var cancelled = await _service.CancelAsync(uuid, User.GetUserId(), req?.Reason);
        return cancelled
            ? Ok(ApiResponse.Ok("Sale order cancelled."))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    [HttpGet("{uuid:guid}/timeline")]
    [RequirePermission(PermissionCodes.SALE_ORDER_VIEW)]
    public async Task<IActionResult> GetTimeline(Guid uuid)
    {
        var timeline = await _service.GetTimelineAsync(uuid);
        return timeline is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<TimelineDetail>.Ok(timeline));
    }

    // A preview of what §4.3's confirm would see right now — reserves nothing.
    [HttpGet("{uuid:guid}/availability")]
    [RequirePermission(PermissionCodes.SALE_ORDER_VIEW)]
    public async Task<IActionResult> GetAvailability(Guid uuid)
    {
        var availability = await _service.GetAvailabilityAsync(uuid);
        return availability is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<IReadOnlyList<SaleOrderLineAvailabilityModel>>.Ok(availability));
    }
}

public class CancelSaleOrderRequest
{
    public string? Reason { get; set; }
}

using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Logistics.Controllers;

/// <summary>
/// Pick lists — the warehouse's instruction sheet for a released delivery.
/// </summary>
/// <remarks>
/// Doing the work is gated by <c>PICKING</c> rather than a new delivery-specific code: that
/// permission already exists, is already seeded as "pick items to fulfil outbound orders", and had
/// nothing behind it until now. Reading is <c>DELIVERY_VIEW</c>, so a supervisor can watch
/// progress without being able to claim the walk.
/// </remarks>
[ApiController]
[Route("api/logistics/pick-lists")]
[RequiresFeature("MODULE_LOGISTICS")]
public class PickListsController : ControllerBase
{
    private readonly IPickListService _svc;
    public PickListsController(IPickListService svc) => _svc = svc;

    [RequirePermission(PermissionCodes.DELIVERY_VIEW)]
    [HttpGet]
    public async Task<IActionResult> GetList([FromQuery] PickListFilter filter)
    {
        var result = await _svc.GetListAsync(filter);
        return Ok(ApiResponse<PaginatedResponse<PickListListItemModel>>.Ok(result));
    }

    [RequirePermission(PermissionCodes.DELIVERY_VIEW)]
    [HttpGet("{uuid:guid}")]
    public async Task<IActionResult> GetById(Guid uuid)
    {
        var detail = await _svc.GetByUuidAsync(uuid);
        return detail is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<PickListModel>.Ok(detail));
    }

    /// <summary>
    /// Records what the picker actually found, one instruction at a time or many at once.
    /// </summary>
    /// <remarks>
    /// Lines may be confirmed in any order, across as many calls as the walk takes, and a line may
    /// be answered again to correct a miscount while the list is still open. Once every line has
    /// been answered the list closes itself, the quantities roll up onto the delivery, stock that
    /// was not picked is handed back, and the delivery moves to <c>PICKED</c>.
    /// </remarks>
    [RequirePermission(PermissionCodes.PICKING)]
    [HttpPost("{uuid:guid}/confirm")]
    public async Task<IActionResult> Confirm(Guid uuid, [FromBody] ConfirmPickRequest req)
    {
        var result = await _svc.ConfirmAsync(uuid, req, User.GetUserId());
        return result is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<ConfirmPickResultModel>.Ok(
                result,
                result.Completed ? "Picking complete." : "Pick confirmed."));
    }

    /// <summary>Hands the walk to somebody.</summary>
    [RequirePermission(PermissionCodes.PICKING)]
    [HttpPost("{uuid:guid}/assign")]
    public async Task<IActionResult> Assign(Guid uuid, [FromBody] AssignPickListRequest req)
    {
        var updated = await _svc.AssignAsync(uuid, req.AssignedToUserId, User.GetUserId());
        return updated
            ? Ok(ApiResponse.Ok("Pick list assigned."))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    /// <summary>
    /// Tears up the instruction sheet and returns the delivery to <c>RELEASED</c>. The stock stays
    /// reserved — the delivery is still going out.
    /// </summary>
    [RequirePermission(PermissionCodes.PICKING)]
    [HttpPost("{uuid:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid uuid, [FromBody] DeliveryReasonRequest req)
    {
        var cancelled = await _svc.CancelAsync(uuid, req, User.GetUserId());
        return cancelled
            ? Ok(ApiResponse.Ok("Pick list cancelled."))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }
}

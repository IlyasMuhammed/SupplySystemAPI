using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Visibility;
using SMS.Shared.Authorization;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Logistics.Controllers;

/// <summary>
/// What has gone wrong with a movement, named and owned until it is settled.
/// </summary>
/// <remarks>
/// Reading is <c>DELIVERY_VIEW</c> — the queue is what a despatch desk works from all day, and
/// walling it off behind an edit right would mean nobody sees a problem until somebody with the
/// right to fix it happens to look. Raising, assigning and closing are <c>DELIVERY_EDIT</c>.
/// </remarks>
[ApiController]
[Route("api/logistics/delivery-exceptions")]
[RequiresFeature("MODULE_LOGISTICS")]
public class DeliveryExceptionsController : ControllerBase
{
    private readonly IDeliveryExceptionService _exceptions;
    public DeliveryExceptionsController(IDeliveryExceptionService exceptions) => _exceptions = exceptions;

    /// <summary>
    /// The queue, worst first then oldest. Defaults to everything still needing work.
    /// </summary>
    [RequirePermission(PermissionCodes.DELIVERY_VIEW)]
    [HttpGet]
    public async Task<IActionResult> GetQueue([FromQuery] ExceptionFilter filter, CancellationToken ct)
    {
        var queue = await _exceptions.GetQueueAsync(filter, ct);
        return Ok(ApiResponse<PaginatedResponse<DeliveryExceptionModel>>.Ok(queue));
    }

    /// <summary>
    /// Counts by type, with the oldest one still open — a small count of something a week old is
    /// what a total hides.
    /// </summary>
    [RequirePermission(PermissionCodes.DELIVERY_VIEW)]
    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary(CancellationToken ct)
    {
        var summary = await _exceptions.GetSummaryAsync(ct);
        return Ok(ApiResponse<ExceptionSummaryModel>.Ok(summary));
    }

    [RequirePermission(PermissionCodes.DELIVERY_VIEW)]
    [HttpGet("{uuid:guid}")]
    public async Task<IActionResult> Get(Guid uuid, CancellationToken ct)
    {
        var exception = await _exceptions.GetAsync(uuid, ct);
        return exception is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<DeliveryExceptionModel>.Ok(exception));
    }

    /// <summary>Raises one by hand — how most damage claims and COD mismatches start.</summary>
    [RequirePermission(PermissionCodes.DELIVERY_EDIT)]
    [HttpPost("consignment/{consignmentUuid:guid}")]
    public async Task<IActionResult> Raise(
        Guid consignmentUuid, [FromBody] RaiseExceptionRequest req, CancellationToken ct)
    {
        var uuid = await _exceptions.RaiseAsync(consignmentUuid, req, User.GetUserId(), ct);
        return uuid is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<Guid>.Ok(uuid.Value, "Exception raised."));
    }

    /// <summary>Reassigns, re-grades, or parks one on somebody else. Not how it closes.</summary>
    [RequirePermission(PermissionCodes.DELIVERY_EDIT)]
    [HttpPatch("{uuid:guid}")]
    public async Task<IActionResult> Patch(
        Guid uuid, [FromBody] PatchExceptionRequest req, CancellationToken ct)
    {
        var patched = await _exceptions.PatchAsync(uuid, req, User.GetUserId(), ct);
        return patched
            ? Ok(ApiResponse.Ok("Exception updated."))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    /// <summary>Closes it as dealt with. The resolution is required.</summary>
    [RequirePermission(PermissionCodes.DELIVERY_EDIT)]
    [HttpPost("{uuid:guid}/resolve")]
    public async Task<IActionResult> Resolve(
        Guid uuid, [FromBody] ResolveExceptionRequest req, CancellationToken ct)
    {
        var resolved = await _exceptions.ResolveAsync(uuid, req, User.GetUserId(), ct);
        return resolved
            ? Ok(ApiResponse.Ok("Exception resolved."))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    /// <summary>
    /// Closes it as never having been a problem. Kept apart from resolving on purpose — counting a
    /// mistaken exception as one that was fixed flatters every figure a scorecard produces.
    /// </summary>
    [RequirePermission(PermissionCodes.DELIVERY_EDIT)]
    [HttpPost("{uuid:guid}/withdraw")]
    public async Task<IActionResult> Withdraw(
        Guid uuid, [FromBody] WithdrawExceptionRequest req, CancellationToken ct)
    {
        var withdrawn = await _exceptions.WithdrawAsync(uuid, req, User.GetUserId(), ct);
        return withdrawn
            ? Ok(ApiResponse.Ok("Exception withdrawn."))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }
}

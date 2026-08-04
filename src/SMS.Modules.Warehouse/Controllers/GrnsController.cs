using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Warehouse.Models;
using SMS.Modules.Warehouse.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Warehouse.Controllers;

[ApiController]
[Route("api/grns")]
[Authorize]
[RequiresFeature("MODULE_WAREHOUSE")]
public class GrnsController : ControllerBase
{
    private readonly IGrnService          _service;
    private readonly INotificationService _notif;

    public GrnsController(IGrnService service, INotificationService notif)
    {
        _service = service;
        _notif   = notif;
    }

    [HttpPost]
    public async Task<IActionResult> CreateGrn([FromBody] CreateGrnRequest req)
    {
        var uuid = await _service.CreateAsync(req, User.GetUserId());
        return Ok(ApiResponse<Guid>.Ok(uuid, StaticResponseMessage.recordCreatedSuccessfully));
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetGrns([FromQuery] GrnListFilter filter)
    {
        var result = await _service.GetListAsync(filter);
        return Ok(ApiResponse<PaginatedResponse<GrnListItemModel>>.Ok(result));
    }

    [HttpGet("{uuid:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetGrnById(Guid uuid)
    {
        var detail = await _service.GetByIdAsync(uuid);
        return detail is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<GrnDetailModel>.Ok(detail));
    }

    [HttpPatch("{uuid:guid}")]
    public async Task<IActionResult> UpdateGrn(Guid uuid, [FromBody] PatchGrnRequest req)
    {
        await _service.UpdateAsync(uuid, req, User.GetUserId());
        return Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully));
    }

    [HttpDelete("{uuid:guid}")]
    public async Task<IActionResult> DeleteGrn(Guid uuid)
    {
        await _service.DeleteAsync(uuid, User.GetUserId());
        return Ok(ApiResponse.Ok(StaticResponseMessage.recordDeletedSuccessfully));
    }

    [HttpPatch("{grnUuid:guid}/lines/{lineUuid:guid}")]
    public async Task<IActionResult> UpdateGrnLine(Guid grnUuid, Guid lineUuid, [FromBody] UpdateGrnLineRequest req)
    {
        await _service.UpdateLineAsync(grnUuid, lineUuid, req, User.GetUserId());
        return Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully));
    }

    // Narrower than UpdateGrnLine: only sets the catalogue product link, and — unlike UpdateGrnLine —
    // works any time before the GRN reaches a terminal state (APPROVED/REJECTED), not just DRAFT.
    // Recovery path for "Cannot post stock: line not linked to a catalogue product" without needing
    // to reject and recreate the whole GRN.
    [HttpPatch("{grnUuid:guid}/lines/{lineUuid:guid}/product")]
    public async Task<IActionResult> LinkGrnLineProduct(Guid grnUuid, Guid lineUuid, [FromBody] LinkGrnLineProductRequest req)
    {
        await _service.LinkLineProductAsync(grnUuid, lineUuid, req.ProductUuid, User.GetUserId());
        return Ok(ApiResponse.Ok("Catalogue product linked."));
    }

    [HttpPost("{grnUuid:guid}/lines/{lineUuid:guid}/inspect")]
    public async Task<IActionResult> InspectGrnLine(Guid grnUuid, Guid lineUuid, [FromBody] InspectGrnLineRequest req)
    {
        await _service.InspectLineAsync(grnUuid, lineUuid, req, User.GetUserId());
        return Ok(ApiResponse.Ok("Inspection recorded."));
    }

    [HttpPost("{uuid:guid}/submit")]
    public async Task<IActionResult> SubmitGrn(Guid uuid)
    {
        await _service.SubmitAsync(uuid, User.GetUserId());
        var grn     = await _service.GetByIdAsync(uuid);
        var actorId = User.GetUserId();
        if (grn is not null)
            await _notif.TryCreateAsync(new NotificationRequest(
                UserId: actorId, Type: "GRN_SUBMITTED", Title: "GRN Submitted",
                Message: $"Goods receipt {grn.GrnNumber} has been submitted for QC inspection.",
                Category: "Warehouse", EntityType: "GRN", EntityUuid: uuid.ToString(),
                NavigationUrl: $"/portal/pages/warehouse/grn/{uuid}",
                CreatedBy: actorId));
        return Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully));
    }

    [HttpPost("{uuid:guid}/qc-confirm")]
    public async Task<IActionResult> QcConfirmGrn(Guid uuid, [FromBody] QcConfirmRequest req)
    {
        await _service.QcConfirmAsync(uuid, req, User.GetUserId());
        return Ok(ApiResponse.Ok("QC confirmed. GRN is now pending inventory manager approval."));
    }

    [HttpPost("{uuid:guid}/qc-reject")]
    public async Task<IActionResult> QcRejectGrn(Guid uuid, [FromBody] QcRejectRequest req)
    {
        await _service.QcRejectAsync(uuid, req, User.GetUserId());
        return Ok(ApiResponse.Ok("QC rejected. GRN has been rejected."));
    }

    [HttpPost("{uuid:guid}/approve")]
    public async Task<IActionResult> ApproveGrn(Guid uuid, [FromBody] ApproveGrnRequest req)
    {
        await _service.ApproveAsync(uuid, User.GetUserId(), req.Remarks);
        return Ok(ApiResponse.Ok("GRN approved."));
    }

    [HttpPost("{uuid:guid}/reject")]
    public async Task<IActionResult> RejectGrn(Guid uuid, [FromBody] RejectGrnRequest req)
    {
        await _service.RejectAsync(uuid, User.GetUserId(), req.RejectionReason);
        return Ok(ApiResponse.Ok("GRN rejected."));
    }
}

public record ApproveGrnRequest(string? Remarks = null);
public record RejectGrnRequest(string RejectionReason);

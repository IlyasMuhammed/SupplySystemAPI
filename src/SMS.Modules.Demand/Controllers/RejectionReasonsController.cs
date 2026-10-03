using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Demand.Controllers;

/// <summary>
/// A32 PA-04 — the organization's rejection reasons (§7, §9.5; docs/sales-preorder/API-CONTRACT.md §6). Reading is
/// for the inquiry and quotation screens' dropdowns — anybody who works either document (the spec's "authenticated";
/// a bare sign-in is not allowed here); every change is SALE_REJECTION_REASON_MANAGE.
/// </summary>
[ApiController]
[Route("api/rejection-reasons")]
[RequiresFeature("MODULE_DEMAND")]
public class RejectionReasonsController : ControllerBase
{
    private readonly IRejectionReasonService _service;

    public RejectionReasonsController(IRejectionReasonService service) => _service = service;

    /// <summary>Active reasons by display order (BR-C5-03); <paramref name="includeInactive"/> for the admin screen.</summary>
    [HttpGet]
    [RequirePermission(PermissionCodes.SALE_INQUIRY_VIEW, PermissionCodes.SALE_INQUIRY_EDIT,
        PermissionCodes.SALE_QUOTATION_VIEW, PermissionCodes.SALE_QUOTATION_EDIT, PermissionCodes.SALE_REJECTION_REASON_MANAGE)]
    public async Task<IActionResult> GetList([FromQuery] bool includeInactive = false) =>
        Ok(ApiResponse<IReadOnlyList<RejectionReasonModel>>.Ok(await _service.GetListAsync(includeInactive)));

    [HttpPost]
    [RequirePermission(PermissionCodes.SALE_REJECTION_REASON_MANAGE)]
    public async Task<IActionResult> Create([FromBody] CreateRejectionReasonRequest req) =>
        Ok(ApiResponse<RejectionReasonModel>.Ok(await _service.CreateAsync(req, User.GetUserId()), StaticResponseMessage.recordCreatedSuccessfully));

    [HttpPut("{uuid:guid}")]
    [RequirePermission(PermissionCodes.SALE_REJECTION_REASON_MANAGE)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] UpdateRejectionReasonRequest req) =>
        Result(await _service.UpdateAsync(uuid, req, User.GetUserId()), StaticResponseMessage.recordUpdatedSuccessfully);

    /// <summary>Soft delete (BR-C5-02/03): out of the dropdowns, kept on the lines that already use it.</summary>
    [HttpPatch("{uuid:guid}/deactivate")]
    [RequirePermission(PermissionCodes.SALE_REJECTION_REASON_MANAGE)]
    public async Task<IActionResult> Deactivate(Guid uuid) =>
        Result(await _service.SetActiveAsync(uuid, false, User.GetUserId()), "Rejection reason deactivated.");

    [HttpPatch("{uuid:guid}/activate")]
    [RequirePermission(PermissionCodes.SALE_REJECTION_REASON_MANAGE)]
    public async Task<IActionResult> Activate(Guid uuid) =>
        Result(await _service.SetActiveAsync(uuid, true, User.GetUserId()), "Rejection reason activated.");

    /// <summary>A custom reason nothing uses; a seeded or used one is a 409 (deactivate it instead).</summary>
    [HttpDelete("{uuid:guid}")]
    [RequirePermission(PermissionCodes.SALE_REJECTION_REASON_MANAGE)]
    public async Task<IActionResult> Delete(Guid uuid) =>
        await _service.DeleteAsync(uuid, User.GetUserId())
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordDeletedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));

    private IActionResult Result(RejectionReasonModel? model, string message) =>
        model is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<RejectionReasonModel>.Ok(model, message));
}

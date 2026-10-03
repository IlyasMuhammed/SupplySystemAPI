using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.Controllers;

[ApiController]
[Route("api/debit-notes")]
[RequiresFeature("MODULE_FINANCE")]
public class DebitNotesController : ControllerBase
{
    private readonly IDebitNoteService _service;

    public DebitNotesController(IDebitNoteService service) => _service = service;

    // As CreditNotesController: a debit note cuts what is owed on a supplier invoice — INVOICE_PROCESS to change
    // one, INVOICE_VIEW to read one — and raising one also admits GOODS_RECEIVE and WAREHOUSE_TRANSFER, because
    // the SRO detail page (guarded by exactly those) resolves a supplier return with it.

    [HttpPost]
    [RequirePermission(PermissionCodes.INVOICE_PROCESS, PermissionCodes.GOODS_RECEIVE, PermissionCodes.WAREHOUSE_TRANSFER)]
    public async Task<IActionResult> CreateDebitNote([FromBody] CreateDebitNoteRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.DebitReason))
            return BadRequest(ApiResponse.Fail("DebitReason is required."));
        if (req.DebitAmount <= 0)
            return BadRequest(ApiResponse.Fail("DebitAmount must be greater than zero."));

        var uuid = await _service.CreateAsync(req, User.GetUserId());
        return Ok(ApiResponse<Guid>.Ok(uuid, "Debit note created and SRO resolved."));
    }

    [HttpGet]
    [RequirePermission(PermissionCodes.INVOICE_VIEW)]
    public async Task<IActionResult> GetDebitNotes([FromQuery] DebitNoteListFilter filter)
    {
        var result = await _service.GetListAsync(filter);
        return Ok(ApiResponse<PaginatedResponse<DebitNoteListItemModel>>.Ok(result));
    }

    [HttpGet("{uuid:guid}")]
    [RequirePermission(PermissionCodes.INVOICE_VIEW)]
    public async Task<IActionResult> GetDebitNoteById(Guid uuid)
    {
        var detail = await _service.GetByIdAsync(uuid);
        return detail is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<DebitNoteDetailModel>.Ok(detail));
    }

    [HttpPatch("{uuid:guid}/status")]
    [RequirePermission(PermissionCodes.INVOICE_PROCESS)]
    public async Task<IActionResult> UpdateStatus(Guid uuid, [FromBody] UpdateDebitNoteStatusRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.NewStatus))
            return BadRequest(ApiResponse.Fail("NewStatus is required."));

        await _service.UpdateStatusAsync(uuid, req, User.GetUserId());
        return Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully));
    }

    [HttpPost("{uuid:guid}/apply")]
    [RequirePermission(PermissionCodes.INVOICE_PROCESS)]
    public async Task<IActionResult> ApplyCarriedForward(Guid uuid, [FromBody] ApplyDebitNoteRequest req)
    {
        await _service.ApplyCarriedForwardAsync(uuid, req, User.GetUserId());
        return Ok(ApiResponse.Ok("Debit note applied to invoice."));
    }
}

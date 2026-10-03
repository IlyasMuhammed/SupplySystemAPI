using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.Controllers;

[ApiController]
[Route("api/credit-notes")]
[RequiresFeature("MODULE_FINANCE")]
public class CreditNotesController : ControllerBase
{
    private readonly ICreditNoteService _service;

    public CreditNotesController(ICreditNoteService service) => _service = service;

    // A credit note reduces what a supplier invoice asks for, so changing one is INVOICE_PROCESS and reading one
    // INVOICE_VIEW. Raising one also admits GOODS_RECEIVE and WAREHOUSE_TRANSFER: the SRO detail page
    // (warehouse/sro/:uuid, guarded by exactly those two) resolves a supplier return by raising a credit note and
    // offers the button on the return's status alone — refusing them here would break that page.

    [HttpPost]
    [RequirePermission(PermissionCodes.INVOICE_PROCESS, PermissionCodes.GOODS_RECEIVE, PermissionCodes.WAREHOUSE_TRANSFER)]
    public async Task<IActionResult> CreateCreditNote([FromBody] CreateCreditNoteRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.SupplierCreditNoteNo))
            return BadRequest(ApiResponse.Fail("SupplierCreditNoteNo is required."));

        var uuid = await _service.CreateAsync(req, User.GetUserId());
        return Ok(ApiResponse<Guid>.Ok(uuid, "Credit note created and applied."));
    }

    [HttpGet]
    [RequirePermission(PermissionCodes.INVOICE_VIEW)]
    public async Task<IActionResult> GetCreditNotes([FromQuery] CreditNoteListFilter filter)
    {
        var result = await _service.GetListAsync(filter);
        return Ok(ApiResponse<PaginatedResponse<CreditNoteListItemModel>>.Ok(result));
    }

    [HttpGet("{uuid:guid}")]
    [RequirePermission(PermissionCodes.INVOICE_VIEW)]
    public async Task<IActionResult> GetCreditNoteById(Guid uuid)
    {
        var detail = await _service.GetByIdAsync(uuid);
        return detail is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<CreditNoteDetailModel>.Ok(detail));
    }

    [HttpPost("{uuid:guid}/apply")]
    [RequirePermission(PermissionCodes.INVOICE_PROCESS)]
    public async Task<IActionResult> ApplyCarriedForward(Guid uuid, [FromBody] ApplyCreditNoteRequest req)
    {
        await _service.ApplyCarriedForwardAsync(uuid, req, User.GetUserId());
        return Ok(ApiResponse.Ok("Credit note applied to invoice."));
    }
}

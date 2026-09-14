using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Material.Models;
using SMS.Modules.Material.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Material.Controllers;

[ApiController]
[Route("api/material-issue-requests")]
[RequiresFeature("MODULE_MIR")]
public class MaterialIssueRequestsController : ControllerBase
{
    private readonly IMirService _service;
    private readonly IMirDocumentService _documentSvc;

    public MaterialIssueRequestsController(IMirService service, IMirDocumentService documentSvc)
    {
        _service     = service;
        _documentSvc = documentSvc;
    }

    [HttpPost]
    [RequirePermission(PermissionCodes.MATERIAL_MANAGE)]
    public async Task<IActionResult> Create([FromBody] CreateMirRequest req)
    {
        var uuid = await _service.CreateAsync(req, User.GetUserId());
        return Ok(ApiResponse<Guid>.Ok(uuid, "Material Issue Request created successfully."));
    }

    [HttpGet]
    [RequirePermission(PermissionCodes.MATERIAL_VIEW)]
    public async Task<IActionResult> GetList([FromQuery] MirListFilter filter)
    {
        var result = await _service.GetListAsync(filter);
        return Ok(ApiResponse<PaginatedResponse<MirListItemModel>>.Ok(result));
    }

    [HttpGet("{uuid:guid}")]
    [RequirePermission(PermissionCodes.MATERIAL_VIEW)]
    public async Task<IActionResult> GetById(Guid uuid)
    {
        var result = await _service.GetByUuidAsync(uuid)
            ?? throw new NotFoundException("MIR", uuid);
        return Ok(ApiResponse<MirDetailModel>.Ok(result));
    }

    [HttpPatch("{uuid:guid}")]
    [RequirePermission(PermissionCodes.MATERIAL_MANAGE)]
    public async Task<IActionResult> Patch(Guid uuid, [FromBody] PatchMirRequest req)
    {
        await _service.PatchAsync(uuid, req, User.GetUserId());
        return Ok(ApiResponse.Ok("Material Issue Request updated successfully."));
    }

    [HttpGet("{uuid:guid}/pdf")]
    [RequirePermission(PermissionCodes.MATERIAL_VIEW)]
    public async Task<IActionResult> DownloadPdf(Guid uuid)
    {
        var bytes = await _documentSvc.GeneratePdfAsync(uuid);
        return File(bytes, "application/pdf", $"MIR-{uuid}.pdf");
    }

    [HttpDelete("{uuid:guid}")]
    [RequirePermission(PermissionCodes.MATERIAL_MANAGE)]
    public async Task<IActionResult> Delete(Guid uuid)
    {
        await _service.DeleteAsync(uuid, User.GetUserId());
        return Ok(ApiResponse.Ok("Material Issue Request deleted successfully."));
    }

    [HttpPost("{uuid:guid}/issue")]
    [RequirePermission(PermissionCodes.MATERIAL_MANAGE)]
    public async Task<IActionResult> Issue(Guid uuid)
    {
        await _service.IssueAsync(uuid, User.GetUserId());
        return Ok(ApiResponse.Ok("Material Issue Request marked as issued."));
    }

    [HttpPost("{uuid:guid}/cancel")]
    [RequirePermission(PermissionCodes.MATERIAL_MANAGE)]
    public async Task<IActionResult> Cancel(Guid uuid)
    {
        await _service.CancelAsync(uuid, User.GetUserId());
        return Ok(ApiResponse.Ok("Material Issue Request cancelled."));
    }
}

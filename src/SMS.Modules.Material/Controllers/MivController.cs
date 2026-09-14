using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Material.Models;
using SMS.Modules.Material.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Material.Controllers;

[ApiController]
[Route("api/material-issue-vouchers")]
[RequiresFeature("MODULE_MIR")]
public class MivController : ControllerBase
{
    private readonly IMivService _service;
    private readonly IMivDocumentService _documentSvc;

    public MivController(IMivService service, IMivDocumentService documentSvc)
    {
        _service     = service;
        _documentSvc = documentSvc;
    }

    /// <summary>Returns the approved lines of a MIR that still have pending qty to issue.</summary>
    [HttpGet("mir-issuable/{mirUuid:guid}")]
    [RequirePermission(PermissionCodes.MATERIAL_VIEW)]
    public async Task<IActionResult> GetIssuable(Guid mirUuid)
    {
        var result = await _service.GetIssuableAsync(mirUuid);
        return Ok(ApiResponse<MirIssuableResponse>.Ok(result));
    }

    /// <summary>Creates a new DRAFT Material Issue Voucher.</summary>
    [HttpPost]
    [RequirePermission(PermissionCodes.MATERIAL_MANAGE)]
    public async Task<IActionResult> Create([FromBody] CreateMivRequest req)
    {
        var uuid = await _service.CreateAsync(req, User.GetUserId());
        return Ok(ApiResponse<Guid>.Ok(uuid, "Material Issue Voucher created successfully."));
    }

    /// <summary>Returns all MIVs (paginated).</summary>
    [HttpGet]
    [RequirePermission(PermissionCodes.MATERIAL_VIEW)]
    public async Task<IActionResult> GetList([FromQuery] MivListFilter filter)
    {
        var result = await _service.GetListAsync(filter);
        return Ok(ApiResponse<PaginatedResponse<MivListItemModel>>.Ok(result));
    }

    /// <summary>Returns a single MIV with all lines.</summary>
    [HttpGet("{uuid:guid}")]
    [RequirePermission(PermissionCodes.MATERIAL_VIEW)]
    public async Task<IActionResult> GetById(Guid uuid)
    {
        var result = await _service.GetByUuidAsync(uuid)
            ?? throw new NotFoundException("MIV", uuid);
        return Ok(ApiResponse<MivDetailModel>.Ok(result));
    }

    /// <summary>Downloads the MIV as a PDF document.</summary>
    [HttpGet("{uuid:guid}/pdf")]
    [RequirePermission(PermissionCodes.MATERIAL_VIEW)]
    public async Task<IActionResult> DownloadPdf(Guid uuid)
    {
        var bytes = await _documentSvc.GeneratePdfAsync(uuid);
        return File(bytes, "application/pdf", $"MIV-{uuid}.pdf");
    }

    /// <summary>Posts a DRAFT MIV — fires all 7 atomic effects.</summary>
    [HttpPost("{uuid:guid}/post")]
    [RequirePermission(PermissionCodes.MATERIAL_MANAGE)]
    public async Task<IActionResult> Post(Guid uuid)
    {
        await _service.PostAsync(uuid, User.GetUserId());
        return Ok(ApiResponse.Ok("Material Issue Voucher posted successfully. Stock levels updated."));
    }

    /// <summary>Cancels a DRAFT MIV (no stock effect).</summary>
    [HttpPost("{uuid:guid}/cancel")]
    [RequirePermission(PermissionCodes.MATERIAL_MANAGE)]
    public async Task<IActionResult> Cancel(Guid uuid)
    {
        await _service.CancelAsync(uuid, User.GetUserId());
        return Ok(ApiResponse.Ok("Material Issue Voucher cancelled."));
    }
}

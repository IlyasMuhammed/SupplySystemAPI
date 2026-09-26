using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Material.Models;
using SMS.Modules.Material.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Material.Controllers;

/// <summary>A30 §28.2 — bills of materials.</summary>
[ApiController]
[Route("api/boms")]
[RequiresFeature("MODULE_MANUFACTURING")]
public class BomsController : ControllerBase
{
    private readonly IBomService     _service;
    private readonly IBomCostService _cost;

    public BomsController(IBomService service, IBomCostService cost)
    {
        _service = service;
        _cost    = cost;
    }

    [HttpPost]
    [RequirePermission(PermissionCodes.BOM_CREATE)]
    public async Task<IActionResult> Create([FromBody] CreateBomRequest req)
    {
        var uuid = await _service.CreateAsync(req, User.GetUserId());
        return Ok(ApiResponse<Guid>.Ok(uuid, "Bill of materials created."));
    }

    [HttpGet]
    [RequirePermission(PermissionCodes.BOM_VIEW)]
    public async Task<IActionResult> GetList([FromQuery] BomListFilter filter)
    {
        var result = await _service.GetListAsync(filter);
        return Ok(ApiResponse<PaginatedResponse<BomListItemModel>>.Ok(result));
    }

    [HttpGet("{uuid:guid}")]
    [RequirePermission(PermissionCodes.BOM_VIEW)]
    public async Task<IActionResult> GetById(Guid uuid)
    {
        var bom = await _service.GetByUuidAsync(uuid) ?? throw new NotFoundException("BOM", uuid);
        return Ok(ApiResponse<BomDetailModel>.Ok(bom));
    }

    /// <summary>Every version of a product's recipe, newest first.</summary>
    [HttpGet("/api/products/{productUuid:guid}/boms")]
    [RequirePermission(PermissionCodes.BOM_VIEW)]
    public async Task<IActionResult> GetVersions(Guid productUuid, [FromQuery] Guid? variantUuid)
    {
        var versions = await _service.GetVersionsAsync(productUuid, variantUuid);
        return Ok(ApiResponse<IReadOnlyList<BomVersionModel>>.Ok(versions));
    }

    [HttpPut("{uuid:guid}")]
    [RequirePermission(PermissionCodes.BOM_EDIT)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] UpdateBomRequest req)
    {
        await _service.UpdateAsync(uuid, req, User.GetUserId());
        return Ok(ApiResponse.Ok("Bill of materials updated."));
    }

    [HttpDelete("{uuid:guid}")]
    [RequirePermission(PermissionCodes.BOM_EDIT)]
    public async Task<IActionResult> Delete(Guid uuid)
    {
        await _service.DeleteAsync(uuid, User.GetUserId());
        return Ok(ApiResponse.Ok("Bill of materials deleted."));
    }

    [HttpPost("{uuid:guid}/submit")]
    [RequirePermission(PermissionCodes.BOM_SUBMIT)]
    public async Task<IActionResult> Submit(Guid uuid)
    {
        await _service.SubmitAsync(uuid, User.GetUserId());
        return Ok(ApiResponse.Ok("Bill of materials submitted for approval."));
    }

    [HttpPost("{uuid:guid}/approve")]
    [RequirePermission(PermissionCodes.BOM_APPROVE)]
    public async Task<IActionResult> Approve(Guid uuid)
    {
        await _service.ApproveAsync(uuid, User.GetUserId());
        return Ok(ApiResponse.Ok("Bill of materials approved."));
    }

    [HttpPost("{uuid:guid}/reject")]
    [RequirePermission(PermissionCodes.BOM_APPROVE)]
    public async Task<IActionResult> Reject(Guid uuid, [FromBody] RejectBomRequest req)
    {
        await _service.RejectAsync(uuid, User.GetUserId(), req.Reason);
        return Ok(ApiResponse.Ok("Bill of materials rejected."));
    }

    [HttpPost("{uuid:guid}/activate")]
    [RequirePermission(PermissionCodes.BOM_ACTIVATE)]
    public async Task<IActionResult> Activate(Guid uuid)
    {
        await _service.ActivateAsync(uuid, User.GetUserId());
        return Ok(ApiResponse.Ok("Bill of materials activated."));
    }

    [HttpPost("{uuid:guid}/obsolete")]
    [RequirePermission(PermissionCodes.BOM_OBSOLETE)]
    public async Task<IActionResult> Obsolete(Guid uuid, [FromBody] ObsoleteBomRequest? req)
    {
        await _service.ObsoleteAsync(uuid, User.GetUserId(), req?.Reason);
        return Ok(ApiResponse.Ok("Bill of materials made obsolete."));
    }

    [HttpPost("{uuid:guid}/new-version")]
    [RequirePermission(PermissionCodes.BOM_CREATE)]
    public async Task<IActionResult> NewVersion(Guid uuid)
    {
        var created = await _service.NewVersionAsync(uuid, User.GetUserId());
        return Ok(ApiResponse<Guid>.Ok(created, "New version drafted."));
    }

    [HttpGet("{uuid:guid}/compare/{otherUuid:guid}")]
    [RequirePermission(PermissionCodes.BOM_VIEW)]
    public async Task<IActionResult> Compare(Guid uuid, Guid otherUuid)
    {
        var comparison = await _service.CompareAsync(uuid, otherUuid);
        return Ok(ApiResponse<BomComparisonModel>.Ok(comparison));
    }

    [HttpGet("{uuid:guid}/cost")]
    [RequirePermission(PermissionCodes.BOM_VIEW)]
    public async Task<IActionResult> Cost(Guid uuid)
    {
        var cost = await _cost.CalculateAsync(uuid);
        return Ok(ApiResponse<BomCostModel>.Ok(cost));
    }
}

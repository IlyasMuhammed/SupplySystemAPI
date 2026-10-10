using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Material.Models;
using SMS.Modules.Material.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Material.Controllers;

/// <summary>
/// A30 §28.5 — quality inspection. Creating one records the decision in the same call (this
/// implementation has no separate draft-then-decide step, since nothing here needs one — see
/// <see cref="QualityInspectionService"/>'s own note), so the write is gated QI_APPROVE, the
/// permission for the decision itself; QI_CREATE, per the spec's own table, gates reading one back.
/// A37 §1.3 — and FEATURE_QUALITY_INSPECTION (every [RequiresFeature] must pass).
/// </summary>
[ApiController]
[Route("api")]
[RequiresFeature("MODULE_MANUFACTURING")]
[RequiresFeature(ModuleCodes.QualityInspection)]
public class QualityInspectionsController : ControllerBase
{
    private readonly IQualityInspectionService _service;

    public QualityInspectionsController(IQualityInspectionService service) => _service = service;

    [HttpPost("production-orders/{uuid:guid}/quality-inspections")]
    [RequirePermission(PermissionCodes.QI_APPROVE)]
    public async Task<IActionResult> Create(Guid uuid, [FromBody] CreateQualityInspectionRequest req)
    {
        var inspectionUuid = await _service.CreateAsync(uuid, req, User.GetUserId());
        return Ok(ApiResponse<Guid>.Ok(inspectionUuid, "Quality inspection recorded."));
    }

    [HttpGet("quality-inspections/{uuid:guid}")]
    [RequirePermission(PermissionCodes.QI_CREATE)]
    public async Task<IActionResult> GetById(Guid uuid)
    {
        var qi = await _service.GetAsync(uuid) ?? throw new NotFoundException("Quality inspection", uuid);
        return Ok(ApiResponse<QualityInspectionModel>.Ok(qi));
    }

    [HttpGet("production-orders/{uuid:guid}/quality-inspection")]
    [RequirePermission(PermissionCodes.QI_CREATE)]
    public async Task<IActionResult> GetForOrder(Guid uuid)
    {
        var qi = await _service.GetForOrderAsync(uuid);
        return Ok(ApiResponse<QualityInspectionModel?>.Ok(qi));
    }
}

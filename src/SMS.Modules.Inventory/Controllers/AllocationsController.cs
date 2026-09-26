using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Inventory.Models;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Constants;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Inventory.Controllers;

/// <summary>A30 §28.8 — the allocation engine over HTTP.</summary>
[ApiController]
[Authorize]
[RequiresFeature("MODULE_INVENTORY")]
public class AllocationsController : ControllerBase
{
    private readonly IAllocationEngine _engine;

    public AllocationsController(IAllocationEngine engine) => _engine = engine;

    // ── Allocations ───────────────────────────────────────────────────────────

    [HttpGet("api/allocations")]
    [RequirePermission(PermissionCodes.ALLOCATION_VIEW)]
    public async Task<IActionResult> GetAllocations(
        [FromQuery] Guid? variantUuid, [FromQuery] Guid? warehouseUuid, [FromQuery] string? demandType,
        [FromQuery] Guid? demandUuid, [FromQuery] string? status,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var result = await _engine.GetAllocationsAsync(
            new AllocationListFilter(variantUuid, warehouseUuid, demandType, demandUuid, status, page, pageSize));
        return Ok(ApiResponse<AllocationPage>.Ok(result));
    }

    [HttpGet("api/allocations/availability")]
    [RequirePermission(PermissionCodes.ALLOCATION_VIEW)]
    public async Task<IActionResult> GetAvailability([FromQuery] Guid variantUuid, [FromQuery] Guid? warehouseUuid)
    {
        if (variantUuid == Guid.Empty) throw new BadRequestException("variantUuid is required.");
        return Ok(ApiResponse<AvailabilityResult>.Ok(await _engine.GetAvailabilityAsync(variantUuid, warehouseUuid)));
    }

    /// <summary>The spec's GET /api/products/{id}/availability, at the level stock is actually held.</summary>
    [HttpGet("api/variants/{variantUuid:guid}/availability")]
    [RequirePermission(PermissionCodes.ALLOCATION_VIEW)]
    public async Task<IActionResult> GetVariantAvailability(Guid variantUuid, [FromQuery] Guid? warehouseUuid) =>
        Ok(ApiResponse<AvailabilityResult>.Ok(await _engine.GetAvailabilityAsync(variantUuid, warehouseUuid)));

    [HttpGet("api/allocations/rules")]
    [RequirePermission(PermissionCodes.ALLOCATION_VIEW)]
    public async Task<IActionResult> GetRules() =>
        Ok(ApiResponse<IReadOnlyList<AllocationRuleDefinition>>.Ok(await _engine.GetRulesAsync()));

    [HttpPut("api/allocations/rules")]
    [RequirePermission(PermissionCodes.ALLOCATION_ADMIN)]
    public async Task<IActionResult> SetRules([FromBody] SetAllocationRulesRequest req) =>
        Ok(ApiResponse<IReadOnlyList<AllocationRuleDefinition>>.Ok(await _engine.SetRulesAsync(req.Rules)));

    [HttpGet("api/allocations/{uuid:guid}")]
    [RequirePermission(PermissionCodes.ALLOCATION_VIEW)]
    public async Task<IActionResult> GetAllocation(Guid uuid)
    {
        var allocation = await _engine.GetAllocationAsync(uuid);
        if (allocation is null) return NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
        return Ok(ApiResponse<AllocationSummary>.Ok(allocation));
    }

    [HttpPost("api/allocations/run")]
    [RequirePermission(PermissionCodes.ALLOCATION_RUN)]
    public async Task<IActionResult> Run([FromBody] RunAllocationRequest req)
    {
        if (req.VariantUuid == Guid.Empty) throw new BadRequestException("variantUuid is required.");
        var result = await _engine.AllocateAsync(req.VariantUuid, req.WarehouseUuid, User.GetUserId());
        return Ok(ApiResponse<AllocationRunResult>.Ok(result));
    }

    // Releasing and moving decide who gets scarce stock (§14.5: firm and reserved allocations
    // change hands only with ALLOCATION_ADMIN).
    [HttpPost("api/allocations/{uuid:guid}/release")]
    [RequirePermission(PermissionCodes.ALLOCATION_ADMIN)]
    public async Task<IActionResult> Release(Guid uuid, [FromBody] AllocationReasonRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Reason)) throw new BadRequestException("A reason is required.");
        await _engine.ReleaseAsync(uuid, req.Reason.Trim(), User.GetUserId());
        return Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully));
    }

    [HttpPost("api/allocations/{uuid:guid}/reallocate")]
    [RequirePermission(PermissionCodes.ALLOCATION_ADMIN)]
    public async Task<IActionResult> Reallocate(Guid uuid, [FromBody] ReallocateRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Reason)) throw new BadRequestException("A reason is required.");
        var moved = await _engine.ReallocateAsync(uuid, req.ToDemandUuid, req.Quantity, req.Reason.Trim(), User.GetUserId());
        return Ok(ApiResponse<AllocationSummary>.Ok(moved));
    }

    // ── Demands ───────────────────────────────────────────────────────────────

    [HttpGet("api/allocations/demands")]
    [RequirePermission(PermissionCodes.ALLOCATION_VIEW)]
    public async Task<IActionResult> GetDemands(
        [FromQuery] Guid? variantUuid, [FromQuery] string? demandType, [FromQuery] Guid? demandUuid,
        [FromQuery] bool openOnly = true)
    {
        var demands = await _engine.GetDemandsAsync(variantUuid, demandType, demandUuid, openOnly);
        return Ok(ApiResponse<IReadOnlyList<DemandAllocationSummary>>.Ok(demands));
    }

    [HttpGet("api/allocations/demands/{uuid:guid}")]
    [RequirePermission(PermissionCodes.ALLOCATION_VIEW)]
    public async Task<IActionResult> GetDemand(Guid uuid)
    {
        var demand = await _engine.GetDemandAsync(uuid);
        if (demand is null) return NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
        return Ok(ApiResponse<DemandAllocationSummary>.Ok(demand));
    }

    /// <summary>Registers a demand by hand and, unless told not to, runs the engine for its variant.</summary>
    [HttpPost("api/allocations/demands")]
    [RequirePermission(PermissionCodes.ALLOCATION_RUN)]
    public async Task<IActionResult> RegisterDemand([FromBody] RegisterDemandRequest req)
    {
        var userId  = User.GetUserId();
        var summary = await _engine.RegisterDemandAsync(req.ToRegistration(), userId);

        if (req.Allocate)
        {
            await _engine.AllocateForDemandAsync(summary.Uuid, userId);
            summary = (await _engine.GetDemandAsync(summary.Uuid))!;
        }

        return Ok(ApiResponse<DemandAllocationSummary>.Ok(summary, StaticResponseMessage.recordCreatedSuccessfully));
    }

    [HttpPost("api/allocations/demands/{uuid:guid}/cancel")]
    [RequirePermission(PermissionCodes.ALLOCATION_ADMIN)]
    public async Task<IActionResult> CancelDemand(Guid uuid, [FromBody] AllocationReasonRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Reason)) throw new BadRequestException("A reason is required.");
        await _engine.CancelDemandAsync(uuid, req.Reason.Trim(), User.GetUserId());
        return Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully));
    }

    // ── Expected supply ───────────────────────────────────────────────────────

    [HttpPost("api/allocations/supplies")]
    [RequirePermission(PermissionCodes.ALLOCATION_RUN)]
    public async Task<IActionResult> RegisterSupply([FromBody] RegisterSupplyRequest req)
    {
        var uuid = await _engine.RegisterSupplyAsync(req.ToRegistration());
        if (req.Allocate) await _engine.AllocateAsync(req.VariantUuid, req.WarehouseUuid, User.GetUserId());
        return Ok(ApiResponse<object>.Ok(new { uuid }, StaticResponseMessage.recordCreatedSuccessfully));
    }
}

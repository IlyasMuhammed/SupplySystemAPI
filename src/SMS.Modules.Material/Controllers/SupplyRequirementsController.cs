using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Material.Models;
using SMS.Modules.Material.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Material.Controllers;

/// <summary>A30 §28.7 — supply requirements: what a shortage needs, and what was raised for it.</summary>
[ApiController]
[Route("api/supply-requirements")]
[RequiresFeature("MODULE_MANUFACTURING")]
public class SupplyRequirementsController : ControllerBase
{
    private readonly IProductionOrderService _orders;

    public SupplyRequirementsController(IProductionOrderService orders) => _orders = orders;

    [HttpGet]
    [RequirePermission(PermissionCodes.SUPPLY_VIEW)]
    public async Task<IActionResult> GetList([FromQuery] SupplyRequirementListFilter filter) =>
        Ok(ApiResponse<PaginatedResponse<SupplyRequirementModel>>.Ok(await _orders.GetSupplyRequirementsAsync(filter)));

    [HttpGet("{uuid:guid}")]
    [RequirePermission(PermissionCodes.SUPPLY_VIEW)]
    public async Task<IActionResult> GetById(Guid uuid)
    {
        var sr = await _orders.GetSupplyRequirementAsync(uuid) ?? throw new NotFoundException("Supply requirement", uuid);
        return Ok(ApiResponse<SupplyRequirementModel>.Ok(sr));
    }

    [HttpPost]
    [RequirePermission(PermissionCodes.SUPPLY_CREATE)]
    public async Task<IActionResult> Create([FromBody] CreateSupplyRequirementRequest req)
    {
        var uuid = await _orders.CreateSupplyRequirementAsync(req, User.GetUserId());
        return Ok(ApiResponse<Guid>.Ok(uuid, "Supply requirement raised."));
    }

    [HttpPost("{uuid:guid}/cancel")]
    [RequirePermission(PermissionCodes.SUPPLY_CANCEL)]
    public async Task<IActionResult> Cancel(Guid uuid, [FromBody] CancelSupplyRequirementRequest req)
    {
        await _orders.CancelSupplyRequirementAsync(uuid, req, User.GetUserId());
        return Ok(ApiResponse.Ok("Supply requirement cancelled."));
    }
}

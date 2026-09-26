using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Material.Models;
using SMS.Modules.Material.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Material.Controllers;

/// <summary>A30 §28.4 — one production material issue: reading it, confirming it, reversing it.</summary>
[ApiController]
[Route("api/production-issues")]
[RequiresFeature("MODULE_MANUFACTURING")]
public class ProductionIssuesController : ControllerBase
{
    private readonly IProductionMaterialIssueService _issues;

    public ProductionIssuesController(IProductionMaterialIssueService issues) => _issues = issues;

    [HttpGet("{uuid:guid}")]
    [RequirePermission(PermissionCodes.PROD_VIEW)]
    public async Task<IActionResult> GetById(Guid uuid)
    {
        var issue = await _issues.GetAsync(uuid) ?? throw new NotFoundException("Production material issue", uuid);
        return Ok(ApiResponse<ProductionIssueModel>.Ok(issue));
    }

    [HttpPost("{uuid:guid}/confirm")]
    [RequirePermission(PermissionCodes.MI_CONFIRM)]
    public async Task<IActionResult> Confirm(Guid uuid)
    {
        await _issues.ConfirmAsync(uuid, User.GetUserId());
        return Ok(ApiResponse.Ok("Material issue confirmed."));
    }

    [HttpPost("{uuid:guid}/reverse")]
    [RequirePermission(PermissionCodes.MI_REVERSE)]
    public async Task<IActionResult> Reverse(Guid uuid, [FromBody] ReverseProductionIssueRequest req)
    {
        await _issues.ReverseAsync(uuid, req.Reason, User.GetUserId());
        return Ok(ApiResponse.Ok("Material issue reversed."));
    }
}

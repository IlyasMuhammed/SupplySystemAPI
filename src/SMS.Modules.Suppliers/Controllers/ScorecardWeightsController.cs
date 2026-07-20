using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Suppliers.Models;
using SMS.Modules.Suppliers.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Pagination;

namespace SMS.Modules.Suppliers.Controllers;

[ApiController]
[Route("api/admin/scorecard-weights")]
public class ScorecardWeightsController : ControllerBase
{
    private readonly IScorecardService _svc;
    public ScorecardWeightsController(IScorecardService svc) => _svc = svc;

    [HttpGet]
    [RequirePermission(PermissionCodes.SUPPLIER_VIEW)]
    public async Task<IActionResult> GetWeights()
    {
        var result = await _svc.GetWeightsAsync();
        return Ok(ApiResponse<List<ScorecardDimensionWeightModel>>.Ok(result));
    }

    // Admin only — SYSTEM_CONFIGURE is only granted to the System Admin role (see AuthDataSeeder).
    [HttpPut]
    [RequirePermission(PermissionCodes.SYSTEM_CONFIGURE)]
    public async Task<IActionResult> UpdateWeights([FromBody] UpdateScorecardWeightsRequest req)
    {
        await _svc.UpdateWeightsAsync(req.Weights, User.GetUserId());
        return Ok(ApiResponse.Ok("Scorecard dimension weights updated."));
    }
}

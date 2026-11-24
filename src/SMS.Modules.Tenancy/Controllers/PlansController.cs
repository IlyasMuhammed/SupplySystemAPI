using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Tenancy.Models;
using SMS.Modules.Tenancy.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Tenancy.Controllers;

/// <summary>A51 P2 — the plans master for the Super Admin: the plan cards, one plan with every feature, its fields (price is display
/// only) and its included features. Platform super admin only; plan changes take effect on the next read.</summary>
[ApiController]
[Route("api/system/plans")]
[RequirePermission(PermissionCodes.PLATFORM_SUPER_ADMIN)]
[RequireSuperAdmin]
public class PlansController : ControllerBase
{
    private readonly IPlanService _plans;

    public PlansController(IPlanService plans) => _plans = plans;

    private int? UserId => User.GetUserId() is var id && id > 0 ? id : null;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(ApiResponse<List<PlanModel>>.Ok(await _plans.ListPlansAsync(ct)));

    [HttpGet("{code}")]
    public async Task<IActionResult> Get(string code, CancellationToken ct)
    {
        var plan = await _plans.GetPlanWithFeaturesAsync(code, ct);
        return plan is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<PlanWithFeaturesModel>.Ok(plan));
    }

    [HttpPut("{code}")]
    public async Task<IActionResult> Update(string code, [FromBody] UpdatePlanRequest req, CancellationToken ct) =>
        Ok(ApiResponse<PlanModel>.Ok(await _plans.UpdatePlanAsync(code, req ?? new(), UserId, ct), StaticResponseMessage.recordUpdatedSuccessfully));

    /// <summary>A51 P7 — pushes the plan's features to the organizations on it (addOnly default; full = Reset to Plan Defaults; dryRun previews).</summary>
    [HttpPost("{code}/apply-to-organizations")]
    public async Task<IActionResult> ApplyToOrganizations(string code, [FromBody] ApplyPlanToOrganizationsRequest? req, CancellationToken ct) =>
        Ok(ApiResponse<ApplyPlanToOrganizationsResult>.Ok(
            await _plans.ApplyToOrganizationsAsync(code, req ?? new(), UserId, ct),
            req?.DryRun == true ? "Preview only; nothing was changed." : "The plan was applied to its organizations."));

    [HttpPut("{code}/features")]
    public async Task<IActionResult> UpdateFeatures(string code, [FromBody] UpdatePlanFeaturesRequest req, CancellationToken ct) =>
        Ok(ApiResponse<PlanWithFeaturesModel>.Ok(
            await _plans.UpdatePlanFeaturesAsync(code, req?.Features ?? new(), UserId, ct), StaticResponseMessage.recordUpdatedSuccessfully));
}

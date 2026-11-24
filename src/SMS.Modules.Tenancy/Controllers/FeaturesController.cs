using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Tenancy.Models;
using SMS.Modules.Tenancy.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Tenancy.Controllers;

// Catalog-only — per-org toggle state and updates now live on OrganizationsController, matching
// the ticket's nested route (.../organizations/{id}/features).
// A51 P2 adds the instant Status / IsVisible switches (PUT {code}/status, PUT {code}/visibility).
[ApiController]
[Route("api/system/features")]
[RequirePermission(PermissionCodes.PLATFORM_SUPER_ADMIN)]
[RequireSuperAdmin] // the code alone is carried by roles any organization can hold — see the attribute
public class FeaturesController : ControllerBase
{
    private readonly ITenancyService _svc;
    private readonly IFeatureAdminService? _admin;

    public FeaturesController(ITenancyService svc, IFeatureAdminService? admin = null)
    {
        _svc = svc;
        _admin = admin;
    }

    [HttpGet]
    public async Task<IActionResult> GetCatalog()
    {
        var catalog = await _svc.GetFeatureCatalogAsync();
        return Ok(ApiResponse<List<FeatureDefinitionModel>>.Ok(catalog));
    }

    [HttpGet("plan-templates")]
    public async Task<IActionResult> GetPlanTemplates()
    {
        var templates = await _svc.GetPlanTemplatesAsync();
        return Ok(ApiResponse<List<PlanFeatureTemplateModel>>.Ok(templates));
    }

    [HttpPut("{code}/status")]
    public async Task<IActionResult> SetStatus(string code, [FromBody] SetFeatureStatusRequest req, CancellationToken ct) =>
        Ok(ApiResponse<FeatureDefinitionModel>.Ok(
            await _admin!.SetStatusAsync(code, req?.Status ?? string.Empty, UserId, ct), StaticResponseMessage.recordUpdatedSuccessfully));

    [HttpPut("{code}/visibility")]
    public async Task<IActionResult> SetVisibility(string code, [FromBody] SetFeatureVisibilityRequest req, CancellationToken ct) =>
        Ok(ApiResponse<FeatureDefinitionModel>.Ok(
            await _admin!.SetVisibilityAsync(code, req?.IsVisible ?? true, UserId, ct), StaticResponseMessage.recordUpdatedSuccessfully));

    private int? UserId => User.GetUserId() is var id && id > 0 ? id : null;
}

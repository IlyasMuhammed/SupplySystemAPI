using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Tenancy.Models;
using SMS.Modules.Tenancy.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Pagination;

namespace SMS.Modules.Tenancy.Controllers;

// Catalog-only — per-org toggle state and updates now live on OrganizationsController, matching
// the ticket's nested route (.../organizations/{id}/features).
[ApiController]
[Route("api/system/features")]
[RequirePermission(PermissionCodes.PLATFORM_SUPER_ADMIN)]
public class FeaturesController : ControllerBase
{
    private readonly ITenancyService _svc;

    public FeaturesController(ITenancyService svc) => _svc = svc;

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
}

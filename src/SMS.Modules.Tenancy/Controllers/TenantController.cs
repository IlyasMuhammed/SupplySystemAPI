using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Tenancy.Models;
using SMS.Modules.Tenancy.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Tenancy.Controllers;

// MT-004: consumed by the React sidebar on every page load to render/hide menu items — the current
// org's enabled features, plus the caller's own role and permissions (already on the JWT, so this
// is a single Organization lookup, not a bigger permission round trip).
[ApiController]
[Route("api/tenant")]
public class TenantController : ControllerBase
{
    private readonly ITenancyService _svc;
    private readonly ITenantContext _tenantContext;

    public TenantController(ITenancyService svc, ITenantContext tenantContext)
    {
        _svc = svc;
        _tenantContext = tenantContext;
    }

    [HttpGet("current")]
    public async Task<IActionResult> GetCurrent()
    {
        var org = await _svc.GetOrganizationByIdAsync(_tenantContext.OrganizationId);
        if (org is null)
            return NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));

        var features = await _svc.GetOrganizationFeaturesAsync(_tenantContext.OrganizationId);

        var result = new CurrentTenantModel
        {
            Id                  = org.Id,
            OrgCode             = org.OrgCode,
            OrgName             = org.OrgName,
            Plan                = org.Plan,
            EnabledFeatureCodes = features.Where(f => f.IsEnabled).Select(f => f.FeatureCode).ToList(),
            IsSuperAdmin        = _tenantContext.IsSuperAdmin,
            RoleName            = User.GetRoleName(),
            Permissions         = User.GetPermissions().ToList()
        };

        return Ok(ApiResponse<CurrentTenantModel>.Ok(result));
    }
}

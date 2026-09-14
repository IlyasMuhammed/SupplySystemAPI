using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Tenancy.Models;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Pagination;

namespace SMS.Modules.Tenancy.Controllers;

// REQ-4.x — self-service settings for the caller's own organization. Always resolves the org via
// ITenantContext (JWT), never an id route parameter — this is distinct from OrganizationsController,
// which is the PLATFORM_SUPER_ADMIN surface for managing every tenant.
[ApiController]
[Route("api/organization-settings")]
[RequirePermission(PermissionCodes.SYSTEM_CONFIGURE)]
public class OrganizationSettingsController : ControllerBase
{
    private readonly IOrganizationSettingsService _svc;
    private readonly ITenantContext _tenantContext;

    public OrganizationSettingsController(IOrganizationSettingsService svc, ITenantContext tenantContext)
    {
        _svc = svc;
        _tenantContext = tenantContext;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var days = await _svc.GetAckLinkExpiryDaysAsync(_tenantContext.OrganizationId);
        return Ok(ApiResponse<OrganizationSettingsModel>.Ok(new OrganizationSettingsModel { AckLinkExpiryDays = days }));
    }

    [HttpPut]
    public async Task<IActionResult> Update([FromBody] UpdateOrganizationSettingsRequest req)
    {
        var updated = await _svc.UpdateAckLinkExpiryDaysAsync(_tenantContext.OrganizationId, req.AckLinkExpiryDays, User.GetUserId());
        if (!updated)
            return BadRequest(ApiResponse.Fail(
                $"Acknowledgment link expiry must be between {OrganizationSettingsDefaults.AckLinkExpiryMinDays} and {OrganizationSettingsDefaults.AckLinkExpiryMaxDays} days."));

        return Ok(ApiResponse.Ok("Acknowledgment link expiry updated."));
    }
}

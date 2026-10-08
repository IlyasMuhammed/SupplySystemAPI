using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Tenancy.Models;
using SMS.Modules.Tenancy.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Pagination;

namespace SMS.Modules.Tenancy.Controllers;

/// <summary>
/// A35 P2-04 (spec #10–11, API-CONTRACT.md §4) — the caller's own organization's currency settings. The organization is
/// always the JWT's (ITenantContext), never a route value, super admin included.
/// </summary>
[ApiController]
[Route("api/organization/currency-settings")]
public class OrganizationCurrencySettingsController : ControllerBase
{
    private readonly IOrganizationCurrencySettingsService _svc;
    private readonly ITenantContext _tenant;

    public OrganizationCurrencySettingsController(IOrganizationCurrencySettingsService svc, ITenantContext tenant)
    {
        _svc = svc;
        _tenant = tenant;
    }

    [HttpGet]
    [RequirePermission(PermissionCodes.CURRENCY_VIEW, PermissionCodes.ORG_CURRENCY_SETTINGS_MANAGE)]
    public async Task<IActionResult> Get(CancellationToken ct = default) =>
        Ok(ApiResponse<OrgCurrencySettingsModel>.Ok(await _svc.GetAsync(_tenant.OrganizationId, ct)));

    [HttpPut]
    [RequirePermission(PermissionCodes.ORG_CURRENCY_SETTINGS_MANAGE)]
    public async Task<IActionResult> Update([FromBody] UpdateOrgCurrencySettingsRequest req, CancellationToken ct = default) =>
        Ok(ApiResponse<OrgCurrencySettingsModel>.Ok(
            await _svc.UpdateAsync(_tenant.OrganizationId, req, User.GetUserId(), ct), "Currency settings saved."));
}

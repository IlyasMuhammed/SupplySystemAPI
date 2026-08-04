using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Tenancy.Models;
using SMS.Modules.Tenancy.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Tenancy.Controllers;

[ApiController]
[Route("api/system/organizations")]
[RequirePermission(PermissionCodes.PLATFORM_SUPER_ADMIN)]
public class OrganizationsController : ControllerBase
{
    private readonly ITenancyService _svc;
    private readonly IOrgUserProvisioningService _orgUsers;

    public OrganizationsController(ITenancyService svc, IOrgUserProvisioningService orgUsers)
    {
        _svc = svc;
        _orgUsers = orgUsers;
    }

    [HttpGet]
    public async Task<IActionResult> GetList([FromQuery] OrganizationFilter filter)
    {
        var result = await _svc.GetOrganizationsAsync(filter);
        return Ok(ApiResponse<PaginatedResponse<OrganizationListItemModel>>.Ok(result));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var org = await _svc.GetOrganizationByIdAsync(id);
        return org is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<OrganizationDetailModel>.Ok(org));
    }

    // Creates the organization and its initial Admin user atomically — the admin receives an
    // email invitation to set their own password (see IOrgUserProvisioningService).
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateOrganizationRequest req)
    {
        var result = await _svc.CreateOrganizationWithAdminAsync(req, User.GetUserId());
        return Ok(ApiResponse<CreateOrganizationResult>.Ok(result, StaticResponseMessage.recordCreatedSuccessfully));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateOrganizationRequest req)
    {
        var updated = await _svc.UpdateOrganizationAsync(id, req, User.GetUserId());
        return updated
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    // Reactivation only — deactivation is a distinct, dedicated action (below) since it also
    // invalidates sessions, not something a generic status toggle should silently trigger.
    [HttpPatch("{id:guid}/status")]
    public async Task<IActionResult> PatchStatus(Guid id, [FromBody] PatchOrganizationStatusRequest req)
    {
        var updated = await _svc.PatchStatusAsync(id, req.IsActive, User.GetUserId());
        return updated
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    // Sets IsActive=false and bulk-invalidates every active session for this org's users —
    // combined with the login-time org-active check, this is what makes deactivation immediately
    // block logins, not just flip a flag.
    [HttpPatch("{id:guid}/deactivate")]
    public async Task<IActionResult> Deactivate(Guid id)
    {
        var updated = await _svc.DeactivateOrganizationAsync(id, User.GetUserId());
        return updated
            ? Ok(ApiResponse.Ok("Organization deactivated; all active sessions for its users were terminated."))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    [HttpPatch("{id:guid}/plan")]
    public async Task<IActionResult> PatchPlan(Guid id, [FromBody] PatchOrganizationPlanRequest req)
    {
        var updated = await _svc.PatchPlanAsync(id, req.Plan, User.GetUserId());
        return updated
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    // Explicit, destructive: re-syncs this org's feature toggles to exactly match its current
    // plan's template, overwriting any manual toggles. Frontend gates this behind a confirmation.
    [HttpPost("{id:guid}/apply-plan-template")]
    public async Task<IActionResult> ApplyPlanTemplate(Guid id)
    {
        var updated = await _svc.ApplyPlanTemplateAsync(id, User.GetUserId());
        return updated
            ? Ok(ApiResponse.Ok("Plan template applied."))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    // ── Feature toggles (moved here from FeaturesController — the ticket's route is nested
    // under Organizations, not a separate Features sub-resource) ─────────────────────────

    [HttpGet("{id:guid}/features")]
    public async Task<IActionResult> GetFeatures(Guid id)
    {
        var features = await _svc.GetOrganizationFeaturesAsync(id);
        return Ok(ApiResponse<List<OrganizationFeatureModel>>.Ok(features));
    }

    [HttpPut("{id:guid}/features")]
    public async Task<IActionResult> UpdateFeatures(Guid id, [FromBody] UpdateFeaturesRequest req)
    {
        var result = await _svc.UpdateFeaturesAsync(id, req.Features, User.GetUserId());
        return Ok(ApiResponse<UpdateFeaturesResult>.Ok(result, "Feature configuration updated."));
    }

    // ── Organization admin (view/change who holds the OrgAdmin role in this org) ───────────

    // Returns every active user in the org (with their role) — the caller derives "who's
    // currently admin" by filtering RoleId == OrgAdmin, and offers the rest as candidates to
    // promote. There's no dedicated AdminUserId column to look up directly.
    [HttpGet("{id:guid}/users")]
    public async Task<IActionResult> GetOrgUsers(Guid id)
    {
        var result = await _orgUsers.GetOrgUsersAsync(id);
        return Ok(ApiResponse<List<OrgUserSummary>>.Ok(result, "Organization users retrieved."));
    }

    [HttpPut("{id:guid}/admin")]
    public async Task<IActionResult> UpdateAdmin(Guid id, [FromBody] UpdateOrgAdminRequest req)
    {
        await _orgUsers.ReassignOrgAdminAsync(id, req.NewAdminUserId);
        return Ok(ApiResponse.Ok("Organization admin updated."));
    }
}

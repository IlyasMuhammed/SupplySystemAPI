using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Tenancy.Models;
using SMS.Modules.Tenancy.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Pagination;

namespace SMS.Modules.Tenancy.Controllers;

/// <summary>A37 API-CONTRACT §1.1 — the org admin's Settings › Modules, always for the caller's own organization.</summary>
[ApiController]
[Route("api/tenant/modules")]
public class ModulesController : ControllerBase
{
    private readonly IModuleRegistryService _svc;
    private readonly ITenantContext _tenant;

    public ModulesController(IModuleRegistryService svc, ITenantContext tenant)
    {
        _svc = svc;
        _tenant = tenant;
    }

    [HttpGet]
    [RequirePermission(PermissionCodes.MODULES_VIEW)]
    public async Task<IActionResult> GetModules(CancellationToken ct) =>
        Ok(ApiResponse<List<ModuleCardModel>>.Ok(await _svc.GetModulesAsync(_tenant.OrganizationId, ct)));

    /// <summary>Any signed-in user — the frontend's module service reads it on start.</summary>
    [HttpGet("enabled")]
    public async Task<IActionResult> GetEnabled(CancellationToken ct) =>
        Ok(ApiResponse<EnabledModulesModel>.Ok(await _svc.GetEnabledAsync(_tenant.OrganizationId, ct)));

    [HttpPost("{code}/enable")]
    [RequirePermission(PermissionCodes.MODULES_MANAGE)]
    public async Task<IActionResult> Enable(string code, [FromBody] EnableModuleRequest? req, CancellationToken ct)
    {
        var card = await _svc.EnableAsync(_tenant.OrganizationId, code, req ?? new(), User.GetUserId(), ct);
        return Ok(ApiResponse<ModuleCardModel>.Ok(card, $"{card.Name} switched on."));
    }

    [HttpPost("{code}/disable")]
    [RequirePermission(PermissionCodes.MODULES_MANAGE)]
    public async Task<IActionResult> Disable(string code, [FromBody] DisableModuleRequest? req, CancellationToken ct)
    {
        var card = await _svc.DisableAsync(_tenant.OrganizationId, code, req ?? new(), User.GetUserId(), ct);
        return Ok(ApiResponse<ModuleCardModel>.Ok(card, $"{card.Name} switched off."));
    }

    [HttpPut("{code}/features/{featureCode}")]
    [RequirePermission(PermissionCodes.MODULES_MANAGE)]
    public async Task<IActionResult> SetFeature(string code, string featureCode, [FromBody] ToggleModuleFeatureRequest req, CancellationToken ct)
    {
        var card = await _svc.SetFeatureAsync(_tenant.OrganizationId, code, featureCode, req, User.GetUserId(), ct);
        return Ok(ApiResponse<ModuleCardModel>.Ok(card, "Feature updated."));
    }

    [HttpGet("{code}/history")]
    [RequirePermission(PermissionCodes.MODULES_VIEW)]
    public async Task<IActionResult> GetHistory(string code, CancellationToken ct) =>
        Ok(ApiResponse<List<ModuleHistoryEntryModel>>.Ok(await _svc.GetHistoryAsync(_tenant.OrganizationId, code, ct)));

    [HttpGet("{code}/impact")]
    [RequirePermission(PermissionCodes.MODULES_VIEW)]
    public async Task<IActionResult> GetImpact(string code, CancellationToken ct) =>
        Ok(ApiResponse<ModuleImpactModel>.Ok(await _svc.GetImpactAsync(_tenant.OrganizationId, code, ct)));
}

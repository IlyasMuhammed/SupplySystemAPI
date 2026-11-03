using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Integration.Auth;
using SMS.Modules.Integration.Core.Settings;
using SMS.Modules.Integration.Models;
using SMS.Shared.Authorization;
using SMS.Shared.Pagination;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Integration.Controllers.Admin;

/// <summary>A49 G-5 (API-CONTRACT §10.4). The map is per connection, so the tenant comes from the connection; another organization's rows cannot be named.</summary>
[ApiController]
[Route("api/integrations/quickbooks/cost-center-mappings")]
[RequiresFeature(IntegrationFeature.Code)]
public sealed class CostCenterMappingsController : ControllerBase
{
    private readonly ICostCenterQboMappingService _svc;
    public CostCenterMappingsController(ICostCenterQboMappingService svc) => _svc = svc;

    [Microsoft.AspNetCore.Http.EndpointSummary("List cost center to QuickBooks Class mappings")]
    [HttpGet, RequirePermission(P.INTEGRATION_VIEW, P.HRM_ACCOUNT_MAPPING_VIEW)]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(ApiResponse<IReadOnlyList<CostCenterMappingModel>>.Ok(await _svc.ListAsync(ct)));

    [Microsoft.AspNetCore.Http.EndpointSummary("List cost centers with no Class mapped")]
    [HttpGet("unmapped"), RequirePermission(P.INTEGRATION_VIEW, P.HRM_ACCOUNT_MAPPING_VIEW)]
    public async Task<IActionResult> Unmapped(CancellationToken ct) =>
        Ok(ApiResponse<IReadOnlyList<CostCenterMappingModel>>.Ok(await _svc.GetUnmappedAsync(ct)));

    /// <summary>What the Class and Location pickers offer.</summary>
    [Microsoft.AspNetCore.Http.EndpointSummary("Stored QuickBooks Classes and Departments for the pickers")]
    [HttpGet("reference"), RequirePermission(P.INTEGRATION_VIEW, P.HRM_ACCOUNT_MAPPING_VIEW)]
    public async Task<IActionResult> Reference(CancellationToken ct) =>
        Ok(ApiResponse<QboClassReferenceModel>.Ok(await _svc.ReferenceAsync(ct)));

    [Microsoft.AspNetCore.Http.EndpointSummary("Map a cost center to a QuickBooks Class or Department")]
    [HttpPut("{code}"), RequirePermission(P.INTEGRATION_MANAGE, P.HRM_ACCOUNT_MAPPING_EDIT)]
    public async Task<IActionResult> Update(string code, [FromBody] UpdateCostCenterMappingRequest request, CancellationToken ct) =>
        Ok(ApiResponse<CostCenterMappingModel>.Ok(await _svc.UpdateAsync(code, request, User.GetUserId(), ct)));

    [Microsoft.AspNetCore.Http.EndpointSummary("Fill the mapping list from HR's cost centers")]
    [HttpPost("sync"), RequirePermission(P.INTEGRATION_MANAGE, P.HRM_ACCOUNT_MAPPING_EDIT)]
    public async Task<IActionResult> Sync(CancellationToken ct) =>
        Ok(ApiResponse<CostCenterSyncResult>.Ok(await _svc.SyncAsync(User.GetUserId(), ct)));
}

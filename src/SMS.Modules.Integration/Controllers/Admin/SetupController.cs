using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Integration.Auth;
using SMS.Modules.Integration.Core.Reference;
using SMS.Modules.Integration.Core.Settings;
using SMS.Modules.Integration.Models;
using SMS.Shared.Authorization;
using SMS.Shared.Pagination;

namespace SMS.Modules.Integration.Controllers.Admin;

/// <summary>
/// Everything between "connected" and "live": QuickBooks reference data, preflight, settings, tax and
/// term mappings, the matching sign-off and the dry-run/live switch. Reads need <c>INTEGRATION_VIEW</c>,
/// changes <c>INTEGRATION_MANAGE</c>; every change is audited.
/// </summary>
[ApiController]
[Route("api/integrations/quickbooks")]
[RequiresFeature(IntegrationFeature.Code)]
public class SetupController : ControllerBase
{
    private readonly IReferenceDataService       _reference;
    private readonly IPreflightService           _preflight;
    private readonly IIntegrationSettingsService _settings;

    public SetupController(IReferenceDataService reference, IPreflightService preflight, IIntegrationSettingsService settings)
    {
        _reference = reference;
        _preflight = preflight;
        _settings  = settings;
    }

    // ── Reference data & preflight ──────────────────────────────────────────────────────────

    /// <summary>Accounts, tax codes, terms and currencies as last fetched — the mapping dropdowns.</summary>
    [RequirePermission(PermissionCodes.INTEGRATION_VIEW)]
    [HttpGet("reference")]
    public async Task<IActionResult> GetReference(CancellationToken ct) =>
        Ok(ApiResponse<ReferenceDataModel>.Ok(await _reference.GetAsync(ct)));

    /// <summary>Fetches them from QuickBooks again. 409 when not connected or QuickBooks does not answer.</summary>
    [RequirePermission(PermissionCodes.INTEGRATION_MANAGE)]
    [HttpPost("reference/refresh")]
    public async Task<IActionResult> RefreshReference(CancellationToken ct) =>
        Ok(ApiResponse<ReferenceDataModel>.Ok(await _reference.RefreshAsync(User.GetUserId(), ct)));

    [RequirePermission(PermissionCodes.INTEGRATION_VIEW)]
    [HttpGet("preflight")]
    public async Task<IActionResult> Preflight(CancellationToken ct) =>
        Ok(ApiResponse<PreflightResultModel>.Ok(await _preflight.RunAsync(ct)));

    // ── Settings ────────────────────────────────────────────────────────────────────────────

    [RequirePermission(PermissionCodes.INTEGRATION_VIEW)]
    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings(CancellationToken ct) =>
        Ok(ApiResponse<IntegrationSettingsModel>.Ok(await _settings.GetSettingsAsync(ct)));

    /// <summary>Mode is not changed here — see <c>POST /mode</c>.</summary>
    [RequirePermission(PermissionCodes.INTEGRATION_MANAGE)]
    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings([FromBody] UpdateIntegrationSettingsRequest request, CancellationToken ct) =>
        Ok(ApiResponse<IntegrationSettingsModel>.Ok(await _settings.UpdateSettingsAsync(request, User.GetUserId(), ct)));

    // ── Mappings ────────────────────────────────────────────────────────────────────────────

    /// <summary>Stored mappings plus every tax rate seen in stored invoices and bills, with how often.</summary>
    [RequirePermission(PermissionCodes.INTEGRATION_VIEW)]
    [HttpGet("tax-mappings")]
    public async Task<IActionResult> GetTaxMappings(CancellationToken ct) =>
        Ok(ApiResponse<List<TaxCodeMappingModel>>.Ok(await _settings.GetTaxMappingsAsync(ct)));

    /// <summary>Upserts; an empty <c>QboTaxCodeId</c> removes that rate's mapping.</summary>
    [RequirePermission(PermissionCodes.INTEGRATION_MANAGE)]
    [HttpPut("tax-mappings")]
    public async Task<IActionResult> SaveTaxMappings([FromBody] SaveTaxCodeMappingsRequest request, CancellationToken ct) =>
        Ok(ApiResponse<List<TaxCodeMappingModel>>.Ok(await _settings.SaveTaxMappingsAsync(request, User.GetUserId(), ct)));

    /// <summary>Stored mappings plus every payment term id seen on stored customers and vendors.</summary>
    [RequirePermission(PermissionCodes.INTEGRATION_VIEW)]
    [HttpGet("term-mappings")]
    public async Task<IActionResult> GetTermMappings(CancellationToken ct) =>
        Ok(ApiResponse<List<TermMappingModel>>.Ok(await _settings.GetTermMappingsAsync(ct)));

    /// <summary>Upserts; an empty <c>QboTermId</c> removes that term's mapping.</summary>
    [RequirePermission(PermissionCodes.INTEGRATION_MANAGE)]
    [HttpPut("term-mappings")]
    public async Task<IActionResult> SaveTermMappings([FromBody] SaveTermMappingsRequest request, CancellationToken ct) =>
        Ok(ApiResponse<List<TermMappingModel>>.Ok(await _settings.SaveTermMappingsAsync(request, User.GetUserId(), ct)));

    // ── Mode & matching ─────────────────────────────────────────────────────────────────────

    /// <summary>DryRun is always allowed; Live needs a passing preflight and confirmed matching (400 lists what is missing).</summary>
    [RequirePermission(PermissionCodes.INTEGRATION_MANAGE)]
    [HttpPost("mode")]
    public async Task<IActionResult> SetMode([FromBody] SetModeRequest request, CancellationToken ct) =>
        Ok(ApiResponse<IntegrationSettingsModel>.Ok(await _settings.SetModeAsync(request, User.GetUserId(), ct)));

    /// <summary>Records (or withdraws) that matching of existing customers, vendors and items is done.</summary>
    [RequirePermission(PermissionCodes.INTEGRATION_MANAGE)]
    [HttpPost("matching/complete")]
    public async Task<IActionResult> ConfirmMatching([FromBody] ConfirmMatchingCompleteRequest request, CancellationToken ct) =>
        Ok(ApiResponse<IntegrationSettingsModel>.Ok(await _settings.ConfirmMatchingAsync(request, User.GetUserId(), ct)));
}

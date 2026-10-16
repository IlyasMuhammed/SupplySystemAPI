using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Integration.Auth;
using SMS.Modules.Integration.Core.Reference;
using SMS.Modules.Integration.Core.Settings;
using SMS.Modules.Integration.Models;
using SMS.Shared.Authorization;
using SMS.Shared.Pagination;

namespace SMS.Modules.Integration.Controllers.Admin;

/// <summary>
/// A44 (docs/financial-posting/API-CONTRACT.md §4, CTRL): sync control, provider defaults and overrides, account mapping,
/// tax import, coverage and dry validation. Reads need <c>INTEGRATION_VIEW</c>, changes <c>INTEGRATION_MANAGE</c> (D-31);
/// every change is audited.
/// </summary>
[ApiController]
[Route("api/integrations/quickbooks")]
[RequiresFeature(IntegrationFeature.Code)]
public class PostingSetupController : ControllerBase
{
    private readonly IQuickBooksSyncControlService      _syncControl;
    private readonly IQuickBooksProviderDefaultsService _defaults;
    private readonly IQuickBooksAccountMappingService   _accounts;
    private readonly IIntegrationSettingsService        _settings;
    private readonly IQuickBooksCoverageService         _coverage;

    public PostingSetupController(
        IQuickBooksSyncControlService syncControl, IQuickBooksProviderDefaultsService defaults, IQuickBooksAccountMappingService accounts,
        IIntegrationSettingsService settings, IQuickBooksCoverageService coverage)
    {
        _syncControl = syncControl;
        _defaults    = defaults;
        _accounts    = accounts;
        _settings    = settings;
        _coverage    = coverage;
    }

    // ── Sync control (D-14) ─────────────────────────────────────────────────────────────────

    [RequirePermission(PermissionCodes.INTEGRATION_VIEW)]
    [HttpGet("sync-control")]
    public async Task<IActionResult> GetSyncControl(CancellationToken ct) =>
        Ok(ApiResponse<SyncControlModel>.Ok(await _syncControl.GetAsync(ct)));

    /// <summary>409 (result = { documentType, heldCount }) when enabling would send held records and <c>confirmRelease</c> is false.</summary>
    [RequirePermission(PermissionCodes.INTEGRATION_MANAGE)]
    [HttpPut("sync-control/{documentType}")]
    public Task<IActionResult> UpdateSyncControl(string documentType, [FromBody] UpdateSyncControlRequest request, CancellationToken ct) =>
        ConflictAware(async () => Ok(ApiResponse<SyncControlUpdateResult>.Ok(
            await _syncControl.UpdateAsync(documentType, request, User.GetUserId(), ct))));

    [RequirePermission(PermissionCodes.INTEGRATION_MANAGE)]
    [HttpPost("sync-control/{documentType}/release")]
    public async Task<IActionResult> ReleaseHeld(string documentType, CancellationToken ct) =>
        Ok(ApiResponse<SyncControlReleaseResult>.Ok(await _syncControl.ReleaseAsync(documentType, User.GetUserId(), ct)));

    // ── Provider defaults & overrides (D-15) ────────────────────────────────────────────────

    [RequirePermission(PermissionCodes.INTEGRATION_VIEW)]
    [HttpGet("provider-defaults")]
    public async Task<IActionResult> GetDefaults(CancellationToken ct) =>
        Ok(ApiResponse<ProviderDefaultsModel>.Ok(await _defaults.GetDefaultsAsync(ct)));

    [RequirePermission(PermissionCodes.INTEGRATION_MANAGE)]
    [HttpPut("provider-defaults")]
    public async Task<IActionResult> SaveDefaults([FromBody] SaveProviderDefaultsRequest request, CancellationToken ct) =>
        Ok(ApiResponse<ProviderDefaultsModel>.Ok(await _defaults.SaveDefaultsAsync(request, User.GetUserId(), ct)));

    [RequirePermission(PermissionCodes.INTEGRATION_VIEW)]
    [HttpGet("document-overrides")]
    public async Task<IActionResult> GetOverrides(CancellationToken ct) =>
        Ok(ApiResponse<List<DocumentOverrideModel>>.Ok(await _defaults.GetOverridesAsync(ct)));

    [RequirePermission(PermissionCodes.INTEGRATION_MANAGE)]
    [HttpPut("document-overrides")]
    public async Task<IActionResult> SaveOverrides([FromBody] SaveDocumentOverridesRequest request, CancellationToken ct) =>
        Ok(ApiResponse<List<DocumentOverrideModel>>.Ok(await _defaults.SaveOverridesAsync(request, User.GetUserId(), ct)));

    // ── Accounts (D-16) ─────────────────────────────────────────────────────────────────────

    [RequirePermission(PermissionCodes.INTEGRATION_VIEW)]
    [HttpGet("accounts")]
    public async Task<IActionResult> GetAccounts(CancellationToken ct) =>
        Ok(ApiResponse<AccountMappingsModel>.Ok(await _accounts.GetAsync(ct)));

    [RequirePermission(PermissionCodes.INTEGRATION_MANAGE)]
    [HttpPost("accounts/auto-map")]
    public async Task<IActionResult> AutoMapAccounts([FromBody] AutoMapAccountsRequest? request, CancellationToken ct) =>
        Ok(ApiResponse<AutoMapAccountsResult>.Ok(await _accounts.AutoMapAsync(request ?? new AutoMapAccountsRequest(), User.GetUserId(), ct)));

    [RequirePermission(PermissionCodes.INTEGRATION_MANAGE)]
    [HttpPut("accounts")]
    public async Task<IActionResult> SaveAccounts([FromBody] SaveAccountMappingsRequest request, CancellationToken ct) =>
        Ok(ApiResponse<AccountMappingsModel>.Ok(await _accounts.SaveAsync(request, User.GetUserId(), ct)));

    // ── Tax (D-18) ──────────────────────────────────────────────────────────────────────────

    [RequirePermission(PermissionCodes.INTEGRATION_MANAGE)]
    [HttpPost("tax-mappings/import")]
    public async Task<IActionResult> ImportTaxMappings(CancellationToken ct) =>
        Ok(ApiResponse<TaxImportResultModel>.Ok(await _settings.ImportTaxMappingsAsync(User.GetUserId(), ct)));

    // ── Coverage & validation (D-27) ────────────────────────────────────────────────────────

    [RequirePermission(PermissionCodes.INTEGRATION_VIEW)]
    [HttpGet("coverage")]
    public async Task<IActionResult> Coverage(CancellationToken ct) =>
        Ok(ApiResponse<CoverageModel>.Ok(await _coverage.GetAsync(ct)));

    /// <summary>Validates the payload the gateway holds for one record; never calls QuickBooks.</summary>
    [RequirePermission(PermissionCodes.INTEGRATION_VIEW)]
    [HttpPost("validation/check")]
    public async Task<IActionResult> ValidationCheck([FromBody] ValidationCheckRequest request, CancellationToken ct) =>
        Ok(ApiResponse<ValidationCheckResult>.Ok(await _coverage.CheckAsync(request, ct)));

    private async Task<IActionResult> ConflictAware(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (IntegrationConflictException ex)
        {
            return ConflictResult(ex);
        }
    }

    internal static IActionResult ConflictResult(IntegrationConflictException ex) =>
        new ObjectResult(ApiResponse.Fail(ex.Message, ex.Detail)) { StatusCode = StatusCodes.Status409Conflict };
}

using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Integration.Auth;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Models;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Pagination;

namespace SMS.Modules.Integration.Controllers.Admin;

/// <summary>
/// The platform's Intuit app keys (docs/quickbooks/APP-CREDENTIALS.md D-7): one set for every organization that does
/// not use its own app. Platform super admin only — a tenant can never read or change them.
/// </summary>
[ApiController]
[Route("api/system/integrations/quickbooks/app")]
[RequirePermission(PermissionCodes.PLATFORM_SUPER_ADMIN)]
[RequireSuperAdmin] // the code alone is carried by roles any organization can hold — see the attribute
public class PlatformAppKeysController : ControllerBase
{
    private readonly IQuickBooksAppSettingsService _keys;

    public PlatformAppKeysController(IQuickBooksAppSettingsService keys) => _keys = keys;

    /// <summary>The keys in force (saved, else configuration) — never the secret.</summary>
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct) =>
        Ok(ApiResponse<PlatformAppKeysModel>.Ok(await _keys.GetPlatformAsync(ct)));

    /// <summary>Saves the keys. A blank secret keeps the stored one. 409 when live connections would be stranded (confirm to go ahead).</summary>
    [HttpPut]
    public async Task<IActionResult> Save([FromBody] SavePlatformAppKeysRequest request, CancellationToken ct) =>
        Ok(ApiResponse<PlatformAppKeysModel>.Ok(await _keys.SavePlatformAsync(request, User.GetUserId(), ct), "QuickBooks app keys saved."));

    /// <summary>Removes the saved keys; the configuration fallback (if any) applies again.</summary>
    [HttpDelete]
    public async Task<IActionResult> Delete([FromQuery] bool confirmBreakingChange, CancellationToken ct) =>
        Ok(ApiResponse<PlatformAppKeysModel>.Ok(await _keys.DeletePlatformAsync(confirmBreakingChange, User.GetUserId(), ct),
            "Saved QuickBooks app keys removed."));
}

/// <summary>
/// The organization's own Intuit app keys (D-7). Saving needs FEATURE_QBO_OWN_APP, which only the super admin licenses;
/// reading shows whether it is on and the callback URL to register. Removing is always allowed.
/// </summary>
[ApiController]
[Route("api/integrations/quickbooks/app-keys")]
[RequiresFeature(IntegrationFeature.Code)]
public class TenantAppKeysController : ControllerBase
{
    private readonly IQuickBooksAppSettingsService _keys;

    public TenantAppKeysController(IQuickBooksAppSettingsService keys) => _keys = keys;

    [RequirePermission(PermissionCodes.INTEGRATION_VIEW)]
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct) =>
        Ok(ApiResponse<TenantAppKeysModel>.Ok(await _keys.GetTenantAsync(ct)));

    /// <summary>Saves the organization's own keys. A blank secret keeps the stored one. 409 while connected through them (except a secret rotation).</summary>
    [RequirePermission(PermissionCodes.INTEGRATION_MANAGE)]
    [RequiresFeature(ModuleCodes.QuickBooksOwnApp)]
    [HttpPut]
    public async Task<IActionResult> Save([FromBody] SaveTenantAppKeysRequest request, CancellationToken ct) =>
        Ok(ApiResponse<TenantAppKeysModel>.Ok(await _keys.SaveTenantAsync(request, User.GetUserId(), ct),
            "Your QuickBooks app keys were saved. They are used the next time QuickBooks is connected."));

    [RequirePermission(PermissionCodes.INTEGRATION_MANAGE)]
    [HttpDelete]
    public async Task<IActionResult> Delete(CancellationToken ct) =>
        Ok(ApiResponse<TenantAppKeysModel>.Ok(await _keys.DeleteTenantAsync(User.GetUserId(), ct),
            "Your QuickBooks app keys were removed. The platform's app is used for new connections."));
}

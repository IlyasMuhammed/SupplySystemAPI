using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Logistics.Controllers;

/// <summary>
/// Carrier accounts — which contract a booking goes out on, and what it may be asked for.
/// </summary>
/// <remarks>
/// Gated by <c>CARRIER_MANAGE</c> throughout, including the reads. Account configuration decides
/// what money gets spent and on whose contract; <c>DELIVERY_TRACK</c> is for watching parcels, and
/// somebody who watches parcels has no business seeing or changing commercial arrangements.
/// </remarks>
[ApiController]
[Route("api/logistics/carrier-accounts")]
[RequiresFeature("MODULE_LOGISTICS")]
public class CarrierAccountsController : ControllerBase
{
    private readonly ICarrierAccountService    _svc;
    private readonly ICarrierCredentialService _credentials;
    private readonly ICarrierIntegrationService _integration;

    public CarrierAccountsController(
        ICarrierAccountService svc, ICarrierCredentialService credentials, ICarrierIntegrationService integration)
    {
        _svc         = svc;
        _credentials = credentials;
        _integration = integration;
    }

    /// <summary>
    /// The courier adapters this deployment has and the credentials each reads — so an account can be set up
    /// from the screen, and the keys to enter are known before a booking is refused for lacking them.
    /// </summary>
    [RequirePermission(PermissionCodes.CARRIER_MANAGE)]
    [HttpGet("providers")]
    public IActionResult GetProviders() =>
        Ok(ApiResponse<IReadOnlyList<CourierProviderModel>>.Ok(_integration.GetProviders()));

    /// <summary>Which adapter a carrier books through. Kept off the carrier's own detail — see the model.</summary>
    [RequirePermission(PermissionCodes.CARRIER_MANAGE)]
    [HttpGet("by-carrier/{carrierUuid:guid}/integration")]
    public async Task<IActionResult> GetIntegration(Guid carrierUuid)
    {
        var integration = await _integration.GetAsync(carrierUuid);
        return integration is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<CarrierIntegrationModel>.Ok(integration));
    }

    /// <summary>
    /// Points a carrier at MANUAL booking or at an adapter. Refused while consignments are on the road
    /// through the current one: their tracking would be asked of a system that never heard of them.
    /// </summary>
    [RequirePermission(PermissionCodes.CARRIER_MANAGE)]
    [HttpPut("by-carrier/{carrierUuid:guid}/integration")]
    public async Task<IActionResult> SetIntegration(Guid carrierUuid, [FromBody] SetCarrierIntegrationRequest req)
    {
        var integration = await _integration.SetAsync(carrierUuid, req, User.GetUserId());
        return integration is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<CarrierIntegrationModel>.Ok(integration, StaticResponseMessage.recordUpdatedSuccessfully));
    }

    [RequirePermission(PermissionCodes.CARRIER_MANAGE)]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCarrierAccountRequest req)
    {
        var uuid = await _svc.CreateAsync(req, User.GetUserId());
        return Ok(ApiResponse<Guid>.Ok(uuid, StaticResponseMessage.recordCreatedSuccessfully));
    }

    /// <summary>Every account for a carrier, default first.</summary>
    [RequirePermission(PermissionCodes.CARRIER_MANAGE)]
    [HttpGet("by-carrier/{carrierUuid:guid}")]
    public async Task<IActionResult> GetForCarrier(Guid carrierUuid)
    {
        var accounts = await _svc.GetForCarrierAsync(carrierUuid);
        return Ok(ApiResponse<IReadOnlyList<CarrierAccountModel>>.Ok(accounts));
    }

    [RequirePermission(PermissionCodes.CARRIER_MANAGE)]
    [HttpGet("{uuid:guid}")]
    public async Task<IActionResult> GetById(Guid uuid)
    {
        var account = await _svc.GetByUuidAsync(uuid);
        return account is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<CarrierAccountModel>.Ok(account));
    }

    /// <remarks>
    /// Send <c>clearOverrides</c> to return a capability to whatever the adapter says — a null in
    /// the flag itself means "leave alone" on a patch, so there is no other way to express it.
    /// </remarks>
    [RequirePermission(PermissionCodes.CARRIER_MANAGE)]
    [HttpPatch("{uuid:guid}")]
    public async Task<IActionResult> Patch(Guid uuid, [FromBody] PatchCarrierAccountRequest req)
    {
        var updated = await _svc.PatchAsync(uuid, req, User.GetUserId());
        return updated
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    [RequirePermission(PermissionCodes.CARRIER_MANAGE)]
    [HttpDelete("{uuid:guid}")]
    public async Task<IActionResult> Delete(Guid uuid)
    {
        var deleted = await _svc.DeleteAsync(uuid, User.GetUserId());
        return deleted
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordDeletedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    // ── Credentials ───────────────────────────────────────────────────────────
    //
    // Gated by CARRIER_CREDENTIAL_MANAGE, a narrower grant than CARRIER_MANAGE: deciding which
    // contract a parcel ships on and holding the keys to a carrier account are different levels
    // of trust.

    /// <summary>
    /// Which credentials are configured — keys, notes and expiry. <b>Never the values.</b>
    /// </summary>
    /// <remarks>
    /// There is no endpoint that returns a stored credential, masked or otherwise. A mask still
    /// discloses length and shape, and "so an administrator can check it" is better answered by
    /// attempting a booking. A credential that cannot be read back can only be replaced.
    /// </remarks>
    [RequirePermission(PermissionCodes.CARRIER_CREDENTIAL_MANAGE)]
    [HttpGet("{uuid:guid}/credentials")]
    public async Task<IActionResult> GetCredentials(Guid uuid)
    {
        var credentials = await _credentials.ListAsync(uuid);
        return Ok(ApiResponse<IReadOnlyList<CarrierCredentialModel>>.Ok(credentials));
    }

    /// <summary>Sets or replaces one credential. Encrypted before it is stored.</summary>
    [RequirePermission(PermissionCodes.CARRIER_CREDENTIAL_MANAGE)]
    [HttpPut("{uuid:guid}/credentials")]
    public async Task<IActionResult> SetCredential(
        Guid uuid, [FromBody] SetCarrierCredentialRequest req)
    {
        var credentialUuid = await _credentials.SetAsync(uuid, req, User.GetUserId());
        return Ok(ApiResponse<Guid>.Ok(credentialUuid, "Credential saved."));
    }

    [RequirePermission(PermissionCodes.CARRIER_CREDENTIAL_MANAGE)]
    [HttpDelete("{uuid:guid}/credentials/{key}")]
    public async Task<IActionResult> RemoveCredential(Guid uuid, string key)
    {
        var removed = await _credentials.RemoveAsync(uuid, key, User.GetUserId());
        return removed
            ? Ok(ApiResponse.Ok("Credential removed."))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }
}

using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Logistics.Controllers;

/// <summary>
/// Carrier services — the named products a carrier sells, and the terms that price them.
/// </summary>
/// <remarks>
/// Gated by <c>CARRIER_MANAGE</c>, like carrier accounts: a dim divisor decides what every parcel
/// on that service is charged for, so editing one is a commercial act, not a lookup edit.
/// </remarks>
[ApiController]
[Route("api/logistics/carrier-services")]
[RequiresFeature("MODULE_LOGISTICS")]
public class CarrierServicesController : ControllerBase
{
    private readonly ICarrierServiceCatalogService _svc;
    public CarrierServicesController(ICarrierServiceCatalogService svc) => _svc = svc;

    [RequirePermission(PermissionCodes.CARRIER_MANAGE)]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCarrierServiceRequest req)
    {
        var uuid = await _svc.CreateAsync(req, User.GetUserId());
        return Ok(ApiResponse<Guid>.Ok(uuid, StaticResponseMessage.recordCreatedSuccessfully));
    }

    /// <summary>Every service a carrier sells, default first.</summary>
    [RequirePermission(PermissionCodes.CARRIER_MANAGE)]
    [HttpGet("by-carrier/{carrierUuid:guid}")]
    public async Task<IActionResult> GetForCarrier(Guid carrierUuid)
    {
        var services = await _svc.GetForCarrierAsync(carrierUuid);
        return Ok(ApiResponse<IReadOnlyList<CarrierServiceModel>>.Ok(services));
    }

    [RequirePermission(PermissionCodes.CARRIER_MANAGE)]
    [HttpGet("{uuid:guid}")]
    public async Task<IActionResult> GetById(Guid uuid)
    {
        var service = await _svc.GetByUuidAsync(uuid);
        return service is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<CarrierServiceModel>.Ok(service));
    }

    /// <remarks>
    /// Send <c>clearLimits</c> to remove a limit: a null in the field itself means "leave alone"
    /// on a patch, so there is no other way to express "no maximum".
    /// </remarks>
    [RequirePermission(PermissionCodes.CARRIER_MANAGE)]
    [HttpPatch("{uuid:guid}")]
    public async Task<IActionResult> Patch(Guid uuid, [FromBody] PatchCarrierServiceRequest req)
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
}

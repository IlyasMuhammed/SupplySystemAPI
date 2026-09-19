using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Logistics.Controllers;

// ── Carriers ──────────────────────────────────────────────────────────────────

[ApiController]
[Route("api/logistics/carriers")]
[RequiresFeature("MODULE_LOGISTICS")]
// Closes a real gap: these endpoints had only the feature gate, so any authenticated user in a
// logistics-enabled organization could reach them. DELIVERY_TRACK is the code the Angular routes
// already guard these screens with, so nobody who can reach them today loses access.
[RequirePermission(PermissionCodes.DELIVERY_TRACK)]
public class CarriersController : ControllerBase
{
    private readonly ICarrierService _svc;
    public CarriersController(ICarrierService svc) => _svc = svc;

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCarrierRequest req)
    {
        var uuid = await _svc.CreateAsync(req, User.GetUserId());
        return Ok(ApiResponse<Guid>.Ok(uuid, StaticResponseMessage.recordCreatedSuccessfully));
    }

    [HttpGet]
    public async Task<IActionResult> GetList([FromQuery] CarrierFilter filter)
    {
        var result = await _svc.GetListAsync(filter);
        return Ok(ApiResponse<PaginatedResponse<CarrierListItemModel>>.Ok(result));
    }

    [HttpGet("active")]
    public async Task<IActionResult> GetAllActive()
    {
        var result = await _svc.GetAllActiveAsync();
        return Ok(ApiResponse<List<CarrierListItemModel>>.Ok(result));
    }

    [HttpGet("{uuid:guid}")]
    public async Task<IActionResult> GetById(Guid uuid)
    {
        var detail = await _svc.GetByUuidAsync(uuid);
        return detail is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<CarrierDetailModel>.Ok(detail));
    }

    [HttpPatch("{uuid:guid}")]
    public async Task<IActionResult> Patch(Guid uuid, [FromBody] PatchCarrierRequest req)
    {
        var updated = await _svc.PatchAsync(uuid, req, User.GetUserId());
        return updated
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    [HttpDelete("{uuid:guid}")]
    public async Task<IActionResult> Delete(Guid uuid)
    {
        var deleted = await _svc.DeleteAsync(uuid);
        return deleted
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordDeletedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }
}

// ── Shipments ─────────────────────────────────────────────────────────────────

/// <summary>
/// <b>Deprecated.</b> Superseded by <c>api/logistics/deliveries</c> (layer A) and
/// <c>api/logistics/consignments</c> (layer C).
/// </summary>
/// <remarks>
/// Kept alive because it is the only logistics API the four existing Angular screens use, and
/// they stay in service until the delivery cockpit ships (T-19). Every row behind it has been
/// copied into the new model by the T-16 backfill; the legacy table is still the source these
/// endpoints read, so consignments created through the new API do not appear here.
/// <para>
/// <b>Removal:</b> one release after the cockpit ships. The response shape is pinned by
/// <c>LegacyShipmentContractTests</c> so it cannot drift in the meantime.
/// </para>
/// </remarks>
[ApiController]
[Route("api/logistics/shipments")]
[RequiresFeature("MODULE_LOGISTICS")]
[RequirePermission(PermissionCodes.DELIVERY_TRACK)]
public class ShipmentsController : ControllerBase
{
    private readonly IShipmentService  _svc;
    private readonly IWebHostEnvironment _env;

    public ShipmentsController(IShipmentService svc, IWebHostEnvironment env)
    {
        _svc = svc;
        _env = env;
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateShipmentRequest req)
    {
        var uuid = await _svc.CreateAsync(req, User.GetUserId());
        return Ok(ApiResponse<Guid>.Ok(uuid, StaticResponseMessage.recordCreatedSuccessfully));
    }

    [HttpGet]
    public async Task<IActionResult> GetList([FromQuery] ShipmentFilter filter)
    {
        var result = await _svc.GetListAsync(filter);
        return Ok(ApiResponse<PaginatedResponse<ShipmentListItemModel>>.Ok(result));
    }

    [HttpGet("{uuid:guid}")]
    public async Task<IActionResult> GetById(Guid uuid)
    {
        var detail = await _svc.GetByUuidAsync(uuid);
        return detail is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<ShipmentDetailModel>.Ok(detail));
    }

    [HttpPatch("{uuid:guid}")]
    public async Task<IActionResult> Patch(Guid uuid, [FromBody] PatchShipmentRequest req)
    {
        var updated = await _svc.PatchAsync(uuid, req, User.GetUserId());
        return updated
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    // The proof-of-delivery upload that lived here was retired with finding F47. It wrote a file
    // into wwwroot and stored the path in a column, which is a proof only for as long as that
    // directory survives a redeploy. T-61 replaced it with api/logistics/delivery-proofs, which
    // stores the bytes, records who took the goods, and is gated by POD_CAPTURE.
}

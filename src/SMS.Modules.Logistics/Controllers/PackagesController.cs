using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Logistics.Controllers;

/// <summary>
/// Packages — layer B, the handling units a delivery is packed into.
/// </summary>
/// <remarks>
/// Gated by <c>DISPATCH</c>, which already exists and already means "prepare goods for dispatch".
/// Reading is <c>DELIVERY_VIEW</c>, so a supervisor or a customer-service desk can see what is in
/// which carton without being able to repack it.
/// </remarks>
[ApiController]
[Route("api/logistics/packages")]
[RequiresFeature("MODULE_LOGISTICS")]
public class PackagesController : ControllerBase
{
    private readonly IPackageService _svc;
    public PackagesController(IPackageService svc) => _svc = svc;

    [RequirePermission(PermissionCodes.DELIVERY_VIEW)]
    [HttpGet("{uuid:guid}")]
    public async Task<IActionResult> GetById(Guid uuid)
    {
        var package = await _svc.GetByUuidAsync(uuid);
        return package is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<PackageModel>.Ok(package));
    }

    /// <summary>Corrects dimensions, weights, seal or the pallet a carton sits on.</summary>
    [RequirePermission(PermissionCodes.DISPATCH)]
    [HttpPatch("{uuid:guid}")]
    public async Task<IActionResult> Patch(Guid uuid, [FromBody] PatchPackageRequest req)
    {
        var updated = await _svc.PatchAsync(uuid, req, User.GetUserId());
        return updated
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    /// <summary>
    /// Takes a carton out of the picture. The row and its barcode are kept — the label may already
    /// be on a real box, and reissuing the number would make two cartons indistinguishable.
    /// </summary>
    [RequirePermission(PermissionCodes.DISPATCH)]
    [HttpPost("{uuid:guid}/void")]
    public async Task<IActionResult> Void(Guid uuid, [FromBody] DeliveryReasonRequest req)
    {
        var voided = await _svc.VoidAsync(uuid, req, User.GetUserId());
        return voided
            ? Ok(ApiResponse.Ok("Package voided. Its contents are unpacked again."))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }
}

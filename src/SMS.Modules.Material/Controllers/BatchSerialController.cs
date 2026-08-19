using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Inventory.Services;
using SMS.Modules.Material.Models;
using SMS.Modules.Material.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Material.Controllers;

[ApiController]
[Route("api/batch-serials")]
[RequiresFeature("MODULE_MIR")]
[RequirePermission(PermissionCodes.MATERIAL_VIEW)]
public class BatchSerialController : ControllerBase
{
    private readonly IBatchSerialService     _batchSerial;
    private readonly IChainOfCustodyService  _custody;

    public BatchSerialController(IBatchSerialService batchSerial, IChainOfCustodyService custody)
    {
        _batchSerial = batchSerial;
        _custody     = custody;
    }

    /// <summary>
    /// Returns available batches (FEFO-ordered) or serial numbers for a variant.
    /// Used by the MIV create form to populate the batch/serial picker.
    /// </summary>
    [HttpGet("available/{variantUuid:guid}")]
    public async Task<IActionResult> GetAvailable(Guid variantUuid, [FromQuery] Guid? warehouseUuid = null)
    {
        var result = await _batchSerial.GetAvailableBatchesAsync(variantUuid, warehouseUuid);
        return Ok(ApiResponse<AvailableBatchesResponse>.Ok(result));
    }

    /// <summary>
    /// Traces a batch number or serial number from GRN receipt through inventory location
    /// to all MIV issues. Provides full chain-of-custody for audit purposes.
    /// </summary>
    [HttpGet("trace")]
    public async Task<IActionResult> Trace([FromQuery] string reference, [FromQuery] Guid? variantUuid = null)
    {
        if (string.IsNullOrWhiteSpace(reference))
            throw new BadRequestException("Reference (batch number or serial number) is required.");

        var result = await _custody.TraceAsync(reference, variantUuid);
        if (result is null)
            throw new NotFoundException("No inventory records found for the given reference.");

        return Ok(ApiResponse<ChainOfCustodyResponse>.Ok(result));
    }
}

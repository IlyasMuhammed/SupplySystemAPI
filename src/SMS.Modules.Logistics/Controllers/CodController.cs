using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Settlement;
using SMS.Shared.Authorization;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Logistics.Controllers;

/// <summary>
/// Cash on delivery — what a carrier collected on our behalf, and whether it came back.
/// </summary>
/// <remarks>
/// The opposite of a freight accrual: this is money the carrier is holding that belongs to us.
/// Recording a collection or a remittance is gated by <c>FREIGHT_INVOICE_RECONCILE</c>, because it
/// decides what a carrier is deemed to have settled.
/// </remarks>
[ApiController]
[Route("api/logistics/cod")]
[RequiresFeature("MODULE_LOGISTICS")]
public class CodController : ControllerBase
{
    private readonly ICodReconciliationService _cod;
    public CodController(ICodReconciliationService cod) => _cod = cod;

    /// <summary>
    /// What carriers are still holding, by carrier and currency — and what was delivered carrying
    /// cash that nobody has said was collected.
    /// </summary>
    [RequirePermission(PermissionCodes.FREIGHT_INVOICE_VIEW)]
    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary([FromQuery] DateTime? asOf, CancellationToken ct)
    {
        var summary = await _cod.GetSummaryAsync(asOf, ct);
        return Ok(ApiResponse<CodSummaryModel>.Ok(summary));
    }

    /// <summary>Outstanding cash, largest first. Defaults to everything still owed to us.</summary>
    [RequirePermission(PermissionCodes.FREIGHT_INVOICE_VIEW)]
    [HttpGet]
    public async Task<IActionResult> GetList([FromQuery] CodFilter filter, CancellationToken ct)
    {
        var list = await _cod.GetListAsync(filter, ct);
        return Ok(ApiResponse<PaginatedResponse<CodCollectionModel>>.Ok(list));
    }

    [RequirePermission(PermissionCodes.FREIGHT_INVOICE_VIEW)]
    [HttpGet("{consignmentUuid:guid}")]
    public async Task<IActionResult> GetForConsignment(Guid consignmentUuid, CancellationToken ct)
    {
        var record = await _cod.GetAsync(consignmentUuid, ct);
        return record is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<CodCollectionModel>.Ok(record));
    }

    /// <summary>Records that the carrier says it took the money.</summary>
    [RequirePermission(PermissionCodes.FREIGHT_INVOICE_RECONCILE)]
    [HttpPost("{consignmentUuid:guid}/collected")]
    public async Task<IActionResult> RecordCollection(
        Guid consignmentUuid, [FromBody] RecordCodCollectionRequest req, CancellationToken ct)
    {
        var record = await _cod.RecordCollectionAsync(consignmentUuid, req, User.GetUserId(), ct);
        return record is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<CodCollectionModel>.Ok(record, "Collection recorded."));
    }

    /// <summary>Records money reaching us. Several are expected — carriers remit in batches.</summary>
    [RequirePermission(PermissionCodes.FREIGHT_INVOICE_RECONCILE)]
    [HttpPost("{consignmentUuid:guid}/remitted")]
    public async Task<IActionResult> RecordRemittance(
        Guid consignmentUuid, [FromBody] RecordCodRemittanceRequest req, CancellationToken ct)
    {
        var record = await _cod.RecordRemittanceAsync(consignmentUuid, req, User.GetUserId(), ct);
        return record is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<CodCollectionModel>.Ok(record, "Remittance recorded."));
    }

    /// <summary>Accepts a shortfall that will not be recovered. The reason is required.</summary>
    [RequirePermission(PermissionCodes.FREIGHT_INVOICE_RECONCILE)]
    [HttpPost("{consignmentUuid:guid}/write-off")]
    public async Task<IActionResult> WriteOff(
        Guid consignmentUuid, [FromBody] WriteOffCodRequest req, CancellationToken ct)
    {
        var written = await _cod.WriteOffAsync(consignmentUuid, req, User.GetUserId(), ct);
        return written
            ? Ok(ApiResponse.Ok("Shortfall written off."))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }
}

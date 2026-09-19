using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Settlement;
using SMS.Shared.Authorization;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Logistics.Controllers;

/// <summary>
/// Freight accruals — what is owed to carriers for movements that have already happened.
/// </summary>
/// <remarks>
/// Reading is gated by <c>SHIPMENT_RATE_VIEW</c>: an accrual balance is what the company owes for
/// carriage, which is the same commercial information a rate is.
/// </remarks>
[ApiController]
[Route("api/logistics/freight-accruals")]
[RequiresFeature("MODULE_LOGISTICS")]
public class FreightAccrualsController : ControllerBase
{
    private readonly IFreightAccrualService _accruals;
    public FreightAccrualsController(IFreightAccrualService accruals) => _accruals = accruals;

    /// <summary>
    /// What is still owed, by carrier and currency, as at a date — and what could not be accrued
    /// because it was never priced.
    /// </summary>
    /// <param name="asOf">
    /// The date to strike the balance at. An accrual released afterwards was still a liability
    /// then, so a month-end figure must be asked for as at month end, not as at today.
    /// </param>
    [RequirePermission(PermissionCodes.SHIPMENT_RATE_VIEW)]
    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary([FromQuery] DateTime? asOf, CancellationToken ct)
    {
        var summary = await _accruals.GetSummaryAsync(asOf, ct);
        return Ok(ApiResponse<FreightAccrualSummaryModel>.Ok(summary));
    }

    [RequirePermission(PermissionCodes.SHIPMENT_RATE_VIEW)]
    [HttpGet("{consignmentUuid:guid}")]
    public async Task<IActionResult> GetForConsignment(Guid consignmentUuid, CancellationToken ct)
    {
        var accrual = await _accruals.GetAsync(consignmentUuid, ct);
        return accrual is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<FreightAccrualModel>.Ok(accrual));
    }

    /// <summary>
    /// Accrues one consignment now, rather than waiting for the nightly sweep. Idempotent — a
    /// consignment already accrued comes back unchanged.
    /// </summary>
    [RequirePermission(PermissionCodes.DELIVERY_EDIT)]
    [HttpPost("{consignmentUuid:guid}")]
    public async Task<IActionResult> Accrue(Guid consignmentUuid, CancellationToken ct)
    {
        var accrual = await _accruals.AccrueAsync(consignmentUuid, User.GetUserId(), ct);
        return accrual is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<FreightAccrualModel>.Ok(accrual, "Accrued."));
    }

    /// <summary>Writes an accrual back without an invoice. The reason is required.</summary>
    [RequirePermission(PermissionCodes.DELIVERY_EDIT)]
    [HttpPost("{consignmentUuid:guid}/reverse")]
    public async Task<IActionResult> Reverse(
        Guid consignmentUuid, [FromBody] ReverseAccrualRequest req, CancellationToken ct)
    {
        var reversed = await _accruals.ReverseAsync(consignmentUuid, req, User.GetUserId(), ct);
        return reversed
            ? Ok(ApiResponse.Ok("Accrual written back."))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }
}

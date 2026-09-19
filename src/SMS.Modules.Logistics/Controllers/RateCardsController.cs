using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Rating;
using SMS.Shared.Authorization;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Logistics.Controllers;

/// <summary>
/// Rate cards — negotiated carrier tariffs, by lane and weight break.
/// </summary>
/// <remarks>
/// Editing is gated by <c>RATE_CARD_MANAGE</c> and reading by <c>SHIPMENT_RATE_VIEW</c>: a tariff
/// decides what carriage is deemed to cost and feeds Phase 4's invoice reconciliation, so changing
/// one is a commercial act and seeing one is commercially sensitive.
/// </remarks>
[ApiController]
[Route("api/logistics/rate-cards")]
[RequiresFeature("MODULE_LOGISTICS")]
public class RateCardsController : ControllerBase
{
    private readonly IRateCardService _svc;
    public RateCardsController(IRateCardService svc) => _svc = svc;

    [RequirePermission(PermissionCodes.RATE_CARD_MANAGE)]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateRateCardRequest req)
    {
        var uuid = await _svc.CreateAsync(req, User.GetUserId());
        return Ok(ApiResponse<Guid>.Ok(uuid, StaticResponseMessage.recordCreatedSuccessfully));
    }

    /// <summary>Every card configured for a carrier, newest period first.</summary>
    /// <param name="on">The date to report <c>isInEffect</c> against. Defaults to today.</param>
    [RequirePermission(PermissionCodes.SHIPMENT_RATE_VIEW)]
    [HttpGet("by-carrier/{carrierUuid:guid}")]
    public async Task<IActionResult> GetForCarrier(Guid carrierUuid, [FromQuery] DateTime? on)
    {
        var cards = await _svc.GetForCarrierAsync(carrierUuid, on);
        return Ok(ApiResponse<IReadOnlyList<RateCardModel>>.Ok(cards));
    }

    [RequirePermission(PermissionCodes.SHIPMENT_RATE_VIEW)]
    [HttpGet("{uuid:guid}")]
    public async Task<IActionResult> GetById(Guid uuid, [FromQuery] DateTime? on)
    {
        var card = await _svc.GetByUuidAsync(uuid, on);
        return card is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<RateCardModel>.Ok(card));
    }

    /// <remarks>
    /// Sending <c>lanes</c> replaces the whole tariff. A card is edited as a whole so it never
    /// spends a moment with a hole in it — a card with a hole prices most consignments correctly
    /// and one silently wrong.
    /// </remarks>
    [RequirePermission(PermissionCodes.RATE_CARD_MANAGE)]
    [HttpPatch("{uuid:guid}")]
    public async Task<IActionResult> Patch(Guid uuid, [FromBody] PatchRateCardRequest req)
    {
        var updated = await _svc.PatchAsync(uuid, req, User.GetUserId());
        return updated
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    [RequirePermission(PermissionCodes.RATE_CARD_MANAGE)]
    [HttpDelete("{uuid:guid}")]
    public async Task<IActionResult> Delete(Guid uuid)
    {
        var deleted = await _svc.DeleteAsync(uuid, User.GetUserId());
        return deleted
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordDeletedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    /// <summary>
    /// What the cards say a carriage costs — a dry run against a weight and a lane, without a
    /// consignment. What a tariff screen uses to show somebody that their card does what they meant.
    /// </summary>
    /// <remarks>
    /// Returns a status rather than an empty price when nothing matches: "no price" and "priced at
    /// nothing" look identical on a screen and mean opposite things.
    /// </remarks>
    [RequirePermission(PermissionCodes.SHIPMENT_RATE_VIEW)]
    [HttpPost("quote")]
    public async Task<IActionResult> Quote([FromBody] RateCardQuoteRequest req, CancellationToken ct)
    {
        var quote = await _svc.QuoteAsync(req, ct);
        return Ok(ApiResponse<RateCardQuote>.Ok(quote));
    }
}

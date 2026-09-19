using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using SMS.Modules.Logistics.Couriers.Booking;
using SMS.Modules.Logistics.Couriers.Labels;
using SMS.Modules.Logistics.Couriers.Tracking;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Rating;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Logistics.Controllers;

/// <summary>
/// Consignments — layer C, the carrier-facing movement.
/// </summary>
/// <remarks>
/// Routed at <c>consignments</c> rather than <c>shipments</c> because the legacy
/// <c>api/logistics/shipments</c> endpoints are still serving the four existing Angular screens
/// until T-16 turns them into shims.
/// <para>
/// Permission codes arrive in T-17, as for deliveries.
/// </para>
/// </remarks>
[ApiController]
[Route("api/logistics/consignments")]
[RequiresFeature("MODULE_LOGISTICS")]
public class ConsignmentsController : ControllerBase
{
    private readonly IConsignmentService        _svc;
    private readonly IConsignmentBookingService _booking;
    private readonly IConsignmentLabelService   _labels;
    private readonly IConsignmentTrackingService _tracking;
    private readonly IChargeableWeightService    _weights;
    private readonly IConsignmentRatingService   _rating;
    private readonly IRateShoppingService        _shopping;

    public ConsignmentsController(
        IConsignmentService svc, IConsignmentBookingService booking, IConsignmentLabelService labels,
        IConsignmentTrackingService tracking, IChargeableWeightService weights,
        IConsignmentRatingService rating, IRateShoppingService shopping)
    {
        _svc      = svc;
        _booking  = booking;
        _labels   = labels;
        _tracking = tracking;
        _weights  = weights;
        _rating   = rating;
        _shopping = shopping;
    }

    /// <summary>
    /// Consignments that have gone quiet for longer than their status allows — not collected, no
    /// scan in transit, stuck in exception, or tracking failing. Longest stuck first.
    /// </summary>
    [RequirePermission(PermissionCodes.DELIVERY_VIEW)]
    [HttpGet("stuck")]
    public async Task<IActionResult> GetStuck()
    {
        var stuck = await _tracking.GetStuckAsync();
        return Ok(ApiResponse<IReadOnlyList<StuckConsignmentModel>>.Ok(stuck));
    }

    /// <summary>Asks the carrier for tracking now. Pressed again within a minute, it does not ask twice.</summary>
    /// <remarks>Gated by <c>DELIVERY_EDIT</c>: it calls the carrier and can change the consignment's status.</remarks>
    [RequirePermission(PermissionCodes.DELIVERY_EDIT)]
    [HttpPost("{uuid:guid}/tracking/refresh")]
    public async Task<IActionResult> RefreshTracking(Guid uuid, CancellationToken ct)
    {
        var result = await _tracking.RefreshAsync(uuid, ct: ct);
        return result is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<TrackingRefreshModel>.Ok(result));
    }

    /// <summary>What the carrier has reported, latest first — from webhooks and the poll.</summary>
    [RequirePermission(PermissionCodes.DELIVERY_VIEW)]
    [HttpGet("{uuid:guid}/tracking")]
    public async Task<IActionResult> GetTracking(Guid uuid)
    {
        var timeline = await _tracking.GetTimelineAsync(uuid);
        return timeline is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<IReadOnlyList<ConsignmentTrackingEventModel>>.Ok(timeline));
    }

    [RequirePermission(PermissionCodes.DELIVERY_EDIT)]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateConsignmentRequest req)
    {
        var uuid = await _svc.CreateAsync(req, User.GetUserId());
        return Ok(ApiResponse<Guid>.Ok(uuid, StaticResponseMessage.recordCreatedSuccessfully));
    }

    [RequirePermission(PermissionCodes.DELIVERY_VIEW)]
    [HttpGet("{uuid:guid}")]
    public async Task<IActionResult> GetById(Guid uuid)
    {
        var detail = await _svc.GetByUuidAsync(uuid);
        return detail is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<ConsignmentDetailModel>.Ok(detail));
    }

    /// <summary>
    /// What this consignment is charged on, package by package: actual weight, volumetric weight,
    /// and which of the two the carrier's service will bill.
    /// </summary>
    /// <remarks>
    /// Reads only. The figures are worked out fresh from the packages and the service's current
    /// terms, and nothing is written — a screen must not change what it is showing.
    /// </remarks>
    [RequirePermission(PermissionCodes.SHIPMENT_RATE_VIEW)]
    [HttpGet("{uuid:guid}/chargeable-weight")]
    public async Task<IActionResult> GetChargeableWeight(Guid uuid, CancellationToken ct)
    {
        var weight = await _weights.GetAsync(uuid, ct);
        return weight is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<ConsignmentWeightModel>.Ok(weight));
    }

    /// <summary>
    /// Works the same figures out and writes them onto the packages, along with the divisor that
    /// produced each one, so a quote made from them can still be explained after the service's
    /// terms change.
    /// </summary>
    /// <remarks>Gated by <c>DELIVERY_EDIT</c> rather than the rating permission: it writes.</remarks>
    [RequirePermission(PermissionCodes.DELIVERY_EDIT)]
    [HttpPost("{uuid:guid}/chargeable-weight")]
    public async Task<IActionResult> RecalculateChargeableWeight(Guid uuid, CancellationToken ct)
    {
        var weight = await _weights.RecalculateAsync(uuid, User.GetUserId(), ct);
        return weight is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<ConsignmentWeightModel>.Ok(weight));
    }

    /// <summary>What this consignment costs to move, and where that figure came from.</summary>
    [RequirePermission(PermissionCodes.SHIPMENT_RATE_VIEW)]
    [HttpGet("{uuid:guid}/rate")]
    public async Task<IActionResult> GetRate(Guid uuid, CancellationToken ct)
    {
        var rate = await _rating.GetAsync(uuid, ct);
        return rate is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<ConsignmentRateModel>.Ok(rate));
    }

    /// <summary>
    /// Prices it and moves it to RATED. The carrier's own rate API is asked first; the rate card
    /// answers when the carrier will not, cannot, or does not reply.
    /// </summary>
    /// <remarks>
    /// Both attempts come back in the response whichever succeeded — "the carrier quoted this" and
    /// "the carrier would not answer, so the card was used" are different facts about the same
    /// number, and only one of them is worth chasing.
    /// </remarks>
    [RequirePermission(PermissionCodes.DELIVERY_EDIT)]
    [HttpPost("{uuid:guid}/rate")]
    public async Task<IActionResult> Rate(
        Guid uuid, [FromBody] RateConsignmentRequest? req, CancellationToken ct)
    {
        var rate = await _rating.RateAsync(uuid, req ?? new RateConsignmentRequest(), User.GetUserId(), ct);
        return rate is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<ConsignmentRateModel>.Ok(rate, "Consignment rated."));
    }

    /// <summary>Records a price obtained outside the system — emailed, or quoted by telephone.</summary>
    [RequirePermission(PermissionCodes.DELIVERY_EDIT)]
    [HttpPost("{uuid:guid}/rate/manual")]
    public async Task<IActionResult> SetManualRate(
        Guid uuid, [FromBody] ManualRateRequest req, CancellationToken ct)
    {
        var rate = await _rating.SetManualRateAsync(uuid, req, User.GetUserId(), ct);
        return rate is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<ConsignmentRateModel>.Ok(rate, "Freight cost recorded."));
    }

    /// <summary>
    /// What every carrier would charge for this consignment, ranked — with the reasoning on each
    /// option, and anything a deadline ruled out listed separately rather than dropped.
    /// </summary>
    /// <remarks>Read-only: it compares prices, it does not choose one.</remarks>
    [RequirePermission(PermissionCodes.SHIPMENT_RATE_VIEW)]
    [HttpPost("{uuid:guid}/rate/shop")]
    public async Task<IActionResult> ShopRates(
        Guid uuid, [FromBody] RateShopRequest? req, CancellationToken ct)
    {
        var shop = await _shopping.ShopAsync(uuid, req ?? new RateShopRequest(), ct);
        return shop is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<RateShopResultModel>.Ok(shop));
    }

    /// <summary>
    /// Takes one of the shopped options: points the consignment at that carrier, account and
    /// service, and rates it.
    /// </summary>
    /// <remarks>
    /// The price is re-quoted rather than taken from the request body. A figure that went out to a
    /// browser and came back changed is not a price any carrier gave — and this is the moment it
    /// would become what an invoice gets reconciled against.
    /// </remarks>
    [RequirePermission(PermissionCodes.DELIVERY_EDIT)]
    [HttpPost("{uuid:guid}/rate/accept")]
    public async Task<IActionResult> AcceptRate(
        Guid uuid, [FromBody] AcceptRateRequest req, CancellationToken ct)
    {
        var rate = await _shopping.AcceptAsync(uuid, req, User.GetUserId(), ct);
        return rate is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<ConsignmentRateModel>.Ok(rate, "Rate accepted."));
    }

    /// <summary>Discards the quote and drops back to DRAFT.</summary>
    [RequirePermission(PermissionCodes.DELIVERY_EDIT)]
    [HttpDelete("{uuid:guid}/rate")]
    public async Task<IActionResult> ClearRate(Guid uuid, CancellationToken ct)
    {
        var cleared = await _rating.ClearAsync(uuid, User.GetUserId(), ct);
        return cleared
            ? Ok(ApiResponse.Ok("Quote discarded."))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    /// <summary>Loads another delivery onto this consignment.</summary>
    [RequirePermission(PermissionCodes.DELIVERY_EDIT)]
    [HttpPost("{uuid:guid}/deliveries/{deliveryUuid:guid}")]
    public async Task<IActionResult> AttachDelivery(Guid uuid, Guid deliveryUuid)
    {
        var attached = await _svc.AttachDeliveryAsync(uuid, deliveryUuid, User.GetUserId());
        return attached
            ? Ok(ApiResponse.Ok("Delivery added to consignment."))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    /// <summary>
    /// Records an airway bill obtained from the carrier outside the system and marks the
    /// consignment booked. For carriers with no API integration — which is most of them.
    /// </summary>
    /// <remarks>Booking commits money to a carrier, so it is gated separately from editing.</remarks>
    [RequirePermission(PermissionCodes.SHIPMENT_BOOK)]
    [HttpPost("{uuid:guid}/book-manual")]
    public async Task<IActionResult> BookManually(Guid uuid, [FromBody] ManualBookingRequest req)
    {
        var booked = await _svc.BookManuallyAsync(uuid, req, User.GetUserId());
        return booked
            ? Ok(ApiResponse.Ok("Consignment booked."))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    /// <summary>
    /// Books the consignment through its carrier's API. Returns <b>202</b>: the carrier call happens
    /// in the background — poll <c>GET booking</c> for the outcome.
    /// </summary>
    /// <remarks>
    /// Asking again while a booking is under way sends nothing twice; it returns the current status.
    /// </remarks>
    [RequirePermission(PermissionCodes.SHIPMENT_BOOK)]
    [HttpPost("{uuid:guid}/book")]
    public async Task<IActionResult> Book(Guid uuid, [FromBody] BookConsignmentRequest? req)
    {
        var status = await _booking.RequestBookingAsync(uuid, req ?? new BookConsignmentRequest(), User.GetUserId());
        return status is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Accepted(ApiResponse<ConsignmentBookingStatusModel>.Ok(status, "Booking requested."));
    }

    /// <summary>Where the booking stands, and whether a person needs to act.</summary>
    [RequirePermission(PermissionCodes.DELIVERY_VIEW)]
    [HttpGet("{uuid:guid}/booking")]
    public async Task<IActionResult> GetBooking(Guid uuid)
    {
        var status = await _booking.GetStatusAsync(uuid);
        return status is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<ConsignmentBookingStatusModel>.Ok(status));
    }

    /// <summary>
    /// The shipping label for the consignment's current airway bill, served inline so a browser
    /// opens it straight to print. Fetched from the carrier and stored on first request.
    /// </summary>
    /// <remarks>
    /// Gated by <c>SHIPMENT_BOOK</c>: whoever ships the parcel prints its label, and a label is the
    /// recipient's name, phone and address — not something every viewer of a delivery needs.
    /// <para>409 when there is no label to give (not booked, cancelled, carrier issues its own);
    /// 502 when the carrier could not supply one right now.</para>
    /// </remarks>
    [RequirePermission(PermissionCodes.SHIPMENT_BOOK)]
    [HttpGet("{uuid:guid}/label")]
    public async Task<IActionResult> GetLabel(Guid uuid, CancellationToken ct)
    {
        ConsignmentLabelFile? label;

        try
        {
            label = await _labels.GetLabelAsync(uuid, User.GetUserId(), ct);
        }
        catch (CarrierUnavailableException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, ApiResponse.Fail(ex.Message));
        }

        if (label is null)
            return NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));

        // Personal data: never cached by a browser or proxy, never sniffed into another type.
        Response.Headers.CacheControl        = "no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.ContentDisposition  =
            new ContentDispositionHeaderValue("inline") { FileNameStar = label.FileName }.ToString();

        return File(label.Content, label.ContentType);
    }

    /// <summary>
    /// Settles a booking whose outcome is unknown, once someone has checked with the carrier.
    /// </summary>
    /// <remarks>Gated like booking itself: confirming a booking commits the same money.</remarks>
    [RequirePermission(PermissionCodes.SHIPMENT_BOOK)]
    [HttpPost("{uuid:guid}/booking/resolve")]
    public async Task<IActionResult> ResolveBooking(Guid uuid, [FromBody] ResolveBookingRequest req)
    {
        var status = await _booking.ResolveAsync(uuid, req, User.GetUserId());
        return status is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<ConsignmentBookingStatusModel>.Ok(status, "Booking resolved."));
    }
}

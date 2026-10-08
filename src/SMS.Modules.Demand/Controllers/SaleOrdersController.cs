using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;
using SMS.WorkflowEngine.Models;

namespace SMS.Modules.Demand.Controllers;

// A29-P3-07 §4.5. /confirm and /cancel are bare status transitions — §4.3's availability check,
// reservation and back-to-back PO creation are not implemented here (see SaleOrderService.ConfirmAsync's
// own remarks); TC-04/TC-05, which exercise that behaviour, cannot be fully verified against this
// controller alone yet.
[ApiController]
[Route("api/sale-orders")]
[RequiresFeature("MODULE_DEMAND")]
public partial class SaleOrdersController : ControllerBase
{
    private readonly ISaleOrderService _service;
    private readonly ISaleOrderReservationService _reservations;

    public SaleOrdersController(ISaleOrderService service, ISaleOrderReservationService reservations)
    {
        _service      = service;
        _reservations = reservations;
    }

    [HttpPost]
    [RequirePermission(PermissionCodes.SALE_ORDER_CREATE)]
    public async Task<IActionResult> Create([FromBody] CreateSaleOrderRequest req)
    {
        var uuid = await _service.CreateAsync(req, User.GetUserId());

        // A32 BR-C3-05 — a customer PO already on another order is a warning in the message, never a refusal.
        var message = StaticResponseMessage.recordCreatedSuccessfully;
        if (!string.IsNullOrWhiteSpace(req.CustomerPoReference))
        {
            var duplicates = await _service.FindCustomerPoDuplicatesAsync(req.CustomerPoReference, excludeUuid: uuid);
            if (duplicates.Count > 0)
                message += $" Warning: customer PO {req.CustomerPoReference.Trim()} is already on {string.Join(", ", duplicates.Select(d => d.SoNumber))}.";
        }

        return Ok(ApiResponse<Guid>.Ok(uuid, message));
    }

    [HttpPut("{uuid:guid}")]
    [RequirePermission(PermissionCodes.SALE_ORDER_EDIT)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] UpdateSaleOrderRequest req)
    {
        var updated = await _service.UpdateAsync(uuid, req, User.GetUserId());
        return updated
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    [HttpGet("defaults")]
    [RequirePermission(PermissionCodes.SALE_ORDER_VIEW)]
    public async Task<IActionResult> GetDefaults() =>
        Ok(ApiResponse<SaleOrderDefaultsModel>.Ok(await _service.GetDefaultsAsync()));

    [HttpGet("{uuid:guid}")]
    [RequirePermission(PermissionCodes.SALE_ORDER_VIEW)]
    public async Task<IActionResult> GetById(Guid uuid)
    {
        var result = await _service.GetByIdAsync(uuid);
        return result is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<SaleOrderModel>.Ok(result));
    }

    [HttpGet]
    [RequirePermission(PermissionCodes.SALE_ORDER_VIEW)]
    public async Task<IActionResult> GetList([FromQuery] SaleOrderListFilter filter)
    {
        var result = await _service.GetListAsync(filter);
        return Ok(ApiResponse<PaginatedResponse<SaleOrderModel>>.Ok(result));
    }

    [HttpPost("{uuid:guid}/confirm")]
    [RequirePermission(PermissionCodes.SALE_ORDER_CONFIRM)]
    public async Task<IActionResult> Confirm(Guid uuid)
    {
        // A33 — 400 with every route blocker when the gate refuses; the result lists the deliveries created (D-1).
        var confirmed = await _service.ConfirmWithResultAsync(uuid, User.GetUserId());
        return confirmed is not null
            ? Ok(ApiResponse<SaleOrderConfirmResultModel>.Ok(confirmed, "Sale order confirmed."))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    [HttpPost("{uuid:guid}/cancel")]
    [RequirePermission(PermissionCodes.SALE_ORDER_CANCEL)]
    public async Task<IActionResult> Cancel(Guid uuid, [FromBody] CancelSaleOrderRequest? req)
    {
        // A33 D-15 — the result lists the deliveries cancelled with it and the issued ones left standing.
        var cancelled = await _service.CancelWithResultAsync(uuid, User.GetUserId(), req?.Reason);
        return cancelled is not null
            ? Ok(ApiResponse<SaleOrderCancelResultModel>.Ok(cancelled, "Sale order cancelled."))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    /// <summary>A33 BR-C3-05 — how a saved order's lines would be split into deliveries (route × warehouse). Persists nothing.</summary>
    [HttpGet("{uuid:guid}/delivery-preview")]
    [RequirePermission(PermissionCodes.SALE_ORDER_VIEW)]
    public async Task<IActionResult> GetDeliveryPreview(Guid uuid)
    {
        var preview = await _service.GetDeliveryPreviewAsync(uuid);
        return preview is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<SaleOrderDeliveryPreviewModel>.Ok(preview));
    }

    /// <summary>A33 BR-C3-05 — the same for the unsaved form; drives its Route column, preview panel and confirm blockers.</summary>
    [HttpPost("delivery-preview")]
    [RequirePermission(PermissionCodes.SALE_ORDER_CREATE, PermissionCodes.SALE_ORDER_EDIT, PermissionCodes.SALE_ORDER_VIEW)]
    public async Task<IActionResult> PreviewDeliveries([FromBody] SaleOrderDeliveryPreviewRequest req) =>
        Ok(ApiResponse<SaleOrderDeliveryPreviewModel>.Ok(await _service.PreviewDeliveriesAsync(req)));

    /// <summary>
    /// A33 — change one DRAFT line's route only (null = inherit): no re-pricing, unlike the full PUT. Returns the line
    /// with its effective route, source and blocker.
    /// </summary>
    [HttpPut("{uuid:guid}/lines/{lineUuid:guid}/fulfillment-route")]
    [RequirePermission(PermissionCodes.SALE_ORDER_EDIT)]
    public async Task<IActionResult> UpdateLineRoute(Guid uuid, Guid lineUuid, [FromBody] UpdateSaleOrderLineRouteRequest? req)
    {
        var line = await _service.UpdateLineRouteAsync(uuid, lineUuid, req?.FulfillmentRouteUuid, User.GetUserId());
        return line is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<SaleOrderLineModel>.Ok(line, StaticResponseMessage.recordUpdatedSuccessfully));
    }

    /// <summary>A34 D-16 — calculate one DRAFT line's lead time and store it (API-CONTRACT §5.2).</summary>
    [HttpPost("{uuid:guid}/lines/{lineUuid:guid}/lead-time")]
    [RequirePermission(PermissionCodes.SALE_ORDER_EDIT)]
    public async Task<IActionResult> CalculateLineLeadTime(Guid uuid, Guid lineUuid)
    {
        var result = await _service.CalculateLineLeadTimeAsync(uuid, lineUuid, User.GetUserId());
        return result is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<SaleLineLeadTimeModel<SaleOrderLineModel>>.Ok(result, "Lead time calculated."));
    }

    /// <summary>A34 D-16 — set or clear one line's manual delivery date (DRAFT / CONFIRMED / PARTIALLY_FULFILLED), no re-pricing.</summary>
    [HttpPut("{uuid:guid}/lines/{lineUuid:guid}/delivery-date")]
    [RequirePermission(PermissionCodes.SALE_ORDER_EDIT)]
    public async Task<IActionResult> UpdateLineDeliveryDate(Guid uuid, Guid lineUuid, [FromBody] UpdateSaleOrderLineDeliveryDateRequest? req)
    {
        var result = await _service.UpdateLineDeliveryDateAsync(uuid, lineUuid, req?.ManualDeliveryDate, User.GetUserId());
        return result is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<SaleOrderLineDeliveryDateResultModel>.Ok(result, result.Warning ?? StaticResponseMessage.recordUpdatedSuccessfully));
    }

    /// <summary>A34 D-17 — create (idempotently) and plan the make-to-order production orders of a confirmed order.</summary>
    [HttpPost("{uuid:guid}/create-production-orders")]
    [RequirePermission(PermissionCodes.SALE_ORDER_CONFIRM, PermissionCodes.PROD_CREATE)]
    public async Task<IActionResult> CreateProductionOrders(Guid uuid)
    {
        var result = await _service.CreateProductionOrdersAsync(uuid, User.GetUserId());
        return result is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<SaleOrderProductionCreationResultModel>.Ok(result,
                result.ProductionCreationFailed ? result.ProductionMessage ?? "Production orders could not be created." : "Production orders created."));
    }

    [HttpGet("{uuid:guid}/timeline")]
    [RequirePermission(PermissionCodes.SALE_ORDER_VIEW)]
    public async Task<IActionResult> GetTimeline(Guid uuid)
    {
        var timeline = await _service.GetTimelineAsync(uuid);
        return timeline is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<TimelineDetail>.Ok(timeline));
    }

    // A preview of what §4.3's confirm would see right now — reserves nothing.
    [HttpGet("{uuid:guid}/availability")]
    [RequirePermission(PermissionCodes.SALE_ORDER_VIEW)]
    public async Task<IActionResult> GetAvailability(Guid uuid)
    {
        var availability = await _service.GetAvailabilityAsync(uuid);
        return availability is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<IReadOnlyList<SaleOrderLineAvailabilityModel>>.Ok(availability));
    }
}

// A32 C3/C4 — the customer's PO and manual reservation (docs/sales-preorder/API-CONTRACT.md §7). The buttons on the
// order page ask for exactly these permissions; reserve/release are disabled, not hidden, without them (BR-C4-08).
public partial class SaleOrdersController
{
    /// <summary>PD-05 — set or replace the customer PO reference, date and linked CUSTOMER_PO file, at any status but CANCELLED/CLOSED.</summary>
    [HttpPut("{uuid:guid}/customer-po")]
    [RequirePermission(PermissionCodes.SALE_ORDER_EDIT)]
    public async Task<IActionResult> UpdateCustomerPo(Guid uuid, [FromBody] UpdateSaleOrderCustomerPoRequest req) =>
        await _service.UpdateCustomerPoAsync(uuid, req, User.GetUserId())
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));

    /// <summary>BR-C3-05 — the organization's other orders with this customer PO reference: a warning to show, never a block.</summary>
    [HttpGet("customer-po-check")]
    [RequirePermission(PermissionCodes.SALE_ORDER_VIEW, PermissionCodes.SALE_ORDER_CREATE, PermissionCodes.SALE_ORDER_EDIT)]
    public async Task<IActionResult> CheckCustomerPo([FromQuery] string? reference, [FromQuery] Guid? excludeUuid) =>
        Ok(ApiResponse<IReadOnlyList<CustomerPoDuplicateModel>>.Ok(
            await _service.FindCustomerPoDuplicatesAsync(reference ?? string.Empty, excludeUuid)));

    /// <summary>PE-06 — hold stock for one line. Always 200 with an outcome (RESERVED, PARTIAL, NEEDS_CONFIRMATION, NONE_AVAILABLE).</summary>
    [HttpPost("{uuid:guid}/lines/{lineUuid:guid}/reserve")]
    [RequirePermission(PermissionCodes.SALE_ORDER_RESERVE)]
    public async Task<IActionResult> ReserveLine(Guid uuid, Guid lineUuid, [FromBody] ReserveSaleOrderLineRequest? req)
    {
        var result = await _reservations.ReserveLineAsync(uuid, lineUuid, req ?? new ReserveSaleOrderLineRequest(), User.GetUserId());
        return result is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<SaleOrderLineReservationModel>.Ok(result, result.Message ?? "Reservation updated."));
    }

    /// <summary>PE-06 — give back stock held for one line (all of it, unless a quantity is given).</summary>
    [HttpPost("{uuid:guid}/lines/{lineUuid:guid}/release")]
    [RequirePermission(PermissionCodes.SALE_ORDER_RELEASE_RESERVATION)]
    public async Task<IActionResult> ReleaseLine(Guid uuid, Guid lineUuid, [FromBody] ReleaseSaleOrderLineRequest? req)
    {
        var result = await _reservations.ReleaseLineAsync(uuid, lineUuid, req ?? new ReleaseSaleOrderLineRequest(), User.GetUserId());
        return result is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<SaleOrderLineReservationModel>.Ok(result, "Reservation released."));
    }

    /// <summary>PE-06 — hold stock for every line that can still take it (partial by default).</summary>
    [HttpPost("{uuid:guid}/reserve-all")]
    [RequirePermission(PermissionCodes.SALE_ORDER_RESERVE)]
    public async Task<IActionResult> ReserveAll(Guid uuid, [FromBody] ReserveAllSaleOrderLinesRequest? req)
    {
        var result = await _reservations.ReserveAllAsync(uuid, req ?? new ReserveAllSaleOrderLinesRequest(), User.GetUserId());
        return result is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<SaleOrderReserveAllModel>.Ok(result, "Reservations updated."));
    }
}

public class CancelSaleOrderRequest
{
    public string? Reason { get; set; }
}

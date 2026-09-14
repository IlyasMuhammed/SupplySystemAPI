using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Warehouse.Models;
using SMS.Modules.Warehouse.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Warehouse.Controllers;

[ApiController]
[Route("api/sros")]
[RequiresFeature("MODULE_WAREHOUSE")]
public class SrosController : ControllerBase
{
    private readonly ISroService          _service;
    private readonly INotificationService _notif;

    public SrosController(ISroService service, INotificationService notif)
    {
        _service = service;
        _notif   = notif;
    }

    // REQ-3.x — same pattern as QuotationsController.RequestOrigin: the browser's actual Origin is
    // more reliable than a static config value that has to be kept in sync by hand. Falls back to
    // Referer's scheme+host; DispatchAsync itself falls back to AppSettings:BaseUrl if this is null too.
    private string? RequestOrigin
    {
        get
        {
            var origin = Request.Headers.Origin.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(origin)) return origin;

            var referer = Request.Headers.Referer.FirstOrDefault();
            return Uri.TryCreate(referer, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : null;
        }
    }

    [HttpPost]
    public async Task<IActionResult> CreateSro([FromBody] CreateSroRequest req)
    {
        var uuid    = await _service.CreateAsync(req, User.GetUserId());
        var actorId = User.GetUserId();
        await _notif.TryCreateAsync(new NotificationRequest(
            UserId: actorId, Type: "SRO_CREATED", Title: "Return Order Created",
            Message: $"Supplier return order has been created and is pending approval.",
            Category: "Warehouse", EntityType: "SRO", EntityUuid: uuid.ToString(),
            NavigationUrl: $"/portal/pages/warehouse/sro/{uuid}",
            CreatedBy: actorId));
        return Ok(ApiResponse<Guid>.Ok(uuid, StaticResponseMessage.recordCreatedSuccessfully));
    }

    [HttpGet]
    public async Task<IActionResult> GetSros([FromQuery] SroListFilter filter)
    {
        var result = await _service.GetListAsync(filter);
        return Ok(ApiResponse<PaginatedResponse<SroListItemModel>>.Ok(result));
    }

    [HttpGet("{uuid:guid}")]
    public async Task<IActionResult> GetSroById(Guid uuid)
    {
        var detail = await _service.GetByIdAsync(uuid);
        return detail is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<SroDetailModel>.Ok(detail));
    }

    [HttpPost("{uuid:guid}/approve")]
    public async Task<IActionResult> ApproveSro(Guid uuid, [FromBody] ApproveSroRequest req)
    {
        await _service.ApproveAsync(uuid, req, User.GetUserId());
        var sro     = await _service.GetByIdAsync(uuid);
        var actorId = User.GetUserId();
        if (sro is not null)
            await _notif.TryCreateAsync(new NotificationRequest(
                UserId: sro.CreatedBy, Type: "SRO_APPROVED", Title: "Return Order Approved",
                Message: $"Supplier return order {sro.SroNumber} has been approved.",
                Category: "Warehouse", EntityType: "SRO", EntityUuid: uuid.ToString(),
                NavigationUrl: $"/portal/pages/warehouse/sro/{uuid}",
                CreatedBy: actorId, SendEmail: true));
        return Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully));
    }

    [HttpPost("{uuid:guid}/reject")]
    public async Task<IActionResult> RejectSro(Guid uuid, [FromBody] RejectSroRequest req)
    {
        await _service.RejectAsync(uuid, req, User.GetUserId());
        var sro     = await _service.GetByIdAsync(uuid);
        var actorId = User.GetUserId();
        if (sro is not null)
            await _notif.TryCreateAsync(new NotificationRequest(
                UserId: sro.CreatedBy, Type: "SRO_REJECTED", Title: "Return Order Rejected",
                Message: $"Supplier return order {sro.SroNumber} has been rejected. Reason: {req.Reason}",
                Category: "Warehouse", EntityType: "SRO", EntityUuid: uuid.ToString(),
                NavigationUrl: $"/portal/pages/warehouse/sro/{uuid}",
                CreatedBy: actorId, SendEmail: true));
        return Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully));
    }

    [HttpPost("{uuid:guid}/dispatch")]
    public async Task<IActionResult> DispatchSro(Guid uuid, [FromBody] DispatchSroRequest req)
    {
        await _service.DispatchAsync(uuid, req, User.GetUserId(), RequestOrigin);
        var sro     = await _service.GetByIdAsync(uuid);
        var actorId = User.GetUserId();
        if (sro is not null)
            await _notif.TryCreateAsync(new NotificationRequest(
                UserId: sro.CreatedBy, Type: "SRO_DISPATCHED", Title: "Return Order Dispatched",
                Message: $"Supplier return order {sro.SroNumber} has been dispatched to the supplier.",
                Category: "Warehouse", EntityType: "SRO", EntityUuid: uuid.ToString(),
                NavigationUrl: $"/portal/pages/warehouse/sro/{uuid}",
                CreatedBy: actorId));
        return Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully));
    }

    [HttpPost("{uuid:guid}/confirm-receipt")]
    public async Task<IActionResult> ConfirmReceipt(Guid uuid, [FromBody] ConfirmReceiptSroRequest req)
    {
        await _service.ConfirmReceiptAsync(uuid, req, User.GetUserId());
        var sro     = await _service.GetByIdAsync(uuid);
        var actorId = User.GetUserId();
        if (sro is not null)
            await _notif.TryCreateAsync(new NotificationRequest(
                UserId: sro.CreatedBy, Type: "SRO_RECEIVED", Title: "Return Received by Supplier",
                Message: $"Supplier has confirmed receipt of returned goods for {sro.SroNumber}.",
                Category: "Warehouse", EntityType: "SRO", EntityUuid: uuid.ToString(),
                NavigationUrl: $"/portal/pages/warehouse/sro/{uuid}",
                CreatedBy: actorId));
        return Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully));
    }

    [HttpPost("{uuid:guid}/resolve")]
    public async Task<IActionResult> ResolveSro(Guid uuid, [FromBody] ResolveSroRequest req)
    {
        await _service.ResolveAsync(uuid, req, User.GetUserId());
        var sro     = await _service.GetByIdAsync(uuid);
        var actorId = User.GetUserId();
        if (sro is not null)
            await _notif.TryCreateAsync(new NotificationRequest(
                UserId: sro.CreatedBy, Type: "SRO_RESOLVED", Title: "Return Order Resolved",
                Message: $"Supplier return order {sro.SroNumber} has been resolved ({req.ResolutionType}).",
                Category: "Warehouse", EntityType: "SRO", EntityUuid: uuid.ToString(),
                NavigationUrl: $"/portal/pages/warehouse/sro/{uuid}",
                CreatedBy: actorId, SendEmail: true));
        return Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully));
    }

    [HttpPost("{uuid:guid}/escalate")]
    public async Task<IActionResult> EscalateSro(Guid uuid, [FromBody] EscalateSroRequest req)
    {
        await _service.EscalateAsync(uuid, req, User.GetUserId());
        var sro     = await _service.GetByIdAsync(uuid);
        var actorId = User.GetUserId();
        if (sro is not null)
            await _notif.TryCreateAsync(new NotificationRequest(
                UserId: sro.CreatedBy, Type: "SRO_ESCALATED", Title: "Return Order Escalated",
                Message: $"Supplier return order {sro.SroNumber} has been escalated. Reason: {req.Reason}",
                Category: "Warehouse", EntityType: "SRO", EntityUuid: uuid.ToString(),
                NavigationUrl: $"/portal/pages/warehouse/sro/{uuid}",
                CreatedBy: actorId, SendEmail: true));
        return Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully));
    }

    [HttpPost("{uuid:guid}/expect-replacement")]
    public async Task<IActionResult> ExpectReplacement(Guid uuid)
    {
        await _service.ExpectReplacementAsync(uuid, User.GetUserId());
        var sro     = await _service.GetByIdAsync(uuid);
        var actorId = User.GetUserId();
        if (sro is not null)
            await _notif.TryCreateAsync(new NotificationRequest(
                UserId: sro.CreatedBy, Type: "SRO_AWAITING_REPLACEMENT", Title: "Awaiting Replacement Delivery",
                Message: $"Supplier return order {sro.SroNumber} is now awaiting replacement delivery from supplier.",
                Category: "Warehouse", EntityType: "SRO", EntityUuid: uuid.ToString(),
                NavigationUrl: $"/portal/pages/warehouse/sro/{uuid}",
                CreatedBy: actorId));
        return Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully));
    }
}

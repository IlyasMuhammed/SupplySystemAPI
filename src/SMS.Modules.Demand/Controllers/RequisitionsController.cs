using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Demand.Controllers;

[ApiController]
[Route("api/requisitions")]
[RequiresFeature("MODULE_DEMAND")]
public class RequisitionsController : ControllerBase
{
    private readonly IRequisitionService   _service;
    private readonly IPurchaseOrderService _poService;
    private readonly INotificationService  _notif;
    private readonly IAuditService         _audit;

    public RequisitionsController(IRequisitionService service, IPurchaseOrderService poService, INotificationService notif, IAuditService audit)
    {
        _service   = service;
        _poService = poService;
        _notif     = notif;
        _audit     = audit;
    }

    private string Ip => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";

    [HttpPost]
    public async Task<IActionResult> CreateRequisition([FromBody] CreatePrRequest req)
    {
        var uuid = await _service.CreateAsync(req, User.GetUserId());
        await _audit.LogAsync(User.GetUserId(), null, "Demand", "CREATE", "PurchaseRequisition", uuid, Ip);
        return Ok(ApiResponse<Guid>.Ok(uuid, StaticResponseMessage.recordCreatedSuccessfully));
    }

    [HttpGet]
    public async Task<IActionResult> GetRequisitions(
        [FromQuery] string? status,
        [FromQuery] string? department,
        [FromQuery] int? requesterId,
        [FromQuery] DateTime? dateFrom,
        [FromQuery] DateTime? dateTo,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var filter = new PrListFilter
        {
            Status = status,
            Department = department,
            RequesterId = requesterId,
            DateFrom = dateFrom,
            DateTo = dateTo,
            Search = search,
            Page = page,
            PageSize = pageSize
        };
        var result = await _service.GetListAsync(filter);
        return Ok(ApiResponse<PaginatedResponse<PrListItemModel>>.Ok(result));
    }

    [HttpGet("{uuid:guid}")]
    public async Task<IActionResult> GetRequisitionById(Guid uuid)
    {
        var detail = await _service.GetByIdAsync(uuid);
        return detail is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<PrDetailModel>.Ok(detail));
    }

    [HttpPatch("{uuid:guid}")]
    public async Task<IActionResult> PatchRequisition(Guid uuid, [FromBody] PatchPrRequest req)
    {
        await _service.UpdateAsync(uuid, req, User.GetUserId());
        await _audit.LogAsync(User.GetUserId(), null, "Demand", "UPDATE", "PurchaseRequisition", uuid, Ip);
        return Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully));
    }

    [HttpPost("{uuid:guid}/approve")]
    public async Task<IActionResult> ApproveRequisition(Guid uuid)
    {
        await _service.ApproveAsync(uuid, User.GetUserId());
        await _audit.LogAsync(User.GetUserId(), null, "Demand", "APPROVE", "PurchaseRequisition", uuid, Ip);
        return Ok(ApiResponse.Ok("Requisition approved."));
    }

    [HttpPost("{uuid:guid}/reject")]
    public async Task<IActionResult> RejectRequisition(Guid uuid, [FromBody] RejectRequisitionRequest req)
    {
        await _service.RejectAsync(uuid, User.GetUserId(), req.RejectionReason);
        await _audit.LogAsync(User.GetUserId(), null, "Demand", "REJECT", "PurchaseRequisition", uuid, Ip, notes: req.RejectionReason);
        return Ok(ApiResponse.Ok("Requisition rejected."));
    }

    [HttpPost("{uuid:guid}/submit")]
    public async Task<IActionResult> SubmitRequisition(Guid uuid)
    {
        await _service.SubmitAsync(uuid, User.GetUserId());
        await _audit.LogAsync(User.GetUserId(), null, "Demand", "SUBMIT", "PurchaseRequisition", uuid, Ip);
        var pr      = await _service.GetByIdAsync(uuid);
        var actorId = User.GetUserId();
        if (pr is not null)
            await _notif.TryCreateAsync(new NotificationRequest(
                UserId: actorId, Type: "PR_SUBMITTED", Title: "Requisition Submitted",
                Message: $"Your purchase requisition {pr.PrNumber} has been submitted for approval.",
                Category: "Procurement", EntityType: "PR", EntityUuid: uuid.ToString(),
                NavigationUrl: $"/portal/pages/demand/requisitions/{uuid}",
                CreatedBy: actorId));
        return Ok(ApiResponse.Ok("Requisition submitted for approval."));
    }

    [HttpPost("{uuid:guid}/convert")]
    public async Task<IActionResult> ConvertRequisition(Guid uuid, [FromBody] ConvertPrToPoRequest req)
    {
        var converted = await _poService.CreateFromPrWithSummaryAsync(uuid, req, User.GetUserId());
        var poUuid  = converted.PoUuids[0];
        var pr      = await _service.GetByIdAsync(uuid);
        var actorId = User.GetUserId();
        await _audit.LogAsync(actorId, null, "Demand", "CONVERT", "PurchaseRequisition", uuid, Ip, notes: $"Converted to PO {poUuid}");
        if (pr is not null)
            await _notif.TryCreateAsync(new NotificationRequest(
                UserId: pr.RequesterId, Type: "PR_CONVERTED", Title: "Requisition Converted to PO",
                Message: $"Your requisition {pr.PrNumber} has been converted to a purchase order.",
                Category: "Procurement", EntityType: "PR", EntityUuid: uuid.ToString(),
                NavigationUrl: $"/portal/pages/demand/purchase-orders/{poUuid}",
                CreatedBy: actorId));
        // A46 C5 / BR-30: `result` stays the PO uuid; the batch pricing summary rides beside it.
        return Ok(PricedApiResponse<Guid>.Ok(poUuid, converted.Summary, "Requisition converted to purchase order."));
    }

    [HttpPost("{uuid:guid}/convert-split")]
    public async Task<IActionResult> ConvertRequisitionSplit(Guid uuid, [FromBody] ConvertPrSplitRequest req)
    {
        var converted = await _poService.CreateFromPrSplitWithSummaryAsync(uuid, req, User.GetUserId());
        var poUuids = converted.PoUuids.ToList();
        var pr      = await _service.GetByIdAsync(uuid);
        var actorId = User.GetUserId();
        if (pr is not null)
            await _notif.TryCreateAsync(new NotificationRequest(
                UserId: pr.RequesterId, Type: "PR_CONVERTED", Title: "Requisition Converted to POs",
                Message: $"Your requisition {pr.PrNumber} has been split into {poUuids.Count} purchase order(s).",
                Category: "Procurement", EntityType: "PR", EntityUuid: uuid.ToString(),
                NavigationUrl: $"/portal/pages/demand/requisitions/{uuid}",
                CreatedBy: actorId));
        return Ok(PricedApiResponse<List<Guid>>.Ok(poUuids, converted.Summary, $"{poUuids.Count} purchase order(s) created."));
    }
}

public record RejectRequisitionRequest(string RejectionReason);

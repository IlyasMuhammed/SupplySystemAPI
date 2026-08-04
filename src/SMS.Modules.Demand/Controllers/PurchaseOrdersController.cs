using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Demand.Controllers;

[ApiController]
[Route("api/purchase-orders")]
[RequiresFeature("MODULE_DEMAND")]
public class PurchaseOrdersController : ControllerBase
{
    private readonly IPurchaseOrderService _service;
    private readonly INotificationService  _notif;
    private readonly IAuditService         _audit;
    private readonly IPoDocumentService    _documentSvc;
    private readonly IUserQueryService     _userQuery;
    private readonly IEmailSender          _emailSender;
    private readonly ILogger<PurchaseOrdersController> _logger;

    public PurchaseOrdersController(
        IPurchaseOrderService service, INotificationService notif, IAuditService audit, IPoDocumentService documentSvc,
        IUserQueryService userQuery, IEmailSender emailSender, ILogger<PurchaseOrdersController> logger)
    {
        _service     = service;
        _notif       = notif;
        _audit       = audit;
        _documentSvc = documentSvc;
        _userQuery   = userQuery;
        _emailSender = emailSender;
        _logger      = logger;
    }

    private string Ip => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";

    [HttpPost]
    public async Task<IActionResult> CreatePurchaseOrder([FromBody] CreatePoRequest req)
    {
        var uuid    = await _service.CreateAsync(req, User.GetUserId());
        var actorId = User.GetUserId();
        await _audit.LogAsync(actorId, null, "Demand", "CREATE", "PurchaseOrder", uuid, Ip);
        await _notif.TryCreateAsync(new NotificationRequest(
            UserId: actorId, Type: "PO_CREATED", Title: "Purchase Order Created",
            Message: $"A new purchase order has been created successfully.",
            Category: "Procurement", EntityType: "PO", EntityUuid: uuid.ToString(),
            NavigationUrl: $"/portal/pages/demand/purchase-orders/{uuid}",
            CreatedBy: actorId));
        return Ok(ApiResponse<Guid>.Ok(uuid, StaticResponseMessage.recordCreatedSuccessfully));
    }

    [HttpGet]
    public async Task<IActionResult> GetPurchaseOrders([FromQuery] PoListFilter filter)
    {
        var result = await _service.GetListAsync(filter);
        return Ok(ApiResponse<PaginatedResponse<PoListItemModel>>.Ok(result));
    }

    [HttpGet("search")]
    public async Task<IActionResult> SearchPurchaseOrders([FromQuery] string? q, [FromQuery] string? status)
    {
        var receivableOnly = string.Equals(status, "receivable", StringComparison.OrdinalIgnoreCase)
                             && string.IsNullOrWhiteSpace(q);
        var result = await _service.SearchForGrnAsync(q, receivableOnly);
        return Ok(ApiResponse<List<PoSearchItemModel>>.Ok(result));
    }

    [HttpGet("{uuid:guid}")]
    public async Task<IActionResult> GetPurchaseOrderById(Guid uuid)
    {
        var detail = await _service.GetByIdAsync(uuid);
        return detail is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<PoDetailModel>.Ok(detail));
    }

    [HttpGet("{uuid:guid}/pdf")]
    public async Task<IActionResult> DownloadPdf(Guid uuid)
    {
        var bytes = await _documentSvc.GeneratePdfAsync(uuid);
        return File(bytes, "application/pdf", $"PO-{uuid}.pdf");
    }

    [HttpPatch("{uuid:guid}")]
    public async Task<IActionResult> UpdatePurchaseOrder(Guid uuid, [FromBody] PatchPoRequest req)
    {
        await _service.UpdateAsync(uuid, req, User.GetUserId());
        await _audit.LogAsync(User.GetUserId(), null, "Demand", "UPDATE", "PurchaseOrder", uuid, Ip);
        return Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully));
    }

    [HttpPost("{uuid:guid}/submit")]
    public async Task<IActionResult> SubmitPurchaseOrderForApproval(Guid uuid)
    {
        await _service.SubmitForApprovalAsync(uuid, User.GetUserId());
        var po      = await _service.GetByIdAsync(uuid);
        var actorId = User.GetUserId();
        await _audit.LogAsync(actorId, null, "Demand", "SUBMIT", "PurchaseOrder", uuid, Ip);
        if (po is not null)
            await _notif.TryCreateAsync(new NotificationRequest(
                UserId: po.CreatedBy, Type: "PO_SUBMITTED", Title: "Purchase Order Submitted for Approval",
                Message: $"Your purchase order {po.PoNumber} has been submitted for approval.",
                Category: "Procurement", EntityType: "PO", EntityUuid: uuid.ToString(),
                NavigationUrl: $"/portal/pages/demand/purchase-orders/{uuid}",
                CreatedBy: actorId));
        return Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully));
    }

    [HttpPost("{uuid:guid}/approve")]
    public async Task<IActionResult> ApprovePurchaseOrder(Guid uuid)
    {
        await _service.ApproveAsync(uuid, User.GetUserId());
        await _audit.LogAsync(User.GetUserId(), null, "Demand", "APPROVE", "PurchaseOrder", uuid, Ip);
        await EmailApprovedDocumentAsync(uuid);
        return Ok(ApiResponse.Ok("Purchase order approved."));
    }

    // Emails the generated PO letter to the PO's creator once it's finally approved — the same
    // recipient the "PO Created"/"PO Sent" in-app notifications already target. A failure here
    // must never fail the approval itself, since the approval has already been persisted.
    private async Task EmailApprovedDocumentAsync(Guid uuid)
    {
        try
        {
            var po = await _service.GetByIdAsync(uuid);
            if (po is null) return;

            var email = await _userQuery.GetUserEmailAsync(po.CreatedBy);
            if (string.IsNullOrWhiteSpace(email)) return;

            var pdfBytes = await _documentSvc.GeneratePdfAsync(uuid);
            var body = $"<p>Purchase order <strong>{po.PoNumber}</strong> has been approved. " +
                       $"The official purchase order document is attached.</p>";

            await _emailSender.SendAsync(
                email, $"Purchase Order Approved — {po.PoNumber}", body,
                new[] { new EmailAttachment($"PO-{po.PoNumber}.pdf", pdfBytes, "application/pdf") });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to email the approved PO document for {PoUuid} — approval itself already committed successfully.", uuid);
        }
    }

    [HttpPost("{uuid:guid}/reject")]
    public async Task<IActionResult> RejectPurchaseOrder(Guid uuid, [FromBody] RejectPoRequest req)
    {
        await _service.RejectAsync(uuid, User.GetUserId(), req.RejectionReason);
        await _audit.LogAsync(User.GetUserId(), null, "Demand", "REJECT", "PurchaseOrder", uuid, Ip, notes: req.RejectionReason);
        return Ok(ApiResponse.Ok("Purchase order rejected."));
    }

    [HttpPost("{uuid:guid}/send")]
    public async Task<IActionResult> SendPurchaseOrder(Guid uuid, [FromBody] SendPoRequest req)
    {
        await _service.SendAsync(uuid, req.SupplierContactMobile, User.GetUserId());
        var po      = await _service.GetByIdAsync(uuid);
        var actorId = User.GetUserId();
        await _audit.LogAsync(actorId, null, "Demand", "SEND", "PurchaseOrder", uuid, Ip);
        if (po is not null)
            await _notif.TryCreateAsync(new NotificationRequest(
                UserId: po.CreatedBy, Type: "PO_SENT", Title: "Purchase Order Sent",
                Message: $"Purchase order {po.PoNumber} has been sent to the supplier.",
                Category: "Procurement", EntityType: "PO", EntityUuid: uuid.ToString(),
                NavigationUrl: $"/portal/pages/demand/purchase-orders/{uuid}",
                CreatedBy: actorId));
        return Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully));
    }
}

public record RejectPoRequest(string RejectionReason);

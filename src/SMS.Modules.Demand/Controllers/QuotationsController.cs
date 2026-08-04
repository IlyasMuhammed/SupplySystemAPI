using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Demand.Controllers;

[ApiController]
[Route("api/quotations")]
[RequiresFeature("MODULE_DEMAND")]
public class QuotationsController : ControllerBase
{
    private readonly IQuotationService _service;
    private readonly IAuditService     _audit;

    public QuotationsController(IQuotationService service, IAuditService audit)
    {
        _service = service;
        _audit   = audit;
    }

    private string Ip => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";

    [HttpPost]
    [RequirePermission(PermissionCodes.RFQ_CREATE)]
    public async Task<IActionResult> CreateQuotation([FromBody] CreateQuotationRequest req)
    {
        var uuid = await _service.CreateAsync(req, User.GetUserId());
        await _audit.LogAsync(User.GetUserId(), null, "Demand", "CREATE", "Quotation", uuid, Ip);
        return Ok(ApiResponse<Guid>.Ok(uuid, StaticResponseMessage.recordCreatedSuccessfully));
    }

    [HttpPatch("{uuid:guid}")]
    public async Task<IActionResult> UpdateQuotation(Guid uuid, [FromBody] PatchQuotationRequest req)
    {
        await _service.UpdateAsync(uuid, req, User.GetUserId());
        return Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully));
    }

    [HttpGet]
    [RequirePermission(PermissionCodes.RFQ_VIEW)]
    public async Task<IActionResult> GetQuotations([FromQuery] QuotationListFilter filter)
    {
        var result = await _service.GetListAsync(filter);
        return Ok(ApiResponse<PaginatedResponse<QuotationListItemModel>>.Ok(result));
    }

    [HttpGet("{uuid:guid}")]
    [RequirePermission(PermissionCodes.RFQ_VIEW)]
    public async Task<IActionResult> GetQuotationById(Guid uuid)
    {
        var detail = await _service.GetByIdAsync(uuid);
        return detail is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<QuotationDetailModel>.Ok(detail));
    }

    [HttpPost("{uuid:guid}/send")]
    [RequirePermission(PermissionCodes.RFQ_MANAGE)]
    public async Task<IActionResult> SendQuotation(Guid uuid, [FromBody] SendQuotationRequest req)
    {
        await _service.SendAsync(uuid, req, User.GetUserId());
        return Ok(ApiResponse.Ok("Quotation sent to suppliers."));
    }

    [HttpPost("{uuid:guid}/responses")]
    [RequirePermission(PermissionCodes.RFQ_MANAGE)]
    public async Task<IActionResult> RecordVendorResponse(Guid uuid, [FromBody] RecordVendorResponseRequest req)
    {
        var responseUuid = await _service.RecordResponseAsync(uuid, req, User.GetUserId());
        return Ok(ApiResponse<Guid>.Ok(responseUuid, "Vendor response recorded."));
    }

    [HttpGet("{uuid:guid}/comparison")]
    [RequirePermission(PermissionCodes.RFQ_VIEW)]
    public async Task<IActionResult> GetComparison(Guid uuid)
    {
        var comparison = await _service.GetComparisonAsync(uuid);
        return Ok(ApiResponse<List<VendorResponseModel>>.Ok(comparison));
    }

    [HttpPost("{uuid:guid}/open-bids")]
    [RequirePermission(PermissionCodes.RFQ_MANAGE)]
    public async Task<IActionResult> OpenBids(Guid uuid)
    {
        await _service.OpenBidsAsync(uuid, User.GetUserId());
        await _audit.LogAsync(User.GetUserId(), null, "Demand", "OPEN_BIDS", "Quotation", uuid, Ip);
        return Ok(ApiResponse.Ok("Bids opened."));
    }

    [HttpPost("{uuid:guid}/award")]
    [RequirePermission(PermissionCodes.RFQ_MANAGE)]
    public async Task<IActionResult> AwardQuotation(Guid uuid, [FromBody] AwardQuotationRequest req)
    {
        await _service.AwardAsync(uuid, req, User.GetUserId());
        await _audit.LogAsync(User.GetUserId(), null, "Demand", "AWARD", "Quotation", uuid, Ip);
        return Ok(ApiResponse.Ok("Quotation awarded successfully."));
    }

    [HttpPost("{uuid:guid}/cancel")]
    [RequirePermission(PermissionCodes.RFQ_MANAGE)]
    public async Task<IActionResult> CancelQuotation(Guid uuid, [FromBody] CancelQuotationRequest req)
    {
        await _service.CancelAsync(uuid, req.Reason, User.GetUserId());
        await _audit.LogAsync(User.GetUserId(), null, "Demand", "CANCEL", "Quotation", uuid, Ip, notes: req.Reason);
        return Ok(ApiResponse.Ok("Quotation cancelled."));
    }

    [HttpPost("{uuid:guid}/send-with-link")]
    [RequirePermission(PermissionCodes.RFQ_MANAGE)]
    public async Task<IActionResult> SendWithLink(Guid uuid, [FromBody] SendWithLinkRequest req)
    {
        var result = await _service.SendWithLinkAsync(uuid, req, User.GetUserId());
        await _audit.LogAsync(User.GetUserId(), null, "Demand", "SEND", "Quotation", uuid, Ip);
        return Ok(ApiResponse<SendWithLinkResult>.Ok(result, "RFQ sent. Access links generated."));
    }

    [HttpGet("{uuid:guid}/access-links")]
    public async Task<IActionResult> GetAccessLinks(Guid uuid)
    {
        var links = await _service.GetAccessLinksAsync(uuid);
        return Ok(ApiResponse<List<RfqAccessLinkModel>>.Ok(links));
    }

    [HttpPost("{uuid:guid}/access-links/{linkId:int}/resend")]
    public async Task<IActionResult> ResendLink(Guid uuid, int linkId)
    {
        await _service.ResendLinkAsync(uuid, linkId);
        return Ok(ApiResponse.Ok("Notifications re-queued for this access link."));
    }
}

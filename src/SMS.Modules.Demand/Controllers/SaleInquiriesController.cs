using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Demand.Controllers;

/// <summary>
/// A32 C1 — Sale Inquiry (API-CONTRACT §4). Every action is gated with the contract's code — the same one the
/// frontend guard/button uses. Attachments are not here: they go through the generic api/attachments with
/// interface code SALE_INQUIRY (contract §3). A service answer of null/false means "not in the caller's
/// organization" → 404; rule breaks (400) and conflicts (409) are exceptions mapped by the global middleware.
/// </summary>
[ApiController]
[Route("api/sale-inquiries")]
[RequiresFeature("MODULE_DEMAND")]
public class SaleInquiriesController : ControllerBase
{
    private readonly ISaleInquiryService _service;
    private readonly ISaleQuotationService _quotations;

    // ISaleQuotationService is injected here, never into SaleInquiryService: the quotation service depends on
    // ISaleInquiryService (MarkQuotedAsync), so the reverse dependency would be a DI cycle.
    public SaleInquiriesController(ISaleInquiryService service, ISaleQuotationService quotations)
    {
        _service    = service;
        _quotations = quotations;
    }

    [HttpGet]
    [RequirePermission(PermissionCodes.SALE_INQUIRY_VIEW)]
    public async Task<IActionResult> GetList([FromQuery] SaleInquiryListFilter filter) =>
        Ok(ApiResponse<PaginatedResponse<SaleInquiryListItemModel>>.Ok(await _service.GetListAsync(filter)));

    [HttpPost]
    [RequirePermission(PermissionCodes.SALE_INQUIRY_CREATE)]
    public async Task<IActionResult> Create([FromBody] CreateSaleInquiryRequest req)
    {
        var uuid = await _service.CreateAsync(req, User.GetUserId());
        return Ok(ApiResponse<Guid>.Ok(uuid, StaticResponseMessage.recordCreatedSuccessfully));
    }

    [HttpGet("{uuid:guid}")]
    [RequirePermission(PermissionCodes.SALE_INQUIRY_VIEW)]
    public async Task<IActionResult> GetById(Guid uuid)
    {
        var result = await _service.GetByIdAsync(uuid);
        return result is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<SaleInquiryModel>.Ok(result));
    }

    [HttpPut("{uuid:guid}")]
    [RequirePermission(PermissionCodes.SALE_INQUIRY_EDIT)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] UpdateSaleInquiryRequest req) =>
        await _service.UpdateAsync(uuid, req, User.GetUserId())
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));

    [HttpPost("{uuid:guid}/lines")]
    [RequirePermission(PermissionCodes.SALE_INQUIRY_EDIT)]
    public async Task<IActionResult> AddLine(Guid uuid, [FromBody] SaleInquiryLineRequest req)
    {
        var lineUuid = await _service.AddLineAsync(uuid, req, User.GetUserId());
        return lineUuid is { } created
            ? Ok(ApiResponse<Guid>.Ok(created, StaticResponseMessage.recordCreatedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    [HttpPut("{uuid:guid}/lines/{lineUuid:guid}")]
    [RequirePermission(PermissionCodes.SALE_INQUIRY_EDIT)]
    public async Task<IActionResult> UpdateLine(Guid uuid, Guid lineUuid, [FromBody] UpdateSaleInquiryLineRequest req) =>
        await _service.UpdateLineAsync(uuid, lineUuid, req, User.GetUserId())
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));

    /// <summary>A34 D-16 — calculate one line's lead time and store it (API-CONTRACT §5.2).</summary>
    [HttpPost("{uuid:guid}/lines/{lineUuid:guid}/lead-time")]
    [RequirePermission(PermissionCodes.SALE_INQUIRY_EDIT)]
    public async Task<IActionResult> CalculateLineLeadTime(Guid uuid, Guid lineUuid, [FromServices] ISalesLineLeadTimeService leadTimes)
    {
        var result = await leadTimes.CalculateInquiryLineAsync(uuid, lineUuid, User.GetUserId());
        return result is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<SaleLineLeadTimeModel<SaleInquiryLineModel>>.Ok(result, "Lead time calculated."));
    }

    [HttpDelete("{uuid:guid}/lines/{lineUuid:guid}")]
    [RequirePermission(PermissionCodes.SALE_INQUIRY_EDIT)]
    public async Task<IActionResult> DeleteLine(Guid uuid, Guid lineUuid) =>
        await _service.DeleteLineAsync(uuid, lineUuid, User.GetUserId())
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordDeletedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));

    /// <summary>User transitions only (UNDER_REVIEW, REVIEW_COMPLETE, DECLINED); QUOTED comes from create-quotation.</summary>
    [HttpPatch("{uuid:guid}/status")]
    [RequirePermission(PermissionCodes.SALE_INQUIRY_EDIT)]
    public async Task<IActionResult> ChangeStatus(Guid uuid, [FromBody] ChangeSaleInquiryStatusRequest req)
    {
        var result = await _service.ChangeStatusAsync(uuid, req, User.GetUserId());
        return result is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<SaleInquiryModel>.Ok(result, $"Inquiry is now {result.Status}."));
    }

    /// <summary>
    /// REVIEW_COMPLETE → a DRAFT sale quotation, and the inquiry becomes QUOTED in the same save (BR-C1-07).
    /// Gated like creating a quotation (contract §2). The quotation service answers 404 (not in the caller's
    /// organization), 409 (already quoted / lost race) or 400 (not reviewed, line without a catalog item).
    /// </summary>
    [HttpPost("{uuid:guid}/create-quotation")]
    [RequirePermission(PermissionCodes.SALE_QUOTATION_CREATE)]
    public async Task<IActionResult> CreateQuotation(Guid uuid, [FromBody] CreateSaleQuotationFromInquiryRequest req)
    {
        var quotation = await _quotations.CreateFromInquiryAsync(uuid, req, User.GetUserId());
        return Ok(ApiResponse<Guid>.Ok(quotation, "Quotation created from the inquiry."));
    }
}

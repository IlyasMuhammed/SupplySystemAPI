using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Demand.Controllers;

/// <summary>
/// A32 C2 — seller-side Sale Quotations (docs/sales-preorder/API-CONTRACT.md §5). Not the buyer-side RFQ
/// <see cref="QuotationsController"/> (api/quotations). Every action carries its contract permission; a false/null
/// from the service is "not in the caller's organization" → 404.
/// </summary>
[ApiController]
[Route("api/sale-quotations")]
[RequiresFeature("MODULE_DEMAND")]
public class SaleQuotationsController : ControllerBase
{
    private readonly ISaleQuotationService _service;

    public SaleQuotationsController(ISaleQuotationService service) => _service = service;

    [HttpGet]
    [RequirePermission(PermissionCodes.SALE_QUOTATION_VIEW)]
    public async Task<IActionResult> GetList([FromQuery] SaleQuotationListFilter filter) =>
        Ok(ApiResponse<PaginatedResponse<SaleQuotationListItemModel>>.Ok(await _service.GetListAsync(filter)));

    [HttpPost]
    [RequirePermission(PermissionCodes.SALE_QUOTATION_CREATE)]
    public async Task<IActionResult> Create([FromBody] CreateSaleQuotationRequest req)
    {
        var uuid = await _service.CreateAsync(req, User.GetUserId());
        return Ok(ApiResponse<Guid>.Ok(uuid, StaticResponseMessage.recordCreatedSuccessfully));
    }

    [HttpGet("{uuid:guid}")]
    [RequirePermission(PermissionCodes.SALE_QUOTATION_VIEW)]
    public async Task<IActionResult> GetById(Guid uuid)
    {
        var result = await _service.GetByIdAsync(uuid);
        return result is null ? NotFoundResult() : Ok(ApiResponse<SaleQuotationModel>.Ok(result));
    }

    [HttpPut("{uuid:guid}")]
    [RequirePermission(PermissionCodes.SALE_QUOTATION_EDIT)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] UpdateSaleQuotationRequest req) =>
        Done(await _service.UpdateAsync(uuid, req, User.GetUserId()), StaticResponseMessage.recordUpdatedSuccessfully);

    [HttpPost("{uuid:guid}/lines")]
    [RequirePermission(PermissionCodes.SALE_QUOTATION_EDIT)]
    public async Task<IActionResult> AddLine(Guid uuid, [FromBody] SaleQuotationLineRequest req)
    {
        var lineUuid = await _service.AddLineAsync(uuid, req, User.GetUserId());
        return lineUuid is { } id
            ? Ok(ApiResponse<Guid>.Ok(id, StaticResponseMessage.recordCreatedSuccessfully))
            : NotFoundResult();
    }

    [HttpPut("{uuid:guid}/lines/{lineUuid:guid}")]
    [RequirePermission(PermissionCodes.SALE_QUOTATION_EDIT)]
    public async Task<IActionResult> UpdateLine(Guid uuid, Guid lineUuid, [FromBody] SaleQuotationLineRequest req) =>
        Done(await _service.UpdateLineAsync(uuid, lineUuid, req, User.GetUserId()), StaticResponseMessage.recordUpdatedSuccessfully);

    /// <summary>A34 D-16 — calculate one DRAFT line's lead time and store it (API-CONTRACT §5.2).</summary>
    [HttpPost("{uuid:guid}/lines/{lineUuid:guid}/lead-time")]
    [RequirePermission(PermissionCodes.SALE_QUOTATION_EDIT)]
    public async Task<IActionResult> CalculateLineLeadTime(Guid uuid, Guid lineUuid, [FromServices] ISalesLineLeadTimeService leadTimes)
    {
        var result = await leadTimes.CalculateQuotationLineAsync(uuid, lineUuid, User.GetUserId());
        return result is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<SaleLineLeadTimeModel<SaleQuotationLineModel>>.Ok(result, "Lead time calculated."));
    }

    [HttpDelete("{uuid:guid}/lines/{lineUuid:guid}")]
    [RequirePermission(PermissionCodes.SALE_QUOTATION_EDIT)]
    public async Task<IActionResult> DeleteLine(Guid uuid, Guid lineUuid) =>
        Done(await _service.DeleteLineAsync(uuid, lineUuid, User.GetUserId()), "Quotation line removed.");

    [HttpPost("{uuid:guid}/send")]
    [RequirePermission(PermissionCodes.SALE_QUOTATION_SEND)]
    public async Task<IActionResult> Send(Guid uuid) =>
        Done(await _service.SendAsync(uuid, User.GetUserId()), "Quotation sent.");

    [HttpPatch("{uuid:guid}/lines/{lineUuid:guid}/customer-response")]
    [RequirePermission(PermissionCodes.SALE_QUOTATION_EDIT)]
    public async Task<IActionResult> RecordCustomerResponse(Guid uuid, Guid lineUuid, [FromBody] RecordCustomerResponseRequest req) =>
        Done(await _service.RecordCustomerResponseAsync(uuid, lineUuid, req, User.GetUserId()), "Customer response recorded.");

    [HttpPost("{uuid:guid}/accept")]
    [RequirePermission(PermissionCodes.SALE_QUOTATION_EDIT)]
    public async Task<IActionResult> Accept(Guid uuid) =>
        Done(await _service.AcceptAsync(uuid, User.GetUserId()), "Quotation accepted.");

    [HttpPost("{uuid:guid}/reject")]
    [RequirePermission(PermissionCodes.SALE_QUOTATION_EDIT)]
    public async Task<IActionResult> Reject(Guid uuid, [FromBody] RejectSaleQuotationRequest? req) =>
        Done(await _service.RejectAsync(uuid, req?.Reason, User.GetUserId()), "Quotation rejected.");

    [HttpPost("{uuid:guid}/convert-to-order")]
    [RequirePermission(PermissionCodes.SALE_ORDER_CREATE)]
    public async Task<IActionResult> ConvertToOrder(Guid uuid, [FromBody] ConvertSaleQuotationToOrderRequest? req)
    {
        var orderUuid = await _service.ConvertToOrderAsync(uuid, req ?? new ConvertSaleQuotationToOrderRequest(), User.GetUserId());
        return orderUuid is { } id
            ? Ok(ApiResponse<Guid>.Ok(id, "Sale order created from the quotation."))
            : NotFoundResult();
    }

    [HttpPost("{uuid:guid}/copy")]
    [RequirePermission(PermissionCodes.SALE_QUOTATION_CREATE)]
    public async Task<IActionResult> Copy(Guid uuid)
    {
        var copyUuid = await _service.CopyAsync(uuid, User.GetUserId());
        return copyUuid is { } id
            ? Ok(ApiResponse<Guid>.Ok(id, StaticResponseMessage.recordCreatedSuccessfully))
            : NotFoundResult();
    }

    private IActionResult Done(bool found, string message) =>
        found ? Ok(ApiResponse.Ok(message)) : NotFoundResult();

    private NotFoundObjectResult NotFoundResult() => NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
}

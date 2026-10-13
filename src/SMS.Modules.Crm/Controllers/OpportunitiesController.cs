using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Crm.Models;
using SMS.Modules.Crm.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Pagination;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Crm.Controllers;

/// <summary>A41 API-CONTRACT §4 — opportunities (§14.2). No create endpoint: opportunities come from lead conversion (BR-11).</summary>
[ApiController]
[RequiresFeature(ModuleCodes.Crm)]
[Route("api/crm/opportunities")]
public sealed class OpportunitiesController : CrmControllerBase
{
    private const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private readonly IOpportunityService _opportunities;
    private readonly ITimelineService _timeline;
    private readonly ITimelineExportService _export;

    public OpportunitiesController(ITenantContext tenant, IOpportunityService opportunities, ITimelineService timeline, ITimelineExportService export)
        : base(tenant)
    {
        _opportunities = opportunities;
        _timeline = timeline;
        _export = export;
    }

    [HttpGet]
    [RequirePermission(P.CRM_VIEW_OPPORTUNITIES)]
    public async Task<IActionResult> List([FromQuery] OpportunityListFilter filter, CancellationToken ct) =>
        Ok(ApiResponse<PaginatedResponse<OpportunityListItem>>.Ok(await _opportunities.ListAsync(Caller, filter, ct)));

    [HttpGet("kanban")]
    [RequirePermission(P.CRM_VIEW_OPPORTUNITIES)]
    public async Task<IActionResult> Kanban([FromQuery] OpportunityKanbanFilter filter, CancellationToken ct) =>
        Ok(ApiResponse<KanbanBoard<OpportunityListItem>>.Ok(await _opportunities.GetKanbanAsync(Caller, filter, ct)));

    [HttpGet("{uuid:guid}")]
    [RequirePermission(P.CRM_VIEW_OPPORTUNITIES)]
    public async Task<IActionResult> Get(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<OpportunityDetail>.Ok(await _opportunities.GetByIdAsync(Caller, uuid, ct)));

    [HttpPut("{uuid:guid}")]
    [RequirePermission(P.CRM_EDIT_OPPORTUNITY)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] OpportunityUpdate req, CancellationToken ct) =>
        Ok(ApiResponse<OpportunityDetail>.Ok(await _opportunities.UpdateAsync(Caller, uuid, req, ct), "Opportunity saved."));

    [HttpPatch("{uuid:guid}/stage")]
    [RequirePermission(P.CRM_EDIT_OPPORTUNITY)]
    public async Task<IActionResult> Stage(Guid uuid, [FromBody] StageMoveRequest req, CancellationToken ct) =>
        Ok(ApiResponse<OpportunityDetail>.Ok(await _opportunities.ChangeStageAsync(Caller, uuid, req.StageUuid, ct), "Opportunity moved."));

    [HttpPost("{uuid:guid}/won")]
    [RequirePermission(P.CRM_CLOSE_OPPORTUNITY)]
    public async Task<IActionResult> Won(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<OpportunityDetail>.Ok(await _opportunities.MarkWonAsync(Caller, uuid, ct), "Opportunity won."));

    [HttpPost("{uuid:guid}/lost")]
    [RequirePermission(P.CRM_CLOSE_OPPORTUNITY)]
    public async Task<IActionResult> Lost(Guid uuid, [FromBody] OpportunityLostRequest req, CancellationToken ct) =>
        Ok(ApiResponse<OpportunityDetail>.Ok(await _opportunities.MarkLostAsync(Caller, uuid, req.Reason, req.Competitor, ct), "Opportunity lost."));

    [HttpPost("{uuid:guid}/reopen")]
    [RequirePermission(P.CRM_REOPEN_OPPORTUNITY)]
    public async Task<IActionResult> Reopen(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<OpportunityDetail>.Ok(await _opportunities.ReopenAsync(Caller, uuid, ct), "Opportunity re-opened."));

    [HttpPost("{uuid:guid}/create-inquiry")]
    [RequirePermission(P.CRM_EDIT_OPPORTUNITY)]
    [RequirePermission(P.SALE_INQUIRY_CREATE)]
    public async Task<IActionResult> CreateInquiry(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<LinkedSalesDocumentModel>.Ok(await _opportunities.CreateInquiryAsync(Caller, uuid, ct), "Sale inquiry created."));

    [HttpPost("{uuid:guid}/create-quotation")]
    [RequirePermission(P.CRM_EDIT_OPPORTUNITY)]
    [RequirePermission(P.SALE_QUOTATION_CREATE)]
    public async Task<IActionResult> CreateQuotation(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<LinkedSalesDocumentModel>.Ok(await _opportunities.CreateQuotationAsync(Caller, uuid, ct), "Sale quotation created."));

    /// <summary>D-13 — a sale order needs lines, so this hands the frontend what the sale order form needs (or the accepted quotation to convert).</summary>
    [HttpPost("{uuid:guid}/create-order")]
    [RequirePermission(P.CRM_EDIT_OPPORTUNITY)]
    [RequirePermission(P.SALE_ORDER_CREATE)]
    public async Task<IActionResult> CreateOrder(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<SaleOrderPrefillModel>.Ok(await _opportunities.CreateOrderAsync(Caller, uuid, ct)));

    [HttpGet("{uuid:guid}/timeline")]
    [RequirePermission(P.CRM_VIEW_OPPORTUNITIES)]
    public async Task<IActionResult> Timeline(Guid uuid, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, [FromQuery] string? search = null,
        CancellationToken ct = default) =>
        Ok(ApiResponse<TimelineResponse>.Ok(await _timeline.GetTimelineAsync(Caller, null, uuid, page, pageSize, search, ct)));

    [HttpGet("{uuid:guid}/timeline/export")]
    [RequirePermission(P.CRM_EXPORT_TIMELINE)]
    public async Task<IActionResult> ExportTimeline(Guid uuid, [FromQuery] string? format, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(format) && !format.Equals("xlsx", StringComparison.OrdinalIgnoreCase))
            return BadRequest(ApiResponse.Fail("Only format=xlsx is available."));
        var (content, name) = await _export.ExportToExcelAsync(Caller, null, uuid, ct);
        return File(content, XlsxType, name);
    }
}

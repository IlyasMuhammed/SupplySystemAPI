using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Crm.Models;
using SMS.Modules.Crm.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Pagination;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Crm.Controllers;

/// <summary>A41 API-CONTRACT §3 — leads (§14.1).</summary>
[ApiController]
[RequiresFeature(ModuleCodes.Crm)]
[Route("api/crm/leads")]
public sealed class LeadsController : CrmControllerBase
{
    private const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private readonly ILeadService _leads;
    private readonly ILeadConversionService _conversion;
    private readonly ITimelineService _timeline;
    private readonly ITimelineExportService _export;

    public LeadsController(ITenantContext tenant, ILeadService leads, ILeadConversionService conversion, ITimelineService timeline,
        ITimelineExportService export) : base(tenant)
    {
        _leads = leads;
        _conversion = conversion;
        _timeline = timeline;
        _export = export;
    }

    [HttpGet]
    [RequirePermission(P.CRM_VIEW_LEADS)]
    public async Task<IActionResult> List([FromQuery] LeadListFilter filter, CancellationToken ct) =>
        Ok(ApiResponse<PaginatedResponse<LeadListItem>>.Ok(await _leads.ListAsync(Caller, filter, ct)));

    [HttpGet("kanban")]
    [RequirePermission(P.CRM_VIEW_LEADS)]
    public async Task<IActionResult> Kanban([FromQuery] LeadKanbanFilter filter, CancellationToken ct) =>
        Ok(ApiResponse<KanbanBoard<LeadListItem>>.Ok(await _leads.GetKanbanAsync(Caller, filter, ct)));

    /// <summary>BR-09 — the create form's advisory duplicate check (nothing saved yet).</summary>
    [HttpGet("duplicate-check")]
    [RequirePermission(P.CRM_VIEW_LEADS)]
    public async Task<IActionResult> DuplicateCheck([FromQuery] LeadDuplicateQuery query, CancellationToken ct) =>
        Ok(ApiResponse<LeadDuplicateResult>.Ok(await _leads.DuplicateCheckAsync(Caller, query, ct)));

    [HttpGet("{uuid:guid}")]
    [RequirePermission(P.CRM_VIEW_LEADS)]
    public async Task<IActionResult> Get(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<LeadDetail>.Ok(await _leads.GetByIdAsync(Caller, uuid, ct)));

    [HttpPost]
    [RequirePermission(P.CRM_CREATE_LEAD)]
    public async Task<IActionResult> Create([FromBody] LeadUpsert req, CancellationToken ct) =>
        Ok(ApiResponse<LeadDetail>.Ok(await _leads.CreateAsync(Caller, req, ct), "Lead created."));

    [HttpPut("{uuid:guid}")]
    [RequirePermission(P.CRM_EDIT_LEAD)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] LeadUpsert req, CancellationToken ct) =>
        Ok(ApiResponse<LeadDetail>.Ok(await _leads.UpdateAsync(Caller, uuid, req, ct), "Lead saved."));

    [HttpPatch("{uuid:guid}/assign")]
    [RequirePermission(P.CRM_ASSIGN_LEAD)]
    public async Task<IActionResult> Assign(Guid uuid, [FromBody] LeadAssignRequest req, CancellationToken ct) =>
        Ok(ApiResponse<LeadDetail>.Ok(await _leads.AssignAsync(Caller, uuid, req, ct), "Lead assigned."));

    [HttpPatch("{uuid:guid}/status")]
    [RequirePermission(P.CRM_EDIT_LEAD)]
    public async Task<IActionResult> Status(Guid uuid, [FromBody] LeadStatusRequest req, CancellationToken ct) =>
        Ok(ApiResponse<LeadDetail>.Ok(await _leads.ChangeStatusAsync(Caller, uuid, req.Status, req.Reason, ct), "Lead status changed."));

    [HttpPatch("{uuid:guid}/stage")]
    [RequirePermission(P.CRM_EDIT_LEAD)]
    public async Task<IActionResult> Stage(Guid uuid, [FromBody] StageMoveRequest req, CancellationToken ct) =>
        Ok(ApiResponse<LeadDetail>.Ok(await _leads.MoveStageAsync(Caller, uuid, req.StageUuid, ct), "Lead moved."));

    [HttpPost("{uuid:guid}/qualify")]
    [RequirePermission(P.CRM_QUALIFY_LEAD)]
    public async Task<IActionResult> Qualify(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<LeadDetail>.Ok(await _leads.QualifyAsync(Caller, uuid, ct), "Lead qualified."));

    [HttpPost("{uuid:guid}/disqualify")]
    [RequirePermission(P.CRM_DISQUALIFY_LEAD)]
    public async Task<IActionResult> Disqualify(Guid uuid, [FromBody] ReasonRequest req, CancellationToken ct) =>
        Ok(ApiResponse<LeadDetail>.Ok(await _leads.DisqualifyAsync(Caller, uuid, req.Reason, ct), "Lead disqualified."));

    [HttpPost("{uuid:guid}/reopen")]
    [RequirePermission(P.CRM_REOPEN_LEAD)]
    public async Task<IActionResult> Reopen(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<LeadDetail>.Ok(await _leads.ReopenAsync(Caller, uuid, ct), "Lead re-opened."));

    [HttpDelete("{uuid:guid}")]
    [RequirePermission(P.CRM_DELETE_LEAD)]
    public async Task<IActionResult> Delete(Guid uuid, CancellationToken ct)
    {
        await _leads.DeleteAsync(Caller, uuid, ct);
        return Ok(ApiResponse.Ok("Lead deleted."));
    }

    [HttpGet("{uuid:guid}/duplicate-check")]
    [RequirePermission(P.CRM_VIEW_LEADS)]
    public async Task<IActionResult> LeadDuplicateCheck(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<LeadDuplicateResult>.Ok(await _leads.DuplicateCheckForLeadAsync(Caller, uuid, ct)));

    [HttpGet("{uuid:guid}/cross-references")]
    [RequirePermission(P.CRM_VIEW_LEADS, P.CRM_VIEW_OPPORTUNITIES)]
    public async Task<IActionResult> CrossReferences(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<CrossReferenceResult>.Ok(await _leads.GetCrossReferencesAsync(Caller, uuid, ct)));

    [HttpGet("{uuid:guid}/timeline")]
    [RequirePermission(P.CRM_VIEW_LEADS)]
    public async Task<IActionResult> Timeline(Guid uuid, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, [FromQuery] string? search = null,
        CancellationToken ct = default) =>
        Ok(ApiResponse<TimelineResponse>.Ok(await _timeline.GetTimelineAsync(Caller, uuid, null, page, pageSize, search, ct)));

    [HttpGet("{uuid:guid}/timeline/export")]
    [RequirePermission(P.CRM_EXPORT_TIMELINE)]
    public async Task<IActionResult> ExportTimeline(Guid uuid, [FromQuery] string? format, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(format) && !format.Equals("xlsx", StringComparison.OrdinalIgnoreCase))
            return BadRequest(ApiResponse.Fail("Only format=xlsx is available."));
        var (content, name) = await _export.ExportToExcelAsync(Caller, uuid, null, ct);
        return File(content, XlsxType, name);
    }

    // ── conversion (§8) ──

    [HttpGet("{uuid:guid}/account-matches")]
    [RequirePermission(P.CRM_CONVERT_LEAD)]
    public async Task<IActionResult> AccountMatches(Guid uuid, [FromQuery] string? search, CancellationToken ct) =>
        Ok(ApiResponse<IReadOnlyList<CrmAccountModel>>.Ok(await _conversion.AccountMatchesAsync(Caller, uuid, search, ct)));

    [HttpPost("{uuid:guid}/convert")]
    [RequirePermission(P.CRM_CONVERT_LEAD)]
    public async Task<IActionResult> Convert(Guid uuid, [FromBody] LeadConvertRequest req, CancellationToken ct) =>
        Ok(ApiResponse<LeadConversionResult>.Ok(await _conversion.ConvertAsync(Caller, uuid, req, ct), "Lead converted."));
}

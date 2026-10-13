using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Accounting.Models;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Pagination;

namespace SMS.Modules.Accounting.Controllers;

/// <summary>A42 API-CONTRACT "Templates — api/finance/templates".</summary>
[Route("api/finance/templates")]
public sealed class TemplatesController : AccountingControllerBase
{
    private readonly ITemplateService _templates;

    public TemplatesController(ITenantContext tenant, ITemplateService templates) : base(tenant) => _templates = templates;

    [HttpGet]
    [RequirePermission(View, ManageAccounts, ManageSetup)]
    public async Task<IActionResult> List([FromQuery] string? countryCode, [FromQuery] string? businessType, [FromQuery] string? search, CancellationToken ct) =>
        Ok(ApiResponse<List<CoaTemplateModel>>.Ok(await _templates.ListAsync(Caller, countryCode, businessType, search, ct)));

    [HttpGet("filters")]
    [RequirePermission(View, ManageAccounts, ManageSetup)]
    public async Task<IActionResult> Filters(CancellationToken ct) =>
        Ok(ApiResponse<TemplateFiltersModel>.Ok(await _templates.FiltersAsync(Caller, ct)));

    /// <summary>Header-only CSV / skeleton JSON (coa-template.csv|json).</summary>
    [HttpGet("blank")]
    [RequirePermission(View, ManageAccounts, ManageSetup)]
    public IActionResult Blank([FromQuery] string? format)
    {
        Caller.Require(Caller.CanView);
        var file = _templates.Blank(format);
        return File(file.Content, file.ContentType, file.FileName);
    }

    [HttpGet("{uuid:guid}")]
    [RequirePermission(View, ManageAccounts, ManageSetup)]
    public async Task<IActionResult> Get(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<CoaTemplateDetailModel>.Ok(await _templates.GetAsync(Caller, uuid, ct)));

    [HttpGet("{uuid:guid}/preview")]
    [RequirePermission(View, ManageAccounts, ManageSetup)]
    public async Task<IActionResult> Preview(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<List<TemplateItemModel>>.Ok(await _templates.PreviewAsync(Caller, uuid, ct)));

    [HttpGet("{uuid:guid}/download")]
    [RequirePermission(View, ManageAccounts, ManageSetup)]
    public async Task<IActionResult> Download(Guid uuid, [FromQuery] string? format, CancellationToken ct)
    {
        var file = await _templates.DownloadAsync(Caller, uuid, format, ct);
        return File(file.Content, file.ContentType, file.FileName);
    }

    /// <summary>Deactivates one of the organization's own templates; a system template → 403 (BR-TPL-001).</summary>
    [HttpDelete("{uuid:guid}")]
    [RequirePermission(ManageSetup)]
    public async Task<IActionResult> Delete(Guid uuid, CancellationToken ct)
    {
        await _templates.DeleteAsync(Caller, uuid, ct);
        return Ok(ApiResponse.Ok("Template removed."));
    }
}

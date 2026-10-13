using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Pagination;

namespace SMS.Modules.Accounting.Controllers;

/// <summary>A42 API-CONTRACT "Chart of accounts — api/finance/chart-of-accounts".</summary>
[Route("api/finance/chart-of-accounts")]
public sealed class ChartOfAccountsController : AccountingControllerBase
{
    private readonly IChartOfAccountsService _chart;

    public ChartOfAccountsController(ITenantContext tenant, IChartOfAccountsService chart) : base(tenant) => _chart = chart;

    [HttpGet]
    [RequirePermission(View, ManageAccounts, ManageSetup)]
    public async Task<IActionResult> Tree([FromQuery] bool includeInactive = false, CancellationToken ct = default) =>
        Ok(ApiResponse<ChartTreeModel>.Ok(await _chart.GetTreeAsync(Caller, includeInactive, ct)));

    [HttpGet("flat")]
    [RequirePermission(View, ManageAccounts, ManageSetup)]
    public async Task<IActionResult> Flat([FromQuery] AccountFlatQuery query, CancellationToken ct) =>
        Ok(ApiResponse<List<AccountModel>>.Ok(await _chart.GetFlatAsync(Caller, query, ct)));

    [HttpGet("suggest-code")]
    [RequirePermission(View, ManageAccounts, ManageSetup)]
    public async Task<IActionResult> SuggestCode([FromQuery] Guid parentUuid, CancellationToken ct) =>
        Ok(ApiResponse<SuggestCodeModel>.Ok(await _chart.SuggestCodeAsync(Caller, parentUuid, ct)));

    [HttpGet("export")]
    [RequirePermission(View, ManageAccounts, ManageSetup)]
    public async Task<IActionResult> Export([FromQuery] string? format, CancellationToken ct)
    {
        var file = await _chart.ExportAsync(Caller, format, ct);
        return File(file.Content, file.ContentType, file.FileName);
    }

    [HttpGet("{uuid:guid}")]
    [RequirePermission(View, ManageAccounts, ManageSetup)]
    public async Task<IActionResult> Get(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<AccountDetailModel>.Ok(await _chart.GetAsync(Caller, uuid, ct)));

    [HttpGet("{uuid:guid}/subtree")]
    [RequirePermission(View, ManageAccounts, ManageSetup)]
    public async Task<IActionResult> Subtree(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<List<AccountModel>>.Ok(await _chart.GetSubtreeAsync(Caller, uuid, ct)));

    [HttpGet("{uuid:guid}/ancestors")]
    [RequirePermission(View, ManageAccounts, ManageSetup)]
    public async Task<IActionResult> Ancestors(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<List<AccountModel>>.Ok(await _chart.GetAncestorsAsync(Caller, uuid, ct)));

    [HttpPost]
    [RequirePermission(ManageAccounts, ManageSetup)]
    public async Task<IActionResult> Create([FromBody] AccountCreateRequest request, CancellationToken ct) =>
        Ok(ApiResponse<AccountDetailModel>.Ok(await _chart.CreateAsync(Caller, request, ct), "Account added."));

    [HttpPut("{uuid:guid}")]
    [RequirePermission(ManageAccounts, ManageSetup)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] AccountUpdateRequest request, CancellationToken ct) =>
        Ok(ApiResponse<AccountDetailModel>.Ok(await _chart.UpdateAsync(Caller, uuid, request, ct), "Account saved."));

    [HttpDelete("{uuid:guid}")]
    [RequirePermission(ManageAccounts, ManageSetup)]
    public async Task<IActionResult> Delete(Guid uuid, CancellationToken ct)
    {
        await _chart.DeleteAsync(Caller, uuid, ct);
        return Ok(ApiResponse.Ok("Account deleted."));
    }

    [HttpPost("apply-template")]
    [RequirePermission(ManageSetup)]
    public async Task<IActionResult> ApplyTemplate([FromBody] ApplyTemplateRequest request, CancellationToken ct) =>
        Ok(ApiResponse<ApplyResultModel>.Ok(await _chart.ApplyTemplateAsync(Caller, request.TemplateUuid, ct), "Template applied."));

    /// <summary>FSD §9.3 "Manual" — a chart of only the 5 roots + 39/390/3900; the user adds levels 2–4 in the tree.</summary>
    [HttpPost("start-blank")]
    [RequirePermission(ManageSetup)]
    public async Task<IActionResult> StartBlank(CancellationToken ct) =>
        Ok(ApiResponse<ApplyResultModel>.Ok(await _chart.StartBlankAsync(Caller, ct), "Blank chart of accounts started."));

    /// <summary>multipart <c>file</c> (.csv / .json, ≤ 5 MB) → ApplyResult, or 400 <c>{ success: false, message, errors }</c>.</summary>
    [HttpPost("upload")]
    [RequirePermission(ManageSetup)]
    [RequestSizeLimit(GlLimits.MaxUploadBytes + 64 * 1024)]
    public async Task<IActionResult> Upload(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new CoaUploadFailedModel { Message = "Choose a CSV or JSON file.", Errors = [] });
        if (file.Length > GlLimits.MaxUploadBytes)
            return BadRequest(new CoaUploadFailedModel { Message = "The file must be 5 MB or smaller.", Errors = [] });
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        try
        {
            return Ok(ApiResponse<ApplyResultModel>.Ok(await _chart.UploadAsync(Caller, file.FileName, ms.ToArray(), ct), "Chart of accounts uploaded."));
        }
        catch (CoaUploadException ex)
        {
            return BadRequest(new CoaUploadFailedModel { Message = ex.Message, Errors = ex.Errors.ToList() });
        }
    }
}

/// <summary>The 400 body of a refused upload: the ApiResponse fields plus every problem (API-CONTRACT <c>{ message, errors }</c>).</summary>
public sealed class CoaUploadFailedModel
{
    public bool              Success { get; set; }
    public string            Message { get; set; } = string.Empty;
    public List<UploadError> Errors  { get; set; } = [];
    public object?           Result  { get; set; }
}

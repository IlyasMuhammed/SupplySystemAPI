using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using SMS.Modules.Accounting.Models;
using SMS.Modules.Accounting.Posting;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Pagination;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Accounting.Controllers;

/// <summary>
/// A44 API-CONTRACT "Details › CORE" — finance posting settings (D-8) and the posting log (D-12). Not derived from
/// <see cref="AccountingControllerBase"/>: the log is also the QuickBooks-only organization's view of its postings, so the
/// gate is FEATURE_GENERAL_LEDGER <b>or</b> MODULE_INTEGRATION (D-40), and the log accepts the integration permissions too
/// (D-41). The settings stay GL-only by permission.
/// </summary>
[ApiController]
[Route("api/finance/posting")]
[RequiresFeature(AccountingCodes.FeatureGeneralLedger, ModuleCodes.Integration)]
public sealed class PostingController : ControllerBase
{
    private readonly ITenantContext _tenant;
    private readonly ITenantFinanceConfigService _config;
    private readonly IPostingLogService _log;

    public PostingController(ITenantContext tenant, ITenantFinanceConfigService config, IPostingLogService log)
    {
        _tenant = tenant;
        _config = config;
        _log = log;
    }

    private AccountingCaller Caller => AccountingCaller.From(User, _tenant);

    [HttpGet("config")]
    [RequirePermission(P.GL_VIEW, P.GL_ACCOUNT_MANAGE, P.GL_SETUP_MANAGE)]
    public async Task<IActionResult> GetConfig(CancellationToken ct) =>
        Ok(ApiResponse<PostingConfigModel>.Ok(await _config.GetAsync(Caller, ct)));

    [HttpPut("config")]
    [RequirePermission(P.GL_SETUP_MANAGE)]
    public async Task<IActionResult> UpdateConfig([FromBody] UpdatePostingConfigRequest request, CancellationToken ct) =>
        Ok(ApiResponse<PostingConfigModel>.Ok(await _config.UpdateAsync(Caller, request ?? new UpdatePostingConfigRequest(), ct), "Posting settings saved."));

    [HttpGet("log")]
    [RequirePermission(P.GL_VIEW, P.GL_ACCOUNT_MANAGE, P.GL_SETUP_MANAGE, P.INTEGRATION_VIEW, P.INTEGRATION_MANAGE)]
    public async Task<IActionResult> List([FromQuery] PostingLogQuery query, CancellationToken ct) =>
        Ok(ApiResponse<PaginatedResponse<PostingLogItemModel>>.Ok(await _log.ListAsync(Caller, query, ct)));

    [HttpGet("log/{uuid:guid}")]
    [RequirePermission(P.GL_VIEW, P.GL_ACCOUNT_MANAGE, P.GL_SETUP_MANAGE, P.INTEGRATION_VIEW, P.INTEGRATION_MANAGE)]
    public async Task<IActionResult> Get(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<PostingLogDetailModel>.Ok(await _log.GetAsync(Caller, uuid, ct)));

    /// <summary>D-33 / D-49a — the postings of one SCM document (post + void) with per-target canAct; 200 [] when none.</summary>
    [HttpGet("by-source/{sourceUuid:guid}")]
    [RequirePermission(P.GL_VIEW, P.GL_ACCOUNT_MANAGE, P.GL_SETUP_MANAGE, P.INTEGRATION_VIEW, P.INTEGRATION_MANAGE)]
    public async Task<IActionResult> BySource(Guid sourceUuid, CancellationToken ct) =>
        Ok(ApiResponse<IReadOnlyList<PostingLogItemModel>>.Ok(await _log.BySourceAsync(Caller, sourceUuid, ct)));

    [HttpPost("log/{uuid:guid}/retry")]
    [RequirePermission(P.GL_SETUP_MANAGE, P.INTEGRATION_MANAGE)]
    public async Task<IActionResult> Retry(Guid uuid, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] PostingTargetActionRequest? request, CancellationToken ct)
    {
        // REV-18 — only the targets the caller may touch; the message names any left out.
        var r = await _log.RetryAsync(Caller, uuid, request?.TargetCode, ct);
        return Ok(ApiResponse<PostingLogDetailModel>.Ok(r.Posting, r.Message("Retried")));
    }

    [HttpPost("log/{uuid:guid}/skip")]
    [RequirePermission(P.GL_SETUP_MANAGE, P.INTEGRATION_MANAGE)]
    public async Task<IActionResult> Skip(Guid uuid, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] PostingTargetActionRequest? request, CancellationToken ct)
    {
        var r = await _log.SkipAsync(Caller, uuid, request?.TargetCode, request?.Reason, ct);
        return Ok(ApiResponse<PostingLogDetailModel>.Ok(r.Posting, r.Message("Skipped")));
    }

    [HttpGet("summary")]
    [RequirePermission(P.GL_VIEW, P.GL_ACCOUNT_MANAGE, P.GL_SETUP_MANAGE, P.INTEGRATION_VIEW, P.INTEGRATION_MANAGE)]
    public async Task<IActionResult> Summary(CancellationToken ct) =>
        Ok(ApiResponse<PostingSummaryModel>.Ok(await _log.SummaryAsync(Caller, ct)));
}

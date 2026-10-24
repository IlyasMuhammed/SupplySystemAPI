using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Accounting.Models;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Pagination;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Accounting.Controllers;

/// <summary>
/// A47 C10 — API-CONTRACT §6 "Period close &amp; year-end close" (FSD §16.5). View: GL_PERIOD_CLOSE_VIEW / EXECUTE / ADMIN;
/// close, adjustment-only, reopen: EXECUTE or ADMIN; final close and the year-end close: ADMIN.
/// </summary>
[Route("api/finance")]
[RequiresGlSetup]
[JournalErrorFilter]
[ProducesResponseType(StatusCodes.Status400BadRequest)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
[ProducesResponseType(StatusCodes.Status404NotFound)]
[ProducesResponseType(StatusCodes.Status409Conflict)]
public sealed class PeriodCloseController : AccountingControllerBase
{
    private const string CloseView = P.GL_PERIOD_CLOSE_VIEW, CloseExecute = P.GL_PERIOD_CLOSE_EXECUTE, CloseAdmin = P.GL_PERIOD_CLOSE_ADMIN;

    private readonly IPeriodCloseService _close;

    public PeriodCloseController(ITenantContext tenant, IPeriodCloseService close) : base(tenant) => _close = close;

    /// <summary>The fiscal year's periods with status, draft counts and allowed actions, and the year-end readiness.</summary>
    [HttpGet("period-close/{fiscalYearUuid:guid}/status")]
    [RequirePermission(CloseView, CloseExecute, CloseAdmin)]
    [ProducesResponseType(typeof(ApiResponse<PeriodCloseOverviewModel>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Status(Guid fiscalYearUuid, CancellationToken ct) =>
        Ok(ApiResponse<PeriodCloseOverviewModel>.Ok(await _close.GetPeriodStatusesAsync(Caller, fiscalYearUuid, ct)));

    /// <summary>Body <c>{ "targetStatus": "Locked" | "AdjustmentOnly" }</c> (missing = Locked).</summary>
    [HttpPost("period-close/{periodUuid:guid}/close")]
    [RequirePermission(CloseExecute, CloseAdmin)]
    [ProducesResponseType(typeof(ApiResponse<PeriodCloseResultModel>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Close(Guid periodUuid, [FromBody] ClosePeriodRequest? request, CancellationToken ct)
    {
        var result = await _close.ClosePeriodAsync(Caller, periodUuid, request?.TargetStatus, ct);
        return Ok(ApiResponse<PeriodCloseResultModel>.Ok(result,
            result.NewStatus == Domain.PeriodStatuses.Locked ? "Period locked." : "Period set to adjustment only."));
    }

    /// <summary>Reopens a Locked or AdjustmentOnly period (Open). EXECUTE or ADMIN.</summary>
    [HttpPost("period-close/{periodUuid:guid}/reopen")]
    [RequirePermission(CloseExecute, CloseAdmin)]
    [ProducesResponseType(typeof(ApiResponse<PeriodCloseResultModel>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Reopen(Guid periodUuid, CancellationToken ct) =>
        Ok(ApiResponse<PeriodCloseResultModel>.Ok(await _close.ReopenPeriodAsync(Caller, periodUuid, ct), "Period reopened."));

    /// <summary>Closes a Locked period for good (Closed). ADMIN.</summary>
    [HttpPost("period-close/{periodUuid:guid}/final-close")]
    [RequirePermission(CloseAdmin)]
    [ProducesResponseType(typeof(ApiResponse<PeriodCloseResultModel>), StatusCodes.Status200OK)]
    public async Task<IActionResult> FinalClose(Guid periodUuid, CancellationToken ct) =>
        Ok(ApiResponse<PeriodCloseResultModel>.Ok(await _close.FinalClosePeriodAsync(Caller, periodUuid, ct), "Period closed."));

    /// <summary>What the year-end close would post (P&amp;L balances to retained earnings) and what blocks it.</summary>
    [HttpGet("year-end-close/{fiscalYearUuid:guid}/preview")]
    [RequirePermission(CloseView, CloseExecute, CloseAdmin)]
    [ProducesResponseType(typeof(ApiResponse<YearEndPreviewModel>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Preview(Guid fiscalYearUuid, [FromQuery] Guid? retainedEarningsAccountUuid, CancellationToken ct) =>
        Ok(ApiResponse<YearEndPreviewModel>.Ok(await _close.PreviewYearEndCloseAsync(Caller, fiscalYearUuid, retainedEarningsAccountUuid, ct)));

    /// <summary>Body <c>{ "retainedEarningsAccountUuid"?: "…" }</c> (missing = DEFAULT_RETAINED_EARNINGS).</summary>
    [HttpPost("year-end-close/{fiscalYearUuid:guid}/execute")]
    [RequirePermission(CloseAdmin)]
    [ProducesResponseType(typeof(ApiResponse<YearEndCloseResultModel>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Execute(Guid fiscalYearUuid, [FromBody] YearEndCloseRequest? request, CancellationToken ct)
    {
        var result = await _close.CloseYearAsync(Caller, fiscalYearUuid, request?.RetainedEarningsAccountUuid, ct);
        return Ok(ApiResponse<YearEndCloseResultModel>.Ok(result, result.AlreadyClosed ? "The fiscal year was already closed." : "Fiscal year closed."));
    }
}

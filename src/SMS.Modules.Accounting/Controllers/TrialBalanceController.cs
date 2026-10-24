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
/// A47 C9 — API-CONTRACT §5 "Trial balance &amp; ledgers" (FSD §16.4). GL_REPORTS_VIEW. The account balance / ledger answer
/// on both <c>api/finance/trial-balance/accounts/{uuid}/…</c> (the contract's table, relative to its section route) and
/// <c>api/finance/accounts/{uuid}/…</c> (FSD §16.4) — D-RPT-9.
/// </summary>
[Route("api/finance")]
[RequiresGlSetup]
[JournalErrorFilter]
[ProducesResponseType(StatusCodes.Status400BadRequest)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
[ProducesResponseType(StatusCodes.Status404NotFound)]
[ProducesResponseType(StatusCodes.Status409Conflict)]
public sealed class TrialBalanceController : AccountingControllerBase
{
    private readonly ITrialBalanceService _reports;

    public TrialBalanceController(ITenantContext tenant, ITrialBalanceService reports) : base(tenant) => _reports = reports;

    /// <summary>The trial balance (Level-4 accounts, base currency, posted and reversed entries) for dates, a fiscal year or a period.</summary>
    [HttpGet("trial-balance")]
    [RequirePermission(P.GL_REPORTS_VIEW)]
    [ProducesResponseType(typeof(ApiResponse<TrialBalanceReportModel>), StatusCodes.Status200OK)]
    public async Task<IActionResult> TrialBalance([FromQuery] TrialBalanceQuery query, CancellationToken ct) =>
        Ok(ApiResponse<TrialBalanceReportModel>.Ok(await _reports.GenerateAsync(Caller, query, ct)));

    /// <summary>The same report as CSV: trial-balance-{from}-{to}.csv (UTF-8 with BOM).</summary>
    [HttpGet("trial-balance/export")]
    [RequirePermission(P.GL_REPORTS_VIEW)]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK, "text/csv")]
    public async Task<IActionResult> Export([FromQuery] TrialBalanceQuery query, CancellationToken ct)
    {
        var file = await _reports.ExportCsvAsync(Caller, query, ct);
        return File(file.Content, file.ContentType, file.FileName);
    }

    /// <summary>One leaf account's balance as of a date (default today), closing entries included.</summary>
    [HttpGet("trial-balance/accounts/{uuid:guid}/balance")]
    [HttpGet("accounts/{uuid:guid}/balance")]
    [RequirePermission(P.GL_REPORTS_VIEW)]
    [ProducesResponseType(typeof(ApiResponse<AccountBalanceModel>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Balance(Guid uuid, [FromQuery] DateOnly? asOfDate, CancellationToken ct) =>
        Ok(ApiResponse<AccountBalanceModel>.Ok(await _reports.GetAccountBalanceAsync(Caller, uuid, asOfDate, ct)));

    /// <summary>One leaf account's ledger lines with running balance, paged (1-500).</summary>
    [HttpGet("trial-balance/accounts/{uuid:guid}/ledger")]
    [HttpGet("accounts/{uuid:guid}/ledger")]
    [RequirePermission(P.GL_REPORTS_VIEW)]
    [ProducesResponseType(typeof(ApiResponse<AccountLedgerModel>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Ledger(Guid uuid, [FromQuery] AccountLedgerQuery query, CancellationToken ct) =>
        Ok(ApiResponse<AccountLedgerModel>.Ok(await _reports.GetAccountLedgerAsync(Caller, uuid, query, ct)));
}

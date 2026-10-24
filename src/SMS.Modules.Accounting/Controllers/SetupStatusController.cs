using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Accounting.Models;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Pagination;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Accounting.Controllers;

/// <summary>
/// Shared by the Accounting controllers: the caller (organization, user, permissions) of this request. Every Accounting
/// controller derives from this and carries <c>[ApiController]</c> + <c>[RequiresFeature(AccountingCodes.FeatureGeneralLedger)]</c>;
/// actions carry <c>[RequirePermission(...)]</c> (read: GL_VIEW plus both manage codes; writes: the manage code).
/// </summary>
[ApiController]
[RequiresFeature(AccountingCodes.FeatureGeneralLedger)]
[ResolveAccountingCallerName]
public abstract class AccountingControllerBase : ControllerBase
{
    private readonly ITenantContext _tenant;

    protected AccountingControllerBase(ITenantContext tenant) => _tenant = tenant;

    /// <summary>A47 QA-1 — with the display name the filter resolved for this request (the token carries none).</summary>
    protected AccountingCaller Caller =>
        AccountingCaller.From(User, _tenant, HttpContext?.Items[ResolveAccountingCallerNameAttribute.ItemKey] as string);

    /// <summary>Permission sets for <c>[RequirePermission]</c>.</summary>
    protected const string View = P.GL_VIEW, ManageAccounts = P.GL_ACCOUNT_MANAGE, ManageSetup = P.GL_SETUP_MANAGE;

    /// <summary>A47 SR-FE-2 — every journal-engine code, for the lookups (fiscal years, flat accounts) the A47 pages read.</summary>
    protected const string LookupJournal1 = P.GL_JOURNAL_CREATE, LookupJournal2 = P.GL_JOURNAL_EDIT, LookupJournal3 = P.GL_JOURNAL_DELETE,
        LookupJournal4 = P.GL_JOURNAL_POST, LookupJournal5 = P.GL_JOURNAL_REVERSE, LookupReconcile1 = P.GL_RECONCILE_VIEW,
        LookupReconcile2 = P.GL_RECONCILE_EXECUTE, LookupReports = P.GL_REPORTS_VIEW, LookupClose1 = P.GL_PERIOD_CLOSE_VIEW,
        LookupClose2 = P.GL_PERIOD_CLOSE_EXECUTE, LookupClose3 = P.GL_PERIOD_CLOSE_ADMIN;
}

/// <summary>A42 API-CONTRACT "Setup status — api/finance/setup".</summary>
[Route("api/finance/setup")]
public sealed class SetupStatusController : AccountingControllerBase
{
    private readonly ISetupStatusService _setup;

    public SetupStatusController(ITenantContext tenant, ISetupStatusService setup) : base(tenant) => _setup = setup;

    [HttpGet("status")]
    [RequirePermission(View, ManageAccounts, ManageSetup)]
    public async Task<IActionResult> Status(CancellationToken ct) =>
        Ok(ApiResponse<SetupStatusModel>.Ok(await _setup.GetAsync(Caller, ct)));

    [HttpGet("history")]
    [RequirePermission(View, ManageAccounts, ManageSetup)]
    public async Task<IActionResult> History([FromQuery] int limit = 100, CancellationToken ct = default) =>
        Ok(ApiResponse<IReadOnlyList<SetupHistoryItemModel>>.Ok(await _setup.HistoryAsync(Caller, limit, ct)));

    /// <summary>409 with <c>result.unmet</c> listing the missing prerequisites (BR-MG-003).</summary>
    [HttpPost("complete")]
    [RequirePermission(ManageSetup)]
    public async Task<IActionResult> Complete(CancellationToken ct)
    {
        try
        {
            return Ok(ApiResponse<SetupStatusModel>.Ok(await _setup.CompleteAsync(Caller, ct), "General ledger setup completed."));
        }
        catch (SetupIncompleteException ex)
        {
            return Conflict(ApiResponse.Fail(ex.Message, new { unmet = ex.Unmet }));
        }
    }

    [HttpPost("reset")]
    [RequirePermission(ManageSetup)]
    public async Task<IActionResult> Reset(CancellationToken ct) =>
        Ok(ApiResponse<SetupStatusModel>.Ok(await _setup.ResetAsync(Caller, ct), "General ledger setup reset."));
}

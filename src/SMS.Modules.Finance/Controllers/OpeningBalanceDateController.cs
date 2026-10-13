using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Finance.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.Controllers;

/// <summary>
/// A40 (TFIN, D-7; docs/data-import/API-CONTRACT.md §Finance) — the organization's opening balance date. Read by the
/// Finance pages and by the data-import wizard (IMPORT_OPENING_BALANCE); set in Finance settings (FINANCE_SETUP_MANAGE,
/// Finance module on), like the other Finance master data.
/// </summary>
[ApiController]
[Route("api/finance/opening-balance-date")]
public class OpeningBalanceDateController : ControllerBase
{
    private readonly IOpeningBalanceDateService _svc;

    public OpeningBalanceDateController(IOpeningBalanceDateService svc) => _svc = svc;

    [HttpGet]
    [RequirePermission(PermissionCodes.FINANCE_SETUP_MANAGE,
        PermissionCodes.INVOICE_VIEW, PermissionCodes.PAYMENT_VIEW, PermissionCodes.SALES_INVOICE_VIEW,
        PermissionCodes.CUSTOMER_LEDGER_VIEW, PermissionCodes.IMPORT_OPENING_BALANCE,
        // Settings › Finance Setup › Currency configuration is reached through the currency permissions too.
        PermissionCodes.CURRENCY_VIEW, PermissionCodes.CURRENCY_MANAGE, PermissionCodes.CURRENCY_RATE_VIEW,
        PermissionCodes.CURRENCY_RATE_MANAGE, PermissionCodes.ORG_CURRENCY_SETTINGS_MANAGE,
        // A42 D-11 — the GL setup wizard shows and edits this date as the conversion date.
        PermissionCodes.GL_VIEW, PermissionCodes.GL_ACCOUNT_MANAGE, PermissionCodes.GL_SETUP_MANAGE)]
    public async Task<IActionResult> Get()
    {
        var date = await _svc.GetAsync(HttpContext.RequestAborted);
        return Ok(ApiResponse<OpeningBalanceDateModel>.Ok(new OpeningBalanceDateModel { Date = OpeningBalanceDateService.Format(date) }));
    }

    /// <summary><c>{ date: 'yyyy-MM-dd' }</c>; <c>null</c> clears it (refused once opening documents exist).</summary>
    [HttpPut]
    [RequirePermission(PermissionCodes.FINANCE_SETUP_MANAGE, PermissionCodes.GL_SETUP_MANAGE)]
    [RequiresFeature("MODULE_FINANCE")]
    public async Task<IActionResult> Put([FromBody] OpeningBalanceDateModel? req)
    {
        var saved = await _svc.SetAsync(OpeningBalanceDateService.Parse(req?.Date), User.GetUserId(), HttpContext.RequestAborted);
        var text  = OpeningBalanceDateService.Format(saved);
        return Ok(ApiResponse<OpeningBalanceDateModel>.Ok(
            new OpeningBalanceDateModel { Date = text },
            text is null ? "Opening balance date cleared." : $"Opening balance date set to {text}."));
    }
}

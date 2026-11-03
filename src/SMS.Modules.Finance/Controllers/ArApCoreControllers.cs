using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Finance.ArAp;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Pagination;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Finance.Controllers;

/// <summary>
/// A48 (CORE) — API-CONTRACT §1.1–§1.3: partner financial profiles (D-4). Reads accept the AR or AP reading codes (the service
/// then requires the role's own); writes need FINANCE_AR_AP_ADMIN and the Finance module.
/// </summary>
[ApiController]
[Route("api/finance/partner-profiles")]
public class PartnerProfilesController : ControllerBase
{
    private readonly IPartnerProfileEndpoints _svc;
    private readonly ITenantContext _tenant;
    private readonly IOrgUserDirectory? _users;

    public PartnerProfilesController(IPartnerProfileEndpoints svc, ITenantContext tenant, IOrgUserDirectory? users = null)
    {
        _svc    = svc;
        _tenant = tenant;
        _users  = users;
    }

    private Task<ArApCaller> CallerAsync() => ArApCaller.FromAsync(User, _tenant, _users, HttpContext.RequestAborted);

    [HttpGet]
    [RequirePermission(P.AR_VIEW, P.CREDIT_CHECK, P.CREDIT_MANAGE, P.AP_VIEW, P.AP_EXECUTE, P.FINANCE_AR_AP_ADMIN)]
    public async Task<IActionResult> List([FromQuery] PartnerProfileListQuery query) =>
        Ok(ApiResponse<PaginatedResponse<PartnerFinancialProfileModel>>.Ok(
            await _svc.ListAsync(await CallerAsync(), query, HttpContext.RequestAborted)));

    [HttpGet("{partnerUuid:guid}")]
    [RequirePermission(P.AR_VIEW, P.CREDIT_CHECK, P.CREDIT_MANAGE, P.AP_VIEW, P.AP_EXECUTE, P.FINANCE_AR_AP_ADMIN)]
    public async Task<IActionResult> Get(Guid partnerUuid, [FromQuery] string? role) =>
        Ok(ApiResponse<PartnerFinancialProfileModel>.Ok(
            await _svc.GetModelAsync(await CallerAsync(), partnerUuid, role, HttpContext.RequestAborted)));

    [HttpPut("{partnerUuid:guid}")]
    [RequirePermission(P.FINANCE_AR_AP_ADMIN)]
    [RequiresFeature("MODULE_FINANCE")]
    public async Task<IActionResult> Update(Guid partnerUuid, [FromQuery] string? role, [FromBody] UpdatePartnerFinancialProfileRequest? request) =>
        Ok(ApiResponse<PartnerFinancialProfileModel>.Ok(
            await _svc.UpdateAsync(await CallerAsync(), partnerUuid, role, request ?? new UpdatePartnerFinancialProfileRequest(), HttpContext.RequestAborted),
            "Financial profile saved."));
}

/// <summary>A48 (CORE) — API-CONTRACT §1.4: the A48 organization settings (credit control mode, D-5).</summary>
[ApiController]
[Route("api/finance/ar-ap/settings")]
public class ArApSettingsController : ControllerBase
{
    private readonly IPartnerProfileEndpoints _svc;
    private readonly ITenantContext _tenant;
    private readonly IOrgUserDirectory? _users;

    public ArApSettingsController(IPartnerProfileEndpoints svc, ITenantContext tenant, IOrgUserDirectory? users = null)
    {
        _svc    = svc;
        _tenant = tenant;
        _users  = users;
    }

    private Task<ArApCaller> CallerAsync() => ArApCaller.FromAsync(User, _tenant, _users, HttpContext.RequestAborted);

    [HttpGet]
    [RequirePermission(
        P.AR_VIEW, P.AR_STATEMENT_SEND, P.AR_CREDIT_MEMO, P.CREDIT_CHECK, P.CREDIT_MANAGE, P.CREDIT_OVERRIDE, P.DUNNING_EXECUTE,
        P.DUNNING_ADMIN, P.AP_VIEW, P.AP_EXECUTE, P.AP_DEBIT_MEMO, P.MATCH_EXECUTE, P.MATCH_OVERRIDE, P.MATCH_APPROVE,
        P.PAYMENT_RUN_PROPOSE, P.PAYMENT_RUN_EDIT, P.PAYMENT_RUN_APPROVE, P.PAYMENT_RUN_EXECUTE, P.PAYMENT_RUN_ADMIN, P.FINANCE_AR_AP_ADMIN)]
    public async Task<IActionResult> Get() =>
        Ok(ApiResponse<ArApSettingsModel>.Ok(await _svc.GetSettingsAsync(await CallerAsync(), HttpContext.RequestAborted)));

    [HttpPut]
    [RequirePermission(P.FINANCE_AR_AP_ADMIN)]
    [RequiresFeature("MODULE_FINANCE")]
    public async Task<IActionResult> Put([FromBody] ArApSettingsModel? request) =>
        Ok(ApiResponse<ArApSettingsModel>.Ok(
            await _svc.UpdateSettingsAsync(await CallerAsync(), request ?? new ArApSettingsModel(), HttpContext.RequestAborted),
            "Receivables & payables settings saved."));
}

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.ArAp.Receivables;

// A48 AR — the HTTP surface of credit control (§2), dunning (§3) and AR credit memos (§4) of API-CONTRACT.md. Refusals come
// back from the services as BadRequest / NotFound / Conflict / Forbidden exceptions (400 / 404 / 409 / 403, global
// middleware). Writes carry [RequiresFeature("MODULE_FINANCE")]; reads none (contract §0).

/// <summary>Shared: who is calling (organization, user id, a name snapshot from the org user directory, permissions).</summary>
public abstract class ArControllerBase : ControllerBase
{
    private readonly IServiceProvider _services;

    protected ArControllerBase(IServiceProvider services) => _services = services;

    protected Task<ArApCaller> CallerAsync(CancellationToken ct) =>
        ArApCaller.FromAsync(User, _services.GetRequiredService<ITenantContext>(), _services.GetService<IOrgUserDirectory>(), ct);
}

[ApiController]
[Route("api/finance/credit")]
public sealed class CreditControlController : ArControllerBase
{
    private readonly CreditControlService _svc;

    internal CreditControlController(CreditControlService svc, IServiceProvider services) : base(services) => _svc = svc;

    [ActivatorUtilitiesConstructor]
    public CreditControlController(IServiceProvider services) : this(services.GetRequiredService<CreditControlService>(), services) { }

    [HttpGet("exposures")]
    [RequirePermission(PermissionCodes.AR_VIEW, PermissionCodes.CREDIT_MANAGE)]
    public async Task<IActionResult> List([FromQuery] CreditExposureFilter filter, CancellationToken ct) =>
        Ok(ApiResponse<PaginatedResponse<CreditExposureModel>>.Ok(await _svc.ListExposuresAsync(filter, ct)));

    [HttpGet("{customerUuid:guid}/exposure")]
    [RequirePermission(PermissionCodes.AR_VIEW, PermissionCodes.CREDIT_CHECK, PermissionCodes.CREDIT_MANAGE)]
    public async Task<IActionResult> Exposure(Guid customerUuid, CancellationToken ct) =>
        Ok(ApiResponse<CreditExposureModel>.Ok(await _svc.GetExposureAsync(customerUuid, ct)));

    [HttpPost("{customerUuid:guid}/check")]
    [RequirePermission(PermissionCodes.CREDIT_CHECK)]
    public async Task<IActionResult> Check(Guid customerUuid, [FromBody] CreditCheckBody body, CancellationToken ct)
    {
        var caller = await CallerAsync(ct);
        await _svc.GetExposureAsync(customerUuid, ct);   // 404 / 400 for an unknown or non-customer partner
        var result = await _svc.CheckAsync(new CreditCheckRequest(
            caller.OrganizationId, customerUuid, body?.Amount ?? 0m, body?.CurrencyCode, null, CreditControlService.DocManual), ct);
        return Ok(ApiResponse<CreditCheckResult>.Ok(result));
    }

    [HttpPost("{customerUuid:guid}/hold")]
    [RequiresFeature("MODULE_FINANCE")]
    [RequirePermission(PermissionCodes.CREDIT_MANAGE)]
    public async Task<IActionResult> Hold(Guid customerUuid, [FromBody] CreditReasonBody body, CancellationToken ct)
    {
        var caller = await CallerAsync(ct);
        return Ok(ApiResponse<CreditExposureModel>.Ok(
            await _svc.PlaceHoldAsync(customerUuid, body?.Reason, caller.UserIdOrNull, caller.UserName, ct), "Customer placed on credit hold."));
    }

    [HttpPost("{customerUuid:guid}/release")]
    [RequiresFeature("MODULE_FINANCE")]
    [RequirePermission(PermissionCodes.CREDIT_MANAGE)]
    public async Task<IActionResult> Release(Guid customerUuid, [FromBody] CreditReasonBody body, CancellationToken ct)
    {
        var caller = await CallerAsync(ct);
        return Ok(ApiResponse<CreditExposureModel>.Ok(
            await _svc.ReleaseHoldAsync(customerUuid, body?.Reason, caller.UserIdOrNull, caller.UserName, ct), "Credit hold released."));
    }

    [HttpPost("{customerUuid:guid}/temporary-limit")]
    [RequiresFeature("MODULE_FINANCE")]
    [RequirePermission(PermissionCodes.CREDIT_MANAGE)]
    public async Task<IActionResult> TemporaryLimit(Guid customerUuid, [FromBody] TemporaryLimitBody body, CancellationToken ct)
    {
        var caller = await CallerAsync(ct);
        return Ok(ApiResponse<CreditExposureModel>.Ok(await _svc.SetTemporaryLimitAsync(
            customerUuid, body?.Amount, body?.ExpiryDate, body?.Reason, caller.UserIdOrNull, caller.UserName, ct),
            body?.Amount is null ? "Temporary credit limit cleared." : "Temporary credit limit set."));
    }

    [HttpPut("{customerUuid:guid}/risk-tier")]
    [RequiresFeature("MODULE_FINANCE")]
    [RequirePermission(PermissionCodes.CREDIT_MANAGE)]
    public async Task<IActionResult> RiskTier(Guid customerUuid, [FromBody] RiskTierBody body, CancellationToken ct)
    {
        var caller = await CallerAsync(ct);
        return Ok(ApiResponse<CreditExposureModel>.Ok(
            await _svc.SetRiskTierAsync(customerUuid, body?.RiskTier, body?.Reason, caller.UserIdOrNull, caller.UserName, ct), "Risk tier updated."));
    }

    [HttpGet("{customerUuid:guid}/history")]
    [RequirePermission(PermissionCodes.AR_VIEW, PermissionCodes.CREDIT_MANAGE)]
    public async Task<IActionResult> History(Guid customerUuid, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        Ok(ApiResponse<PaginatedResponse<CreditHistoryModel>>.Ok(await _svc.HistoryAsync(customerUuid, page, pageSize, ct)));
}

[ApiController]
[Route("api/finance/dunning")]
public sealed class DunningController : ArControllerBase
{
    private readonly DunningService _svc;

    internal DunningController(DunningService svc, IServiceProvider services) : base(services) => _svc = svc;

    [ActivatorUtilitiesConstructor]
    public DunningController(IServiceProvider services) : this(services.GetRequiredService<DunningService>(), services) { }

    [HttpGet("procedures")]
    [RequirePermission(PermissionCodes.AR_VIEW, PermissionCodes.DUNNING_EXECUTE, PermissionCodes.DUNNING_ADMIN, PermissionCodes.FINANCE_AR_AP_ADMIN)]
    public async Task<IActionResult> Procedures(CancellationToken ct) =>
        Ok(ApiResponse<List<DunningProcedureModel>>.Ok(await _svc.ListProceduresAsync(await CallerAsync(ct), ct)));

    [HttpGet("procedures/{uuid:guid}")]
    [RequirePermission(PermissionCodes.AR_VIEW, PermissionCodes.DUNNING_EXECUTE, PermissionCodes.DUNNING_ADMIN, PermissionCodes.FINANCE_AR_AP_ADMIN)]
    public async Task<IActionResult> Procedure(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<DunningProcedureModel>.Ok(await _svc.GetProcedureAsync(uuid, await CallerAsync(ct), ct)));

    [HttpPost("procedures")]
    [RequiresFeature("MODULE_FINANCE")]
    [RequirePermission(PermissionCodes.FINANCE_AR_AP_ADMIN)]
    public async Task<IActionResult> CreateProcedure([FromBody] DunningProcedureModel body, CancellationToken ct) =>
        Ok(ApiResponse<DunningProcedureModel>.Ok(await _svc.CreateProcedureAsync(body, await CallerAsync(ct), ct), "Dunning procedure created."));

    [HttpPut("procedures/{uuid:guid}")]
    [RequiresFeature("MODULE_FINANCE")]
    [RequirePermission(PermissionCodes.FINANCE_AR_AP_ADMIN)]
    public async Task<IActionResult> UpdateProcedure(Guid uuid, [FromBody] DunningProcedureModel body, CancellationToken ct) =>
        Ok(ApiResponse<DunningProcedureModel>.Ok(await _svc.UpdateProcedureAsync(uuid, body, await CallerAsync(ct), ct), "Dunning procedure updated."));

    [HttpPost("runs/preview")]
    [RequiresFeature("MODULE_FINANCE")]
    [RequirePermission(PermissionCodes.DUNNING_EXECUTE)]
    public async Task<IActionResult> Preview([FromBody] DunningPreviewRequest? body, CancellationToken ct) =>
        Ok(ApiResponse<DunningRunModel>.Ok(await _svc.PreviewAsync(body ?? new DunningPreviewRequest(), await CallerAsync(ct), ct), "Dunning run proposed."));

    [HttpPost("runs/{uuid:guid}/items/{itemUuid:guid}/skip")]
    [RequiresFeature("MODULE_FINANCE")]
    [RequirePermission(PermissionCodes.DUNNING_EXECUTE)]
    public async Task<IActionResult> Skip(Guid uuid, Guid itemUuid, [FromBody] DunningSkipBody body, CancellationToken ct) =>
        Ok(ApiResponse<DunningRunModel>.Ok(await _svc.SkipItemAsync(uuid, itemUuid, body?.Reason, await CallerAsync(ct), ct), "Item skipped."));

    [HttpPost("runs/{uuid:guid}/execute")]
    [RequiresFeature("MODULE_FINANCE")]
    [RequirePermission(PermissionCodes.DUNNING_EXECUTE)]
    public async Task<IActionResult> Execute(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<DunningRunModel>.Ok(await _svc.ExecuteAsync(uuid, await CallerAsync(ct), ct), "Dunning run executed."));

    [HttpPost("runs/{uuid:guid}/cancel")]
    [RequiresFeature("MODULE_FINANCE")]
    [RequirePermission(PermissionCodes.DUNNING_EXECUTE)]
    public async Task<IActionResult> Cancel(Guid uuid, [FromBody] DunningCancelBody? body, CancellationToken ct) =>
        Ok(ApiResponse<DunningRunModel>.Ok(await _svc.CancelRunAsync(uuid, body?.Reason, await CallerAsync(ct), ct), "Dunning run cancelled."));

    [HttpGet("runs")]
    [RequirePermission(PermissionCodes.AR_VIEW, PermissionCodes.DUNNING_EXECUTE, PermissionCodes.DUNNING_ADMIN)]
    public async Task<IActionResult> Runs([FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        Ok(ApiResponse<PaginatedResponse<DunningRunModel>>.Ok(await _svc.ListRunsAsync(page, pageSize, await CallerAsync(ct), ct)));

    [HttpGet("runs/{uuid:guid}")]
    [RequirePermission(PermissionCodes.AR_VIEW, PermissionCodes.DUNNING_EXECUTE, PermissionCodes.DUNNING_ADMIN)]
    public async Task<IActionResult> Run(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<DunningRunModel>.Ok(await _svc.GetRunAsync(uuid, await CallerAsync(ct), ct)));

    [HttpGet("customers/{customerUuid:guid}/history")]
    [RequirePermission(PermissionCodes.AR_VIEW, PermissionCodes.DUNNING_EXECUTE, PermissionCodes.DUNNING_ADMIN)]
    public async Task<IActionResult> History(Guid customerUuid, CancellationToken ct) =>
        Ok(ApiResponse<DunningHistoryModel>.Ok(await _svc.CustomerHistoryAsync(customerUuid, await CallerAsync(ct), ct)));

    [HttpPost("customers/{customerUuid:guid}/reset")]
    [RequiresFeature("MODULE_FINANCE")]
    [RequirePermission(PermissionCodes.DUNNING_ADMIN)]
    public async Task<IActionResult> Reset(Guid customerUuid, [FromBody] DunningResetBody body, CancellationToken ct) =>
        Ok(ApiResponse<DunningHistoryModel>.Ok(await _svc.ResetAsync(customerUuid, body?.Reason, body?.Level, await CallerAsync(ct), ct), "Dunning level reset."));
}

[ApiController]
[Route("api/finance/ar")]
public sealed class ArCreditMemoController : ArControllerBase
{
    private readonly ArCreditMemoService _svc;

    internal ArCreditMemoController(ArCreditMemoService svc, IServiceProvider services) : base(services) => _svc = svc;

    [ActivatorUtilitiesConstructor]
    public ArCreditMemoController(IServiceProvider services) : this(services.GetRequiredService<ArCreditMemoService>(), services) { }

    [HttpPost("credit-memo")]
    [RequiresFeature("MODULE_FINANCE")]
    [RequirePermission(PermissionCodes.AR_CREDIT_MEMO)]
    public async Task<IActionResult> Create([FromBody] CreateCreditMemoRequest body, CancellationToken ct) =>
        Ok(ApiResponse<CreditMemoModel>.Ok(await _svc.CreateAsync(body, await CallerAsync(ct), ct), "Credit memo created."));

    [HttpPost("credit-memo/{creditNoteUuid:guid}/apply")]
    [RequiresFeature("MODULE_FINANCE")]
    [RequirePermission(PermissionCodes.AR_CREDIT_MEMO)]
    public async Task<IActionResult> Apply(Guid creditNoteUuid, [FromBody] ApplyCreditMemoRequest body, CancellationToken ct) =>
        Ok(ApiResponse<CreditMemoModel>.Ok(await _svc.ApplyAsync(creditNoteUuid, body, await CallerAsync(ct), ct), "Credit applied."));

    [HttpGet("credit-memo/{creditNoteUuid:guid}")]
    [RequirePermission(PermissionCodes.AR_VIEW, PermissionCodes.AR_CREDIT_MEMO)]
    public async Task<IActionResult> Get(Guid creditNoteUuid, CancellationToken ct) =>
        Ok(ApiResponse<CreditMemoModel>.Ok(await _svc.GetAsync(creditNoteUuid, await CallerAsync(ct), ct)));

    [HttpGet("credit-memo/{creditNoteUuid:guid}/applications")]
    [RequirePermission(PermissionCodes.AR_VIEW, PermissionCodes.AR_CREDIT_MEMO)]
    public async Task<IActionResult> Applications(Guid creditNoteUuid, CancellationToken ct) =>
        Ok(ApiResponse<List<CreditApplicationModel>>.Ok(await _svc.GetApplicationsAsync(creditNoteUuid, await CallerAsync(ct), ct)));

    [HttpGet("customers/{customerUuid:guid}/open-credits")]
    [RequirePermission(PermissionCodes.AR_VIEW, PermissionCodes.AR_CREDIT_MEMO)]
    public async Task<IActionResult> OpenCredits(Guid customerUuid, CancellationToken ct) =>
        Ok(ApiResponse<List<CreditMemoModel>>.Ok(await _svc.OpenCreditsAsync(customerUuid, await CallerAsync(ct), ct)));
}

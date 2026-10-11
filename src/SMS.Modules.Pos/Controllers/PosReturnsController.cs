using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SMS.Modules.Pos.Models;
using SMS.Modules.Pos.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Pagination;
using P = SMS.Modules.Pos.Models.PosPermissions;

namespace SMS.Modules.Pos.Controllers;

/// <summary>A39 — a <see cref="PosRuleException"/> answers 400 with its <c>result</c> (e.g. <c>{ requiresManager, reasons }</c>).</summary>
public sealed class PosRuleResultFilter : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        if (context.Exception is not PosRuleException rule) return;
        context.Result = new BadRequestObjectResult(ApiResponse.Fail(rule.Message, rule.Result));
        context.ExceptionHandled = true;
    }
}

/// <summary>A39 API-CONTRACT §1–§3, §5 — reasons, policies, lookup, manager authorization, exchange, return sync.</summary>
[ApiController]
[RequiresFeature(ModuleCodes.Pos)]
[PosRuleResultFilter]
public class PosReturnsController : ControllerBase
{
    private readonly IPosReturnPolicyService _policies;
    private readonly IPosOrderService        _orders;

    public PosReturnsController(IPosReturnPolicyService policies, IPosOrderService orders)
    {
        _policies = policies;
        _orders   = orders;
    }

    private PosCaller Caller => PosCaller.From(User);

    // ── §1 reasons ──

    [HttpGet("api/pos/return-reasons")]
    [RequirePermission(P.OrderReturn, P.ReturnReasonManage)]
    public async Task<IActionResult> Reasons([FromQuery] PosReasonListFilter filter, CancellationToken ct) =>
        Ok(ApiResponse<IReadOnlyList<PosReturnReasonModel>>.Ok(await _policies.ListReasonsAsync(filter, ct)));

    [HttpPost("api/pos/return-reasons")]
    [RequirePermission(P.ReturnReasonManage)]
    public async Task<IActionResult> CreateReason([FromBody] PosReturnReasonUpsertRequest req, CancellationToken ct) =>
        Ok(ApiResponse<PosReturnReasonModel>.Ok(await _policies.CreateReasonAsync(req, ct), "Return reason created."));

    [HttpPut("api/pos/return-reasons/{uuid:guid}")]
    [RequirePermission(P.ReturnReasonManage)]
    public async Task<IActionResult> UpdateReason(Guid uuid, [FromBody] PosReturnReasonUpsertRequest req, CancellationToken ct) =>
        Ok(ApiResponse<PosReturnReasonModel>.Ok(await _policies.UpdateReasonAsync(uuid, req, ct), "Return reason saved."));

    [HttpPatch("api/pos/return-reasons/{uuid:guid}/status")]
    [RequirePermission(P.ReturnReasonManage)]
    public async Task<IActionResult> ReasonStatus(Guid uuid, [FromBody] PosStatusRequest req, CancellationToken ct) =>
        Ok(ApiResponse<PosReturnReasonModel>.Ok(await _policies.SetReasonStatusAsync(uuid, req.IsActive, ct), "Return reason saved."));

    // ── §1 policies ──

    [HttpGet("api/pos/stores/{uuid:guid}/return-policy")]
    [RequirePermission(P.StoreView, P.OrderReturn)]
    public async Task<IActionResult> StorePolicy(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<PosEffectivePolicyModel>.Ok(await _policies.GetEffectiveAsync(uuid, ct)));

    [HttpPut("api/pos/stores/{uuid:guid}/return-policy")]
    [RequirePermission(P.ReturnPolicyManage)]
    public async Task<IActionResult> PutStorePolicy(Guid uuid, [FromBody] PosReturnPolicyUpsertRequest req, CancellationToken ct) =>
        Ok(ApiResponse<PosReturnPolicyModel>.Ok(await _policies.PutStorePolicyAsync(uuid, req, User.GetUserId(), ct), "Return policy saved."));

    [HttpDelete("api/pos/stores/{uuid:guid}/return-policy")]
    [RequirePermission(P.ReturnPolicyManage)]
    public async Task<IActionResult> DeleteStorePolicy(Guid uuid, CancellationToken ct)
    {
        await _policies.DeleteStorePolicyAsync(uuid, ct);
        return Ok(ApiResponse.Ok("The store now follows the organization's default policy."));
    }

    [HttpGet("api/pos/return-policies")]
    [RequirePermission(P.ReturnPolicyManage)]
    public async Task<IActionResult> Policies(CancellationToken ct) =>
        Ok(ApiResponse<IReadOnlyList<PosReturnPolicyModel>>.Ok(await _policies.ListPoliciesAsync(ct)));

    [HttpPut("api/pos/return-policies/default")]
    [RequirePermission(P.ReturnPolicyManage)]
    public async Task<IActionResult> PutDefaultPolicy([FromBody] PosReturnPolicyUpsertRequest req, CancellationToken ct) =>
        Ok(ApiResponse<PosReturnPolicyModel>.Ok(await _policies.PutDefaultPolicyAsync(req, User.GetUserId(), ct), "Return policy saved."));

    // ── §2 lookup ──

    [HttpGet("api/pos/orders/lookup")]
    [RequirePermission(P.OrderReturn)]
    public async Task<IActionResult> Lookup([FromQuery] string? receipt, [FromQuery] Guid? storeUuid, CancellationToken ct) =>
        Ok(ApiResponse<PosOrderLookupModel>.Ok(await _orders.LookupAsync(receipt, storeUuid, ct)));

    [HttpGet("api/pos/orders/{uuid:guid}/returnable-lines")]
    [RequirePermission(P.OrderReturn)]
    public async Task<IActionResult> ReturnableLines(Guid uuid, [FromQuery] Guid? storeUuid, CancellationToken ct) =>
        Ok(ApiResponse<PosOrderLookupModel>.Ok(await _orders.ReturnableLinesAsync(uuid, storeUuid, ct)));

    // ── §3 authorization, exchange, sync ──

    [HttpPost("api/pos/returns/authorize")]
    [RequirePermission(P.OrderReturn)]
    public async Task<IActionResult> Authorize([FromBody] PosReturnAuthorizeRequest req, CancellationToken ct) =>
        Ok(ApiResponse<PosReturnAuthorizationModel>.Ok(await _orders.AuthorizeReturnAsync(req, Caller, ct), "Return authorized."));

    [HttpPost("api/pos/orders/{uuid:guid}/exchange")]
    [RequirePermission(P.OrderReturn)]
    [RequiresFeature(PosFeatureCodes.Returns)]
    public async Task<IActionResult> Exchange(Guid uuid, [FromBody] PosExchangeRequest req, CancellationToken ct) =>
        Ok(ApiResponse<PosOrderModel>.Ok(await _orders.ExchangeAsync(uuid, req, Caller, ct), "Exchange recorded."));

    // Offline returns already happened at the till: they land during grace like offline sales.
    [HttpPost("api/pos/returns/sync")]
    [RequirePermission(P.OrderReturn)]
    [AllowDuringGrace]
    public async Task<IActionResult> Sync([FromBody] PosReturnSyncRequest req, CancellationToken ct) =>
        Ok(ApiResponse<PosSyncResultModel>.Ok(await _orders.SyncReturnAsync(req, Caller, ct), "Return synced."));

    // ── §5 PWA sync ──

    [HttpGet("api/pos/sync/return-reasons")]
    [RequirePermission(P.OrderCreate)]
    public async Task<IActionResult> SyncReasons([FromQuery] DateTimeOffset? since, CancellationToken ct) =>
        Ok(ApiResponse<PosReasonSyncModel>.Ok(await _policies.SyncReasonsAsync(since?.UtcDateTime, ct)));

    [HttpGet("api/pos/sync/return-policy")]
    [RequirePermission(P.OrderCreate)]
    public async Task<IActionResult> SyncPolicy([FromQuery] Guid terminalUuid, CancellationToken ct) =>
        Ok(ApiResponse<PosEffectivePolicyModel>.Ok(await _policies.GetEffectiveForTerminalAsync(terminalUuid, ct)));
}

/// <summary>A39 API-CONTRACT §4 — store credit notes.</summary>
[ApiController]
[RequiresFeature(ModuleCodes.Pos)]
public class PosStoreCreditController : ControllerBase
{
    private readonly IPosStoreCreditService _credit;
    public PosStoreCreditController(IPosStoreCreditService credit) => _credit = credit;

    private PosCaller Caller => PosCaller.From(User);

    [HttpPost("api/pos/credit-notes")]
    [RequirePermission(P.CreditNoteIssue)]
    [RequiresFeature(PosFeatureCodes.CreditNotes)]
    public async Task<IActionResult> Issue([FromBody] PosStoreCreditIssueRequest req, CancellationToken ct) =>
        Ok(ApiResponse<PosStoreCreditModel>.Ok(await _credit.IssueAsync(req, Caller, ct), "Store credit issued."));

    [HttpGet("api/pos/credit-notes")]
    [RequirePermission(P.CreditNoteView)]
    public async Task<IActionResult> List([FromQuery] PosStoreCreditListFilter filter, CancellationToken ct) =>
        Ok(ApiResponse<PaginatedResponse<PosStoreCreditModel>>.Ok(await _credit.ListAsync(filter, ct)));

    [HttpGet("api/pos/credit-notes/lookup")]
    [RequirePermission(P.CreditNoteView, P.CreditNoteRedeem)]
    public async Task<IActionResult> Lookup([FromQuery] string? number, CancellationToken ct) =>
        Ok(ApiResponse<PosStoreCreditModel>.Ok(await _credit.LookupAsync(number, ct)));

    [HttpGet("api/pos/credit-notes/{uuid:guid}")]
    [RequirePermission(P.CreditNoteView)]
    public async Task<IActionResult> Get(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<PosStoreCreditDetailModel>.Ok(await _credit.GetAsync(uuid, ct)));

    [HttpGet("api/pos/customers/{customerUuid:guid}/credit-notes")]
    [RequirePermission(P.CreditNoteView, P.CreditNoteRedeem)]
    public async Task<IActionResult> ForCustomer(Guid customerUuid, [FromQuery] string? status, CancellationToken ct) =>
        Ok(ApiResponse<IReadOnlyList<PosStoreCreditModel>>.Ok(await _credit.ListForCustomerAsync(customerUuid, status, ct)));

    [HttpPatch("api/pos/credit-notes/{uuid:guid}/cancel")]
    [RequirePermission(P.CreditNoteCancel)]
    public async Task<IActionResult> Cancel(Guid uuid, [FromBody] PosStoreCreditCancelRequest? req, CancellationToken ct) =>
        Ok(ApiResponse<PosStoreCreditModel>.Ok(await _credit.CancelAsync(uuid, req?.Note, Caller, ct), "Store credit cancelled."));

    [HttpGet("api/pos/credit-notes/{uuid:guid}/transactions")]
    [RequirePermission(P.CreditNoteView)]
    public async Task<IActionResult> Transactions(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<IReadOnlyList<PosStoreCreditTxnModel>>.Ok(await _credit.TransactionsAsync(uuid, ct)));
}

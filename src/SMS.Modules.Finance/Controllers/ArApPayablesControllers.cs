using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Finance.ArAp;
using SMS.Modules.Finance.ArAp.Payables;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Pagination;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Finance.Controllers;

// A48 AP — API-CONTRACT §6 (matching), §7 (schedules, early-payment terms), §8 (payment runs), §9 (debit memos).
// Reads carry no feature gate; every write carries MODULE_FINANCE (§0).

/// <summary>The caller as the AP services take it (int id + name snapshot, §0 "Who").</summary>
public abstract class ApControllerBase : ControllerBase
{
    private readonly ITenantContext _tenant;
    private readonly IOrgUserDirectory? _users;

    protected ApControllerBase(ITenantContext tenant, IOrgUserDirectory? users)
    {
        _tenant = tenant;
        _users  = users;
    }

    protected CancellationToken Ct => HttpContext?.RequestAborted ?? CancellationToken.None;

    protected async Task<ApActor> ActorAsync()
    {
        var caller = await ArApCaller.FromAsync(User, _tenant, _users, Ct);
        return new ApActor(caller.UserIdOrNull, caller.UserName);
    }
}

/// <summary>§6 — the per-line three-way / two-way match of supplier invoices, and the tolerance rules.</summary>
[ApiController]
[Route("api/finance/matching")]
public class InvoiceMatchingController : ApControllerBase
{
    private readonly IInvoiceMatchingService _matching;
    private readonly IToleranceRuleService _rules;

    public InvoiceMatchingController(IInvoiceMatchingService matching, IToleranceRuleService rules, ITenantContext tenant, IOrgUserDirectory? users = null)
        : base(tenant, users)
    {
        _matching = matching;
        _rules    = rules;
    }

    [HttpPost("invoices/{invoiceUuid:guid}/match")]
    [RequirePermission(P.MATCH_EXECUTE)]
    [RequiresFeature("MODULE_FINANCE")]
    public async Task<IActionResult> Match(Guid invoiceUuid, [FromBody] RunMatchRequest? request) =>
        Ok(ApiResponse<MatchDetailModel>.Ok(await _matching.MatchAsync(invoiceUuid, request?.MatchType, await ActorAsync(), Ct), "Invoice matched."));

    [HttpGet("invoices/{invoiceUuid:guid}")]
    [RequirePermission(P.AP_VIEW, P.MATCH_EXECUTE, P.MATCH_OVERRIDE, P.MATCH_APPROVE)]
    public async Task<IActionResult> Get(Guid invoiceUuid) =>
        Ok(ApiResponse<MatchDetailModel>.Ok(await _matching.GetAsync(invoiceUuid, Ct)));

    [HttpPost("invoices/{invoiceUuid:guid}/override")]
    [RequirePermission(P.MATCH_OVERRIDE)]
    [RequiresFeature("MODULE_FINANCE")]
    public async Task<IActionResult> Override(Guid invoiceUuid, [FromBody] OverrideMatchRequest? request) =>
        Ok(ApiResponse<MatchDetailModel>.Ok(await _matching.OverrideAsync(invoiceUuid, request?.Reason, await ActorAsync(), Ct), "Match variance overridden."));

    [HttpPost("invoices/{invoiceUuid:guid}/approve")]
    [RequirePermission(P.MATCH_APPROVE)]
    [RequiresFeature("MODULE_FINANCE")]
    public async Task<IActionResult> Approve(Guid invoiceUuid) =>
        Ok(ApiResponse<MatchDetailModel>.Ok(await _matching.ApproveAsync(invoiceUuid, await ActorAsync(), Ct), "Invoice approved."));

    /// <summary>FE-6 — reject from the matching page (the existing supplier-invoice rejection).</summary>
    [HttpPost("invoices/{invoiceUuid:guid}/reject")]
    [RequirePermission(P.MATCH_APPROVE)]
    [RequiresFeature("MODULE_FINANCE")]
    public async Task<IActionResult> Reject(Guid invoiceUuid, [FromBody] OverrideMatchRequest? request) =>
        Ok(ApiResponse<MatchDetailModel>.Ok(await _matching.RejectAsync(invoiceUuid, request?.Reason, await ActorAsync(), Ct), "Invoice rejected."));

    [HttpGet("pending")]
    [RequirePermission(P.AP_VIEW, P.MATCH_EXECUTE, P.MATCH_OVERRIDE, P.MATCH_APPROVE)]
    public async Task<IActionResult> Pending([FromQuery] Guid? vendorUuid, [FromQuery] string? result, [FromQuery] int? page, [FromQuery] int? pageSize,
        [FromQuery] string? search) =>
        Ok(ApiResponse<PaginatedResponse<MatchSummaryModel>>.Ok(await _matching.ListPendingAsync(vendorUuid, result, page, pageSize, Ct, search)));

    [HttpGet("po/{poUuid:guid}")]
    [RequirePermission(P.AP_VIEW, P.MATCH_EXECUTE, P.MATCH_OVERRIDE, P.MATCH_APPROVE)]
    public async Task<IActionResult> ByPurchaseOrder(Guid poUuid) =>
        Ok(ApiResponse<IReadOnlyList<MatchSummaryModel>>.Ok(await _matching.ByPurchaseOrderAsync(poUuid, Ct)));

    [HttpGet("tolerances")]
    [RequirePermission(P.AP_VIEW, P.MATCH_EXECUTE, P.MATCH_OVERRIDE, P.MATCH_APPROVE, P.FINANCE_AR_AP_ADMIN)]
    public async Task<IActionResult> Tolerances() =>
        Ok(ApiResponse<IReadOnlyList<ToleranceRuleModel>>.Ok(await _rules.ListAsync(Ct)));

    [HttpPost("tolerances")]
    [RequirePermission(P.FINANCE_AR_AP_ADMIN)]
    [RequiresFeature("MODULE_FINANCE")]
    public async Task<IActionResult> CreateTolerance([FromBody] ToleranceRuleRequest? request) =>
        Ok(ApiResponse<ToleranceRuleModel>.Ok(await _rules.CreateAsync(request ?? new ToleranceRuleRequest(), await ActorAsync(), Ct), "Tolerance rule created."));

    [HttpPut("tolerances/{uuid:guid}")]
    [RequirePermission(P.FINANCE_AR_AP_ADMIN)]
    [RequiresFeature("MODULE_FINANCE")]
    public async Task<IActionResult> UpdateTolerance(Guid uuid, [FromBody] ToleranceRuleRequest? request) =>
        Ok(ApiResponse<ToleranceRuleModel>.Ok(await _rules.UpdateAsync(uuid, request ?? new ToleranceRuleRequest(), await ActorAsync(), Ct), "Tolerance rule saved."));
}

/// <summary>§7 — payment schedules (C10) and the C15 discount calculation.</summary>
[ApiController]
[Route("api/finance/payment-schedules")]
public class PaymentSchedulesController : ApControllerBase
{
    private readonly IPaymentScheduleService _schedules;

    public PaymentSchedulesController(IPaymentScheduleService schedules, ITenantContext tenant, IOrgUserDirectory? users = null)
        : base(tenant, users) => _schedules = schedules;

    [HttpGet]
    [RequirePermission(P.AP_VIEW, P.AP_EXECUTE, P.PAYMENT_RUN_PROPOSE, P.PAYMENT_RUN_EDIT, P.PAYMENT_RUN_APPROVE, P.PAYMENT_RUN_EXECUTE, P.PAYMENT_RUN_ADMIN)]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] Guid? vendorUuid, [FromQuery] DateOnly? dueFrom,
        [FromQuery] DateOnly? dueTo, [FromQuery] int? page, [FromQuery] int? pageSize) =>
        Ok(ApiResponse<PaginatedResponse<PaymentScheduleModel>>.Ok(await _schedules.ListAsync(status, vendorUuid, dueFrom, dueTo, page, pageSize, Ct)));

    [HttpGet("upcoming")]
    [RequirePermission(P.AP_VIEW, P.AP_EXECUTE, P.PAYMENT_RUN_PROPOSE, P.PAYMENT_RUN_EDIT, P.PAYMENT_RUN_APPROVE, P.PAYMENT_RUN_EXECUTE, P.PAYMENT_RUN_ADMIN)]
    public async Task<IActionResult> Upcoming([FromQuery] DateOnly? from, [FromQuery] DateOnly? to) =>
        Ok(ApiResponse<IReadOnlyList<PaymentScheduleModel>>.Ok(await _schedules.UpcomingAsync(from, to, Ct)));

    [HttpGet("discounts")]
    [RequirePermission(P.AP_VIEW, P.AP_EXECUTE, P.PAYMENT_RUN_PROPOSE, P.PAYMENT_RUN_EDIT, P.PAYMENT_RUN_APPROVE, P.PAYMENT_RUN_EXECUTE, P.PAYMENT_RUN_ADMIN)]
    public async Task<IActionResult> Discounts([FromQuery] DateOnly? asOfDate, [FromQuery] int? lookAheadDays) =>
        Ok(ApiResponse<IReadOnlyList<PaymentScheduleModel>>.Ok(await _schedules.DiscountOpportunitiesAsync(asOfDate, lookAheadDays, Ct)));

    [HttpGet("cash-flow")]
    [RequirePermission(P.AP_VIEW, P.AP_EXECUTE, P.PAYMENT_RUN_PROPOSE, P.PAYMENT_RUN_EDIT, P.PAYMENT_RUN_APPROVE, P.PAYMENT_RUN_EXECUTE, P.PAYMENT_RUN_ADMIN)]
    public async Task<IActionResult> CashFlow([FromQuery] DateOnly? from, [FromQuery] int? weeks) =>
        Ok(ApiResponse<CashFlowForecastModel>.Ok(await _schedules.CashFlowAsync(from, weeks, Ct)));

    [HttpGet("{uuid:guid}/discount")]
    [RequirePermission(P.AP_VIEW, P.AP_EXECUTE, P.PAYMENT_RUN_PROPOSE, P.PAYMENT_RUN_EDIT, P.PAYMENT_RUN_APPROVE, P.PAYMENT_RUN_EXECUTE, P.PAYMENT_RUN_ADMIN)]
    public async Task<IActionResult> Discount(Guid uuid, [FromQuery] DateOnly? paymentDate) =>
        Ok(ApiResponse<DiscountCalculation>.Ok(await _schedules.CalculateDiscountAsync(uuid, paymentDate, Ct)));

    [HttpPost("{uuid:guid}/block")]
    [RequirePermission(P.AP_EXECUTE)]
    [RequiresFeature("MODULE_FINANCE")]
    public async Task<IActionResult> Block(Guid uuid, [FromBody] BlockScheduleRequest? request) =>
        Ok(ApiResponse<PaymentScheduleModel>.Ok(await _schedules.BlockAsync(uuid, request?.Reason, await ActorAsync(), Ct), "Payment blocked."));

    [HttpPost("{uuid:guid}/unblock")]
    [RequirePermission(P.AP_EXECUTE)]
    [RequiresFeature("MODULE_FINANCE")]
    public async Task<IActionResult> Unblock(Guid uuid) =>
        Ok(ApiResponse<PaymentScheduleModel>.Ok(await _schedules.UnblockAsync(uuid, await ActorAsync(), Ct), "Payment unblocked."));

    [HttpPut("{uuid:guid}")]
    [RequirePermission(P.AP_EXECUTE)]
    [RequiresFeature("MODULE_FINANCE")]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] UpdateScheduleRequest? request) =>
        Ok(ApiResponse<PaymentScheduleModel>.Ok(await _schedules.UpdateAsync(uuid, request ?? new UpdateScheduleRequest(), await ActorAsync(), Ct), "Payment schedule saved."));

    [HttpPost("{uuid:guid}/early-payment-term")]
    [RequirePermission(P.AP_EXECUTE)]
    [RequiresFeature("MODULE_FINANCE")]
    public async Task<IActionResult> SetTerm(Guid uuid, [FromBody] SetScheduleTermRequest? request) =>
        Ok(ApiResponse<PaymentScheduleModel>.Ok(await _schedules.SetEarlyPaymentTermAsync(uuid, request?.EarlyPaymentTermUuid, await ActorAsync(), Ct), "Early payment term applied."));
}

/// <summary>§7 — early-payment terms (C15).</summary>
[ApiController]
[Route("api/finance/early-payment-terms")]
public class EarlyPaymentTermsController : ApControllerBase
{
    private readonly IEarlyPaymentTermService _terms;

    public EarlyPaymentTermsController(IEarlyPaymentTermService terms, ITenantContext tenant, IOrgUserDirectory? users = null)
        : base(tenant, users) => _terms = terms;

    [HttpGet]
    [RequirePermission(P.AP_VIEW, P.FINANCE_AR_AP_ADMIN)]
    public async Task<IActionResult> List() =>
        Ok(ApiResponse<IReadOnlyList<EarlyPaymentTermModel>>.Ok(await _terms.ListAsync(Ct)));

    [HttpPost]
    [RequirePermission(P.FINANCE_AR_AP_ADMIN)]
    [RequiresFeature("MODULE_FINANCE")]
    public async Task<IActionResult> Create([FromBody] EarlyPaymentTermRequest? request) =>
        Ok(ApiResponse<EarlyPaymentTermModel>.Ok(await _terms.CreateAsync(request ?? new EarlyPaymentTermRequest(), await ActorAsync(), Ct), "Early payment term created."));

    [HttpPut("{uuid:guid}")]
    [RequirePermission(P.FINANCE_AR_AP_ADMIN)]
    [RequiresFeature("MODULE_FINANCE")]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] EarlyPaymentTermRequest? request) =>
        Ok(ApiResponse<EarlyPaymentTermModel>.Ok(await _terms.UpdateAsync(uuid, request ?? new EarlyPaymentTermRequest(), await ActorAsync(), Ct), "Early payment term saved."));
}

/// <summary>§8 — payment runs (C11).</summary>
[ApiController]
[Route("api/finance/payment-runs")]
public class PaymentRunsController : ApControllerBase
{
    private readonly IPaymentRunService _runs;

    public PaymentRunsController(IPaymentRunService runs, ITenantContext tenant, IOrgUserDirectory? users = null)
        : base(tenant, users) => _runs = runs;

    [HttpPost("propose")]
    [RequirePermission(P.PAYMENT_RUN_PROPOSE)]
    [RequiresFeature("MODULE_FINANCE")]
    public async Task<IActionResult> Propose([FromBody] ProposePaymentRunRequest? request) =>
        Ok(ApiResponse<PaymentRunDetailModel>.Ok(await _runs.ProposeAsync(request ?? new ProposePaymentRunRequest(), await ActorAsync(), Ct), "Payment run proposed."));

    [HttpGet]
    [RequirePermission(P.AP_VIEW, P.PAYMENT_RUN_PROPOSE, P.PAYMENT_RUN_EDIT, P.PAYMENT_RUN_APPROVE, P.PAYMENT_RUN_EXECUTE, P.PAYMENT_RUN_ADMIN)]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] int? page, [FromQuery] int? pageSize) =>
        Ok(ApiResponse<PaginatedResponse<PaymentRunSummaryModel>>.Ok(await _runs.ListAsync(status, page, pageSize, Ct)));

    [HttpGet("{uuid:guid}")]
    [RequirePermission(P.AP_VIEW, P.PAYMENT_RUN_PROPOSE, P.PAYMENT_RUN_EDIT, P.PAYMENT_RUN_APPROVE, P.PAYMENT_RUN_EXECUTE, P.PAYMENT_RUN_ADMIN)]
    public async Task<IActionResult> Get(Guid uuid) =>
        Ok(ApiResponse<PaymentRunDetailModel>.Ok(await _runs.GetAsync(uuid, Ct)));

    [HttpPost("{uuid:guid}/items/{itemUuid:guid}/remove")]
    [RequirePermission(P.PAYMENT_RUN_EDIT)]
    [RequiresFeature("MODULE_FINANCE")]
    public async Task<IActionResult> RemoveItem(Guid uuid, Guid itemUuid, [FromBody] RemoveRunItemRequest? request) =>
        Ok(ApiResponse<PaymentRunDetailModel>.Ok(await _runs.RemoveItemAsync(uuid, itemUuid, request?.Reason, await ActorAsync(), Ct), "Item removed."));

    [HttpDelete("{uuid:guid}/items/{itemUuid:guid}")]
    [RequirePermission(P.PAYMENT_RUN_EDIT)]
    [RequiresFeature("MODULE_FINANCE")]
    public async Task<IActionResult> DeleteItem(Guid uuid, Guid itemUuid, [FromBody] RemoveRunItemRequest? request) =>
        Ok(ApiResponse<PaymentRunDetailModel>.Ok(await _runs.RemoveItemAsync(uuid, itemUuid, request?.Reason, await ActorAsync(), Ct), "Item removed."));

    [HttpPost("{uuid:guid}/items/{itemUuid:guid}/restore")]
    [RequirePermission(P.PAYMENT_RUN_EDIT)]
    [RequiresFeature("MODULE_FINANCE")]
    public async Task<IActionResult> RestoreItem(Guid uuid, Guid itemUuid) =>
        Ok(ApiResponse<PaymentRunDetailModel>.Ok(await _runs.RestoreItemAsync(uuid, itemUuid, await ActorAsync(), Ct), "Item restored."));

    [HttpPut("{uuid:guid}/items/{itemUuid:guid}")]
    [RequirePermission(P.PAYMENT_RUN_EDIT)]
    [RequiresFeature("MODULE_FINANCE")]
    public async Task<IActionResult> UpdateItem(Guid uuid, Guid itemUuid, [FromBody] UpdateRunItemRequest? request) =>
        Ok(ApiResponse<PaymentRunDetailModel>.Ok(await _runs.UpdateItemMethodAsync(uuid, itemUuid, request?.PaymentMethod, await ActorAsync(), Ct), "Item saved."));

    [HttpPost("{uuid:guid}/review")]
    [RequirePermission(P.PAYMENT_RUN_EDIT)]
    [RequiresFeature("MODULE_FINANCE")]
    public async Task<IActionResult> Review(Guid uuid) =>
        Ok(ApiResponse<PaymentRunDetailModel>.Ok(await _runs.ReviewAsync(uuid, await ActorAsync(), Ct), "Payment run sent to review."));

    [HttpPost("{uuid:guid}/approve")]
    [RequirePermission(P.PAYMENT_RUN_APPROVE)]
    [RequiresFeature("MODULE_FINANCE")]
    public async Task<IActionResult> Approve(Guid uuid) =>
        Ok(ApiResponse<PaymentRunDetailModel>.Ok(await _runs.ApproveAsync(uuid, await ActorAsync(), Ct), "Payment run approved."));

    [HttpPost("{uuid:guid}/execute")]
    [RequirePermission(P.PAYMENT_RUN_EXECUTE)]
    [RequiresFeature("MODULE_FINANCE")]
    public async Task<IActionResult> Execute(Guid uuid) => Executed(await _runs.ExecuteAsync(uuid, await ActorAsync(), Ct));

    /// <summary>D-AP-13 — a run stuck Executing carries on with its Pending batches (Paid / Failed ones are skipped).</summary>
    [HttpPost("{uuid:guid}/resume")]
    [RequirePermission(P.PAYMENT_RUN_EXECUTE)]
    [RequiresFeature("MODULE_FINANCE")]
    public async Task<IActionResult> Resume(Guid uuid) => Executed(await _runs.ResumeAsync(uuid, await ActorAsync(), Ct));

    /// <summary>200 when the run completed in the request; 202 while a background job executes it (poll GET until Completed).</summary>
    private IActionResult Executed(PaymentRunDetailModel run) =>
        run.Run.Status == "Executing"
            ? StatusCode(StatusCodes.Status202Accepted, ApiResponse<PaymentRunDetailModel>.Ok(run, "Payment run is being executed in the background."))
            : Ok(ApiResponse<PaymentRunDetailModel>.Ok(run, "Payment run executed."));

    [HttpGet("{uuid:guid}/export")]
    [RequirePermission(P.PAYMENT_RUN_EXECUTE)]
    public async Task<IActionResult> Export(Guid uuid, [FromQuery] string? format)
    {
        var file = await _runs.ExportAsync(uuid, format, await ActorAsync(), Ct);
        return File(file.Content, file.ContentType, file.FileName);
    }

    [HttpPost("{uuid:guid}/cancel")]
    [RequirePermission(P.PAYMENT_RUN_ADMIN)]
    [RequiresFeature("MODULE_FINANCE")]
    public async Task<IActionResult> Cancel(Guid uuid, [FromBody] CancelRunRequest? request) =>
        Ok(ApiResponse<PaymentRunDetailModel>.Ok(await _runs.CancelAsync(uuid, request?.Reason, await ActorAsync(), Ct), "Payment run cancelled."));
}

/// <summary>§9 — AP debit memos (C14).</summary>
[ApiController]
[Route("api/finance/ap/debit-memo")]
public class ApDebitMemoController : ApControllerBase
{
    private readonly IApDebitMemoService _memos;

    public ApDebitMemoController(IApDebitMemoService memos, ITenantContext tenant, IOrgUserDirectory? users = null)
        : base(tenant, users) => _memos = memos;

    [HttpPost]
    [RequirePermission(P.AP_DEBIT_MEMO)]
    [RequiresFeature("MODULE_FINANCE")]
    public async Task<IActionResult> Create([FromBody] CreateApDebitMemoRequest? request) =>
        Ok(ApiResponse<ApDebitMemoModel>.Ok(await _memos.CreateAsync(request ?? new CreateApDebitMemoRequest(), await ActorAsync(), Ct), "Debit memo created."));

    [HttpPost("{debitNoteUuid:guid}/apply")]
    [RequirePermission(P.AP_DEBIT_MEMO)]
    [RequiresFeature("MODULE_FINANCE")]
    public async Task<IActionResult> Apply(Guid debitNoteUuid, [FromBody] ApplyApDebitMemoRequest? request) =>
        Ok(ApiResponse<ApDebitMemoModel>.Ok(await _memos.ApplyAsync(debitNoteUuid, request ?? new ApplyApDebitMemoRequest(), await ActorAsync(), Ct), "Debit memo applied."));

    [HttpGet("{debitNoteUuid:guid}/applications")]
    [RequirePermission(P.AP_VIEW, P.AP_DEBIT_MEMO)]
    public async Task<IActionResult> Applications(Guid debitNoteUuid) =>
        Ok(ApiResponse<IReadOnlyList<SupplierNoteApplicationModel>>.Ok(await _memos.ApplicationsAsync(debitNoteUuid, Ct)));

    /// <summary>FE-8 — one debit note (memo or SRO note) with remaining amount and applications.</summary>
    [HttpGet("{debitNoteUuid:guid}")]
    [RequirePermission(P.AP_VIEW, P.AP_DEBIT_MEMO)]
    public async Task<IActionResult> Get(Guid debitNoteUuid) =>
        Ok(ApiResponse<ApDebitMemoModel>.Ok(await _memos.GetAsync(debitNoteUuid, Ct)));

    /// <summary>FE-8 — a vendor's debit notes, newest first; openOnly = carried forward with an amount left to apply.</summary>
    [HttpGet("~/api/finance/ap/vendors/{vendorUuid:guid}/debit-memos")]
    [RequirePermission(P.AP_VIEW, P.AP_DEBIT_MEMO)]
    public async Task<IActionResult> ByVendor(Guid vendorUuid, [FromQuery] bool? openOnly, [FromQuery] int? page, [FromQuery] int? pageSize) =>
        Ok(ApiResponse<PaginatedResponse<ApDebitMemoModel>>.Ok(await _memos.ListByVendorAsync(vendorUuid, openOnly ?? false, page, pageSize, Ct)));
}

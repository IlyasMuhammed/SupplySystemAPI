using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Services.Life;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Hrm.Controllers;

/// <summary>API-CONTRACT §11.</summary>
[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[RequiresFeature(HrmFeatureCodes.Loans)]
[Route("api/hrm/loans")]
public sealed class LoansController : HrmControllerBase
{
    private readonly ILoanService _svc;
    public LoansController(ITenantContext tenant, ILoanService svc) : base(tenant) => _svc = svc;

    [Microsoft.AspNetCore.Http.EndpointSummary("List loans in scope")]
    [HttpGet, RequirePermission(P.HRM_LOAN_VIEW)]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] Guid? employeeUuid, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) => Done(await _svc.ListAsync(Caller, status, employeeUuid, page, pageSize, ct));
    [Microsoft.AspNetCore.Http.EndpointSummary("The caller's own loans")]
    [HttpGet("me"), RequirePermission(P.HRM_SELF_SERVICE)]
    public async Task<IActionResult> Mine(CancellationToken ct) => Done(await _svc.MineAsync(Caller, ct));
    [Microsoft.AspNetCore.Http.EndpointSummary("Get a loan with its installments")]
    [HttpGet("{uuid:guid}"), RequirePermission(P.HRM_LOAN_VIEW)]
    public async Task<IActionResult> Get(Guid uuid, CancellationToken ct) => Done(await _svc.GetAsync(Caller, uuid, ct));
    [Microsoft.AspNetCore.Http.EndpointSummary("Create a draft loan or advance")]
    [HttpPost, RequirePermission(P.HRM_LOAN_MANAGE)]
    public async Task<IActionResult> Create([FromBody] LoanUpsert req, CancellationToken ct) => Done(await _svc.CreateAsync(Caller, req, ct), "Loan created.");
    [Microsoft.AspNetCore.Http.EndpointSummary("Update a draft loan")]
    [HttpPut("{uuid:guid}"), RequirePermission(P.HRM_LOAN_MANAGE)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] LoanUpsert req, CancellationToken ct) => Done(await _svc.UpdateAsync(Caller, uuid, req, ct), "Loan saved.");
    [Microsoft.AspNetCore.Http.EndpointSummary("Approve a loan")]
    [HttpPost("{uuid:guid}/approve"), RequirePermission(P.HRM_LOAN_APPROVE)]
    public async Task<IActionResult> Approve(Guid uuid, CancellationToken ct) => Done(await _svc.ApproveAsync(Caller, uuid, ct), "Loan approved.");
    [Microsoft.AspNetCore.Http.EndpointSummary("Reject a loan")]
    [HttpPost("{uuid:guid}/reject"), RequirePermission(P.HRM_LOAN_APPROVE)]
    public async Task<IActionResult> Reject(Guid uuid, [FromBody] LifeNoteRequest req, CancellationToken ct) => Done(await _svc.RejectAsync(Caller, uuid, req.Note, ct), "Loan rejected.");
    [Microsoft.AspNetCore.Http.EndpointSummary("Disburse a loan and post it")]
    [HttpPost("{uuid:guid}/disburse"), RequirePermission(P.HRM_LOAN_MANAGE)]
    public async Task<IActionResult> Disburse(Guid uuid, [FromBody] LoanDisburseRequest req, CancellationToken ct) => Done(await _svc.DisburseAsync(Caller, uuid, req, ct), "Loan disbursed.");
    [Microsoft.AspNetCore.Http.EndpointSummary("Retry a failed loan posting")]
    [HttpPost("{uuid:guid}/retry-posting"), RequirePermission(P.HRM_LOAN_MANAGE)]
    public async Task<IActionResult> Retry(Guid uuid, CancellationToken ct) => Done(await _svc.RetryPostingAsync(Caller, uuid, ct));
    [Microsoft.AspNetCore.Http.EndpointSummary("Waive a loan installment")]
    [HttpPost("installments/{uuid:guid}/waive"), RequirePermission(P.HRM_LOAN_APPROVE)]
    public async Task<IActionResult> Waive(Guid uuid, [FromBody] LifeNoteRequest req, CancellationToken ct) => Done(await _svc.WaiveInstallmentAsync(Caller, uuid, req.Note, ct), "Installment waived.");
}

[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[RequiresFeature(HrmFeatureCodes.Loans)]
[Route("api/hrm/settlements")]
public sealed class SettlementsController : HrmControllerBase
{
    private readonly IFinalSettlementService _svc;
    public SettlementsController(ITenantContext tenant, IFinalSettlementService svc) : base(tenant) => _svc = svc;

    [Microsoft.AspNetCore.Http.EndpointSummary("List final settlements")]
    [HttpGet, RequirePermission(P.HRM_SETTLEMENT_VIEW)]
    public async Task<IActionResult> List([FromQuery] string? status, CancellationToken ct) => Done(await _svc.ListAsync(Caller, status, ct));
    [Microsoft.AspNetCore.Http.EndpointSummary("Get a final settlement")]
    [HttpGet("{uuid:guid}"), RequirePermission(P.HRM_SETTLEMENT_VIEW)]
    public async Task<IActionResult> Get(Guid uuid, CancellationToken ct) => Done(await _svc.GetAsync(Caller, uuid, ct));
    [Microsoft.AspNetCore.Http.EndpointSummary("Compute a draft final settlement")]
    [HttpPost("compute"), RequirePermission(P.HRM_SETTLEMENT_MANAGE)]
    public async Task<IActionResult> Compute([FromBody] SettlementComputeRequest req, CancellationToken ct) => Done(await _svc.ComputeAsync(Caller, req, ct), "Settlement computed.");
    [Microsoft.AspNetCore.Http.EndpointSummary("Edit a draft settlement's notes and lines")]
    [HttpPut("{uuid:guid}"), RequirePermission(P.HRM_SETTLEMENT_MANAGE)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] SettlementUpdate req, CancellationToken ct) => Done(await _svc.UpdateAsync(Caller, uuid, req, ct), "Settlement saved.");
    [Microsoft.AspNetCore.Http.EndpointSummary("Approve a settlement")]
    [HttpPost("{uuid:guid}/approve"), RequirePermission(P.HRM_SETTLEMENT_MANAGE)]
    public async Task<IActionResult> Approve(Guid uuid, CancellationToken ct) => Done(await _svc.ApproveAsync(Caller, uuid, ct));
    [Microsoft.AspNetCore.Http.EndpointSummary("Post an approved settlement")]
    [HttpPost("{uuid:guid}/post"), RequirePermission(P.HRM_SETTLEMENT_MANAGE)]
    public async Task<IActionResult> Post(Guid uuid, CancellationToken ct) => Done(await _svc.PostAsync(Caller, uuid, ct));
    [Microsoft.AspNetCore.Http.EndpointSummary("Pay a posted settlement")]
    [HttpPost("{uuid:guid}/pay"), RequirePermission(P.HRM_SETTLEMENT_MANAGE)]
    public async Task<IActionResult> Pay(Guid uuid, [FromBody] LifePayRequest req, CancellationToken ct) => Done(await _svc.PayAsync(Caller, uuid, req, ct));
    [Microsoft.AspNetCore.Http.EndpointSummary("Cancel a settlement")]
    [HttpPost("{uuid:guid}/cancel"), RequirePermission(P.HRM_SETTLEMENT_MANAGE)]
    public async Task<IActionResult> Cancel(Guid uuid, CancellationToken ct) => Done(await _svc.CancelAsync(Caller, uuid, ct));
    [Microsoft.AspNetCore.Http.EndpointSummary("Retry a failed settlement posting")]
    [HttpPost("{uuid:guid}/retry-posting"), RequirePermission(P.HRM_SETTLEMENT_MANAGE)]
    public async Task<IActionResult> Retry(Guid uuid, CancellationToken ct) => Done(await _svc.RetryPostingAsync(Caller, uuid, ct));
}

[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[RequiresFeature(HrmFeatureCodes.Loans)]
[Route("api/hrm/increments")]
public sealed class IncrementsController : HrmControllerBase
{
    private readonly IIncrementService _svc;
    public IncrementsController(ITenantContext tenant, IIncrementService svc) : base(tenant) => _svc = svc;

    [Microsoft.AspNetCore.Http.EndpointSummary("List increment proposals")]
    [HttpGet, RequirePermission(P.HRM_INCREMENT_MANAGE, P.HRM_INCREMENT_APPROVE)]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] Guid? cycleUuid, CancellationToken ct) => Done(await _svc.ListAsync(Caller, status, cycleUuid, ct));
    [Microsoft.AspNetCore.Http.EndpointSummary("Create an increment proposal")]
    [HttpPost, RequirePermission(P.HRM_INCREMENT_MANAGE)]
    public async Task<IActionResult> Create([FromBody] IncrementCreate req, CancellationToken ct) => Done(await _svc.CreateAsync(Caller, req, ct));
    [Microsoft.AspNetCore.Http.EndpointSummary("Create increment proposals from a review cycle's ratings")]
    [HttpPost("bulk"), RequirePermission(P.HRM_INCREMENT_MANAGE)]
    public async Task<IActionResult> Bulk([FromBody] IncrementBulkRequest req, CancellationToken ct) => Done(await _svc.BulkAsync(Caller, req, ct));
    [Microsoft.AspNetCore.Http.EndpointSummary("Approve an increment")]
    [HttpPost("{uuid:guid}/approve"), RequirePermission(P.HRM_INCREMENT_APPROVE)]
    public async Task<IActionResult> Approve(Guid uuid, CancellationToken ct) => Done(await _svc.ApproveAsync(Caller, uuid, ct));
    [Microsoft.AspNetCore.Http.EndpointSummary("Reject an increment")]
    [HttpPost("{uuid:guid}/reject"), RequirePermission(P.HRM_INCREMENT_APPROVE)]
    public async Task<IActionResult> Reject(Guid uuid, [FromBody] LifeNoteRequest req, CancellationToken ct) => Done(await _svc.RejectAsync(Caller, uuid, req.Note, ct));
    [Microsoft.AspNetCore.Http.EndpointSummary("Apply an approved increment as a salary assignment")]
    [HttpPost("{uuid:guid}/apply"), RequirePermission(P.HRM_INCREMENT_APPROVE)]
    public async Task<IActionResult> Apply(Guid uuid, CancellationToken ct) => Done(await _svc.ApplyAsync(Caller, uuid, ct));
}

[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[RequiresFeature(HrmFeatureCodes.Loans)]
[Route("api/hrm")]
public sealed class LettersController : HrmControllerBase
{
    private readonly ILetterService _letters;
    private readonly ITaxCertificateService _tax;
    public LettersController(ITenantContext tenant, ILetterService letters, ITaxCertificateService tax) : base(tenant) { _letters = letters; _tax = tax; }

    [Microsoft.AspNetCore.Http.EndpointSummary("List letter templates")]
    [HttpGet("letter-templates"), RequirePermission(P.HRM_LETTER_ISSUE)]
    public async Task<IActionResult> Templates(CancellationToken ct) => Done(await _letters.ListTemplatesAsync(Caller, ct));
    [Microsoft.AspNetCore.Http.EndpointSummary("Create a letter template")]
    [HttpPost("letter-templates"), RequirePermission(P.HRM_LETTER_ISSUE)]
    public async Task<IActionResult> CreateTemplate([FromBody] LetterTemplateUpsert req, CancellationToken ct) => Done(await _letters.CreateTemplateAsync(Caller, req, ct));
    [Microsoft.AspNetCore.Http.EndpointSummary("Update a letter template")]
    [HttpPut("letter-templates/{uuid:guid}"), RequirePermission(P.HRM_LETTER_ISSUE)]
    public async Task<IActionResult> UpdateTemplate(Guid uuid, [FromBody] LetterTemplateUpsert req, CancellationToken ct) => Done(await _letters.UpdateTemplateAsync(Caller, uuid, req, ct));
    [Microsoft.AspNetCore.Http.EndpointSummary("List issued letters")]
    [HttpGet("letters"), RequirePermission(P.HRM_LETTER_ISSUE)]
    public async Task<IActionResult> Letters([FromQuery] Guid? employeeUuid, CancellationToken ct) => Done(await _letters.ListAsync(Caller, employeeUuid, ct));
    [Microsoft.AspNetCore.Http.EndpointSummary("The caller's own letters")]
    [HttpGet("letters/me"), RequirePermission(P.HRM_SELF_SERVICE)]
    public async Task<IActionResult> MyLetters(CancellationToken ct) => Done(await _letters.MineAsync(Caller, ct));
    [Microsoft.AspNetCore.Http.EndpointSummary("Issue a letter from a template")]
    [HttpPost("letters"), RequirePermission(P.HRM_LETTER_ISSUE)]
    public async Task<IActionResult> Issue([FromBody] LetterIssueRequest req, CancellationToken ct) => Done(await _letters.IssueAsync(Caller, req, ct), "Letter issued.");
    [Microsoft.AspNetCore.Http.EndpointSummary("Download an issued letter as PDF")]
    [HttpGet("letters/{uuid:guid}/pdf"), RequirePermission(P.HRM_LETTER_ISSUE, P.HRM_SELF_SERVICE)]
    public async Task<IActionResult> LetterPdf(Guid uuid, CancellationToken ct) { var f = await _letters.GetPdfAsync(Caller, uuid, ct); return File(f.Content, "application/pdf", f.FileName); }

    [Microsoft.AspNetCore.Http.EndpointSummary("Generate tax certificates for a tax year")]
    [HttpPost("tax-certificates/generate"), RequirePermission(P.HRM_TAX_CERT_VIEW)]
    public async Task<IActionResult> Generate([FromBody] TaxCertificateGenerate req, CancellationToken ct) => Done(await _tax.GenerateAsync(Caller, req, ct));
    [Microsoft.AspNetCore.Http.EndpointSummary("List tax certificates")]
    [HttpGet("tax-certificates"), RequirePermission(P.HRM_TAX_CERT_VIEW)]
    public async Task<IActionResult> Certificates([FromQuery] int? taxYear, CancellationToken ct) => Done(await _tax.ListAsync(Caller, taxYear, ct));
    [Microsoft.AspNetCore.Http.EndpointSummary("The caller's own tax certificates")]
    [HttpGet("tax-certificates/me"), RequirePermission(P.HRM_SELF_SERVICE)]
    public async Task<IActionResult> MyCertificates(CancellationToken ct) => Done(await _tax.MineAsync(Caller, ct));
    [Microsoft.AspNetCore.Http.EndpointSummary("Download a tax certificate as PDF")]
    [HttpGet("tax-certificates/{uuid:guid}/pdf"), RequirePermission(P.HRM_TAX_CERT_VIEW, P.HRM_SELF_SERVICE)]
    public async Task<IActionResult> CertificatePdf(Guid uuid, CancellationToken ct) { var f = await _tax.GetPdfAsync(Caller, uuid, ct); return File(f.Content, "application/pdf", f.FileName); }
}

/// <summary>API-CONTRACT §12.</summary>
[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[RequiresFeature(HrmFeatureCodes.Expenses)]
[Route("api/hrm/expense-categories")]
public sealed class ExpenseCategoriesController : HrmControllerBase
{
    private readonly IExpenseCategoryService _svc;
    public ExpenseCategoriesController(ITenantContext tenant, IExpenseCategoryService svc) : base(tenant) => _svc = svc;

    [Microsoft.AspNetCore.Http.EndpointSummary("List expense categories")]
    [HttpGet, RequirePermission(P.HRM_EXPENSE_VIEW, P.HRM_SELF_SERVICE, P.HRM_ACCOUNT_MAPPING_VIEW)]
    public async Task<IActionResult> List([FromQuery] bool includeInactive, CancellationToken ct) => Done(await _svc.ListAsync(Caller, includeInactive, ct));
    [Microsoft.AspNetCore.Http.EndpointSummary("Create an expense category")]
    [HttpPost, RequirePermission(P.HRM_ACCOUNT_MAPPING_EDIT, P.HRM_SETTINGS_MANAGE)]
    public async Task<IActionResult> Create([FromBody] ExpenseCategoryUpsert req, CancellationToken ct) => Done(await _svc.CreateAsync(Caller, req, ct));
    [Microsoft.AspNetCore.Http.EndpointSummary("Update an expense category")]
    [HttpPut("{uuid:guid}"), RequirePermission(P.HRM_ACCOUNT_MAPPING_EDIT, P.HRM_SETTINGS_MANAGE)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] ExpenseCategoryUpsert req, CancellationToken ct) => Done(await _svc.UpdateAsync(Caller, uuid, req, ct));
    [Microsoft.AspNetCore.Http.EndpointSummary("Activate or deactivate an expense category")]
    [HttpPatch("{uuid:guid}/active"), RequirePermission(P.HRM_ACCOUNT_MAPPING_EDIT, P.HRM_SETTINGS_MANAGE)]
    public async Task<IActionResult> SetActive(Guid uuid, [FromBody] SetActiveRequest req, CancellationToken ct) => Done(await _svc.SetActiveAsync(Caller, uuid, req.IsActive, ct));
}

[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[RequiresFeature(HrmFeatureCodes.Expenses)]
[Route("api/hrm/expense-claims")]
public sealed class ExpenseClaimsController : HrmControllerBase
{
    private readonly IExpenseClaimService _svc;
    public ExpenseClaimsController(ITenantContext tenant, IExpenseClaimService svc) : base(tenant) => _svc = svc;

    [Microsoft.AspNetCore.Http.EndpointSummary("List expense claims in scope")]
    [HttpGet, RequirePermission(P.HRM_EXPENSE_VIEW, P.HRM_EXPENSE_APPROVE, P.HRM_EXPENSE_PAY)]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] Guid? employeeUuid, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) => Done(await _svc.ListAsync(Caller, status, employeeUuid, page, pageSize, ct));
    [Microsoft.AspNetCore.Http.EndpointSummary("The caller's own expense claims")]
    [HttpGet("me"), RequirePermission(P.HRM_SELF_SERVICE)]
    public async Task<IActionResult> Mine(CancellationToken ct) => Done(await _svc.MineAsync(Caller, ct));
    [Microsoft.AspNetCore.Http.EndpointSummary("Get an expense claim")]
    [HttpGet("{uuid:guid}"), RequirePermission(P.HRM_EXPENSE_VIEW, P.HRM_EXPENSE_APPROVE, P.HRM_EXPENSE_PAY, P.HRM_SELF_SERVICE)]
    public async Task<IActionResult> Get(Guid uuid, CancellationToken ct) => Done(await _svc.GetAsync(Caller, uuid, ct));
    [Microsoft.AspNetCore.Http.EndpointSummary("Create a draft expense claim")]
    [HttpPost, RequirePermission(P.HRM_SELF_SERVICE)]
    public async Task<IActionResult> Create([FromBody] ExpenseClaimUpsert req, CancellationToken ct) => Done(await _svc.CreateAsync(Caller, req, ct), "Claim saved.");
    [Microsoft.AspNetCore.Http.EndpointSummary("Update a draft expense claim")]
    [HttpPut("{uuid:guid}"), RequirePermission(P.HRM_SELF_SERVICE)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] ExpenseClaimUpsert req, CancellationToken ct) => Done(await _svc.UpdateAsync(Caller, uuid, req, ct), "Claim saved.");
    [Microsoft.AspNetCore.Http.EndpointSummary("Submit a claim to its approval workflow")]
    [HttpPost("{uuid:guid}/submit"), RequirePermission(P.HRM_SELF_SERVICE)]
    public async Task<IActionResult> Submit(Guid uuid, CancellationToken ct) => Done(await _svc.SubmitAsync(Caller, uuid, ct), "Claim submitted.");
    [Microsoft.AspNetCore.Http.EndpointSummary("Approve a claim and post the expense")]
    [HttpPost("{uuid:guid}/approve"), RequirePermission(P.HRM_EXPENSE_APPROVE)]
    public async Task<IActionResult> Approve(Guid uuid, CancellationToken ct) => Done(await _svc.ApproveAsync(Caller, uuid, ct));
    [Microsoft.AspNetCore.Http.EndpointSummary("Reject a claim")]
    [HttpPost("{uuid:guid}/reject"), RequirePermission(P.HRM_EXPENSE_APPROVE)]
    public async Task<IActionResult> Reject(Guid uuid, [FromBody] LifeNoteRequest req, CancellationToken ct) => Done(await _svc.RejectAsync(Caller, uuid, req.Note, ct));
    [Microsoft.AspNetCore.Http.EndpointSummary("Pay a separately settled claim and post the payment")]
    [HttpPost("{uuid:guid}/pay"), RequirePermission(P.HRM_EXPENSE_PAY)]
    public async Task<IActionResult> Pay(Guid uuid, [FromBody] LifePayRequest req, CancellationToken ct) => Done(await _svc.PayAsync(Caller, uuid, req, ct));
    [Microsoft.AspNetCore.Http.EndpointSummary("Retry a failed claim posting")]
    [HttpPost("{uuid:guid}/retry-posting"), RequirePermission(P.HRM_EXPENSE_PAY, P.HRM_EXPENSE_APPROVE)]
    public async Task<IActionResult> Retry(Guid uuid, CancellationToken ct) => Done(await _svc.RetryPostingAsync(Caller, uuid, ct));
}

[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[RequiresFeature(HrmFeatureCodes.Expenses)]
[Route("api/hrm/remittances")]
public sealed class RemittancesController : HrmControllerBase
{
    private readonly IStatutoryRemittanceService _svc;
    public RemittancesController(ITenantContext tenant, IStatutoryRemittanceService svc) : base(tenant) => _svc = svc;

    [Microsoft.AspNetCore.Http.EndpointSummary("Statutory amounts calculated against paid, per period")]
    [HttpGet("tracker"), RequirePermission(P.HRM_REMITTANCE_VIEW)]
    public async Task<IActionResult> Tracker([FromQuery] int? year, [FromQuery] string? type, CancellationToken ct) => Done(await _svc.TrackerAsync(Caller, year, type, ct));
    [Microsoft.AspNetCore.Http.EndpointSummary("List statutory remittances")]
    [HttpGet, RequirePermission(P.HRM_REMITTANCE_VIEW)]
    public async Task<IActionResult> List([FromQuery] string? type, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) => Done(await _svc.ListAsync(Caller, type, from, to, ct));
    [Microsoft.AspNetCore.Http.EndpointSummary("Record a statutory remittance and post it")]
    [HttpPost, RequirePermission(P.HRM_REMITTANCE_MANAGE)]
    public async Task<IActionResult> Create([FromBody] RemittanceCreate req, CancellationToken ct) => Done(await _svc.CreateAsync(Caller, req, ct), "Remittance recorded.");
    [Microsoft.AspNetCore.Http.EndpointSummary("Retry a failed remittance posting")]
    [HttpPost("{uuid:guid}/retry-posting"), RequirePermission(P.HRM_REMITTANCE_MANAGE)]
    public async Task<IActionResult> Retry(Guid uuid, CancellationToken ct) => Done(await _svc.RetryPostingAsync(Caller, uuid, ct));
}

using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Services.Pay;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Pagination;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Hrm.Controllers;

// API-CONTRACT §7 (salary) and §9 (payroll). Order of checks: tenant → module and sub-feature → permission → HR scope in the service.

[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[RequiresFeature(HrmFeatureCodes.Payroll)]
[Route("api/hrm/salary-components")]
public sealed class SalaryComponentsController : HrmControllerBase
{
    private readonly ISalaryComponentService _svc;
    public SalaryComponentsController(ITenantContext tenant, ISalaryComponentService svc) : base(tenant) => _svc = svc;

    [Microsoft.AspNetCore.Http.EndpointSummary("List salary components")]
    [HttpGet, RequirePermission(P.HRM_PAYROLL_CONFIG, P.HRM_PAYROLL_VIEW, P.HRM_SALARY_EDIT)]
    public async Task<IActionResult> List(CancellationToken ct) => Done(await _svc.ListAsync(Caller, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Create a salary component")]
    [HttpPost, RequirePermission(P.HRM_PAYROLL_CONFIG)]
    public async Task<IActionResult> Create([FromBody] SalaryComponentUpsert req, CancellationToken ct) => Done(await _svc.CreateAsync(Caller, req, ct), "Component created.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Update a salary component")]
    [HttpPut("{uuid:guid}"), RequirePermission(P.HRM_PAYROLL_CONFIG)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] SalaryComponentUpsert req, CancellationToken ct) => Done(await _svc.UpdateAsync(Caller, uuid, req, ct), "Component saved.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Activate or deactivate a salary component")]
    [HttpPatch("{uuid:guid}/active"), RequirePermission(P.HRM_PAYROLL_CONFIG)]
    public async Task<IActionResult> SetActive(Guid uuid, [FromBody] PayActiveRequest req, CancellationToken ct) => Done(await _svc.SetActiveAsync(Caller, uuid, req.IsActive, ct));
}

[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[RequiresFeature(HrmFeatureCodes.Payroll)]
[Route("api/hrm/salary-structures")]
public sealed class SalaryStructuresController : HrmControllerBase
{
    private readonly ISalaryStructureService _svc;
    public SalaryStructuresController(ITenantContext tenant, ISalaryStructureService svc) : base(tenant) => _svc = svc;

    [Microsoft.AspNetCore.Http.EndpointSummary("List salary structures")]
    [HttpGet, RequirePermission(P.HRM_PAYROLL_CONFIG, P.HRM_SALARY_EDIT)]
    public async Task<IActionResult> List(CancellationToken ct) => Done(await _svc.ListAsync(Caller, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Get a salary structure")]
    [HttpGet("{uuid:guid}"), RequirePermission(P.HRM_PAYROLL_CONFIG, P.HRM_SALARY_EDIT)]
    public async Task<IActionResult> Get(Guid uuid, CancellationToken ct) => Done(await _svc.GetAsync(Caller, uuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Create a salary structure")]
    [HttpPost, RequirePermission(P.HRM_PAYROLL_CONFIG)]
    public async Task<IActionResult> Create([FromBody] SalaryStructureUpsert req, CancellationToken ct) => Done(await _svc.CreateAsync(Caller, req, ct), "Structure created.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Update a salary structure")]
    [HttpPut("{uuid:guid}"), RequirePermission(P.HRM_PAYROLL_CONFIG)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] SalaryStructureUpsert req, CancellationToken ct) => Done(await _svc.UpdateAsync(Caller, uuid, req, ct), "Structure saved.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Activate or deactivate a salary structure")]
    [HttpPatch("{uuid:guid}/active"), RequirePermission(P.HRM_PAYROLL_CONFIG)]
    public async Task<IActionResult> SetActive(Guid uuid, [FromBody] PayActiveRequest req, CancellationToken ct) => Done(await _svc.SetActiveAsync(Caller, uuid, req.IsActive, ct));
}

[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[RequiresFeature(HrmFeatureCodes.Payroll)]
[Route("api/hrm/salary-formula")]
public sealed class SalaryFormulaController : HrmControllerBase
{
    private readonly ISalaryStructureService _svc;
    public SalaryFormulaController(ITenantContext tenant, ISalaryStructureService svc) : base(tenant) => _svc = svc;

    [Microsoft.AspNetCore.Http.EndpointSummary("Test a salary formula with sample variables")]
    [HttpPost("test"), RequirePermission(P.HRM_PAYROLL_CONFIG)]
    public async Task<IActionResult> Test([FromBody] FormulaTestRequest req, CancellationToken ct) => Done(await _svc.TestFormulaAsync(Caller, req, ct));
}

[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[RequiresFeature(HrmFeatureCodes.Payroll)]
public sealed class SalaryAssignmentsController : HrmControllerBase
{
    private readonly ISalaryAssignmentService _svc;
    public SalaryAssignmentsController(ITenantContext tenant, ISalaryAssignmentService svc) : base(tenant) => _svc = svc;

    [Microsoft.AspNetCore.Http.EndpointSummary("An employee's salary assignments")]
    [HttpGet("api/hrm/employees/{uuid:guid}/salary"), RequirePermission(P.HRM_SALARY_VIEW, P.HRM_SELF_SERVICE)]
    public async Task<IActionResult> List(Guid uuid, CancellationToken ct) => Done(await _svc.ListAsync(Caller, uuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("An employee's salary in force")]
    [HttpGet("api/hrm/employees/{uuid:guid}/salary/current"), RequirePermission(P.HRM_SALARY_VIEW, P.HRM_SELF_SERVICE)]
    public async Task<IActionResult> Current(Guid uuid, CancellationToken ct) => Done(await _svc.GetCurrentAsync(Caller, uuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Create a draft salary assignment")]
    [HttpPost("api/hrm/employees/{uuid:guid}/salary"), RequirePermission(P.HRM_SALARY_EDIT)]
    public async Task<IActionResult> Create(Guid uuid, [FromBody] SalaryAssignmentCreate req, CancellationToken ct) => Done(await _svc.CreateAsync(Caller, uuid, req, ct), "Salary draft saved.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Submit a salary assignment to its approval workflow")]
    [HttpPost("api/hrm/salary-assignments/{uuid:guid}/submit"), RequirePermission(P.HRM_SALARY_EDIT)]
    public async Task<IActionResult> Submit(Guid uuid, CancellationToken ct) => Done(await _svc.SubmitAsync(Caller, uuid, ct), "Submitted for approval.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Approve a salary assignment (supersedes the previous one)")]
    [HttpPost("api/hrm/salary-assignments/{uuid:guid}/approve"), RequirePermission(P.HRM_SALARY_APPROVE)]
    public async Task<IActionResult> Approve(Guid uuid, CancellationToken ct) => Done(await _svc.ApproveAsync(Caller, uuid, ct), "Salary approved.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Reject a salary assignment")]
    [HttpPost("api/hrm/salary-assignments/{uuid:guid}/reject"), RequirePermission(P.HRM_SALARY_APPROVE)]
    public async Task<IActionResult> Reject(Guid uuid, [FromBody] SalaryDecisionRequest req, CancellationToken ct) => Done(await _svc.RejectAsync(Caller, uuid, req.Note, ct), "Salary rejected.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Create draft salary revisions for several employees")]
    [HttpPost("api/hrm/salary-assignments/bulk-revision"), RequirePermission(P.HRM_SALARY_EDIT)]
    public async Task<IActionResult> Bulk([FromBody] BulkRevisionRequest req, CancellationToken ct) => Done(await _svc.BulkRevisionAsync(Caller, req, ct), "Drafts created.");
}

[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[RequiresFeature(HrmFeatureCodes.Payroll)]
public sealed class StatutoryController : HrmControllerBase
{
    private readonly IStatutoryService _svc;
    public StatutoryController(ITenantContext tenant, IStatutoryService svc) : base(tenant) => _svc = svc;

    [Microsoft.AspNetCore.Http.EndpointSummary("List tax tables")]
    [HttpGet("api/hrm/tax-tables"), RequirePermission(P.HRM_PAYROLL_CONFIG, P.HRM_STATUTORY_VERIFY)]
    public async Task<IActionResult> Tables(CancellationToken ct) => Done(await _svc.ListTaxTablesAsync(Caller, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Get a tax table")]
    [HttpGet("api/hrm/tax-tables/{uuid:guid}"), RequirePermission(P.HRM_PAYROLL_CONFIG, P.HRM_STATUTORY_VERIFY)]
    public async Task<IActionResult> Table(Guid uuid, CancellationToken ct) => Done(await _svc.GetTaxTableAsync(Caller, uuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Create a tax table")]
    [HttpPost("api/hrm/tax-tables"), RequirePermission(P.HRM_PAYROLL_CONFIG)]
    public async Task<IActionResult> CreateTable([FromBody] TaxTableUpsert req, CancellationToken ct) => Done(await _svc.CreateTaxTableAsync(Caller, req, ct), "Tax table created, unverified.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Update a tax table (clears verification)")]
    [HttpPut("api/hrm/tax-tables/{uuid:guid}"), RequirePermission(P.HRM_PAYROLL_CONFIG)]
    public async Task<IActionResult> UpdateTable(Guid uuid, [FromBody] TaxTableUpsert req, CancellationToken ct) => Done(await _svc.UpdateTaxTableAsync(Caller, uuid, req, ct), "Tax table saved; verification cleared.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Mark a tax table as reviewed")]
    [HttpPost("api/hrm/tax-tables/{uuid:guid}/verify"), RequirePermission(P.HRM_STATUTORY_VERIFY)]
    public async Task<IActionResult> VerifyTable(Guid uuid, [FromBody] VerifyRequest req, CancellationToken ct) => Done(await _svc.VerifyTaxTableAsync(Caller, uuid, req.Note, ct), "Marked as reviewed.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Withdraw a tax table's review")]
    [HttpPost("api/hrm/tax-tables/{uuid:guid}/unverify"), RequirePermission(P.HRM_STATUTORY_VERIFY)]
    public async Task<IActionResult> UnverifyTable(Guid uuid, CancellationToken ct) => Done(await _svc.UnverifyTaxTableAsync(Caller, uuid, ct), "Marked as not reviewed.");

    [Microsoft.AspNetCore.Http.EndpointSummary("List statutory rates")]
    [HttpGet("api/hrm/statutory-rates"), RequirePermission(P.HRM_PAYROLL_CONFIG, P.HRM_STATUTORY_VERIFY)]
    public async Task<IActionResult> Rates(CancellationToken ct) => Done(await _svc.ListRatesAsync(Caller, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Get a statutory rate")]
    [HttpGet("api/hrm/statutory-rates/{uuid:guid}"), RequirePermission(P.HRM_PAYROLL_CONFIG, P.HRM_STATUTORY_VERIFY)]
    public async Task<IActionResult> Rate(Guid uuid, CancellationToken ct) => Done(await _svc.GetRateAsync(Caller, uuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Create a statutory rate")]
    [HttpPost("api/hrm/statutory-rates"), RequirePermission(P.HRM_PAYROLL_CONFIG)]
    public async Task<IActionResult> CreateRate([FromBody] StatutoryRateUpsert req, CancellationToken ct) => Done(await _svc.CreateRateAsync(Caller, req, ct), "Rate created, unverified.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Update a statutory rate (clears verification)")]
    [HttpPut("api/hrm/statutory-rates/{uuid:guid}"), RequirePermission(P.HRM_PAYROLL_CONFIG)]
    public async Task<IActionResult> UpdateRate(Guid uuid, [FromBody] StatutoryRateUpsert req, CancellationToken ct) => Done(await _svc.UpdateRateAsync(Caller, uuid, req, ct), "Rate saved; verification cleared.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Mark a statutory rate as reviewed")]
    [HttpPost("api/hrm/statutory-rates/{uuid:guid}/verify"), RequirePermission(P.HRM_STATUTORY_VERIFY)]
    public async Task<IActionResult> VerifyRate(Guid uuid, [FromBody] VerifyRequest req, CancellationToken ct) => Done(await _svc.VerifyRateAsync(Caller, uuid, req.Note, ct), "Marked as reviewed.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Withdraw a statutory rate's review")]
    [HttpPost("api/hrm/statutory-rates/{uuid:guid}/unverify"), RequirePermission(P.HRM_STATUTORY_VERIFY)]
    public async Task<IActionResult> UnverifyRate(Guid uuid, CancellationToken ct) => Done(await _svc.UnverifyRateAsync(Caller, uuid, ct), "Marked as not reviewed.");
}

/// <summary>§9.</summary>
[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[RequiresFeature(HrmFeatureCodes.Payroll)]
[Route("api/hrm/payroll-runs")]
public sealed class PayrollRunsController : HrmControllerBase
{
    private readonly IPayrollRunService _svc;
    private readonly IPayslipService _slips;

    public PayrollRunsController(ITenantContext tenant, IPayrollRunService svc, IPayslipService slips) : base(tenant)
    {
        _svc = svc;
        _slips = slips;
    }

    private FileContentResult Download(PayFile f) => File(f.Content, f.ContentType, f.FileName);

    [Microsoft.AspNetCore.Http.EndpointSummary("List payroll runs")]
    [HttpGet, RequirePermission(P.HRM_PAYROLL_VIEW)]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        Done(await _svc.ListAsync(Caller, status, from, to, page, pageSize, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Create a draft payroll run")]
    [HttpPost, RequirePermission(P.HRM_PAYROLL_RUN)]
    public async Task<IActionResult> Create([FromBody] PayrollRunCreate req, CancellationToken ct) => Done(await _svc.CreateAsync(Caller, req, ct), "Payroll run created.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Get a payroll run")]
    [HttpGet("{uuid:guid}"), RequirePermission(P.HRM_PAYROLL_VIEW)]
    public async Task<IActionResult> Get(Guid uuid, CancellationToken ct) => Done(await _svc.GetAsync(Caller, uuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("A payroll run's employee lines")]
    [HttpGet("{uuid:guid}/lines"), RequirePermission(P.HRM_PAYROLL_VIEW)]
    public async Task<IActionResult> Lines(Guid uuid, [FromQuery] string? search, [FromQuery] bool blockedOnly, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        Done(await _svc.ListLinesAsync(Caller, uuid, search, blockedOnly, page, pageSize, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Calculate a run (202 and a background job above 200 employees)")]
    [HttpPost("{uuid:guid}/calculate"), RequirePermission(P.HRM_PAYROLL_RUN)]
    public async Task<IActionResult> Calculate(Guid uuid, CancellationToken ct)
    {
        var result = await _svc.CalculateAsync(Caller, uuid, ct);
        return result.CalculationQueued ? Accepted(ApiResponse<PayrollRunModel>.Ok(result, "The calculation is running in the background.")) : Done(result, "Payroll calculated.");
    }

    [Microsoft.AspNetCore.Http.EndpointSummary("Progress of a background calculation")]
    [HttpGet("{uuid:guid}/progress"), RequirePermission(P.HRM_PAYROLL_VIEW, P.HRM_PAYROLL_RUN)]
    public async Task<IActionResult> Progress(Guid uuid, CancellationToken ct) => Done(await _svc.GetProgressAsync(Caller, uuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Move a calculated run to review")]
    [HttpPost("{uuid:guid}/review"), RequirePermission(P.HRM_PAYROLL_APPROVE)]
    public async Task<IActionResult> Review(Guid uuid, CancellationToken ct) => Done(await _svc.ReviewAsync(Caller, uuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Approve a run under review")]
    [HttpPost("{uuid:guid}/approve"), RequirePermission(P.HRM_PAYROLL_APPROVE)]
    public async Task<IActionResult> Approve(Guid uuid, [FromBody] PayrollApproveRequest? req, CancellationToken ct) => Done(await _svc.ApproveAsync(Caller, uuid, req?.ExcludeBlocked ?? false, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Reopen a run for recalculation")]
    [HttpPost("{uuid:guid}/reopen"), RequirePermission(P.HRM_PAYROLL_APPROVE)]
    public async Task<IActionResult> Reopen(Guid uuid, CancellationToken ct) => Done(await _svc.ReopenAsync(Caller, uuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Check whether a run can post to its targets")]
    [HttpGet("{uuid:guid}/posting-readiness"), RequirePermission(P.HRM_PAYROLL_VIEW)]
    public async Task<IActionResult> Readiness(Guid uuid, CancellationToken ct) => Done(await _svc.GetReadinessAsync(Caller, uuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Finalize a run and post it")]
    [HttpPost("{uuid:guid}/finalize"), RequirePermission(P.HRM_PAYROLL_FINALIZE)]
    public async Task<IActionResult> Finalize(Guid uuid, CancellationToken ct) => Done(await _svc.FinalizeAsync(Caller, uuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Retry a failed payroll posting")]
    [HttpPost("{uuid:guid}/retry-posting"), RequirePermission(P.HRM_PAYROLL_FINALIZE)]
    public async Task<IActionResult> Retry(Guid uuid, CancellationToken ct) => Done(await _svc.RetryPostingAsync(Caller, uuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Download the bank payment CSV (logged per employee)")]
    [HttpGet("{uuid:guid}/bank-file"), RequirePermission(P.HRM_PAYROLL_PAY)]
    public async Task<IActionResult> BankFile(Guid uuid, CancellationToken ct) => Download(await _svc.GetBankFileAsync(Caller, uuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Mark a run paid and post the payment")]
    [HttpPost("{uuid:guid}/pay"), RequirePermission(P.HRM_PAYROLL_PAY)]
    public async Task<IActionResult> Pay(Guid uuid, [FromBody] PayrollPayRequest req, CancellationToken ct) => Done(await _svc.PayAsync(Caller, uuid, req, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Reverse a finalized or posted run")]
    [HttpPost("{uuid:guid}/reverse"), RequirePermission(P.HRM_PAYROLL_REVERSE)]
    public async Task<IActionResult> Reverse(Guid uuid, [FromBody] PayrollReverseRequest req, CancellationToken ct) => Done(await _svc.ReverseAsync(Caller, uuid, req.Reason, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Cancel a run before finalizing")]
    [HttpPost("{uuid:guid}/cancel"), RequirePermission(P.HRM_PAYROLL_RUN)]
    public async Task<IActionResult> Cancel(Guid uuid, CancellationToken ct) => Done(await _svc.CancelAsync(Caller, uuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Generate all payslips of a run")]
    [HttpPost("{uuid:guid}/payslips"), RequirePermission(P.HRM_PAYROLL_RUN)]
    public async Task<IActionResult> Payslips(Guid uuid, CancellationToken ct) => Done(await _slips.GenerateAllAsync(Caller, uuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Download one line's payslip as PDF")]
    [HttpGet("{uuid:guid}/lines/{line:guid}/payslip"), RequirePermission(P.HRM_PAYROLL_VIEW)]
    public async Task<IActionResult> Payslip(Guid uuid, Guid line, CancellationToken ct) => Download(await _slips.GetForLineAsync(Caller, uuid, line, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("The run's summarized journal lines")]
    [HttpGet("{uuid:guid}/journal"), RequirePermission(P.HRM_PAYROLL_VIEW)]
    public async Task<IActionResult> Journal(Guid uuid, CancellationToken ct) => Done(await _svc.GetJournalAsync(Caller, uuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Export the run's journal as CSV")]
    [HttpGet("{uuid:guid}/journal-export/csv"), RequirePermission(P.HRM_PAYROLL_VIEW)]
    public async Task<IActionResult> JournalCsv(Guid uuid, CancellationToken ct) => Download(await _svc.ExportJournalAsync(Caller, uuid, false, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Export the run's journal as Excel")]
    [HttpGet("{uuid:guid}/journal-export/xlsx"), RequirePermission(P.HRM_PAYROLL_VIEW)]
    public async Task<IActionResult> JournalXlsx(Guid uuid, CancellationToken ct) => Download(await _svc.ExportJournalAsync(Caller, uuid, true, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Run totals against what each posting target received")]
    [HttpGet("{uuid:guid}/reconciliation"), RequirePermission(P.HRM_PAYROLL_VIEW, P.HRM_REPORTS_VIEW)]
    public async Task<IActionResult> Reconciliation(Guid uuid, CancellationToken ct) => Done(await _svc.GetReconciliationAsync(Caller, uuid, ct));
}

[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[RequiresFeature(HrmFeatureCodes.Payroll)]
public sealed class PayrollMiscController : HrmControllerBase
{
    private readonly IPayrollAdjustmentService _adjustments;
    private readonly IPayslipService _slips;
    private readonly IPayrollRunService _runs;

    public PayrollMiscController(ITenantContext tenant, IPayrollAdjustmentService adjustments, IPayslipService slips, IPayrollRunService runs) : base(tenant)
    {
        _adjustments = adjustments;
        _slips = slips;
        _runs = runs;
    }

    [Microsoft.AspNetCore.Http.EndpointSummary("List payroll adjustments")]
    [HttpGet("api/hrm/payroll-adjustments"), RequirePermission(P.HRM_PAYROLL_RUN, P.HRM_PAYROLL_VIEW)]
    public async Task<IActionResult> Adjustments([FromQuery] Guid? employeeUuid, [FromQuery] bool? pending, CancellationToken ct) => Done(await _adjustments.ListAsync(Caller, employeeUuid, pending, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Create a one-off payroll adjustment")]
    [HttpPost("api/hrm/payroll-adjustments"), RequirePermission(P.HRM_PAYROLL_RUN)]
    public async Task<IActionResult> CreateAdjustment([FromBody] PayrollAdjustmentCreate req, CancellationToken ct) => Done(await _adjustments.CreateAsync(Caller, req, ct), "Adjustment added.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Delete an adjustment not yet applied")]
    [HttpDelete("api/hrm/payroll-adjustments/{uuid:guid}"), RequirePermission(P.HRM_PAYROLL_RUN)]
    public async Task<IActionResult> DeleteAdjustment(Guid uuid, CancellationToken ct)
    {
        await _adjustments.DeleteAsync(Caller, uuid, ct);
        return Done(true, "Adjustment removed.");
    }

    [Microsoft.AspNetCore.Http.EndpointSummary("The caller's own payslips")]
    [HttpGet("api/hrm/payslips/me"), RequirePermission(P.HRM_SELF_SERVICE)]
    public async Task<IActionResult> Mine([FromQuery] int? year, CancellationToken ct) => Done(await _slips.ListOwnAsync(Caller, year, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Download a payslip as PDF")]
    [HttpGet("api/hrm/payslips/{uuid:guid}/pdf"), RequirePermission(P.HRM_SELF_SERVICE, P.HRM_PAYROLL_VIEW)]
    public async Task<IActionResult> Pdf(Guid uuid, CancellationToken ct)
    {
        var f = await _slips.GetPdfAsync(Caller, uuid, ct);
        return File(f.Content, f.ContentType, f.FileName);
    }

    [Microsoft.AspNetCore.Http.EndpointSummary("An employee's payroll history")]
    [HttpGet("api/hrm/employees/{uuid:guid}/payroll-history"), RequirePermission(P.HRM_PAYROLL_VIEW)]
    public async Task<IActionResult> History(Guid uuid, CancellationToken ct) => Done(await _runs.GetEmployeeHistoryAsync(Caller, uuid, ct));
}

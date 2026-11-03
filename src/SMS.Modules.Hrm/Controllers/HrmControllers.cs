using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Security;
using SMS.Modules.Hrm.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Pagination;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Hrm.Controllers;

/// <summary>The controller seeds for itself, if at all: see <see cref="HrmEnsureSeededFilter"/>.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class SkipHrmSeedAttribute : Attribute { }

/// <summary>Seeds the organization on its first HRM request (per-request cached), so every controller of every agent sees the defaults.</summary>
public sealed class HrmEnsureSeededFilter : IAsyncActionFilter
{
    private readonly IHrmSeeder _seeder;
    private readonly ITenantContext _tenant;
    private readonly HrmUserNames? _names;

    public HrmEnsureSeededFilter(IHrmSeeder seeder, ITenantContext tenant, HrmUserNames? names = null)
    {
        _seeder = seeder;
        _tenant = tenant;
        _names = names;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        // A49 FD-9: the setup routes run for an organization that may hold no licence; their service seeds once it has checked it.
        if (!context.ActionDescriptor.EndpointMetadata.OfType<SkipHrmSeedAttribute>().Any())
            await _seeder.EnsureAsync(_tenant.OrganizationId, http.RequestAborted);
        // Once per request, so HrmControllerBase.Caller (a sync property) carries the name into every "...ByName" column.
        if (_names is not null && http.User.Identity?.IsAuthenticated == true)
            http.Items[HrmCaller.ResolvedNameItem] = await _names.NameAsync(_tenant.OrganizationId, http.User.GetUserId(), HrmCaller.ClaimName(http.User), http.RequestAborted);
        await next();
    }
}

/// <summary>Every HRM controller derives from this: tenant caller + seed filter. The module gate stays on each derived controller.</summary>
[TypeFilter(typeof(HrmEnsureSeededFilter))]
public abstract class HrmControllerBase : ControllerBase
{
    private readonly ITenantContext _tenant;
    protected HrmControllerBase(ITenantContext tenant) => _tenant = tenant;
    protected HrmCaller Caller => HrmCaller.From(User, _tenant, HttpContext?.Items[HrmCaller.ResolvedNameItem] as string);
    protected IActionResult Done<T>(T result, string message = "Request successful") => Ok(ApiResponse<T>.Ok(result, message));
}

/// <summary>A49 D-25 — the five HRM upload kinds, read through the row that holds them. Uploads stay on api/attachments/upload. The codes are every kind's View rule; the service decides per file (404).</summary>
[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[Route("api/hrm/attachments")]
public sealed class HrmAttachmentsController : HrmControllerBase
{
    private readonly IHrmAttachmentAccess _svc;
    public HrmAttachmentsController(ITenantContext tenant, IHrmAttachmentAccess svc) : base(tenant) => _svc = svc;

    [Microsoft.AspNetCore.Http.EndpointSummary("Download an HRM attachment")]
    [HttpGet("{uuid:guid}/content"), RequirePermission(P.HRM_SELF_SERVICE, P.HRM_EMPLOYEE_EDIT, P.HRM_EMPLOYEE_SENSITIVE_VIEW, P.HRM_LEAVE_VIEW, P.HRM_LEAVE_APPROVE, P.HRM_LEAVE_MANAGE, P.HRM_EXPENSE_VIEW, P.HRM_EXPENSE_APPROVE, P.HRM_EXPENSE_PAY, P.HRM_TRAINING_VIEW, P.HRM_TRAINING_MANAGE, P.HRM_RECRUITMENT_VIEW, P.HRM_RECRUITMENT_MANAGE)]
    public async Task<IActionResult> Content(Guid uuid, CancellationToken ct)
    {
        var file = await _svc.ReadAsync(Caller, uuid, ct);
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(file.Content, file.ContentType, file.FileName);
    }

    [Microsoft.AspNetCore.Http.EndpointSummary("List the attachments of an HRM row")]
    [HttpGet, RequirePermission(P.HRM_SELF_SERVICE, P.HRM_EMPLOYEE_EDIT, P.HRM_EMPLOYEE_SENSITIVE_VIEW, P.HRM_LEAVE_VIEW, P.HRM_LEAVE_APPROVE, P.HRM_LEAVE_MANAGE, P.HRM_EXPENSE_VIEW, P.HRM_EXPENSE_APPROVE, P.HRM_EXPENSE_PAY, P.HRM_TRAINING_VIEW, P.HRM_TRAINING_MANAGE, P.HRM_RECRUITMENT_VIEW, P.HRM_RECRUITMENT_MANAGE)]
    public async Task<IActionResult> List([FromQuery] string interfaceCode, [FromQuery] Guid rowUuid, CancellationToken ct) => Done(await _svc.ListAsync(Caller, interfaceCode, rowUuid, ct));

    /// <summary>A file chosen on a form and dropped before the save: the uploader, while no row holds it.</summary>
    [Microsoft.AspNetCore.Http.EndpointSummary("Delete an unused upload (uploader only)")]
    [HttpDelete("{uuid:guid}"), RequirePermission(P.HRM_SELF_SERVICE, P.HRM_EMPLOYEE_EDIT, P.HRM_LEAVE_MANAGE, P.HRM_TRAINING_MANAGE, P.HRM_RECRUITMENT_MANAGE)]
    public async Task<IActionResult> Remove(Guid uuid, CancellationToken ct)
    {
        await _svc.RemoveUnusedAsync(Caller, uuid, ct);
        return Done(true, "File removed.");
    }
}

/// <summary>API-CONTRACT §2.2.</summary>
[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[Route("api/hrm/branches")]
public sealed class BranchesController : HrmControllerBase
{
    private readonly IBranchService _svc;
    public BranchesController(ITenantContext tenant, IBranchService svc) : base(tenant) => _svc = svc;

    [Microsoft.AspNetCore.Http.EndpointSummary("List branches")]
    [HttpGet, RequirePermission(P.HRM_ORG_VIEW, P.HRM_ORG_MANAGE, P.HRM_EMPLOYEE_VIEW, P.HRM_EMPLOYEE_EDIT)]
    public async Task<IActionResult> List([FromQuery] bool includeInactive, CancellationToken ct) => Done(await _svc.ListAsync(Caller, includeInactive, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Get a branch")]
    [HttpGet("{uuid:guid}"), RequirePermission(P.HRM_ORG_VIEW, P.HRM_ORG_MANAGE)]
    public async Task<IActionResult> Get(Guid uuid, CancellationToken ct) => Done(await _svc.GetAsync(Caller, uuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Create a branch")]
    [HttpPost, RequirePermission(P.HRM_ORG_MANAGE)]
    public async Task<IActionResult> Create([FromBody] BranchUpsert req, CancellationToken ct) => Done(await _svc.CreateAsync(Caller, req, ct), "Branch created.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Update a branch")]
    [HttpPut("{uuid:guid}"), RequirePermission(P.HRM_ORG_MANAGE)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] BranchUpsert req, CancellationToken ct) => Done(await _svc.UpdateAsync(Caller, uuid, req, ct), "Branch saved.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Activate or deactivate a branch")]
    [HttpPatch("{uuid:guid}/active"), RequirePermission(P.HRM_ORG_MANAGE)]
    public async Task<IActionResult> SetActive(Guid uuid, [FromBody] SetActiveRequest req, CancellationToken ct) => Done(await _svc.SetActiveAsync(Caller, uuid, req.IsActive, ct));
}

/// <summary>API-CONTRACT §2.3 (FD-9).</summary>
[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[Route("api/hrm/cost-centers")]
public sealed class CostCentersController : HrmControllerBase
{
    private readonly ICostCenterService _svc;
    public CostCentersController(ITenantContext tenant, ICostCenterService svc) : base(tenant) => _svc = svc;

    [Microsoft.AspNetCore.Http.EndpointSummary("List cost centers")]
    [HttpGet, RequirePermission(P.HRM_ORG_VIEW, P.HRM_ORG_MANAGE, P.HRM_SETTINGS_MANAGE, P.HRM_ACCOUNT_MAPPING_VIEW, P.HRM_EMPLOYEE_EDIT)]
    public async Task<IActionResult> List([FromQuery] bool includeInactive, CancellationToken ct) => Done(await _svc.ListAsync(Caller, includeInactive, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Create a cost center")]
    [HttpPost, RequirePermission(P.HRM_ORG_MANAGE)]
    public async Task<IActionResult> Create([FromBody] CostCenterUpsert req, CancellationToken ct) => Done(await _svc.CreateAsync(Caller, req, ct), "Cost center created.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Update a cost center")]
    [HttpPut("{uuid:guid}"), RequirePermission(P.HRM_ORG_MANAGE)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] CostCenterUpsert req, CancellationToken ct) => Done(await _svc.UpdateAsync(Caller, uuid, req, ct), "Cost center saved.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Activate or deactivate a cost center")]
    [HttpPatch("{uuid:guid}/active"), RequirePermission(P.HRM_ORG_MANAGE)]
    public async Task<IActionResult> SetActive(Guid uuid, [FromBody] SetActiveRequest req, CancellationToken ct) => Done(await _svc.SetActiveAsync(Caller, uuid, req.IsActive, ct));
}

/// <summary>API-CONTRACT §2.4.</summary>
[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[Route("api/hrm/departments")]
public sealed class DepartmentsController : HrmControllerBase
{
    private readonly IDepartmentService _svc;
    public DepartmentsController(ITenantContext tenant, IDepartmentService svc) : base(tenant) => _svc = svc;

    [Microsoft.AspNetCore.Http.EndpointSummary("List departments")]
    [HttpGet, RequirePermission(P.HRM_ORG_VIEW, P.HRM_ORG_MANAGE, P.HRM_EMPLOYEE_VIEW, P.HRM_EMPLOYEE_EDIT)]
    public async Task<IActionResult> List([FromQuery] bool includeInactive, CancellationToken ct) => Done(await _svc.ListAsync(Caller, includeInactive, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Department hierarchy as a tree")]
    [HttpGet("tree"), RequirePermission(P.HRM_ORG_VIEW, P.HRM_ORG_MANAGE, P.HRM_EMPLOYEE_VIEW)]
    public async Task<IActionResult> Tree([FromQuery] bool includeInactive, CancellationToken ct) => Done(await _svc.GetTreeAsync(Caller, includeInactive, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Create a department")]
    [HttpPost, RequirePermission(P.HRM_ORG_MANAGE)]
    public async Task<IActionResult> Create([FromBody] DepartmentUpsert req, CancellationToken ct) => Done(await _svc.CreateAsync(Caller, req, ct), "Department created.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Update a department")]
    [HttpPut("{uuid:guid}"), RequirePermission(P.HRM_ORG_MANAGE)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] DepartmentUpsert req, CancellationToken ct) => Done(await _svc.UpdateAsync(Caller, uuid, req, ct), "Department saved.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Activate or deactivate a department")]
    [HttpPatch("{uuid:guid}/active"), RequirePermission(P.HRM_ORG_MANAGE)]
    public async Task<IActionResult> SetActive(Guid uuid, [FromBody] SetActiveRequest req, CancellationToken ct) => Done(await _svc.SetActiveAsync(Caller, uuid, req.IsActive, ct));
}

/// <summary>API-CONTRACT §2.5.</summary>
[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[Route("api/hrm/designations")]
public sealed class DesignationsController : HrmControllerBase
{
    private readonly IDesignationService _svc;
    public DesignationsController(ITenantContext tenant, IDesignationService svc) : base(tenant) => _svc = svc;

    [Microsoft.AspNetCore.Http.EndpointSummary("List designations")]
    [HttpGet, RequirePermission(P.HRM_ORG_VIEW, P.HRM_ORG_MANAGE, P.HRM_EMPLOYEE_VIEW, P.HRM_EMPLOYEE_EDIT)]
    public async Task<IActionResult> List([FromQuery] bool includeInactive, CancellationToken ct) => Done(await _svc.ListAsync(Caller, includeInactive, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Create a designation")]
    [HttpPost, RequirePermission(P.HRM_ORG_MANAGE)]
    public async Task<IActionResult> Create([FromBody] DesignationUpsert req, CancellationToken ct) => Done(await _svc.CreateAsync(Caller, req, ct), "Designation created.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Update a designation")]
    [HttpPut("{uuid:guid}"), RequirePermission(P.HRM_ORG_MANAGE)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] DesignationUpsert req, CancellationToken ct) => Done(await _svc.UpdateAsync(Caller, uuid, req, ct), "Designation saved.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Activate or deactivate a designation")]
    [HttpPatch("{uuid:guid}/active"), RequirePermission(P.HRM_ORG_MANAGE)]
    public async Task<IActionResult> SetActive(Guid uuid, [FromBody] SetActiveRequest req, CancellationToken ct) => Done(await _svc.SetActiveAsync(Caller, uuid, req.IsActive, ct));
}

/// <summary>API-CONTRACT §2.6.</summary>
[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[Route("api/hrm/grades")]
public sealed class GradesController : HrmControllerBase
{
    private readonly IGradeService _svc;
    public GradesController(ITenantContext tenant, IGradeService svc) : base(tenant) => _svc = svc;

    [Microsoft.AspNetCore.Http.EndpointSummary("List grades (salary band masked without HRM_SALARY_VIEW)")]
    [HttpGet, RequirePermission(P.HRM_ORG_VIEW, P.HRM_ORG_MANAGE, P.HRM_EMPLOYEE_VIEW, P.HRM_EMPLOYEE_EDIT)]
    public async Task<IActionResult> List([FromQuery] bool includeInactive, CancellationToken ct) => Done(await _svc.ListAsync(Caller, includeInactive, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Create a grade")]
    [HttpPost, RequirePermission(P.HRM_ORG_MANAGE)]
    public async Task<IActionResult> Create([FromBody] GradeUpsert req, CancellationToken ct) => Done(await _svc.CreateAsync(Caller, req, ct), "Grade created.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Update a grade")]
    [HttpPut("{uuid:guid}"), RequirePermission(P.HRM_ORG_MANAGE)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] GradeUpsert req, CancellationToken ct) => Done(await _svc.UpdateAsync(Caller, uuid, req, ct), "Grade saved.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Activate or deactivate a grade")]
    [HttpPatch("{uuid:guid}/active"), RequirePermission(P.HRM_ORG_MANAGE)]
    public async Task<IActionResult> SetActive(Guid uuid, [FromBody] SetActiveRequest req, CancellationToken ct) => Done(await _svc.SetActiveAsync(Caller, uuid, req.IsActive, ct));
}

/// <summary>API-CONTRACT §2.1, §2.7.</summary>
[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[Route("api/hrm")]
public sealed class HrmSettingsController : HrmControllerBase
{
    private readonly IHrmSettingsService _settings;
    private readonly IHrUserScopeService _scopes;

    public HrmSettingsController(ITenantContext tenant, IHrmSettingsService settings, IHrUserScopeService scopes) : base(tenant)
    {
        _settings = settings;
        _scopes = scopes;
    }

    [Microsoft.AspNetCore.Http.EndpointSummary("Get the organization's HR settings")]
    [HttpGet("settings"), RequirePermission(P.HRM_SETTINGS_MANAGE, P.HRM_ACCOUNT_MAPPING_VIEW, P.HRM_PAYROLL_VIEW)]
    public async Task<IActionResult> GetSettings(CancellationToken ct) => Done(await _settings.GetAsync(Caller, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Update the organization's HR settings")]
    [HttpPut("settings"), RequirePermission(P.HRM_SETTINGS_MANAGE)]
    public async Task<IActionResult> UpdateSettings([FromBody] HrmSettingsUpdate req, CancellationToken ct) => Done(await _settings.UpdateAsync(Caller, req, ct), "HR settings saved.");

    [Microsoft.AspNetCore.Http.EndpointSummary("List a user's extra HR scopes")]
    [HttpGet("user-scopes"), RequirePermission(P.HRM_SETTINGS_MANAGE)]
    public async Task<IActionResult> ListScopes([FromQuery] int? userId, CancellationToken ct) => Done(await _scopes.ListAsync(Caller, userId, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Search active users for the HR scope picker")]
    [HttpGet("user-scopes/users"), RequirePermission(P.HRM_SETTINGS_MANAGE)]
    public async Task<IActionResult> ScopeUsers([FromQuery] string? search, CancellationToken ct) => Done(await _scopes.SearchUsersAsync(Caller, search, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Give a user an HR scope on a branch or department")]
    [HttpPost("user-scopes"), RequirePermission(P.HRM_SETTINGS_MANAGE)]
    public async Task<IActionResult> AddScope([FromBody] HrUserScopeCreate req, CancellationToken ct) => Done(await _scopes.AddAsync(Caller, req, ct), "HR scope added.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Remove an HR scope")]
    [HttpDelete("user-scopes/{uuid:guid}"), RequirePermission(P.HRM_SETTINGS_MANAGE)]
    public async Task<IActionResult> RemoveScope(Guid uuid, CancellationToken ct)
    {
        await _scopes.RemoveAsync(Caller, uuid, ct);
        return Ok(ApiResponse.Ok("HR scope removed."));
    }
}

/// <summary>API-CONTRACT §3 (employee), §4 (employment), §5 (bank).</summary>
[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[Route("api/hrm/employees")]
public sealed class EmployeesController : HrmControllerBase
{
    private readonly IEmployeeService _employees;
    private readonly IEmploymentRecordService _employment;
    private readonly IEmployeeBankAccountService _bank;

    public EmployeesController(ITenantContext tenant, IEmployeeService employees, IEmploymentRecordService employment, IEmployeeBankAccountService bank) : base(tenant)
    {
        _employees = employees;
        _employment = employment;
        _bank = bank;
    }

    [Microsoft.AspNetCore.Http.EndpointSummary("List employees in scope (identifiers masked)")]
    [HttpGet, RequirePermission(P.HRM_EMPLOYEE_VIEW, P.HRM_EMPLOYEE_EDIT)]
    public async Task<IActionResult> List([FromQuery] EmployeeListFilter filter, CancellationToken ct) => Done(await _employees.ListAsync(Caller, filter, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("The caller's own employee record")]
    [HttpGet("me"), RequirePermission(P.HRM_SELF_SERVICE)]
    public async Task<IActionResult> Me(CancellationToken ct) => Done(await _employees.GetOwnAsync(Caller, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Get an employee (identifiers masked)")]
    [HttpGet("{uuid:guid}"), RequirePermission(P.HRM_EMPLOYEE_VIEW, P.HRM_EMPLOYEE_EDIT)]
    public async Task<IActionResult> Get(Guid uuid, CancellationToken ct) => Done(await _employees.GetAsync(Caller, uuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Create an employee, optionally with the hire record")]
    [HttpPost, RequirePermission(P.HRM_EMPLOYEE_EDIT)]
    public async Task<IActionResult> Create([FromBody] EmployeeUpsert req, CancellationToken ct) => Done(await _employees.CreateAsync(Caller, req, ct), "Employee created.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Replace an employee's record")]
    [HttpPut("{uuid:guid}"), RequirePermission(P.HRM_EMPLOYEE_EDIT)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] EmployeeUpsert req, CancellationToken ct) => Done(await _employees.UpdateAsync(Caller, uuid, req, ct), "Employee saved.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Change an employee's status")]
    [HttpPatch("{uuid:guid}/status"), RequirePermission(P.HRM_EMPLOYEE_EDIT)]
    public async Task<IActionResult> Status(Guid uuid, [FromBody] EmployeeStatusChange req, CancellationToken ct) => Done(await _employees.ChangeStatusAsync(Caller, uuid, req, ct), "Status changed.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Reveal CNIC and NTN (logged)")]
    [HttpGet("{uuid:guid}/sensitive"), RequirePermission(P.HRM_EMPLOYEE_SENSITIVE_VIEW)]
    public async Task<IActionResult> Sensitive(Guid uuid, CancellationToken ct) => Done(await _employees.RevealAsync(Caller, uuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("List an employee's documents")]
    [HttpGet("{uuid:guid}/documents"), RequirePermission(P.HRM_EMPLOYEE_EDIT, P.HRM_EMPLOYEE_SENSITIVE_VIEW)]
    public async Task<IActionResult> Documents(Guid uuid, CancellationToken ct) => Done(await _employees.ListDocumentsAsync(Caller, uuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Add a document to an employee")]
    [HttpPost("{uuid:guid}/documents"), RequirePermission(P.HRM_EMPLOYEE_EDIT)]
    public async Task<IActionResult> AddDocument(Guid uuid, [FromBody] EmployeeDocumentCreate req, CancellationToken ct) => Done(await _employees.AddDocumentAsync(Caller, uuid, req, ct), "Document added.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Remove an employee's document")]
    [HttpDelete("{uuid:guid}/documents/{documentUuid:guid}"), RequirePermission(P.HRM_EMPLOYEE_EDIT)]
    public async Task<IActionResult> RemoveDocument(Guid uuid, Guid documentUuid, CancellationToken ct)
    {
        await _employees.RemoveDocumentAsync(Caller, uuid, documentUuid, ct);
        return Ok(ApiResponse.Ok("Document removed."));
    }

    // ── §4 employment ──

    [Microsoft.AspNetCore.Http.EndpointSummary("Employment history, newest first")]
    [HttpGet("{uuid:guid}/employment"), RequirePermission(P.HRM_EMPLOYEE_VIEW, P.HRM_EMPLOYEE_EDIT, P.HRM_EMPLOYMENT_MANAGE)]
    public async Task<IActionResult> Employment(Guid uuid, CancellationToken ct) => Done(await _employment.ListAsync(Caller, uuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("The current employment record")]
    [HttpGet("{uuid:guid}/employment/current"), RequirePermission(P.HRM_EMPLOYEE_VIEW, P.HRM_EMPLOYEE_EDIT, P.HRM_EMPLOYMENT_MANAGE)]
    public async Task<IActionResult> CurrentEmployment(Guid uuid, CancellationToken ct) => Done(await _employment.GetCurrentAsync(Caller, uuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Add an employment record (closes the latest)")]
    [HttpPost("{uuid:guid}/employment"), RequirePermission(P.HRM_EMPLOYMENT_MANAGE)]
    public async Task<IActionResult> AddEmployment(Guid uuid, [FromBody] EmploymentRecordCreate req, CancellationToken ct) => Done(await _employment.CreateAsync(Caller, uuid, req, ct), "Employment record added.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Transfer an employee")]
    [HttpPost("{uuid:guid}/employment/transfer"), RequirePermission(P.HRM_EMPLOYMENT_MANAGE)]
    public async Task<IActionResult> Transfer(Guid uuid, [FromBody] EmploymentTransferRequest req, CancellationToken ct) => Done(await _employment.TransferAsync(Caller, uuid, req, ct), "Transfer recorded.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Promote an employee")]
    [HttpPost("{uuid:guid}/employment/promotion"), RequirePermission(P.HRM_EMPLOYMENT_MANAGE)]
    public async Task<IActionResult> Promotion(Guid uuid, [FromBody] EmploymentPromotionRequest req, CancellationToken ct) => Done(await _employment.PromoteAsync(Caller, uuid, req, ct), "Promotion recorded.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Correct the latest employment record")]
    [HttpPut("{uuid:guid}/employment/{recordUuid:guid}"), RequirePermission(P.HRM_EMPLOYMENT_MANAGE)]
    public async Task<IActionResult> CorrectEmployment(Guid uuid, Guid recordUuid, [FromBody] EmploymentRecordCreate req, CancellationToken ct) => Done(await _employment.CorrectAsync(Caller, uuid, recordUuid, req, ct), "Employment record corrected.");

    // ── §5 bank accounts ──

    [Microsoft.AspNetCore.Http.EndpointSummary("List an employee's bank accounts (masked)")]
    [HttpGet("{uuid:guid}/bank-accounts"), RequirePermission(P.HRM_BANK_VIEW, P.HRM_BANK_EDIT)]
    public async Task<IActionResult> BankAccounts(Guid uuid, CancellationToken ct) => Done(await _bank.ListAsync(Caller, uuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Add a bank account")]
    [HttpPost("{uuid:guid}/bank-accounts"), RequirePermission(P.HRM_BANK_EDIT)]
    public async Task<IActionResult> AddBankAccount(Guid uuid, [FromBody] EmployeeBankAccountUpsert req, CancellationToken ct) => Done(await _bank.CreateAsync(Caller, uuid, req, ct), "Bank account added.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Update a bank account")]
    [HttpPut("{uuid:guid}/bank-accounts/{accountUuid:guid}"), RequirePermission(P.HRM_BANK_EDIT)]
    public async Task<IActionResult> UpdateBankAccount(Guid uuid, Guid accountUuid, [FromBody] EmployeeBankAccountUpsert req, CancellationToken ct) => Done(await _bank.UpdateAsync(Caller, uuid, accountUuid, req, ct), "Bank account saved.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Make a bank account the primary")]
    [HttpPost("{uuid:guid}/bank-accounts/{accountUuid:guid}/primary"), RequirePermission(P.HRM_BANK_EDIT)]
    public async Task<IActionResult> SetPrimary(Guid uuid, Guid accountUuid, CancellationToken ct) => Done(await _bank.SetPrimaryAsync(Caller, uuid, accountUuid, ct), "Primary account changed.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Deactivate a bank account")]
    [HttpDelete("{uuid:guid}/bank-accounts/{accountUuid:guid}"), RequirePermission(P.HRM_BANK_EDIT)]
    public async Task<IActionResult> DeactivateBankAccount(Guid uuid, Guid accountUuid, CancellationToken ct) => Done(await _bank.DeactivateAsync(Caller, uuid, accountUuid, ct), "Bank account deactivated.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Reveal a bank account number and IBAN (logged)")]
    [HttpGet("{uuid:guid}/bank-accounts/{accountUuid:guid}/sensitive"), RequirePermission(P.HRM_BANK_VIEW)]
    public async Task<IActionResult> RevealBank(Guid uuid, Guid accountUuid, CancellationToken ct) => Done(await _bank.RevealAsync(Caller, uuid, accountUuid, ct));
}

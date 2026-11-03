using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Security;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;

namespace SMS.Modules.Hrm.Services;

// The seams other HRM agents build on (API-CONTRACT §13). They add new files; these stay CORE's.

/// <summary>An employee's terms on a date, flattened for payroll, attendance and posting.</summary>
public sealed record EmployeeTerms(
    int EmployeeId, Guid EmployeeUuid, string EmployeeNumber, string FullName, string Status, int? UserId,
    int RecordId, DateOnly EffectiveFrom, DateOnly? EffectiveTo, string EmploymentType,
    int DepartmentId, string DepartmentName, int DesignationId, string DesignationName, int BranchId, string BranchName,
    int? GradeId, int? ManagerEmployeeId, int? ShiftId, string? PayrollGroup, int NoticePeriodDays, DateOnly? JoinDate, DateOnly? LeaveDate);

/// <summary>A cost-center share of one employee's pay. Percent sums to 100.</summary>
public sealed record CostCenterShare(string CostCenterCode, decimal Percent);

/// <summary>A decrypted payment destination, for the bank file only: never log it, never return it from a list.</summary>
public sealed record EmployeeBankDetails(
    int EmployeeId, string BankName, string? BranchName, string? AccountTitle, string? AccountNumber, string? Iban, string PaymentMethod);

/// <summary>Read side of the employee master for other agents. Organization explicit, no scope check (the caller has done it).</summary>
internal interface IHrmEmployeeDirectory
{
    /// <summary>The record in force on <paramref name="asOf"/>, else null.</summary>
    Task<EmployeeTerms?> GetTermsAsync(Guid organizationId, int employeeId, DateOnly asOf, CancellationToken ct = default);

    /// <summary>Every employee on the books at some point in [from, to], with the record in force at <paramref name="to"/> (or their last one in the period), optionally of one payroll group ("" = no group).</summary>
    Task<IReadOnlyList<EmployeeTerms>> GetEmployedInPeriodAsync(Guid organizationId, DateOnly from, DateOnly to, string? payrollGroup = null, CancellationToken ct = default);

    /// <summary>§11A.2 — where an employee's expense posts for the mode of the run. Central: the payroll cost center. By department: the record's splits, else the record's override, else the department's cost center, else the payroll cost center.</summary>
    Task<IReadOnlyList<CostCenterShare>> GetCostCenterSharesAsync(Guid organizationId, int recordId, string mode, CancellationToken ct = default);

    /// <summary>The primary active account per employee, decrypted (a missing id = none; PAY blocks the line, or pays by cash/cheque).</summary>
    Task<IReadOnlyDictionary<int, EmployeeBankDetails>> GetPrimaryBankDetailsAsync(Guid organizationId, IReadOnlyCollection<int> employeeIds, CancellationToken ct = default);

    Task<HrmSettings> GetSettingsAsync(Guid organizationId, CancellationToken ct = default);
}

internal sealed class HrmEmployeeDirectory : IHrmEmployeeDirectory
{
    private readonly HrmDbContext _db;
    private readonly IHrmSensitiveData _sensitive;
    private readonly IHrmSeeder _seeder;

    public HrmEmployeeDirectory(HrmDbContext db, IHrmSensitiveData sensitive, IHrmSeeder seeder)
    {
        _db = db;
        _sensitive = sensitive;
        _seeder = seeder;
    }

    private IQueryable<EmploymentRecord> Records(Guid org) =>
        _db.EmploymentRecords.IgnoreQueryFilters().AsNoTracking()
            .Include(r => r.Employee).Include(r => r.Department).Include(r => r.Designation).Include(r => r.Branch)
            .Where(r => r.OrganizationId == org && !r.Employee!.IsDeleted);

    private static EmployeeTerms Map(EmploymentRecord r) => new(
        r.EmployeeId, r.Employee!.Uuid, r.Employee.EmployeeNumber, r.Employee.FullName, r.Employee.Status, r.Employee.UserId,
        r.Id, r.EffectiveFrom, r.EffectiveTo, r.EmploymentType, r.DepartmentId, r.Department!.Name, r.DesignationId, r.Designation!.Name,
        r.BranchId, r.Branch!.Name, r.GradeId, r.ManagerEmployeeId, r.ShiftId, r.PayrollGroup, r.NoticePeriodDays, r.Employee.JoinDate, r.Employee.LeaveDate);

    public async Task<EmployeeTerms?> GetTermsAsync(Guid organizationId, int employeeId, DateOnly asOf, CancellationToken ct = default)
    {
        var record = await Records(organizationId)
            .Where(r => r.EmployeeId == employeeId && r.EffectiveFrom <= asOf && (r.EffectiveTo == null || r.EffectiveTo >= asOf))
            .FirstOrDefaultAsync(ct);
        return record is null ? null : Map(record);
    }

    public async Task<IReadOnlyList<EmployeeTerms>> GetEmployedInPeriodAsync(Guid organizationId, DateOnly from, DateOnly to, string? payrollGroup = null, CancellationToken ct = default)
    {
        var excluded = new[] { EmployeeStatuses.Candidate, EmployeeStatuses.Archived };
        var rows = await Records(organizationId)
            .Where(r => r.EffectiveFrom <= to && (r.EffectiveTo == null || r.EffectiveTo >= from) && !excluded.Contains(r.Employee!.Status))
            .ToListAsync(ct);
        var latest = rows.GroupBy(r => r.EmployeeId).Select(g => g.OrderByDescending(r => r.EffectiveFrom).First());
        if (payrollGroup is not null)
            latest = latest.Where(r => (r.PayrollGroup ?? string.Empty) == payrollGroup);
        return latest.Where(r => r.Employee!.LeaveDate is null || r.Employee.LeaveDate >= from)
            .OrderBy(r => r.Employee!.EmployeeNumber).Select(Map).ToList();
    }

    public async Task<IReadOnlyList<CostCenterShare>> GetCostCenterSharesAsync(Guid organizationId, int recordId, string mode, CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(organizationId, ct);
        var central = settings.PayrollCostCenterId is { } id
            ? await _db.CostCenters.IgnoreQueryFilters().AsNoTracking().Where(c => c.Id == id && c.OrganizationId == organizationId).Select(c => c.Code).FirstOrDefaultAsync(ct)
            : null;
        if (mode == PayrollCostCenterModes.ByDepartment)
        {
            var splits = await _db.EmploymentCostSplits.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.OrganizationId == organizationId && s.EmploymentRecordId == recordId)
                .Select(s => new CostCenterShare(s.CostCenter!.Code, s.Percent)).ToListAsync(ct);
            if (splits.Count > 0) return splits;

            var record = await _db.EmploymentRecords.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.Id == recordId && r.OrganizationId == organizationId)
                .Select(r => new { r.CostCenterCode, Department = r.Department!.CostCenterCode }).FirstOrDefaultAsync(ct);
            var code = record?.CostCenterCode ?? record?.Department ?? central;
            if (code is not null) return [new CostCenterShare(code, 100m)];
        }
        return central is null ? [] : [new CostCenterShare(central, 100m)];
    }

    public async Task<IReadOnlyDictionary<int, EmployeeBankDetails>> GetPrimaryBankDetailsAsync(Guid organizationId, IReadOnlyCollection<int> employeeIds, CancellationToken ct = default)
    {
        var ids = employeeIds.Distinct().ToList();
        var rows = await _db.EmployeeBankAccounts.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.OrganizationId == organizationId && ids.Contains(a.EmployeeId) && a.IsPrimary && a.IsActive).ToListAsync(ct);
        return rows.ToDictionary(a => a.EmployeeId, a => new EmployeeBankDetails(
            a.EmployeeId, a.BankName, a.BranchName, a.AccountTitle,
            a.AccountNumberEncrypted is null ? null : _sensitive.Reveal(a.AccountNumberEncrypted),
            a.IbanEncrypted is null ? null : _sensitive.Reveal(a.IbanEncrypted),
            a.PaymentMethod));
    }

    public async Task<HrmSettings> GetSettingsAsync(Guid organizationId, CancellationToken ct = default)
    {
        await _seeder.EnsureAsync(organizationId, ct);
        return await _db.Settings.IgnoreQueryFilters().FirstAsync(s => s.OrganizationId == organizationId, ct);
    }
}

/// <summary>
/// Submits an HRM document to the workflow engine. Definitions are one NAMED step (HR_APPROVER); this resolves the person: the
/// employee's line manager, else the department head, else the first other active holder of <c>fallbackPermission</c>. Nobody →
/// 409 HRM_NO_APPROVER. Status changes come back through the agent's IDocumentStatusHandler (values: APPROVED / REJECTED / CANCELLED /
/// RECALLED / PENDING…). HRM documents are not DRAFT when submitted, so SkipStatusCheck is on.
/// </summary>
internal interface IHrmApprovalGateway
{
    Task<Guid> SubmitAsync(HrmCaller caller, string interfaceCode, Guid documentUuid, string documentNumber, int? employeeId,
        string fallbackPermission, decimal? conditionValue = null, CancellationToken ct = default);
}

internal sealed class HrmApprovalGateway : IHrmApprovalGateway
{
    private readonly HrmDbContext _db;
    private readonly IWorkflowActionService _workflow;
    private readonly IOrgUserDirectory? _users;

    public HrmApprovalGateway(HrmDbContext db, IWorkflowActionService workflow, IOrgUserDirectory? users = null)
    {
        _db = db;
        _workflow = workflow;
        _users = users;
    }

    public async Task<Guid> SubmitAsync(HrmCaller caller, string interfaceCode, Guid documentUuid, string documentNumber, int? employeeId,
        string fallbackPermission, decimal? conditionValue = null, CancellationToken ct = default)
    {
        var approver = await ResolveApproverAsync(caller, employeeId, fallbackPermission, ct)
            ?? throw HrmErrors.Conflict("HRM_NO_APPROVER", "Nobody can approve this: give the employee a line manager with a user account, or give someone the approving permission.");
        return await _workflow.SubmitAsync(new SubmitDocumentCommand
        {
            InterfaceCode   = interfaceCode,
            DocumentId      = documentUuid,
            DocumentNumber  = documentNumber,
            ConditionValue  = conditionValue ?? 0m,
            SubmittedBy     = caller.UserId,
            NamedApprovers  = new Dictionary<string, int> { [HrmWorkflowCodes.NamedApprover] = approver },
            SkipStatusCheck = true
        });
    }

    internal async Task<int?> ResolveApproverAsync(HrmCaller caller, int? employeeId, string fallbackPermission, CancellationToken ct)
    {
        var org = caller.OrganizationId;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (employeeId is { } id)
        {
            var record = await _db.EmploymentRecords.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.OrganizationId == org && r.EmployeeId == id && r.EffectiveFrom <= today && (r.EffectiveTo == null || r.EffectiveTo >= today))
                .Select(r => new { Manager = r.ManagerEmployee!.UserId, Head = r.Department!.HeadEmployee!.UserId, HeadId = r.Department.HeadEmployeeId })
                .FirstOrDefaultAsync(ct);
            if (record?.Manager is { } manager && manager != caller.UserId) return manager;
            if (record?.Head is { } head && head != caller.UserId && record.HeadId != id) return head;
        }
        if (_users is null) return null;
        var holders = await _users.SearchAsync(org, null, 200, ct);
        return holders.Where(u => u.IsActive && u.UserId != caller.UserId && u.Permissions.Contains(fallbackPermission))
            .OrderBy(u => u.UserId).Select(u => (int?)u.UserId).FirstOrDefault();
    }
}

/// <summary>Files a generated PDF for an HR document; returns the attachment uuid (the spec's FileKey). Read back with <see cref="ReadAsync"/>.</summary>
internal interface IHrmDocumentStore
{
    Task<Guid> StorePdfAsync(HrmCaller caller, string interfaceCode, Guid documentUuid, string fileName, byte[] pdf, string requiredPermission, CancellationToken ct = default);
    Task<AttachmentContent?> ReadAsync(Guid attachmentUuid);
}

internal sealed class HrmDocumentStore : IHrmDocumentStore
{
    private readonly IAttachmentService _attachments;
    public HrmDocumentStore(IAttachmentService attachments) => _attachments = attachments;

    public async Task<Guid> StorePdfAsync(HrmCaller caller, string interfaceCode, Guid documentUuid, string fileName, byte[] pdf, string requiredPermission, CancellationToken ct = default)
    {
        var stored = await _attachments.StoreGeneratedAsync(new GeneratedAttachmentRequest
        {
            InterfaceCode = interfaceCode, DocumentId = documentUuid, FileName = fileName, ContentType = "application/pdf",
            Content = pdf, RequiredPermission = requiredPermission
        }, caller.UserId);
        return stored.Uuid;
    }

    public Task<AttachmentContent?> ReadAsync(Guid attachmentUuid) => _attachments.GetContentAsync(attachmentUuid);
}

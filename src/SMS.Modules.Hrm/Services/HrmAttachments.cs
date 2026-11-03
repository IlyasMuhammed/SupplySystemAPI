using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Security;
using SMS.Modules.Hrm.Services.Tal;
using SMS.Shared.Authorization;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;

namespace SMS.Modules.Hrm.Services;

/// <summary>What a row says about one of its files. The bytes come from the content route only.</summary>
public sealed class HrmAttachmentModel
{
    public Guid     Uuid         { get; set; }
    public string   FileName     { get; set; } = "";
    public string?  ContentType  { get; set; }
    public long     FileSize     { get; set; }
    public DateTime UploadedDate { get; set; }
}

/// <summary>
/// A49 D-25 — the only way to read the five HRM upload kinds. <c>api/attachments</c> only knows a permission, so it refuses them; here the
/// row holding the file decides: self, or the kind's permission plus HR scope. Anything the caller may not see is a 404, as for another
/// organization's row.
/// </summary>
public interface IHrmAttachmentAccess
{
    Task<AttachmentContent> ReadAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<IReadOnlyList<HrmAttachmentModel>> ListAsync(HrmCaller caller, string interfaceCode, Guid rowUuid, CancellationToken ct = default);

    /// <summary>A file chosen on a form and then dropped. Only its uploader, and only while no row holds it.</summary>
    Task RemoveUnusedAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
}

/// <summary>
/// A49 D-25 — what a save checks before a row keeps an attachment uuid: it exists in this organization, is the right kind, and this caller
/// uploaded it. A made-up uuid cannot satisfy LEAVE_ATTACHMENT_REQUIRED, and pasting a uuid from someone else's row cannot grant read access
/// to that file through a row of your own. A uuid the row already holds is not checked again, so another user can edit the row.
/// </summary>
internal interface IHrmAttachmentGuard
{
    Task RequireAsync(HrmCaller caller, Guid uuid, string interfaceCode);
}

internal static class HrmAttachmentGuardExtensions
{
    /// <summary>The value to store: null for no file. A null guard is a unit harness with no attachment store.</summary>
    public static async Task<Guid?> CheckedAsync(this IHrmAttachmentGuard? guard, HrmCaller caller, Guid? uuid, string interfaceCode, params Guid?[] alreadyOnRow)
    {
        if (uuid is not { } value || value == Guid.Empty) return null;
        if (guard is not null && !alreadyOnRow.Contains(value)) await guard.RequireAsync(caller, value, interfaceCode);
        return value;
    }
}

internal sealed class HrmAttachmentGuard : IHrmAttachmentGuard
{
    private readonly IAttachmentService? _attachments;
    public HrmAttachmentGuard(IAttachmentService? attachments = null) => _attachments = attachments;

    public async Task RequireAsync(HrmCaller caller, Guid uuid, string interfaceCode)
    {
        if (_attachments is null) return;
        var file = await _attachments.FindAsync(uuid);
        // One answer for all three, so the save does not reveal whether someone else's file exists.
        if (file is null || file.InterfaceCode != interfaceCode || file.UploadedBy != caller.UserId)
            throw HrmErrors.Bad("HRM_ATTACHMENT_INVALID", "This file was not found among your uploads for this form. Upload it again.");
    }
}

internal sealed class HrmAttachmentAccess : IHrmAttachmentAccess
{
    private readonly HrmDbContext _db;
    private readonly IHrmScopeService _scope;
    private readonly IAttachmentService _attachments;
    private readonly IRecruitmentService _recruitment;

    public HrmAttachmentAccess(HrmDbContext db, IHrmScopeService scope, IAttachmentService attachments, IRecruitmentService recruitment)
    {
        _db = db;
        _scope = scope;
        _attachments = attachments;
        _recruitment = recruitment;
    }

    /// <summary>Who holds this kind's files besides the employee: the same codes as the kind's View rule, without self-service.</summary>
    private static string[] HrCodes(string kind) => kind switch
    {
        HrmAttachmentCodes.Employee => [PermissionCodes.HRM_EMPLOYEE_EDIT, PermissionCodes.HRM_EMPLOYEE_SENSITIVE_VIEW],
        HrmAttachmentCodes.LeaveRequest => [PermissionCodes.HRM_LEAVE_VIEW, PermissionCodes.HRM_LEAVE_APPROVE, PermissionCodes.HRM_LEAVE_MANAGE],
        HrmAttachmentCodes.ExpenseClaim => [PermissionCodes.HRM_EXPENSE_VIEW, PermissionCodes.HRM_EXPENSE_APPROVE, PermissionCodes.HRM_EXPENSE_PAY],
        HrmAttachmentCodes.Enrollment => [PermissionCodes.HRM_TRAINING_VIEW, PermissionCodes.HRM_TRAINING_MANAGE],
        HrmAttachmentCodes.Candidate => [PermissionCodes.HRM_RECRUITMENT_VIEW, PermissionCodes.HRM_RECRUITMENT_MANAGE],
        _ => []
    };

    /// <summary>The rows holding a file. Deleted rows count: a file does not become its uploader's again because the row was deleted.</summary>
    private async Task<(bool Linked, List<int> EmployeeIds, List<Guid> CandidateUuids)> HoldersAsync(Guid org, string kind, Guid uuid, CancellationToken ct)
    {
        List<int> employees = [];
        List<Guid> candidates = [];
        switch (kind)
        {
            case HrmAttachmentCodes.Employee:
                employees.AddRange(await _db.EmployeeDocuments.IgnoreQueryFilters().AsNoTracking().Where(d => d.OrganizationId == org && d.AttachmentUuid == uuid).Select(d => d.EmployeeId).ToListAsync(ct));
                employees.AddRange(await _db.Employees.IgnoreQueryFilters().AsNoTracking().Where(e => e.OrganizationId == org && e.PhotoAttachmentUuid == uuid).Select(e => e.Id).ToListAsync(ct));
                break;
            case HrmAttachmentCodes.LeaveRequest:
                employees.AddRange(await _db.LeaveRequests.IgnoreQueryFilters().AsNoTracking().Where(r => r.OrganizationId == org && r.AttachmentUuid == uuid).Select(r => r.EmployeeId).ToListAsync(ct));
                break;
            case HrmAttachmentCodes.ExpenseClaim:
                employees.AddRange(await _db.ExpenseLines.IgnoreQueryFilters().AsNoTracking().Where(l => l.OrganizationId == org && l.AttachmentUuid == uuid).Select(l => l.Claim!.EmployeeId).ToListAsync(ct));
                break;
            case HrmAttachmentCodes.Enrollment:
                employees.AddRange(await _db.CourseEnrollments.IgnoreQueryFilters().AsNoTracking().Where(e => e.OrganizationId == org && e.CertificateAttachmentUuid == uuid).Select(e => e.EmployeeId).ToListAsync(ct));
                break;
            case HrmAttachmentCodes.Candidate:
                candidates.AddRange(await _db.Candidates.IgnoreQueryFilters().AsNoTracking().Where(c => c.OrganizationId == org && c.ResumeAttachmentUuid == uuid).Select(c => c.Uuid).ToListAsync(ct));
                break;
        }
        return (employees.Count > 0 || candidates.Count > 0, employees, candidates);
    }

    private async Task<bool> MaySeeEmployeeAsync(HrmCaller caller, string kind, int employeeId, CancellationToken ct)
    {
        if (caller.Has(PermissionCodes.HRM_SELF_SERVICE) && await _scope.OwnEmployeeIdAsync(caller, ct) == employeeId) return true;
        return caller.HasAny(HrCodes(kind)) && await _scope.CanAccessAsync(caller, employeeId, ct);
    }

    /// <summary>Recruitment scope lives in the recruitment service (own candidates + vacancies in scope): asked rather than copied.</summary>
    private async Task<bool> MaySeeCandidateAsync(HrmCaller caller, Guid candidateUuid, CancellationToken ct)
    {
        if (!caller.HasAny(HrCodes(HrmAttachmentCodes.Candidate))) return false;
        try { await _recruitment.GetCandidateAsync(caller, candidateUuid, ct); return true; }
        catch (NotFoundException) { return false; }
    }

    private async Task<AttachmentModel> RequireReadableAsync(HrmCaller caller, Guid uuid, CancellationToken ct)
    {
        var file = await _attachments.FindAsync(uuid);
        if (file is null || !AttachmentAccessPolicy.KeepsUploadsPrivate(file.InterfaceCode)) throw new NotFoundException("Attachment", uuid);

        var (linked, employees, candidates) = await HoldersAsync(caller.OrganizationId, file.InterfaceCode, uuid, ct);
        if (!linked && file.UploadedBy == caller.UserId) return file;
        foreach (var employee in employees) if (await MaySeeEmployeeAsync(caller, file.InterfaceCode, employee, ct)) return file;
        foreach (var candidate in candidates) if (await MaySeeCandidateAsync(caller, candidate, ct)) return file;
        throw new NotFoundException("Attachment", uuid);
    }

    public async Task<AttachmentContent> ReadAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        await RequireReadableAsync(caller, uuid, ct);
        return await _attachments.GetContentAsync(uuid) ?? throw new NotFoundException("Attachment", uuid);
    }

    public async Task<IReadOnlyList<HrmAttachmentModel>> ListAsync(HrmCaller caller, string interfaceCode, Guid rowUuid, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var kind = interfaceCode?.Trim() ?? "";
        if (!AttachmentAccessPolicy.KeepsUploadsPrivate(kind)) throw HrmErrors.Bad("HRM_ATTACHMENT_KIND_INVALID", "This kind of document has no HRM attachments.");

        int? employeeId = null;
        Guid?[] held;
        switch (kind)
        {
            case HrmAttachmentCodes.Employee:
            {
                var row = await _db.Employees.IgnoreQueryFilters().AsNoTracking().Where(e => e.OrganizationId == org && !e.IsDeleted && e.Uuid == rowUuid).Select(e => new { e.Id, e.PhotoAttachmentUuid }).FirstOrDefaultAsync(ct);
                if (row is null) throw new NotFoundException("Employee", rowUuid);
                employeeId = row.Id;
                var documents = await _db.EmployeeDocuments.IgnoreQueryFilters().AsNoTracking().Where(d => d.OrganizationId == org && d.EmployeeId == row.Id).Select(d => (Guid?)d.AttachmentUuid).ToListAsync(ct);
                held = [row.PhotoAttachmentUuid, .. documents];
                break;
            }
            case HrmAttachmentCodes.LeaveRequest:
            {
                var row = await _db.LeaveRequests.IgnoreQueryFilters().AsNoTracking().Where(r => r.OrganizationId == org && r.Uuid == rowUuid).Select(r => new { r.EmployeeId, r.AttachmentUuid }).FirstOrDefaultAsync(ct);
                if (row is null) throw new NotFoundException("Leave request", rowUuid);
                employeeId = row.EmployeeId;
                held = [row.AttachmentUuid];
                break;
            }
            case HrmAttachmentCodes.ExpenseClaim:
            {
                var row = await _db.ExpenseClaims.IgnoreQueryFilters().AsNoTracking().Where(c => c.OrganizationId == org && c.Uuid == rowUuid).Select(c => new { c.EmployeeId, Files = c.Lines.Select(l => l.AttachmentUuid).ToList() }).FirstOrDefaultAsync(ct);
                if (row is null) throw new NotFoundException("Expense claim", rowUuid);
                employeeId = row.EmployeeId;
                held = [.. row.Files];
                break;
            }
            case HrmAttachmentCodes.Enrollment:
            {
                var row = await _db.CourseEnrollments.IgnoreQueryFilters().AsNoTracking().Where(e => e.OrganizationId == org && e.Uuid == rowUuid).Select(e => new { e.EmployeeId, e.CertificateAttachmentUuid }).FirstOrDefaultAsync(ct);
                if (row is null) throw new NotFoundException("Enrollment", rowUuid);
                employeeId = row.EmployeeId;
                held = [row.CertificateAttachmentUuid];
                break;
            }
            default: // HRM_CANDIDATE
            {
                var row = await _db.Candidates.IgnoreQueryFilters().AsNoTracking().Where(c => c.OrganizationId == org && !c.IsDeleted && c.Uuid == rowUuid).Select(c => new { c.ResumeAttachmentUuid }).FirstOrDefaultAsync(ct);
                if (row is null) throw new NotFoundException("Candidate", rowUuid);
                held = [row.ResumeAttachmentUuid];
                break;
            }
        }

        var allowed = employeeId is { } id ? await MaySeeEmployeeAsync(caller, kind, id, ct) : await MaySeeCandidateAsync(caller, rowUuid, ct);
        if (!allowed) throw new NotFoundException("Attachment", rowUuid);

        var result = new List<HrmAttachmentModel>();
        foreach (var uuid in held.OfType<Guid>().Distinct())
        {
            // A row's uuid names a file of its own kind only; anything else is left out rather than served.
            if (await _attachments.FindAsync(uuid) is { } file && file.InterfaceCode == kind)
                result.Add(new HrmAttachmentModel { Uuid = file.UUID, FileName = file.FileName, ContentType = file.ContentType, FileSize = file.FileSize ?? 0, UploadedDate = file.UploadedDate });
        }
        return result;
    }

    public async Task RemoveUnusedAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var file = await _attachments.FindAsync(uuid);
        if (file is null || !AttachmentAccessPolicy.KeepsUploadsPrivate(file.InterfaceCode) || file.UploadedBy != caller.UserId)
            throw new NotFoundException("Attachment", uuid);
        if ((await HoldersAsync(caller.OrganizationId, file.InterfaceCode, uuid, ct)).Linked)
            throw HrmErrors.Conflict("ATTACHMENT_IN_USE", "This file is on a saved record, so it cannot be removed here.");
        await _attachments.RemovePrivateUploadAsync(uuid);
    }
}

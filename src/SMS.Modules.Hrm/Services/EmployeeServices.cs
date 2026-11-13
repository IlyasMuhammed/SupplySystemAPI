using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Security;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Hrm.Services;

// API-CONTRACT §3–§5. Rule: the list and the detail never decrypt anything. An S3 value leaves the server only from a reveal
// endpoint, which needs its own permission and writes the access log first.

public interface IEmployeeService
{
    Task<PaginatedResponse<EmployeeListItem>> ListAsync(HrmCaller caller, EmployeeListFilter filter, CancellationToken ct = default);
    /// <summary>The picker: employees in scope who are not Archived or Candidates, by number or name, at most 200. No contact, identity, bank or pay field.</summary>
    Task<IReadOnlyList<EmployeeLookupItem>> LookupAsync(HrmCaller caller, string? search, int take, CancellationToken ct = default);
    Task<EmployeeDetail> GetAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    /// <summary>Self service: the caller's own record (404 HRM_NO_EMPLOYEE when the user has none).</summary>
    Task<EmployeeDetail> GetOwnAsync(HrmCaller caller, CancellationToken ct = default);
    Task<EmployeeDetail> CreateAsync(HrmCaller caller, EmployeeUpsert request, CancellationToken ct = default);
    Task<EmployeeDetail> UpdateAsync(HrmCaller caller, Guid uuid, EmployeeUpsert request, CancellationToken ct = default);
    Task<EmployeeDetail> ChangeStatusAsync(HrmCaller caller, Guid uuid, EmployeeStatusChange request, CancellationToken ct = default);
    Task<EmployeeSensitiveModel> RevealAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<IReadOnlyList<EmployeeDocumentModel>> ListDocumentsAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<EmployeeDocumentModel> AddDocumentAsync(HrmCaller caller, Guid uuid, EmployeeDocumentCreate request, CancellationToken ct = default);
    Task RemoveDocumentAsync(HrmCaller caller, Guid uuid, Guid documentUuid, CancellationToken ct = default);
}

public interface IEmploymentRecordService
{
    Task<IReadOnlyList<EmploymentRecordModel>> ListAsync(HrmCaller caller, Guid employeeUuid, CancellationToken ct = default);
    /// <summary>Task 059 — the record in force today, else null.</summary>
    Task<EmploymentRecordModel?> GetCurrentAsync(HrmCaller caller, Guid employeeUuid, CancellationToken ct = default);
    /// <summary>A new record: closes the one it follows at EffectiveFrom − 1. No overlap; never before the latest record's start.</summary>
    Task<EmploymentRecordModel> CreateAsync(HrmCaller caller, Guid employeeUuid, EmploymentRecordCreate request, CancellationToken ct = default);
    Task<EmploymentRecordModel> TransferAsync(HrmCaller caller, Guid employeeUuid, EmploymentTransferRequest request, CancellationToken ct = default);
    Task<EmploymentRecordModel> PromoteAsync(HrmCaller caller, Guid employeeUuid, EmploymentPromotionRequest request, CancellationToken ct = default);
    /// <summary>Corrects the latest record in place (ChangeReason unchanged).</summary>
    Task<EmploymentRecordModel> CorrectAsync(HrmCaller caller, Guid employeeUuid, Guid recordUuid, EmploymentRecordCreate request, CancellationToken ct = default);
}

public interface IEmployeeBankAccountService
{
    Task<IReadOnlyList<EmployeeBankAccountModel>> ListAsync(HrmCaller caller, Guid employeeUuid, CancellationToken ct = default);
    Task<EmployeeBankAccountModel> CreateAsync(HrmCaller caller, Guid employeeUuid, EmployeeBankAccountUpsert request, CancellationToken ct = default);
    Task<EmployeeBankAccountModel> UpdateAsync(HrmCaller caller, Guid employeeUuid, Guid accountUuid, EmployeeBankAccountUpsert request, CancellationToken ct = default);
    Task<EmployeeBankAccountModel> SetPrimaryAsync(HrmCaller caller, Guid employeeUuid, Guid accountUuid, CancellationToken ct = default);
    Task<EmployeeBankAccountModel> DeactivateAsync(HrmCaller caller, Guid employeeUuid, Guid accountUuid, CancellationToken ct = default);
    Task<EmployeeBankSensitiveModel> RevealAsync(HrmCaller caller, Guid employeeUuid, Guid accountUuid, CancellationToken ct = default);
}

internal sealed class EmployeeService : HrmServiceBase, IEmployeeService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IHrmScopeService _scope;
    private readonly IHrmSensitiveData _sensitive;
    private readonly IHrmSeeder _seeder;
    private readonly IEmploymentRecordFactory _records;
    private readonly IOrgUserDirectory? _users;
    private readonly IHrmAttachmentGuard? _attachments;

    public EmployeeService(HrmDbContext db, IHrmScopeService scope, IHrmSensitiveData sensitive, IHrmSeeder seeder, IEmploymentRecordFactory records,
        IOrgUserDirectory? users = null, IHrmAttachmentGuard? attachments = null) : base(db)
    {
        _attachments = attachments;
        _scope = scope;
        _sensitive = sensitive;
        _seeder = seeder;
        _records = records;
        _users = users;
    }

    public async Task<PaginatedResponse<EmployeeListItem>> ListAsync(HrmCaller caller, EmployeeListFilter filter, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var scope = await _scope.GetAsync(caller, ct);
        var q = _scope.Apply(Db.Employees.AsNoTracking(), caller, scope);

        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            var status = HrmValidate.OneOf(filter.Status, EmployeeStatuses.All, "status");
            q = q.Where(e => e.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim();
            q = q.Where(e => e.EmployeeNumber.Contains(s) || (e.FirstName + " " + e.LastName).Contains(s) || (e.WorkEmail != null && e.WorkEmail.Contains(s)));
        }
        if (!string.IsNullOrWhiteSpace(filter.Cnic))
        {
            caller.Require(PermissionCodes.HRM_EMPLOYEE_SENSITIVE_VIEW, "Searching by CNIC needs the permission to view identity numbers.");
            var digits = _sensitive.NormalizeCnic(filter.Cnic)!;
            var hash = _sensitive.Hash(org, digits);
            q = q.Where(e => e.CnicHash == hash);
        }

        var today = HrmValidate.Today;
        // A department/branch/designation/manager filter reads the record in force.
        if (filter.DepartmentUuid is { } dept) q = q.Where(e => e.EmploymentRecords.Any(r => r.EffectiveFrom <= today && (r.EffectiveTo == null || r.EffectiveTo >= today) && r.Department!.Uuid == dept));
        if (filter.BranchUuid is { } branch) q = q.Where(e => e.EmploymentRecords.Any(r => r.EffectiveFrom <= today && (r.EffectiveTo == null || r.EffectiveTo >= today) && r.Branch!.Uuid == branch));
        if (filter.DesignationUuid is { } desig) q = q.Where(e => e.EmploymentRecords.Any(r => r.EffectiveFrom <= today && (r.EffectiveTo == null || r.EffectiveTo >= today) && r.Designation!.Uuid == desig));
        if (filter.ManagerUuid is { } manager) q = q.Where(e => e.EmploymentRecords.Any(r => r.EffectiveFrom <= today && (r.EffectiveTo == null || r.EffectiveTo >= today) && r.ManagerEmployee!.Uuid == manager));

        var page = Math.Max(filter.Page, 1);
        var size = Math.Clamp(filter.PageSize, 1, 200);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(e => e.EmployeeNumber).Skip((page - 1) * size).Take(size)
            .Select(e => new
            {
                e.Id, e.Uuid, e.EmployeeNumber, e.FirstName, e.LastName, e.Status, e.WorkEmail, e.Mobile, e.JoinDate, e.CnicLastDigit
            }).ToListAsync(ct);

        var ids = rows.Select(r => r.Id).ToList();
        var terms = await Db.EmploymentRecords.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.OrganizationId == org && ids.Contains(r.EmployeeId) && r.EffectiveFrom <= today && (r.EffectiveTo == null || r.EffectiveTo >= today))
            .Select(r => new
            {
                r.EmployeeId, DepartmentUuid = r.Department!.Uuid, DepartmentName = r.Department.Name, DesignationUuid = r.Designation!.Uuid, DesignationName = r.Designation.Name,
                BranchUuid = r.Branch!.Uuid, BranchName = r.Branch.Name, ManagerUuid = r.ManagerEmployee == null ? (Guid?)null : r.ManagerEmployee.Uuid,
                ManagerName = r.ManagerEmployee == null ? null : r.ManagerEmployee.FirstName + " " + r.ManagerEmployee.LastName
            }).ToListAsync(ct);
        var byEmployee = terms.GroupBy(t => t.EmployeeId).ToDictionary(g => g.Key, g => g.First());

        return new PaginatedResponse<EmployeeListItem>
        {
            Data = rows.Select(r =>
            {
                byEmployee.TryGetValue(r.Id, out var t);
                return new EmployeeListItem
                {
                    Uuid = r.Uuid, EmployeeNumber = r.EmployeeNumber, FullName = $"{r.FirstName} {r.LastName}".Trim(), Status = r.Status, WorkEmail = r.WorkEmail, Mobile = r.Mobile,
                    JoinDate = r.JoinDate, CnicMasked = _sensitive.MaskCnic(r.CnicLastDigit),
                    DepartmentUuid = t?.DepartmentUuid, DepartmentName = t?.DepartmentName, DesignationUuid = t?.DesignationUuid, DesignationName = t?.DesignationName,
                    BranchUuid = t?.BranchUuid, BranchName = t?.BranchName, ManagerUuid = t?.ManagerUuid, ManagerName = t?.ManagerName
                };
            }).ToList(),
            TotalRecords = total, Page = page, PageSize = size, TotalPages = (int)Math.Ceiling(total / (double)size)
        };
    }

    public async Task<IReadOnlyList<EmployeeLookupItem>> LookupAsync(HrmCaller caller, string? search, int take, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var scope = await _scope.GetAsync(caller, ct);
        var q = _scope.Apply(Db.Employees.AsNoTracking(), caller, scope)
            .Where(e => e.Status != EmployeeStatuses.Archived && e.Status != EmployeeStatuses.Candidate);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(e => e.EmployeeNumber.Contains(s) || (e.FirstName + " " + e.LastName).Contains(s));
        }
        var today = HrmValidate.Today;
        return await q.OrderBy(e => e.EmployeeNumber).Take(Math.Clamp(take <= 0 ? 200 : take, 1, 200))
            .Select(e => new EmployeeLookupItem
            {
                Uuid = e.Uuid, EmployeeNumber = e.EmployeeNumber, FullName = (e.FirstName + " " + e.LastName).Trim(), Status = e.Status,
                DepartmentName = e.EmploymentRecords.Where(r => r.OrganizationId == org && r.EffectiveFrom <= today && (r.EffectiveTo == null || r.EffectiveTo >= today))
                    .Select(r => r.Department!.Name).FirstOrDefault()
            }).ToListAsync(ct);
    }

    public async Task<EmployeeDetail> GetAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default) =>
        await MapAsync(caller, await _scope.RequireEmployeeAsync(caller, uuid, ct), ct);

    public async Task<EmployeeDetail> GetOwnAsync(HrmCaller caller, CancellationToken ct = default) =>
        await MapAsync(caller, await _scope.RequireOwnEmployeeAsync(caller, ct), ct);

    private async Task<EmployeeDetail> MapAsync(HrmCaller caller, Employee e, CancellationToken ct)
    {
        var current = await _records.CurrentModelAsync(caller.OrganizationId, e.Id, ct);
        return new EmployeeDetail
        {
            Uuid = e.Uuid, EmployeeNumber = e.EmployeeNumber, FirstName = e.FirstName, LastName = e.LastName, FullName = e.FullName, FatherOrHusbandName = e.FatherOrHusbandName,
            DateOfBirth = e.DateOfBirth, Gender = e.Gender, MaritalStatus = e.MaritalStatus, Nationality = e.Nationality,
            CnicMasked = _sensitive.MaskCnic(e.CnicLastDigit), NtnMasked = _sensitive.MaskTail(e.NtnLast4),
            PersonalEmail = e.PersonalEmail, WorkEmail = e.WorkEmail, Phone = e.Phone, Mobile = e.Mobile,
            CurrentAddress = Read<EmployeeAddress>(e.CurrentAddressJson), PermanentAddress = Read<EmployeeAddress>(e.PermanentAddressJson),
            EmergencyContacts = Read<List<EmergencyContact>>(e.EmergencyContactsJson) ?? [],
            PhotoAttachmentUuid = e.PhotoAttachmentUuid, Status = e.Status, UserId = e.UserId, SourceCandidateUuid = e.SourceCandidateUuid,
            JoinDate = e.JoinDate, LeaveDate = e.LeaveDate, DepartureReason = e.DepartureReason, Notes = caller.Has(PermissionCodes.HRM_EMPLOYEE_EDIT) ? e.Notes : null,
            CurrentEmployment = current, AllowedStatuses = EmployeeStatuses.NextFrom(e.Status).ToList(),
            CanEdit = caller.Has(PermissionCodes.HRM_EMPLOYEE_EDIT) && e.Status != EmployeeStatuses.Archived,
            CanRevealSensitive = caller.Has(PermissionCodes.HRM_EMPLOYEE_SENSITIVE_VIEW)
        };
    }

    private static T? Read<T>(string? json) where T : class => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<T>(json, Json);
    private static string? Write<T>(T? value) where T : class => value is null ? null : JsonSerializer.Serialize(value, Json);

    private async Task ApplyAsync(Employee row, EmployeeUpsert r, HrmCaller caller, CancellationToken ct)
    {
        var org = caller.OrganizationId;
        row.FirstName = HrmValidate.Name(r.FirstName, 60, "First name");
        row.LastName = HrmValidate.Name(r.LastName, 60, "Last name");
        row.FatherOrHusbandName = HrmValidate.Optional(r.FatherOrHusbandName, 100, "Father's or husband's name");
        if (r.DateOfBirth is { } dob && dob > HrmValidate.Today) throw new BadRequestException("The date of birth cannot be in the future.");
        row.DateOfBirth = r.DateOfBirth;
        row.Gender = string.IsNullOrWhiteSpace(r.Gender) ? null : HrmValidate.OneOf(r.Gender, Genders.All, "gender");
        row.MaritalStatus = string.IsNullOrWhiteSpace(r.MaritalStatus) ? null : HrmValidate.OneOf(r.MaritalStatus, MaritalStatuses.All, "maritalStatus");
        row.Nationality = HrmValidate.Optional(r.Nationality, 3, "Nationality")?.ToUpperInvariant();
        row.PersonalEmail = HrmValidate.Optional(r.PersonalEmail, 200, "Personal email");
        row.WorkEmail = HrmValidate.Optional(r.WorkEmail, 200, "Work email");
        row.Phone = HrmValidate.Optional(r.Phone, 30, "Phone");
        row.Mobile = HrmValidate.Optional(r.Mobile, 30, "Mobile");
        row.CurrentAddressJson = Write(r.CurrentAddress);
        row.PermanentAddressJson = Write(r.PermanentAddress);
        row.EmergencyContactsJson = r.EmergencyContacts is null ? row.EmergencyContactsJson : Write(r.EmergencyContacts);
        row.PhotoAttachmentUuid = await _attachments.CheckedAsync(caller, r.PhotoAttachmentUuid, HrmAttachmentCodes.Employee, row.PhotoAttachmentUuid);
        row.JoinDate = r.JoinDate ?? row.JoinDate;
        row.Notes = HrmValidate.Optional(r.Notes, 2000, "Notes");

        // Null = keep, "" = clear.
        if (r.Cnic is not null)
        {
            var digits = _sensitive.NormalizeCnic(r.Cnic);
            if (digits is null) { row.CnicEncrypted = row.CnicHash = row.CnicLastDigit = null; }
            else
            {
                var p = _sensitive.Protect(org, digits, 1);
                if (await Db.Employees.IgnoreQueryFilters().AnyAsync(e => e.OrganizationId == org && e.CnicHash == p.Hash && e.Id != row.Id && !e.IsDeleted, ct))
                    throw HrmErrors.Conflict("EMPLOYEE_CNIC_DUPLICATE", "Another employee already has this CNIC.");
                row.CnicEncrypted = p.Cipher;
                row.CnicHash = p.Hash;
                row.CnicLastDigit = p.Tail;
            }
        }
        if (r.Ntn is not null)
        {
            var ntn = r.Ntn.Trim();
            if (ntn.Length == 0) { row.NtnEncrypted = row.NtnHash = row.NtnLast4 = null; }
            else
            {
                if (ntn.Length > 30) throw new BadRequestException("An NTN cannot be longer than 30 characters.");
                var p = _sensitive.Protect(org, ntn, 4);
                row.NtnEncrypted = p.Cipher;
                row.NtnHash = p.Hash;
                row.NtnLast4 = p.Tail;
            }
        }

        if (r.UserId != row.UserId)
        {
            if (r.UserId is { } userId)
            {
                if (await Db.Employees.IgnoreQueryFilters().AnyAsync(e => e.OrganizationId == org && e.UserId == userId && e.Id != row.Id && !e.IsDeleted, ct))
                    throw HrmErrors.Conflict("EMPLOYEE_USER_TAKEN", "This user is already linked to another employee.");
                if (_users is not null && (await _users.GetUsersAsync(org, [userId], ct)).Count == 0)
                    throw new NotFoundException("User", userId);
            }
            row.UserId = r.UserId;
        }
    }

    /// <summary>Task 042 — atomic, so two concurrent hires never get the same number and a number is never reused.</summary>
    private async Task<string> NextNumberAsync(Guid org, CancellationToken ct)
    {
        await _seeder.EnsureAsync(org, ct);
        if (Db.Database.IsRelational())
        {
            var result = await Db.Database.SqlQueryRaw<EmployeeNumberRow>(
                "UPDATE [hrm].[HrmSettings] SET [NextEmployeeNumber] = [NextEmployeeNumber] + 1 OUTPUT deleted.[NextEmployeeNumber] AS [Number], inserted.[EmployeeNumberPrefix] AS [Prefix] WHERE [OrganizationId] = {0}", org)
                .AsNoTracking().ToListAsync(ct);
            var row = result.Single();
            return $"{row.Prefix}-{row.Number:D4}";
        }
        var settings = await Db.Settings.IgnoreQueryFilters().FirstAsync(s => s.OrganizationId == org, ct);
        var number = settings.NextEmployeeNumber++;
        await Db.SaveChangesAsync(ct);
        return $"{settings.EmployeeNumberPrefix}-{number:D4}";
    }

    public async Task<EmployeeDetail> CreateAsync(HrmCaller caller, EmployeeUpsert request, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var status = string.IsNullOrWhiteSpace(request.Status) ? EmployeeStatuses.Active : HrmValidate.OneOf(request.Status, EmployeeStatuses.All, "status");
        if (status is not (EmployeeStatuses.Candidate or EmployeeStatuses.Probation or EmployeeStatuses.Active))
            throw HrmErrors.Bad("EMPLOYEE_STATUS_INVALID", "A new employee starts as Candidate, Probation or Active.");

        var row = new Employee { Status = status, SourceCandidateUuid = request.SourceCandidateUuid == Guid.Empty ? null : request.SourceCandidateUuid };
        await ApplyAsync(row, request, caller, ct);
        // Number taken after validation: a refused form does not burn one.
        row.EmployeeNumber = await NextNumberAsync(org, ct);
        Stamp(row, caller, isNew: true);

        await Db.InTransactionAsync(async () =>
        {
            Db.Employees.Add(row);
            await Db.SaveChangesAsync(ct);
            if (request.Employment is { } employment)
            {
                employment.ChangeReason ??= EmploymentChangeReasons.Hire;
                employment.EffectiveFrom ??= row.JoinDate;
                await _records.AddAsync(caller, row, employment, ct);
                if (row.JoinDate is null)
                {
                    row.JoinDate = employment.EffectiveFrom;
                    await Db.SaveChangesAsync(ct);
                }
            }
        }, ct);
        return await MapAsync(caller, row, ct);
    }

    public async Task<EmployeeDetail> UpdateAsync(HrmCaller caller, Guid uuid, EmployeeUpsert request, CancellationToken ct = default)
    {
        var row = await _scope.RequireEmployeeAsync(caller, uuid, ct);
        if (row.Status == EmployeeStatuses.Archived) throw HrmErrors.Conflict("EMPLOYEE_ARCHIVED", "An archived employee cannot be changed.");
        await ApplyAsync(row, request, caller, ct);
        Stamp(row, caller);
        await SaveAsync(ct, "This employee");
        return await MapAsync(caller, row, ct);
    }

    public async Task<EmployeeDetail> ChangeStatusAsync(HrmCaller caller, Guid uuid, EmployeeStatusChange request, CancellationToken ct = default)
    {
        var row = await _scope.RequireEmployeeAsync(caller, uuid, ct);
        var next = HrmValidate.OneOf(request.Status, EmployeeStatuses.All, "status");
        if (next == row.Status) return await MapAsync(caller, row, ct);
        if (!EmployeeStatuses.CanMove(row.Status, next))
            throw HrmErrors.Conflict("EMPLOYEE_STATUS_TRANSITION", $"An employee cannot go from {row.Status} to {next}.");

        if (next == EmployeeStatuses.Terminated)
        {
            var leave = request.EffectiveDate ?? throw HrmErrors.Bad("EMPLOYEE_LEAVE_DATE_REQUIRED", "Give the last working day.");
            if (row.JoinDate is { } join && leave < join) throw HrmErrors.Bad("EMPLOYEE_LEAVE_DATE_REQUIRED", "The last working day cannot be before the join date.");
            row.LeaveDate = leave;
            row.DepartureReason = HrmValidate.OneOf(request.DepartureReason, DepartureReasons.All, "departureReason");
        }
        else if (row.Status == EmployeeStatuses.OnNotice && next == EmployeeStatuses.Active)
        {
            row.LeaveDate = null; // the resignation was withdrawn
            row.DepartureReason = null;
        }
        else if (next == EmployeeStatuses.OnNotice)
        {
            row.LeaveDate = request.EffectiveDate;
            if (!string.IsNullOrWhiteSpace(request.DepartureReason)) row.DepartureReason = HrmValidate.OneOf(request.DepartureReason, DepartureReasons.All, "departureReason");
        }
        row.Status = next;
        Stamp(row, caller);
        await SaveAsync(ct, "This employee");
        return await MapAsync(caller, row, ct);
    }

    public async Task<EmployeeSensitiveModel> RevealAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        caller.Require(PermissionCodes.HRM_EMPLOYEE_SENSITIVE_VIEW);
        var row = await _scope.RequireEmployeeAsync(caller, uuid, ct);
        var model = new EmployeeSensitiveModel
        {
            Cnic = row.CnicEncrypted is null ? null : _sensitive.FormatCnic(_sensitive.Reveal(row.CnicEncrypted)),
            Ntn = row.NtnEncrypted is null ? null : _sensitive.Reveal(row.NtnEncrypted),
        };
        // Logged before the value leaves: no log, no value.
        Log(caller, row.Id, SensitiveFields.Cnic, model.Cnic is not null);
        Log(caller, row.Id, SensitiveFields.Ntn, model.Ntn is not null);
        await Db.SaveChangesAsync(ct);
        return model;
    }

    private void Log(HrmCaller caller, int employeeId, string field, bool present)
    {
        if (!present) return;
        var log = new SensitiveAccessLog { EmployeeId = employeeId, Field = field, Purpose = "API reveal", UserId = caller.UserIdOrNull, UserName = caller.UserName };
        Stamp(log, caller, isNew: true);
        Db.SensitiveAccessLogs.Add(log);
    }

    public async Task<IReadOnlyList<EmployeeDocumentModel>> ListDocumentsAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var e = await _scope.RequireEmployeeAsync(caller, uuid, ct);
        return await Db.EmployeeDocuments.IgnoreQueryFilters().AsNoTracking().Where(d => d.OrganizationId == caller.OrganizationId && d.EmployeeId == e.Id)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new EmployeeDocumentModel { Uuid = d.Uuid, DocumentType = d.DocumentType, Title = d.Title, AttachmentUuid = d.AttachmentUuid, ExpiryDate = d.ExpiryDate, CreatedAt = d.CreatedAt })
            .ToListAsync(ct);
    }

    public async Task<EmployeeDocumentModel> AddDocumentAsync(HrmCaller caller, Guid uuid, EmployeeDocumentCreate request, CancellationToken ct = default)
    {
        var e = await _scope.RequireEmployeeAsync(caller, uuid, ct);
        if (request.AttachmentUuid == Guid.Empty) throw new BadRequestException("Upload the file first: an attachment is required.");
        await _attachments.CheckedAsync(caller, request.AttachmentUuid, HrmAttachmentCodes.Employee);
        var row = new EmployeeDocument
        {
            EmployeeId = e.Id, DocumentType = HrmValidate.OneOf(request.DocumentType ?? EmployeeDocumentTypes.Other, EmployeeDocumentTypes.All, "documentType"),
            Title = HrmValidate.Name(request.Title, 200, "Title"), AttachmentUuid = request.AttachmentUuid, ExpiryDate = request.ExpiryDate
        };
        Stamp(row, caller, isNew: true);
        Db.EmployeeDocuments.Add(row);
        await SaveAsync(ct);
        return new EmployeeDocumentModel { Uuid = row.Uuid, DocumentType = row.DocumentType, Title = row.Title, AttachmentUuid = row.AttachmentUuid, ExpiryDate = row.ExpiryDate, CreatedAt = row.CreatedAt };
    }

    public async Task RemoveDocumentAsync(HrmCaller caller, Guid uuid, Guid documentUuid, CancellationToken ct = default)
    {
        var e = await _scope.RequireEmployeeAsync(caller, uuid, ct);
        var row = await Db.EmployeeDocuments.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.OrganizationId == caller.OrganizationId && d.EmployeeId == e.Id && d.Uuid == documentUuid, ct)
            ?? throw new NotFoundException("Employee document", documentUuid);
        Db.EmployeeDocuments.Remove(row);
        await SaveAsync(ct);
    }
}

internal sealed class EmployeeNumberRow
{
    public int Number { get; set; }
    public string Prefix { get; set; } = "";
}

/// <summary>The record writing rules, shared with the employee create (and TAL's convert-to-employee).</summary>
internal interface IEmploymentRecordFactory
{
    /// <summary>Adds a record for <paramref name="employee"/>; closes the previous one. Saves.</summary>
    Task<EmploymentRecord> AddAsync(HrmCaller caller, Employee employee, EmploymentRecordCreate request, CancellationToken ct = default);
    Task<EmploymentRecordModel?> CurrentModelAsync(Guid organizationId, int employeeId, CancellationToken ct = default);
}

internal sealed class EmploymentRecordService : HrmServiceBase, IEmploymentRecordService, IEmploymentRecordFactory
{
    private readonly IHrmScopeService _scope;

    public EmploymentRecordService(HrmDbContext db, IHrmScopeService scope) : base(db) => _scope = scope;

    private IQueryable<EmploymentRecordModel> Project(Guid org) =>
        Db.EmploymentRecords.IgnoreQueryFilters().AsNoTracking().Where(r => r.OrganizationId == org).Select(r => new EmploymentRecordModel
        {
            Uuid = r.Uuid, EmployeeUuid = r.Employee!.Uuid, EffectiveFrom = r.EffectiveFrom, EffectiveTo = r.EffectiveTo, EmploymentType = r.EmploymentType,
            DepartmentUuid = r.Department!.Uuid, DepartmentName = r.Department.Name, DesignationUuid = r.Designation!.Uuid, DesignationName = r.Designation.Name,
            BranchUuid = r.Branch!.Uuid, BranchName = r.Branch.Name, GradeUuid = r.Grade == null ? null : r.Grade.Uuid, GradeName = r.Grade == null ? null : r.Grade.Name,
            ManagerUuid = r.ManagerEmployee == null ? null : r.ManagerEmployee.Uuid, ManagerName = r.ManagerEmployee == null ? null : r.ManagerEmployee.FirstName + " " + r.ManagerEmployee.LastName,
            ShiftUuid = r.Shift == null ? null : r.Shift.Uuid, ShiftName = r.Shift == null ? null : r.Shift.Name,
            ProbationEndDate = r.ProbationEndDate, ConfirmationDate = r.ConfirmationDate, ContractEndDate = r.ContractEndDate, NoticePeriodDays = r.NoticePeriodDays,
            CostCenterUuid = r.CostCenter == null ? null : r.CostCenter.Uuid, CostCenterCode = r.CostCenterCode, PayrollGroup = r.PayrollGroup, ChangeReason = r.ChangeReason, Notes = r.Notes,
            CostSplits = r.CostSplits.Select(s => new CostSplitModel { CostCenterUuid = s.CostCenter!.Uuid, CostCenterCode = s.CostCenter.Code, Percent = s.Percent }).ToList()
        });

    public async Task<IReadOnlyList<EmploymentRecordModel>> ListAsync(HrmCaller caller, Guid employeeUuid, CancellationToken ct = default)
    {
        await _scope.RequireEmployeeAsync(caller, employeeUuid, ct);
        return await Project(caller.OrganizationId).Where(r => r.EmployeeUuid == employeeUuid).OrderByDescending(r => r.EffectiveFrom).ToListAsync(ct);
    }

    public async Task<EmploymentRecordModel?> GetCurrentAsync(HrmCaller caller, Guid employeeUuid, CancellationToken ct = default)
    {
        var e = await _scope.RequireEmployeeAsync(caller, employeeUuid, ct);
        return await CurrentModelAsync(caller.OrganizationId, e.Id, ct);
    }

    public async Task<EmploymentRecordModel?> CurrentModelAsync(Guid organizationId, int employeeId, CancellationToken ct = default)
    {
        var today = HrmValidate.Today;
        var uuid = await Db.EmploymentRecords.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.OrganizationId == organizationId && r.EmployeeId == employeeId && r.EffectiveFrom <= today && (r.EffectiveTo == null || r.EffectiveTo >= today))
            .Select(r => (Guid?)r.Uuid).FirstOrDefaultAsync(ct);
        return uuid is null ? null : await Project(organizationId).FirstOrDefaultAsync(r => r.Uuid == uuid, ct);
    }

    private async Task ApplyAsync(EmploymentRecord row, EmploymentRecordCreate r, Employee employee, Guid org, CancellationToken ct)
    {
        row.EmploymentType = HrmValidate.OneOf(r.EmploymentType ?? EmploymentTypes.Permanent, EmploymentTypes.All, "employmentType");
        row.DepartmentId = await RequireActiveAsync(Db.Departments, org, r.DepartmentUuid, "Department", d => d.IsActive, ct);
        row.DesignationId = await RequireActiveAsync(Db.Designations, org, r.DesignationUuid, "Designation", d => d.IsActive, ct);
        row.BranchId = await RequireActiveAsync(Db.Branches, org, r.BranchUuid, "Branch", d => d.IsActive, ct);
        row.GradeId = await OptionalIdAsync(Db.Grades, org, r.GradeUuid, "Grade", ct);
        row.ShiftId = await OptionalIdAsync(Db.Shifts, org, r.ShiftUuid, "Shift", ct);
        row.ProbationEndDate = r.ProbationEndDate;
        row.ConfirmationDate = r.ConfirmationDate;
        row.ContractEndDate = r.ContractEndDate;
        if (r.NoticePeriodDays < 0) throw new BadRequestException("The notice period cannot be negative.");
        row.NoticePeriodDays = r.NoticePeriodDays ?? 0;
        row.PayrollGroup = HrmValidate.Optional(r.PayrollGroup, 30, "Payroll group");
        row.Notes = HrmValidate.Optional(r.Notes, 500, "Notes");

        if (r.CostCenterUuid is { } cc && cc != Guid.Empty)
        {
            var center = await Db.CostCenters.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(c => c.OrganizationId == org && c.Uuid == cc, ct)
                ?? throw new NotFoundException("Cost center", cc);
            if (!center.IsActive) throw HrmErrors.Bad("COST_CENTER_INACTIVE", $"Cost center {center.Code} is inactive.");
            row.CostCenterId = center.Id;
            row.CostCenterCode = center.Code;
        }
        else
        {
            row.CostCenterId = null;
            row.CostCenterCode = null;
        }

        row.ManagerEmployeeId = null;
        if (r.ManagerUuid is { } manager && manager != Guid.Empty)
        {
            var managerId = await Db.Employees.IgnoreQueryFilters().Where(e => e.OrganizationId == org && e.Uuid == manager && !e.IsDeleted).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("Employee", manager);
            await CheckNoManagerCycleAsync(org, employee.Id, managerId, ct);
            row.ManagerEmployeeId = managerId;
        }
    }

    private static async Task<int> RequireActiveAsyncImpl<T>(IQueryable<T> set, Guid org, Guid? uuid, string thing, Func<T, bool> isActive, CancellationToken ct) where T : HrmEntity
    {
        if (uuid is null || uuid == Guid.Empty) throw HrmErrors.Bad("EMPLOYMENT_FIELD_REQUIRED", $"{thing} is required.");
        var row = await set.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(x => x.OrganizationId == org && x.Uuid == uuid, ct)
            ?? throw new NotFoundException(thing, uuid);
        if (!isActive(row)) throw HrmErrors.Bad("EMPLOYMENT_FIELD_INACTIVE", $"{thing} {uuid} is inactive.");
        return row.Id;
    }

    private static Task<int> RequireActiveAsync<T>(IQueryable<T> set, Guid org, Guid? uuid, string thing, Func<T, bool> isActive, CancellationToken ct) where T : HrmEntity =>
        RequireActiveAsyncImpl(set, org, uuid, thing, isActive, ct);

    /// <summary>§5.5 — "no cycles": the proposed manager must not report to this employee, directly or not.</summary>
    private async Task CheckNoManagerCycleAsync(Guid org, int employeeId, int managerId, CancellationToken ct)
    {
        if (managerId == employeeId) throw HrmErrors.Bad("EMPLOYMENT_MANAGER_CYCLE", "An employee cannot be their own manager.");
        var today = HrmValidate.Today;
        var lines = await Db.EmploymentRecords.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.OrganizationId == org && (r.EffectiveTo == null || r.EffectiveTo >= today))
            .Select(r => new { r.EmployeeId, r.ManagerEmployeeId }).ToListAsync(ct);
        var seen = new HashSet<int>();
        var at = (int?)managerId;
        while (at is { } id && seen.Add(id))
        {
            if (id == employeeId) throw HrmErrors.Bad("EMPLOYMENT_MANAGER_CYCLE", "The manager already reports to this employee.");
            at = lines.Where(l => l.EmployeeId == id).Select(l => l.ManagerEmployeeId).FirstOrDefault();
        }
    }

    private async Task ReplaceSplitsAsync(HrmCaller caller, EmploymentRecord row, List<CostSplitModel>? splits, CancellationToken ct)
    {
        if (splits is null) return;
        var org = caller.OrganizationId;
        if (splits.Count > 0)
        {
            if (Math.Abs(splits.Sum(s => s.Percent) - 100m) > 0.0001m) throw HrmErrors.Bad("COST_SPLIT_TOTAL", "Cost center percentages must add up to 100.");
            if (splits.Any(s => s.Percent <= 0)) throw HrmErrors.Bad("COST_SPLIT_TOTAL", "A cost center percentage must be above zero.");
            if (splits.Select(s => s.CostCenterUuid).Distinct().Count() != splits.Count) throw HrmErrors.Bad("COST_SPLIT_TOTAL", "A cost center can appear only once.");
        }
        var existing = await Db.EmploymentCostSplits.IgnoreQueryFilters().Where(s => s.EmploymentRecordId == row.Id).ToListAsync(ct);
        Db.EmploymentCostSplits.RemoveRange(existing);
        foreach (var split in splits)
        {
            var id = await OptionalIdAsync(Db.CostCenters, org, split.CostCenterUuid, "Cost center", ct) ?? throw new BadRequestException("A cost center is required on each split.");
            var entity = new EmploymentCostSplit { EmploymentRecordId = row.Id, CostCenterId = id, Percent = split.Percent };
            Stamp(entity, caller, isNew: true);
            Db.EmploymentCostSplits.Add(entity);
        }
        await Db.SaveChangesAsync(ct);
    }

    public async Task<EmploymentRecord> AddAsync(HrmCaller caller, Employee employee, EmploymentRecordCreate request, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var from = request.EffectiveFrom ?? throw HrmErrors.Bad("EMPLOYMENT_FIELD_REQUIRED", "The effective date is required.");
        var reason = HrmValidate.OneOf(request.ChangeReason ?? EmploymentChangeReasons.Other, EmploymentChangeReasons.All, "changeReason");

        var latest = await Db.EmploymentRecords.IgnoreQueryFilters()
            .Where(r => r.OrganizationId == org && r.EmployeeId == employee.Id).OrderByDescending(r => r.EffectiveFrom).FirstOrDefaultAsync(ct);
        if (latest is not null)
        {
            if (from <= latest.EffectiveFrom)
                throw HrmErrors.Conflict("EMPLOYMENT_OVERLAP", $"A new record must start after the latest one ({latest.EffectiveFrom:yyyy-MM-dd}).");
            if (latest.EffectiveTo is { } to && from <= to)
                throw HrmErrors.Conflict("EMPLOYMENT_OVERLAP", $"The latest record runs until {to:yyyy-MM-dd}.");
        }

        var row = new EmploymentRecord { EmployeeId = employee.Id, EffectiveFrom = from, ChangeReason = reason };
        await ApplyAsync(row, request, employee, org, ct);
        Stamp(row, caller, isNew: true);

        await Db.InTransactionAsync(async () =>
        {
            if (latest is { EffectiveTo: null })
            {
                latest.EffectiveTo = from.AddDays(-1);
                Stamp(latest, caller);
                await SaveAsync(ct, "This employment record");
            }
            Db.EmploymentRecords.Add(row);
            await SaveAsync(ct);
            await ReplaceSplitsAsync(caller, row, request.CostSplits, ct);
        }, ct);
        return row;
    }

    private async Task<EmploymentRecordModel> ModelAsync(Guid org, Guid uuid, CancellationToken ct) => await Project(org).FirstAsync(r => r.Uuid == uuid, ct);

    public async Task<EmploymentRecordModel> CreateAsync(HrmCaller caller, Guid employeeUuid, EmploymentRecordCreate request, CancellationToken ct = default)
    {
        var employee = await _scope.RequireEmployeeAsync(caller, employeeUuid, ct);
        var row = await AddAsync(caller, employee, request, ct);
        return await ModelAsync(caller.OrganizationId, row.Uuid, ct);
    }

    /// <summary>A follow-on record: starts from the latest one's terms.</summary>
    private async Task<EmploymentRecordCreate> CarryOverAsync(Guid org, int employeeId, CancellationToken ct)
    {
        var latestUuid = await Db.EmploymentRecords.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.OrganizationId == org && r.EmployeeId == employeeId).OrderByDescending(r => r.EffectiveFrom).Select(r => (Guid?)r.Uuid).FirstOrDefaultAsync(ct)
            ?? throw HrmErrors.Conflict("EMPLOYMENT_NONE", "This employee has no employment record yet.");
        var latest = await ModelAsync(org, latestUuid, ct);
        return new EmploymentRecordCreate
        {
            EmploymentType = latest.EmploymentType, DepartmentUuid = latest.DepartmentUuid, DesignationUuid = latest.DesignationUuid, BranchUuid = latest.BranchUuid,
            GradeUuid = latest.GradeUuid, ManagerUuid = latest.ManagerUuid, ShiftUuid = latest.ShiftUuid, ProbationEndDate = latest.ProbationEndDate,
            ConfirmationDate = latest.ConfirmationDate, ContractEndDate = latest.ContractEndDate, NoticePeriodDays = latest.NoticePeriodDays,
            CostCenterUuid = latest.CostCenterUuid, PayrollGroup = latest.PayrollGroup,
            CostSplits = latest.CostSplits.Select(s => new CostSplitModel { CostCenterUuid = s.CostCenterUuid, Percent = s.Percent }).ToList()
        };
    }

    public async Task<EmploymentRecordModel> TransferAsync(HrmCaller caller, Guid employeeUuid, EmploymentTransferRequest request, CancellationToken ct = default)
    {
        var employee = await _scope.RequireEmployeeAsync(caller, employeeUuid, ct);
        var next = await CarryOverAsync(caller.OrganizationId, employee.Id, ct);
        if (request.DepartmentUuid is null && request.BranchUuid is null && request.ManagerUuid is null && request.CostCenterUuid is null)
            throw HrmErrors.Bad("EMPLOYMENT_TRANSFER_EMPTY", "A transfer changes the department, branch, manager or cost center.");
        next.EffectiveFrom = request.EffectiveFrom;
        next.DepartmentUuid = request.DepartmentUuid ?? next.DepartmentUuid;
        next.BranchUuid = request.BranchUuid ?? next.BranchUuid;
        next.ManagerUuid = request.ManagerUuid ?? next.ManagerUuid;
        next.CostCenterUuid = request.CostCenterUuid ?? next.CostCenterUuid;
        next.Notes = request.Notes;
        next.ChangeReason = EmploymentChangeReasons.Transfer;
        var row = await AddAsync(caller, employee, next, ct);
        return await ModelAsync(caller.OrganizationId, row.Uuid, ct);
    }

    public async Task<EmploymentRecordModel> PromoteAsync(HrmCaller caller, Guid employeeUuid, EmploymentPromotionRequest request, CancellationToken ct = default)
    {
        var employee = await _scope.RequireEmployeeAsync(caller, employeeUuid, ct);
        var next = await CarryOverAsync(caller.OrganizationId, employee.Id, ct);
        if (request.DesignationUuid is null && request.GradeUuid is null)
            throw HrmErrors.Bad("EMPLOYMENT_PROMOTION_EMPTY", "A promotion changes the designation or grade.");
        next.EffectiveFrom = request.EffectiveFrom;
        next.DesignationUuid = request.DesignationUuid ?? next.DesignationUuid;
        next.GradeUuid = request.GradeUuid ?? next.GradeUuid;
        next.ManagerUuid = request.ManagerUuid ?? next.ManagerUuid;
        next.Notes = request.Notes;
        next.ChangeReason = EmploymentChangeReasons.Promotion;
        var row = await AddAsync(caller, employee, next, ct);
        return await ModelAsync(caller.OrganizationId, row.Uuid, ct);
    }

    public async Task<EmploymentRecordModel> CorrectAsync(HrmCaller caller, Guid employeeUuid, Guid recordUuid, EmploymentRecordCreate request, CancellationToken ct = default)
    {
        var employee = await _scope.RequireEmployeeAsync(caller, employeeUuid, ct);
        var org = caller.OrganizationId;
        var row = await Db.EmploymentRecords.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.OrganizationId == org && r.EmployeeId == employee.Id && r.Uuid == recordUuid, ct)
            ?? throw new NotFoundException("Employment record", recordUuid);
        var latest = await Db.EmploymentRecords.IgnoreQueryFilters().Where(r => r.OrganizationId == org && r.EmployeeId == employee.Id).MaxAsync(r => r.EffectiveFrom, ct);
        if (row.EffectiveFrom != latest) throw HrmErrors.Conflict("EMPLOYMENT_NOT_LATEST", "Only the latest record can be corrected: history stays as it was.");

        if (request.EffectiveFrom is { } from && from != row.EffectiveFrom)
        {
            var previous = await Db.EmploymentRecords.IgnoreQueryFilters()
                .Where(r => r.OrganizationId == org && r.EmployeeId == employee.Id && r.Id != row.Id).OrderByDescending(r => r.EffectiveFrom).FirstOrDefaultAsync(ct);
            if (previous is not null)
            {
                if (from <= previous.EffectiveFrom) throw HrmErrors.Conflict("EMPLOYMENT_OVERLAP", $"The record must start after the previous one ({previous.EffectiveFrom:yyyy-MM-dd}).");
                previous.EffectiveTo = from.AddDays(-1);
                Stamp(previous, caller);
            }
            row.EffectiveFrom = from;
        }
        await ApplyAsync(row, request, employee, org, ct);
        if (request.ChangeReason is not null) row.ChangeReason = HrmValidate.OneOf(request.ChangeReason, EmploymentChangeReasons.All, "changeReason");
        Stamp(row, caller);
        await Db.InTransactionAsync(async () =>
        {
            await SaveAsync(ct, "This employment record");
            await ReplaceSplitsAsync(caller, row, request.CostSplits, ct);
        }, ct);
        return await ModelAsync(org, row.Uuid, ct);
    }
}

internal sealed class EmployeeBankAccountService : HrmServiceBase, IEmployeeBankAccountService
{
    private readonly IHrmScopeService _scope;
    private readonly IHrmSensitiveData _sensitive;

    public EmployeeBankAccountService(HrmDbContext db, IHrmScopeService scope, IHrmSensitiveData sensitive) : base(db)
    {
        _scope = scope;
        _sensitive = sensitive;
    }

    private EmployeeBankAccountModel Map(EmployeeBankAccount a, Guid employeeUuid) => new()
    {
        Uuid = a.Uuid, EmployeeUuid = employeeUuid, BankName = a.BankName, BranchName = a.BranchName, AccountTitle = a.AccountTitle,
        AccountNumberMasked = _sensitive.MaskTail(a.AccountNumberLast4), IbanMasked = _sensitive.MaskTail(a.IbanLast4),
        IsPrimary = a.IsPrimary, IsActive = a.IsActive, PaymentMethod = a.PaymentMethod
    };

    /// <summary>Own accounts always; someone else's need a bank code.</summary>
    private async Task<Employee> RequireAsync(HrmCaller caller, Guid employeeUuid, CancellationToken ct)
    {
        var e = await _scope.RequireEmployeeAsync(caller, employeeUuid, ct);
        return e;
    }

    public async Task<IReadOnlyList<EmployeeBankAccountModel>> ListAsync(HrmCaller caller, Guid employeeUuid, CancellationToken ct = default)
    {
        var e = await RequireAsync(caller, employeeUuid, ct);
        var rows = await Db.EmployeeBankAccounts.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.OrganizationId == caller.OrganizationId && a.EmployeeId == e.Id).OrderByDescending(a => a.IsPrimary).ThenBy(a => a.BankName).ToListAsync(ct);
        return rows.Select(a => Map(a, employeeUuid)).ToList();
    }

    private void Apply(EmployeeBankAccount row, EmployeeBankAccountUpsert r, Guid org)
    {
        row.BankName = HrmValidate.Name(r.BankName, 100, "Bank name");
        row.BranchName = HrmValidate.Optional(r.BranchName, 100, "Branch name");
        row.AccountTitle = HrmValidate.Optional(r.AccountTitle, 200, "Account title");
        row.PaymentMethod = HrmValidate.OneOf(r.PaymentMethod ?? row.PaymentMethod, BankPaymentMethods.All, "paymentMethod");
        if (r.AccountNumber is not null)
        {
            var value = r.AccountNumber.Trim();
            if (value.Length > 50) throw new BadRequestException("An account number cannot be longer than 50 characters.");
            var p = value.Length == 0 ? null : _sensitive.Protect(org, value, 4);
            row.AccountNumberEncrypted = p?.Cipher;
            row.AccountNumberLast4 = p?.Tail;
        }
        if (r.Iban is not null)
        {
            var value = r.Iban.Replace(" ", "").Trim().ToUpperInvariant();
            if (value.Length > 34) throw new BadRequestException("An IBAN cannot be longer than 34 characters.");
            var p = value.Length == 0 ? null : _sensitive.Protect(org, value, 4);
            row.IbanEncrypted = p?.Cipher;
            row.IbanLast4 = p?.Tail;
        }
        if (row.PaymentMethod == BankPaymentMethods.Bank && row.AccountNumberEncrypted is null && row.IbanEncrypted is null)
            throw HrmErrors.Bad("BANK_ACCOUNT_NUMBER_REQUIRED", "A bank payment needs an account number or an IBAN.");
    }

    public async Task<EmployeeBankAccountModel> CreateAsync(HrmCaller caller, Guid employeeUuid, EmployeeBankAccountUpsert request, CancellationToken ct = default)
    {
        var e = await RequireAsync(caller, employeeUuid, ct);
        var org = caller.OrganizationId;
        var row = new EmployeeBankAccount { EmployeeId = e.Id };
        Apply(row, request, org);
        var current = await Db.EmployeeBankAccounts.IgnoreQueryFilters().Where(a => a.OrganizationId == org && a.EmployeeId == e.Id && a.IsPrimary && a.IsActive).FirstOrDefaultAsync(ct);
        // The first account is the primary; a later one only when asked.
        row.IsPrimary = current is null || request.IsPrimary == true;
        Stamp(row, caller, isNew: true);
        await Db.InTransactionAsync(async () =>
        {
            if (current is not null && row.IsPrimary)
            {
                current.IsPrimary = false;
                Stamp(current, caller);
                await SaveAsync(ct);
            }
            Db.EmployeeBankAccounts.Add(row);
            await SaveAsync(ct);
        }, ct);
        return Map(row, employeeUuid);
    }

    private async Task<EmployeeBankAccount> FindAsync(Guid org, int employeeId, Guid uuid, CancellationToken ct) =>
        await Db.EmployeeBankAccounts.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.OrganizationId == org && a.EmployeeId == employeeId && a.Uuid == uuid, ct)
        ?? throw new NotFoundException("Bank account", uuid);

    public async Task<EmployeeBankAccountModel> UpdateAsync(HrmCaller caller, Guid employeeUuid, Guid accountUuid, EmployeeBankAccountUpsert request, CancellationToken ct = default)
    {
        var e = await RequireAsync(caller, employeeUuid, ct);
        var row = await FindAsync(caller.OrganizationId, e.Id, accountUuid, ct);
        Apply(row, request, caller.OrganizationId);
        Stamp(row, caller);
        await SaveAsync(ct, "This bank account");
        if (request.IsPrimary == true && !row.IsPrimary) return await SetPrimaryAsync(caller, employeeUuid, accountUuid, ct);
        return Map(row, employeeUuid);
    }

    public async Task<EmployeeBankAccountModel> SetPrimaryAsync(HrmCaller caller, Guid employeeUuid, Guid accountUuid, CancellationToken ct = default)
    {
        var e = await RequireAsync(caller, employeeUuid, ct);
        var org = caller.OrganizationId;
        var row = await FindAsync(org, e.Id, accountUuid, ct);
        if (!row.IsActive) throw HrmErrors.Conflict("BANK_ACCOUNT_INACTIVE", "An inactive account cannot be the primary one.");
        if (row.IsPrimary) return Map(row, employeeUuid);
        var others = await Db.EmployeeBankAccounts.IgnoreQueryFilters().Where(a => a.OrganizationId == org && a.EmployeeId == e.Id && a.IsPrimary && a.Id != row.Id).ToListAsync(ct);
        // Two saves, in this order, or the filtered unique index refuses the second primary.
        await Db.InTransactionAsync(async () =>
        {
            foreach (var other in others)
            {
                other.IsPrimary = false;
                Stamp(other, caller);
            }
            await SaveAsync(ct, "This bank account");
            row.IsPrimary = true;
            Stamp(row, caller);
            await SaveAsync(ct, "This bank account");
        }, ct);
        return Map(row, employeeUuid);
    }

    public async Task<EmployeeBankAccountModel> DeactivateAsync(HrmCaller caller, Guid employeeUuid, Guid accountUuid, CancellationToken ct = default)
    {
        var e = await RequireAsync(caller, employeeUuid, ct);
        var row = await FindAsync(caller.OrganizationId, e.Id, accountUuid, ct);
        row.IsActive = false;
        row.IsPrimary = false;
        Stamp(row, caller);
        await SaveAsync(ct, "This bank account");
        return Map(row, employeeUuid);
    }

    public async Task<EmployeeBankSensitiveModel> RevealAsync(HrmCaller caller, Guid employeeUuid, Guid accountUuid, CancellationToken ct = default)
    {
        caller.Require(PermissionCodes.HRM_BANK_VIEW);
        var e = await RequireAsync(caller, employeeUuid, ct);
        var row = await FindAsync(caller.OrganizationId, e.Id, accountUuid, ct);
        var model = new EmployeeBankSensitiveModel
        {
            AccountNumber = row.AccountNumberEncrypted is null ? null : _sensitive.Reveal(row.AccountNumberEncrypted),
            Iban = row.IbanEncrypted is null ? null : _sensitive.Reveal(row.IbanEncrypted),
        };
        void Log(string field, bool present)
        {
            if (!present) return;
            var log = new SensitiveAccessLog { EmployeeId = e.Id, Field = field, Purpose = "API reveal", UserId = caller.UserIdOrNull, UserName = caller.UserName };
            Stamp(log, caller, isNew: true);
            Db.SensitiveAccessLogs.Add(log);
        }
        Log(SensitiveFields.BankAccount, model.AccountNumber is not null);
        Log(SensitiveFields.Iban, model.Iban is not null);
        await Db.SaveChangesAsync(ct);
        return model;
    }
}

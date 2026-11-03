using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Security;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Hrm.Services.Post;

/// <summary>
/// E-14 (A-15, FD-8, FSD Appendix A): an Odoo employee export → hr employees, through the employee services so every rule holds (CNIC encrypted
/// and unique, hire record, bank details). New people only (D-POST-5): a CNIC already on file is refused. The Appendix's <c>wage</c> is not
/// imported: a salary needs a structure and an approval, and an S2 value should not arrive by a generic import.
/// </summary>
internal sealed class HrmEmployeeImportTarget : IImportTarget
{
    private const string LookupNotFound = "LOOKUP_NOT_FOUND", Duplicate = "DUPLICATE_IN_DB", Invalid = "INVALID_VALUE", ModuleOff = "MODULE_OFF";

    private static readonly IReadOnlyDictionary<string, string> Genders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["male"] = "M", ["m"] = "M", ["female"] = "F", ["f"] = "F", ["other"] = "Other"
    };

    private static readonly IReadOnlyDictionary<string, string> Maritals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["single"] = "Single", ["married"] = "Married", ["cohabitant"] = "Other", ["widower"] = "Widowed", ["widow"] = "Widowed", ["widowed"] = "Widowed",
        ["divorced"] = "Divorced", ["other"] = "Other"
    };

    private readonly HrmDbContext _db;
    private readonly IEmployeeService _employees;
    private readonly IEmployeeBankAccountService _bank;
    private readonly IHrmSensitiveData _sensitive;
    private readonly IHrmSeeder _seeder;
    private readonly IModuleGate? _gate;
    private readonly HrmUserNames? _names;

    public HrmEmployeeImportTarget(HrmDbContext db, IEmployeeService employees, IEmployeeBankAccountService bank, IHrmSensitiveData sensitive,
        IHrmSeeder seeder, IModuleGate? gate = null, HrmUserNames? names = null)
    {
        _names = names;
        _db = db;
        _employees = employees;
        _bank = bank;
        _sensitive = sensitive;
        _seeder = seeder;
        _gate = gate;
    }

    public ImportProfileDefinition Describe() => new(
        ImportEntityTypes.Employee, "Employees (Odoo CSV)",
        "New employees from an Odoo hr.employee export. Departments, designations and branches must exist first. Salaries are not imported.",
        ImportOrder: 40, IsOpeningBalance: false, MatchFields: ["identification_id"],
        Fields:
        [
            new("name", "Name", "Split on the first space into first and last name; review afterwards.", ImportFieldTypes.Text, true, false, 1, "Sara Khan", MaxLength: 200, Aliases: ["employee_name", "full_name"]),
            new("identification_id", "CNIC", "13 digits; dashes are ignored. Matches a person already on file.", ImportFieldTypes.Text, false, true, 2, "12345-1234567-1", MaxLength: 20, Aliases: ["cnic", "national_id", "ICNUM"]),
            new("birthday", "Date of Birth", null, ImportFieldTypes.Date, false, false, 3, "1990-05-01", Aliases: ["date_of_birth", "dob"], UnambiguousDateOnly: true),
            new("gender", "Gender", "male, female or other.", ImportFieldTypes.Text, false, false, 4, "female", MaxLength: 10),
            new("marital", "Marital Status", "single, married, cohabitant, widower or divorced.", ImportFieldTypes.Text, false, false, 5, "single", MaxLength: 20, Aliases: ["marital_status"]),
            new("work_email", "Work Email", null, ImportFieldTypes.Email, false, false, 6, "sara@example.com", MaxLength: 150),
            new("private_email", "Personal Email", null, ImportFieldTypes.Email, false, false, 7, "sara@mail.example", MaxLength: 150, Aliases: ["personal_email"]),
            new("mobile_phone", "Mobile", null, ImportFieldTypes.Phone, false, false, 8, "+92 300 1234567", MaxLength: 30, Aliases: ["mobile"]),
            new("department_id", "Department", "Code or name of an existing department.", ImportFieldTypes.Lookup, false, false, 9, "OPS", MaxLength: 100, LookupList: "departments", Aliases: ["department"]),
            new("job_id", "Designation", "Code or name of an existing designation.", ImportFieldTypes.Lookup, false, false, 10, "Officer", MaxLength: 100, LookupList: "designations", Aliases: ["job_title", "designation"]),
            new("work_location_id", "Branch", "Code or name of an existing branch.", ImportFieldTypes.Lookup, false, false, 11, "HO", MaxLength: 100, LookupList: "branches", Aliases: ["branch", "work_location"]),
            new("parent_id", "Manager", "Employee number of an existing manager.", ImportFieldTypes.Text, false, false, 12, "EMP-0001", MaxLength: 30, Aliases: ["manager"]),
            new("date_start", "Join Date", "Needed with department, designation and branch: creates the hire record.", ImportFieldTypes.Date, false, false, 13, "2024-01-15", Aliases: ["contract_date_start", "join_date"], UnambiguousDateOnly: true),
            new("date_end", "Contract End", null, ImportFieldTypes.Date, false, false, 14, null, Aliases: ["contract_date_end"], UnambiguousDateOnly: true),
            new("bank_name", "Bank", "With bank_account_id: the primary bank account.", ImportFieldTypes.Text, false, false, 15, "HBL", MaxLength: 100),
            new("bank_account_id", "Bank Account Number", null, ImportFieldTypes.Text, false, false, 16, "0123456789", MaxLength: 50, Aliases: ["bank_account"])
        ],
        AllowUpdate: false, RequiresModule: ModuleCodes.Hrm, RequiredPermissions: RequiredCodes,
        SensitiveFields: ["identification_id", "bank_account_id"],
        ExternalIdHelp: "Optional: the person's id in your previous system. This import only creates: a row whose External ID was imported before is skipped under the default strategy, or refused under ERROR. It never updates the employee.");

    /// <summary>D-POST-5: what the rows write. Employee, hire record, S3 identity, S3 bank. Enforced by the import module at start, validate and run.</summary>
    internal static readonly IReadOnlyList<string> RequiredCodes =
    [
        PermissionCodes.HRM_EMPLOYEE_EDIT, PermissionCodes.HRM_EMPLOYMENT_MANAGE, PermissionCodes.HRM_EMPLOYEE_SENSITIVE_VIEW, PermissionCodes.HRM_BANK_EDIT
    ];

    public async Task<IReadOnlyList<ImportTemplateList>> GetTemplateListsAsync(Guid organizationId, CancellationToken ct = default) =>
    [
        new("departments", await _db.Departments.IgnoreQueryFilters().AsNoTracking().Where(d => d.OrganizationId == organizationId && d.IsActive).OrderBy(d => d.Code).Select(d => d.Code).Take(1000).ToListAsync(ct)),
        new("designations", await _db.Designations.IgnoreQueryFilters().AsNoTracking().Where(d => d.OrganizationId == organizationId && d.IsActive).OrderBy(d => d.Code).Select(d => d.Code).Take(1000).ToListAsync(ct)),
        new("branches", await _db.Branches.IgnoreQueryFilters().AsNoTracking().Where(b => b.OrganizationId == organizationId && b.IsActive).OrderBy(b => b.Code).Select(b => b.Code).Take(1000).ToListAsync(ct))
    ];

    // ── What a row refers to ─────────────────────────────────────────────────

    private sealed record Row(int Number, IReadOnlyDictionary<string, string?> Values)
    {
        public string? Get(string field) => Values.TryGetValue(field, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
    }

    private sealed class Lookups
    {
        public required Dictionary<string, Guid> Departments { get; init; }
        public required Dictionary<string, Guid> Designations { get; init; }
        public required Dictionary<string, Guid> Branches { get; init; }
        public required Dictionary<string, Guid> Employees { get; init; }
        public required HashSet<string> CnicHashes { get; init; }
    }

    private static Dictionary<string, Guid> ByCodeOrName(IEnumerable<(string Code, string Name, Guid Uuid)> rows)
    {
        var map = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows) { map[r.Name] = r.Uuid; }
        foreach (var r in rows) { map[r.Code] = r.Uuid; } // A code wins over another row's identical name.
        return map;
    }

    private async Task<Lookups> LoadAsync(Guid org, CancellationToken ct) => new()
    {
        Departments = ByCodeOrName((await _db.Departments.IgnoreQueryFilters().AsNoTracking().Where(d => d.OrganizationId == org && d.IsActive).Select(d => new { d.Code, d.Name, d.Uuid }).ToListAsync(ct)).Select(d => (d.Code, d.Name, d.Uuid)).ToList()),
        Designations = ByCodeOrName((await _db.Designations.IgnoreQueryFilters().AsNoTracking().Where(d => d.OrganizationId == org && d.IsActive).Select(d => new { d.Code, d.Name, d.Uuid }).ToListAsync(ct)).Select(d => (d.Code, d.Name, d.Uuid)).ToList()),
        Branches = ByCodeOrName((await _db.Branches.IgnoreQueryFilters().AsNoTracking().Where(b => b.OrganizationId == org && b.IsActive).Select(b => new { b.Code, b.Name, b.Uuid }).ToListAsync(ct)).Select(b => (b.Code, b.Name, b.Uuid)).ToList()),
        Employees = (await _db.Employees.IgnoreQueryFilters().AsNoTracking().Where(e => e.OrganizationId == org && !e.IsDeleted).Select(e => new { e.EmployeeNumber, e.Uuid }).ToListAsync(ct))
            .ToDictionary(e => e.EmployeeNumber, e => e.Uuid, StringComparer.OrdinalIgnoreCase),
        CnicHashes = (await _db.Employees.IgnoreQueryFilters().AsNoTracking().Where(e => e.OrganizationId == org && !e.IsDeleted && e.CnicHash != null).Select(e => e.CnicHash!).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal)
    };

    private List<ImportFieldError> Check(Guid org, Row row, Lookups lookups, ISet<string> seenInBatch)
    {
        var errors = new List<ImportFieldError>();
        void Lookup(string field, IReadOnlyDictionary<string, Guid> map, string what)
        {
            if (row.Get(field) is { } v && !map.ContainsKey(v))
                errors.Add(new ImportFieldError(field, LookupNotFound, $"{what} {v} does not exist.", v));
        }

        Lookup("department_id", lookups.Departments, "Department");
        Lookup("job_id", lookups.Designations, "Designation");
        Lookup("work_location_id", lookups.Branches, "Branch");
        Lookup("parent_id", lookups.Employees, "Manager");

        if (row.Get("gender") is { } gender && !Genders.ContainsKey(gender))
            errors.Add(new ImportFieldError("gender", Invalid, "Gender must be male, female or other.", gender));
        if (row.Get("marital") is { } marital && !Maritals.ContainsKey(marital))
            errors.Add(new ImportFieldError("marital", Invalid, "Marital status must be single, married, cohabitant, widower or divorced.", marital));

        var (first, last) = SplitName(row.Get("name"));
        if (first.Length > 0 && last.Length == 0)
            errors.Add(new ImportFieldError("name", "NAME_NEEDS_TWO_WORDS", "An employee needs a first and a last name: this name has one word.", row.Get("name")));
        else if (first.Length > 60 || last.Length > 60)
            errors.Add(new ImportFieldError("name", Invalid, "First and last name can be at most 60 characters each.", null));

        var hire = new[] { "department_id", "job_id", "work_location_id", "date_start" }.Select(row.Get).ToList();
        if (hire.Any(v => v is not null) && hire.Any(v => v is null))
            errors.Add(new ImportFieldError("date_start", "MISSING_DEPENDENCY", "A hire record needs department, designation, branch and join date together.", null));
        else if (hire.All(v => v is null))
            foreach (var (field, label) in new[] { ("parent_id", "Manager"), ("date_end", "Contract end") })
                if (row.Get(field) is not null)
                    errors.Add(new ImportFieldError(field, "MISSING_DEPENDENCY", $"{label} belongs on the hire record, and this row has none: give department, designation, branch and join date, or clear it.", null));

        if (row.Get("bank_account_id") is not null && row.Get("bank_name") is null)
            errors.Add(new ImportFieldError("bank_name", "MISSING_DEPENDENCY", "A bank account needs its bank name.", null));

        if (row.Get("identification_id") is { } raw)
        {
            try
            {
                // Never echoed back: the error stores the field's value, and a CNIC is S3.
                var hash = _sensitive.Hash(org, _sensitive.NormalizeCnic(raw)!);
                if (lookups.CnicHashes.Contains(hash))
                    errors.Add(new ImportFieldError("identification_id", Duplicate, "An employee with this CNIC already exists.", null));
                else if (!seenInBatch.Add(hash))
                    errors.Add(new ImportFieldError("identification_id", Duplicate, "This CNIC appears twice in the file.", null));
            }
            catch (BadRequestException)
            {
                errors.Add(new ImportFieldError("identification_id", Invalid, "A CNIC must have 13 digits.", null));
            }
        }
        return errors;
    }

    // ── IImportTarget ────────────────────────────────────────────────────────

    private async Task<bool> ModuleOnAsync(Guid org, CancellationToken ct) => _gate is null || await _gate.IsEnabledAsync(org, ModuleCodes.Hrm, ct);

    public async Task<IReadOnlyList<ImportRowCheck>> ValidateAsync(Guid organizationId, IReadOnlyList<ImportRowInput> rows, CancellationToken ct = default)
    {
        if (!await ModuleOnAsync(organizationId, ct))
            return rows.Select(r => new ImportRowCheck(r.RowNumber, null, [new ImportFieldError("name", ModuleOff, "Human Resources is not switched on for this organization.", null)])).ToList();

        await _seeder.EnsureAsync(organizationId, ct);
        var lookups = await LoadAsync(organizationId, ct);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return rows.Select(r => new ImportRowCheck(r.RowNumber, null, Check(organizationId, new Row(r.RowNumber, r.Values), lookups, seen))).ToList();
    }

    public async Task<IReadOnlyList<ImportRowOutcome>> ApplyAsync(Guid organizationId, int userId, IReadOnlyList<ImportApplyRow> rows, CancellationToken ct = default)
    {
        if (!await ModuleOnAsync(organizationId, ct)) throw new ForbiddenException("Human Resources is not switched on for this organization.");
        await _seeder.EnsureAsync(organizationId, ct);

        // The import module has checked RequiredCodes against the person (at start, validate and run); this caller is limited to what a row does.
        var caller = new HrmCaller(organizationId, userId,
            [PermissionCodes.HRM_EMPLOYEE_EDIT, PermissionCodes.HRM_EMPLOYMENT_MANAGE, PermissionCodes.HRM_BANK_EDIT, PermissionCodes.HRM_SCOPE_ALL],
            _names is null ? null : await _names.NameAsync(organizationId, userId, ct: ct));

        var outcomes = new List<ImportRowOutcome>(rows.Count);
        await _db.InTransactionAsync(async () =>
        {
            outcomes.Clear();
            var lookups = await LoadAsync(organizationId, ct);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var input in rows)
            {
                var row = new Row(input.RowNumber, input.Values);
                var errors = Check(organizationId, row, lookups, seen);
                if (errors.Count > 0) throw new BadRequestException($"Row {input.RowNumber}: {errors[0].Message}");

                try
                {
                    var employee = await _employees.CreateAsync(caller, ToUpsert(row, lookups), ct);
                    if (row.Get("bank_account_id") is { } account)
                        await _bank.CreateAsync(caller, employee.Uuid, new EmployeeBankAccountUpsert { BankName = row.Get("bank_name"), AccountNumber = account, IsPrimary = true }, ct);
                    outcomes.Add(new ImportRowOutcome(input.RowNumber, "IMPORTED", employee.Uuid));
                }
                catch (Exception ex) when (ex is BadRequestException or ConflictException or NotFoundException)
                {
                    // The importer marks "Row n:" as the failing row and the rest of the batch as rolled back because of it.
                    throw new BadRequestException($"Row {input.RowNumber}: {ex.Message}");
                }
            }
        }, ct);
        return outcomes;
    }

    private static (string First, string Last) SplitName(string? name)
    {
        name ??= string.Empty;
        var space = name.IndexOf(' ');
        return space > 0 ? (name[..space], name[(space + 1)..].Trim()) : (name, string.Empty);
    }

    private static EmployeeUpsert ToUpsert(Row row, Lookups lookups)
    {
        var (first, last) = SplitName(row.Get("name"));

        var upsert = new EmployeeUpsert
        {
            FirstName = first, LastName = last, Cnic = row.Get("identification_id"),
            DateOfBirth = Date(row.Get("birthday")),
            Gender = row.Get("gender") is { } g ? Genders[g] : null,
            MaritalStatus = row.Get("marital") is { } m ? Maritals[m] : null,
            WorkEmail = row.Get("work_email"), PersonalEmail = row.Get("private_email"), Mobile = row.Get("mobile_phone"),
            JoinDate = Date(row.Get("date_start")), Status = EmployeeStatuses.Active
        };
        if (row.Get("department_id") is { } department)
            upsert.Employment = new EmploymentRecordCreate
            {
                EffectiveFrom = Date(row.Get("date_start")),
                DepartmentUuid = lookups.Departments[department],
                DesignationUuid = lookups.Designations[row.Get("job_id")!],
                BranchUuid = lookups.Branches[row.Get("work_location_id")!],
                ManagerUuid = row.Get("parent_id") is { } manager ? lookups.Employees[manager] : null,
                ContractEndDate = Date(row.Get("date_end"))
            };
        return upsert;
    }

    private static DateOnly? Date(string? value) => value is null ? null : DateOnly.ParseExact(value, "yyyy-MM-dd");
}

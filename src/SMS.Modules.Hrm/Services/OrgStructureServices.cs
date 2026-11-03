using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Security;
using SMS.Shared.Authorization;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Hrm.Services;

// API-CONTRACT §2 — organization structure. Every query filters on the caller's organization explicitly. Another organization's
// uuid is 404. "In use" = an employment record in force or in the future.

public interface IBranchService
{
    Task<IReadOnlyList<BranchModel>> ListAsync(HrmCaller caller, bool includeInactive, CancellationToken ct = default);
    Task<BranchModel> GetAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<BranchModel> CreateAsync(HrmCaller caller, BranchUpsert request, CancellationToken ct = default);
    Task<BranchModel> UpdateAsync(HrmCaller caller, Guid uuid, BranchUpsert request, CancellationToken ct = default);
    Task<BranchModel> SetActiveAsync(HrmCaller caller, Guid uuid, bool isActive, CancellationToken ct = default);
}

public interface ICostCenterService
{
    Task<IReadOnlyList<CostCenterModel>> ListAsync(HrmCaller caller, bool includeInactive, CancellationToken ct = default);
    Task<CostCenterModel> CreateAsync(HrmCaller caller, CostCenterUpsert request, CancellationToken ct = default);
    Task<CostCenterModel> UpdateAsync(HrmCaller caller, Guid uuid, CostCenterUpsert request, CancellationToken ct = default);
    Task<CostCenterModel> SetActiveAsync(HrmCaller caller, Guid uuid, bool isActive, CancellationToken ct = default);
}

public interface IDepartmentService
{
    Task<IReadOnlyList<DepartmentModel>> ListAsync(HrmCaller caller, bool includeInactive, CancellationToken ct = default);
    /// <summary>Task 017 — roots first, children by name; includes inactive when asked.</summary>
    Task<IReadOnlyList<DepartmentTreeNode>> GetTreeAsync(HrmCaller caller, bool includeInactive, CancellationToken ct = default);
    Task<DepartmentModel> CreateAsync(HrmCaller caller, DepartmentUpsert request, CancellationToken ct = default);
    Task<DepartmentModel> UpdateAsync(HrmCaller caller, Guid uuid, DepartmentUpsert request, CancellationToken ct = default);
    Task<DepartmentModel> SetActiveAsync(HrmCaller caller, Guid uuid, bool isActive, CancellationToken ct = default);
}

public interface IDesignationService
{
    Task<IReadOnlyList<DesignationModel>> ListAsync(HrmCaller caller, bool includeInactive, CancellationToken ct = default);
    Task<DesignationModel> CreateAsync(HrmCaller caller, DesignationUpsert request, CancellationToken ct = default);
    Task<DesignationModel> UpdateAsync(HrmCaller caller, Guid uuid, DesignationUpsert request, CancellationToken ct = default);
    Task<DesignationModel> SetActiveAsync(HrmCaller caller, Guid uuid, bool isActive, CancellationToken ct = default);
}

public interface IGradeService
{
    Task<IReadOnlyList<GradeModel>> ListAsync(HrmCaller caller, bool includeInactive, CancellationToken ct = default);
    Task<GradeModel> CreateAsync(HrmCaller caller, GradeUpsert request, CancellationToken ct = default);
    Task<GradeModel> UpdateAsync(HrmCaller caller, Guid uuid, GradeUpsert request, CancellationToken ct = default);
    Task<GradeModel> SetActiveAsync(HrmCaller caller, Guid uuid, bool isActive, CancellationToken ct = default);
}

public interface IHrmSettingsService
{
    Task<HrmSettingsModel> GetAsync(HrmCaller caller, CancellationToken ct = default);
    Task<HrmSettingsModel> UpdateAsync(HrmCaller caller, HrmSettingsUpdate request, CancellationToken ct = default);
}

public interface IHrUserScopeService
{
    Task<IReadOnlyList<HrUserScopeModel>> ListAsync(HrmCaller caller, int? userId, CancellationToken ct = default);
    /// <summary>FE-4 — active users of the organization for the picker (no user-admin code needed).</summary>
    Task<IReadOnlyList<HrScopeUserModel>> SearchUsersAsync(HrmCaller caller, string? search, CancellationToken ct = default);
    Task<HrUserScopeModel> AddAsync(HrmCaller caller, HrUserScopeCreate request, CancellationToken ct = default);
    Task RemoveAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
}

internal static class HrmValidate
{
    public static string Code(string? value, int max = 20)
    {
        var code = value?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(code)) throw new BadRequestException("Code is required.");
        if (code.Length > max) throw new BadRequestException($"Code cannot be longer than {max} characters.");
        return code;
    }

    public static string Name(string? value, int max = 100, string label = "Name")
    {
        var name = value?.Trim();
        if (string.IsNullOrEmpty(name)) throw new BadRequestException($"{label} is required.");
        if (name.Length > max) throw new BadRequestException($"{label} cannot be longer than {max} characters.");
        return name;
    }

    public static string? Optional(string? value, int max, string label)
    {
        var text = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (text is { Length: var n } && n > max) throw new BadRequestException($"{label} cannot be longer than {max} characters.");
        return text;
    }

    public static string OneOf(string? value, IReadOnlyList<string> allowed, string field)
    {
        var match = allowed.FirstOrDefault(a => string.Equals(a, value?.Trim(), StringComparison.OrdinalIgnoreCase));
        return match ?? throw new BadRequestException($"Unknown {field} '{value}'. Allowed: {string.Join(", ", allowed)}.");
    }

    public static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);
}

internal abstract class HrmServiceBase
{
    protected readonly HrmDbContext Db;
    protected HrmServiceBase(HrmDbContext db) => Db = db;

    protected static void Stamp(HrmEntity row, HrmCaller caller, bool isNew = false)
    {
        if (isNew)
        {
            row.OrganizationId = caller.OrganizationId;
            row.CreatedBy = caller.UserIdOrNull;
            row.CreatedByName = caller.UserName;
        }
        row.ModifiedBy = caller.UserIdOrNull;
        row.ModifiedByName = caller.UserName;
    }

    protected async Task SaveAsync(CancellationToken ct, string conflictThing = "This record")
    {
        try
        {
            await Db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException($"{conflictThing} was changed by someone else; reload and try again.");
        }
    }

    /// <summary>A unique-index violation (SQL 2601/2627) raced past the service check: answered like the check.</summary>
    protected static bool IsDuplicate(DbUpdateException ex) =>
        ex.InnerException?.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true;

    protected IQueryable<EmploymentRecord> LiveRecords(Guid org)
    {
        var today = HrmValidate.Today;
        return Db.EmploymentRecords.IgnoreQueryFilters().Where(r => r.OrganizationId == org && !r.Employee!.IsDeleted
            && (r.EffectiveTo == null || r.EffectiveTo >= today)
            && r.Employee.Status != EmployeeStatuses.Terminated && r.Employee.Status != EmployeeStatuses.Archived);
    }

    protected async Task<int?> OptionalIdAsync<T>(IQueryable<T> set, Guid org, Guid? uuid, string thing, CancellationToken ct) where T : HrmEntity
    {
        if (uuid is null || uuid == Guid.Empty) return null;
        var id = await set.IgnoreQueryFilters().Where(x => x.OrganizationId == org && x.Uuid == uuid).Select(x => (int?)x.Id).FirstOrDefaultAsync(ct);
        return id ?? throw new NotFoundException(thing, uuid);
    }
}

internal sealed class BranchService : HrmServiceBase, IBranchService
{
    public BranchService(HrmDbContext db) : base(db) { }

    private static BranchModel Map(Branch b) => new()
    {
        Uuid = b.Uuid, Code = b.Code, Name = b.Name, Address = b.Address, City = b.City, WarehouseUuid = b.WarehouseUuid, IsActive = b.IsActive
    };

    private Task<Branch?> FindAsync(Guid org, Guid uuid, CancellationToken ct) =>
        Db.Branches.IgnoreQueryFilters().FirstOrDefaultAsync(b => b.OrganizationId == org && b.Uuid == uuid, ct);

    public async Task<IReadOnlyList<BranchModel>> ListAsync(HrmCaller caller, bool includeInactive, CancellationToken ct = default)
    {
        var rows = await Db.Branches.IgnoreQueryFilters().AsNoTracking()
            .Where(b => b.OrganizationId == caller.OrganizationId && (includeInactive || b.IsActive)).OrderBy(b => b.Name).ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    public async Task<BranchModel> GetAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default) =>
        Map(await FindAsync(caller.OrganizationId, uuid, ct) ?? throw new NotFoundException("Branch", uuid));

    private void Apply(Branch row, BranchUpsert request)
    {
        row.Code = HrmValidate.Code(request.Code);
        row.Name = HrmValidate.Name(request.Name);
        row.Address = HrmValidate.Optional(request.Address, 500, "Address");
        row.City = HrmValidate.Optional(request.City, 100, "City");
        row.WarehouseUuid = request.WarehouseUuid == Guid.Empty ? null : request.WarehouseUuid;
    }

    private async Task CheckCodeAsync(Guid org, string code, int id, CancellationToken ct)
    {
        if (await Db.Branches.IgnoreQueryFilters().AnyAsync(b => b.OrganizationId == org && b.Code == code && b.Id != id, ct))
            throw HrmErrors.Conflict("BRANCH_CODE_TAKEN", $"A branch with code {code} already exists.");
    }

    public async Task<BranchModel> CreateAsync(HrmCaller caller, BranchUpsert request, CancellationToken ct = default)
    {
        var row = new Branch();
        Apply(row, request);
        await CheckCodeAsync(caller.OrganizationId, row.Code, 0, ct);
        Stamp(row, caller, isNew: true);
        Db.Branches.Add(row);
        await SaveAsync(ct);
        return Map(row);
    }

    public async Task<BranchModel> UpdateAsync(HrmCaller caller, Guid uuid, BranchUpsert request, CancellationToken ct = default)
    {
        var row = await FindAsync(caller.OrganizationId, uuid, ct) ?? throw new NotFoundException("Branch", uuid);
        Apply(row, request);
        await CheckCodeAsync(caller.OrganizationId, row.Code, row.Id, ct);
        Stamp(row, caller);
        await SaveAsync(ct, "This branch");
        return Map(row);
    }

    public async Task<BranchModel> SetActiveAsync(HrmCaller caller, Guid uuid, bool isActive, CancellationToken ct = default)
    {
        var row = await FindAsync(caller.OrganizationId, uuid, ct) ?? throw new NotFoundException("Branch", uuid);
        if (!isActive && row.IsActive)
        {
            var count = await LiveRecords(caller.OrganizationId).CountAsync(r => r.BranchId == row.Id, ct);
            if (count > 0) throw HrmErrors.Conflict("BRANCH_IN_USE", $"{count} employee(s) work at this branch; move them first.");
        }
        row.IsActive = isActive;
        Stamp(row, caller);
        await SaveAsync(ct, "This branch");
        return Map(row);
    }
}

internal sealed class CostCenterService : HrmServiceBase, ICostCenterService
{
    private readonly IHrmSeeder _seeder;
    public CostCenterService(HrmDbContext db, IHrmSeeder seeder) : base(db) => _seeder = seeder;

    private async Task<int?> PayrollCostCenterIdAsync(Guid org, CancellationToken ct) =>
        await Db.Settings.IgnoreQueryFilters().AsNoTracking().Where(s => s.OrganizationId == org).Select(s => s.PayrollCostCenterId).FirstOrDefaultAsync(ct);

    private async Task<CostCenterModel> MapOneAsync(Guid org, CostCenter row, CancellationToken ct)
    {
        var payroll = await PayrollCostCenterIdAsync(org, ct);
        var parent = row.ParentId is { } p ? await Db.CostCenters.IgnoreQueryFilters().AsNoTracking().Where(c => c.Id == p).Select(c => (Guid?)c.Uuid).FirstOrDefaultAsync(ct) : null;
        var branch = row.BranchId is { } b ? await Db.Branches.IgnoreQueryFilters().AsNoTracking().Where(c => c.Id == b).Select(c => (Guid?)c.Uuid).FirstOrDefaultAsync(ct) : null;
        return new CostCenterModel { Uuid = row.Uuid, Code = row.Code, Name = row.Name, ParentUuid = parent, BranchUuid = branch, IsActive = row.IsActive, IsPayrollCostCenter = row.Id == payroll };
    }

    public async Task<IReadOnlyList<CostCenterModel>> ListAsync(HrmCaller caller, bool includeInactive, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var payroll = await PayrollCostCenterIdAsync(org, ct);
        return await Db.CostCenters.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.OrganizationId == org && (includeInactive || c.IsActive)).OrderBy(c => c.Code)
            .Select(c => new CostCenterModel
            {
                Uuid = c.Uuid, Code = c.Code, Name = c.Name, ParentUuid = c.Parent == null ? null : c.Parent.Uuid, BranchUuid = c.Branch == null ? null : c.Branch.Uuid,
                IsActive = c.IsActive, IsPayrollCostCenter = c.Id == payroll
            }).ToListAsync(ct);
    }

    private async Task ApplyAsync(CostCenter row, CostCenterUpsert request, Guid org, CancellationToken ct)
    {
        row.Code = HrmValidate.Code(request.Code);
        row.Name = HrmValidate.Name(request.Name);
        if (await Db.CostCenters.IgnoreQueryFilters().AnyAsync(c => c.OrganizationId == org && c.Code == row.Code && c.Id != row.Id, ct))
            throw HrmErrors.Conflict("COST_CENTER_CODE_TAKEN", $"A cost center with code {row.Code} already exists.");
        row.BranchId = await OptionalIdAsync(Db.Branches, org, request.BranchUuid, "Branch", ct);
        var parent = await OptionalIdAsync(Db.CostCenters, org, request.ParentUuid, "Cost center", ct);
        if (parent is not null && row.Id != 0)
        {
            // No cycles: walk up from the proposed parent.
            var parents = await Db.CostCenters.IgnoreQueryFilters().AsNoTracking().Where(c => c.OrganizationId == org).ToDictionaryAsync(c => c.Id, c => c.ParentId, ct);
            for (int? at = parent; at is not null; at = parents.GetValueOrDefault(at.Value))
                if (at == row.Id) throw HrmErrors.Bad("COST_CENTER_CYCLE", "A cost center cannot sit under itself.");
        }
        row.ParentId = parent;
    }

    public async Task<CostCenterModel> CreateAsync(HrmCaller caller, CostCenterUpsert request, CancellationToken ct = default)
    {
        await _seeder.EnsureAsync(caller.OrganizationId, ct);
        var row = new CostCenter();
        await ApplyAsync(row, request, caller.OrganizationId, ct);
        Stamp(row, caller, isNew: true);
        Db.CostCenters.Add(row);
        await SaveAsync(ct);
        return await MapOneAsync(caller.OrganizationId, row, ct);
    }

    public async Task<CostCenterModel> UpdateAsync(HrmCaller caller, Guid uuid, CostCenterUpsert request, CancellationToken ct = default)
    {
        var row = await Db.CostCenters.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.OrganizationId == caller.OrganizationId && c.Uuid == uuid, ct)
            ?? throw new NotFoundException("Cost center", uuid);
        var oldCode = row.Code;
        await ApplyAsync(row, request, caller.OrganizationId, ct);
        Stamp(row, caller);
        if (oldCode != row.Code)
        {
            // Code is denormalized on departments and records for the posting line; posted runs keep the code they had.
            await foreach (var dept in Db.Departments.IgnoreQueryFilters().Where(d => d.CostCenterId == row.Id).AsAsyncEnumerable().WithCancellation(ct))
                dept.CostCenterCode = row.Code;
            await foreach (var rec in Db.EmploymentRecords.IgnoreQueryFilters().Where(r => r.CostCenterId == row.Id).AsAsyncEnumerable().WithCancellation(ct))
                rec.CostCenterCode = row.Code;
        }
        await SaveAsync(ct, "This cost center");
        return await MapOneAsync(caller.OrganizationId, row, ct);
    }

    public async Task<CostCenterModel> SetActiveAsync(HrmCaller caller, Guid uuid, bool isActive, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var row = await Db.CostCenters.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.OrganizationId == org && c.Uuid == uuid, ct)
            ?? throw new NotFoundException("Cost center", uuid);
        if (!isActive && row.IsActive)
        {
            if (await PayrollCostCenterIdAsync(org, ct) == row.Id)
                throw HrmErrors.Conflict("COST_CENTER_IS_PAYROLL", "This is the payroll cost center; choose another one first.");
            if (await Db.Departments.IgnoreQueryFilters().AnyAsync(d => d.OrganizationId == org && d.CostCenterId == row.Id && d.IsActive, ct)
                || await LiveRecords(org).AnyAsync(r => r.CostCenterId == row.Id, ct))
                throw HrmErrors.Conflict("COST_CENTER_IN_USE", "Departments or employees use this cost center; move them first.");
        }
        row.IsActive = isActive;
        Stamp(row, caller);
        await SaveAsync(ct, "This cost center");
        return await MapOneAsync(org, row, ct);
    }
}

internal sealed class DepartmentService : HrmServiceBase, IDepartmentService
{
    public DepartmentService(HrmDbContext db) : base(db) { }

    private async Task<List<DepartmentTreeNode>> LoadAsync(Guid org, bool includeInactive, CancellationToken ct)
    {
        var today = HrmValidate.Today;
        var counts = await LiveRecords(org).GroupBy(r => r.DepartmentId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var rows = await Db.Departments.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.OrganizationId == org && (includeInactive || d.IsActive))
            .OrderBy(d => d.Name)
            .Select(d => new
            {
                d.Id,
                Node = new DepartmentTreeNode
                {
                    Uuid = d.Uuid, Code = d.Code, Name = d.Name, ParentUuid = d.Parent == null ? null : d.Parent.Uuid,
                    HeadEmployeeUuid = d.HeadEmployee == null ? null : d.HeadEmployee.Uuid,
                    HeadEmployeeName = d.HeadEmployee == null ? null : d.HeadEmployee.FirstName + " " + d.HeadEmployee.LastName,
                    BranchUuid = d.Branch == null ? null : d.Branch.Uuid, BranchName = d.Branch == null ? null : d.Branch.Name,
                    CostCenterUuid = d.CostCenter == null ? null : d.CostCenter.Uuid, CostCenterCode = d.CostCenterCode, IsActive = d.IsActive
                }
            }).ToListAsync(ct);
        foreach (var r in rows) r.Node.EmployeeCount = counts.GetValueOrDefault(r.Id);
        return rows.Select(r => r.Node).ToList();
    }

    public async Task<IReadOnlyList<DepartmentModel>> ListAsync(HrmCaller caller, bool includeInactive, CancellationToken ct = default) =>
        (await LoadAsync(caller.OrganizationId, includeInactive, ct)).Select(Flat).ToList();

    private static DepartmentModel Flat(DepartmentTreeNode n) => new()
    {
        Uuid = n.Uuid, Code = n.Code, Name = n.Name, ParentUuid = n.ParentUuid, HeadEmployeeUuid = n.HeadEmployeeUuid, HeadEmployeeName = n.HeadEmployeeName,
        BranchUuid = n.BranchUuid, BranchName = n.BranchName, CostCenterUuid = n.CostCenterUuid, CostCenterCode = n.CostCenterCode, IsActive = n.IsActive, EmployeeCount = n.EmployeeCount
    };

    public async Task<IReadOnlyList<DepartmentTreeNode>> GetTreeAsync(HrmCaller caller, bool includeInactive, CancellationToken ct = default)
    {
        var nodes = await LoadAsync(caller.OrganizationId, includeInactive, ct);
        var byUuid = nodes.ToDictionary(n => n.Uuid);
        var roots = new List<DepartmentTreeNode>();
        foreach (var node in nodes)
        {
            // A child whose parent is filtered out (inactive) shows as a root rather than vanishing.
            if (node.ParentUuid is { } parent && byUuid.TryGetValue(parent, out var p)) p.Children.Add(node);
            else roots.Add(node);
        }
        return roots;
    }

    private async Task<DepartmentModel> ModelAsync(Guid org, Guid uuid, CancellationToken ct) =>
        Flat((await LoadAsync(org, true, ct)).First(n => n.Uuid == uuid));

    private async Task ApplyAsync(Department row, DepartmentUpsert request, Guid org, CancellationToken ct)
    {
        row.Code = HrmValidate.Code(request.Code);
        row.Name = HrmValidate.Name(request.Name);
        if (await Db.Departments.IgnoreQueryFilters().AnyAsync(d => d.OrganizationId == org && d.Code == row.Code && d.Id != row.Id, ct))
            throw HrmErrors.Conflict("DEPARTMENT_CODE_TAKEN", $"A department with code {row.Code} already exists.");

        row.BranchId = await OptionalIdAsync(Db.Branches, org, request.BranchUuid, "Branch", ct);

        if (request.HeadEmployeeUuid is { } head && head != Guid.Empty)
            row.HeadEmployeeId = await Db.Employees.IgnoreQueryFilters().Where(e => e.OrganizationId == org && e.Uuid == head && !e.IsDeleted).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("Employee", head);
        else row.HeadEmployeeId = null;

        if (request.CostCenterUuid is { } cc && cc != Guid.Empty)
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

        var parent = await OptionalIdAsync(Db.Departments, org, request.ParentUuid, "Department", ct);
        if (parent is not null && row.Id != 0)
        {
            var parents = await Db.Departments.IgnoreQueryFilters().AsNoTracking().Where(d => d.OrganizationId == org).ToDictionaryAsync(d => d.Id, d => d.ParentId, ct);
            for (int? at = parent; at is not null; at = parents.GetValueOrDefault(at.Value))
                if (at == row.Id) throw HrmErrors.Bad("DEPARTMENT_CYCLE", "A department cannot sit under itself or one of its own sub-departments.");
        }
        row.ParentId = parent;
    }

    public async Task<DepartmentModel> CreateAsync(HrmCaller caller, DepartmentUpsert request, CancellationToken ct = default)
    {
        var row = new Department();
        await ApplyAsync(row, request, caller.OrganizationId, ct);
        Stamp(row, caller, isNew: true);
        Db.Departments.Add(row);
        await SaveAsync(ct);
        return await ModelAsync(caller.OrganizationId, row.Uuid, ct);
    }

    public async Task<DepartmentModel> UpdateAsync(HrmCaller caller, Guid uuid, DepartmentUpsert request, CancellationToken ct = default)
    {
        var row = await Db.Departments.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.OrganizationId == caller.OrganizationId && d.Uuid == uuid, ct)
            ?? throw new NotFoundException("Department", uuid);
        await ApplyAsync(row, request, caller.OrganizationId, ct);
        Stamp(row, caller);
        await SaveAsync(ct, "This department");
        return await ModelAsync(caller.OrganizationId, uuid, ct);
    }

    public async Task<DepartmentModel> SetActiveAsync(HrmCaller caller, Guid uuid, bool isActive, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var row = await Db.Departments.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.OrganizationId == org && d.Uuid == uuid, ct)
            ?? throw new NotFoundException("Department", uuid);
        if (!isActive && row.IsActive)
        {
            var count = await LiveRecords(org).CountAsync(r => r.DepartmentId == row.Id, ct);
            if (count > 0) throw HrmErrors.Conflict("DEPARTMENT_IN_USE", $"{count} employee(s) are in this department; move them first.");
            if (await Db.Departments.IgnoreQueryFilters().AnyAsync(d => d.OrganizationId == org && d.ParentId == row.Id && d.IsActive, ct))
                throw HrmErrors.Conflict("DEPARTMENT_HAS_CHILDREN", "This department has active sub-departments.");
        }
        row.IsActive = isActive;
        Stamp(row, caller);
        await SaveAsync(ct, "This department");
        return await ModelAsync(org, uuid, ct);
    }
}

internal sealed class DesignationService : HrmServiceBase, IDesignationService
{
    public DesignationService(HrmDbContext db) : base(db) { }

    private static DesignationModel Map(Designation d) => new() { Uuid = d.Uuid, Code = d.Code, Name = d.Name, Level = d.Level, IsActive = d.IsActive };

    public async Task<IReadOnlyList<DesignationModel>> ListAsync(HrmCaller caller, bool includeInactive, CancellationToken ct = default) =>
        (await Db.Designations.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.OrganizationId == caller.OrganizationId && (includeInactive || d.IsActive)).OrderBy(d => d.Level).ThenBy(d => d.Name).ToListAsync(ct)).Select(Map).ToList();

    private async Task ApplyAsync(Designation row, DesignationUpsert request, Guid org, CancellationToken ct)
    {
        row.Code = HrmValidate.Code(request.Code);
        row.Name = HrmValidate.Name(request.Name);
        row.Level = request.Level;
        if (await Db.Designations.IgnoreQueryFilters().AnyAsync(d => d.OrganizationId == org && d.Code == row.Code && d.Id != row.Id, ct))
            throw HrmErrors.Conflict("DESIGNATION_CODE_TAKEN", $"A designation with code {row.Code} already exists.");
    }

    public async Task<DesignationModel> CreateAsync(HrmCaller caller, DesignationUpsert request, CancellationToken ct = default)
    {
        var row = new Designation();
        await ApplyAsync(row, request, caller.OrganizationId, ct);
        Stamp(row, caller, isNew: true);
        Db.Designations.Add(row);
        await SaveAsync(ct);
        return Map(row);
    }

    public async Task<DesignationModel> UpdateAsync(HrmCaller caller, Guid uuid, DesignationUpsert request, CancellationToken ct = default)
    {
        var row = await Db.Designations.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.OrganizationId == caller.OrganizationId && d.Uuid == uuid, ct)
            ?? throw new NotFoundException("Designation", uuid);
        await ApplyAsync(row, request, caller.OrganizationId, ct);
        Stamp(row, caller);
        await SaveAsync(ct, "This designation");
        return Map(row);
    }

    public async Task<DesignationModel> SetActiveAsync(HrmCaller caller, Guid uuid, bool isActive, CancellationToken ct = default)
    {
        var row = await Db.Designations.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.OrganizationId == caller.OrganizationId && d.Uuid == uuid, ct)
            ?? throw new NotFoundException("Designation", uuid);
        if (!isActive && row.IsActive && await LiveRecords(caller.OrganizationId).AnyAsync(r => r.DesignationId == row.Id, ct))
            throw HrmErrors.Conflict("DESIGNATION_IN_USE", "Employees hold this designation; change them first.");
        row.IsActive = isActive;
        Stamp(row, caller);
        await SaveAsync(ct, "This designation");
        return Map(row);
    }
}

internal sealed class GradeService : HrmServiceBase, IGradeService
{
    public GradeService(HrmDbContext db) : base(db) { }

    /// <summary>§5.3 — the salary band is S2.</summary>
    private static GradeModel Map(Grade g, HrmCaller caller)
    {
        var see = caller.Has(PermissionCodes.HRM_SALARY_VIEW);
        return new GradeModel
        {
            Uuid = g.Uuid, Code = g.Code, Name = g.Name, IsActive = g.IsActive, SalaryMasked = !see,
            MinSalary = see ? g.MinSalary : null, MaxSalary = see ? g.MaxSalary : null
        };
    }

    public async Task<IReadOnlyList<GradeModel>> ListAsync(HrmCaller caller, bool includeInactive, CancellationToken ct = default) =>
        (await Db.Grades.IgnoreQueryFilters().AsNoTracking()
            .Where(g => g.OrganizationId == caller.OrganizationId && (includeInactive || g.IsActive)).OrderBy(g => g.Code).ToListAsync(ct)).Select(g => Map(g, caller)).ToList();

    private async Task ApplyAsync(Grade row, GradeUpsert request, Guid org, CancellationToken ct)
    {
        row.Code = HrmValidate.Code(request.Code);
        row.Name = HrmValidate.Name(request.Name);
        if (request.MinSalary < 0 || request.MaxSalary < 0) throw HrmErrors.Bad("GRADE_RANGE_INVALID", "A salary cannot be negative.");
        if (request.MinSalary is { } min && request.MaxSalary is { } max && min > max)
            throw HrmErrors.Bad("GRADE_RANGE_INVALID", "The minimum salary cannot be above the maximum.");
        row.MinSalary = request.MinSalary;
        row.MaxSalary = request.MaxSalary;
        if (await Db.Grades.IgnoreQueryFilters().AnyAsync(g => g.OrganizationId == org && g.Code == row.Code && g.Id != row.Id, ct))
            throw HrmErrors.Conflict("GRADE_CODE_TAKEN", $"A grade with code {row.Code} already exists.");
    }

    public async Task<GradeModel> CreateAsync(HrmCaller caller, GradeUpsert request, CancellationToken ct = default)
    {
        var row = new Grade();
        await ApplyAsync(row, request, caller.OrganizationId, ct);
        Stamp(row, caller, isNew: true);
        Db.Grades.Add(row);
        await SaveAsync(ct);
        return Map(row, caller);
    }

    public async Task<GradeModel> UpdateAsync(HrmCaller caller, Guid uuid, GradeUpsert request, CancellationToken ct = default)
    {
        var row = await Db.Grades.IgnoreQueryFilters().FirstOrDefaultAsync(g => g.OrganizationId == caller.OrganizationId && g.Uuid == uuid, ct)
            ?? throw new NotFoundException("Grade", uuid);
        // Editing the band without seeing it would silently overwrite it with what the form could not show.
        if (!caller.Has(PermissionCodes.HRM_SALARY_VIEW))
        {
            request.MinSalary = row.MinSalary;
            request.MaxSalary = row.MaxSalary;
        }
        await ApplyAsync(row, request, caller.OrganizationId, ct);
        Stamp(row, caller);
        await SaveAsync(ct, "This grade");
        return Map(row, caller);
    }

    public async Task<GradeModel> SetActiveAsync(HrmCaller caller, Guid uuid, bool isActive, CancellationToken ct = default)
    {
        var row = await Db.Grades.IgnoreQueryFilters().FirstOrDefaultAsync(g => g.OrganizationId == caller.OrganizationId && g.Uuid == uuid, ct)
            ?? throw new NotFoundException("Grade", uuid);
        if (!isActive && row.IsActive && await LiveRecords(caller.OrganizationId).AnyAsync(r => r.GradeId == row.Id, ct))
            throw HrmErrors.Conflict("GRADE_IN_USE", "Employees are on this grade; change them first.");
        row.IsActive = isActive;
        Stamp(row, caller);
        await SaveAsync(ct, "This grade");
        return Map(row, caller);
    }
}

internal sealed class HrmSettingsService : HrmServiceBase, IHrmSettingsService
{
    private readonly IHrmSeeder _seeder;
    public HrmSettingsService(HrmDbContext db, IHrmSeeder seeder) : base(db) => _seeder = seeder;

    private async Task<HrmSettings> LoadAsync(Guid org, CancellationToken ct)
    {
        await _seeder.EnsureAsync(org, ct);
        return await Db.Settings.IgnoreQueryFilters().Include(s => s.PayrollCostCenter).FirstAsync(s => s.OrganizationId == org, ct);
    }

    private static HrmSettingsModel Map(HrmSettings s) => new()
    {
        PayrollCostCenterUuid = s.PayrollCostCenter?.Uuid, PayrollCostCenterCode = s.PayrollCostCenter?.Code, PayrollCostCenterName = s.PayrollCostCenter?.Name,
        PayrollCostCenterMode = s.PayrollCostCenterMode, PostDatePolicy = s.PostDatePolicy, EmployeeNumberPrefix = s.EmployeeNumberPrefix, CountryCode = s.CountryCode,
        WorkingDaysDivisor = s.WorkingDaysDivisor, DefaultExpenseSettlement = s.DefaultExpenseSettlement, DefaultReceiptThreshold = s.DefaultReceiptThreshold,
        EnablementCompletedAt = s.EnablementCompletedAt
    };

    public async Task<HrmSettingsModel> GetAsync(HrmCaller caller, CancellationToken ct = default) => Map(await LoadAsync(caller.OrganizationId, ct));

    public async Task<HrmSettingsModel> UpdateAsync(HrmCaller caller, HrmSettingsUpdate request, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var row = await LoadAsync(org, ct);
        if (request.PayrollCostCenterUuid is { } uuid && uuid != Guid.Empty)
        {
            var center = await Db.CostCenters.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.OrganizationId == org && c.Uuid == uuid, ct)
                ?? throw new NotFoundException("Cost center", uuid);
            if (!center.IsActive) throw HrmErrors.Bad("COST_CENTER_INACTIVE", $"Cost center {center.Code} is inactive.");
            row.PayrollCostCenterId = center.Id;
            row.PayrollCostCenter = center;
        }
        // §11A.2 — a mode change applies to future runs only: each run snapshots its mode at calculation.
        if (request.PayrollCostCenterMode is not null) row.PayrollCostCenterMode = HrmValidate.OneOf(request.PayrollCostCenterMode, PayrollCostCenterModes.All, "payrollCostCenterMode");
        if (request.PostDatePolicy is not null) row.PostDatePolicy = HrmValidate.OneOf(request.PostDatePolicy, PayrollPostDatePolicies.All, "postDatePolicy");
        if (request.DefaultExpenseSettlement is not null) row.DefaultExpenseSettlement = HrmValidate.OneOf(request.DefaultExpenseSettlement, ExpenseSettlementMethods.All, "defaultExpenseSettlement");
        if (request.EmployeeNumberPrefix is not null)
        {
            var prefix = HrmValidate.Code(request.EmployeeNumberPrefix, 10);
            row.EmployeeNumberPrefix = prefix;
        }
        if (request.WorkingDaysDivisor is { } divisor)
        {
            if (divisor is < 1 or > 31) throw new BadRequestException("The working days divisor must be between 1 and 31.");
            row.WorkingDaysDivisor = divisor;
        }
        if (request.DefaultReceiptThreshold is { } threshold)
        {
            if (threshold < 0) throw new BadRequestException("The receipt threshold cannot be negative.");
            row.DefaultReceiptThreshold = threshold;
        }
        Stamp(row, caller);
        await SaveAsync(ct, "The HR settings");
        return Map(row);
    }
}

internal sealed class HrUserScopeService : HrmServiceBase, IHrUserScopeService
{
    private readonly SMS.Shared.Common.IOrgUserDirectory? _users;

    public HrUserScopeService(HrmDbContext db, SMS.Shared.Common.IOrgUserDirectory? users = null) : base(db) => _users = users;

    public async Task<IReadOnlyList<HrScopeUserModel>> SearchUsersAsync(HrmCaller caller, string? search, CancellationToken ct = default)
    {
        if (_users is null) return [];
        return (await _users.SearchAsync(caller.OrganizationId, search, 50, ct))
            .Select(u => new HrScopeUserModel { UserId = u.UserId, Name = u.Name, Email = u.Email }).ToList();
    }

    public async Task<IReadOnlyList<HrUserScopeModel>> ListAsync(HrmCaller caller, int? userId, CancellationToken ct = default) =>
        await Db.UserScopes.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.OrganizationId == caller.OrganizationId && (userId == null || s.UserId == userId))
            .OrderBy(s => s.UserId)
            .Select(s => new HrUserScopeModel
            {
                Uuid = s.Uuid, UserId = s.UserId, ScopeType = s.ScopeType,
                BranchUuid = s.Branch == null ? null : s.Branch.Uuid, BranchName = s.Branch == null ? null : s.Branch.Name,
                DepartmentUuid = s.Department == null ? null : s.Department.Uuid, DepartmentName = s.Department == null ? null : s.Department.Name
            }).ToListAsync(ct);

    public async Task<HrUserScopeModel> AddAsync(HrmCaller caller, HrUserScopeCreate request, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        if (request.UserId <= 0) throw new BadRequestException("A user is required.");
        if ((request.BranchUuid is null) == (request.DepartmentUuid is null))
            throw new BadRequestException("Give either a branch or a department.");
        var branch = await OptionalIdAsync(Db.Branches, org, request.BranchUuid, "Branch", ct);
        var department = await OptionalIdAsync(Db.Departments, org, request.DepartmentUuid, "Department", ct);
        var existing = await Db.UserScopes.IgnoreQueryFilters().FirstOrDefaultAsync(
            s => s.OrganizationId == org && s.UserId == request.UserId && s.BranchId == branch && s.DepartmentId == department, ct);
        if (existing is null)
        {
            existing = new HrUserScope { UserId = request.UserId, ScopeType = branch is null ? HrScopeTypes.Department : HrScopeTypes.Branch, BranchId = branch, DepartmentId = department };
            Stamp(existing, caller, isNew: true);
            Db.UserScopes.Add(existing);
            await SaveAsync(ct);
        }
        return (await ListAsync(caller, request.UserId, ct)).First(s => s.Uuid == existing.Uuid);
    }

    public async Task RemoveAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var row = await Db.UserScopes.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.OrganizationId == caller.OrganizationId && s.Uuid == uuid, ct)
            ?? throw new NotFoundException("HR scope", uuid);
        Db.UserScopes.Remove(row);
        await SaveAsync(ct);
    }
}

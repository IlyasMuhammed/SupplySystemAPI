using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Shared.Authorization;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Hrm.Security;

/// <summary>
/// What one caller may see inside the tenant (§3.3): All, or the union of own branch / own department (+ children) / explicit
/// assignments (hrm.HrUserScopes), the people reporting to them (Team, direct and indirect) and themselves (Self).
/// </summary>
public sealed record HrmScope(
    bool All, int? OwnEmployeeId,
    IReadOnlySet<int> BranchIds, IReadOnlySet<int> DepartmentIds, IReadOnlySet<int> TeamEmployeeIds);

/// <summary>
/// The shared HR scope service: every agent's employee-bound service goes through it (API-CONTRACT §13). Outside the scope = 404,
/// exactly like another organization's row. Results are cached for the request (the service is scoped).
/// </summary>
internal interface IHrmScopeService
{
    Task<HrmScope> GetAsync(HrmCaller caller, CancellationToken ct = default);

    /// <summary>The caller's own employee row id, or null when no employee is linked to the user.</summary>
    Task<int?> OwnEmployeeIdAsync(HrmCaller caller, CancellationToken ct = default);

    /// <summary>Narrows employees to the scope. Always filters on the organization too.</summary>
    IQueryable<Employee> Apply(IQueryable<Employee> query, HrmCaller caller, HrmScope scope);

    /// <summary>The employee row by uuid; 404 for another organization, a deleted row or outside the scope.</summary>
    Task<Employee> RequireEmployeeAsync(HrmCaller caller, Guid employeeUuid, CancellationToken ct = default);

    /// <summary>Same, by int id, for a row loaded by another service.</summary>
    Task<bool> CanAccessAsync(HrmCaller caller, int employeeId, CancellationToken ct = default);

    /// <summary>Self-service: the employee row of the caller; 404 HRM_NO_EMPLOYEE when the user is not linked to one.</summary>
    Task<Employee> RequireOwnEmployeeAsync(HrmCaller caller, CancellationToken ct = default);
}

internal sealed class HrmScopeService : IHrmScopeService
{
    private readonly HrmDbContext _db;
    private readonly Dictionary<(Guid, int), HrmScope> _cache = [];

    public HrmScopeService(HrmDbContext db) => _db = db;

    private static readonly IReadOnlySet<int> None = new HashSet<int>();

    public async Task<int?> OwnEmployeeIdAsync(HrmCaller caller, CancellationToken ct = default)
    {
        if (caller.UserId <= 0) return null;
        return await _db.Employees.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.OrganizationId == caller.OrganizationId && e.UserId == caller.UserId && !e.IsDeleted)
            .Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
    }

    public async Task<HrmScope> GetAsync(HrmCaller caller, CancellationToken ct = default)
    {
        var key = (caller.OrganizationId, caller.UserId);
        if (_cache.TryGetValue(key, out var cached)) return cached;

        var org = caller.OrganizationId;
        var own = await OwnEmployeeIdAsync(caller, ct);
        if (caller.Has(PermissionCodes.HRM_SCOPE_ALL))
            return _cache[key] = new HrmScope(true, own, None, None, None);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var branches = new HashSet<int>();
        var departmentRoots = new HashSet<int>();

        var explicitScopes = await _db.UserScopes.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.OrganizationId == org && s.UserId == caller.UserId)
            .Select(s => new { s.BranchId, s.DepartmentId }).ToListAsync(ct);
        foreach (var s in explicitScopes)
        {
            if (s.BranchId is { } b) branches.Add(b);
            if (s.DepartmentId is { } d) departmentRoots.Add(d);
        }

        // Everyone's current reporting line: enough to build the team and to find the caller's own branch/department.
        var records = await _db.EmploymentRecords.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.OrganizationId == org && (r.EffectiveTo == null || r.EffectiveTo >= today))
            .Select(r => new { r.EmployeeId, r.ManagerEmployeeId, r.BranchId, r.DepartmentId }).ToListAsync(ct);

        if (own is { } self)
        {
            var mine = records.Where(r => r.EmployeeId == self).ToList();
            if (caller.Has(PermissionCodes.HRM_SCOPE_BRANCH)) branches.UnionWith(mine.Select(r => r.BranchId));
            if (caller.Has(PermissionCodes.HRM_SCOPE_DEPARTMENT)) departmentRoots.UnionWith(mine.Select(r => r.DepartmentId));
        }

        var departments = new HashSet<int>();
        if (departmentRoots.Count > 0)
        {
            var tree = await _db.Departments.IgnoreQueryFilters().AsNoTracking()
                .Where(d => d.OrganizationId == org).Select(d => new { d.Id, d.ParentId }).ToListAsync(ct);
            var pending = new Queue<int>(departmentRoots);
            while (pending.TryDequeue(out var id))
                if (departments.Add(id))
                    foreach (var child in tree.Where(d => d.ParentId == id)) pending.Enqueue(child.Id);
        }

        var team = new HashSet<int>();
        if (own is { } manager)
        {
            var pending = new Queue<int>([manager]);
            while (pending.TryDequeue(out var id))
                foreach (var report in records.Where(r => r.ManagerEmployeeId == id))
                    if (report.EmployeeId != manager && team.Add(report.EmployeeId)) pending.Enqueue(report.EmployeeId);
        }

        return _cache[key] = new HrmScope(false, own, branches, departments, team);
    }

    public IQueryable<Employee> Apply(IQueryable<Employee> query, HrmCaller caller, HrmScope scope)
    {
        var org = caller.OrganizationId;
        query = query.IgnoreQueryFilters().Where(e => e.OrganizationId == org && !e.IsDeleted);
        if (scope.All) return query;

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var own = scope.OwnEmployeeId ?? -1;
        var team = scope.TeamEmployeeIds.ToList();
        var branches = scope.BranchIds.ToList();
        var departments = scope.DepartmentIds.ToList();
        return query.Where(e => e.Id == own || team.Contains(e.Id)
            || e.EmploymentRecords.Any(r => (r.EffectiveTo == null || r.EffectiveTo >= today)
                && (branches.Contains(r.BranchId) || departments.Contains(r.DepartmentId))));
    }

    public async Task<Employee> RequireEmployeeAsync(HrmCaller caller, Guid employeeUuid, CancellationToken ct = default)
    {
        var scope = await GetAsync(caller, ct);
        return await Apply(_db.Employees, caller, scope).FirstOrDefaultAsync(e => e.Uuid == employeeUuid, ct)
            ?? throw new NotFoundException("Employee", employeeUuid);
    }

    public async Task<bool> CanAccessAsync(HrmCaller caller, int employeeId, CancellationToken ct = default)
    {
        var scope = await GetAsync(caller, ct);
        return await Apply(_db.Employees, caller, scope).AnyAsync(e => e.Id == employeeId, ct);
    }

    public async Task<Employee> RequireOwnEmployeeAsync(HrmCaller caller, CancellationToken ct = default)
    {
        var own = await OwnEmployeeIdAsync(caller, ct);
        if (own is null) throw new NotFoundException("HRM_NO_EMPLOYEE: Your user is not linked to an employee record.");
        return await _db.Employees.IgnoreQueryFilters().FirstAsync(e => e.Id == own && e.OrganizationId == caller.OrganizationId, ct);
    }
}

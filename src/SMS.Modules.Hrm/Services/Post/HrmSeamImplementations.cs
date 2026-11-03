using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Data;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Hrm.Services.Post;

/// <summary>
/// FD-9 / §11A.2: HRM cannot be switched on without an active payroll cost center, on any path. Reads only; it never seeds, so an organization
/// that merely tried to enable HRM gains no HR data.
/// </summary>
internal sealed class HrmModuleEnablementGuard : IModuleEnablementGuard
{
    public const string RequiredCode = "PAYROLL_COST_CENTER_REQUIRED";

    private readonly HrmDbContext _db;
    public HrmModuleEnablementGuard(HrmDbContext db) => _db = db;

    public string ModuleCode => ModuleCodes.Hrm;

    public async Task CheckAsync(Guid organizationId, CancellationToken ct = default)
    {
        var active = await _db.Settings.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.OrganizationId == organizationId && s.PayrollCostCenterId != null)
            .AnyAsync(s => s.PayrollCostCenter!.IsActive && s.PayrollCostCenter.OrganizationId == organizationId, ct);
        if (!active)
            throw new UnprocessableEntityException($"{RequiredCode}: Choose a payroll cost center before switching on Human Resources.");
    }
}

internal sealed class HrmCostCenterSource : IHrmCostCenterSource
{
    private readonly HrmDbContext _db;
    public HrmCostCenterSource(HrmDbContext db) => _db = db;

    public async Task<IReadOnlyList<HrmCostCenterRef>> ListAsync(Guid organizationId, CancellationToken ct = default)
    {
        var payroll = await _db.Settings.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.OrganizationId == organizationId).Select(s => s.PayrollCostCenterId).FirstOrDefaultAsync(ct);
        var rows = await _db.CostCenters.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.OrganizationId == organizationId).OrderBy(c => c.Code)
            .Select(c => new { c.Id, c.Code, c.Name, c.IsActive }).ToListAsync(ct);
        return rows.Select(c => new HrmCostCenterRef(c.Code, c.Name, c.IsActive, c.Id == payroll)).ToList();
    }
}

/// <summary>G-11. Names and numbers only; a CNIC is encrypted and never travels.</summary>
internal sealed class HrmPersonalIdentifierSource : IHrmPersonalIdentifierSource
{
    private readonly HrmDbContext _db;
    public HrmPersonalIdentifierSource(HrmDbContext db) => _db = db;

    public async Task<IReadOnlyCollection<string>> GetAsync(Guid organizationId, CancellationToken ct = default)
    {
        var rows = await _db.Employees.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.OrganizationId == organizationId && !e.IsDeleted)
            .Select(e => new { e.FullName, e.EmployeeNumber }).ToListAsync(ct);
        return rows.SelectMany(e => new[] { e.FullName, e.EmployeeNumber })
            .Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

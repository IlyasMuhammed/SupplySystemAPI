using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Data;
using SMS.Shared.Common;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Hrm.Onboarding;

/// <summary>
/// A50 D-30 / D-94 — the HRM steps of the onboarding checklist (shown only while MODULE_HRM is on, ChecklistCatalog).
/// HRM_SETUP = the payroll cost center is chosen; HRM_ADD_EMPLOYEE = at least one employee that is not deleted;
/// HRM_VERIFY_TAX = a person has verified a tax table. This only READS verification: nothing here marks anything verified (G-21).
/// </summary>
internal sealed class HrmOnboardingSignal : IOnboardingSignal
{
    private readonly HrmDbContext _db;
    private readonly ITenantContext _tenant;

    public HrmOnboardingSignal(HrmDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    public IReadOnlyCollection<string> StepCodes { get; } = [OnboardingStepCodes.HrmSetup, OnboardingStepCodes.HrmAddEmployee, OnboardingStepCodes.HrmVerifyTax];

    public async Task<IReadOnlySet<string>> CompletedAsync(IReadOnlySet<Guid> demoEntityIds, CancellationToken ct = default)
    {
        var org = _tenant.OrganizationId;
        var done = new HashSet<string>();
        if (await _db.Settings.IgnoreQueryFilters().AsNoTracking().AnyAsync(s => s.OrganizationId == org && s.PayrollCostCenterId != null, ct))
            done.Add(OnboardingStepCodes.HrmSetup);
        if (await _db.Employees.IgnoreQueryFilters().AsNoTracking().AnyAsync(e => e.OrganizationId == org && !e.IsDeleted, ct))
            done.Add(OnboardingStepCodes.HrmAddEmployee);
        if (await _db.TaxSlabTables.IgnoreQueryFilters().AsNoTracking().AnyAsync(t => t.OrganizationId == org && t.IsVerified, ct))
            done.Add(OnboardingStepCodes.HrmVerifyTax);
        return done;
    }
}

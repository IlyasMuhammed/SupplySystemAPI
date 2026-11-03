using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Hrm.Services;

/// <summary>A37 D-18 — what is in progress when an administrator is about to switch HRM off.</summary>
internal sealed class HrmImpactProvider : IModuleImpactProvider
{
    private readonly HrmDbContext _db;
    public HrmImpactProvider(HrmDbContext db) => _db = db;

    public string ModuleCode => ModuleCodes.Hrm;

    public async Task<IReadOnlyList<ModuleImpactItem>> GetInProgressAsync(Guid organizationId, CancellationToken ct = default)
    {
        var employed = EmployeeStatuses.Employed.ToList();
        var openRuns = new[] { PayrollRunStatuses.Draft, PayrollRunStatuses.Calculated, PayrollRunStatuses.UnderReview, PayrollRunStatuses.Approved, PayrollRunStatuses.Finalized, PayrollRunStatuses.Posted };
        var items = new List<ModuleImpactItem>
        {
            new("Employees on the books", await _db.Employees.IgnoreQueryFilters().CountAsync(e => e.OrganizationId == organizationId && !e.IsDeleted && employed.Contains(e.Status), ct)),
            new("Payroll runs not yet paid", await _db.PayrollRuns.IgnoreQueryFilters().CountAsync(r => r.OrganizationId == organizationId && openRuns.Contains(r.Status), ct)),
            new("Leave requests pending", await _db.LeaveRequests.IgnoreQueryFilters().CountAsync(r => r.OrganizationId == organizationId && r.Status == LeaveRequestStatuses.Pending, ct)),
            new("Expense claims not yet paid", await _db.ExpenseClaims.IgnoreQueryFilters().CountAsync(c => c.OrganizationId == organizationId && (c.Status == ExpenseClaimStatuses.Submitted || c.Status == ExpenseClaimStatuses.Approved), ct)),
            new("Loans being repaid", await _db.Loans.IgnoreQueryFilters().CountAsync(l => l.OrganizationId == organizationId && l.Status == LoanStatuses.Disbursed, ct)),
        };
        return items.Where(i => i.Count > 0).ToList();
    }
}

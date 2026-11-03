using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Models;
using SMS.Shared.Exceptions;
using Xunit;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Hrm.Tests;

/// <summary>Tasks 010, 018, 024 + the deactivation guards and FD-9's cost-center rules.</summary>
public sealed class OrgStructureTests
{
    [Fact]
    public async Task Branch_create_uppercases_the_code_and_a_duplicate_is_409()
    {
        var h = new HrmHarness();
        var a = await h.Branches.CreateAsync(h.Admin(), new BranchUpsert { Code = " khi ", Name = "Karachi", City = "Karachi" });
        a.Code.Should().Be("KHI");
        a.IsActive.Should().BeTrue();

        var dup = () => h.Branches.CreateAsync(h.Admin(), new BranchUpsert { Code = "KHI", Name = "Other" });
        (await dup.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("BRANCH_CODE_TAKEN:");
    }

    [Fact]
    public async Task Same_branch_code_is_fine_in_another_organization_and_its_uuid_is_404_here()
    {
        var h = new HrmHarness();
        var other = new HrmHarness(dbName: h.DbName);
        await h.Branches.CreateAsync(h.Admin(), new BranchUpsert { Code = "KHI", Name = "Mine" });
        var theirs = await other.Branches.CreateAsync(other.Admin(), new BranchUpsert { Code = "KHI", Name = "Theirs" });

        (await h.Branches.ListAsync(h.Admin(), true)).Should().ContainSingle(b => b.Name == "Mine");
        var read = () => h.Branches.GetAsync(h.Admin(), theirs.Uuid);
        await read.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task A_branch_in_use_cannot_be_deactivated_until_the_employee_moves()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var e = await h.EmployeeAsync(b);

        var deactivate = () => h.Branches.SetActiveAsync(h.Admin(), b.Karachi, false);
        (await deactivate.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("BRANCH_IN_USE:");

        await h.Employment.TransferAsync(h.Admin(), e.Uuid, new EmploymentTransferRequest { EffectiveFrom = new DateOnly(2026, 2, 1), BranchUuid = b.Lahore });
        // Karachi's record closed at 2026-01-31: only the Lahore one is live.
        (await h.Branches.SetActiveAsync(h.Admin(), b.Karachi, false)).IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Department_tree_nests_children_and_counts_live_employees()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        await h.EmployeeAsync(b, department: b.Field);

        var tree = await h.Departments.GetTreeAsync(h.Admin(), false);
        tree.Select(n => n.Code).Should().BeEquivalentTo(["OPS", "SALES"]);
        var ops = tree.Single(n => n.Code == "OPS");
        ops.CostCenterCode.Should().Be("OPS");
        ops.Children.Should().ContainSingle(c => c.Code == "FIELD" && c.EmployeeCount == 1);
    }

    [Fact]
    public async Task A_department_cannot_become_its_own_ancestor()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var move = () => h.Departments.UpdateAsync(h.Admin(), b.Ops, new DepartmentUpsert { Code = "OPS", Name = "Operations", ParentUuid = b.Field });
        (await move.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().StartWith("DEPARTMENT_CYCLE:");
    }

    [Fact]
    public async Task A_department_with_active_children_or_employees_stays_active()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var withChild = () => h.Departments.SetActiveAsync(h.Admin(), b.Ops, false);
        (await withChild.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("DEPARTMENT_HAS_CHILDREN:");

        await h.EmployeeAsync(b, department: b.Sales);
        var withEmployee = () => h.Departments.SetActiveAsync(h.Admin(), b.Sales, false);
        (await withEmployee.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("DEPARTMENT_IN_USE:");
    }

    [Fact]
    public async Task Changing_a_cost_center_code_carries_to_its_departments()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        await h.CostCenters.UpdateAsync(h.Admin(), b.CostCenter, new CostCenterUpsert { Code = "OPS2", Name = "Operations" });
        (await h.Departments.ListAsync(h.Admin(), false)).Single(d => d.Code == "OPS").CostCenterCode.Should().Be("OPS2");
    }

    [Fact]
    public async Task Grade_min_above_max_is_refused()
    {
        var h = new HrmHarness();
        var bad = () => h.Grades.CreateAsync(h.Admin(), new GradeUpsert { Code = "G1", Name = "One", MinSalary = 200, MaxSalary = 100 });
        (await bad.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().StartWith("GRADE_RANGE_INVALID:");
        (await h.Grades.CreateAsync(h.Admin(), new GradeUpsert { Code = "G1", Name = "One", MinSalary = 100, MaxSalary = 100 })).MaxSalary.Should().Be(100);
    }

    [Fact]
    public async Task Grade_band_is_masked_without_salary_view_and_is_not_wiped_by_a_masked_editor()
    {
        var h = new HrmHarness();
        var grade = await h.Grades.CreateAsync(h.Admin(), new GradeUpsert { Code = "G1", Name = "One", MinSalary = 100, MaxSalary = 200 });

        var viewer = h.As(5, P.HRM_ORG_MANAGE);
        var seen = (await h.Grades.ListAsync(viewer, false)).Single();
        seen.MinSalary.Should().BeNull();
        seen.MaxSalary.Should().BeNull();
        seen.SalaryMasked.Should().BeTrue();

        await h.Grades.UpdateAsync(viewer, grade.Uuid, new GradeUpsert { Code = "G1", Name = "Renamed" });
        var after = (await h.Grades.ListAsync(h.Admin(), false)).Single();
        after.Name.Should().Be("Renamed");
        after.MinSalary.Should().Be(100);
        after.MaxSalary.Should().Be(200);
    }

    [Fact]
    public async Task The_payroll_cost_center_cannot_be_deactivated()
    {
        var h = new HrmHarness();
        var center = await h.CostCenters.CreateAsync(h.Admin(), new CostCenterUpsert { Code = "SAL", Name = "Salaries" });
        await h.Settings.UpdateAsync(h.Admin(), new HrmSettingsUpdate { PayrollCostCenterUuid = center.Uuid });

        var off = () => h.CostCenters.SetActiveAsync(h.Admin(), center.Uuid, false);
        (await off.Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("COST_CENTER_IS_PAYROLL:");
        (await h.CostCenters.ListAsync(h.Admin(), false)).Single().IsPayrollCostCenter.Should().BeTrue();
    }

    [Fact]
    public async Task Another_organizations_cost_center_cannot_be_the_payroll_one_and_an_inactive_one_neither()
    {
        var h = new HrmHarness();
        var other = new HrmHarness(dbName: h.DbName);
        var theirs = await other.CostCenters.CreateAsync(other.Admin(), new CostCenterUpsert { Code = "X", Name = "X" });
        var cross = () => h.Settings.UpdateAsync(h.Admin(), new HrmSettingsUpdate { PayrollCostCenterUuid = theirs.Uuid });
        await cross.Should().ThrowAsync<NotFoundException>();

        var mine = await h.CostCenters.CreateAsync(h.Admin(), new CostCenterUpsert { Code = "Y", Name = "Y" });
        await h.CostCenters.SetActiveAsync(h.Admin(), mine.Uuid, false);
        var inactive = () => h.Settings.UpdateAsync(h.Admin(), new HrmSettingsUpdate { PayrollCostCenterUuid = mine.Uuid });
        (await inactive.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().StartWith("COST_CENTER_INACTIVE:");
    }

    [Fact]
    public async Task Settings_validate_their_enums_and_start_on_the_adopted_defaults()
    {
        var h = new HrmHarness();
        var s = await h.Settings.GetAsync(h.Admin());
        s.PayrollCostCenterMode.Should().Be(PayrollCostCenterModes.Central);
        s.PostDatePolicy.Should().Be(PayrollPostDatePolicies.PayDateWithFallback);
        s.DefaultExpenseSettlement.Should().Be(ExpenseSettlementMethods.WithPayroll);
        s.PayrollCostCenterUuid.Should().BeNull();

        var bad = () => h.Settings.UpdateAsync(h.Admin(), new HrmSettingsUpdate { PostDatePolicy = "Whenever" });
        await bad.Should().ThrowAsync<BadRequestException>();
        (await h.Settings.UpdateAsync(h.Admin(), new HrmSettingsUpdate { PayrollCostCenterMode = "by_department" })).PayrollCostCenterMode.Should().Be(PayrollCostCenterModes.ByDepartment);
    }
}

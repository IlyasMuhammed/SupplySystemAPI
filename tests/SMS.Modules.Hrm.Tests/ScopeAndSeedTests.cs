using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Services;
using SMS.Shared.Exceptions;
using Xunit;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Hrm.Tests;

/// <summary>§3.3 HR scope (task 420's unit half) and the per-organization seeds (tasks 103, 136, 168, 174, 197, 198, 381, 375).</summary>
public sealed class ScopeTests
{
    private static async Task<(HrmHarness h, HrmHarness.Base b, EmployeeDetail boss, EmployeeDetail report, EmployeeDetail deep, EmployeeDetail field, EmployeeDetail sales, EmployeeDetail lahore)> WorldAsync()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var boss = await h.EmployeeAsync(b, "Boss", userId: 1, department: b.Ops);
        var report = await h.EmployeeAsync(b, "Report", userId: 2, department: b.Ops, manager: boss.Uuid);
        var deep = await h.EmployeeAsync(b, "Deep", userId: 3, department: b.Ops, manager: report.Uuid);
        var field = await h.EmployeeAsync(b, "Field", department: b.Field);
        var sales = await h.EmployeeAsync(b, "Sales", department: b.Sales);
        var lahore = await h.EmployeeAsync(b, "Lahore", department: b.Sales, branch: b.Lahore);
        return (h, b, boss, report, deep, field, sales, lahore);
    }

    private static async Task<List<string>> SeenAsync(HrmHarness h, params string[] permissions) => await SeenAsAsync(h, 999, permissions);

    private static async Task<List<string>> SeenAsAsync(HrmHarness h, int userId, params string[] permissions)
    {
        var page = await h.Employees.ListAsync(h.As(userId, [P.HRM_EMPLOYEE_VIEW, .. permissions]), new EmployeeListFilter { PageSize = 200 });
        return page.Data.Select(e => e.FullName.Split(' ')[0]).ToList();
    }

    [Fact]
    public async Task All_sees_everyone()
    {
        var (h, _, _, _, _, _, _, _) = await WorldAsync();
        (await SeenAsync(h, P.HRM_SCOPE_ALL)).Should().HaveCount(6);
    }

    [Fact]
    public async Task No_scope_code_and_no_employee_sees_nobody()
    {
        var (h, _, _, _, _, _, _, _) = await WorldAsync();
        (await SeenAsync(h)).Should().BeEmpty();
    }

    [Fact]
    public async Task Self_sees_only_self()
    {
        var (h, _, _, _, _, _, _, _) = await WorldAsync();
        (await SeenAsAsync(h, 3)).Should().Equal("Deep");
    }

    [Fact]
    public async Task A_manager_sees_their_whole_reporting_line_and_themselves()
    {
        var (h, _, _, _, _, _, _, _) = await WorldAsync();
        (await SeenAsAsync(h, 1)).Should().BeEquivalentTo(["Boss", "Report", "Deep"]);
        (await SeenAsAsync(h, 2)).Should().BeEquivalentTo(["Report", "Deep"]);
    }

    [Fact]
    public async Task Department_scope_includes_children_of_the_callers_own_department()
    {
        var (h, _, _, _, _, _, _, _) = await WorldAsync();
        (await SeenAsAsync(h, 3, P.HRM_SCOPE_DEPARTMENT)).Should().BeEquivalentTo(["Boss", "Report", "Deep", "Field"]);
    }

    [Fact]
    public async Task Branch_scope_is_the_callers_own_branch()
    {
        var (h, _, _, _, _, _, _, _) = await WorldAsync();
        (await SeenAsAsync(h, 3, P.HRM_SCOPE_BRANCH)).Should().BeEquivalentTo(["Boss", "Report", "Deep", "Field", "Sales"]);
    }

    [Fact]
    public async Task An_explicit_assignment_extends_the_scope_and_removing_it_takes_it_back()
    {
        var (h, b, _, _, _, _, _, _) = await WorldAsync();
        var added = await h.UserScopes.AddAsync(h.Admin(), new HrUserScopeCreate { UserId = 999, BranchUuid = b.Lahore });
        added.ScopeType.Should().Be(HrScopeTypes.Branch);
        (await SeenAsync(h)).Should().Equal("Lahore");

        // A second call is the same assignment, not a second row.
        (await h.UserScopes.AddAsync(h.Admin(), new HrUserScopeCreate { UserId = 999, BranchUuid = b.Lahore })).Uuid.Should().Be(added.Uuid);

        await h.UserScopes.RemoveAsync(h.Admin(), added.Uuid);
        (await SeenAsync(new HrmHarness(h.Org, h.DbName))).Should().BeEmpty();
    }

    [Fact]
    public async Task Outside_the_scope_is_404_not_403()
    {
        var (h, _, _, _, _, _, sales, _) = await WorldAsync();
        var read = () => h.Employees.GetAsync(h.As(3, P.HRM_EMPLOYEE_VIEW), sales.Uuid);
        await read.Should().ThrowAsync<NotFoundException>();
        var bank = () => h.Bank.ListAsync(h.As(3, P.HRM_BANK_VIEW), sales.Uuid);
        await bank.Should().ThrowAsync<NotFoundException>();
        var record = () => h.Employment.ListAsync(h.As(3, P.HRM_EMPLOYEE_VIEW), sales.Uuid);
        await record.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Another_organization_is_404_even_with_all_scope()
    {
        var (h, _, boss, _, _, _, _, _) = await WorldAsync();
        var other = new HrmHarness(dbName: h.DbName);
        var read = () => other.Employees.GetAsync(other.Admin(), boss.Uuid);
        await read.Should().ThrowAsync<NotFoundException>();
        (await other.Employees.ListAsync(other.Admin(), new EmployeeListFilter())).Data.Should().BeEmpty();
    }

    [Fact]
    public async Task Me_returns_own_record_and_a_user_without_one_gets_the_coded_404()
    {
        var (h, _, _, report, _, _, _, _) = await WorldAsync();
        (await h.Employees.GetOwnAsync(h.As(2, P.HRM_SELF_SERVICE))).Uuid.Should().Be(report.Uuid);
        var none = () => h.Employees.GetOwnAsync(h.As(777, P.HRM_SELF_SERVICE));
        (await none.Should().ThrowAsync<NotFoundException>()).Which.Message.Should().StartWith("HRM_NO_EMPLOYEE:");
    }

    [Fact]
    public async Task CanAccess_follows_the_scope()
    {
        var (h, _, _, _, deep, _, sales, _) = await WorldAsync();
        var deepId = (await h.Db.Employees.FirstAsync(e => e.Uuid == deep.Uuid)).Id;
        var salesId = (await h.Db.Employees.FirstAsync(e => e.Uuid == sales.Uuid)).Id;
        (await h.Scope.CanAccessAsync(h.As(1), deepId)).Should().BeTrue();
        (await h.Scope.CanAccessAsync(h.As(1), salesId)).Should().BeFalse();
    }
}

public sealed class SeedTests
{
    [Fact]
    public async Task Seeding_is_idempotent_and_touches_no_other_organization()
    {
        var h = new HrmHarness();
        await h.Seeder.EnsureAsync(h.Org);
        var counts = await CountsAsync(h);
        counts.Leave.Should().Be(6);
        counts.Components.Should().Be(19);

        await new HrmHarness(h.Org, h.DbName).Seeder.EnsureAsync(h.Org);
        (await CountsAsync(h)).Should().Be(counts);

        var other = new HrmHarness(dbName: h.DbName);
        (await other.Db.LeaveTypes.IgnoreQueryFilters().CountAsync(x => x.OrganizationId == other.Org)).Should().Be(0);
        await other.Seeder.EnsureAsync(other.Org);
        (await other.Db.LeaveTypes.IgnoreQueryFilters().CountAsync(x => x.OrganizationId == other.Org)).Should().Be(6);
    }

    private static async Task<(int Leave, int Components, int Holidays, int Categories)> CountsAsync(HrmHarness h) => (
        await h.Db.LeaveTypes.CountAsync(), await h.Db.SalaryComponents.CountAsync(), await h.Db.Holidays.CountAsync(), await h.Db.ExpenseCategories.CountAsync());

    [Fact]
    public async Task G21_tax_and_statutory_seeds_are_never_verified_and_say_so()
    {
        var h = new HrmHarness();
        await h.Seeder.EnsureAsync(h.Org);
        var tables = await h.Db.TaxSlabTables.Include(t => t.Slabs).ToListAsync();
        tables.Should().NotBeEmpty().And.OnlyContain(t => !t.IsVerified && t.SourceNote!.StartsWith("UNVERIFIED") && t.Name.Contains("UNVERIFIED"));
        tables[0].Slabs.Should().HaveCountGreaterThan(1);
        var rates = await h.Db.StatutoryRates.ToListAsync();
        rates.Should().HaveCount(4).And.OnlyContain(r => !r.IsVerified && r.SourceNote!.StartsWith("UNVERIFIED") && r.Name.Contains("UNVERIFIED"));
    }

    [Fact]
    public async Task Moon_dependent_holidays_are_flagged_estimated_and_fixed_ones_are_not()
    {
        var h = new HrmHarness();
        await h.Seeder.EnsureAsync(h.Org);
        var holidays = await h.Db.Holidays.ToListAsync();
        holidays.Where(x => x.Name.Contains("Eid") || x.Name.Contains("Ashura")).Should().NotBeEmpty().And.OnlyContain(x => x.IsEstimated);
        holidays.Where(x => x.Name == "Independence Day").Should().HaveCount(2).And.OnlyContain(x => !x.IsEstimated);
        holidays.Select(x => x.Date.Year).Distinct().Should().BeEquivalentTo([2026, 2027]);
    }

    [Fact]
    public async Task Components_carry_real_accounting_roles_and_the_structure_lists_them()
    {
        var h = new HrmHarness();
        await h.Seeder.EnsureAsync(h.Org);
        var components = await h.Db.SalaryComponents.ToListAsync();
        components.Should().OnlyContain(c => c.PostingRole!.StartsWith("DEFAULT_"));
        components.Single(c => c.Code == "EOBI_ER").ExpenseRole.Should().Be("DEFAULT_EMPLOYER_CONTRIB_EXPENSE");
        components.Single(c => c.Code == "ITAX").Formula.Should().Be("SLAB(TAXABLE_INCOME)");

        var structure = await h.Db.SalaryStructures.Include(s => s.Lines).ThenInclude(l => l.Component).SingleAsync();
        structure.IsDefault.Should().BeTrue();
        structure.Lines.OrderBy(l => l.Sequence).First().Component!.Code.Should().Be("BASIC");
    }

    [Fact]
    public async Task Expense_categories_seed_without_an_account_so_nothing_points_at_a_code_that_may_not_exist()
    {
        var h = new HrmHarness();
        await h.Seeder.EnsureAsync(h.Org);
        var categories = await h.Db.ExpenseCategories.ToListAsync();
        categories.Should().HaveCount(6).And.OnlyContain(c => c.AccountCode == null);
    }

    [Fact]
    public async Task A_row_removed_by_hand_is_not_brought_back_once_seeded()
    {
        var h = new HrmHarness();
        await h.Seeder.EnsureAsync(h.Org);
        var casual = await h.Db.LeaveTypes.Include(t => t.Policies).SingleAsync(t => t.Code == "CASUAL");
        h.Db.LeaveTypes.Remove(casual);
        await h.Db.SaveChangesAsync();
        await new HrmHarness(h.Org, h.DbName).Seeder.EnsureAsync(h.Org);
        (await h.Db.LeaveTypes.CountAsync(t => t.Code == "CASUAL")).Should().Be(0);
    }

    [Fact]
    public async Task Append_only_tables_refuse_changes()
    {
        var h = new HrmHarness();
        var b = await h.BaseAsync();
        var e = await h.EmployeeAsync(b, cnic: "42101-1234567-3");
        await h.Employees.RevealAsync(h.Admin(), e.Uuid);
        var log = await h.Db.SensitiveAccessLogs.FirstAsync();
        log.Purpose = "tampered";
        var save = () => h.Db.SaveChangesAsync();
        await save.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void Registration_is_complete_every_interface_has_a_mapped_table()
    {
        var h = new HrmHarness();
        var types = h.Db.Model.GetEntityTypes().ToList();
        types.Should().HaveCount(58);
        types.Should().OnlyContain(t => t.GetSchema() == "hrm");
        types.Should().OnlyContain(t => t.GetQueryFilter() != null, "the tenant filter applies to every entity");
        types.SelectMany(t => t.GetForeignKeys()).Should().OnlyContain(fk => fk.PrincipalEntityType.GetSchema() == "hrm", "task 073: no cross-schema FK");
    }
}

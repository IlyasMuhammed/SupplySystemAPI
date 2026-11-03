using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Auth.Data;
using SMS.Modules.Auth.Domain;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Auth.Tests;

/// <summary>A49 G-22 / task 070: HR codes in the catalog, HR Manager + HR Officer roles, the grants by role code, what the System Admin must not get.</summary>
public sealed class HrmPermissionSeedTests
{
    private static AuthDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new StaticTenantContext { IsSuperAdmin = true });

    private static async Task SeedAsync(AuthDbContext db)
    {
        var seeder = new AuthDataSeeder(db, new PasswordHasher<UserAccount>());
        foreach (var step in new[] { "SeedPermissionsAsync", "SeedRolesAsync", "SeedRolePermissionsAsync" })
            await (Task)typeof(AuthDataSeeder).GetMethod(step, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(seeder, null)!;
        await seeder.SeedHrmGrantsAsync();
    }

    private static async Task<List<string>> GrantsAsync(AuthDbContext db, string roleCode)
    {
        var role = await db.Roles.IgnoreQueryFilters().SingleAsync(r => r.RoleCode == roleCode);
        return await db.RolePermissions.IgnoreQueryFilters().Where(rp => rp.RoleID == role.RoleID && rp.IsAllowed)
            .Join(db.Permissions, rp => rp.PermissionID, p => p.PermissionID, (rp, p) => p.Code)
            .Where(c => c.StartsWith("HRM_")).ToListAsync();
    }

    [Fact]
    public async Task Every_hr_code_exists_with_its_module_and_the_catalog_has_no_extras()
    {
        await using var db = NewDb();
        await SeedAsync(db);
        var hr = await db.Permissions.Where(p => p.Code.StartsWith("HRM_")).ToListAsync();
        hr.Select(p => p.Code).Should().BeEquivalentTo(PermissionCodes.HrmCodes);
        hr.Should().OnlyContain(p => p.ModuleCode == ModuleCodes.Hrm);
        PermissionCodes.All.Should().Contain(PermissionCodes.HrmCodes);
    }

    [Fact]
    public async Task Hr_manager_has_everything_and_hr_officer_no_money_or_identity_codes()
    {
        await using var db = NewDb();
        await SeedAsync(db);
        (await GrantsAsync(db, "HR_MANAGER")).Should().BeEquivalentTo(PermissionCodes.HrmCodes);

        var officer = await GrantsAsync(db, "HR_OFFICER");
        officer.Should().Contain([PermissionCodes.HRM_EMPLOYEE_EDIT, PermissionCodes.HRM_LEAVE_MANAGE, PermissionCodes.HRM_SELF_SERVICE]);
        officer.Should().NotContain([PermissionCodes.HRM_EMPLOYEE_SENSITIVE_VIEW, PermissionCodes.HRM_BANK_VIEW, PermissionCodes.HRM_SALARY_VIEW, PermissionCodes.HRM_PAYROLL_VIEW]);
    }

    [Fact]
    public async Task Org_admin_has_every_hr_code_and_system_admin_none_of_the_sensitive_ones()
    {
        await using var db = NewDb();
        await SeedAsync(db);
        (await GrantsAsync(db, "ORG_ADMIN")).Should().BeEquivalentTo(PermissionCodes.HrmCodes);

        var system = await GrantsAsync(db, "SYSTEM_ADMIN");
        system.Should().NotContain(PermissionCodes.HrmSensitiveCodes);
        system.Should().Contain(PermissionCodes.HRM_ORG_VIEW);
    }

    [Fact]
    public async Task G22_finance_and_auditor_grants_and_self_service_for_every_role()
    {
        await using var db = NewDb();
        await SeedAsync(db);
        (await GrantsAsync(db, "FINANCE_OFFICER")).Should().Contain([PermissionCodes.HRM_SETTINGS_MANAGE, PermissionCodes.HRM_ACCOUNT_MAPPING_VIEW, PermissionCodes.HRM_ACCOUNT_MAPPING_EDIT]);
        var auditor = await GrantsAsync(db, "AUDITOR");
        auditor.Should().Contain([PermissionCodes.HRM_PAYROLL_VIEW, PermissionCodes.HRM_REPORTS_VIEW, PermissionCodes.HRM_ACCOUNT_MAPPING_VIEW]);
        auditor.Should().NotContain([PermissionCodes.HRM_EMPLOYEE_EDIT, PermissionCodes.HRM_PAYROLL_RUN, PermissionCodes.HRM_EMPLOYEE_SENSITIVE_VIEW]);

        foreach (var role in await db.Roles.IgnoreQueryFilters().Select(r => r.RoleCode).ToListAsync())
            (await GrantsAsync(db, role)).Should().Contain(PermissionCodes.HRM_SELF_SERVICE, role);
    }

    [Fact]
    public async Task A_second_start_adds_nothing_and_a_grant_switched_off_stays_off()
    {
        await using var db = NewDb();
        await SeedAsync(db);
        var role = await db.Roles.IgnoreQueryFilters().SingleAsync(r => r.RoleCode == "FINANCE_OFFICER");
        var perm = await db.Permissions.SingleAsync(p => p.Code == PermissionCodes.HRM_SETTINGS_MANAGE);
        var grant = await db.RolePermissions.IgnoreQueryFilters().SingleAsync(rp => rp.RoleID == role.RoleID && rp.PermissionID == perm.PermissionID);
        grant.IsAllowed = false;
        await db.SaveChangesAsync();
        var before = await db.RolePermissions.IgnoreQueryFilters().CountAsync();

        await SeedAsync(db);

        (await db.RolePermissions.IgnoreQueryFilters().CountAsync()).Should().Be(before);
        (await GrantsAsync(db, "FINANCE_OFFICER")).Should().NotContain(PermissionCodes.HRM_SETTINGS_MANAGE);
    }
}

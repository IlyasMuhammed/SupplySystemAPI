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

/// <summary>A41 D-3 / D-4 — the 21 CRM_* codes (module, role-editor group) and the Sales Representative / Sales Manager roles.</summary>
public class CrmPermissionSeedTests
{
    private static object Field(string name) =>
        typeof(AuthDataSeeder).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static string Grouping(string code) =>
        (string)typeof(SMS.Modules.Auth.Repositories.AuthRepository)
            .GetMethod("GetPermissionModule", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [code])!;

    private static readonly string[] CrmCodes = PermissionCodes.All.Where(c => c.StartsWith("CRM_")).ToArray();

    [Fact]
    public void Codes_are_in_the_catalog_seeded_once_mapped_to_the_crm_and_grouped()
    {
        CrmCodes.Should().HaveCount(21);
        var seed = ((string Name, string Code, string Description)[])Field("PermissionSeed");
        foreach (var code in CrmCodes)
        {
            seed.Should().ContainSingle(p => p.Code == code).Which.Description.Should().NotBeNullOrWhiteSpace();
            ModuleCodeMap.ForPermission(code).Should().Be(ModuleCodes.Crm);
            Grouping(code).Should().Be("CRM");
        }
        PermissionCodes.All.Should().OnlyHaveUniqueItems();
    }

    private static AuthDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new StaticTenantContext { IsSuperAdmin = true });

    private static async Task SeedAsync(AuthDbContext db)
    {
        var seeder = new AuthDataSeeder(db, new PasswordHasher<UserAccount>());
        foreach (var step in new[] { "SeedPermissionsAsync", "SeedRolesAsync", "SeedRolePermissionsAsync" })
            await (Task)typeof(AuthDataSeeder).GetMethod(step, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(seeder, null)!;
    }

    private static async Task<List<string>> GrantsAsync(AuthDbContext db, string roleCode)
    {
        var roleId = (await db.Roles.IgnoreQueryFilters().SingleAsync(r => r.RoleCode == roleCode)).RoleID;
        return await db.RolePermissions.IgnoreQueryFilters().Where(rp => rp.RoleID == roleId && rp.IsAllowed)
            .Join(db.Permissions, rp => rp.PermissionID, p => p.PermissionID, (rp, p) => p.Code).ToListAsync();
    }

    [Fact]
    public async Task The_sales_roles_are_seeded_with_the_appendix_A_grants()
    {
        var db = NewDb();
        await SeedAsync(db);

        var rep = await GrantsAsync(db, "SALES_REP");
        rep.Should().Contain(["CRM_VIEW_LEADS", "CRM_CREATE_LEAD", "CRM_EDIT_LEAD", "CRM_QUALIFY_LEAD", "CRM_CONVERT_LEAD", "CRM_DISQUALIFY_LEAD",
            "CRM_VIEW_OPPORTUNITIES", "CRM_EDIT_OPPORTUNITY", "CRM_CLOSE_OPPORTUNITY", "CRM_VIEW_ACTIVITIES", "CRM_CREATE_ACTIVITY",
            "CRM_EDIT_ACTIVITY", "CRM_EXPORT_TIMELINE", "CRM_VIEW_CAMPAIGNS", "SALE_INQUIRY_CREATE", "SALE_QUOTATION_CREATE", "SALE_ORDER_CREATE"]);
        rep.Should().NotContain(["CRM_ASSIGN_LEAD", "CRM_REOPEN_LEAD", "CRM_DELETE_LEAD", "CRM_REOPEN_OPPORTUNITY", "CRM_DELETE_ACTIVITY",
            "CRM_MANAGE_CAMPAIGNS", "CRM_ADMIN"]);

        var manager = await GrantsAsync(db, "SALES_MANAGER");
        manager.Where(c => c.StartsWith("CRM_")).Should().BeEquivalentTo(CrmCodes.Except(["CRM_ADMIN"]));

        (await GrantsAsync(db, "ORG_ADMIN")).Should().Contain(CrmCodes);
        (await GrantsAsync(db, "SYSTEM_ADMIN")).Should().Contain(CrmCodes);
        (await GrantsAsync(db, "FINANCE_MANAGER")).Should().NotContain(c => c.StartsWith("CRM_"), "no existing role is given CRM codes");
        (await db.Permissions.Where(p => p.Code.StartsWith("CRM_")).Select(p => p.ModuleCode).Distinct().ToListAsync()).Should().Equal(ModuleCodes.Crm);
    }

    [Fact]
    public async Task The_read_only_auditor_sees_crm_but_can_change_nothing_and_a_switched_off_grant_stays_off()
    {
        var db = NewDb();
        await SeedAsync(db);
        var seeder = new AuthDataSeeder(db, new PasswordHasher<UserAccount>());
        await seeder.SeedCrmViewGrantsAsync();

        (await GrantsAsync(db, "AUDITOR")).Where(c => c.StartsWith("CRM_")).Should().BeEquivalentTo(
            ["CRM_VIEW_LEADS", "CRM_VIEW_OPPORTUNITIES", "CRM_VIEW_ACTIVITIES", "CRM_VIEW_CAMPAIGNS", "CRM_EXPORT_TIMELINE"]);

        // Idempotent, and an administrator's switch-off is respected on the next start.
        var roleId = (await db.Roles.IgnoreQueryFilters().SingleAsync(r => r.RoleCode == "AUDITOR")).RoleID;
        var perm = await db.Permissions.SingleAsync(p => p.Code == "CRM_VIEW_CAMPAIGNS");
        (await db.RolePermissions.IgnoreQueryFilters().SingleAsync(rp => rp.RoleID == roleId && rp.PermissionID == perm.PermissionID)).IsAllowed = false;
        await db.SaveChangesAsync();
        var rows = await db.RolePermissions.IgnoreQueryFilters().CountAsync();
        await seeder.SeedCrmViewGrantsAsync();
        (await db.RolePermissions.IgnoreQueryFilters().CountAsync()).Should().Be(rows);
        (await GrantsAsync(db, "AUDITOR")).Should().NotContain("CRM_VIEW_CAMPAIGNS");
    }

    [Fact]
    public async Task Re_seeding_adds_no_second_sales_role()
    {
        var db = NewDb();
        await SeedAsync(db);
        var roles = await db.Roles.IgnoreQueryFilters().CountAsync();
        await SeedAsync(db);
        (await db.Roles.IgnoreQueryFilters().CountAsync()).Should().Be(roles);
        (await db.Roles.IgnoreQueryFilters().CountAsync(r => r.RoleCode == "SALES_REP")).Should().Be(1);
    }
}

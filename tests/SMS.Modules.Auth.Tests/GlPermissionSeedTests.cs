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

/// <summary>A42 D-14 — GL_VIEW / GL_ACCOUNT_MANAGE / GL_SETUP_MANAGE: catalog, module (MODULE_FINANCE), role-editor group,
/// grants by role code and by what a (custom) role holds, only missing grants added, a switched-off grant kept off.</summary>
public class GlPermissionSeedTests
{
    private static readonly string[] GlCodes = ["GL_VIEW", "GL_ACCOUNT_MANAGE", "GL_SETUP_MANAGE"];

    private static object Field(string name) =>
        typeof(AuthDataSeeder).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static string Grouping(string code) =>
        (string)typeof(SMS.Modules.Auth.Repositories.AuthRepository)
            .GetMethod("GetPermissionModule", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [code])!;

    [Fact]
    public void Codes_are_in_the_catalog_seeded_once_mapped_to_finance_and_grouped()
    {
        PermissionCodes.All.Where(c => c.StartsWith("GL_")).Should().BeEquivalentTo(GlCodes);
        new[] { PermissionCodes.GL_VIEW, PermissionCodes.GL_ACCOUNT_MANAGE, PermissionCodes.GL_SETUP_MANAGE }
            .Should().Equal(AccountingCodes.GlView, AccountingCodes.GlAccountManage, AccountingCodes.GlSetupManage);
        var seed = ((string Name, string Code, string Description)[])Field("PermissionSeed");
        foreach (var code in GlCodes)
        {
            seed.Should().ContainSingle(p => p.Code == code).Which.Description.Should().NotBeNullOrWhiteSpace();
            ModuleCodeMap.ForPermission(code).Should().Be(ModuleCodes.Finance);
            Grouping(code).Should().Be("General Ledger");
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
        await seeder.SeedCurrencyGrantsAsync();
        await seeder.SeedCustomerGrantsAsync();
        await seeder.SeedImportGrantsAsync();
        await seeder.SeedGlGrantsAsync();
    }

    private static async Task<List<string>> GlGrantsAsync(AuthDbContext db, string roleCode)
    {
        var roleId = (await db.Roles.IgnoreQueryFilters().SingleAsync(r => r.RoleCode == roleCode)).RoleID;
        return await db.RolePermissions.IgnoreQueryFilters().Where(rp => rp.RoleID == roleId && rp.IsAllowed)
            .Join(db.Permissions, rp => rp.PermissionID, p => p.PermissionID, (rp, p) => p.Code)
            .Where(c => c.StartsWith("GL_")).ToListAsync();
    }

    [Theory]
    [InlineData("SYSTEM_ADMIN", "GL_VIEW,GL_ACCOUNT_MANAGE,GL_SETUP_MANAGE")]
    [InlineData("ORG_ADMIN", "GL_VIEW,GL_ACCOUNT_MANAGE,GL_SETUP_MANAGE")]
    [InlineData("FINANCE_MANAGER", "GL_VIEW,GL_ACCOUNT_MANAGE,GL_SETUP_MANAGE")]
    [InlineData("FINANCE_OFFICER", "GL_VIEW")]
    [InlineData("AUDITOR", "GL_VIEW")]
    [InlineData("PROCUREMENT_MANAGER", "")]
    [InlineData("INVENTORY_MANAGER", "")]
    [InlineData("REQUESTER", "")]
    [InlineData("POS_CASHIER", "")]
    [InlineData("SALES_REP", "")]
    public async Task Each_built_in_role_gets_its_gl_grants(string roleCode, string expected)
    {
        var db = NewDb();
        await SeedAsync(db);
        (await GlGrantsAsync(db, roleCode)).Should().BeEquivalentTo(expected.Split(',', StringSplitOptions.RemoveEmptyEntries));
        (await db.Permissions.Where(p => p.Code.StartsWith("GL_")).Select(p => p.ModuleCode).Distinct().ToListAsync())
            .Should().Equal(ModuleCodes.Finance);
    }

    [Fact]
    public async Task Custom_roles_get_grants_from_what_they_hold()
    {
        var db = NewDb();
        await SeedAsync(db);
        var org = Guid.NewGuid();
        db.Roles.AddRange(
            new Role { RoleID = 900, Name = "Controller", RoleCode = "CTRL", IsGlobal = false, OrganizationId = org },
            new Role { RoleID = 901, Name = "AR Clerk", RoleCode = "ARC", IsGlobal = false, OrganizationId = org },
            new Role { RoleID = 902, Name = "Off", RoleCode = "OFF", IsGlobal = false, OrganizationId = org });
        var perm = await db.Permissions.ToDictionaryAsync(p => p.Code, p => p.PermissionID);
        db.RolePermissions.AddRange(
            new RolePermission { RoleID = 900, PermissionID = perm[PermissionCodes.FINANCE_SETUP_MANAGE], IsAllowed = true, OrganizationId = org },
            new RolePermission { RoleID = 901, PermissionID = perm[PermissionCodes.SALES_INVOICE_VIEW], IsAllowed = true, OrganizationId = org },
            new RolePermission { RoleID = 902, PermissionID = perm[PermissionCodes.INVOICE_VIEW], IsAllowed = false, OrganizationId = org });
        await db.SaveChangesAsync();

        await new AuthDataSeeder(db, new PasswordHasher<UserAccount>()).SeedGlGrantsAsync();

        (await GlGrantsAsync(db, "CTRL")).Should().BeEquivalentTo(GlCodes);
        (await GlGrantsAsync(db, "ARC")).Should().BeEquivalentTo(["GL_VIEW"]);
        (await GlGrantsAsync(db, "OFF")).Should().BeEmpty("a switched-off source grant gives nothing");
    }

    [Fact]
    public async Task Re_seeding_adds_nothing_and_keeps_a_switched_off_grant_off()
    {
        var db = NewDb();
        await SeedAsync(db);
        var roleId = (await db.Roles.IgnoreQueryFilters().SingleAsync(r => r.RoleCode == "FINANCE_MANAGER")).RoleID;
        var perm = await db.Permissions.SingleAsync(p => p.Code == "GL_SETUP_MANAGE");
        (await db.RolePermissions.IgnoreQueryFilters().SingleAsync(rp => rp.RoleID == roleId && rp.PermissionID == perm.PermissionID)).IsAllowed = false;
        await db.SaveChangesAsync();
        var rows = await db.RolePermissions.IgnoreQueryFilters().CountAsync();

        await SeedAsync(db);

        (await db.RolePermissions.IgnoreQueryFilters().CountAsync()).Should().Be(rows);
        (await GlGrantsAsync(db, "FINANCE_MANAGER")).Should().NotContain("GL_SETUP_MANAGE");
    }
}

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

/// <summary>A40 D-9 / D-11 (IMP) — the six IMPORT_* codes, their module and role-editor group, grants by role CODE, only
/// missing grants added, an administrator's switch-off kept.</summary>
public class ImportPermissionSeedTests
{
    private static readonly string[] ImportCodes =
        ["IMPORT_PROFILE_VIEW", "IMPORT_PROFILE_MANAGE", "IMPORT_UPLOAD", "IMPORT_EXECUTE", "IMPORT_MANAGE", "IMPORT_OPENING_BALANCE"];

    private static object Field(string name) =>
        typeof(AuthDataSeeder).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static string Grouping(string code) =>
        (string)typeof(SMS.Modules.Auth.Repositories.AuthRepository)
            .GetMethod("GetPermissionModule", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [code])!;

    [Fact]
    public void Codes_are_in_the_catalog_seeded_once_mapped_to_data_import_and_grouped()
    {
        PermissionCodes.All.Where(c => c.StartsWith("IMPORT_")).Should().BeEquivalentTo(ImportCodes);
        var seed = ((string Name, string Code, string Description)[])Field("PermissionSeed");
        foreach (var code in ImportCodes)
        {
            seed.Should().ContainSingle(p => p.Code == code).Which.Description.Should().NotBeNullOrWhiteSpace();
            ModuleCodeMap.ForPermission(code).Should().Be(ModuleCodes.DataImport);
            Grouping(code).Should().Be("Data Import");
        }
        PermissionCodes.All.Except(ImportCodes).Select(ModuleCodeMap.ForPermission).Should().NotContain(ModuleCodes.DataImport);
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
    }

    private static async Task<List<string>> ImportGrantsAsync(AuthDbContext db, string roleCode)
    {
        var roleId = (await db.Roles.IgnoreQueryFilters().SingleAsync(r => r.RoleCode == roleCode)).RoleID;
        return await db.RolePermissions.IgnoreQueryFilters().Where(rp => rp.RoleID == roleId && rp.IsAllowed)
            .Join(db.Permissions, rp => rp.PermissionID, p => p.PermissionID, (rp, p) => p.Code)
            .Where(c => c.StartsWith("IMPORT_")).ToListAsync();
    }

    [Theory]
    [InlineData("SYSTEM_ADMIN", "IMPORT_PROFILE_VIEW,IMPORT_PROFILE_MANAGE,IMPORT_UPLOAD,IMPORT_EXECUTE,IMPORT_MANAGE,IMPORT_OPENING_BALANCE")]
    [InlineData("ORG_ADMIN", "IMPORT_PROFILE_VIEW,IMPORT_PROFILE_MANAGE,IMPORT_UPLOAD,IMPORT_EXECUTE,IMPORT_MANAGE,IMPORT_OPENING_BALANCE")]
    [InlineData("FINANCE_MANAGER", "IMPORT_PROFILE_VIEW,IMPORT_UPLOAD,IMPORT_EXECUTE,IMPORT_OPENING_BALANCE")]
    [InlineData("FINANCE_OFFICER", "IMPORT_PROFILE_VIEW,IMPORT_UPLOAD,IMPORT_EXECUTE")]
    [InlineData("INVENTORY_MANAGER", "IMPORT_PROFILE_VIEW,IMPORT_UPLOAD,IMPORT_EXECUTE")]
    [InlineData("PROCUREMENT_MANAGER", "IMPORT_PROFILE_VIEW,IMPORT_UPLOAD,IMPORT_EXECUTE")]
    [InlineData("PURCHASE_OFFICER", "IMPORT_PROFILE_VIEW,IMPORT_UPLOAD")]
    [InlineData("WAREHOUSE_OPERATOR", "IMPORT_PROFILE_VIEW,IMPORT_UPLOAD")]
    [InlineData("REQUESTER", "IMPORT_PROFILE_VIEW,IMPORT_UPLOAD")]
    [InlineData("SUPPLY_DEPT_ADMIN", "IMPORT_PROFILE_VIEW,IMPORT_UPLOAD")]
    [InlineData("AUDITOR", "")]
    [InlineData("POS_CASHIER", "")]
    [InlineData("STORE_MANAGER", "")]
    public async Task Each_built_in_role_gets_its_import_grants(string roleCode, string expected)
    {
        var db = NewDb();
        await SeedAsync(db);
        (await ImportGrantsAsync(db, roleCode)).Should().BeEquivalentTo(expected.Split(',', StringSplitOptions.RemoveEmptyEntries));
        (await db.Permissions.Where(p => p.Code.StartsWith("IMPORT_")).Select(p => p.ModuleCode).Distinct().ToListAsync())
            .Should().Equal(ModuleCodes.DataImport);
    }

    [Fact]
    public async Task Re_seeding_adds_nothing_and_keeps_a_switched_off_grant_off()
    {
        var db = NewDb();
        await SeedAsync(db);
        var roleId = (await db.Roles.IgnoreQueryFilters().SingleAsync(r => r.RoleCode == "FINANCE_MANAGER")).RoleID;
        var perm = await db.Permissions.SingleAsync(p => p.Code == "IMPORT_OPENING_BALANCE");
        (await db.RolePermissions.IgnoreQueryFilters().SingleAsync(rp => rp.RoleID == roleId && rp.PermissionID == perm.PermissionID)).IsAllowed = false;
        await db.SaveChangesAsync();
        var rows = await db.RolePermissions.IgnoreQueryFilters().CountAsync();

        await SeedAsync(db);

        (await db.RolePermissions.IgnoreQueryFilters().CountAsync()).Should().Be(rows);
        (await ImportGrantsAsync(db, "FINANCE_MANAGER")).Should().NotContain("IMPORT_OPENING_BALANCE");
    }

    [Fact]
    public async Task A_custom_role_squatting_a_seed_id_gains_no_import_grant()
    {
        var db = NewDb();
        db.Roles.Add(new Role { RoleID = (int)EnumRole.FinanceManager, Name = "Squatter", RoleCode = "SQUAT", IsGlobal = false, OrganizationId = Guid.NewGuid() });
        await db.SaveChangesAsync();
        await SeedAsync(db);
        (await ImportGrantsAsync(db, "SQUAT")).Should().BeEmpty();
        (await ImportGrantsAsync(db, "FINANCE_MANAGER")).Should().Contain("IMPORT_OPENING_BALANCE");
    }
}

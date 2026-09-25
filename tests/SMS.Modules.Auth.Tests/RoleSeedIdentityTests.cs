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

/// <summary>
/// SeedRolesAsync/SeedRolePermissionsAsync identify a built-in role by its RoleCode, not by the
/// literal id RoleSeed happens to carry for it — proven against the exact failure this codebase hit
/// for real: SUPPLY_DEPT_ADMIN's intended id (11) was already a hand-created "Test Role" in the
/// shared dev database, so the old, position-based seeder silently granted that unrelated role
/// SALE_ORDER_CONFIG_WRITE instead of ever creating "Supply Department Administrator".
/// <para>
/// AuthDataSeeder.SeedAsync() opens with Database.MigrateAsync(), unsupported by the InMemory
/// provider (see SaleOrderAdminRoleSeedTests), so this invokes the two private seeding methods
/// directly via reflection, the same way that file reads the seed data.
/// </para>
/// </summary>
public class RoleSeedIdentityTests
{
    private static readonly Type SeederType = typeof(AuthDataSeeder);

    private static AuthDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(name).Options,
            new StaticTenantContext { IsSuperAdmin = true });

    private static AuthDataSeeder NewSeeder(AuthDbContext db) =>
        new(db, new PasswordHasher<UserAccount>());

    private static Task InvokePrivateAsync(AuthDataSeeder seeder, string methodName) =>
        (Task)SeederType.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(seeder, null)!;

    private static async Task SeedPermissionsOnlyAsync(AuthDataSeeder seeder) =>
        await InvokePrivateAsync(seeder, "SeedPermissionsAsync");

    private static async Task SeedRolesOnlyAsync(AuthDataSeeder seeder) =>
        await InvokePrivateAsync(seeder, "SeedRolesAsync");

    private static async Task SeedRolePermissionsOnlyAsync(AuthDataSeeder seeder) =>
        await InvokePrivateAsync(seeder, "SeedRolePermissionsAsync");

    [Fact]
    public async Task A_role_already_sitting_on_a_built_in_roles_id_under_another_code_does_not_stop_that_built_in_role_being_created()
    {
        var db = NewDb(Guid.NewGuid().ToString());
        // Exactly what SMSGlobal had: id 11 (SupplyDeptAdmin's seed id) already taken by an
        // unrelated, hand-created role.
        db.Roles.Add(new Role
        {
            RoleID = (int)EnumRole.SupplyDeptAdmin, Name = "Test Role", RoleCode = "TEST_ROLE",
            IsActive = true, IsGlobal = true
        });
        await db.SaveChangesAsync();

        var seeder = NewSeeder(db);
        await SeedRolesOnlyAsync(seeder);

        var squatter = await db.Roles.SingleAsync(r => r.RoleCode == "TEST_ROLE");
        squatter.RoleID.Should().Be((int)EnumRole.SupplyDeptAdmin, "the pre-existing role must not be touched");
        squatter.Name.Should().Be("Test Role");

        var supplyDeptAdmin = await db.Roles.SingleAsync(r => r.RoleCode == "SUPPLY_DEPT_ADMIN");
        supplyDeptAdmin.Name.Should().Be("Supply Department Administrator");
        supplyDeptAdmin.RoleID.Should().NotBe((int)EnumRole.SupplyDeptAdmin, "that id was already taken");
    }

    [Fact]
    public async Task Running_role_seeding_twice_creates_nothing_a_second_time()
    {
        var db = NewDb(Guid.NewGuid().ToString());
        db.Roles.Add(new Role { RoleID = (int)EnumRole.SupplyDeptAdmin, Name = "Test Role", RoleCode = "TEST_ROLE", IsActive = true, IsGlobal = true });
        await db.SaveChangesAsync();
        var seeder = NewSeeder(db);
        await SeedRolesOnlyAsync(seeder);
        var afterFirst = await db.Roles.CountAsync();

        await SeedRolesOnlyAsync(seeder);

        (await db.Roles.CountAsync()).Should().Be(afterFirst);
    }

    [Fact]
    public async Task The_permissions_seeded_for_a_bumped_role_land_on_the_role_actually_created_not_the_id_squatter()
    {
        var db = NewDb(Guid.NewGuid().ToString());
        db.Roles.Add(new Role { RoleID = (int)EnumRole.SupplyDeptAdmin, Name = "Test Role", RoleCode = "TEST_ROLE", IsActive = true, IsGlobal = true });
        await db.SaveChangesAsync();
        var seeder = NewSeeder(db);

        await SeedPermissionsOnlyAsync(seeder);
        await SeedRolesOnlyAsync(seeder);
        await SeedRolePermissionsOnlyAsync(seeder);

        var supplyDeptAdminId = (await db.Roles.SingleAsync(r => r.RoleCode == "SUPPLY_DEPT_ADMIN")).RoleID;
        var granted = await db.RolePermissions.Where(rp => rp.RoleID == supplyDeptAdminId)
            .Join(db.Permissions, rp => rp.PermissionID, p => p.PermissionID, (rp, p) => p.Code)
            .ToListAsync();

        granted.Should().BeEquivalentTo([PermissionCodes.SALE_ORDER_CONFIG_READ, PermissionCodes.SALE_ORDER_CONFIG_WRITE]);

        var squatterGranted = await db.RolePermissions.CountAsync(rp => rp.RoleID == (int)EnumRole.SupplyDeptAdmin);
        squatterGranted.Should().Be(0, "the role that happened to hold this id must gain nothing from a seed entry meant for someone else");
    }

    [Fact]
    public async Task A_pre_role_code_row_at_the_right_id_is_still_adopted_and_named_rather_than_duplicated()
    {
        var db = NewDb(Guid.NewGuid().ToString());
        // The state a database could be in between the original role-seeding migration and
        // AddRoleCodeAndIsActive: the row exists, but with no code yet.
        db.Roles.Add(new Role { RoleID = (int)EnumRole.SystemAdmin, Name = "System Admin", RoleCode = "", IsActive = true, IsGlobal = true });
        await db.SaveChangesAsync();
        var seeder = NewSeeder(db);

        await SeedRolesOnlyAsync(seeder);

        (await db.Roles.CountAsync(r => r.RoleCode == "SYSTEM_ADMIN")).Should().Be(1);
        var role = await db.Roles.SingleAsync(r => r.RoleID == (int)EnumRole.SystemAdmin);
        role.RoleCode.Should().Be("SYSTEM_ADMIN");
    }

    [Fact]
    public async Task With_no_drift_every_built_in_role_still_gets_its_own_seed_id()
    {
        var db = NewDb(Guid.NewGuid().ToString());
        var seeder = NewSeeder(db);

        await SeedRolesOnlyAsync(seeder);

        var supplyDeptAdmin = await db.Roles.SingleAsync(r => r.RoleCode == "SUPPLY_DEPT_ADMIN");
        supplyDeptAdmin.RoleID.Should().Be((int)EnumRole.SupplyDeptAdmin);
    }
}

using System.Reflection;
using FluentAssertions;
using SMS.Modules.Auth.Data;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Auth.Tests;

/// <summary>
/// A29-P3-01 §3.1 — the SUPPLY_DEPT_ADMIN role and its two permissions. Mirrors
/// LogisticsPermissionSeedTests' approach of reading AuthDataSeeder's private seed data via
/// reflection rather than running the seeder against a real database — AuthDataSeeder.SeedAsync()
/// opens with Database.MigrateAsync(), which the InMemory provider does not support, so the seed
/// arrays/dictionary are the only thing a unit test can actually exercise directly.
/// </summary>
public class SaleOrderAdminRoleSeedTests
{
    private static readonly Type SeederType = typeof(AuthDataSeeder);

    private static (int Id, string Name, string Code, string Description)[] RoleSeed() =>
        (ValueTuple<int, string, string, string>[])GetStaticField("RoleSeed")!;

    private static (string Name, string Code, string Description)[] PermissionSeed() =>
        (ValueTuple<string, string, string>[])GetStaticField("PermissionSeed")!;

    private static Dictionary<int, string[]> RolePermissionSeed() =>
        (Dictionary<int, string[]>)GetStaticField("RolePermissionSeed")!;

    private static object? GetStaticField(string name) =>
        SeederType.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);

    [Fact]
    public void The_supply_dept_admin_role_is_seeded_with_the_right_id_and_code()
    {
        var role = RoleSeed().Should().ContainSingle(r => r.Id == (int)EnumRole.SupplyDeptAdmin).Subject;
        role.Code.Should().Be("SUPPLY_DEPT_ADMIN");
        role.Name.Should().Be("Supply Department Administrator");
    }

    [Fact]
    public void Both_sale_order_config_permissions_are_seeded()
    {
        var codes = PermissionSeed().Select(p => p.Code).ToList();
        codes.Should().Contain(PermissionCodes.SALE_ORDER_CONFIG_READ);
        codes.Should().Contain(PermissionCodes.SALE_ORDER_CONFIG_WRITE);
    }

    [Fact]
    public void Supply_dept_admin_gets_exactly_read_and_write_and_nothing_else()
    {
        var granted = RolePermissionSeed()[(int)EnumRole.SupplyDeptAdmin];
        granted.Should().BeEquivalentTo(
        [
            PermissionCodes.SALE_ORDER_CONFIG_READ,
            PermissionCodes.SALE_ORDER_CONFIG_WRITE
        ]);
    }

    // ── §3.1's actual point: IT/Org Admin can view but not change ────────────

    [Fact]
    public void System_admin_can_read_sale_order_config_but_not_change_it()
    {
        var granted = RolePermissionSeed()[(int)EnumRole.SystemAdmin];
        granted.Should().Contain(PermissionCodes.SALE_ORDER_CONFIG_READ);
        granted.Should().NotContain(PermissionCodes.SALE_ORDER_CONFIG_WRITE);
    }

    [Fact]
    public void Org_admin_can_read_sale_order_config_but_not_change_it()
    {
        var granted = RolePermissionSeed()[(int)EnumRole.OrgAdmin];
        granted.Should().Contain(PermissionCodes.SALE_ORDER_CONFIG_READ);
        granted.Should().NotContain(PermissionCodes.SALE_ORDER_CONFIG_WRITE);
    }

    [Fact]
    public void System_admin_still_gets_every_other_permission_in_the_catalog()
    {
        // The §3.1 carve-out must not have widened into "System Admin lost more than this one code."
        var granted = RolePermissionSeed()[(int)EnumRole.SystemAdmin];
        var expected = PermissionCodes.All.Except([PermissionCodes.SALE_ORDER_CONFIG_WRITE]);
        granted.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void Only_supply_dept_admin_can_change_sale_order_config()
    {
        var rolesWithWrite = RolePermissionSeed()
            .Where(kv => kv.Value.Contains(PermissionCodes.SALE_ORDER_CONFIG_WRITE))
            .Select(kv => kv.Key)
            .ToList();

        rolesWithWrite.Should().BeEquivalentTo([(int)EnumRole.SupplyDeptAdmin]);
    }

    [Fact]
    public void The_permission_is_grouped_under_sale_order_administration_in_the_role_editor()
    {
        var method = typeof(SMS.Modules.Auth.Repositories.AuthRepository)
            .GetMethod("GetPermissionModule", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        ((string)method!.Invoke(null, [PermissionCodes.SALE_ORDER_CONFIG_READ])!).Should().Be("Sale Order Administration");
        ((string)method!.Invoke(null, [PermissionCodes.SALE_ORDER_CONFIG_WRITE])!).Should().Be("Sale Order Administration");
    }
}

using System.Reflection;
using FluentAssertions;
using SMS.Modules.Auth.Data;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Auth.Tests;

/// <summary>
/// A33 PA-06 / D-9 — the fulfillment route codes and DELIVERY_APPROVE (docs/fulfillment-routes/API-CONTRACT.md §2): in
/// the catalog and <c>All</c>, seeded once with a name and description, granted to the admins and the Inventory Manager
/// (the built-in role holding STOCK_MANAGE), and grouped under Logistics in the role editor.
/// </summary>
public class FulfillmentRoutePermissionSeedTests
{
    private static readonly string[] RouteCodes =
    [
        PermissionCodes.FULFILLMENT_ROUTE_VIEW, PermissionCodes.FULFILLMENT_ROUTE_MANAGE, PermissionCodes.FULFILLMENT_ROUTE_ASSIGN
    ];

    private static readonly string[] Codes = [.. RouteCodes, PermissionCodes.DELIVERY_APPROVE];

    private static (string Name, string Code, string Description)[] PermissionSeed() =>
        (ValueTuple<string, string, string>[])typeof(AuthDataSeeder)
            .GetField("PermissionSeed", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static Dictionary<int, string[]> RolePermissionSeed() =>
        (Dictionary<int, string[]>)typeof(AuthDataSeeder)
            .GetField("RolePermissionSeed", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static string Grouping(string code) =>
        (string)typeof(SMS.Modules.Auth.Repositories.AuthRepository)
            .GetMethod("GetPermissionModule", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [code])!;

    [Fact]
    public void Every_code_is_upper_snake_in_the_catalog_and_seeded_once_with_a_name_and_description()
    {
        PermissionCodes.All.Should().Contain(Codes);
        Codes.Should().OnlyContain(c => c == c.ToUpperInvariant() && !c.Contains(' '));

        var seeded = PermissionSeed();
        foreach (var code in Codes)
        {
            var row = seeded.Should().ContainSingle(p => p.Code == code, $"{code} must be seeded exactly once").Subject;
            row.Name.Should().NotBeNullOrWhiteSpace();
            row.Description.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Theory]
    [InlineData((int)EnumRole.SystemAdmin)]
    [InlineData((int)EnumRole.OrgAdmin)]
    public void The_administrators_get_every_new_code(int role) =>
        RolePermissionSeed()[role].Should().Contain(Codes);

    [Fact]
    public void The_inventory_manager_views_manages_and_assigns_routes_but_does_not_approve_dispatch()
    {
        var granted = RolePermissionSeed()[(int)EnumRole.InventoryManager];
        granted.Should().Contain(RouteCodes);
        granted.Should().NotContain(PermissionCodes.DELIVERY_APPROVE);
    }

    [Theory]
    [InlineData((int)EnumRole.SupplyDeptAdmin)] // stays exactly config read/write (SaleOrderAdminRoleSeedTests)
    [InlineData((int)EnumRole.ProcurementManager)]
    [InlineData((int)EnumRole.PurchaseOfficer)]
    [InlineData((int)EnumRole.WarehouseOperator)]
    [InlineData((int)EnumRole.FinanceOfficer)]
    [InlineData((int)EnumRole.FinanceManager)]
    [InlineData((int)EnumRole.Requester)]
    [InlineData((int)EnumRole.Auditor)]
    public void No_other_role_gets_one(int role) =>
        RolePermissionSeed()[role].Should().NotIntersectWith(Codes, ((EnumRole)role).ToString());

    [Theory]
    [InlineData(PermissionCodes.FULFILLMENT_ROUTE_VIEW)]
    [InlineData(PermissionCodes.FULFILLMENT_ROUTE_MANAGE)]
    [InlineData(PermissionCodes.FULFILLMENT_ROUTE_ASSIGN)]
    [InlineData(PermissionCodes.DELIVERY_APPROVE)]
    public void They_group_under_logistics_in_the_role_editor(string code) =>
        Grouping(code).Should().Be("Logistics");
}

using System.Reflection;
using FluentAssertions;
using SMS.Modules.Auth.Data;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Auth.Tests;

/// <summary>
/// A36-X-01 / D-12 — the five SERVICE_ORDER_* codes: in the catalog and <c>All</c>, seeded once with a name and
/// description, and granted per D-12 (admins and the Inventory Manager all five; the Warehouse Operator view, edit and
/// complete; procurement, purchasing, finance and the auditor view only; nobody else any).
/// </summary>
public class ServiceOrderPermissionSeedTests
{
    private static readonly string[] Codes =
    [
        PermissionCodes.SERVICE_ORDER_VIEW, PermissionCodes.SERVICE_ORDER_CREATE, PermissionCodes.SERVICE_ORDER_EDIT,
        PermissionCodes.SERVICE_ORDER_COMPLETE, PermissionCodes.SERVICE_ORDER_CANCEL
    ];

    private static (string Name, string Code, string Description)[] PermissionSeed() =>
        (ValueTuple<string, string, string>[])typeof(AuthDataSeeder)
            .GetField("PermissionSeed", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static Dictionary<int, string[]> RolePermissionSeed() =>
        (Dictionary<int, string[]>)typeof(AuthDataSeeder)
            .GetField("RolePermissionSeed", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static string Grouping(string code) =>
        (string)typeof(SMS.Modules.Auth.Repositories.AuthRepository)
            .GetMethod("GetPermissionModule", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [code])!;

    private static IEnumerable<string> ServiceCodesOf(EnumRole role) =>
        RolePermissionSeed()[(int)role].Where(c => c.StartsWith("SERVICE_ORDER_"));

    [Fact]
    public void Every_code_is_in_the_catalog_and_seeded_once_with_a_name_and_description()
    {
        Codes.Should().Equal("SERVICE_ORDER_VIEW", "SERVICE_ORDER_CREATE", "SERVICE_ORDER_EDIT", "SERVICE_ORDER_COMPLETE", "SERVICE_ORDER_CANCEL");
        PermissionCodes.All.Should().Contain(Codes);

        var seeded = PermissionSeed();
        foreach (var code in Codes)
        {
            var row = seeded.Should().ContainSingle(p => p.Code == code, $"{code} must be seeded exactly once").Subject;
            row.Name.Should().NotBeNullOrWhiteSpace();
            row.Description.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Theory]
    [InlineData(EnumRole.SystemAdmin)]
    [InlineData(EnumRole.OrgAdmin)]
    [InlineData(EnumRole.InventoryManager)]
    public void Admins_and_the_inventory_manager_get_all_five(EnumRole role) =>
        ServiceCodesOf(role).Should().BeEquivalentTo(Codes);

    [Fact]
    public void The_warehouse_operator_views_edits_and_completes_but_does_not_create_or_cancel() =>
        ServiceCodesOf(EnumRole.WarehouseOperator).Should().BeEquivalentTo(
            [PermissionCodes.SERVICE_ORDER_VIEW, PermissionCodes.SERVICE_ORDER_EDIT, PermissionCodes.SERVICE_ORDER_COMPLETE]);

    [Theory]
    [InlineData(EnumRole.ProcurementManager)]
    [InlineData(EnumRole.PurchaseOfficer)]
    [InlineData(EnumRole.FinanceOfficer)]
    [InlineData(EnumRole.FinanceManager)]
    [InlineData(EnumRole.Auditor)]
    public void Readers_only_view(EnumRole role) =>
        ServiceCodesOf(role).Should().Equal(PermissionCodes.SERVICE_ORDER_VIEW);

    [Theory]
    [InlineData(EnumRole.Requester)]
    [InlineData(EnumRole.SupplyDeptAdmin)]
    public void Nobody_else_gets_one(EnumRole role) =>
        ServiceCodesOf(role).Should().BeEmpty();

    [Fact]
    public void They_group_under_service_orders_in_the_role_editor() =>
        Codes.Select(Grouping).Should().OnlyContain(g => g == "Service Orders");
}

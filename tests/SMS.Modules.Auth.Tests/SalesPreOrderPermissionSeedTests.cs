using System.Reflection;
using FluentAssertions;
using SMS.Modules.Auth.Data;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Auth.Tests;

/// <summary>
/// A32 PA-05 — the sales pre-order permission codes (docs/sales-preorder/API-CONTRACT.md §2) live where every
/// permission does: the constants and <c>All</c>, the seeded definitions, the role grants and the role editor's
/// grouping. Read from the seeder's private data by reflection, like <see cref="ReceivablesPermissionSeedTests"/>.
/// </summary>
public class SalesPreOrderPermissionSeedTests
{
    private static readonly string[] Codes =
    [
        PermissionCodes.SALE_INQUIRY_VIEW, PermissionCodes.SALE_INQUIRY_CREATE, PermissionCodes.SALE_INQUIRY_EDIT,
        PermissionCodes.SALE_QUOTATION_VIEW, PermissionCodes.SALE_QUOTATION_CREATE, PermissionCodes.SALE_QUOTATION_EDIT,
        PermissionCodes.SALE_QUOTATION_SEND,
        PermissionCodes.SALE_ORDER_RESERVE, PermissionCodes.SALE_ORDER_RELEASE_RESERVATION,
        PermissionCodes.SALE_REJECTION_REASON_MANAGE
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
    public void The_inventory_manager_stands_in_for_the_specs_warehouse_manager_and_can_reach_the_buttons()
    {
        RolePermissionSeed()[(int)EnumRole.InventoryManager].Should().Contain(
        [
            PermissionCodes.SALE_ORDER_VIEW, PermissionCodes.SALE_ORDER_RESERVE, PermissionCodes.SALE_ORDER_RELEASE_RESERVATION
        ]);
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
    public void No_other_role_gets_a_sales_pre_order_code(int role) =>
        RolePermissionSeed()[role].Should().NotIntersectWith(Codes, ((EnumRole)role).ToString());

    [Theory]
    [InlineData(PermissionCodes.SALE_INQUIRY_VIEW, "Sale Inquiries")]
    [InlineData(PermissionCodes.SALE_INQUIRY_EDIT, "Sale Inquiries")]
    [InlineData(PermissionCodes.SALE_QUOTATION_SEND, "Sale Quotations")]
    [InlineData(PermissionCodes.SALE_ORDER_RESERVE, "Sale Orders")]
    [InlineData(PermissionCodes.SALE_ORDER_RELEASE_RESERVATION, "Sale Orders")]
    [InlineData(PermissionCodes.SALE_REJECTION_REASON_MANAGE, "Sale Order Administration")]
    public void They_group_with_sales_in_the_role_editor(string code, string group) =>
        Grouping(code).Should().Be(group);
}

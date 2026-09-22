using System.Reflection;
using FluentAssertions;
using SMS.Modules.Auth.Data;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Auth.Tests;

/// <summary>
/// A29-P8-05 — <c>PRODUCT_LEDGER_VIEW</c> lives in the same three places every permission does: the
/// constants and <c>All</c>, the seeded definitions, and the role editor's grouping. It opens what a
/// product cost and what it earned, so who holds it matters as much as that it exists.
/// </summary>
public class ProductLedgerPermissionSeedTests
{
    private const string Code = PermissionCodes.PRODUCT_LEDGER_VIEW;

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
    public void The_code_is_in_the_catalog_and_seeded_once_with_a_name_and_a_description()
    {
        Code.Should().Be("PRODUCT_LEDGER_VIEW");
        PermissionCodes.All.Should().Contain(Code);

        var row = PermissionSeed().Should().ContainSingle(p => p.Code == Code).Subject;
        row.Name.Should().NotBeNullOrWhiteSpace();
        row.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void It_appears_under_finance_in_the_role_editor_not_under_other()
    {
        Grouping(Code).Should().Be("Finance");
    }

    [Theory]
    [InlineData((int)EnumRole.FinanceOfficer)]
    [InlineData((int)EnumRole.FinanceManager)]
    [InlineData((int)EnumRole.Auditor)]
    public void The_roles_that_answer_for_the_books_can_read_it(int role)
    {
        RolePermissionSeed()[role].Should().Contain(Code);
    }

    [Fact]
    public void Nobody_who_only_buys_stores_or_requests_can_see_what_things_cost_and_earn_through_it()
    {
        var others = new[]
        {
            EnumRole.ProcurementManager, EnumRole.PurchaseOfficer, EnumRole.InventoryManager,
            EnumRole.WarehouseOperator, EnumRole.Requester
        };

        foreach (var role in others)
            RolePermissionSeed()[(int)role].Should().NotContain(Code, role.ToString());
    }

    [Fact]
    public void The_permission_is_new_and_no_existing_stock_or_report_permission_was_widened_to_carry_it()
    {
        Code.Should().NotBe(PermissionCodes.INVENTORY_VIEW).And.NotBe(PermissionCodes.REPORT_VIEW);

        // Holding the stock view or the generic report view, without this code, must not reach cost and margin.
        var inventoryManager = RolePermissionSeed()[(int)EnumRole.InventoryManager];
        inventoryManager.Should().Contain(PermissionCodes.INVENTORY_VIEW).And.NotContain(Code);
    }

    [Fact]
    public void The_two_admin_roles_get_it_through_the_catalog_without_a_seeder_edit()
    {
        RolePermissionSeed()[(int)EnumRole.SystemAdmin].Should().Contain(Code);
        RolePermissionSeed()[(int)EnumRole.OrgAdmin].Should().Contain(Code);
    }
}

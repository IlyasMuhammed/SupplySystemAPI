using System.Reflection;
using FluentAssertions;
using SMS.Modules.Auth.Data;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Auth.Tests;

/// <summary>
/// A29-P7-08 — the receivables permissions live in the same three places every permission does: the
/// constants and <c>All</c>, the seeded definitions, and the role editor's grouping. Same approach as
/// <see cref="LogisticsPermissionSeedTests"/>: read the seeder's private data by reflection, since
/// running the seeder needs a real database.
/// </summary>
public class ReceivablesPermissionSeedTests
{
    private static readonly string[] Codes =
    [
        PermissionCodes.SALES_INVOICE_VIEW,
        PermissionCodes.SALES_INVOICE_MANAGE,
        PermissionCodes.CUSTOMER_PAYMENT_VIEW,
        PermissionCodes.CUSTOMER_PAYMENT_RECORD,
        PermissionCodes.CUSTOMER_LEDGER_VIEW
    ];

    private static readonly string[] Views =
    [
        PermissionCodes.SALES_INVOICE_VIEW, PermissionCodes.CUSTOMER_PAYMENT_VIEW, PermissionCodes.CUSTOMER_LEDGER_VIEW
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
    public void Every_receivables_code_is_in_the_catalog_and_seeded_with_a_name_and_a_description()
    {
        PermissionCodes.All.Should().Contain(Codes);

        var seeded = PermissionSeed();
        foreach (var code in Codes)
        {
            var row = seeded.Should().ContainSingle(p => p.Code == code, $"{code} must be seeded exactly once").Subject;
            row.Name.Should().NotBeNullOrWhiteSpace();
            row.Description.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void They_appear_under_finance_in_the_role_editor_not_under_other()
    {
        foreach (var code in Codes)
            Grouping(code).Should().Be("Finance", code);
    }

    [Fact]
    public void They_are_new_codes_and_do_not_widen_what_the_supplier_side_permissions_mean()
    {
        // Being allowed to pay a supplier must not, by itself, allow recording that a customer paid.
        Codes.Should().NotIntersectWith(
        [
            PermissionCodes.INVOICE_VIEW, PermissionCodes.INVOICE_PROCESS,
            PermissionCodes.PAYMENT_VIEW, PermissionCodes.PAYMENT_PROCESS, PermissionCodes.PAYMENT_APPROVE
        ]);
    }

    [Theory]
    [InlineData((int)EnumRole.FinanceOfficer)]
    [InlineData((int)EnumRole.FinanceManager)]
    public void The_finance_roles_can_run_receivables_end_to_end(int role)
    {
        RolePermissionSeed()[role].Should().Contain(Codes);
    }

    [Fact]
    public void The_auditor_can_read_receivables_but_change_nothing()
    {
        var granted = RolePermissionSeed()[(int)EnumRole.Auditor];

        granted.Should().Contain(Views);
        granted.Should().NotContain(PermissionCodes.SALES_INVOICE_MANAGE);
        granted.Should().NotContain(PermissionCodes.CUSTOMER_PAYMENT_RECORD);
    }

    [Fact]
    public void Nobody_who_only_buys_or_only_warehouses_can_touch_receivables()
    {
        var others = new[]
        {
            EnumRole.ProcurementManager, EnumRole.PurchaseOfficer, EnumRole.InventoryManager,
            EnumRole.WarehouseOperator, EnumRole.Requester
        };

        foreach (var role in others)
            RolePermissionSeed()[(int)role].Should().NotIntersectWith(Codes, role.ToString());
    }

    [Fact]
    public void The_two_admin_roles_get_them_through_the_catalog_without_a_seeder_edit()
    {
        RolePermissionSeed()[(int)EnumRole.SystemAdmin].Should().Contain(Codes);
        RolePermissionSeed()[(int)EnumRole.OrgAdmin].Should().Contain(Codes);
    }
}

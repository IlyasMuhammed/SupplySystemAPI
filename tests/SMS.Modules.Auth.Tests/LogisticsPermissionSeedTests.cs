using System.Reflection;
using FluentAssertions;
using SMS.Shared.Authorization;
using Xunit;

namespace SMS.Modules.Auth.Tests;

/// <summary>
/// T-17 — permission codes live in three places that have to agree: the constants and the
/// <c>All</c> list in <see cref="PermissionCodes"/>, the seeded definitions in
/// <c>AuthDataSeeder</c>, and the module grouping in <c>AuthRepository</c>. Nothing in the build
/// connects them, so a code added to one and forgotten in the others compiles happily and simply
/// never appears in the role editor.
/// </summary>
public class LogisticsPermissionSeedTests
{
    private static IReadOnlyList<string> DeclaredConstants() =>
        typeof(PermissionCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

    // ── TC-17.1 ──────────────────────────────────────────────────────────────

    [Fact]
    public void Every_declared_permission_constant_is_listed_in_all()
    {
        // System Admin is seeded from PermissionCodes.All. A constant missing from it is a
        // permission nobody — not even System Admin — can ever be granted.
        DeclaredConstants().Should().BeEquivalentTo(PermissionCodes.All);
    }

    [Fact]
    public void No_permission_code_is_declared_twice()
    {
        DeclaredConstants().Should().OnlyHaveUniqueItems();
        PermissionCodes.All.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void The_logistics_codes_this_module_enforces_all_exist()
    {
        // Each of these is referenced by a [RequirePermission] on a Logistics controller action.
        PermissionCodes.All.Should().Contain(
        [
            PermissionCodes.DELIVERY_TRACK,
            PermissionCodes.DELIVERY_VIEW,
            PermissionCodes.DELIVERY_CREATE,
            PermissionCodes.DELIVERY_EDIT,
            PermissionCodes.SHIPMENT_BOOK,
            PermissionCodes.CARRIER_MANAGE,
            PermissionCodes.CARRIER_CREDENTIAL_MANAGE,
            PermissionCodes.SHIPMENT_RATE_VIEW
        ]);
    }

    // ── TC-17.3 — grouping in the role editor ───────────────────────────────

    [Theory]
    [InlineData("DELIVERY_TRACK",  "Logistics")]
    [InlineData("DELIVERY_VIEW",   "Logistics")]
    [InlineData("DELIVERY_CREATE", "Logistics")]
    [InlineData("DELIVERY_EDIT",   "Logistics")]
    [InlineData("SHIPMENT_BOOK",   "Logistics")]
    [InlineData("SHIPMENT_RATE_VIEW", "Logistics")]
    // The Warehouse grouping is matched earlier in the switch and must not be captured by the
    // new DELIVERY_/SHIPMENT_ prefixes.
    [InlineData("PICKING",         "Warehouse")]
    [InlineData("DISPATCH",        "Warehouse")]
    [InlineData("WAREHOUSE_TRANSFER", "Warehouse")]
    public void A_permission_is_grouped_under_the_module_it_belongs_to(string code, string expected) =>
        InvokeGrouping(code).Should().Be(expected);

    [Fact]
    public void Every_logistics_permission_lands_in_a_named_group_rather_than_other()
    {
        // "Other" is the fall-through, and a code landing there shows up at the bottom of the
        // role editor with no heading.
        //
        // Scoped to this module's codes on purpose. Running it across PermissionCodes.All shows
        // that PLATFORM_SUPER_ADMIN is already ungrouped — a pre-existing gap in
        // AuthRepository.GetPermissionModule that predates this work. It is reported rather than
        // quietly fixed here, because changing how another module's permission is grouped in the
        // role editor is not this task's call to make.
        var ungrouped = PermissionCodes.All
            .Where(code => code.StartsWith("DELIVERY_")
                        || code.StartsWith("SHIPMENT_")
                        || code.StartsWith("CARRIER_")
                        || code.StartsWith("RATE_CARD_")
                        || code.StartsWith("SHIPPING_RULE_")
                        || code.StartsWith("FREIGHT_"))
            .Where(code => InvokeGrouping(code) == "Other")
            .ToList();

        ungrouped.Should().BeEmpty();
    }

    private static string InvokeGrouping(string code)
    {
        var method = typeof(SMS.Modules.Auth.Repositories.AuthRepository)
            .GetMethod("GetPermissionModule", BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull("AuthRepository.GetPermissionModule drives the role editor's grouping");

        return (string)method!.Invoke(null, [code])!;
    }
}

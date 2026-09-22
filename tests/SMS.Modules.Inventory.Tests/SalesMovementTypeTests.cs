using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Inventory.Tests;

/// <summary>
/// A29-P8-04 §12.1 — the four sales movement kinds are named beside the existing goods-issue transaction
/// types. Nothing enumerates the strings the ledgers hold, so what is checkable is that each is spelled
/// as the spec spells it, that none collides with another, and that each fits every ledger column that
/// will one day carry it.
/// </summary>
public class SalesMovementTypeTests
{
    private static readonly string[] SalesKinds =
    [
        GoodsIssueTransactionType.SalesShip, GoodsIssueTransactionType.SalesHandover,
        GoodsIssueTransactionType.SalesReturn, GoodsIssueTransactionType.DropShipVirtual
    ];

    private static IReadOnlyList<string> Everything() =>
        typeof(GoodsIssueTransactionType).GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

    [Fact]
    public void The_four_sales_kinds_are_spelled_as_section_12_1_spells_them()
    {
        SalesKinds.Should().Equal("SALES_SHIP", "SALES_HANDOVER", "SALES_RETURN", "DROP_SHIP_VIRTUAL");
    }

    [Fact]
    public void They_sit_beside_the_existing_types_without_replacing_or_renaming_any()
    {
        Everything().Should().BeEquivalentTo(
        [
            "DELIVERY_ISSUE", "TRANSFER_OUT", "TRANSFER_IN",
            "SALES_SHIP", "SALES_HANDOVER", "SALES_RETURN", "DROP_SHIP_VIRTUAL"
        ]);
    }

    [Fact]
    public void No_two_types_are_the_same_string_and_none_is_blank()
    {
        Everything().Should().OnlyHaveUniqueItems().And.OnlyContain(t => !string.IsNullOrWhiteSpace(t));
    }

    [Fact]
    public void Every_type_is_upper_snake_case_like_the_ones_already_in_the_ledgers()
    {
        Everything().Should().OnlyContain(t => System.Text.RegularExpressions.Regex.IsMatch(t, "^[A-Z]+(_[A-Z]+)*$"));
    }

    [Fact]
    public void Every_type_fits_the_transaction_type_column_of_both_ledgers_that_record_it()
    {
        using var inventory = new InventoryDbContext(
            new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new StaticTenantContext());
        using var finance = new FinanceDbContext(
            new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new StaticTenantContext());

        var inventoryMax = inventory.Model.FindEntityType(typeof(InventoryLedgerEntry))!.FindProperty(nameof(InventoryLedgerEntry.TransactionType))!.GetMaxLength();
        var masterMax    = finance.Model.FindEntityType(typeof(MasterProductLedger))!.FindProperty(nameof(MasterProductLedger.TransactionType))!.GetMaxLength();

        inventoryMax.Should().NotBeNull();
        masterMax.Should().NotBeNull();
        Everything().Should().OnlyContain(t => t.Length <= inventoryMax && t.Length <= masterMax,
            "the master ledger's column is the narrower of the two");
    }
}

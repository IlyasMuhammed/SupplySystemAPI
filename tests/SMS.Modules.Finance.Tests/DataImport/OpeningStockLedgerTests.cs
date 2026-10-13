using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Finance.Tests.DataImport;

/// <summary>
/// A40 (TFIN, for TINV's OPENING_STOCK target) — the product ledger's OPENING_STOCK entry type and the master product ledger's
/// optional transaction date.
/// </summary>
public sealed class OpeningStockLedgerTests
{
    private static ProductLedgerPosting Opening(Guid variant, decimal qty, decimal cost, DateTime? date = null) =>
        new(variant, ProductLedgerEntryTypes.OpeningStock, ProductLedgerDirections.In, qty, cost,
            "OpeningStock", Guid.NewGuid(), "OPEN-WH-MAIN", ProductLedgerRig.User, EntryDate: date);

    [Fact]
    public void OPENING_STOCK_is_a_known_entry_type()
    {
        ProductLedgerEntryTypes.OpeningStock.Should().Be("OPENING_STOCK");
        ProductLedgerEntryTypes.All.Should().Contain("OPENING_STOCK");
    }

    [Fact]
    public async Task Opening_stock_comes_in_at_its_cost_blends_into_the_wac_like_a_purchase_and_counts_with_purchases()
    {
        var rig = new ProductLedgerRig();
        var (variant, _) = rig.Variants.New();

        var opening = await rig.Service().AppendEntryAsync(Opening(variant, 100m, 10m, new DateTime(2026, 6, 30)));
        (opening.Direction, opening.TotalCost, opening.RunningQty, opening.RunningValue, opening.WeightedAverageCost)
            .Should().Be(("IN", 1000m, 100m, 1000m, 10m));

        await rig.Service().AppendEntryAsync(ProductLedgerRig.Buy(variant, 50m, 16m));
        var sale = await rig.Service().AppendEntryAsync(ProductLedgerRig.Sell(variant, 30m));
        sale.UnitCost.Should().Be(12m, "(1000 + 800) / 150");

        (await rig.EntriesAsync(variant))[0].Should().Match<SMS.Modules.Finance.Domain.ProductLedgerEntry>(e =>
            e.EntryType == "OPENING_STOCK" && e.EntryDate == new DateTime(2026, 6, 30));

        await using var db = Receivables.Db(rig.Org, rig.DbName);
        var query   = new ProductLedgerQueryService(db, rig.Variants);
        var summary = await query.GetSummaryAsync(variant);
        (summary.PurchasedQuantity, summary.PurchasedCost, summary.CurrentQuantity).Should().Be((150m, 1800m, 120m));

        var filtered = await query.GetLedgerAsync(variant, new ProductLedgerFilter { EntryType = "opening_stock" });
        filtered.Data.Should().ContainSingle();
    }

    [Fact]
    public async Task Opening_stock_can_only_come_in_and_needs_a_cost()
    {
        var rig = new ProductLedgerRig();
        var (variant, _) = rig.Variants.New();

        var outward = () => rig.Service().AppendEntryAsync(Opening(variant, 1m, 1m) with { Direction = ProductLedgerDirections.Out, UnitCost = null });
        await outward.Should().ThrowAsync<ArgumentException>();
        var noCost = () => rig.Service().AppendEntryAsync(Opening(variant, 1m, 1m) with { UnitCost = null });
        await noCost.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task A_master_product_ledger_movement_takes_the_callers_date_and_defaults_to_now()
    {
        await using var db = Receivables.Db(Guid.NewGuid(), Guid.NewGuid().ToString());
        var svc = new MasterProductLedgerService(db);

        ProductMovementContext Movement(DateTime? date) => new()
        {
            VariantId = 1, ProductCode = "SKU", ProductName = "P", WarehouseId = 1, WarehouseName = "Main",
            TransactionType = "OPENING_STOCK", ReferenceType = "OpeningStock", ReferenceId = Guid.NewGuid(), ReferenceNumber = "OPEN-1",
            QuantityIn = 5m, UnitCost = 2m, SourceType = "ADJUSTMENT", DestinationType = "WAREHOUSE", CreatedBy = 1,
            TransactionDate = date
        };

        var before = DateTime.UtcNow.AddSeconds(-1);
        await svc.PostMovementAsync(Movement(new DateTime(2026, 6, 30)));
        await svc.PostMovementAsync(Movement(null));

        var rows = await db.MasterProductLedgers.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        rows[0].TransactionDate.Should().Be(new DateTime(2026, 6, 30));
        rows[0].CreatedDate.Should().BeOnOrAfter(before);
        rows[1].TransactionDate.Should().BeOnOrAfter(before);
    }
}

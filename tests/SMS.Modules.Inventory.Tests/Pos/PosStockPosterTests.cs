using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Inventory.Tests.Pos;

/// <summary>A38 C9 / D-9 (INV) — POS stock postings: no reservation, never refused, negative on the default row.</summary>
public class PosStockPosterTests
{
    [Fact]
    public async Task A_sale_takes_free_stock_FEFO_across_batch_rows()
    {
        var h = new PosHarness();
        var w = await h.WarehouseAsync();
        var v = await h.VariantAsync(batchTracked: true);
        var late  = await h.RowAsync(v, w, 3m, batch: "B-LATE",  expiry: DateTime.UtcNow.AddDays(10), unitCost: 6m);
        var early = await h.RowAsync(v, w, 4m, batch: "B-EARLY", expiry: DateTime.UtcNow.AddDays(5),  unitCost: 4m);

        var result = await h.Poster.PostAsync(h.Sale(w, Guid.NewGuid(), (v, 5m)));

        result.NegativeVariantUuids.Should().BeEmpty();
        var rows = await h.RowsAsync(v, w);
        rows.Single(r => r.Id == early.Id).QtyOnHand.Should().Be(0m, "the batch expiring first goes first");
        rows.Single(r => r.Id == late.Id).QtyOnHand.Should().Be(2m);
        rows.Should().HaveCount(2, "no default row is needed when free stock covers the sale");

        var ledger = await h.Db.InventoryLedgerEntries.AsNoTracking().ToListAsync();
        ledger.Should().HaveCount(2, "one ledger row per inventory row touched");
        ledger.Should().OnlyContain(e => e.TransactionType == PosStockTransactionType.Sale && e.QuantityOut != null);
        ledger.Select(e => (e.QuantityOut, e.UnitCost)).Should().BeEquivalentTo(new[] { ((decimal?)4m, 4m), ((decimal?)1m, 6m) });
    }

    [Fact]
    public async Task A_shortfall_goes_negative_on_a_new_default_row_and_leaves_other_reservations_alone()
    {
        var h = new PosHarness();
        var w = await h.WarehouseAsync();
        var v = await h.VariantAsync(batchTracked: true);
        var held = await h.RowAsync(v, w, 5m, reserved: 5m, batch: "B-HELD");
        var free = await h.RowAsync(v, w, 3m, batch: "B-FREE");

        var result = await h.Poster.PostAsync(h.Sale(w, Guid.NewGuid(), (v, 5m)));

        result.NegativeVariantUuids.Should().Equal(v.Uuid);
        var rows = await h.RowsAsync(v, w);
        rows.Single(r => r.Id == held.Id).Should().BeEquivalentTo(new { QtyOnHand = 5m, QtyReserved = 5m }, "another document holds it");
        rows.Single(r => r.Id == free.Id).QtyOnHand.Should().Be(0m);
        var fallback = rows.Single(r => r.BatchNumber == null && r.SerialNumber == null);
        fallback.QtyOnHand.Should().Be(-2m);
        fallback.QtyReserved.Should().Be(0m);
        fallback.OrganizationId.Should().Be(h.Org);

        var levels = await h.Poster.GetLevelsAsync(h.Org, w.Uuid);
        levels.Should().ContainSingle().Which.Should().Be(new PosStockLevel(v.Uuid, -2m));
    }

    [Fact]
    public async Task On_an_untracked_row_the_sale_takes_the_free_part_and_the_rest_below_the_reserved_quantity()
    {
        var h = new PosHarness();
        var w = await h.WarehouseAsync();
        var v = await h.VariantAsync();
        var row = await h.RowAsync(v, w, 10m, reserved: 4m);

        var result = await h.Poster.PostAsync(h.Sale(w, Guid.NewGuid(), (v, 8m)));

        result.NegativeVariantUuids.Should().Equal(v.Uuid);
        var after = (await h.RowsAsync(v, w)).Single();
        after.Id.Should().Be(row.Id);
        after.Should().BeEquivalentTo(new { QtyOnHand = 2m, QtyReserved = 4m }, "the reservation itself is never consumed");
        (await h.Db.InventoryLedgerEntries.SingleAsync()).QuantityOut.Should().Be(8m, "one row touched, one ledger row");
    }

    [Fact]
    public async Task A_sale_with_no_stock_row_at_all_still_posts()
    {
        var h = new PosHarness();
        var w = await h.WarehouseAsync();
        var v = await h.VariantAsync(purchasePrice: 1.2m);

        var result = await h.Poster.PostAsync(h.Sale(w, Guid.NewGuid(), (v, 3m)));

        result.NegativeVariantUuids.Should().Equal(v.Uuid);
        var row = (await h.RowsAsync(v, w)).Single();
        row.QtyOnHand.Should().Be(-3m);
        row.UnitCost.Should().Be(1.2m, "the variant's purchase price is the only cost known");
    }

    [Fact]
    public async Task Posting_the_same_document_twice_moves_stock_once()
    {
        var h = new PosHarness();
        var w = await h.WarehouseAsync();
        var v = await h.VariantAsync();
        await h.RowAsync(v, w, 2m);
        var sale = h.Sale(w, Guid.NewGuid(), (v, 3m));

        var first  = await h.Poster.PostAsync(sale);
        var second = await h.Poster.PostAsync(sale);

        first.NegativeVariantUuids.Should().Equal(v.Uuid);
        second.NegativeVariantUuids.Should().Equal([v.Uuid], "a repeat still reports what is negative");
        (await h.RowsAsync(v, w)).Single().QtyOnHand.Should().Be(-1m);
        (await h.Db.InventoryLedgerEntries.CountAsync()).Should().Be(1);
        (await h.Fin.MasterProductLedgers.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_return_and_a_void_put_stock_back_on_the_default_row()
    {
        var h = new PosHarness();
        var w = await h.WarehouseAsync();
        var v = await h.VariantAsync(batchTracked: true);
        await h.RowAsync(v, w, 5m, batch: "B-1", unitCost: 3m);
        var order = Guid.NewGuid();

        await h.Poster.PostAsync(h.Sale(w, order, (v, 5m)));
        await h.Poster.PostAsync(h.Inbound(PosStockTransactionType.Return, w, Guid.NewGuid(), (v, 2m)));
        var result = await h.Poster.PostAsync(h.Inbound(PosStockTransactionType.Void, w, order, (v, 3m)));

        result.NegativeVariantUuids.Should().BeEmpty();
        var rows = await h.RowsAsync(v, w);
        rows.Single(r => r.BatchNumber == "B-1").QtyOnHand.Should().Be(0m);
        rows.Single(r => r.BatchNumber == null).QtyOnHand.Should().Be(5m);

        var inbound = await h.Db.InventoryLedgerEntries.AsNoTracking().Where(e => e.QuantityIn != null).ToListAsync();
        inbound.Select(e => e.TransactionType).Should().BeEquivalentTo([PosStockTransactionType.Return, PosStockTransactionType.Void]);
        inbound.Should().OnlyContain(e => e.UnitCost == 3m, "the store's average cost when the batch row is empty");
    }

    [Fact]
    public async Task Ledger_and_master_product_ledger_rows_name_the_document_and_the_customer_side()
    {
        var h = new PosHarness();
        var w = await h.WarehouseAsync("Mall store");
        var v = await h.VariantAsync();
        await h.RowAsync(v, w, 10m, unitCost: 7m);
        var order = Guid.NewGuid();

        await h.Poster.PostAsync(h.Sale(w, order, (v, 4m)));
        await h.Poster.PostAsync(h.Inbound(PosStockTransactionType.Void, w, order, (v, 4m)));

        var entries = await h.Db.InventoryLedgerEntries.AsNoTracking().OrderBy(e => e.CreatedAt).ToListAsync();
        entries.Should().HaveCount(2);
        entries[0].Should().BeEquivalentTo(new
        {
            TransactionType = "POS_SALE", ReferenceType = "POS_ORDER", ReferenceId = order, ReferenceNumber = "POS-S1-20261010-0001",
            QuantityOut = (decimal?)4m, UnitCost = 7m, BalanceAfter = -4m, OrganizationId = h.Org, CreatedBy = 7
        });
        entries[1].Should().BeEquivalentTo(new { TransactionType = "POS_VOID", QuantityIn = (decimal?)4m, BalanceAfter = 0m });

        var master = await h.Fin.MasterProductLedgers.AsNoTracking().OrderBy(m => m.CreatedDate).ToListAsync();
        master.Should().HaveCount(2);
        master[0].Should().BeEquivalentTo(new
        {
            TransactionType = "POS_SALE", SourceType = "WAREHOUSE", SourceName = "Mall store", DestinationType = "CUSTOMER",
            ReferenceId = order, QuantityOut = (decimal?)4m, TotalValue = 28m
        });
        master[1].Should().BeEquivalentTo(new { TransactionType = "POS_VOID", SourceType = "CUSTOMER", DestinationType = "WAREHOUSE" });
    }

    [Fact]
    public async Task Levels_are_on_hand_minus_reserved_per_variant_in_that_warehouse_only()
    {
        var h = new PosHarness();
        var store = await h.WarehouseAsync("Store");
        var other = await h.WarehouseAsync("Central");
        var a = await h.VariantAsync("A", batchTracked: true);
        var b = await h.VariantAsync("B");
        await h.RowAsync(a, store, 5m, reserved: 1m, batch: "1");
        await h.RowAsync(a, store, 2m, batch: "2");
        await h.RowAsync(b, store, 1m, reserved: 3m);
        await h.RowAsync(b, other, 50m);

        var all = await h.Poster.GetLevelsAsync(h.Org, store.Uuid);
        all.Should().BeEquivalentTo([new PosStockLevel(a.Uuid, 6m), new PosStockLevel(b.Uuid, -2m)]);

        (await h.Poster.GetLevelsAsync(h.Org, store.Uuid, [b.Uuid])).Should().Equal(new PosStockLevel(b.Uuid, -2m));
        (await h.Poster.GetLevelsAsync(Guid.NewGuid(), store.Uuid)).Should().BeEmpty("another organization sees nothing");
    }

    [Fact]
    public async Task Services_move_no_stock()
    {
        var h = new PosHarness();
        var w = await h.WarehouseAsync();
        var service = await h.VariantAsync("Gift wrap", stockable: false);

        var result = await h.Poster.PostAsync(h.Sale(w, Guid.NewGuid(), (service, 1m)));

        result.NegativeVariantUuids.Should().BeEmpty();
        (await h.RowsAsync(service, w)).Should().BeEmpty();
        (await h.Db.InventoryLedgerEntries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Malformed_postings_are_refused_before_anything_moves()
    {
        var h = new PosHarness();
        var w = await h.WarehouseAsync();
        var v = await h.VariantAsync();
        await h.RowAsync(v, w, 5m);

        var wrongDirection = h.Sale(w, Guid.NewGuid(), (v, 1m)) with { IsOutbound = false };
        var unknownType    = h.Sale(w, Guid.NewGuid(), (v, 1m)) with { TransactionType = "SALES_SHIP" };
        var zero           = h.Sale(w, Guid.NewGuid(), (v, 0m));
        var stranger       = h.Sale(w, Guid.NewGuid()) with { Lines = [new PosStockLine(Guid.NewGuid(), 1m)] };
        var otherOrg       = h.Sale(w, Guid.NewGuid(), (v, 1m)) with { OrganizationId = Guid.NewGuid() };

        foreach (var bad in new[] { wrongDirection, unknownType, zero, stranger, otherOrg })
            await h.Poster.Invoking(p => p.PostAsync(bad)).Should().ThrowAsync<BadRequestException>();

        (await h.RowsAsync(v, w)).Single().QtyOnHand.Should().Be(5m);
    }
}

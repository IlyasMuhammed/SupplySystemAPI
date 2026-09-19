using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Inventory.Tests;

/// <summary>
/// Taking stock off the books (T-27).
/// <para>
/// Every deduction is matched to a reservation row, and every reservation row names the exact
/// inventory row it came from. That is what keeps batch-tracked stock honest: the batch that
/// leaves the ledger is the batch the picker took off the shelf, not one re-chosen at the last
/// moment.
/// </para>
/// </summary>
public class GoodsIssuePosterTests
{
    private const int User = 7;

    private sealed record Harness(
        InventoryDbContext Db,
        StockReservationService Reservations,
        GoodsIssuePoster Poster,
        Guid VariantUuid)
    {
        internal int VariantId { get; set; }
    }

    private static async Task<Harness> NewHarness()
    {
        var tenant = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var db = new InventoryDbContext(
            new DbContextOptionsBuilder<InventoryDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);

        var category = new ProductCategory { Name = "Cable", Code = "CABLE", IsActive = true };
        db.ProductCategories.Add(category);
        await db.SaveChangesAsync();

        var product = new Product
        {
            Uuid = Guid.NewGuid(), Name = "4mm cable", Sku = $"SKU{Guid.NewGuid():N}"[..12],
            CategoryId = category.Id, IsActive = true, IsBatchTracked = true
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();

        var variant = new ProductVariant
        {
            Uuid = Guid.NewGuid(), ProductId = product.Id, Sku = $"V{Guid.NewGuid():N}"[..12],
            VariantName = "Default", IsDefault = true, IsActive = true
        };
        db.ProductVariants.Add(variant);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var ledger = new InventoryLedgerService(db, NullLogger<InventoryLedgerService>.Instance);

        return new Harness(db, new StockReservationService(db), new GoodsIssuePoster(db, ledger), variant.Uuid)
        {
            VariantId = variant.Id
        };
    }

    private static async Task<Guid> NewWarehouse(Harness h, string name)
    {
        // Qualified: the bare name collides with the SMS.Modules.Warehouse namespace.
        var warehouse = new Domain.Warehouse
        {
            Uuid = Guid.NewGuid(), Name = name, Code = $"W{Guid.NewGuid():N}"[..6], IsActive = true
        };
        h.Db.Warehouses.Add(warehouse);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return warehouse.Uuid;
    }

    private static async Task<int> SeedRow(
        Harness h, Guid warehouseUuid, decimal onHand,
        string? batch = null, DateTime? expiry = null, decimal unitCost = 5m)
    {
        var warehouseId = (await h.Db.Warehouses.AsNoTracking()
            .SingleAsync(w => w.Uuid == warehouseUuid)).Id;

        var item = new InventoryItem
        {
            Uuid = Guid.NewGuid(), VariantId = h.VariantId, WarehouseId = warehouseId,
            BatchNumber = batch, ExpiryDate = expiry, QtyOnHand = onHand, UnitCost = unitCost
        };
        h.Db.InventoryItems.Add(item);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return item.Id;
    }

    private static async Task<InventoryItem> Row(Harness h, int id) =>
        await h.Db.InventoryItems.AsNoTracking().SingleAsync(i => i.Id == id);

    private static GoodsIssuePosting Posting(Guid? toWarehouse = null) =>
        new("DELIVERY", Guid.NewGuid(), "DLV-2026-00001", toWarehouse, "Acme Ltd", "Issued.");

    // ── The deduction ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Issuing_takes_the_stock_off_the_books_and_ends_the_hold()
    {
        var h  = await NewHarness();
        var wh = await NewWarehouse(h, "Central");
        var id = await SeedRow(h, wh, 500m, batch: "B-1");

        var source = Guid.NewGuid();
        await h.Reservations.ReserveAsync(
            ReservationSourceType.Delivery, source,
            [new ReservationRequest(h.VariantUuid, wh, 120m)], User);

        var result = await h.Poster.PostAsync(
            ReservationSourceType.Delivery, source, Posting(), User);

        result.QuantityOut.Should().Be(120m);
        result.QuantityIn.Should().Be(0m);
        result.MovementsPosted.Should().Be(1);

        var row = await Row(h, id);
        row.QtyOnHand.Should().Be(380m, "the units have gone");
        row.QtyReserved.Should().Be(0m, "and stopped being promised");
    }

    [Fact]
    public async Task A_ledger_entry_records_the_movement_against_the_delivery()
    {
        var h  = await NewHarness();
        var wh = await NewWarehouse(h, "Central");
        await SeedRow(h, wh, 500m, batch: "B-1", unitCost: 12.5m);

        var source   = Guid.NewGuid();
        var posting  = Posting();

        await h.Reservations.ReserveAsync(
            ReservationSourceType.Delivery, source,
            [new ReservationRequest(h.VariantUuid, wh, 40m)], User);

        await h.Poster.PostAsync(ReservationSourceType.Delivery, source, posting, User);

        var entry = await h.Db.InventoryLedgerEntries.AsNoTracking().SingleAsync();

        entry.TransactionType.Should().Be("DELIVERY_ISSUE");
        entry.ReferenceType.Should().Be("DELIVERY");
        entry.ReferenceId.Should().Be(posting.ReferenceUuid);
        entry.ReferenceNumber.Should().Be("DLV-2026-00001");
        entry.QuantityOut.Should().Be(40m);
        entry.QuantityIn.Should().BeNull();
        entry.UnitCost.Should().Be(12.5m);
        entry.TransactionValue.Should().Be(500m);
        entry.BalanceAfter.Should().Be(-40m, "the ledger had no prior balance for this row");
        entry.CreatedBy.Should().Be(User);
    }

    [Fact]
    public async Task The_batch_that_was_reserved_is_the_batch_that_leaves()
    {
        // The reason posting goes through the reservations. Re-deriving the rows here would let
        // the stock that leaves the books differ from the stock that left the building.
        var h  = await NewHarness();
        var wh = await NewWarehouse(h, "Central");
        var soon = await SeedRow(h, wh, 30m, batch: "B-SOON", expiry: new DateTime(2026, 1, 1));
        var late = await SeedRow(h, wh, 40m, batch: "B-LATE", expiry: new DateTime(2028, 1, 1));

        var source = Guid.NewGuid();
        await h.Reservations.ReserveAsync(
            ReservationSourceType.Delivery, source,
            [new ReservationRequest(h.VariantUuid, wh, 50m)], User);

        var result = await h.Poster.PostAsync(
            ReservationSourceType.Delivery, source, Posting(), User);

        result.MovementsPosted.Should().Be(2, "one per reserved row");

        // FEFO took all 30 of B-SOON and 20 of B-LATE; the deduction follows exactly.
        (await Row(h, soon)).QtyOnHand.Should().Be(0m);
        (await Row(h, late)).QtyOnHand.Should().Be(20m);

        var batches = await h.Db.InventoryLedgerEntries.AsNoTracking()
            .OrderBy(e => e.LedgerId).Select(e => e.QuantityOut).ToListAsync();
        batches.Sum().Should().Be(50m);
    }

    [Fact]
    public async Task Stock_held_by_somebody_else_is_untouched()
    {
        var h  = await NewHarness();
        var wh = await NewWarehouse(h, "Central");
        var id = await SeedRow(h, wh, 200m, batch: "B-1");

        var mine   = Guid.NewGuid();
        var theirs = Guid.NewGuid();

        await h.Reservations.ReserveAsync(
            ReservationSourceType.Delivery, mine,
            [new ReservationRequest(h.VariantUuid, wh, 50m)], User);
        await h.Reservations.ReserveAsync(
            ReservationSourceType.Mir, theirs,
            [new ReservationRequest(h.VariantUuid, wh, 60m)], User);

        await h.Poster.PostAsync(ReservationSourceType.Delivery, mine, Posting(), User);

        var row = await Row(h, id);
        row.QtyOnHand.Should().Be(150m, "only my 50 left");
        row.QtyReserved.Should().Be(60m, "the MIR still holds its own");

        (await h.Reservations.GetAllocationsAsync(ReservationSourceType.Mir, theirs))
            .Should().ContainSingle();
    }

    [Fact]
    public async Task The_hold_is_closed_as_consumed_not_released()
    {
        // Released means the units came back. Consumed means they left. The arithmetic is the
        // same; the audit trail is not.
        var h  = await NewHarness();
        var wh = await NewWarehouse(h, "Central");
        await SeedRow(h, wh, 100m, batch: "B-1");

        var source = Guid.NewGuid();
        await h.Reservations.ReserveAsync(
            ReservationSourceType.Delivery, source,
            [new ReservationRequest(h.VariantUuid, wh, 40m)], User);

        await h.Poster.PostAsync(ReservationSourceType.Delivery, source, Posting(), User);

        var hold = await h.Db.StockReservations.AsNoTracking()
            .SingleAsync(r => r.SourceUuid == source);

        hold.Status.Should().Be("CONSUMED");
        hold.ReservedQty.Should().Be(0m);
        hold.ReleasedBy.Should().Be(User);
        hold.ReleaseReason.Should().Contain("DLV-2026-00001");
    }

    // ── Not deducting twice ───────────────────────────────────────────────────

    [Fact]
    public async Task Posting_a_second_time_deducts_nothing()
    {
        // The document has no active holds left, so a retry — a duplicated request, a resumed job
        // — finds nothing to deduct rather than taking the stock twice.
        var h  = await NewHarness();
        var wh = await NewWarehouse(h, "Central");
        var id = await SeedRow(h, wh, 500m, batch: "B-1");

        var source = Guid.NewGuid();
        await h.Reservations.ReserveAsync(
            ReservationSourceType.Delivery, source,
            [new ReservationRequest(h.VariantUuid, wh, 120m)], User);

        await h.Poster.PostAsync(ReservationSourceType.Delivery, source, Posting(), User);

        var again = await h.Poster.PostAsync(
            ReservationSourceType.Delivery, source, Posting(), User);

        again.MovementsPosted.Should().Be(0);
        again.QuantityOut.Should().Be(0m);

        (await Row(h, id)).QtyOnHand.Should().Be(380m, "deducted once, not twice");
        (await h.Db.InventoryLedgerEntries.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_document_holding_nothing_posts_nothing()
    {
        var h = await NewHarness();

        var result = await h.Poster.PostAsync(
            ReservationSourceType.Delivery, Guid.NewGuid(), Posting(), User);

        result.MovementsPosted.Should().Be(0);
        (await h.Db.InventoryLedgerEntries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task On_hand_is_never_driven_negative()
    {
        // If the counters have already drifted, deducting further buries the evidence instead of
        // surfacing it.
        var h  = await NewHarness();
        var wh = await NewWarehouse(h, "Central");
        var id = await SeedRow(h, wh, 100m, batch: "B-1");

        var source = Guid.NewGuid();
        await h.Reservations.ReserveAsync(
            ReservationSourceType.Delivery, source,
            [new ReservationRequest(h.VariantUuid, wh, 80m)], User);

        // Something else emptied the shelf behind our back.
        var row = await h.Db.InventoryItems.SingleAsync(i => i.Id == id);
        row.QtyOnHand = 20m;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        await h.Poster.PostAsync(ReservationSourceType.Delivery, source, Posting(), User);

        (await Row(h, id)).QtyOnHand.Should().Be(0m);
    }

    // ── Transfers ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_transfer_moves_the_stock_rather_than_destroying_it()
    {
        var h     = await NewHarness();
        var from  = await NewWarehouse(h, "Central");
        var to    = await NewWarehouse(h, "North");
        var id    = await SeedRow(h, from, 100m, batch: "B-1");

        var source = Guid.NewGuid();
        await h.Reservations.ReserveAsync(
            ReservationSourceType.Delivery, source,
            [new ReservationRequest(h.VariantUuid, from, 60m)], User);

        var result = await h.Poster.PostAsync(
            ReservationSourceType.Delivery, source, Posting(toWarehouse: to), User);

        result.QuantityOut.Should().Be(60m);
        result.QuantityIn.Should().Be(60m);
        result.MovementsPosted.Should().Be(2);

        (await Row(h, id)).QtyOnHand.Should().Be(40m);

        var toId = (await h.Db.Warehouses.AsNoTracking().SingleAsync(w => w.Uuid == to)).Id;
        var landed = await h.Db.InventoryItems.AsNoTracking()
            .SingleAsync(i => i.WarehouseId == toId);

        landed.QtyOnHand.Should().Be(60m);
        landed.BatchNumber.Should().Be("B-1", "a batch-tracked item stays batch-tracked on arrival");
    }

    [Fact]
    public async Task A_transfer_writes_both_halves_to_the_ledger()
    {
        var h    = await NewHarness();
        var from = await NewWarehouse(h, "Central");
        var to   = await NewWarehouse(h, "North");
        await SeedRow(h, from, 100m, batch: "B-1");

        var source = Guid.NewGuid();
        await h.Reservations.ReserveAsync(
            ReservationSourceType.Delivery, source,
            [new ReservationRequest(h.VariantUuid, from, 60m)], User);

        await h.Poster.PostAsync(
            ReservationSourceType.Delivery, source, Posting(toWarehouse: to), User);

        var entries = await h.Db.InventoryLedgerEntries.AsNoTracking().ToListAsync();

        entries.Should().HaveCount(2);
        entries.Should().ContainSingle(e => e.TransactionType == "TRANSFER_OUT" && e.QuantityOut == 60m);
        entries.Should().ContainSingle(e => e.TransactionType == "TRANSFER_IN"  && e.QuantityIn  == 60m);
        entries.Select(e => e.WarehouseId).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task A_transfer_adds_to_a_row_the_destination_already_has()
    {
        var h    = await NewHarness();
        var from = await NewWarehouse(h, "Central");
        var to   = await NewWarehouse(h, "North");
        await SeedRow(h, from, 100m, batch: "B-1");
        var existing = await SeedRow(h, to, 25m, batch: "B-1");

        var source = Guid.NewGuid();
        await h.Reservations.ReserveAsync(
            ReservationSourceType.Delivery, source,
            [new ReservationRequest(h.VariantUuid, from, 60m)], User);

        await h.Poster.PostAsync(
            ReservationSourceType.Delivery, source, Posting(toWarehouse: to), User);

        (await Row(h, existing)).QtyOnHand.Should().Be(85m, "added, not duplicated");

        var toId = (await h.Db.Warehouses.AsNoTracking().SingleAsync(w => w.Uuid == to)).Id;
        (await h.Db.InventoryItems.CountAsync(i => i.WarehouseId == toId)).Should().Be(1);
    }

    [Fact]
    public async Task Several_reserved_rows_landing_on_one_destination_row_all_arrive()
    {
        // Two batches out of one warehouse, one of which the destination already stocks. Without
        // checking the change tracker the second would create a duplicate row and lose the first.
        var h    = await NewHarness();
        var from = await NewWarehouse(h, "Central");
        var to   = await NewWarehouse(h, "North");
        await SeedRow(h, from, 30m, batch: null, expiry: null);
        await SeedRow(h, from, 40m, batch: null, expiry: null);

        var source = Guid.NewGuid();
        await h.Reservations.ReserveAsync(
            ReservationSourceType.Delivery, source,
            [new ReservationRequest(h.VariantUuid, from, 70m)], User);

        await h.Poster.PostAsync(
            ReservationSourceType.Delivery, source, Posting(toWarehouse: to), User);

        var toId = (await h.Db.Warehouses.AsNoTracking().SingleAsync(w => w.Uuid == to)).Id;
        var landed = await h.Db.InventoryItems.AsNoTracking()
            .Where(i => i.WarehouseId == toId).ToListAsync();

        landed.Should().ContainSingle("both reservations share one untracked destination row");
        landed[0].QtyOnHand.Should().Be(70m);
    }

    [Fact]
    public async Task A_transfer_to_a_warehouse_that_does_not_exist_deducts_nothing()
    {
        var h    = await NewHarness();
        var from = await NewWarehouse(h, "Central");
        var id   = await SeedRow(h, from, 100m, batch: "B-1");

        var source = Guid.NewGuid();
        await h.Reservations.ReserveAsync(
            ReservationSourceType.Delivery, source,
            [new ReservationRequest(h.VariantUuid, from, 60m)], User);

        var act = async () => await h.Poster.PostAsync(
            ReservationSourceType.Delivery, source, Posting(toWarehouse: Guid.NewGuid()), User);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*nowhere to land*");

        (await Row(h, id)).QtyOnHand.Should().Be(100m, "checked before anything was deducted");
        (await Row(h, id)).QtyReserved.Should().Be(60m);
        (await h.Db.InventoryLedgerEntries.CountAsync()).Should().Be(0);
    }

    // ── The movement kind (A29-P6-03 §12.1) ──────────────────────────────────

    [Theory]
    [InlineData("SALES_SHIP")]
    [InlineData("SALES_HANDOVER")]
    public async Task A_named_transaction_type_is_what_the_ledger_records(string transactionType)
    {
        var h  = await NewHarness();
        var wh = await NewWarehouse(h, "Central");
        await SeedRow(h, wh, 100m, batch: "B-1");

        var source = Guid.NewGuid();
        await h.Reservations.ReserveAsync(ReservationSourceType.Delivery, source,
            [new ReservationRequest(h.VariantUuid, wh, 40m)], User);

        await h.Poster.PostAsync(ReservationSourceType.Delivery, source,
            Posting() with { TransactionType = transactionType }, User);

        var entry = await h.Db.InventoryLedgerEntries.AsNoTracking().SingleAsync();
        entry.TransactionType.Should().Be(transactionType);
        entry.QuantityOut.Should().Be(40m);
    }

    [Fact]
    public async Task A_transfer_keeps_its_own_two_transaction_types_whatever_is_named()
    {
        var h    = await NewHarness();
        var from = await NewWarehouse(h, "Central");
        var to   = await NewWarehouse(h, "North");
        await SeedRow(h, from, 100m, batch: "B-1");

        var source = Guid.NewGuid();
        await h.Reservations.ReserveAsync(ReservationSourceType.Delivery, source,
            [new ReservationRequest(h.VariantUuid, from, 40m)], User);

        await h.Poster.PostAsync(ReservationSourceType.Delivery, source,
            Posting(to) with { TransactionType = "SALES_SHIP" }, User);

        (await h.Db.InventoryLedgerEntries.AsNoTracking().Select(e => e.TransactionType).ToListAsync())
            .Should().BeEquivalentTo(["TRANSFER_OUT", "TRANSFER_IN"]);
    }
}

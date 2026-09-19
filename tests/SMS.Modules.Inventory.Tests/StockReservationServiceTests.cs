using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Inventory.Tests;

/// <summary>
/// The source-agnostic reservation ledger.
/// <para>
/// Availability is <c>QtyOnHand − QtyReserved</c>, so the counter and these rows have to agree.
/// Nearly every test here is really the same assertion from a different angle: after any
/// operation, the sum of active holds still equals the counter.
/// </para>
/// </summary>
public class StockReservationServiceTests
{
    private const int User = 7;

    private sealed record Harness(InventoryDbContext Db, StockReservationService Service, Guid OrgId);

    private static Harness NewHarness()
    {
        var tenant = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var db = new InventoryDbContext(
            new DbContextOptionsBuilder<InventoryDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);

        return new Harness(db, new StockReservationService(db), tenant.OrganizationId);
    }

    /// <summary>Creates a product, one variant and a stock row in a named warehouse.</summary>
    private static async Task<(Guid VariantUuid, Guid WarehouseUuid, int ItemId)> SeedStock(
        Harness h, decimal onHand, string warehouseName = "Central")
    {
        var category = new ProductCategory { Name = "Cable", Code = "CABLE", IsActive = true };
        h.Db.ProductCategories.Add(category);
        await h.Db.SaveChangesAsync();

        var product = new Product
        {
            Uuid = Guid.NewGuid(), Name = "4mm cable", Sku = $"SKU{Guid.NewGuid():N}"[..12],
            CategoryId = category.Id, IsActive = true
        };
        h.Db.Products.Add(product);
        await h.Db.SaveChangesAsync();

        var variant = new ProductVariant
        {
            Uuid = Guid.NewGuid(), ProductId = product.Id, Sku = $"V{Guid.NewGuid():N}"[..12],
            VariantName = "Default", IsDefault = true, IsActive = true
        };
        h.Db.ProductVariants.Add(variant);

        // Qualified: the bare name collides with the SMS.Modules.Warehouse namespace.
        var warehouse = new Domain.Warehouse
        {
            Uuid = Guid.NewGuid(), Name = warehouseName,
            Code = $"W{Guid.NewGuid():N}"[..6], IsActive = true
        };
        h.Db.Warehouses.Add(warehouse);
        await h.Db.SaveChangesAsync();

        var item = new InventoryItem
        {
            Uuid = Guid.NewGuid(), VariantId = variant.Id, WarehouseId = warehouse.Id,
            QtyOnHand = onHand, QtyReserved = 0
        };
        h.Db.InventoryItems.Add(item);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return (variant.Uuid, warehouse.Uuid, item.Id);
    }

    private static async Task<decimal> ReservedOn(Harness h, int itemId) =>
        (await h.Db.InventoryItems.AsNoTracking().SingleAsync(i => i.Id == itemId)).QtyReserved;

    /// <summary>The invariant: the counter equals the sum of what is actually held.</summary>
    private static async Task AssertCounterMatchesHolds(Harness h, int itemId)
    {
        var counter = await ReservedOn(h, itemId);
        var held = await h.Db.StockReservations.AsNoTracking()
            .Where(r => r.InventoryItemId == itemId && r.Status == "ACTIVE")
            .SumAsync(r => r.ReservedQty);

        counter.Should().Be(held, "QtyReserved must always equal the sum of active reservations");
    }

    // ── Reserving ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reserving_records_the_hold_and_increments_the_counter()
    {
        var h = NewHarness();
        var (variant, warehouse, itemId) = await SeedStock(h, onHand: 500m);
        var source = Guid.NewGuid();

        var result = await h.Service.ReserveAsync(
            ReservationSourceType.Delivery, source,
            [new ReservationRequest(variant, warehouse, 120m)], User);

        result.Succeeded.Should().BeTrue();
        (await ReservedOn(h, itemId)).Should().Be(120m);
        await AssertCounterMatchesHolds(h, itemId);

        var held = await h.Service.GetBySourceAsync(ReservationSourceType.Delivery, source);
        held.Should().ContainSingle();
        held[0].ReservedQty.Should().Be(120m);
        held[0].Status.Should().Be("ACTIVE");
    }

    [Fact]
    public async Task Reserving_more_than_is_available_holds_nothing()
    {
        var h = NewHarness();
        var (variant, warehouse, itemId) = await SeedStock(h, onHand: 50m);

        var result = await h.Service.ReserveAsync(
            ReservationSourceType.Delivery, Guid.NewGuid(),
            [new ReservationRequest(variant, warehouse, 80m)], User);

        result.Succeeded.Should().BeFalse();
        result.Shortfalls.Should().ContainSingle();
        result.Shortfalls.Single().Shortfall.Should().Be(30m);
        result.Shortfalls.Single().Available.Should().Be(50m);

        (await ReservedOn(h, itemId)).Should().Be(0m, "a refused reservation must change nothing");
        (await h.Db.StockReservations.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task One_short_line_prevents_the_whole_reservation()
    {
        // All-or-nothing. Holding stock for some lines leaves a document that cannot be fulfilled
        // and says nothing about it.
        var h = NewHarness();
        var (plenty, plentyWarehouse, plentyItem) = await SeedStock(h, onHand: 500m, warehouseName: "A");
        var (scarce, scarceWarehouse, scarceItem) = await SeedStock(h, onHand: 5m, warehouseName: "B");

        var result = await h.Service.ReserveAsync(
            ReservationSourceType.Delivery, Guid.NewGuid(),
            [
                new ReservationRequest(plenty, plentyWarehouse, 10m),
                new ReservationRequest(scarce, scarceWarehouse, 50m)
            ], User);

        result.Succeeded.Should().BeFalse();
        (await ReservedOn(h, plentyItem)).Should().Be(0m, "the satisfiable line was not held either");
        (await ReservedOn(h, scarceItem)).Should().Be(0m);
    }

    [Fact]
    public async Task Available_stock_accounts_for_what_is_already_held()
    {
        var h = NewHarness();
        var (variant, warehouse, itemId) = await SeedStock(h, onHand: 100m);

        await h.Service.ReserveAsync(ReservationSourceType.Mir, Guid.NewGuid(),
            [new ReservationRequest(variant, warehouse, 70m)], User);

        var second = await h.Service.ReserveAsync(ReservationSourceType.Delivery, Guid.NewGuid(),
            [new ReservationRequest(variant, warehouse, 40m)], User);

        second.Succeeded.Should().BeFalse("only 30 of the 100 remain free");
        second.Shortfalls.Single().Available.Should().Be(30m);
        (await ReservedOn(h, itemId)).Should().Be(70m);
    }

    [Fact]
    public async Task A_reservation_for_nothing_is_refused()
    {
        var h = NewHarness();
        var (variant, warehouse, _) = await SeedStock(h, onHand: 100m);

        var result = await h.Service.ReserveAsync(
            ReservationSourceType.Delivery, Guid.NewGuid(),
            [new ReservationRequest(variant, warehouse, 0m)], User);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task An_item_with_no_stock_record_reports_that_plainly()
    {
        var h = NewHarness();

        var result = await h.Service.ReserveAsync(
            ReservationSourceType.Delivery, Guid.NewGuid(),
            [new ReservationRequest(Guid.NewGuid(), null, 10m)], User);

        result.Succeeded.Should().BeFalse();
        result.Shortfalls.Single().Reason.Should().Contain("No stock record");
    }

    [Fact]
    public async Task With_no_warehouse_named_the_one_with_most_free_stock_is_used()
    {
        var h = NewHarness();
        var (variant, _, smallItem) = await SeedStock(h, onHand: 20m, warehouseName: "Small");

        // A second stock row for the same variant in a bigger warehouse.
        var big = new Domain.Warehouse { Uuid = Guid.NewGuid(), Name = "Big", Code = "BIG", IsActive = true };
        h.Db.Warehouses.Add(big);
        await h.Db.SaveChangesAsync();

        var variantId = await h.Db.ProductVariants.Where(v => v.Uuid == variant).Select(v => v.Id).SingleAsync();
        var bigItem = new InventoryItem
        {
            Uuid = Guid.NewGuid(), VariantId = variantId, WarehouseId = big.Id, QtyOnHand = 300m
        };
        h.Db.InventoryItems.Add(bigItem);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var result = await h.Service.ReserveAsync(
            ReservationSourceType.Delivery, Guid.NewGuid(),
            [new ReservationRequest(variant, null, 100m)], User);

        result.Succeeded.Should().BeTrue();
        (await ReservedOn(h, bigItem.Id)).Should().Be(100m);
        (await ReservedOn(h, smallItem)).Should().Be(0m, "the small warehouse could not have covered it");
    }

    // ── Several sources, one ledger ───────────────────────────────────────────

    [Fact]
    public async Task Different_kinds_of_document_hold_the_same_item_independently()
    {
        // The reason this ledger is source-agnostic. A MIR, a delivery and later a sales order
        // all hold against one counter, and each can find and release only its own rows.
        var h = NewHarness();
        var (variant, warehouse, itemId) = await SeedStock(h, onHand: 500m);

        var mir      = Guid.NewGuid();
        var delivery = Guid.NewGuid();
        var sales    = Guid.NewGuid();

        await h.Service.ReserveAsync(ReservationSourceType.Mir, mir,
            [new ReservationRequest(variant, warehouse, 100m)], User);
        await h.Service.ReserveAsync(ReservationSourceType.Delivery, delivery,
            [new ReservationRequest(variant, warehouse, 50m)], User);
        await h.Service.ReserveAsync(ReservationSourceType.SalesOrder, sales,
            [new ReservationRequest(variant, warehouse, 25m)], User);

        (await ReservedOn(h, itemId)).Should().Be(175m);
        await AssertCounterMatchesHolds(h, itemId);

        // Releasing one leaves the others alone — which a per-module table could not guarantee
        // without every module agreeing on how to sum the others.
        await h.Service.ReleaseBySourceAsync(ReservationSourceType.Delivery, delivery, "cancelled", User);

        (await ReservedOn(h, itemId)).Should().Be(125m);
        await AssertCounterMatchesHolds(h, itemId);

        (await h.Service.GetBySourceAsync(ReservationSourceType.Mir, mir))
            .Should().ContainSingle().Which.Status.Should().Be("ACTIVE");
    }

    [Fact]
    public async Task The_same_id_under_a_different_source_type_is_a_different_hold()
    {
        var h = NewHarness();
        var (variant, warehouse, _) = await SeedStock(h, onHand: 500m);
        var sharedId = Guid.NewGuid();

        await h.Service.ReserveAsync(ReservationSourceType.Mir, sharedId,
            [new ReservationRequest(variant, warehouse, 10m)], User);
        await h.Service.ReserveAsync(ReservationSourceType.Delivery, sharedId,
            [new ReservationRequest(variant, warehouse, 20m)], User);

        var freed = await h.Service.ReleaseBySourceAsync(
            ReservationSourceType.Delivery, sharedId, "cancelled", User);

        freed.Should().Be(1);
        (await h.Service.GetBySourceAsync(ReservationSourceType.Mir, sharedId))
            .Single().Status.Should().Be("ACTIVE");
    }

    // ── Releasing and consuming ───────────────────────────────────────────────

    [Fact]
    public async Task Releasing_gives_the_stock_back_and_records_why()
    {
        var h = NewHarness();
        var (variant, warehouse, itemId) = await SeedStock(h, onHand: 500m);
        var source = Guid.NewGuid();

        await h.Service.ReserveAsync(ReservationSourceType.Delivery, source,
            [new ReservationRequest(variant, warehouse, 120m)], User);

        var freed = await h.Service.ReleaseBySourceAsync(
            ReservationSourceType.Delivery, source, "Delivery cancelled", User);

        freed.Should().Be(1);
        (await ReservedOn(h, itemId)).Should().Be(0m);

        var row = await h.Db.StockReservations.AsNoTracking().SingleAsync();
        row.Status.Should().Be("RELEASED");
        row.ReleaseReason.Should().Be("Delivery cancelled");
        row.ReleasedBy.Should().Be(User);
        row.ReleasedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Releasing_twice_frees_nothing_the_second_time()
    {
        // Retried jobs and double clicks both land here; the counter must not go below what is
        // actually held.
        var h = NewHarness();
        var (variant, warehouse, itemId) = await SeedStock(h, onHand: 500m);
        var source = Guid.NewGuid();

        await h.Service.ReserveAsync(ReservationSourceType.Delivery, source,
            [new ReservationRequest(variant, warehouse, 120m)], User);

        (await h.Service.ReleaseBySourceAsync(ReservationSourceType.Delivery, source, "once", User))
            .Should().Be(1);
        (await h.Service.ReleaseBySourceAsync(ReservationSourceType.Delivery, source, "twice", User))
            .Should().Be(0);

        (await ReservedOn(h, itemId)).Should().Be(0m);
    }

    [Fact]
    public async Task Releasing_a_source_that_never_reserved_anything_is_harmless()
    {
        var h = NewHarness();

        (await h.Service.ReleaseBySourceAsync(
            ReservationSourceType.Delivery, Guid.NewGuid(), "nothing to free", User))
            .Should().Be(0);
    }

    [Fact]
    public async Task Consuming_ends_the_hold_and_says_the_stock_left()
    {
        var h = NewHarness();
        var (variant, warehouse, itemId) = await SeedStock(h, onHand: 500m);
        var source = Guid.NewGuid();

        await h.Service.ReserveAsync(ReservationSourceType.Delivery, source,
            [new ReservationRequest(variant, warehouse, 120m)], User);

        await h.Service.ConsumeBySourceAsync(ReservationSourceType.Delivery, source, User);

        // The counter falls exactly as a release would — the units are gone, not returned. The
        // difference is what the audit trail says, not the arithmetic.
        (await ReservedOn(h, itemId)).Should().Be(0m);
        (await h.Db.StockReservations.AsNoTracking().SingleAsync()).Status.Should().Be("CONSUMED");
    }

    [Fact]
    public async Task The_counter_never_goes_negative()
    {
        // If the rows and the counter have already drifted, refusing to go below zero keeps
        // availability believable rather than compounding the error.
        var h = NewHarness();
        var (variant, warehouse, itemId) = await SeedStock(h, onHand: 500m);
        var source = Guid.NewGuid();

        await h.Service.ReserveAsync(ReservationSourceType.Delivery, source,
            [new ReservationRequest(variant, warehouse, 100m)], User);

        // Simulate drift: something else zeroed the counter behind the ledger's back.
        var item = await h.Db.InventoryItems.SingleAsync(i => i.Id == itemId);
        item.QtyReserved = 0m;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        await h.Service.ReleaseBySourceAsync(ReservationSourceType.Delivery, source, "cancelled", User);

        (await ReservedOn(h, itemId)).Should().Be(0m);
    }

    [Fact]
    public async Task An_empty_request_succeeds_without_touching_anything()
    {
        var h = NewHarness();

        var result = await h.Service.ReserveAsync(
            ReservationSourceType.Delivery, Guid.NewGuid(), [], User);

        result.Succeeded.Should().BeTrue();
        (await h.Db.StockReservations.CountAsync()).Should().Be(0);
    }

    // ── Handing a hold to another document (A29-P6-03) ────────────────────────
    //
    // A sale order holds its stock from confirmation; the delivery that ships it must pick and
    // issue that same stock. Re-parenting the hold keeps the rows and the counter exactly as they
    // were — no window for anyone else to take the units, no batch re-chosen.

    private static async Task<List<StockReservation>> ActiveRows(Harness h, string sourceType, Guid sourceUuid) =>
        await h.Db.StockReservations.AsNoTracking()
            .Where(r => r.SourceType == sourceType && r.SourceUuid == sourceUuid && r.Status == "ACTIVE")
            .OrderBy(r => r.Id)
            .ToListAsync();

    [Fact]
    public async Task Transferring_a_whole_hold_changes_its_owner_and_leaves_the_counter_alone()
    {
        var h = NewHarness();
        var (variant, warehouse, itemId) = await SeedStock(h, onHand: 500m);
        var order = Guid.NewGuid(); var orderLine = Guid.NewGuid();
        var delivery = Guid.NewGuid(); var deliveryLine = Guid.NewGuid();

        await h.Service.ReserveAsync(ReservationSourceType.SalesOrder, order,
            [new ReservationRequest(variant, warehouse, 120m, orderLine)], User,
            expiresAt: DateTime.UtcNow.AddHours(48));

        var moved = await h.Service.TransferLineAsync(
            ReservationSourceType.SalesOrder, order, orderLine,
            ReservationSourceType.Delivery, delivery, deliveryLine, 120m, User);

        moved.Should().Be(120m);
        (await ActiveRows(h, ReservationSourceType.SalesOrder, order)).Should().BeEmpty();

        var row = (await ActiveRows(h, ReservationSourceType.Delivery, delivery)).Should().ContainSingle().Subject;
        row.ReservedQty.Should().Be(120m);
        row.SourceLineUuid.Should().Be(deliveryLine);
        row.InventoryItemId.Should().Be(itemId, "the same stock row");
        row.ExpiresAt.Should().BeNull("a hold that reached a delivery no longer expires");

        (await ReservedOn(h, itemId)).Should().Be(120m, "the units were reserved before and after");
        await AssertCounterMatchesHolds(h, itemId);
        (await h.Db.StockReservations.CountAsync()).Should().Be(1, "moved, not copied");
    }

    [Fact]
    public async Task Transferring_part_of_a_hold_splits_it()
    {
        var h = NewHarness();
        var (variant, warehouse, itemId) = await SeedStock(h, onHand: 500m);
        var order = Guid.NewGuid(); var orderLine = Guid.NewGuid();
        var delivery = Guid.NewGuid(); var deliveryLine = Guid.NewGuid();

        await h.Service.ReserveAsync(ReservationSourceType.SalesOrder, order,
            [new ReservationRequest(variant, warehouse, 100m, orderLine)], User);

        var moved = await h.Service.TransferLineAsync(
            ReservationSourceType.SalesOrder, order, orderLine,
            ReservationSourceType.Delivery, delivery, deliveryLine, 40m, User);

        moved.Should().Be(40m);

        var kept = (await ActiveRows(h, ReservationSourceType.SalesOrder, order)).Should().ContainSingle().Subject;
        kept.ReservedQty.Should().Be(60m);

        var taken = (await ActiveRows(h, ReservationSourceType.Delivery, delivery)).Should().ContainSingle().Subject;
        taken.ReservedQty.Should().Be(40m);
        taken.InventoryItemId.Should().Be(kept.InventoryItemId);
        taken.ReservedBy.Should().Be(User);

        (await ReservedOn(h, itemId)).Should().Be(100m);
        await AssertCounterMatchesHolds(h, itemId);
    }

    [Fact]
    public async Task Transferring_more_than_is_held_moves_only_what_is_there()
    {
        var h = NewHarness();
        var (variant, warehouse, itemId) = await SeedStock(h, onHand: 500m);
        var order = Guid.NewGuid(); var orderLine = Guid.NewGuid();

        await h.Service.ReserveAsync(ReservationSourceType.SalesOrder, order,
            [new ReservationRequest(variant, warehouse, 50m, orderLine)], User);

        var moved = await h.Service.TransferLineAsync(
            ReservationSourceType.SalesOrder, order, orderLine,
            ReservationSourceType.Delivery, Guid.NewGuid(), Guid.NewGuid(), 80m, User);

        moved.Should().Be(50m);
        (await ReservedOn(h, itemId)).Should().Be(50m);
        await AssertCounterMatchesHolds(h, itemId);
    }

    [Fact]
    public async Task Nothing_active_moves_nothing()
    {
        var h = NewHarness();
        var (variant, warehouse, itemId) = await SeedStock(h, onHand: 500m);
        var order = Guid.NewGuid(); var orderLine = Guid.NewGuid();

        await h.Service.ReserveAsync(ReservationSourceType.SalesOrder, order,
            [new ReservationRequest(variant, warehouse, 50m, orderLine)], User);
        await h.Service.ReleaseBySourceAsync(ReservationSourceType.SalesOrder, order, "expired", User);

        (await h.Service.TransferLineAsync(
            ReservationSourceType.SalesOrder, order, orderLine,
            ReservationSourceType.Delivery, Guid.NewGuid(), Guid.NewGuid(), 50m, User)).Should().Be(0m);
        (await h.Service.TransferLineAsync(
            ReservationSourceType.SalesOrder, order, Guid.NewGuid(),
            ReservationSourceType.Delivery, Guid.NewGuid(), Guid.NewGuid(), 50m, User)).Should().Be(0m);
        (await h.Service.TransferLineAsync(
            ReservationSourceType.SalesOrder, order, orderLine,
            ReservationSourceType.Delivery, Guid.NewGuid(), Guid.NewGuid(), 0m, User)).Should().Be(0m);

        (await ReservedOn(h, itemId)).Should().Be(0m);
        await AssertCounterMatchesHolds(h, itemId);
    }

    [Fact]
    public async Task A_partial_transfer_hands_over_the_soonest_expiring_batch_first()
    {
        // The delivery gets the batch the order would have shipped first, so FEFO survives the
        // hand-over rather than being re-decided at the dock.
        var h = NewHarness();
        var (variant, warehouse, soonId) = await SeedStock(h, onHand: 30m);
        var soon = await h.Db.InventoryItems.SingleAsync(i => i.Id == soonId);
        soon.BatchNumber = "B-SOON"; soon.ExpiryDate = new DateTime(2026, 1, 1);
        h.Db.InventoryItems.Add(new InventoryItem
        {
            Uuid = Guid.NewGuid(), VariantId = soon.VariantId, WarehouseId = soon.WarehouseId,
            BatchNumber = "B-LATE", ExpiryDate = new DateTime(2028, 1, 1), QtyOnHand = 40m
        });
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var order = Guid.NewGuid(); var orderLine = Guid.NewGuid();
        await h.Service.ReserveAsync(ReservationSourceType.SalesOrder, order,
            [new ReservationRequest(variant, warehouse, 50m, orderLine)], User);

        var delivery = Guid.NewGuid();
        await h.Service.TransferLineAsync(
            ReservationSourceType.SalesOrder, order, orderLine,
            ReservationSourceType.Delivery, delivery, Guid.NewGuid(), 30m, User);

        var moved = (await ActiveRows(h, ReservationSourceType.Delivery, delivery)).Should().ContainSingle().Subject;
        moved.InventoryItemId.Should().Be(soonId, "the soonest-expiring batch moves first");
        moved.ReservedQty.Should().Be(30m);

        var kept = await ActiveRows(h, ReservationSourceType.SalesOrder, order);
        kept.Should().ContainSingle().Which.ReservedQty.Should().Be(20m, "the 20 from B-LATE stay with the order");
    }
}

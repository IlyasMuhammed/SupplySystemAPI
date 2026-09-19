using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Inventory.Tests;

/// <summary>
/// How a reservation chooses which stock rows to draw from (T-24).
/// <para>
/// Stock is held per (warehouse, bin, batch, serial), so one variant in one warehouse is normally
/// several rows. A reservation spans <b>as many rows as it needs inside one warehouse</b> and
/// never crosses warehouses, and it consumes them <b>FEFO</b> — which is where the pick order is
/// really decided, because the pick list reports this choice rather than making its own.
/// </para>
/// </summary>
public class StockAllocationTests
{
    private const int User = 7;

    private sealed record Harness(InventoryDbContext Db, StockReservationService Service, Guid VariantUuid)
    {
        internal int VariantId { get; set; }
    }

    private static async Task<Harness> NewHarness(bool batchTracked = true)
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
            CategoryId = category.Id, IsActive = true, IsBatchTracked = batchTracked
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

        return new Harness(db, new StockReservationService(db), variant.Uuid) { VariantId = variant.Id };
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

    /// <summary>One stock row: a batch of a given size, optionally with an expiry and a bin.</summary>
    private static async Task<int> SeedRow(
        Harness h, Guid warehouseUuid, decimal onHand,
        string? batch = null, DateTime? expiry = null, int? binId = null)
    {
        var warehouseId = (await h.Db.Warehouses.AsNoTracking()
            .SingleAsync(w => w.Uuid == warehouseUuid)).Id;

        var item = new InventoryItem
        {
            Uuid = Guid.NewGuid(), VariantId = h.VariantId, WarehouseId = warehouseId,
            BinId = binId, BatchNumber = batch, ExpiryDate = expiry, QtyOnHand = onHand
        };
        h.Db.InventoryItems.Add(item);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return item.Id;
    }

    private static Task<List<StockReservation>> Holds(Harness h, Guid sourceUuid) =>
        h.Db.StockReservations.AsNoTracking()
            .Where(r => r.SourceUuid == sourceUuid)
            .OrderBy(r => r.Id)
            .ToListAsync();

    private static async Task<decimal> ReservedOn(Harness h, int itemId) =>
        (await h.Db.InventoryItems.AsNoTracking().SingleAsync(i => i.Id == itemId)).QtyReserved;

    // ── Across rows, inside one warehouse ─────────────────────────────────────

    [Fact]
    public async Task A_line_can_be_covered_from_several_batches_in_the_same_warehouse()
    {
        // The defect this replaced: one row was the limit, so a warehouse holding 40 and 30 of the
        // same item in two batches refused a line for 60 — stock that was on the shelf and free.
        var h  = await NewHarness();
        var wh = await NewWarehouse(h, "Central");
        var a  = await SeedRow(h, wh, 40m, batch: "B-A", expiry: new DateTime(2027, 1, 1));
        var b  = await SeedRow(h, wh, 30m, batch: "B-B", expiry: new DateTime(2026, 1, 1));

        var source = Guid.NewGuid();
        var result = await h.Service.ReserveAsync(
            ReservationSourceType.Delivery, source,
            [new ReservationRequest(h.VariantUuid, wh, 60m)], User);

        result.Succeeded.Should().BeTrue();

        var holds = await Holds(h, source);
        holds.Should().HaveCount(2);
        holds.Sum(r => r.ReservedQty).Should().Be(60m);

        // B-B expires first, so it goes first and is fully used; the balance comes from B-A.
        holds[0].InventoryItemId.Should().Be(b);
        holds[0].ReservedQty.Should().Be(30m);
        holds[1].InventoryItemId.Should().Be(a);
        holds[1].ReservedQty.Should().Be(30m);

        (await ReservedOn(h, b)).Should().Be(30m);
        (await ReservedOn(h, a)).Should().Be(30m);
    }

    [Fact]
    public async Task Availability_is_the_whole_warehouse_not_its_biggest_row()
    {
        var h  = await NewHarness();
        var wh = await NewWarehouse(h, "Central");
        await SeedRow(h, wh, 40m, batch: "B-A");
        await SeedRow(h, wh, 30m, batch: "B-B");

        (await h.Service.GetAvailableAsync([h.VariantUuid], null))[0].Available.Should().Be(70m);
    }

    [Fact]
    public async Task Stock_is_never_drawn_from_two_warehouses_at_once()
    {
        // A picker walking to a second bin is a pick list. A picker walking to a second building
        // is a transfer, and quietly turning one into the other would strand the delivery.
        var h     = await NewHarness();
        var north = await NewWarehouse(h, "North");
        var south = await NewWarehouse(h, "South");
        await SeedRow(h, north, 40m, batch: "N-1");
        await SeedRow(h, south, 30m, batch: "S-1");

        var result = await h.Service.ReserveAsync(
            ReservationSourceType.Delivery, Guid.NewGuid(),
            [new ReservationRequest(h.VariantUuid, null, 60m)], User);

        result.Succeeded.Should().BeFalse();
        result.Shortfalls.Single().Available.Should().Be(40m, "the best single warehouse, not 70");
    }

    [Fact]
    public async Task The_warehouse_with_the_most_free_stock_wins()
    {
        var h     = await NewHarness();
        var north = await NewWarehouse(h, "North");
        var south = await NewWarehouse(h, "South");
        await SeedRow(h, north, 10m, batch: "N-1");
        await SeedRow(h, south, 25m, batch: "S-1");
        await SeedRow(h, south, 25m, batch: "S-2");

        var result = await h.Service.GetAvailableAsync([h.VariantUuid], null);

        result[0].WarehouseName.Should().Be("South");
        result[0].Available.Should().Be(50m, "summed across South's two rows");
    }

    [Fact]
    public async Task Asking_for_a_warehouse_keeps_the_allocation_inside_it()
    {
        var h     = await NewHarness();
        var north = await NewWarehouse(h, "North");
        var south = await NewWarehouse(h, "South");
        await SeedRow(h, north, 20m, batch: "N-1");
        await SeedRow(h, north, 20m, batch: "N-2");
        await SeedRow(h, south, 900m, batch: "S-1");

        var source = Guid.NewGuid();
        var result = await h.Service.ReserveAsync(
            ReservationSourceType.Delivery, source,
            [new ReservationRequest(h.VariantUuid, north, 35m)], User);

        result.Succeeded.Should().BeTrue();
        (await Holds(h, source)).Should().HaveCount(2)
            .And.Subject.Sum(r => r.ReservedQty).Should().Be(35m);
    }

    // ── FEFO, then FIFO ───────────────────────────────────────────────────────

    [Fact]
    public async Task The_batch_that_expires_first_is_taken_first()
    {
        // Not a preference. Stock left to expire is stock written off, and the picker has no way
        // to know which carton is older once it is on the shelf.
        var h  = await NewHarness();
        var wh = await NewWarehouse(h, "Central");
        await SeedRow(h, wh, 50m, batch: "LATE",  expiry: new DateTime(2028, 6, 1));
        await SeedRow(h, wh, 50m, batch: "SOON",  expiry: new DateTime(2026, 6, 1));
        await SeedRow(h, wh, 50m, batch: "MID",   expiry: new DateTime(2027, 6, 1));

        var source = Guid.NewGuid();
        await h.Service.ReserveAsync(
            ReservationSourceType.Delivery, source,
            [new ReservationRequest(h.VariantUuid, wh, 120m)], User);

        var order = await h.Db.StockReservations.AsNoTracking()
            .Where(r => r.SourceUuid == source)
            .OrderBy(r => r.Id)
            .Select(r => r.InventoryItem!.BatchNumber)
            .ToListAsync();

        order.Should().Equal(["SOON", "MID", "LATE"]);
    }

    [Fact]
    public async Task Stock_that_never_expires_is_taken_in_arrival_order()
    {
        // FIFO is the fallback, and a row is created the first time a batch is received, so
        // ascending id is the order the stock turned up in.
        var h     = await NewHarness(batchTracked: false);
        var wh    = await NewWarehouse(h, "Central");
        var first = await SeedRow(h, wh, 20m);
        var then  = await SeedRow(h, wh, 20m);

        var source = Guid.NewGuid();
        await h.Service.ReserveAsync(
            ReservationSourceType.Delivery, source,
            [new ReservationRequest(h.VariantUuid, wh, 30m)], User);

        var holds = await Holds(h, source);
        holds[0].InventoryItemId.Should().Be(first);
        holds[0].ReservedQty.Should().Be(20m);
        holds[1].InventoryItemId.Should().Be(then);
        holds[1].ReservedQty.Should().Be(10m);
    }

    [Fact]
    public async Task Dated_stock_is_taken_before_undated_stock()
    {
        var h     = await NewHarness();
        var wh    = await NewWarehouse(h, "Central");
        var undated = await SeedRow(h, wh, 50m, batch: "NO-DATE");
        var dated   = await SeedRow(h, wh, 50m, batch: "DATED", expiry: new DateTime(2030, 1, 1));

        var source = Guid.NewGuid();
        await h.Service.ReserveAsync(
            ReservationSourceType.Delivery, source,
            [new ReservationRequest(h.VariantUuid, wh, 10m)], User);

        var holds = await Holds(h, source);
        holds.Should().ContainSingle();
        holds[0].InventoryItemId.Should().Be(dated, "a date that can pass beats one that cannot");
        _ = undated;
    }

    // ── Not double-promising ──────────────────────────────────────────────────

    [Fact]
    public async Task A_row_already_partly_held_offers_only_its_remainder()
    {
        var h  = await NewHarness();
        var wh = await NewWarehouse(h, "Central");
        var a  = await SeedRow(h, wh, 40m, batch: "B-A", expiry: new DateTime(2026, 1, 1));
        var b  = await SeedRow(h, wh, 40m, batch: "B-B", expiry: new DateTime(2027, 1, 1));

        await h.Service.ReserveAsync(
            ReservationSourceType.Delivery, Guid.NewGuid(),
            [new ReservationRequest(h.VariantUuid, wh, 30m)], User);

        var second = Guid.NewGuid();
        await h.Service.ReserveAsync(
            ReservationSourceType.Delivery, second,
            [new ReservationRequest(h.VariantUuid, wh, 40m)], User);

        var holds = await Holds(h, second);
        holds.Sum(r => r.ReservedQty).Should().Be(40m);

        (await ReservedOn(h, a)).Should().Be(40m, "filled up by both");
        (await ReservedOn(h, b)).Should().Be(30m);
    }

    [Fact]
    public async Task Two_lines_of_one_document_cannot_both_claim_the_same_units()
    {
        // The counter is not written until the whole reservation commits, so without tracking
        // what earlier lines took, the second line would be planned against stock already spoken
        // for — and the reservation would then hold 80 of 50.
        var h  = await NewHarness();
        var wh = await NewWarehouse(h, "Central");
        await SeedRow(h, wh, 50m, batch: "ONLY");

        var result = await h.Service.ReserveAsync(
            ReservationSourceType.Delivery, Guid.NewGuid(),
            [
                new ReservationRequest(h.VariantUuid, wh, 40m, Guid.NewGuid()),
                new ReservationRequest(h.VariantUuid, wh, 40m, Guid.NewGuid())
            ], User);

        result.Succeeded.Should().BeFalse();
        result.Shortfalls.Should().ContainSingle()
            .Which.Available.Should().Be(10m, "what the first line left behind");
    }

    [Fact]
    public async Task Two_lines_that_together_fit_are_both_held()
    {
        var h  = await NewHarness();
        var wh = await NewWarehouse(h, "Central");
        await SeedRow(h, wh, 50m, batch: "ONLY");

        var source = Guid.NewGuid();
        var result = await h.Service.ReserveAsync(
            ReservationSourceType.Delivery, source,
            [
                new ReservationRequest(h.VariantUuid, wh, 30m, Guid.NewGuid()),
                new ReservationRequest(h.VariantUuid, wh, 20m, Guid.NewGuid())
            ], User);

        result.Succeeded.Should().BeTrue();
        (await Holds(h, source)).Sum(r => r.ReservedQty).Should().Be(50m);
    }

    [Fact]
    public async Task Releasing_a_multi_row_hold_gives_every_row_back()
    {
        var h  = await NewHarness();
        var wh = await NewWarehouse(h, "Central");
        var a  = await SeedRow(h, wh, 40m, batch: "B-A", expiry: new DateTime(2026, 1, 1));
        var b  = await SeedRow(h, wh, 40m, batch: "B-B", expiry: new DateTime(2027, 1, 1));

        var source = Guid.NewGuid();
        await h.Service.ReserveAsync(
            ReservationSourceType.Delivery, source,
            [new ReservationRequest(h.VariantUuid, wh, 60m)], User);

        var freed = await h.Service.ReleaseBySourceAsync(
            ReservationSourceType.Delivery, source, "Cancelled", User);

        freed.Should().Be(2);
        (await ReservedOn(h, a)).Should().Be(0m);
        (await ReservedOn(h, b)).Should().Be(0m);
        (await h.Service.GetAvailableAsync([h.VariantUuid], null))[0].Available.Should().Be(80m);
    }

    // ── Reading the allocation back ───────────────────────────────────────────

    [Fact]
    public async Task Allocations_report_where_each_hold_sits_and_what_is_in_it()
    {
        var h  = await NewHarness();
        var wh = await NewWarehouse(h, "Central");
        await SeedRow(h, wh, 30m, batch: "B-SOON", expiry: new DateTime(2026, 1, 1));
        await SeedRow(h, wh, 40m, batch: "B-LATE", expiry: new DateTime(2027, 1, 1));

        var source   = Guid.NewGuid();
        var lineUuid = Guid.NewGuid();

        await h.Service.ReserveAsync(
            ReservationSourceType.Delivery, source,
            [new ReservationRequest(h.VariantUuid, wh, 50m, lineUuid)], User);

        var allocations = await h.Service.GetAllocationsAsync(ReservationSourceType.Delivery, source);

        allocations.Should().HaveCount(2);

        allocations[0].BatchNumber.Should().Be("B-SOON");
        allocations[0].Quantity.Should().Be(30m);
        allocations[0].ExpiryDate.Should().Be(new DateTime(2026, 1, 1));
        allocations[0].WarehouseName.Should().Be("Central");
        allocations[0].SourceLineUuid.Should().Be(lineUuid);
        allocations[0].BinCode.Should().BeNull("this stock has not been put away to a bin");

        allocations[1].BatchNumber.Should().Be("B-LATE");
        allocations[1].Quantity.Should().Be(20m);
    }

    [Fact]
    public async Task Allocations_name_the_bin_when_the_stock_has_been_put_away()
    {
        var h  = await NewHarness();
        var wh = await NewWarehouse(h, "Central");

        var warehouseId = (await h.Db.Warehouses.AsNoTracking().SingleAsync(w => w.Uuid == wh)).Id;
        var zone = new Zone { WarehouseId = warehouseId, Name = "Chilled", Code = "CH", IsActive = true };
        h.Db.Zones.Add(zone);
        await h.Db.SaveChangesAsync();

        var bin = new Bin { ZoneId = zone.Id, Code = "CH-01-04", IsActive = true };
        h.Db.Bins.Add(bin);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        await SeedRow(h, wh, 30m, batch: "B-1", binId: bin.Id);

        var source = Guid.NewGuid();
        await h.Service.ReserveAsync(
            ReservationSourceType.Delivery, source,
            [new ReservationRequest(h.VariantUuid, wh, 10m)], User);

        var allocation = (await h.Service.GetAllocationsAsync(ReservationSourceType.Delivery, source)).Single();

        allocation.BinCode.Should().Be("CH-01-04");
        allocation.ZoneName.Should().Be("Chilled");
    }

    [Fact]
    public async Task A_released_hold_is_not_an_allocation_any_more()
    {
        // A pick list built from released holds would send someone for stock the delivery gave back.
        var h  = await NewHarness();
        var wh = await NewWarehouse(h, "Central");
        await SeedRow(h, wh, 30m, batch: "B-1");

        var source = Guid.NewGuid();
        await h.Service.ReserveAsync(
            ReservationSourceType.Delivery, source,
            [new ReservationRequest(h.VariantUuid, wh, 10m)], User);
        await h.Service.ReleaseBySourceAsync(ReservationSourceType.Delivery, source, "Cancelled", User);

        (await h.Service.GetAllocationsAsync(ReservationSourceType.Delivery, source))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task A_document_holding_nothing_has_no_allocations()
    {
        var h = await NewHarness();

        (await h.Service.GetAllocationsAsync(ReservationSourceType.Delivery, Guid.NewGuid()))
            .Should().BeEmpty();
    }

    // ── Giving part of one hold back (T-25) ───────────────────────────────────

    [Fact]
    public async Task Part_of_one_hold_can_be_handed_back_leaving_the_rest_active()
    {
        // What a short pick needs: sent for 30, found 22, so 8 stop being promised — and the
        // goods issue that follows then consumes 22 rather than deducting 8 that never moved.
        var h  = await NewHarness();
        var wh = await NewWarehouse(h, "Central");
        var id = await SeedRow(h, wh, 100m, batch: "B-1");

        var source = Guid.NewGuid();
        await h.Service.ReserveAsync(
            ReservationSourceType.Delivery, source,
            [new ReservationRequest(h.VariantUuid, wh, 30m)], User);

        var allocation = (await h.Service.GetAllocationsAsync(ReservationSourceType.Delivery, source)).Single();

        var freed = await h.Service.ReleaseAllocationAsync(
            allocation.ReservationUuid, 8m, "Short picked", User);

        freed.Should().Be(8m);
        (await ReservedOn(h, id)).Should().Be(22m);
        (await h.Service.GetAvailableAsync([h.VariantUuid], null))[0].Available.Should().Be(78m);

        var still = (await h.Service.GetAllocationsAsync(ReservationSourceType.Delivery, source)).Single();
        still.Quantity.Should().Be(22m, "the hold shrank rather than closing");
    }

    [Fact]
    public async Task Handing_back_all_of_a_hold_closes_it()
    {
        var h  = await NewHarness();
        var wh = await NewWarehouse(h, "Central");
        var id = await SeedRow(h, wh, 100m, batch: "B-1");

        var source = Guid.NewGuid();
        await h.Service.ReserveAsync(
            ReservationSourceType.Delivery, source,
            [new ReservationRequest(h.VariantUuid, wh, 30m)], User);

        var allocation = (await h.Service.GetAllocationsAsync(ReservationSourceType.Delivery, source)).Single();

        (await h.Service.ReleaseAllocationAsync(
            allocation.ReservationUuid, 30m, "Nothing on the shelf", User)).Should().Be(30m);

        (await ReservedOn(h, id)).Should().Be(0m);
        (await h.Service.GetAllocationsAsync(ReservationSourceType.Delivery, source)).Should().BeEmpty();

        var row = await h.Db.StockReservations.AsNoTracking()
            .SingleAsync(r => r.UUID == allocation.ReservationUuid);
        row.Status.Should().Be("RELEASED");
        row.ReleaseReason.Should().Be("Nothing on the shelf");
    }

    [Fact]
    public async Task Only_the_bin_that_came_up_short_shrinks()
    {
        // The reason this is addressed by reservation and not by source line: one line's stock is
        // held across several bins, and a shortfall belongs to the bin it happened in.
        var h  = await NewHarness();
        var wh = await NewWarehouse(h, "Central");
        var a  = await SeedRow(h, wh, 30m, batch: "B-A", expiry: new DateTime(2026, 1, 1));
        var b  = await SeedRow(h, wh, 30m, batch: "B-B", expiry: new DateTime(2027, 1, 1));

        var source = Guid.NewGuid();
        await h.Service.ReserveAsync(
            ReservationSourceType.Delivery, source,
            [new ReservationRequest(h.VariantUuid, wh, 50m)], User);

        var allocations = await h.Service.GetAllocationsAsync(ReservationSourceType.Delivery, source);

        await h.Service.ReleaseAllocationAsync(allocations[0].ReservationUuid, 5m, "Damaged", User);

        (await ReservedOn(h, a)).Should().Be(25m, "the short bin");
        (await ReservedOn(h, b)).Should().Be(20m, "untouched");
    }

    [Fact]
    public async Task Handing_back_more_than_is_held_hands_back_only_what_is_there()
    {
        var h  = await NewHarness();
        var wh = await NewWarehouse(h, "Central");
        var id = await SeedRow(h, wh, 100m, batch: "B-1");

        var source = Guid.NewGuid();
        await h.Service.ReserveAsync(
            ReservationSourceType.Delivery, source,
            [new ReservationRequest(h.VariantUuid, wh, 20m)], User);

        var allocation = (await h.Service.GetAllocationsAsync(ReservationSourceType.Delivery, source)).Single();

        (await h.Service.ReleaseAllocationAsync(
            allocation.ReservationUuid, 999m, "Over-asked", User)).Should().Be(20m);

        (await ReservedOn(h, id)).Should().Be(0m, "never driven negative");
    }

    [Fact]
    public async Task Handing_back_a_hold_that_is_already_closed_does_nothing()
    {
        // A retried confirmation must not keep handing the same units back, which would push the
        // counter down past what is actually held.
        var h  = await NewHarness();
        var wh = await NewWarehouse(h, "Central");
        var id = await SeedRow(h, wh, 100m, batch: "B-1");

        var source = Guid.NewGuid();
        await h.Service.ReserveAsync(
            ReservationSourceType.Delivery, source,
            [new ReservationRequest(h.VariantUuid, wh, 20m)], User);

        var allocation = (await h.Service.GetAllocationsAsync(ReservationSourceType.Delivery, source)).Single();

        await h.Service.ReleaseAllocationAsync(allocation.ReservationUuid, 20m, "Once", User);

        (await h.Service.ReleaseAllocationAsync(
            allocation.ReservationUuid, 20m, "Twice", User)).Should().Be(0m);

        (await ReservedOn(h, id)).Should().Be(0m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Handing_back_nothing_is_a_no_op(decimal quantity)
    {
        var h  = await NewHarness();
        var wh = await NewWarehouse(h, "Central");
        var id = await SeedRow(h, wh, 100m, batch: "B-1");

        var source = Guid.NewGuid();
        await h.Service.ReserveAsync(
            ReservationSourceType.Delivery, source,
            [new ReservationRequest(h.VariantUuid, wh, 20m)], User);

        var allocation = (await h.Service.GetAllocationsAsync(ReservationSourceType.Delivery, source)).Single();

        (await h.Service.ReleaseAllocationAsync(
            allocation.ReservationUuid, quantity, "Nothing", User)).Should().Be(0m);

        (await ReservedOn(h, id)).Should().Be(20m);
    }

    [Fact]
    public async Task An_unknown_reservation_hands_back_nothing()
    {
        var h = await NewHarness();

        (await h.Service.ReleaseAllocationAsync(Guid.NewGuid(), 10m, "Unknown", User))
            .Should().Be(0m);
    }

    // ── A29-P4-01 §4.4 — SALES_ORDER is source-agnostic, same as every other source type ──────

    [Fact]
    public async Task Fefo_allocation_works_identically_for_a_sales_order_source()
    {
        // The same defect-fix test as The_batch_that_expires_first_is_taken_first above, run
        // against SALES_ORDER instead of DELIVERY — proving the allocation logic never special-
        // cases by source type, exactly as StockReservation's own doc comment says it shouldn't.
        var h  = await NewHarness();
        var wh = await NewWarehouse(h, "Central");
        await SeedRow(h, wh, 50m, batch: "LATE", expiry: new DateTime(2028, 6, 1));
        await SeedRow(h, wh, 50m, batch: "SOON", expiry: new DateTime(2026, 6, 1));
        await SeedRow(h, wh, 50m, batch: "MID",  expiry: new DateTime(2027, 6, 1));

        var source = Guid.NewGuid();
        var result = await h.Service.ReserveAsync(
            ReservationSourceType.SalesOrder, source,
            [new ReservationRequest(h.VariantUuid, wh, 120m)], User);

        result.Succeeded.Should().BeTrue();
        var order = await h.Db.StockReservations.AsNoTracking()
            .Where(r => r.SourceUuid == source)
            .OrderBy(r => r.Id)
            .Select(r => r.InventoryItem!.BatchNumber)
            .ToListAsync();
        order.Should().Equal(["SOON", "MID", "LATE"]);
    }

    [Fact]
    public async Task A_sales_order_reservation_can_span_several_batches_in_one_warehouse()
    {
        var h  = await NewHarness();
        var wh = await NewWarehouse(h, "Central");
        await SeedRow(h, wh, 40m, batch: "B-A", expiry: new DateTime(2027, 1, 1));
        await SeedRow(h, wh, 30m, batch: "B-B", expiry: new DateTime(2026, 1, 1));

        var source = Guid.NewGuid();
        var result = await h.Service.ReserveAsync(
            ReservationSourceType.SalesOrder, source,
            [new ReservationRequest(h.VariantUuid, wh, 60m)], User);

        result.Succeeded.Should().BeTrue();
        (await Holds(h, source)).Sum(r => r.ReservedQty).Should().Be(60m);
    }

    [Fact]
    public async Task A_reservation_with_no_expiry_leaves_expires_at_null()
    {
        var h  = await NewHarness();
        var wh = await NewWarehouse(h, "Central");
        await SeedRow(h, wh, 10m, batch: "B-1");

        var source = Guid.NewGuid();
        await h.Service.ReserveAsync(
            ReservationSourceType.Delivery, source,
            [new ReservationRequest(h.VariantUuid, wh, 5m)], User);

        (await Holds(h, source)).Single().ExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task A_sales_order_reservation_carries_the_expiry_it_was_given()
    {
        var h  = await NewHarness();
        var wh = await NewWarehouse(h, "Central");
        await SeedRow(h, wh, 10m, batch: "B-1");

        var expiresAt = DateTime.UtcNow.AddHours(72);
        var source = Guid.NewGuid();
        await h.Service.ReserveAsync(
            ReservationSourceType.SalesOrder, source,
            [new ReservationRequest(h.VariantUuid, wh, 5m)], User, expiresAt);

        (await Holds(h, source)).Single().ExpiresAt.Should().Be(expiresAt);
    }

    [Fact]
    public async Task Every_row_a_multi_batch_sales_order_reservation_creates_carries_the_same_expiry()
    {
        var h  = await NewHarness();
        var wh = await NewWarehouse(h, "Central");
        await SeedRow(h, wh, 40m, batch: "B-A", expiry: new DateTime(2027, 1, 1));
        await SeedRow(h, wh, 30m, batch: "B-B", expiry: new DateTime(2026, 1, 1));

        var expiresAt = DateTime.UtcNow.AddHours(72);
        var source = Guid.NewGuid();
        await h.Service.ReserveAsync(
            ReservationSourceType.SalesOrder, source,
            [new ReservationRequest(h.VariantUuid, wh, 60m)], User, expiresAt);

        (await Holds(h, source)).Should().OnlyContain(r => r.ExpiresAt == expiresAt);
    }

    [Fact]
    public async Task A_sales_order_reservation_can_be_released_and_consumed_the_same_as_any_other_source()
    {
        var h  = await NewHarness();
        var wh = await NewWarehouse(h, "Central");
        var id = await SeedRow(h, wh, 50m, batch: "B-1");

        var source = Guid.NewGuid();
        await h.Service.ReserveAsync(
            ReservationSourceType.SalesOrder, source,
            [new ReservationRequest(h.VariantUuid, wh, 20m)], User, DateTime.UtcNow.AddHours(72));

        (await h.Service.ConsumeBySourceAsync(ReservationSourceType.SalesOrder, source, User)).Should().Be(1);
        (await ReservedOn(h, id)).Should().Be(0m);
        (await Holds(h, source)).Single().Status.Should().Be("CONSUMED");
    }

    // ── A29-P4-08 §4.3/§4.4 — multi-tenant isolation ────────────────────────────
    // Every other test in this file uses one org per harness, which never actually exercises the
    // tenant filter — a bug that dropped it would pass all of them just the same. This one shares a
    // single database between two orgs, the only way to prove GetAvailableAsync/ReserveAsync
    // (neither of which calls IgnoreQueryFilters) genuinely can't see or touch a variant that
    // belongs to another organization.

    [Fact]
    public async Task Availability_and_reservation_never_see_another_organizations_stock()
    {
        var dbName = Guid.NewGuid().ToString();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();

        var dbA = new InventoryDbContext(
            new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(dbName).Options,
            new StaticTenantContext { OrganizationId = orgA });
        var dbB = new InventoryDbContext(
            new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(dbName).Options,
            new StaticTenantContext { OrganizationId = orgB });

        var category = new ProductCategory { Name = "Cable", Code = "CABLE-A", IsActive = true };
        dbA.ProductCategories.Add(category);
        await dbA.SaveChangesAsync();
        var product = new Product { Uuid = Guid.NewGuid(), Name = "A-Cable", Sku = "SKU-A", CategoryId = category.Id, IsActive = true };
        dbA.Products.Add(product);
        await dbA.SaveChangesAsync();
        var variant = new ProductVariant { Uuid = Guid.NewGuid(), ProductId = product.Id, Sku = "V-A", VariantName = "Default", IsDefault = true, IsActive = true };
        dbA.ProductVariants.Add(variant);
        await dbA.SaveChangesAsync();
        var warehouse = new Domain.Warehouse { Uuid = Guid.NewGuid(), Name = "WH-A", Code = "WHA", IsActive = true };
        dbA.Warehouses.Add(warehouse);
        await dbA.SaveChangesAsync();
        dbA.InventoryItems.Add(new InventoryItem { Uuid = Guid.NewGuid(), VariantId = variant.Id, WarehouseId = warehouse.Id, QtyOnHand = 100m });
        await dbA.SaveChangesAsync();
        dbA.ChangeTracker.Clear();

        var serviceB = new StockReservationService(dbB);

        // Org B asks about org A's own variant uuid — the ambient filter means org B's InventoryItems
        // query simply never reaches org A's row, not that it reaches it and rejects it.
        (await serviceB.GetAvailableAsync([variant.Uuid], warehouseUuid: null)).Should().BeEmpty();

        var result = await serviceB.ReserveAsync(
            ReservationSourceType.SalesOrder, Guid.NewGuid(),
            [new ReservationRequest(variant.Uuid, null, 10m)], User);

        result.Succeeded.Should().BeFalse("org B has no stock of its own for a variant that only exists under org A");
        (await dbA.StockReservations.AsNoTracking().CountAsync()).Should().Be(0, "org B's failed attempt must never touch org A's ledger");
    }
}

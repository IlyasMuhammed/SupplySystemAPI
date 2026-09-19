using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Inventory.Tests;

/// <summary>
/// <c>GetAvailableAsync</c> — the batch read behind a delivery's availability check (T-23).
/// <para>
/// The assertion that matters throughout: <b>it answers exactly what <c>ReserveAsync</c> would
/// do</b>. A preview that promises stock the reservation then refuses is worse than no preview,
/// because the user acts on it.
/// </para>
/// </summary>
public class StockAvailabilityTests
{
    private const int User = 7;

    private sealed record Harness(InventoryDbContext Db, StockReservationService Service);

    private static Harness NewHarness()
    {
        var tenant = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var db = new InventoryDbContext(
            new DbContextOptionsBuilder<InventoryDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);

        return new Harness(db, new StockReservationService(db));
    }

    /// <summary>A variant with no stock anywhere yet.</summary>
    private static async Task<Guid> SeedVariant(Harness h, string name = "4mm cable")
    {
        var category = new ProductCategory { Name = "Cable", Code = $"C{Guid.NewGuid():N}"[..6], IsActive = true };
        h.Db.ProductCategories.Add(category);
        await h.Db.SaveChangesAsync();

        var product = new Product
        {
            Uuid = Guid.NewGuid(), Name = name, Sku = $"SKU{Guid.NewGuid():N}"[..12],
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
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return variant.Uuid;
    }

    /// <summary>Puts stock for an existing variant into a named warehouse.</summary>
    private static async Task<Guid> SeedStockAt(
        Harness h, Guid variantUuid, string warehouseName, decimal onHand, decimal reserved = 0m)
    {
        var variantId = (await h.Db.ProductVariants.AsNoTracking()
            .SingleAsync(v => v.Uuid == variantUuid)).Id;

        // Qualified: the bare name collides with the SMS.Modules.Warehouse namespace.
        var warehouse = new Domain.Warehouse
        {
            Uuid = Guid.NewGuid(), Name = warehouseName,
            Code = $"W{Guid.NewGuid():N}"[..6], IsActive = true
        };
        h.Db.Warehouses.Add(warehouse);
        await h.Db.SaveChangesAsync();

        h.Db.InventoryItems.Add(new InventoryItem
        {
            Uuid = Guid.NewGuid(), VariantId = variantId, WarehouseId = warehouse.Id,
            QtyOnHand = onHand, QtyReserved = reserved
        });
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return warehouse.Uuid;
    }

    // ── What it reports ───────────────────────────────────────────────────────

    [Fact]
    public async Task Availability_is_what_is_on_hand_less_what_is_already_held()
    {
        var h = NewHarness();
        var variant = await SeedVariant(h);
        await SeedStockAt(h, variant, "Central", onHand: 500m);

        await h.Service.ReserveAsync(
            ReservationSourceType.Delivery, Guid.NewGuid(),
            [new ReservationRequest(variant, null, 120m)], User);

        var result = await h.Service.GetAvailableAsync([variant], null);

        result.Should().ContainSingle();
        result[0].VariantUuid.Should().Be(variant);
        result[0].Available.Should().Be(380m);
        result[0].WarehouseName.Should().Be("Central");
    }

    [Fact]
    public async Task Several_variants_are_answered_in_one_call()
    {
        // A delivery asks about every line at once; one query per line would be a round trip per
        // row on a screen that has to feel instant.
        var h = NewHarness();
        var cable = await SeedVariant(h, "Cable");
        var boxes = await SeedVariant(h, "Boxes");
        await SeedStockAt(h, cable, "Central", 500m);
        await SeedStockAt(h, boxes, "Central", 15m);

        var result = await h.Service.GetAvailableAsync([cable, boxes], null);

        result.Should().HaveCount(2);
        result.Single(r => r.VariantUuid == cable).Available.Should().Be(500m);
        result.Single(r => r.VariantUuid == boxes).Available.Should().Be(15m);
    }

    [Fact]
    public async Task A_variant_with_no_stock_row_is_omitted_rather_than_reported_as_zero()
    {
        // The caller has to be able to tell "none left" from "this item is not stocked here" —
        // they are different problems, and the delivery screen words them differently.
        var h = NewHarness();
        var stocked   = await SeedVariant(h, "Stocked");
        var unstocked = await SeedVariant(h, "Unstocked");
        await SeedStockAt(h, stocked, "Central", 10m);

        var result = await h.Service.GetAvailableAsync([stocked, unstocked], null);

        result.Should().ContainSingle().Which.VariantUuid.Should().Be(stocked);
    }

    [Fact]
    public async Task A_row_that_is_fully_reserved_reports_zero_not_a_negative()
    {
        // Pre-existing drift must not read as negative availability, which would flow straight
        // into a shortfall calculation and produce nonsense.
        var h = NewHarness();
        var variant = await SeedVariant(h);
        await SeedStockAt(h, variant, "Central", onHand: 100m, reserved: 140m);

        (await h.Service.GetAvailableAsync([variant], null))[0].Available.Should().Be(0m);
    }

    [Fact]
    public async Task Nothing_asked_for_means_nothing_returned()
    {
        var h = NewHarness();

        (await h.Service.GetAvailableAsync([], null)).Should().BeEmpty();
        (await h.Service.GetAvailableAsync([Guid.Empty], null)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_same_variant_asked_for_twice_is_answered_once()
    {
        var h = NewHarness();
        var variant = await SeedVariant(h);
        await SeedStockAt(h, variant, "Central", 40m);

        (await h.Service.GetAvailableAsync([variant, variant], null)).Should().ContainSingle();
    }

    // ── Which warehouse it speaks for ─────────────────────────────────────────

    [Fact]
    public async Task With_no_warehouse_asked_for_it_reports_the_best_single_one()
    {
        // Not the sum. 40 + 30 across two warehouses cannot ship a line of 60 from either, and
        // ReserveAsync would refuse it — so reporting 70 here would promise what it then refuses.
        var h = NewHarness();
        var variant = await SeedVariant(h);
        await SeedStockAt(h, variant, "North", 40m);
        await SeedStockAt(h, variant, "South", 30m);

        var result = await h.Service.GetAvailableAsync([variant], null);

        result.Should().ContainSingle();
        result[0].Available.Should().Be(40m);
        result[0].WarehouseName.Should().Be("North");
    }

    [Fact]
    public async Task The_best_warehouse_is_the_one_with_the_most_free_stock_not_the_most_on_hand()
    {
        // A big warehouse whose stock is all spoken for is the wrong answer.
        var h = NewHarness();
        var variant = await SeedVariant(h);
        await SeedStockAt(h, variant, "Big",   onHand: 500m, reserved: 495m);
        await SeedStockAt(h, variant, "Small", onHand: 60m,  reserved: 0m);

        var result = await h.Service.GetAvailableAsync([variant], null);

        result[0].WarehouseName.Should().Be("Small");
        result[0].Available.Should().Be(60m);
    }

    [Fact]
    public async Task Asking_about_one_warehouse_ignores_stock_elsewhere()
    {
        // A delivery shipping from a named warehouse cannot draw on another one's shelves.
        var h = NewHarness();
        var variant = await SeedVariant(h);
        var north   = await SeedStockAt(h, variant, "North", 40m);
        await SeedStockAt(h, variant, "South", 900m);

        var result = await h.Service.GetAvailableAsync([variant], north);

        result.Should().ContainSingle();
        result[0].Available.Should().Be(40m);
        result[0].WarehouseUuid.Should().Be(north);
    }

    [Fact]
    public async Task A_variant_not_stocked_in_the_asked_for_warehouse_is_omitted()
    {
        var h = NewHarness();
        var variant = await SeedVariant(h);
        await SeedStockAt(h, variant, "North", 40m);
        var south = await SeedStockAt(h, await SeedVariant(h, "Other"), "South", 900m);

        (await h.Service.GetAvailableAsync([variant], south)).Should().BeEmpty();
    }

    // ── It agrees with the reservation ────────────────────────────────────────

    [Theory]
    [InlineData(40)]   // exactly what is free
    [InlineData(39)]   // under
    [InlineData(41)]   // over
    public async Task What_it_reports_is_exactly_what_a_reservation_will_accept(decimal quantity)
    {
        // The contract between preview and action, stated as a test: a quantity at or below the
        // reported figure reserves, and anything above it does not.
        var h = NewHarness();
        var variant = await SeedVariant(h);
        await SeedStockAt(h, variant, "North", 40m);
        await SeedStockAt(h, variant, "South", 30m);

        var reported = (await h.Service.GetAvailableAsync([variant], null))[0].Available;

        var result = await h.Service.ReserveAsync(
            ReservationSourceType.Delivery, Guid.NewGuid(),
            [new ReservationRequest(variant, null, quantity)], User);

        result.Succeeded.Should().Be(quantity <= reported);
    }

    [Fact]
    public async Task What_it_reports_shrinks_as_reservations_are_taken_against_it()
    {
        var h = NewHarness();
        var variant = await SeedVariant(h);
        await SeedStockAt(h, variant, "Central", 100m);

        decimal Available() => h.Service.GetAvailableAsync([variant], null).Result[0].Available;

        Available().Should().Be(100m);

        var source = Guid.NewGuid();
        await h.Service.ReserveAsync(ReservationSourceType.Delivery, source,
            [new ReservationRequest(variant, null, 60m)], User);

        Available().Should().Be(40m);

        await h.Service.ReleaseBySourceAsync(
            ReservationSourceType.Delivery, source, "Cancelled", User);

        Available().Should().Be(100m, "released units are free again");
    }
}

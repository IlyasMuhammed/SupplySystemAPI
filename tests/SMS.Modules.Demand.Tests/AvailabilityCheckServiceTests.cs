using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>
/// A29-P4-02 §4.3's four scenarios, wired against a real <see cref="StockReservationService"/>
/// (Inventory) rather than a mock — the only way to actually prove the reservation, the FEFO
/// allocation, and the resulting SaleOrderLine fields all agree with one another, exactly the way
/// production has these two modules collaborate through the shared IStockReservationService
/// contract.
/// </summary>
public class AvailabilityCheckServiceTests
{
    private const int User = 7;

    private sealed record Harness(
        DemandDbContext DemandDb, InventoryDbContext InventoryDb,
        AvailabilityCheckService Service, Guid OrgId, Mock<ISaleOrderConfigService> Config);

    private static Harness NewHarness(int ttlHours = 72, bool dropShipEnabled = false)
    {
        var orgId = Guid.NewGuid();
        var demandTenant = new StaticTenantContext { OrganizationId = orgId };
        var inventoryTenant = new StaticTenantContext { OrganizationId = orgId };

        var demandDb = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, demandTenant);
        var inventoryDb = new InventoryDbContext(
            new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, inventoryTenant);

        var stock = new StockReservationService(inventoryDb);

        var config = new Mock<ISaleOrderConfigService>();
        config.Setup(c => c.GetConfigAsync()).ReturnsAsync(new SaleOrderConfigModel
        {
            ReservationTtlHours = ttlHours, DropShipEnabled = dropShipEnabled
        });

        var service = new AvailabilityCheckService(demandDb, stock, config.Object);
        return new Harness(demandDb, inventoryDb, service, orgId, config);
    }

    /// <summary>Seeds one InventoryItem row in a named warehouse and returns the variant's UUID.</summary>
    private static async Task<(Guid VariantUuid, Guid WarehouseUuid)> SeedStock(Harness h, decimal onHand)
    {
        var category = new ProductCategory { Name = "Cable", Code = "CABLE", IsActive = true };
        h.InventoryDb.ProductCategories.Add(category);
        await h.InventoryDb.SaveChangesAsync();

        var product = new Product
        {
            Uuid = Guid.NewGuid(), Name = "4mm cable", Sku = $"SKU{Guid.NewGuid():N}"[..12],
            CategoryId = category.Id, IsActive = true, Status = "ACTIVE"
        };
        h.InventoryDb.Products.Add(product);
        await h.InventoryDb.SaveChangesAsync();

        var variant = new ProductVariant
        {
            Uuid = Guid.NewGuid(), ProductId = product.Id, Sku = $"V{Guid.NewGuid():N}"[..12],
            VariantName = "Default", IsDefault = true, IsActive = true
        };
        h.InventoryDb.ProductVariants.Add(variant);

        var warehouse = new SMS.Modules.Inventory.Domain.Warehouse
        {
            Uuid = Guid.NewGuid(), Name = "Central", Code = $"W{Guid.NewGuid():N}"[..6], IsActive = true
        };
        h.InventoryDb.Warehouses.Add(warehouse);
        await h.InventoryDb.SaveChangesAsync();

        if (onHand > 0)
        {
            h.InventoryDb.InventoryItems.Add(new InventoryItem
            {
                Uuid = Guid.NewGuid(), VariantId = variant.Id, WarehouseId = warehouse.Id,
                QtyOnHand = onHand, QtyReserved = 0
            });
            await h.InventoryDb.SaveChangesAsync();
        }
        h.InventoryDb.ChangeTracker.Clear();

        return (variant.Uuid, warehouse.Uuid);
    }

    private static async Task<Guid> SeedSaleOrder(Harness h, params (Guid VariantUuid, decimal Qty)[] lines)
    {
        var order = new SaleOrder
        {
            SoNumber = $"SO-TEST-{Guid.NewGuid():N}"[..15], PartnerId = Guid.NewGuid(),
            OrderDate = DateTime.UtcNow.Date, CurrencyId = Guid.NewGuid(),
            Status = "DRAFT", DeliveryMode = "SELF_PICKUP", CreatedBy = User
        };
        foreach (var (variantUuid, qty) in lines)
            order.Lines.Add(new SaleOrderLine
            {
                VariantUuid = variantUuid, Quantity = qty, UnitPrice = 10m,
                LineTotal = qty * 10m, Status = "OPEN"
            });

        h.DemandDb.SaleOrders.Add(order);
        await h.DemandDb.SaveChangesAsync();
        h.DemandDb.ChangeTracker.Clear();
        return order.UUID;
    }

    // ── Scenario 1: IN_STOCK ─────────────────────────────────────────────────

    [Fact]
    public async Task Full_availability_reserves_the_whole_line_and_marks_it_in_stock()
    {
        var h = NewHarness();
        var (variant, _) = await SeedStock(h, onHand: 50m);
        var orderUuid = await SeedSaleOrder(h, (variant, 30m));

        await h.Service.CheckAndReserveAsync(orderUuid, User);
        await h.DemandDb.SaveChangesAsync(); // P4-03: CheckAndReserveAsync no longer self-saves; ConfirmAsync owns that now

        var line = await h.DemandDb.SaleOrderLines.AsNoTracking().SingleAsync(l => l.VariantUuid == variant);
        line.FulfillmentMode.Should().Be("IN_STOCK");
        line.Status.Should().Be("RESERVED");
        line.AvailableQtyAtConfirm.Should().Be(50m);
        line.DeficitQty.Should().Be(0m);

        var reservation = await h.InventoryDb.StockReservations.AsNoTracking().SingleAsync();
        reservation.SourceType.Should().Be("SALES_ORDER");
        reservation.SourceUuid.Should().Be(orderUuid);
        reservation.ReservedQty.Should().Be(30m);
    }

    [Fact]
    public async Task Availability_exactly_equal_to_the_order_is_still_in_stock()
    {
        var h = NewHarness();
        var (variant, _) = await SeedStock(h, onHand: 30m);
        var orderUuid = await SeedSaleOrder(h, (variant, 30m));

        await h.Service.CheckAndReserveAsync(orderUuid, User);
        await h.DemandDb.SaveChangesAsync(); // P4-03: CheckAndReserveAsync no longer self-saves; ConfirmAsync owns that now

        var line = await h.DemandDb.SaleOrderLines.AsNoTracking().SingleAsync();
        line.FulfillmentMode.Should().Be("IN_STOCK");
        line.DeficitQty.Should().Be(0m);
    }

    [Fact]
    public async Task Reservation_carries_the_line_uuid_as_its_source_line()
    {
        var h = NewHarness();
        var (variant, _) = await SeedStock(h, onHand: 50m);
        var orderUuid = await SeedSaleOrder(h, (variant, 10m));
        var line = await h.DemandDb.SaleOrderLines.AsNoTracking().SingleAsync();

        await h.Service.CheckAndReserveAsync(orderUuid, User);
        await h.DemandDb.SaveChangesAsync(); // P4-03: CheckAndReserveAsync no longer self-saves; ConfirmAsync owns that now

        (await h.InventoryDb.StockReservations.AsNoTracking().SingleAsync()).SourceLineUuid.Should().Be(line.UUID);
    }

    [Fact]
    public async Task Reservation_expiry_comes_from_the_configured_ttl()
    {
        var h = NewHarness(ttlHours: 48);
        var (variant, _) = await SeedStock(h, onHand: 50m);
        var orderUuid = await SeedSaleOrder(h, (variant, 10m));
        var before = DateTime.UtcNow;

        await h.Service.CheckAndReserveAsync(orderUuid, User);
        await h.DemandDb.SaveChangesAsync(); // P4-03: CheckAndReserveAsync no longer self-saves; ConfirmAsync owns that now

        var reservation = await h.InventoryDb.StockReservations.AsNoTracking().SingleAsync();
        reservation.ExpiresAt.Should().NotBeNull();
        reservation.ExpiresAt!.Value.Should().BeCloseTo(before.AddHours(48), TimeSpan.FromSeconds(5));
    }

    // ── A29-P5-07 — what it reports back for the timeline ────────────────────

    [Fact]
    public async Task Reports_what_it_reserved_per_line_and_nothing_for_a_line_that_reserved_nothing()
    {
        var h = NewHarness();
        var (inStock, _)    = await SeedStock(h, onHand: 100m);
        var (split, _)      = await SeedStock(h, onHand: 60m);
        var (outOfStock, _) = await SeedStock(h, onHand: 0m);
        var orderUuid = await SeedSaleOrder(h, (inStock, 10m), (split, 100m), (outOfStock, 5m));
        var lines = await h.DemandDb.SaleOrderLines.AsNoTracking().ToListAsync();

        var reservations = await h.Service.CheckAndReserveAsync(orderUuid, User);

        reservations.Should().HaveCount(2, "the back-to-back line reserved nothing and is simply absent");
        var inStockReservation = reservations.Single(r => r.VariantUuid == inStock);
        inStockReservation.ReservedQty.Should().Be(10m);
        inStockReservation.LineUuid.Should().Be(lines.Single(l => l.VariantUuid == inStock).UUID);
        var splitReservation = reservations.Single(r => r.VariantUuid == split);
        splitReservation.ReservedQty.Should().Be(60m, "SPLIT reserves what was available, not what was ordered");
        splitReservation.WarehouseName.Should().Be("Central");
        splitReservation.WarehouseUuid.Should().NotBeNull();
    }

    // ── Scenario 2: SPLIT ────────────────────────────────────────────────────

    [Fact]
    public async Task Partial_availability_reserves_what_exists_and_tracks_the_deficit()
    {
        var h = NewHarness();
        var (variant, _) = await SeedStock(h, onHand: 60m);
        var orderUuid = await SeedSaleOrder(h, (variant, 100m));

        await h.Service.CheckAndReserveAsync(orderUuid, User);
        await h.DemandDb.SaveChangesAsync(); // P4-03: CheckAndReserveAsync no longer self-saves; ConfirmAsync owns that now

        var line = await h.DemandDb.SaleOrderLines.AsNoTracking().SingleAsync();
        line.FulfillmentMode.Should().Be("SPLIT");
        line.Status.Should().Be("RESERVED");
        line.AvailableQtyAtConfirm.Should().Be(60m);
        line.DeficitQty.Should().Be(40m);

        (await h.InventoryDb.StockReservations.AsNoTracking().SingleAsync()).ReservedQty.Should().Be(60m);
    }

    // ── Scenario 3: BACK_TO_BACK ─────────────────────────────────────────────

    [Fact]
    public async Task No_availability_reserves_nothing_and_marks_the_full_deficit()
    {
        var h = NewHarness();
        var (variant, _) = await SeedStock(h, onHand: 0m);
        var orderUuid = await SeedSaleOrder(h, (variant, 25m));

        await h.Service.CheckAndReserveAsync(orderUuid, User);
        await h.DemandDb.SaveChangesAsync(); // P4-03: CheckAndReserveAsync no longer self-saves; ConfirmAsync owns that now

        var line = await h.DemandDb.SaleOrderLines.AsNoTracking().SingleAsync();
        line.FulfillmentMode.Should().Be("BACK_TO_BACK");
        line.Status.Should().Be("OPEN");
        line.AvailableQtyAtConfirm.Should().Be(0m);
        line.DeficitQty.Should().Be(25m);

        (await h.InventoryDb.StockReservations.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_variant_with_no_stock_record_at_all_is_treated_as_back_to_back()
    {
        var h = NewHarness();
        var orderUuid = await SeedSaleOrder(h, (Guid.NewGuid(), 10m)); // no SeedStock call at all

        await h.Service.CheckAndReserveAsync(orderUuid, User);
        await h.DemandDb.SaveChangesAsync(); // P4-03: CheckAndReserveAsync no longer self-saves; ConfirmAsync owns that now

        var line = await h.DemandDb.SaleOrderLines.AsNoTracking().SingleAsync();
        line.FulfillmentMode.Should().Be("BACK_TO_BACK");
        line.DeficitQty.Should().Be(10m);
    }

    // ── Scenario 4: DROP_SHIP ────────────────────────────────────────────────

    [Fact]
    public async Task A_line_already_marked_drop_ship_is_left_alone_when_the_org_allows_it()
    {
        var h = NewHarness(dropShipEnabled: true);
        var (variant, _) = await SeedStock(h, onHand: 100m); // plenty on hand — must not matter
        var orderUuid = await SeedSaleOrder(h, (variant, 10m));
        var line = await h.DemandDb.SaleOrderLines.SingleAsync();
        line.FulfillmentMode = "DROP_SHIP";
        await h.DemandDb.SaveChangesAsync();
        h.DemandDb.ChangeTracker.Clear();

        await h.Service.CheckAndReserveAsync(orderUuid, User);
        await h.DemandDb.SaveChangesAsync(); // P4-03: CheckAndReserveAsync no longer self-saves; ConfirmAsync owns that now

        var result = await h.DemandDb.SaleOrderLines.AsNoTracking().SingleAsync();
        result.FulfillmentMode.Should().Be("DROP_SHIP");
        result.AvailableQtyAtConfirm.Should().BeNull();
        result.DeficitQty.Should().Be(10m);
        (await h.InventoryDb.StockReservations.CountAsync()).Should().Be(0, "drop-ship has no warehouse stock impact");
    }

    [Fact]
    public async Task A_line_marked_drop_ship_falls_back_to_a_normal_check_when_the_org_disallows_it()
    {
        var h = NewHarness(dropShipEnabled: false);
        var (variant, _) = await SeedStock(h, onHand: 100m);
        var orderUuid = await SeedSaleOrder(h, (variant, 10m));
        var line = await h.DemandDb.SaleOrderLines.SingleAsync();
        line.FulfillmentMode = "DROP_SHIP";
        await h.DemandDb.SaveChangesAsync();
        h.DemandDb.ChangeTracker.Clear();

        await h.Service.CheckAndReserveAsync(orderUuid, User);
        await h.DemandDb.SaveChangesAsync(); // P4-03: CheckAndReserveAsync no longer self-saves; ConfirmAsync owns that now

        (await h.DemandDb.SaleOrderLines.AsNoTracking().SingleAsync()).FulfillmentMode.Should().Be("IN_STOCK");
    }

    // ── Multi-line orders ────────────────────────────────────────────────────

    [Fact]
    public async Task Each_line_on_a_multi_line_order_is_scored_independently()
    {
        var h = NewHarness();
        var (fullyStocked, _) = await SeedStock(h, onHand: 100m);
        var (outOfStock, _)   = await SeedStock(h, onHand: 0m);
        var orderUuid = await SeedSaleOrder(h, (fullyStocked, 10m), (outOfStock, 5m));

        await h.Service.CheckAndReserveAsync(orderUuid, User);
        await h.DemandDb.SaveChangesAsync(); // P4-03: CheckAndReserveAsync no longer self-saves; ConfirmAsync owns that now

        var lines = await h.DemandDb.SaleOrderLines.AsNoTracking().ToListAsync();
        lines.Single(l => l.VariantUuid == fullyStocked).FulfillmentMode.Should().Be("IN_STOCK");
        lines.Single(l => l.VariantUuid == outOfStock).FulfillmentMode.Should().Be("BACK_TO_BACK");
    }

    // ── Errors / edge cases ──────────────────────────────────────────────────

    [Fact]
    public async Task An_unknown_order_throws_not_found()
    {
        var h = NewHarness();

        var act = () => h.Service.CheckAndReserveAsync(Guid.NewGuid(), User);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Availability_lost_between_preview_and_reserve_is_refused_rather_than_silently_downgraded()
    {
        // A single in-memory DbContext can't produce a genuine race (GetAvailableAsync would
        // simply see the same committed state ReserveAsync does), so this test isolates the one
        // thing it needs to prove — that a false ReserveAsync result surfaces as ConflictException
        // — behind a mocked IStockReservationService instead of the real one the other tests use.
        var orgId = Guid.NewGuid();
        var demandDb = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new StaticTenantContext { OrganizationId = orgId });

        var variant = Guid.NewGuid();
        var order = new SaleOrder
        {
            SoNumber = "SO-RACE-TEST", PartnerId = Guid.NewGuid(), OrderDate = DateTime.UtcNow.Date,
            CurrencyId = Guid.NewGuid(), Status = "DRAFT", DeliveryMode = "SELF_PICKUP", CreatedBy = User,
            Lines = { new SaleOrderLine { VariantUuid = variant, Quantity = 20m, UnitPrice = 10m, LineTotal = 200m, Status = "OPEN" } }
        };
        demandDb.SaleOrders.Add(order);
        await demandDb.SaveChangesAsync();

        var stock = new Mock<IStockReservationService>();
        stock.Setup(s => s.GetAvailableAsync(It.IsAny<IReadOnlyList<Guid>>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new VariantAvailability(variant, Guid.NewGuid(), "Central", 50m)]);
        stock.Setup(s => s.ReserveAsync(
                ReservationSourceType.SalesOrder, order.UUID, It.IsAny<IReadOnlyList<ReservationRequest>>(),
                User, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReservationResult(false,
                [new ReservationLineResult(variant, null, 20m, 0, 20m, 0, "Taken by someone else.")]));

        var config = new Mock<ISaleOrderConfigService>();
        config.Setup(c => c.GetConfigAsync()).ReturnsAsync(new SaleOrderConfigModel { ReservationTtlHours = 72 });

        var service = new AvailabilityCheckService(demandDb, stock.Object, config.Object);

        var act = () => service.CheckAndReserveAsync(order.UUID, User);

        await act.Should().ThrowAsync<ConflictException>();
    }
}

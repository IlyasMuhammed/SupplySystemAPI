using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Services;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;
using SMS.Modules.Logistics.Services;
using SMS.Modules.Material.Data;
using SMS.Modules.Warehouse.Data;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;
using Inv = SMS.Modules.Inventory.Domain;

namespace SMS.Modules.Logistics.Tests.Routes;

/// <summary>
/// A33 C-2 — a DRAFT delivery holds nothing. A32 counted every not-yet-issued delivery, DRAFT included, as "held"
/// for a sale order line, so once A33 auto-creates DRAFT deliveries at confirm every line would read fully held: no
/// manual reserve, DeficitQty zero, and the GRN link reserving nothing when back-to-back goods arrive. The order's
/// own SALES_ORDER hold moves onto a delivery only at release, so only RELEASED-and-later deliveries hold.
/// </summary>
public class SaleOrderHeldQuantityTests
{
    private const int User = 7;

    // ── The Logistics number (ISaleOrderDeliveryQuantities) ──────────────────

    private static async Task SeedDelivery(
        LogisticsDbContext db, Guid saleOrderUuid, Guid soLineUuid, string status, decimal qty,
        string? statusBeforeHold = null, bool deleted = false)
    {
        db.DeliveryOrders.Add(new DeliveryOrder
        {
            UUID             = Guid.NewGuid(),
            TraceId          = Guid.NewGuid(),
            DeliveryNumber   = $"DLV-2026-{Random.Shared.Next(10000, 99999)}",
            Direction        = "OUTBOUND",
            SourceType       = "SALE_ORDER",
            SourceUuid       = saleOrderUuid,
            SaleOrderUuid    = saleOrderUuid,
            DeliveryMode     = "SHIP",
            Status           = status,
            StatusBeforeHold = statusBeforeHold,
            IsDelete         = deleted,
            CreatedBy        = 1,
            CreatedDate      = DateTime.UtcNow,
            Lines =
            {
                new DeliveryOrderLine
                {
                    UUID = Guid.NewGuid(), LineNo = 1, ItemDescription = "4mm cable", QtyOrdered = qty,
                    SoLineUuid = soLineUuid, SourceLineUuid = soLineUuid, CreatedBy = 1, CreatedDate = DateTime.UtcNow
                }
            }
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Held_counts_released_until_issued_and_never_a_draft()
    {
        var (db, _, _) = LogisticsTestDb.New();
        var so   = Guid.NewGuid();
        var line = Guid.NewGuid();

        // One delivery per status, each with its own power of two, so the sum names exactly which ones counted.
        await SeedDelivery(db, so, line, "DRAFT",               1m);
        await SeedDelivery(db, so, line, "RELEASED",            2m);
        await SeedDelivery(db, so, line, "PICKING",             4m);
        await SeedDelivery(db, so, line, "PICKED",              8m);
        await SeedDelivery(db, so, line, "PACKED",             16m);
        await SeedDelivery(db, so, line, "STAGED",             32m);
        await SeedDelivery(db, so, line, "PENDING_APPROVAL",   64m);
        await SeedDelivery(db, so, line, "ON_HOLD",           128m, statusBeforeHold: "PICKED");
        await SeedDelivery(db, so, line, "GOODS_ISSUED",      256m);
        await SeedDelivery(db, so, line, "CANCELLED",         512m);
        await SeedDelivery(db, so, line, "SHORT_CLOSED",     1024m);
        await SeedDelivery(db, so, line, "DELIVERED",        2048m);
        await SeedDelivery(db, so, line, "RELEASED",         4096m, deleted: true);
        await SeedDelivery(db, Guid.NewGuid(), line, "RELEASED", 8192m);   // another order

        ISaleOrderDeliveryQuantities quantities = new SaleOrderDeliveryQuantities(db);

        (await quantities.GetHeldBySoLineAsync(so))[line].Should().Be(2m + 4m + 8m + 16m + 32m + 64m + 128m,
            "a DRAFT delivery has reserved nothing — the order's hold moves onto a delivery only at release");
        (await quantities.GetInFlightBySoLineAsync(so))[line].Should().Be(1m + 2m + 4m + 8m + 16m + 32m + 64m + 128m,
            "in flight is unchanged: a DRAFT still claims the quantity, so it cannot be put on a second delivery");
    }

    [Fact]
    public async Task A_hold_that_interrupted_a_draft_holds_nothing_either()
    {
        // The state machine never holds a DRAFT; this guards rows it did not make (imports, hand edits).
        var (db, _, _) = LogisticsTestDb.New();
        var so   = Guid.NewGuid();
        var line = Guid.NewGuid();
        await SeedDelivery(db, so, line, "ON_HOLD", 5m, statusBeforeHold: "DRAFT");
        await SeedDelivery(db, so, line, "ON_HOLD", 3m, statusBeforeHold: "STAGED");

        ISaleOrderDeliveryQuantities quantities = new SaleOrderDeliveryQuantities(db);

        (await quantities.GetHeldBySoLineAsync(so))[line].Should().Be(3m);
        (await quantities.GetInFlightBySoLineAsync(so))[line].Should().Be(8m);
    }

    [Fact]
    public async Task A_line_on_nothing_but_drafts_holds_nothing_and_is_absent()
    {
        var (db, _, _) = LogisticsTestDb.New();
        var so   = Guid.NewGuid();
        var line = Guid.NewGuid();
        await SeedDelivery(db, so, line, "DRAFT", 10m);

        ISaleOrderDeliveryQuantities quantities = new SaleOrderDeliveryQuantities(db);

        (await quantities.GetHeldBySoLineAsync(so)).Should().NotContainKey(line);
    }

    // ── What Demand does with it (SaleOrderHolds, end to end) ─────────────────

    private sealed class World
    {
        public required LogisticsDbContext Log;
        public required DemandDbContext Demand;
        public required InventoryDbContext Inv;
        public required StockReservationService Stock;
        public required AvailabilityCheckService Availability;
        public required DeliveryFromSourceRepository FromSource;
        public required DeliveryReleaseRepository Release;
        public required SaleOrderReservationService Reservations;
        public required int WarehouseId;

        public void Fresh()
        {
            Log.ChangeTracker.Clear();
            Demand.ChangeTracker.Clear();
            Inv.ChangeTracker.Clear();
        }
    }

    private static World NewWorld()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = new StaticTenantContext { OrganizationId = Guid.NewGuid() };

        DbContextOptions<T> Options<T>() where T : DbContext =>
            new DbContextOptionsBuilder<T>().UseInMemoryDatabase(dbName).Options;

        var log    = LogisticsTestDb.Open(dbName, tenant);
        var demand = new DemandDbContext(Options<DemandDbContext>(), tenant);
        var inv    = new InventoryDbContext(Options<InventoryDbContext>(), tenant);
        var stock  = new StockReservationService(inv);

        var warehouse = new Inv.Warehouse { Uuid = Guid.NewGuid(), Name = "Karachi Main", Code = "KHI", IsActive = true, CreatedBy = 1 };
        inv.Warehouses.Add(warehouse);
        inv.SaveChanges();
        inv.ChangeTracker.Clear();

        var config = new Mock<ISaleOrderConfigService>();
        config.Setup(c => c.GetConfigAsync()).ReturnsAsync(new SaleOrderConfigModel
        {
            ReservationTtlHours = 72, SelfPickupEnabled = true, DropShipEnabled = false, AutoPoEnabled = false
        });

        var numbers = new DocumentNumberGenerator(log, tenant);

        return new World
        {
            Log = log, Demand = demand, Inv = inv, Stock = stock,
            Availability = new AvailabilityCheckService(demand, stock, config.Object),
            FromSource   = new DeliveryFromSourceRepository(
                log, demand, new WarehouseDbContext(Options<WarehouseDbContext>(), tenant),
                new MaterialDbContext(Options<MaterialDbContext>(), tenant), numbers,
                new AddressNormalizer(new FakeCityLookup()), new ProductVariantResolver(inv), stock),
            Release      = new DeliveryReleaseRepository(log, stock, numbers),
            // The real Logistics implementation behind Demand's holds arithmetic — the seam C-2 is about.
            Reservations = new SaleOrderReservationService(
                demand, tenant, stock, config.Object, Mock.Of<IBackgroundJobClient>(), new SaleOrderDeliveryQuantities(log)),
            WarehouseId  = warehouse.Id
        };
    }

    private static async Task<Guid> SeedVariant(World w, decimal onHand)
    {
        var sku = $"CAB-{Guid.NewGuid():N}"[..12];
        var product = new Inv.Product
        {
            Uuid = Guid.NewGuid(), Sku = sku, Name = "4mm cable", UomCode = "EA", IsActive = true, CreatedBy = 1,
            Variants = { new Inv.ProductVariant { Uuid = Guid.NewGuid(), Sku = sku, VariantName = "Default", IsDefault = true, IsActive = true, CreatedBy = 1 } }
        };
        w.Inv.Products.Add(product);
        await w.Inv.SaveChangesAsync();
        w.Inv.InventoryItems.Add(new Inv.InventoryItem
        {
            Uuid = Guid.NewGuid(), VariantId = product.Variants.Single().Id, WarehouseId = w.WarehouseId,
            QtyOnHand = onHand, UnitCost = 25m
        });
        await w.Inv.SaveChangesAsync();
        w.Fresh();
        return product.Variants.Single().Uuid;
    }

    /// <summary>Confirmed the way ConfirmAsync confirms: the real availability check reserves under SALES_ORDER.</summary>
    private static async Task<(Guid Order, Guid Line)> ConfirmOrder(World w, Guid variant, decimal qty)
    {
        var order = new SaleOrder
        {
            SoNumber = $"SO-2026-{Random.Shared.Next(10000, 99999)}", PartnerId = Guid.NewGuid(),
            OrderDate = DateTime.UtcNow.Date, CurrencyId = Guid.NewGuid(), Status = "DRAFT",
            DeliveryMode = "SELF_PICKUP", CreatedBy = User, TraceId = Guid.NewGuid(),
            Lines = { new SaleOrderLine { VariantUuid = variant, Quantity = qty, UnitPrice = 40m, LineTotal = qty * 40m } }
        };
        w.Demand.SaleOrders.Add(order);
        await w.Demand.SaveChangesAsync();

        await w.Availability.CheckAndReserveAsync(order.UUID, User);
        order.Status = "CONFIRMED";
        await w.Demand.SaveChangesAsync();
        w.Fresh();

        return (order.UUID, order.Lines.Single().UUID);
    }

    private static async Task<Guid> DraftDelivery(World w, Guid order)
    {
        var uuid = await w.FromSource.CreateFromSourceAsync(
            new CreateDeliveryFromSourceRequest { SourceType = "SALE_ORDER", SourceUuid = order }, User);
        w.Fresh();
        return uuid;
    }

    [Fact]
    public async Task A_line_whose_only_delivery_is_a_draft_is_not_held_and_can_be_reserved_again()
    {
        // The A33 shape: confirm, the delivery is drafted at once, and the order's own hold is then lost (released by
        // hand here; the expiry sweep does the same). Nothing holds stock for the line any more.
        var w       = NewWorld();
        var variant = await SeedVariant(w, onHand: 10m);
        var (order, line) = await ConfirmOrder(w, variant, 10m);
        await DraftDelivery(w, order);

        var released = await w.Reservations.ReleaseLineAsync(order, line, new ReleaseSaleOrderLineRequest(), User);
        w.Fresh();

        released!.ReservedQty.Should().Be(0m);
        released.ReservableQty.Should().Be(10m, "a DRAFT delivery holds nothing, so the whole line is reservable");
        released.DeliveryIndicator.Should().Be("RED", "nothing holds stock for this line");

        var stored = await w.Demand.SaleOrderLines.AsNoTracking().SingleAsync(l => l.UUID == line);
        stored.DeficitQty.Should().Be(10m,
            "DeficitQty is what the GRN link may still reserve when back-to-back goods arrive — a draft must not zero it");

        var reserved = await w.Reservations.ReserveLineAsync(order, line, new ReserveSaleOrderLineRequest(), User);
        reserved!.ChangedQty.Should().Be(10m);
        reserved.DeliveryIndicator.Should().Be("BLUE");
    }

    [Fact]
    public async Task Once_released_the_delivery_holds_the_line_and_nothing_more_is_reservable()
    {
        // The other half of the rule, unchanged from A32: release moves the order's hold onto the delivery, and those
        // units are still the line's — not reservable a second time.
        var w       = NewWorld();
        var variant = await SeedVariant(w, onHand: 30m);
        var (order, line) = await ConfirmOrder(w, variant, 10m);
        var delivery = await DraftDelivery(w, order);

        (await w.Release.ReleaseAsync(delivery, null, User)).Should().BeTrue();
        w.Fresh();

        (await w.Stock.GetBySourceAsync(ReservationSourceType.SalesOrder, order))
            .Where(h => h.Status == "ACTIVE").Should().BeEmpty("release took the order's hold over");

        var act = () => w.Reservations.ReserveLineAsync(order, line, new ReserveSaleOrderLineRequest(), User);
        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*Nothing is left to reserve*");
    }
}

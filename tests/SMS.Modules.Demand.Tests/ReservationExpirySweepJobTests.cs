using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Services;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>
/// A29-P4-05 §4.4/§5.1 — wired against a real <see cref="StockReservationService"/> (Inventory),
/// same as <see cref="AvailabilityCheckServiceTests"/>, so the release and the resulting
/// SaleOrderLine fields are proven to agree rather than assumed to.
/// </summary>
public class ReservationExpirySweepJobTests
{
    private const int Creator = 11;

    private sealed record Harness(
        DemandDbContext DemandDb, InventoryDbContext InventoryDb,
        ReservationExpirySweepJob Job, Guid OrgId, Mock<INotificationService> Notifications);

    private static Harness NewHarness()
    {
        var orgId = Guid.NewGuid();
        var demandDb = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new StaticTenantContext { OrganizationId = orgId });
        var inventoryDb = new InventoryDbContext(
            new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new StaticTenantContext { OrganizationId = orgId });

        var stock = new StockReservationService(inventoryDb);
        var notifications = new Mock<INotificationService>();
        var job = new ReservationExpirySweepJob(
            demandDb, stock, notifications.Object, NullLogger<ReservationExpirySweepJob>.Instance);

        return new Harness(demandDb, inventoryDb, job, orgId, notifications);
    }

    /// <summary>
    /// Seeds stock, a CONFIRMED-looking sale order line, and reserves it directly through
    /// <see cref="IStockReservationService"/> with the given expiry — the same shape
    /// AvailabilityCheckService.CheckAndReserveAsync would have left behind, without re-driving
    /// that whole flow just to get a reservation on the books.
    /// </summary>
    private static async Task<(Guid OrderUuid, Guid LineUuid, Guid ReservationUuid)> SeedReservedLine(
        Harness h, DateTime? expiresAt, decimal qty = 10m, int soCreatedBy = Creator)
    {
        var category = new ProductCategory { Name = "Cable", Code = $"C{Guid.NewGuid():N}"[..8], IsActive = true };
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

        var warehouse = new Warehouse
        {
            Uuid = Guid.NewGuid(), Name = "Central", Code = $"W{Guid.NewGuid():N}"[..6], IsActive = true
        };
        h.InventoryDb.Warehouses.Add(warehouse);
        await h.InventoryDb.SaveChangesAsync();

        var inventoryItem = new InventoryItem
        {
            Uuid = Guid.NewGuid(), VariantId = variant.Id, WarehouseId = warehouse.Id,
            QtyOnHand = 100m, QtyReserved = qty
        };
        h.InventoryDb.InventoryItems.Add(inventoryItem);
        await h.InventoryDb.SaveChangesAsync();
        var inventoryItemId = inventoryItem.Id;
        h.InventoryDb.ChangeTracker.Clear();

        var order = new SaleOrder
        {
            SoNumber = $"SO-{Guid.NewGuid():N}"[..12], PartnerId = Guid.NewGuid(),
            OrderDate = DateTime.UtcNow.Date, CurrencyId = Guid.NewGuid(),
            Status = "CONFIRMED", DeliveryMode = "SELF_PICKUP", CreatedBy = soCreatedBy,
            Lines =
            {
                new SaleOrderLine
                {
                    VariantUuid = variant.Uuid, Quantity = qty, UnitPrice = 10m, LineTotal = qty * 10m,
                    FulfillmentMode = "IN_STOCK", AvailableQtyAtConfirm = qty, DeficitQty = 0, Status = "RESERVED"
                }
            }
        };
        h.DemandDb.SaleOrders.Add(order);
        await h.DemandDb.SaveChangesAsync();
        var lineUuid = order.Lines.Single().UUID;
        h.DemandDb.ChangeTracker.Clear();

        var result = await h.InventoryDb.StockReservations.AddAsync(new StockReservation
        {
            UUID = Guid.NewGuid(), OrganizationId = h.OrgId, InventoryItemId = inventoryItemId, VariantUuid = variant.Uuid,
            WarehouseId = warehouse.Id, ReservedQty = qty, SourceType = ReservationSourceType.SalesOrder,
            SourceUuid = order.UUID, SourceLineUuid = lineUuid, Status = "ACTIVE",
            ReservedAt = DateTime.UtcNow, ReservedBy = soCreatedBy, ExpiresAt = expiresAt
        });
        await h.InventoryDb.SaveChangesAsync();
        h.InventoryDb.ChangeTracker.Clear();

        return (order.UUID, lineUuid, result.Entity.UUID);
    }

    // ── Release path ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Expired_reservation_is_released_and_its_line_reverts_to_open()
    {
        var h = NewHarness();
        var (_, lineUuid, reservationUuid) = await SeedReservedLine(h, DateTime.UtcNow.AddHours(-1));

        await h.Job.RunAsync();

        var reservation = await h.InventoryDb.StockReservations.AsNoTracking()
            .SingleAsync(r => r.UUID == reservationUuid);
        reservation.Status.Should().Be("RELEASED");

        var line = await h.DemandDb.SaleOrderLines.AsNoTracking().SingleAsync(l => l.UUID == lineUuid);
        line.Status.Should().Be("OPEN");
        line.DeficitQty.Should().Be(10m);
    }

    [Fact]
    public async Task Reservation_not_yet_expired_is_left_untouched()
    {
        var h = NewHarness();
        var (_, lineUuid, reservationUuid) = await SeedReservedLine(h, DateTime.UtcNow.AddHours(5));

        await h.Job.RunAsync();

        (await h.InventoryDb.StockReservations.AsNoTracking().SingleAsync(r => r.UUID == reservationUuid))
            .Status.Should().Be("ACTIVE");
        (await h.DemandDb.SaleOrderLines.AsNoTracking().SingleAsync(l => l.UUID == lineUuid))
            .Status.Should().Be("RESERVED");
    }

    [Fact]
    public async Task A_reservation_with_no_expiry_is_never_swept()
    {
        var h = NewHarness();
        var (_, lineUuid, reservationUuid) = await SeedReservedLine(h, expiresAt: null);

        await h.Job.RunAsync();

        (await h.InventoryDb.StockReservations.AsNoTracking().SingleAsync(r => r.UUID == reservationUuid))
            .Status.Should().Be("ACTIVE");
        (await h.DemandDb.SaleOrderLines.AsNoTracking().SingleAsync(l => l.UUID == lineUuid))
            .Status.Should().Be("RESERVED");
    }

    [Fact]
    public async Task Running_the_sweep_twice_releases_the_same_reservation_only_once()
    {
        var h = NewHarness();
        await SeedReservedLine(h, DateTime.UtcNow.AddHours(-1));

        await h.Job.RunAsync();
        var act = () => h.Job.RunAsync();

        await act.Should().NotThrowAsync();
        (await h.InventoryDb.StockReservations.AsNoTracking().CountAsync(r => r.Status == "ACTIVE"))
            .Should().Be(0);
    }

    [Fact]
    public async Task Only_the_expired_line_reverts_when_a_sibling_reservation_has_not_expired()
    {
        // Both lines belong to the same order but were reserved with different expiries — proves
        // the release targets one reservation, not everything ReleaseBySourceAsync would have hit.
        var h = NewHarness();
        var (orderUuid, expiredLineUuid, _) = await SeedReservedLine(h, DateTime.UtcNow.AddHours(-1));

        var category = await h.InventoryDb.ProductCategories.FirstAsync();
        var product = new Product
        {
            Uuid = Guid.NewGuid(), Name = "Connector", Sku = $"SKU{Guid.NewGuid():N}"[..12],
            CategoryId = category.Id, IsActive = true, Status = "ACTIVE"
        };
        h.InventoryDb.Products.Add(product);
        await h.InventoryDb.SaveChangesAsync();
        var variant2 = new ProductVariant
        {
            Uuid = Guid.NewGuid(), ProductId = product.Id, Sku = $"V{Guid.NewGuid():N}"[..12],
            VariantName = "Default", IsDefault = true, IsActive = true
        };
        h.InventoryDb.ProductVariants.Add(variant2);
        var warehouse = await h.InventoryDb.Warehouses.FirstAsync();
        var inventoryItem2 = new InventoryItem
        {
            Uuid = Guid.NewGuid(), VariantId = variant2.Id, WarehouseId = warehouse.Id, QtyOnHand = 50m, QtyReserved = 5m
        };
        h.InventoryDb.InventoryItems.Add(inventoryItem2);
        await h.InventoryDb.SaveChangesAsync();
        var inventoryItem2Id = inventoryItem2.Id;

        var order = await h.DemandDb.SaleOrders.Include(o => o.Lines).SingleAsync(o => o.UUID == orderUuid);
        var otherLine = new SaleOrderLine
        {
            SaleOrderId = order.Id, VariantUuid = variant2.Uuid, Quantity = 5m, UnitPrice = 8m, LineTotal = 40m,
            FulfillmentMode = "IN_STOCK", AvailableQtyAtConfirm = 5m, DeficitQty = 0, Status = "RESERVED"
        };
        h.DemandDb.SaleOrderLines.Add(otherLine);
        await h.DemandDb.SaveChangesAsync();
        var otherLineUuid = otherLine.UUID;

        h.InventoryDb.StockReservations.Add(new StockReservation
        {
            UUID = Guid.NewGuid(), OrganizationId = h.OrgId, InventoryItemId = inventoryItem2Id, VariantUuid = variant2.Uuid,
            WarehouseId = warehouse.Id, ReservedQty = 5m, SourceType = ReservationSourceType.SalesOrder,
            SourceUuid = orderUuid, SourceLineUuid = otherLineUuid, Status = "ACTIVE",
            ReservedAt = DateTime.UtcNow, ReservedBy = Creator, ExpiresAt = DateTime.UtcNow.AddHours(5)
        });
        await h.InventoryDb.SaveChangesAsync();
        h.InventoryDb.ChangeTracker.Clear();
        h.DemandDb.ChangeTracker.Clear();

        await h.Job.RunAsync();

        (await h.DemandDb.SaleOrderLines.AsNoTracking().SingleAsync(l => l.UUID == expiredLineUuid))
            .Status.Should().Be("OPEN");
        (await h.DemandDb.SaleOrderLines.AsNoTracking().SingleAsync(l => l.UUID == otherLineUuid))
            .Status.Should().Be("RESERVED");
    }

    // ── Warning path ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_reservation_expiring_within_24h_sends_a_warning_to_the_order_creator()
    {
        var h = NewHarness();
        var (orderUuid, _, _) = await SeedReservedLine(h, DateTime.UtcNow.AddHours(10), soCreatedBy: 42);

        await h.Job.RunAsync();

        h.Notifications.Verify(n => n.TryCreateAsync(It.Is<NotificationRequest>(r =>
            r.UserId == 42 && r.Type == "EXPIRING" && r.SendEmail == true &&
            r.EntityType == "SaleOrder" && r.EntityUuid == orderUuid.ToString())), Times.Once);
    }

    [Fact]
    public async Task A_reservation_more_than_24h_from_expiry_is_not_warned_about_yet()
    {
        var h = NewHarness();
        await SeedReservedLine(h, DateTime.UtcNow.AddHours(48));

        await h.Job.RunAsync();

        h.Notifications.Verify(n => n.TryCreateAsync(It.IsAny<NotificationRequest>()), Times.Never);
    }

    [Fact]
    public async Task An_already_expired_reservation_is_not_also_warned_about()
    {
        var h = NewHarness();
        await SeedReservedLine(h, DateTime.UtcNow.AddHours(-1));

        await h.Job.RunAsync();

        h.Notifications.Verify(n => n.TryCreateAsync(It.IsAny<NotificationRequest>()), Times.Never);
    }

    [Fact]
    public async Task Running_the_sweep_twice_warns_only_once_for_the_same_reservation()
    {
        var h = NewHarness();
        await SeedReservedLine(h, DateTime.UtcNow.AddHours(10));

        await h.Job.RunAsync();
        await h.Job.RunAsync();

        h.Notifications.Verify(n => n.TryCreateAsync(It.IsAny<NotificationRequest>()), Times.Once);
    }

    [Fact]
    public async Task Warned_reservations_are_marked_so_a_later_run_will_not_repeat_them()
    {
        var h = NewHarness();
        var (_, _, reservationUuid) = await SeedReservedLine(h, DateTime.UtcNow.AddHours(10));

        await h.Job.RunAsync();

        var reservation = await h.InventoryDb.StockReservations.AsNoTracking()
            .SingleAsync(r => r.UUID == reservationUuid);
        reservation.ExpiryWarningSentAt.Should().NotBeNull();
        reservation.Status.Should().Be("ACTIVE", "a warning does not itself release the hold");
    }

    // ── Cross-org isolation ──────────────────────────────────────────────────

    [Fact]
    public async Task Sweeping_one_org_does_not_touch_another_orgs_expired_reservation()
    {
        // A dynamic ITenantContext this time, not the fixed StaticTenantContext the other tests
        // use: this is the one behaviour that genuinely depends on HangfireTenantScope being set
        // per org group inside the job, which a single fixed tenant can't exercise.
        var httpAccessor = new HttpContextAccessor();
        var tenant = new TenantContextForTest(httpAccessor);
        var demandDb = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);
        var inventoryDb = new InventoryDbContext(
            new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);
        var stock = new StockReservationService(inventoryDb);
        var notifications = new Mock<INotificationService>();
        var job = new ReservationExpirySweepJob(
            demandDb, stock, notifications.Object, NullLogger<ReservationExpirySweepJob>.Instance);

        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();

        HangfireTenantScope.OrganizationId = orgA;
        var hA = new Harness(demandDb, inventoryDb, job, orgA, notifications);
        var (_, lineA, resA) = await SeedReservedLine(hA, DateTime.UtcNow.AddHours(-1));

        HangfireTenantScope.OrganizationId = orgB;
        var hB = new Harness(demandDb, inventoryDb, job, orgB, notifications);
        var (_, lineB, resB) = await SeedReservedLine(hB, DateTime.UtcNow.AddHours(5));

        HangfireTenantScope.OrganizationId = null;

        await job.RunAsync();

        (await inventoryDb.StockReservations.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.UUID == resA))
            .Status.Should().Be("RELEASED");
        (await demandDb.SaleOrderLines.IgnoreQueryFilters().AsNoTracking().SingleAsync(l => l.UUID == lineA))
            .Status.Should().Be("OPEN");

        (await inventoryDb.StockReservations.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.UUID == resB))
            .Status.Should().Be("ACTIVE");
        (await demandDb.SaleOrderLines.IgnoreQueryFilters().AsNoTracking().SingleAsync(l => l.UUID == lineB))
            .Status.Should().Be("RESERVED");

        HangfireTenantScope.OrganizationId = null;
    }

    /// <summary>Mirrors SMS.Shared's internal TenantContext (not accessible from here): no
    /// HttpContext, so OrganizationId falls through to HangfireTenantScope exactly as a bare
    /// recurring job's real tenant context would.</summary>
    private sealed class TenantContextForTest : ITenantContext
    {
        public TenantContextForTest(IHttpContextAccessor accessor) { }
        public Guid OrganizationId => HangfireTenantScope.OrganizationId ?? Guid.Empty;
        public bool IsSuperAdmin => false;
    }
}

using System.Data;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Logistics.Tests.Routes;

/// <summary>
/// A34 PE-04 — what only SQL Server can show (LocalDB, throwaway database; Demand created from its model, Logistics
/// through its real migrations):
/// <list type="bullet">
/// <item>the creator takes the sale order's own application lock (<see cref="SaleOrderLocks.HoldsResource"/>) before it
/// re-reads the order: a cancel holding that lock makes it wait, and once the cancel commits it sees CANCELLED and creates
/// nothing (R-8);</item>
/// <item>a replay creates 0, and racing calls (FGR hook vs sweep vs "Create delivery now") put the accepted quantity on
/// deliveries exactly once.</item>
/// </list>
/// </summary>
public sealed class ProductionDeliveryCreatorSqlServerTests : IAsyncLifetime
{
    private const string Server = @"Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True;";
    private readonly string _connection = $"{Server}Database=A34_ProdDlv_{Guid.NewGuid():N};";
    private readonly Guid _org = Guid.NewGuid();
    private readonly FakeVariantResolver _variants = new();
    private readonly FakeStockReservationService _reservations = new();
    private Guid _route;
    private Guid _so;
    private Guid _line;

    private StaticTenantContext Tenant => new() { OrganizationId = _org };

    private LogisticsDbContext Logistics() =>
        new(new DbContextOptionsBuilder<LogisticsDbContext>().UseSqlServer(_connection).Options, Tenant);

    private DemandDbContext Demand() =>
        new(new DbContextOptionsBuilder<DemandDbContext>().UseSqlServer(_connection).Options, Tenant);

    public async Task InitializeAsync()
    {
        await using (var demand = Demand()) await demand.Database.EnsureCreatedAsync();
        await using (var db = Logistics()) await db.Database.MigrateAsync();

        Guid address;
        await using (var db = Logistics())
        {
            await new FulfillmentRouteSeeder(db).EnsureSeededAsync(_org);
            _route = await db.FulfillmentRoutes.Where(r => r.OrganizationId == _org && r.Code == "MFG_PICK_SHIP")
                .Select(r => r.UUID).SingleAsync();
            var a = new Address
            {
                UUID = Guid.NewGuid(), OrganizationId = _org, Line1 = "Plot 12, Korangi", CityName = "Karachi",
                CountryName = "Pakistan", CreatedBy = 1, CreatedDate = DateTime.UtcNow
            };
            db.Addresses.Add(a);
            await db.SaveChangesAsync();
            address = a.UUID;
        }

        await using (var demand = Demand())
        {
            var order = new SaleOrder
            {
                UUID = Guid.NewGuid(), OrganizationId = _org, TraceId = Guid.NewGuid(), SoNumber = "SO-2026-03400",
                PartnerId = Guid.NewGuid(), OrderDate = new DateTime(2026, 10, 1), CurrencyId = Guid.NewGuid(),
                Status = "CONFIRMED", DeliveryMode = "SHIP", ShippingAddressId = address, CreatedBy = 1
            };
            order.Lines.Add(new SaleOrderLine
            {
                OrganizationId = _org, VariantUuid = _variants.AddVariant("FG-1", "Cabinet"), Quantity = 10m,
                UnitPrice = 40m, LineTotal = 400m, Status = "OPEN", FulfillmentMode = "MAKE_TO_ORDER",
                FulfillmentRouteUuid = _route, FulfillmentRouteCode = "MFG_PICK_SHIP", RouteSource = "VARIANT"
            });
            demand.SaleOrders.Add(order);
            await demand.SaveChangesAsync();
            _so   = order.UUID;
            _line = order.Lines.Single().UUID;
        }
    }

    public async Task DisposeAsync()
    {
        await using var db = Logistics();
        await db.Database.EnsureDeletedAsync();
    }

    private async Task<ProductionDeliveryResult> CallAsync(decimal accepted)
    {
        await using var db     = Logistics();
        await using var demand = Demand();
        var creator = new ProductionDeliveryCreator(
            db, demand, new DocumentNumberGenerator(db, Tenant), _variants, _reservations, new FulfillmentRouteLookup(db));
        return await creator.CreateForProductionOrderAsync(
            _org, new ProductionDeliveryRequest(PoUuid, "PROD-2026-00034", _so, _line, _route, accepted, Guid.NewGuid()), 7);
    }

    private static readonly Guid PoUuid = Guid.NewGuid();

    private async Task<List<DeliveryOrder>> DeliveriesAsync()
    {
        await using var db = Logistics();
        return await db.DeliveryOrders.IgnoreQueryFilters().AsNoTracking().Include(d => d.Lines)
            .Where(d => d.ProductionOrderUuid == PoUuid).ToListAsync();
    }

    [Fact]
    public async Task It_waits_for_a_cancel_holding_the_orders_lock_then_sees_the_order_cancelled_and_creates_nothing()
    {
        await using var cancel = new SqlConnection(_connection);
        await cancel.OpenAsync();
        await using var tx = (SqlTransaction)await cancel.BeginTransactionAsync();
        await using (var take = new SqlCommand(
            "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @res, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 5000; SELECT @r;",
            cancel, tx))
        {
            take.Parameters.Add(new SqlParameter("@res", SqlDbType.NVarChar, 255) { Value = SaleOrderLocks.HoldsResource(_so) });
            Convert.ToInt32(await take.ExecuteScalarAsync()).Should().BeGreaterOrEqualTo(0, "the test holds the order's lock");
        }

        var call = Task.Run(() => CallAsync(10m));
        await Task.Delay(1500);
        call.IsCompleted.Should().BeFalse("the creator must wait for the order's lock before it reads the order");

        await using (var cancelOrder = new SqlCommand(
            "UPDATE demand.sale_orders SET Status = 'CANCELLED' WHERE UUID = @so", cancel, tx))
        {
            cancelOrder.Parameters.AddWithValue("@so", _so);
            (await cancelOrder.ExecuteNonQueryAsync()).Should().Be(1);
        }
        await tx.CommitAsync();

        var result = await call;
        result.QuantityCreated.Should().Be(0m);
        result.SkippedReason.Should().Contain("CANCELLED");
        (await DeliveriesAsync()).Should().BeEmpty("no orphan DRAFT delivery on a cancelled order");
    }

    [Fact]
    public async Task A_replay_creates_nothing_and_racing_calls_deliver_the_accepted_quantity_exactly_once()
    {
        var racing = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => CallAsync(10m)));

        racing.Sum(r => r.QuantityCreated).Should().Be(10m, "the order's lock serializes the FGR hook, the sweep and the button");
        racing.Count(r => r.QuantityCreated > 0).Should().Be(1);

        var replay = await CallAsync(10m);
        replay.QuantityCreated.Should().Be(0m);
        replay.LatestDelivery.Should().NotBeNull();

        var deliveries = await DeliveriesAsync();
        deliveries.Should().ContainSingle();
        deliveries.Single().Lines.Single().QtyOrdered.Should().Be(10m);
        replay.LatestDelivery!.DeliveryUuid.Should().Be(deliveries.Single().UUID);
    }
}

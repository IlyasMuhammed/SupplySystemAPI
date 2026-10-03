using System.Collections.Concurrent;
using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Services;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>
/// A33 PC-04/PC-10 on real SQL Server (LocalDB), where the per-order lock is a real <c>sp_getapplock</c>: two
/// confirmations of one draft at the same moment still reserve it once, and its deliveries are asked for once. The
/// stand-in delivery creator takes the same per-order lock on its own connection, as Logistics' real one does
/// (REV-02). If confirm called it while still holding that lock, it would wait out the timeout and fail.
/// </summary>
public sealed class SaleOrderConfirmDeliveriesConcurrencyTests : IAsyncLifetime
{
    private const string Server = @"Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True;";
    private readonly string _connection = $"{Server}Database=A33_ConfirmRace_{Guid.NewGuid():N};";
    private readonly Guid _org = Guid.NewGuid();

    private DemandDbContext NewContext() => new(
        new DbContextOptionsBuilder<DemandDbContext>().UseSqlServer(_connection).Options,
        new StaticTenantContext { OrganizationId = _org });

    public async Task InitializeAsync()
    {
        await using var db = NewContext();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await using var db = NewContext();
        await db.Database.EnsureDeletedAsync();
    }

    private sealed class Routes(Guid org) : IFulfillmentRouteLookup, IVariantFulfillmentRoutes
    {
        public readonly FulfillmentRouteSummary PickAndShip = new(
            Guid.NewGuid(), "PICK_AND_SHIP", "Pick & Ship", true, true, true, false, true, ["PICK", "GOODS_ISSUE", "SHIP"]);

        public Task<IReadOnlyDictionary<Guid, FulfillmentRouteSummary>> GetAsync(Guid organizationId, IReadOnlyCollection<Guid> routeUuids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, FulfillmentRouteSummary>>(
                organizationId == org && routeUuids.Contains(PickAndShip.Uuid)
                    ? new Dictionary<Guid, FulfillmentRouteSummary> { [PickAndShip.Uuid] = PickAndShip }
                    : new Dictionary<Guid, FulfillmentRouteSummary>());

        public Task<FulfillmentRouteDefaults> GetOrgDefaultsAsync(Guid organizationId, CancellationToken ct = default) =>
            Task.FromResult(organizationId == org ? new FulfillmentRouteDefaults(PickAndShip, null) : new FulfillmentRouteDefaults(null, null));

        public Task<IReadOnlyList<FulfillmentRouteSummary>> ListActiveAsync(Guid organizationId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FulfillmentRouteSummary>>([PickAndShip]);

        public Task<IReadOnlyDictionary<Guid, Guid>> GetRouteUuidsAsync(Guid organizationId, IReadOnlyCollection<Guid> variantUuids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, Guid>>(new Dictionary<Guid, Guid>());
    }

    /// <summary>Counts its calls and, like Logistics' creator, takes the order's lock on its own connection first.</summary>
    private sealed class LockingCreator(Func<DemandDbContext> newContext) : ISaleOrderDeliveryCreator
    {
        public int Calls;
        public readonly ConcurrentBag<string> StatusesSeenUnderLock = [];

        public async Task<SaleOrderDeliveryCreationResult> CreateForConfirmedOrderAsync(
            Guid organizationId, Guid saleOrderUuid, IReadOnlyList<SaleOrderLineRoute> lineRoutes, int userId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            await using var db = newContext();
            var status = await SaleOrderHolds.OneChangeAtATimeAsync(db, saleOrderUuid, () =>
                db.SaleOrders.Where(o => o.UUID == saleOrderUuid).Select(o => o.Status).SingleAsync());
            StatusesSeenUnderLock.Add(status);
            return new SaleOrderDeliveryCreationResult(
                [new CreatedSaleOrderDelivery(Guid.NewGuid(), "DLV-2026-00001", lineRoutes[0].RouteUuid, "PICK_AND_SHIP", "SHIP", null, lineRoutes.Count)], []);
        }
    }

    /// <summary>A stock ledger that is safe across threads and slow to reserve, so both confirms overlap.</summary>
    private sealed class SlowLedger : IStockReservationService
    {
        private readonly ConcurrentDictionary<Guid, (Guid Source, ReservationSummary Hold)> _holds = new();

        public decimal HeldFor(Guid lineUuid) => _holds.Values.Where(h => h.Hold.Status == "ACTIVE" && h.Hold.SourceLineUuid == lineUuid).Sum(h => h.Hold.ReservedQty);

        public async Task<ReservationResult> ReserveAsync(string sourceType, Guid sourceUuid, IReadOnlyList<ReservationRequest> requests,
            int userId, DateTime? expiresAt = null, CancellationToken ct = default)
        {
            await Task.Delay(300, ct);
            foreach (var r in requests)
            {
                var uuid = Guid.NewGuid();
                _holds[uuid] = (sourceUuid, new ReservationSummary(uuid, r.VariantUuid, r.WarehouseUuid ?? Guid.Empty, r.Quantity, "ACTIVE", r.SourceLineUuid));
            }
            return new ReservationResult(true, requests.Select(r => new ReservationLineResult(r.VariantUuid, r.SourceLineUuid, r.Quantity, r.Quantity, 0m, 100m, null)).ToList());
        }

        public Task<IReadOnlyList<VariantAvailability>> GetAvailableAsync(IReadOnlyList<Guid> variantUuids, Guid? warehouseUuid, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<VariantAvailability>>(variantUuids.Select(v => new VariantAvailability(v, Guid.NewGuid(), "Central", 100m)).ToList());

        public Task<IReadOnlyList<ReservationSummary>> GetBySourceAsync(string sourceType, Guid sourceUuid, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ReservationSummary>>(_holds.Values.Where(h => h.Source == sourceUuid).Select(h => h.Hold).ToList());

        public Task<int> ReleaseBySourceAsync(string sourceType, Guid sourceUuid, string reason, int userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> ConsumeBySourceAsync(string sourceType, Guid sourceUuid, int userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<decimal> ConsumeLineAsync(string sourceType, Guid sourceUuid, Guid sourceLineUuid, decimal quantity, int userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<decimal> ReleaseAllocationAsync(Guid reservationUuid, decimal quantity, string reason, int userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<decimal> TransferLineAsync(string fromSourceType, Guid fromSourceUuid, Guid fromSourceLineUuid, string toSourceType, Guid toSourceUuid, Guid? toSourceLineUuid, decimal quantity, int userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<StockAllocation>> GetAllocationsAsync(string sourceType, Guid sourceUuid, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ExpiringReservation>> GetExpiredAsync(string sourceType, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ExpiringReservation>> GetExpiringWithinAsync(string sourceType, TimeSpan within, CancellationToken ct = default) => throw new NotSupportedException();
        public Task MarkExpiryWarningSentAsync(IReadOnlyList<Guid> reservationUuids, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ExpiringReservation?> GetByUuidAsync(Guid reservationUuid, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private SaleOrderService Service(DemandDbContext db, SlowLedger ledger, Routes routes, ISaleOrderDeliveryCreator creator)
    {
        var tenant = new StaticTenantContext { OrganizationId = _org };
        var config = new Mock<ISaleOrderConfigService>();
        config.Setup(c => c.GetConfigAsync()).ReturnsAsync(new SaleOrderConfigModel { ReservationTtlHours = 72 });
        return new SaleOrderService(
            db, tenant, Mock.Of<IOrganizationCurrencyService>(), Mock.Of<IDocumentNumberGenerator>(), Mock.Of<IPricingService>(),
            ledger, Mock.Of<ITimelineService>(), Mock.Of<IBackgroundJobClient>(),
            new AvailabilityCheckService(db, ledger, config.Object), Mock.Of<IPurchaseOrderService>(), Mock.Of<ISaleOrderEmailService>(),
            routes: new EffectiveRouteResolver(routes, routes), deliveryCreator: creator);
    }

    [Fact]
    public async Task Two_simultaneous_confirms_reserve_once_and_ask_for_the_deliveries_once_outside_the_lock()
    {
        Guid orderUuid, lineUuid;
        await using (var seed = NewContext())
        {
            var order = new SaleOrder
            {
                SoNumber = "SO-A33-RACE", PartnerId = Guid.NewGuid(), OrderDate = DateTime.UtcNow.Date, CurrencyId = Guid.NewGuid(),
                Status = "DRAFT", DeliveryMode = "SHIP", ShippingAddressId = Guid.NewGuid(), CreatedBy = 1,
                Lines = { new SaleOrderLine { VariantUuid = Guid.NewGuid(), Quantity = 10m, UnitPrice = 1m, LineTotal = 10m, Status = "OPEN" } }
            };
            seed.SaleOrders.Add(order);
            await seed.SaveChangesAsync();
            (orderUuid, lineUuid) = (order.UUID, order.Lines.Single().UUID);
        }

        var ledger  = new SlowLedger();
        var routes  = new Routes(_org);
        var creator = new LockingCreator(NewContext);

        async Task<object> Attempt()
        {
            await using var db = NewContext();
            try { return (await Service(db, ledger, routes, creator).ConfirmWithResultAsync(orderUuid, 1))!; }
            catch (BadRequestException ex) { return ex; }
            catch (ConflictException ex) { return ex; }
        }

        var results = await Task.WhenAll(Attempt(), Attempt());

        results.OfType<SaleOrderConfirmResultModel>().Should().ContainSingle()
            .Which.DeliveryCreationFailed.Should().BeFalse("the creator got the order's lock, so confirm had released it");
        results.OfType<BadRequestException>().Should().ContainSingle("the loser waits, then reads CONFIRMED");
        ledger.HeldFor(lineUuid).Should().Be(10m, "the order is held once, not twice");
        creator.Calls.Should().Be(1);
        creator.StatusesSeenUnderLock.Should().Equal("CONFIRMED");

        await using var check = NewContext();
        var saved = await check.SaleOrders.Include(o => o.Lines).SingleAsync(o => o.UUID == orderUuid);
        saved.Status.Should().Be("CONFIRMED");
        saved.Lines.Single().FulfillmentRouteUuid.Should().Be(routes.PickAndShip.Uuid);
        saved.Lines.Single().RouteSource.Should().Be("ORG_DEFAULT");
        saved.DeliveryCreationPendingSince.Should().BeNull();
    }
}

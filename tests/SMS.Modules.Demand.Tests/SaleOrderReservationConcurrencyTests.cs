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
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>
/// A32 PE-03/PE-11 — two "reserve the rest of this line" calls racing on one order must not both hold it. Each reads
/// the unreserved balance, then reserves; without the order's application lock both read 10 and both hold 10. Real SQL
/// Server (LocalDB) because the lock is <c>sp_getapplock</c>; the stock ledger is a thread-safe stand-in whose reserve
/// is slow on purpose, so both calls are inside the read-then-reserve window together.
/// </summary>
public sealed class SaleOrderReservationConcurrencyTests : IAsyncLifetime
{
    private const string Server = @"Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True;";
    private readonly string _connection = $"{Server}Database=A32_ReserveRace_{Guid.NewGuid():N};";
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

    private static SaleOrderReservationService Service(DemandDbContext db, SlowLedger ledger)
    {
        var config = new Mock<ISaleOrderConfigService>();
        config.Setup(c => c.GetConfigAsync()).ReturnsAsync(new SaleOrderConfigModel { ReservationTtlHours = 72 });
        return new SaleOrderReservationService(db, new StaticTenantContext { OrganizationId = db.TenantContext.OrganizationId },
            ledger, config.Object, Mock.Of<IBackgroundJobClient>());
    }

    private async Task<(Guid Order, Guid Line)> SeedAsync()
    {
        await using var db = NewContext();
        var order = new SaleOrder
        {
            SoNumber = "SO-RACE-1", PartnerId = Guid.NewGuid(), OrderDate = DateTime.UtcNow.Date, CurrencyId = Guid.NewGuid(),
            Status = "CONFIRMED", DeliveryMode = "SELF_PICKUP", CreatedBy = 1,
            Lines = { new SaleOrderLine { VariantUuid = Guid.NewGuid(), Quantity = 10m, UnitPrice = 1m, LineTotal = 10m, Status = "OPEN", DeficitQty = 10m } }
        };
        db.SaleOrders.Add(order);
        await db.SaveChangesAsync();
        return (order.UUID, order.Lines.Single().UUID);
    }

    [Fact]
    public async Task Two_concurrent_reserve_the_rest_calls_on_one_line_hold_it_once()
    {
        var (orderUuid, lineUuid) = await SeedAsync();
        var ledger = new SlowLedger(available: 100m);

        async Task<object> Attempt()
        {
            await using var db = NewContext();
            try { return (await Service(db, ledger).ReserveLineAsync(orderUuid, lineUuid, new ReserveSaleOrderLineRequest(), 1))!; }
            catch (BadRequestException ex) { return ex; }
        }

        var results = await Task.WhenAll(Attempt(), Attempt());

        ledger.HeldFor(lineUuid).Should().Be(10m, "the line is 10; the second caller must see the first one's hold");
        results.OfType<SaleOrderLineReservationModel>().Should().ContainSingle().Which.Outcome.Should().Be("RESERVED");
        results.OfType<BadRequestException>().Should().ContainSingle("the loser finds nothing left to reserve");
    }

    [Fact]
    public async Task A_cancel_racing_a_reserve_never_leaves_a_hold_on_the_cancelled_order()
    {
        var (orderUuid, lineUuid) = await SeedAsync();
        var ledger = new SlowLedger(available: 100m);

        async Task Reserve()
        {
            await using var db = NewContext();
            try { await Service(db, ledger).ReserveLineAsync(orderUuid, lineUuid, new ReserveSaleOrderLineRequest(), 1); }
            catch (BadRequestException) { /* the cancel won: a cancelled order cannot hold stock */ }
        }

        async Task Cancel()
        {
            await Task.Delay(50);
            await using var db = NewContext();
            await SaleOrderHolds.OneChangeAtATimeAsync(db, orderUuid, async () =>
            {
                await ledger.ReleaseBySourceAsync(ReservationSourceType.SalesOrder, orderUuid, "cancel", 1);
                var order = await db.SaleOrders.SingleAsync(o => o.UUID == orderUuid);
                order.Status = "CANCELLED";
                await db.SaveChangesAsync();
                return true;
            });
        }

        await Task.WhenAll(Reserve(), Cancel());

        ledger.HeldFor(lineUuid).Should().Be(0m);
    }

    /// <summary>A stock ledger that is safe across threads and slow to reserve.</summary>
    private sealed class SlowLedger(decimal available) : IStockReservationService
    {
        private readonly ConcurrentDictionary<Guid, ReservationSummary> _holds = new();
        private readonly ConcurrentDictionary<Guid, Guid> _source = new();
        private readonly object _gate = new();

        public decimal HeldFor(Guid lineUuid) =>
            _holds.Values.Where(h => h.Status == "ACTIVE" && h.SourceLineUuid == lineUuid).Sum(h => h.ReservedQty);

        public async Task<ReservationResult> ReserveAsync(string sourceType, Guid sourceUuid, IReadOnlyList<ReservationRequest> requests,
            int userId, DateTime? expiresAt = null, CancellationToken ct = default)
        {
            await Task.Delay(300, ct); // widen the window between "read the balance" and "hold it"
            lock (_gate)
            {
                foreach (var r in requests)
                {
                    var uuid = Guid.NewGuid();
                    _holds[uuid] = new ReservationSummary(uuid, r.VariantUuid, r.WarehouseUuid ?? Guid.Empty, r.Quantity, "ACTIVE", r.SourceLineUuid);
                    _source[uuid] = sourceUuid;
                }
            }
            return new ReservationResult(true, requests.Select(r => new ReservationLineResult(r.VariantUuid, r.SourceLineUuid, r.Quantity, r.Quantity, 0m, available, null)).ToList());
        }

        public Task<IReadOnlyList<ReservationSummary>> GetBySourceAsync(string sourceType, Guid sourceUuid, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ReservationSummary>>(_holds.Where(h => _source[h.Key] == sourceUuid).Select(h => h.Value).ToList());

        public Task<IReadOnlyList<VariantAvailability>> GetAvailableAsync(IReadOnlyList<Guid> variantUuids, Guid? warehouseUuid, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<VariantAvailability>>(variantUuids.Select(v => new VariantAvailability(v, Guid.NewGuid(), "Central", available)).ToList());

        public Task<int> ReleaseBySourceAsync(string sourceType, Guid sourceUuid, string reason, int userId, CancellationToken ct = default)
        {
            lock (_gate)
            {
                var mine = _holds.Where(h => _source[h.Key] == sourceUuid && h.Value.Status == "ACTIVE").ToList();
                foreach (var (key, hold) in mine) _holds[key] = hold with { Status = "RELEASED" };
                return Task.FromResult(mine.Count);
            }
        }

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
}

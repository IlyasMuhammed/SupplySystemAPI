using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>
/// A30 Phase 4 Track C / decision D1 — re-parenting a SALES_ORDER demand's real hold onto the sale
/// order line the delivery pipeline reads from, with the reservation ledger mocked so the transfer
/// itself (StockReservationService's own job, proven in Inventory's tests) is not re-tested here.
/// </summary>
public class SaleOrderFulfillmentListenerTests
{
    private const int User = 9;

    private sealed record Harness(SaleOrderFulfillmentListener Listener, DemandDbContext Db, Mock<IStockReservationService> Reservations);

    private static Harness NewHarness()
    {
        var db = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new StaticTenantContext { OrganizationId = Guid.NewGuid() });
        var reservations = new Mock<IStockReservationService>();
        var jobs = new Mock<IBackgroundJobClient>();
        jobs.Setup(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("fake-job-id");

        var listener = new SaleOrderFulfillmentListener(db, reservations.Object, jobs.Object);
        return new Harness(listener, db, reservations);
    }

    private static async Task<(Guid OrderUuid, Guid LineUuid, Guid Variant)> Seed(Harness h, decimal quantity = 50m, decimal deficit = 50m)
    {
        var variant = Guid.NewGuid();
        var order = new SaleOrder
        {
            SoNumber = "SO-2026-00060", PartnerId = Guid.NewGuid(), OrderDate = DateTime.UtcNow.Date, CurrencyId = Guid.NewGuid(),
            Status = "CONFIRMED", DeliveryMode = "SELF_PICKUP", CreatedBy = 1,
            Lines = { new SaleOrderLine { VariantUuid = variant, Quantity = quantity, UnitPrice = 40m, LineTotal = quantity * 40m, FulfillmentMode = "BACK_TO_BACK", DeficitQty = deficit, Status = "OPEN" } }
        };
        h.Db.SaleOrders.Add(order);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
        return (order.UUID, order.Lines.Single().UUID, variant);
    }

    private static AllocationRunResult RunResult(Guid demandUuid, Guid orderUuid, Guid lineUuid, Guid variant, decimal reserved, decimal shortage = 0m) =>
        new(variant, null, 1, reserved, 0, shortage,
        [
            new DemandAllocationSummary(demandUuid, AllocationDemandType.SalesOrder, orderUuid, lineUuid, "SO-2026-00060",
                variant, null, reserved + shortage, reserved, 0, 0, shortage, DateTime.UtcNow, AllocationPriority.Normal, AllocationDemandStatus.Open)
        ]);

    [Fact]
    public async Task A_real_hold_is_moved_onto_the_sale_order_line_and_the_deficit_shrinks()
    {
        var h = NewHarness();
        var (orderUuid, lineUuid, variant) = await Seed(h);
        var demandUuid = Guid.NewGuid();
        var recordUuid = Guid.NewGuid();

        h.Reservations.Setup(r => r.GetBySourceAsync(ReservationSourceType.Allocation, demandUuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ReservationSummary(Guid.NewGuid(), variant, Guid.NewGuid(), 30m, "ACTIVE", recordUuid)]);
        h.Reservations.Setup(r => r.TransferLineAsync(
                ReservationSourceType.Allocation, demandUuid, recordUuid,
                ReservationSourceType.SalesOrder, orderUuid, lineUuid, 30m, User, It.IsAny<CancellationToken>()))
            .ReturnsAsync(30m);

        await h.Listener.OnAllocationRunAsync(RunResult(demandUuid, orderUuid, lineUuid, variant, reserved: 30m, shortage: 20m), User);

        var line = await h.Db.SaleOrderLines.AsNoTracking().SingleAsync(l => l.UUID == lineUuid);
        line.DeficitQty.Should().Be(20m, "50 originally short, less the 30 just moved onto this line");
        line.Status.Should().Be("OPEN", "20 is still short, so the line has not fully reserved yet");
    }

    [Fact]
    public async Task Fully_covering_the_deficit_moves_the_line_to_reserved()
    {
        var h = NewHarness();
        var (orderUuid, lineUuid, variant) = await Seed(h, quantity: 30m, deficit: 30m);
        var demandUuid = Guid.NewGuid();
        var recordUuid = Guid.NewGuid();

        h.Reservations.Setup(r => r.GetBySourceAsync(ReservationSourceType.Allocation, demandUuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ReservationSummary(Guid.NewGuid(), variant, Guid.NewGuid(), 30m, "ACTIVE", recordUuid)]);
        h.Reservations.Setup(r => r.TransferLineAsync(
                ReservationSourceType.Allocation, demandUuid, recordUuid,
                ReservationSourceType.SalesOrder, orderUuid, lineUuid, 30m, User, It.IsAny<CancellationToken>()))
            .ReturnsAsync(30m);

        await h.Listener.OnAllocationRunAsync(RunResult(demandUuid, orderUuid, lineUuid, variant, reserved: 30m), User);

        var line = await h.Db.SaleOrderLines.AsNoTracking().SingleAsync(l => l.UUID == lineUuid);
        line.DeficitQty.Should().Be(0m);
        line.Status.Should().Be("RESERVED");
    }

    [Fact]
    public async Task A_demand_for_a_different_kind_is_ignored()
    {
        var h = NewHarness();
        var (orderUuid, lineUuid, variant) = await Seed(h);
        var demandUuid = Guid.NewGuid();

        var result = new AllocationRunResult(variant, null, 1, 30m, 0, 0,
        [
            new DemandAllocationSummary(demandUuid, AllocationDemandType.ProductionMaterial, orderUuid, lineUuid, "PROD-2026-00001",
                variant, null, 30m, 30m, 0, 0, 0, DateTime.UtcNow, AllocationPriority.Normal, AllocationDemandStatus.Open)
        ]);

        await h.Listener.OnAllocationRunAsync(result, User);

        h.Reservations.Verify(r => r.GetBySourceAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Nothing_reserved_yet_is_a_quiet_no_op()
    {
        var h = NewHarness();
        var (orderUuid, lineUuid, variant) = await Seed(h);
        var demandUuid = Guid.NewGuid();

        await h.Listener.OnAllocationRunAsync(RunResult(demandUuid, orderUuid, lineUuid, variant, reserved: 0m, shortage: 50m), User);

        h.Reservations.Verify(r => r.GetBySourceAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

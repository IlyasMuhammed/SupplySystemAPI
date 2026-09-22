using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Services;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;
using SMS.Modules.Logistics.Services;
using SMS.Modules.Logistics.Visibility;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Logistics.Tests;

// A shipped delivery used to stay at GOODS_ISSUED for ever: the carrier's scans moved the consignment
// and nothing moved the delivery, so the sale order never updated and the customer could not be
// invoiced. These tests are the join between the two.
public class DeliveryProgressTests
{
    private const int User = 42;

    private static readonly DateTime T0 = new(2026, 9, 21, 9, 0, 0, DateTimeKind.Utc);

    private sealed record Harness(
        LogisticsDbContext Db,
        DeliveryProgressService Progress,
        Mock<ISaleOrderFulfillmentService> Fulfillment,
        StaticTenantContext Tenant,
        string DbName);

    private static Harness NewHarness(Action<Mock<ISaleOrderFulfillmentService>>? configure = null)
    {
        var (db, tenant, dbName) = LogisticsTestDb.New();

        var fulfillment = new Mock<ISaleOrderFulfillmentService>();
        fulfillment.Setup(f => f.RecordDeliveryCompletedAsync(It.IsAny<DeliveryCompletion>()))
                   .ReturnsAsync(new FulfillmentResult(true, "FULFILLED", true, 3m, 3m));
        configure?.Invoke(fulfillment);

        var progress = new DeliveryProgressService(
            db, fulfillment.Object, NullLogger<DeliveryProgressService>.Instance);

        return new Harness(db, progress, fulfillment, tenant, dbName);
    }

    private static async Task<Guid> NewDelivery(
        Harness h, string status = "GOODS_ISSUED", string? mode = "SHIP", decimal shipped = 3m)
    {
        var delivery = new DeliveryOrder
        {
            UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(),
            DeliveryNumber = $"DLV-2026-{Random.Shared.Next(1, 99_999):D5}",
            Direction = "OUTBOUND", SourceType = "SALE_ORDER",
            SourceUuid = Guid.NewGuid(), SaleOrderUuid = Guid.NewGuid(), DeliveryMode = mode,
            Status = status, CreatedBy = User, CreatedDate = T0,
            Lines =
            [
                new DeliveryOrderLine
                {
                    UUID = Guid.NewGuid(), LineNo = 1, ItemDescription = "LED TV", UnitOfMeasure = "EA",
                    QtyOrdered = 3m, QtyPicked = 3m, QtyPacked = 3m, QtyShipped = shipped,
                    SoLineUuid = Guid.NewGuid(), CreatedBy = User, CreatedDate = T0
                }
            ]
        };

        h.Db.DeliveryOrders.Add(delivery);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return delivery.UUID;
    }

    private static async Task<Guid> NewConsignment(Harness h, string status)
    {
        var consignment = new Consignment
        {
            UUID = Guid.NewGuid(), ConsignmentNumber = $"CN-2026-{Random.Shared.Next(1, 99_999):D5}",
            Status = status, MasterAwb = "AWB-1", CreatedBy = User, CreatedDate = T0
        };

        h.Db.Consignments.Add(consignment);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return consignment.UUID;
    }

    private static async Task Load(Harness h, Guid consignmentUuid, Guid deliveryUuid, int? stopId = null)
    {
        var consignment = await h.Db.Consignments.AsNoTracking().SingleAsync(c => c.UUID == consignmentUuid);
        var delivery    = await h.Db.DeliveryOrders.AsNoTracking().SingleAsync(d => d.UUID == deliveryUuid);

        h.Db.ConsignmentDeliveries.Add(new ConsignmentDelivery
        {
            UUID = Guid.NewGuid(), ConsignmentId = consignment.Id, DeliveryOrderId = delivery.Id,
            Sequence = 1, ConsignmentStopId = stopId, CreatedBy = User, CreatedDate = T0
        });
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
    }

    private static async Task<int> NewStop(Harness h, Guid consignmentUuid, DateTime? arrivedAt = null)
    {
        var consignment = await h.Db.Consignments.AsNoTracking().SingleAsync(c => c.UUID == consignmentUuid);

        var stop = new ConsignmentStop
        {
            UUID = Guid.NewGuid(), ConsignmentId = consignment.Id, Sequence = 1,
            ActualArrival = arrivedAt, CreatedBy = User, CreatedDate = T0
        };

        h.Db.ConsignmentStops.Add(stop);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return stop.Id;
    }

    private static async Task SetConsignment(Harness h, Guid uuid, string status)
    {
        var consignment = await h.Db.Consignments.SingleAsync(c => c.UUID == uuid);
        consignment.Status = status;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
    }

    private static async Task SetDelivery(Harness h, Guid uuid, string status)
    {
        var delivery = await h.Db.DeliveryOrders.SingleAsync(d => d.UUID == uuid);
        delivery.Status = status;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
    }

    private static async Task<DeliveryOrder> Read(Harness h, Guid uuid) =>
        await h.Db.DeliveryOrders.AsNoTracking().Include(d => d.Lines).SingleAsync(d => d.UUID == uuid);

    /// <summary>A shipped delivery on a consignment in the given state.</summary>
    private static async Task<(Guid Delivery, Guid Consignment)> Shipped(
        Harness h, string consignmentStatus, string deliveryStatus = "GOODS_ISSUED")
    {
        var delivery    = await NewDelivery(h, deliveryStatus);
        var consignment = await NewConsignment(h, consignmentStatus);
        await Load(h, consignment, delivery);

        return (delivery, consignment);
    }

    // ── The carrier has the goods ─────────────────────────────────────────────

    [Theory]
    [InlineData("PICKED_UP")]
    [InlineData("IN_TRANSIT")]
    [InlineData("OUT_FOR_DELIVERY")]
    [InlineData("DELIVERY_ATTEMPTED")]
    public async Task A_delivery_is_in_transit_once_the_carrier_has_the_goods(string consignmentStatus)
    {
        var h = NewHarness();
        var (delivery, consignment) = await Shipped(h, consignmentStatus);

        var moved = await h.Progress.SyncAsync(consignment, User);

        moved.Should().Be(1);
        (await Read(h, delivery)).Status.Should().Be("IN_TRANSIT");
    }

    [Fact]
    public async Task Going_in_transit_does_not_tell_the_sale_order_or_count_anything_as_delivered()
    {
        var h = NewHarness();
        var (delivery, consignment) = await Shipped(h, "IN_TRANSIT");

        await h.Progress.SyncAsync(consignment, User);

        (await Read(h, delivery)).Lines.Single().QtyDelivered.Should().Be(0m);
        h.Fulfillment.Verify(f => f.RecordDeliveryCompletedAsync(It.IsAny<DeliveryCompletion>()), Times.Never);
    }

    [Theory]
    [InlineData("BOOKED")]
    [InlineData("EXCEPTION")]
    public async Task A_consignment_the_carrier_has_not_collected_moves_nothing(string consignmentStatus)
    {
        // EXCEPTION too: a pickup that failed is an exception, and then the goods are still on our dock.
        var h = NewHarness();
        var (delivery, consignment) = await Shipped(h, consignmentStatus);

        (await h.Progress.SyncAsync(consignment, User)).Should().Be(0);
        (await Read(h, delivery)).Status.Should().Be("GOODS_ISSUED");
    }

    // ── The handover ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_delivered_consignment_delivers_the_delivery_and_counts_what_was_shipped()
    {
        var h = NewHarness();
        var (delivery, consignment) = await Shipped(h, "DELIVERED", "IN_TRANSIT");

        var moved = await h.Progress.SyncAsync(consignment, User);

        moved.Should().Be(1);

        var read = await Read(h, delivery);
        read.Status.Should().Be("DELIVERED");
        read.Lines.Single().QtyDelivered.Should().Be(3m);
        read.ModifiedBy.Should().Be(User);
    }

    [Fact]
    public async Task A_delivery_still_at_goods_issued_goes_straight_to_delivered()
    {
        // A manual carrier is never scanned in transit — the proof of delivery is the only thing that
        // ever moves it, and the delivery machine allows GOODS_ISSUED to DELIVERED for exactly this.
        var h = NewHarness();
        var (delivery, consignment) = await Shipped(h, "DELIVERED");

        await h.Progress.SyncAsync(consignment, User);

        (await Read(h, delivery)).Status.Should().Be("DELIVERED");
    }

    [Fact]
    public async Task Only_what_was_shipped_counts_as_delivered()
    {
        var h = NewHarness();
        var delivery    = await NewDelivery(h, shipped: 2m);
        var consignment = await NewConsignment(h, "DELIVERED");
        await Load(h, consignment, delivery);

        await h.Progress.SyncAsync(consignment, User);

        (await Read(h, delivery)).Lines.Single().QtyDelivered.Should().Be(2m);
    }

    [Fact]
    public async Task The_sale_order_is_told_which_lines_reached_the_customer()
    {
        var h = NewHarness();
        var (delivery, consignment) = await Shipped(h, "DELIVERED");
        var before = await Read(h, delivery);

        await h.Progress.SyncAsync(consignment, User);

        h.Fulfillment.Verify(f => f.RecordDeliveryCompletedAsync(It.Is<DeliveryCompletion>(c =>
            c.SaleOrderUuid == before.SaleOrderUuid
         && c.DeliveryUuid == delivery
         && c.UserId == User
         && c.Lines.Count == 1
         && c.Lines[0].SoLineUuid == before.Lines.Single().SoLineUuid
         && c.Lines[0].QtyDelivered == 3m)), Times.Once);
    }

    [Fact]
    public async Task A_failure_telling_the_sale_order_does_not_undo_the_handover()
    {
        var h = NewHarness(f => f
            .Setup(x => x.RecordDeliveryCompletedAsync(It.IsAny<DeliveryCompletion>()))
            .ThrowsAsync(new InvalidOperationException("the demand database is down")));
        var (delivery, consignment) = await Shipped(h, "DELIVERED");

        var moved = await h.Progress.SyncAsync(consignment, User);

        moved.Should().Be(1);
        (await Read(h, delivery)).Status.Should().Be("DELIVERED");
    }

    [Fact]
    public async Task Syncing_twice_moves_nothing_the_second_time_and_tells_the_order_once()
    {
        var h = NewHarness();
        var (_, consignment) = await Shipped(h, "DELIVERED");

        (await h.Progress.SyncAsync(consignment, User)).Should().Be(1);
        (await h.Progress.SyncAsync(consignment, User)).Should().Be(0);

        h.Fulfillment.Verify(f => f.RecordDeliveryCompletedAsync(It.IsAny<DeliveryCompletion>()), Times.Once);
    }

    [Fact]
    public async Task Every_delivery_on_the_consignment_moves_together()
    {
        var h = NewHarness();
        var first       = await NewDelivery(h);
        var second      = await NewDelivery(h);
        var consignment = await NewConsignment(h, "DELIVERED");
        await Load(h, consignment, first);
        await Load(h, consignment, second);

        (await h.Progress.SyncAsync(consignment, User)).Should().Be(2);

        (await Read(h, first)).Status.Should().Be("DELIVERED");
        (await Read(h, second)).Status.Should().Be("DELIVERED");
    }

    // ── What a carrier does not get to override ───────────────────────────────

    [Theory]
    [InlineData("PACKED")]
    [InlineData("STAGED")]
    [InlineData("ON_HOLD")]
    [InlineData("CANCELLED")]
    public async Task A_delivery_that_has_not_been_issued_is_left_alone(string deliveryStatus)
    {
        var h = NewHarness();
        var (delivery, consignment) = await Shipped(h, "DELIVERED", deliveryStatus);

        (await h.Progress.SyncAsync(consignment, User)).Should().Be(0);
        (await Read(h, delivery)).Status.Should().Be(deliveryStatus);
    }

    [Fact]
    public async Task A_self_pickup_delivery_is_never_moved_by_a_courier()
    {
        // A collection is proved at the counter with a name and an ID; a scan is not that.
        var h = NewHarness();
        var delivery    = await NewDelivery(h, mode: "SELF_PICKUP");
        var consignment = await NewConsignment(h, "DELIVERED");
        await Load(h, consignment, delivery);

        (await h.Progress.SyncAsync(consignment, User)).Should().Be(0);
        (await Read(h, delivery)).Status.Should().Be("GOODS_ISSUED");
    }

    [Fact]
    public async Task A_delivery_at_a_stop_nobody_has_reached_is_still_on_the_vehicle()
    {
        // Recording a handover at one stop marks the whole consignment delivered.
        var h = NewHarness();
        var delivery    = await NewDelivery(h);
        var consignment = await NewConsignment(h, "DELIVERED");
        var stop        = await NewStop(h, consignment, arrivedAt: null);
        await Load(h, consignment, delivery, stop);

        await h.Progress.SyncAsync(consignment, User);

        (await Read(h, delivery)).Status.Should().Be("IN_TRANSIT");
    }

    [Fact]
    public async Task A_delivery_at_a_stop_that_has_been_reached_is_delivered()
    {
        var h = NewHarness();
        var delivery    = await NewDelivery(h);
        var consignment = await NewConsignment(h, "DELIVERED");
        var stop        = await NewStop(h, consignment, arrivedAt: T0);
        await Load(h, consignment, delivery, stop);

        await h.Progress.SyncAsync(consignment, User);

        (await Read(h, delivery)).Status.Should().Be("DELIVERED");
    }

    // ── The sweep ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_sweep_catches_every_delivery_that_is_behind_its_consignment()
    {
        var h = NewHarness();
        var (pickedUp, _)  = await Shipped(h, "PICKED_UP");
        var (delivered, _) = await Shipped(h, "DELIVERED");

        (await h.Progress.SweepAsync(0)).Should().Be(2);

        (await Read(h, pickedUp)).Status.Should().Be("IN_TRANSIT");
        (await Read(h, delivered)).Status.Should().Be("DELIVERED");
    }

    [Fact]
    public async Task The_sweep_leaves_alone_a_delivery_that_is_already_caught_up()
    {
        var h = NewHarness();
        var (transit, _) = await Shipped(h, "IN_TRANSIT", "IN_TRANSIT");
        var (done, _)    = await Shipped(h, "DELIVERED", "DELIVERED");

        (await h.Progress.SweepAsync(0)).Should().Be(0);

        (await Read(h, transit)).Status.Should().Be("IN_TRANSIT");
        (await Read(h, done)).Status.Should().Be("DELIVERED");
        h.Fulfillment.Verify(f => f.RecordDeliveryCompletedAsync(It.IsAny<DeliveryCompletion>()), Times.Never);
    }

    [Fact]
    public async Task A_delivery_issued_after_its_consignment_had_already_been_delivered_catches_up()
    {
        // A manual carrier's proof can be typed in before the warehouse presses Issue goods. The
        // delivery could not follow then; nothing has told it since, so the sweep has to.
        var h = NewHarness();
        var (delivery, consignment) = await Shipped(h, "DELIVERED", "STAGED");

        (await h.Progress.SyncAsync(consignment, User)).Should().Be(0);

        await SetDelivery(h, delivery, "GOODS_ISSUED");

        (await h.Progress.SweepAsync(0)).Should().Be(1);
        (await Read(h, delivery)).Status.Should().Be("DELIVERED");
    }

    [Fact]
    public async Task The_sweep_is_attributed_to_the_system_not_to_a_person()
    {
        var h = NewHarness();
        var (delivery, _) = await Shipped(h, "DELIVERED");

        await h.Progress.SweepAsync(0);

        (await Read(h, delivery)).ModifiedBy.Should().Be(0);
    }

    // ── Proof of delivery is the moment the person is waiting ─────────────────

    [Fact]
    public async Task Recording_a_proof_of_delivery_delivers_the_delivery_straight_away()
    {
        var h = NewHarness();
        var delivery    = await NewDelivery(h);
        var consignment = await NewConsignment(h, "OUT_FOR_DELIVERY");
        await Load(h, consignment, delivery);

        var proofs = new DeliveryProofService(h.Db, h.Progress);

        await proofs.RecordAsync(consignment, new RecordProofRequest
        {
            ReceivedBy = "R. Ahmed", DeliveredAt = T0
        }, User);

        var read = await Read(h, delivery);
        read.Status.Should().Be("DELIVERED");
        read.Lines.Single().QtyDelivered.Should().Be(3m);

        h.Fulfillment.Verify(f => f.RecordDeliveryCompletedAsync(It.IsAny<DeliveryCompletion>()), Times.Once);
    }

    [Fact]
    public async Task A_handover_typed_in_for_a_booked_manual_consignment_delivers_the_delivery()
    {
        // The manual carrier's whole journey: booked by hand, silent, then a person records the handover.
        // The consignment is BOOKED at that point — never OUT_FOR_DELIVERY — and the delivery must still follow.
        var h = NewHarness();
        var delivery    = await NewDelivery(h);
        var consignment = await NewConsignment(h, "BOOKED");
        await Load(h, consignment, delivery);

        var proofs = new DeliveryProofService(h.Db, h.Progress);

        await proofs.RecordAsync(consignment, new RecordProofRequest
        {
            ReceivedBy = "Gate guard", DeliveredAt = T0
        }, User);

        var read = await Read(h, delivery);
        read.Status.Should().Be("DELIVERED");
        read.Lines.Single().QtyDelivered.Should().Be(3m);

        h.Fulfillment.Verify(f => f.RecordDeliveryCompletedAsync(It.IsAny<DeliveryCompletion>()), Times.Once);
    }

    // ── The delivery page knows its consignments ──────────────────────────────

    [Fact]
    public async Task The_delivery_detail_names_the_consignments_carrying_it()
    {
        var h = NewHarness();
        var delivery    = await NewDelivery(h);
        var consignment = await NewConsignment(h, "IN_TRANSIT");
        await Load(h, consignment, delivery);

        var repo = new DeliveryRepository(
            h.Db, new DocumentNumberGenerator(h.Db, h.Tenant), new AddressNormalizer(new FakeCityLookup()));

        var detail = await repo.GetByUuidAsync(delivery);

        var carried = detail!.Consignments.Should().ContainSingle().Subject;
        carried.ConsignmentUuid.Should().Be(consignment);
        carried.Status.Should().Be("IN_TRANSIT");
        carried.MasterAwb.Should().Be("AWB-1");
    }

    [Fact]
    public async Task A_delivery_on_no_consignment_says_so()
    {
        var h = NewHarness();
        var delivery = await NewDelivery(h);

        var repo = new DeliveryRepository(
            h.Db, new DocumentNumberGenerator(h.Db, h.Tenant), new AddressNormalizer(new FakeCityLookup()));

        (await repo.GetByUuidAsync(delivery))!.Consignments.Should().BeEmpty();
    }
}

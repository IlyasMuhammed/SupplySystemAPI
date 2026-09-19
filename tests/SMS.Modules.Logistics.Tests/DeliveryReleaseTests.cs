using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Logistics.Tests;

// T-22 — release and hard reservation.
public class DeliveryReleaseTests
{
    private const int User = 42;

    private sealed record Harness(
        LogisticsDbContext Db,
        DeliveryRepository Deliveries,
        DeliveryReleaseRepository Release,
        DeliveryStatusRepository Status,
        FakeStockReservationService Reservations);

    private static Harness NewHarness()
    {
        var (db, tenant, _) = LogisticsTestDb.New();
        var reservations = new FakeStockReservationService();

        return new Harness(
            db,
            new DeliveryRepository(db, new DocumentNumberGenerator(db, tenant),
                                   new AddressNormalizer(new FakeCityLookup())),
            new DeliveryReleaseRepository(db, reservations, new DocumentNumberGenerator(db, tenant)),
            new DeliveryStatusRepository(db, reservations),
            reservations);
    }

    private static async Task<(Guid Uuid, Guid VariantUuid)> NewOutboundDelivery(
        Harness h, decimal qty = 100m, decimal availableStock = 500m)
    {
        var variantUuid = Guid.NewGuid();
        h.Reservations.SetAvailable(variantUuid, availableStock);

        var uuid = await h.Deliveries.CreateAsync(new CreateDeliveryRequest
        {
            SourceType = "MANUAL",
            Direction  = "OUTBOUND",
            ShipFromWarehouseUuid = Guid.NewGuid(),
            Lines = [new CreateDeliveryLineRequest
            {
                ItemDescription = "4mm cable", UnitOfMeasure = "M",
                QtyOrdered = qty, VariantUuid = variantUuid
            }]
        }, User);

        return (uuid, variantUuid);
    }

    private static async Task<string> StatusOf(Harness h, Guid uuid) =>
        (await h.Db.DeliveryOrders.AsNoTracking().SingleAsync(d => d.UUID == uuid)).Status;

    // ── Releasing holds the stock ──────────────────────────────────────────────

    [Fact]
    public async Task Releasing_a_delivery_hard_reserves_every_line()
    {
        var h = NewHarness();
        var (uuid, variantUuid) = await NewOutboundDelivery(h, qty: 100m, availableStock: 500m);

        (await h.Release.ReleaseAsync(uuid, null, User)).Should().BeTrue();

        (await StatusOf(h, uuid)).Should().Be("RELEASED");
        h.Reservations.ActiveFor(uuid).Should().Be(100m);
        h.Reservations.RemainingAvailable(variantUuid).Should().Be(400m);
    }

    [Fact]
    public async Task Two_deliveries_cannot_promise_the_same_units()
    {
        // The whole point of a hard reservation. Without it both releases succeed and the
        // warehouse discovers the problem at picking, with goods already committed twice.
        var h = NewHarness();
        var variantUuid = Guid.NewGuid();
        h.Reservations.SetAvailable(variantUuid, 100m);

        async Task<Guid> Delivery(decimal qty) => await h.Deliveries.CreateAsync(new CreateDeliveryRequest
        {
            SourceType = "MANUAL", Direction = "OUTBOUND",
            Lines = [new CreateDeliveryLineRequest
            {
                ItemDescription = "4mm cable", QtyOrdered = qty, VariantUuid = variantUuid
            }]
        }, User);

        var first  = await Delivery(80m);
        var second = await Delivery(80m);

        await h.Release.ReleaseAsync(first, null, User);

        var act = async () => await h.Release.ReleaseAsync(second, null, User);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*not enough stock*")
            .WithMessage("*20*");

        (await StatusOf(h, second)).Should().Be("DRAFT", "a refused release leaves the delivery alone");
        h.Reservations.ActiveFor(second).Should().Be(0m, "nothing is held when the release is refused");
    }

    [Fact]
    public async Task A_shortfall_names_the_line_and_the_real_figure()
    {
        var h = NewHarness();
        var (uuid, _) = await NewOutboundDelivery(h, qty: 100m, availableStock: 30m);

        var act = async () => await h.Release.ReleaseAsync(uuid, null, User);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*Line 1*")
            .WithMessage("*4mm cable*")
            .WithMessage("*100*")
            .WithMessage("*30*");
    }

    [Fact]
    public async Task Nothing_is_held_when_any_line_falls_short()
    {
        // All-or-nothing. A delivery holding stock for two of its three lines is a delivery that
        // cannot be picked, quietly.
        var h = NewHarness();
        var plenty = Guid.NewGuid();
        var scarce = Guid.NewGuid();
        h.Reservations.SetAvailable(plenty, 500m);
        h.Reservations.SetAvailable(scarce, 5m);

        var uuid = await h.Deliveries.CreateAsync(new CreateDeliveryRequest
        {
            SourceType = "MANUAL", Direction = "OUTBOUND",
            Lines =
            [
                new CreateDeliveryLineRequest { ItemDescription = "Plenty", QtyOrdered = 10m, VariantUuid = plenty },
                new CreateDeliveryLineRequest { ItemDescription = "Scarce", QtyOrdered = 50m, VariantUuid = scarce }
            ]
        }, User);

        var act = async () => await h.Release.ReleaseAsync(uuid, null, User);
        await act.Should().ThrowAsync<ConflictException>();

        h.Reservations.RemainingAvailable(plenty).Should().Be(500m, "the line that could be held was not");
        h.Reservations.ActiveFor(uuid).Should().Be(0m);
    }

    // ── Inbound deliveries hold nothing ───────────────────────────────────────

    [Fact]
    public async Task An_inbound_delivery_reserves_nothing()
    {
        // Only stock that is leaving can be over-promised. An ASN says goods are arriving —
        // there is nothing on hand to hold, and holding would reduce availability for no reason.
        var h = NewHarness();

        var uuid = await h.Deliveries.CreateAsync(new CreateDeliveryRequest
        {
            SourceType = "PO", SourceUuid = Guid.NewGuid(), SourceNumber = "PO-2026-00001",
            Lines = [new CreateDeliveryLineRequest
            {
                ItemDescription = "4mm cable", QtyOrdered = 100m, VariantUuid = Guid.NewGuid()
            }]
        }, User);

        (await h.Release.ReleaseAsync(uuid, null, User)).Should().BeTrue();

        (await StatusOf(h, uuid)).Should().Be("RELEASED");
        h.Reservations.ReserveCallCount.Should().Be(0, "an inbound delivery has nothing to reserve");
    }

    [Fact]
    public async Task A_transfer_reserves_stock_at_the_sending_warehouse()
    {
        var h = NewHarness();
        var variantUuid = Guid.NewGuid();
        h.Reservations.SetAvailable(variantUuid, 200m);

        var uuid = await h.Deliveries.CreateAsync(new CreateDeliveryRequest
        {
            SourceType = "TRANSFER",
            ShipFromWarehouseUuid = Guid.NewGuid(),
            ShipToWarehouseUuid   = Guid.NewGuid(),
            Lines = [new CreateDeliveryLineRequest
            {
                ItemDescription = "4mm cable", QtyOrdered = 60m, VariantUuid = variantUuid
            }]
        }, User);

        await h.Release.ReleaseAsync(uuid, null, User);

        h.Reservations.ActiveFor(uuid).Should().Be(60m);
    }

    // ── What cannot be released ───────────────────────────────────────────────

    [Theory]
    [InlineData("RELEASED")]
    [InlineData("PICKING")]
    [InlineData("GOODS_ISSUED")]
    [InlineData("CANCELLED")]
    public async Task A_delivery_that_is_not_a_draft_cannot_be_released(string status)
    {
        var h = NewHarness();
        var (uuid, _) = await NewOutboundDelivery(h);

        var delivery = await h.Db.DeliveryOrders.SingleAsync(d => d.UUID == uuid);
        delivery.Status = status;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var act = async () => await h.Release.ReleaseAsync(uuid, null, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage($"*{status}*");
        h.Reservations.ReserveCallCount.Should().Be(0);
    }

    [Fact]
    public async Task A_migrated_delivery_with_no_line_detail_cannot_be_released()
    {
        // Backfilled rows have a real header and no lines. Releasing one would reserve nothing
        // and then present an unpickable delivery to the warehouse.
        var h = NewHarness();
        var (uuid, _) = await NewOutboundDelivery(h);

        var delivery = await h.Db.DeliveryOrders.SingleAsync(d => d.UUID == uuid);
        delivery.LinesUnknown = true;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var act = async () => await h.Release.ReleaseAsync(uuid, null, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*migrated*");
    }

    [Fact]
    public async Task A_line_with_no_resolved_stock_item_blocks_the_release()
    {
        // SRO lines are product-scoped, and a product with no default variant leaves VariantUuid
        // null (T-12). Stock is held per variant, so releasing would hold nothing for that line.
        var h = NewHarness();

        var uuid = await h.Deliveries.CreateAsync(new CreateDeliveryRequest
        {
            SourceType = "MANUAL", Direction = "OUTBOUND",
            Lines = [new CreateDeliveryLineRequest
            {
                ItemDescription = "Unresolved item", QtyOrdered = 10m, ProductUuid = Guid.NewGuid()
            }]
        }, User);

        var act = async () => await h.Release.ReleaseAsync(uuid, null, User);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*no stock item resolved*")
            .WithMessage("*Line 1*");
    }

    [Fact]
    public async Task An_unknown_delivery_is_reported_as_not_found()
    {
        var h = NewHarness();

        (await h.Release.ReleaseAsync(Guid.NewGuid(), null, User)).Should().BeFalse();
    }

    // ── Giving the stock back ─────────────────────────────────────────────────

    [Fact]
    public async Task Cancelling_a_released_delivery_returns_its_stock()
    {
        // A cancelled delivery that keeps its hold makes those units permanently unavailable,
        // with nothing pointing at the cause.
        var h = NewHarness();
        var (uuid, variantUuid) = await NewOutboundDelivery(h, qty: 100m, availableStock: 500m);

        await h.Release.ReleaseAsync(uuid, null, User);
        h.Reservations.RemainingAvailable(variantUuid).Should().Be(400m);

        await h.Status.CancelAsync(uuid, new DeliveryReasonRequest { Reason = "Order withdrawn" }, User);

        h.Reservations.ActiveFor(uuid).Should().Be(0m);
        h.Reservations.RemainingAvailable(variantUuid).Should().Be(500m, "the units are free again");
        h.Reservations.ReasonsGiven.Should().ContainSingle()
            .Which.Should().Contain("Order withdrawn");
    }

    [Fact]
    public async Task Cancelling_a_draft_that_never_reserved_anything_is_harmless()
    {
        // Release is idempotent on the empty case, so a cancel before release must not fail.
        var h = NewHarness();
        var (uuid, _) = await NewOutboundDelivery(h);

        var act = async () => await h.Status.CancelAsync(
            uuid, new DeliveryReasonRequest { Reason = "Never needed" }, User);

        await act.Should().NotThrowAsync();
        h.Reservations.ActiveFor(uuid).Should().Be(0m);
    }

    [Fact]
    public async Task Short_closing_returns_the_stock_that_is_no_longer_coming()
    {
        var h = NewHarness();
        var (uuid, variantUuid) = await NewOutboundDelivery(h, qty: 100m, availableStock: 500m);

        await h.Release.ReleaseAsync(uuid, null, User);

        var delivery = await h.Db.DeliveryOrders.Include(d => d.Lines).SingleAsync(d => d.UUID == uuid);
        delivery.Status = "PARTIALLY_DELIVERED";
        delivery.Lines.Single().QtyDelivered = 60m;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        await h.Status.ShortCloseAsync(
            uuid, new DeliveryReasonRequest { Reason = "Supplier could not fulfil" }, User);

        h.Reservations.ActiveFor(uuid).Should().Be(0m);
        h.Reservations.RemainingAvailable(variantUuid).Should().Be(500m);
    }

    [Fact]
    public async Task Releasing_the_same_delivery_twice_frees_nothing_the_second_time()
    {
        var h = NewHarness();
        var (uuid, variantUuid) = await NewOutboundDelivery(h);

        await h.Release.ReleaseAsync(uuid, null, User);
        await h.Status.CancelAsync(uuid, new DeliveryReasonRequest { Reason = "one" }, User);

        // A retried job or a double click must not push the counter below what is actually held.
        var freed = await h.Reservations.ReleaseBySourceAsync(
            ReservationSourceType.Delivery, uuid, "two", User);

        freed.Should().Be(0);
        h.Reservations.RemainingAvailable(variantUuid).Should().Be(500m);
    }
}

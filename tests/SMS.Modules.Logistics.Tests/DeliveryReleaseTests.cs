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

    // ── Sale-order deliveries (A29-P6-03) ─────────────────────────────────────
    //
    // The order already holds its stock under SALES_ORDER. Releasing the delivery that ships it
    // must take that hold over — reserving again would either fail (nothing free) or hold the
    // same units twice — and only what the order does not hold comes from free stock.

    private static readonly Guid Central = Guid.NewGuid();
    private static readonly Guid North   = Guid.NewGuid();

    private sealed record OrderDelivery(Guid DeliveryUuid, Guid OrderUuid, Guid OrderLineUuid, Guid VariantUuid);

    /// <summary>
    /// An order holding <paramref name="held"/> units in <paramref name="holdIn"/>, with
    /// <paramref name="free"/> more on the shelf, and a draft delivery for
    /// <paramref name="deliveryQty"/> of it shipping from <paramref name="shipFrom"/>.
    /// </summary>
    private static async Task<OrderDelivery> NewSaleOrderDelivery(
        Harness h, decimal held = 100m, decimal free = 0m, decimal deliveryQty = 100m,
        Guid? holdIn = null, Guid? shipFrom = null, bool shipFromUnspecified = false)
    {
        var variantUuid   = Guid.NewGuid();
        var orderUuid     = Guid.NewGuid();
        var orderLineUuid = Guid.NewGuid();
        var warehouse     = holdIn ?? Central;

        h.Reservations.SetLayout(variantUuid,
            (new FakeStockReservationService.StockLocation(warehouse, "Central"), held + free));
        (await h.Reservations.ReserveAsync(ReservationSourceType.SalesOrder, orderUuid,
            [new ReservationRequest(variantUuid, warehouse, held, orderLineUuid)], User)).Succeeded.Should().BeTrue();

        var uuid = await h.Deliveries.CreateAsync(new CreateDeliveryRequest
        {
            SourceType = "SALE_ORDER", SourceUuid = orderUuid, SourceNumber = "SO-2026-00042",
            ShipFromWarehouseUuid = shipFromUnspecified ? null : shipFrom ?? Central,
            Lines = [new CreateDeliveryLineRequest { ItemDescription = "4mm cable", QtyOrdered = deliveryQty, VariantUuid = variantUuid }]
        }, User);

        var delivery = await h.Db.DeliveryOrders.Include(d => d.Lines).SingleAsync(d => d.UUID == uuid);
        delivery.SaleOrderUuid = orderUuid;
        delivery.DeliveryMode  = "SHIP";
        delivery.Lines.Single().SoLineUuid = orderLineUuid;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return new OrderDelivery(uuid, orderUuid, orderLineUuid, variantUuid);
    }

    [Fact]
    public async Task Releasing_a_sale_order_delivery_takes_over_the_orders_hold_instead_of_reserving_again()
    {
        var h = NewHarness();
        var d = await NewSaleOrderDelivery(h, held: 100m, free: 0m, deliveryQty: 100m);

        (await h.Release.ReleaseAsync(d.DeliveryUuid, null, User)).Should().BeTrue();

        (await StatusOf(h, d.DeliveryUuid)).Should().Be("RELEASED");
        h.Reservations.ActiveFor(d.DeliveryUuid).Should().Be(100m);
        h.Reservations.ActiveFor(d.OrderUuid).Should().Be(0m, "the hold changed hands");
        h.Reservations.ReserveCallCount.Should().Be(1, "only the order's own reservation — nothing was reserved twice");
        h.Reservations.RemainingAvailable(d.VariantUuid).Should().Be(0m);
    }

    [Fact]
    public async Task A_partial_delivery_takes_only_its_share_of_the_hold()
    {
        var h = NewHarness();
        var d = await NewSaleOrderDelivery(h, held: 100m, deliveryQty: 40m);

        await h.Release.ReleaseAsync(d.DeliveryUuid, null, User);

        h.Reservations.ActiveFor(d.DeliveryUuid).Should().Be(40m);
        h.Reservations.ActiveFor(d.OrderUuid).Should().Be(60m, "still held for the next delivery");
    }

    [Fact]
    public async Task What_the_order_does_not_hold_comes_from_free_stock()
    {
        // A back-to-back balance: the order held 60 at confirmation and the other 40 has since
        // arrived on a GRN that nobody reserved.
        var h = NewHarness();
        var d = await NewSaleOrderDelivery(h, held: 60m, free: 40m, deliveryQty: 100m);

        await h.Release.ReleaseAsync(d.DeliveryUuid, null, User);

        h.Reservations.ActiveFor(d.DeliveryUuid).Should().Be(100m);
        h.Reservations.ActiveFor(d.OrderUuid).Should().Be(0m);
        h.Reservations.RemainingAvailable(d.VariantUuid).Should().Be(0m);
        h.Reservations.ReserveCallCount.Should().Be(2, "the order's, plus one for the 40 from free stock");
    }

    [Fact]
    public async Task When_free_stock_cannot_cover_the_balance_nothing_changes_hands()
    {
        // All-or-nothing still holds: a refused release leaves the order's hold exactly as it was.
        var h = NewHarness();
        var d = await NewSaleOrderDelivery(h, held: 60m, free: 30m, deliveryQty: 100m);

        var act = async () => await h.Release.ReleaseAsync(d.DeliveryUuid, null, User);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*not enough stock*")
            .WithMessage("*needed 40*")
            .WithMessage("*30 available*");

        (await StatusOf(h, d.DeliveryUuid)).Should().Be("DRAFT");
        h.Reservations.ActiveFor(d.DeliveryUuid).Should().Be(0m);
        h.Reservations.ActiveFor(d.OrderUuid).Should().Be(60m, "untouched");
    }

    [Fact]
    public async Task Availability_counts_the_orders_own_hold()
    {
        // Without this the preview would say "0 available" for stock that is held precisely for
        // this delivery, and a SPLIT release would backorder the whole thing.
        var h = NewHarness();
        var d = await NewSaleOrderDelivery(h, held: 100m, free: 0m, deliveryQty: 100m);

        var result = await h.Release.GetAvailabilityAsync(d.DeliveryUuid);

        result!.RequiresStock.Should().BeTrue();
        result.Lines.Single().QtyAvailable.Should().Be(100m);
        result.Lines.Single().Shortfall.Should().Be(0m);
        result.Lines.Single().WarehouseName.Should().NotBeNullOrWhiteSpace();
        result.CanReleaseInFull.Should().BeTrue();
    }

    [Fact]
    public async Task Splitting_a_sale_order_delivery_ships_what_the_order_holds_and_backorders_the_rest()
    {
        var h = NewHarness();
        var d = await NewSaleOrderDelivery(h, held: 60m, free: 0m, deliveryQty: 100m);

        await h.Release.ReleaseAsync(d.DeliveryUuid, new ReleaseDeliveryRequest { OnShortage = ShortageAction.Split }, User);

        var released = await h.Db.DeliveryOrders.Include(x => x.Lines).AsNoTracking().SingleAsync(x => x.UUID == d.DeliveryUuid);
        released.Status.Should().Be("RELEASED");
        released.Lines.Single().QtyOrdered.Should().Be(60m);
        h.Reservations.ActiveFor(d.DeliveryUuid).Should().Be(60m);
        h.Reservations.ActiveFor(d.OrderUuid).Should().Be(0m);

        var backorder = await h.Db.DeliveryOrders.Include(x => x.Lines).AsNoTracking().SingleAsync(x => x.UUID != d.DeliveryUuid);
        backorder.Status.Should().Be("DRAFT");
        backorder.SaleOrderUuid.Should().Be(d.OrderUuid);
        backorder.Lines.Single().QtyOrdered.Should().Be(40m);
        backorder.Lines.Single().SoLineUuid.Should().Be(d.OrderLineUuid);
    }

    [Fact]
    public async Task Cancelling_a_released_sale_order_delivery_returns_the_hold_to_the_order()
    {
        // The customer's order still stands; its stock goes back to the order, not to whoever
        // asks next.
        var h = NewHarness();
        var d = await NewSaleOrderDelivery(h, held: 100m, deliveryQty: 100m);
        await h.Release.ReleaseAsync(d.DeliveryUuid, null, User);
        h.Reservations.ActiveFor(d.OrderUuid).Should().Be(0m);

        await h.Status.CancelAsync(d.DeliveryUuid, new DeliveryReasonRequest { Reason = "Truck broke down" }, User);

        h.Reservations.ActiveFor(d.DeliveryUuid).Should().Be(0m);
        h.Reservations.ActiveFor(d.OrderUuid).Should().Be(100m, "back with the order");
        h.Reservations.RemainingAvailable(d.VariantUuid).Should().Be(0m, "never became free stock");
    }

    [Fact]
    public async Task Short_closing_before_issue_returns_the_unshipped_balance_to_the_order()
    {
        var h = NewHarness();
        var d = await NewSaleOrderDelivery(h, held: 100m, deliveryQty: 100m);
        await h.Release.ReleaseAsync(d.DeliveryUuid, null, User);

        var delivery = await h.Db.DeliveryOrders.SingleAsync(x => x.UUID == d.DeliveryUuid);
        delivery.Status = "PICKED";
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        await h.Status.ShortCloseAsync(d.DeliveryUuid, new DeliveryReasonRequest { Reason = "Customer took less" }, User);

        h.Reservations.ActiveFor(d.DeliveryUuid).Should().Be(0m);
        h.Reservations.ActiveFor(d.OrderUuid).Should().Be(100m);
    }

    [Fact]
    public async Task A_hold_in_another_warehouse_is_not_taken_over()
    {
        // The delivery ships from Central; the order's stock sits in North. A pick list cannot
        // span buildings, so that hold is neither counted nor moved — and with nothing free in
        // Central the release is refused, leaving the order's hold where it was.
        var h = NewHarness();
        var d = await NewSaleOrderDelivery(h, held: 100m, free: 0m, deliveryQty: 100m, holdIn: North, shipFrom: Central);

        (await h.Release.GetAvailabilityAsync(d.DeliveryUuid))!.Lines.Single().QtyAvailable.Should().Be(0m);

        var act = async () => await h.Release.ReleaseAsync(d.DeliveryUuid, null, User);
        await act.Should().ThrowAsync<ConflictException>();

        h.Reservations.ActiveFor(d.OrderUuid).Should().Be(100m);
    }

    [Fact]
    public async Task A_delivery_that_names_no_warehouse_takes_the_hold_wherever_it_is()
    {
        var h = NewHarness();
        var d = await NewSaleOrderDelivery(h, held: 100m, deliveryQty: 100m, holdIn: North, shipFromUnspecified: true);

        await h.Release.ReleaseAsync(d.DeliveryUuid, null, User);

        h.Reservations.ActiveFor(d.DeliveryUuid).Should().Be(100m);
        h.Reservations.ActiveFor(d.OrderUuid).Should().Be(0m);
    }
}

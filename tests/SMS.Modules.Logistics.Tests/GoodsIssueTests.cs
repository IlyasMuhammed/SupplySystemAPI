using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Services;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Logistics.Tests;

// T-27 — goods issue. The double-deduction tests are the point of this file.
public class GoodsIssueTests
{
    private const int User   = 42;
    private const int Picker = 99;

    private static readonly Guid CentralUuid = Guid.NewGuid();
    private static readonly Guid NorthUuid   = Guid.NewGuid();

    private sealed record Harness(
        LogisticsDbContext Db,
        DemandDbContext Demand,
        DeliveryRepository Deliveries,
        DeliveryReleaseRepository Release,
        DeliveryStatusRepository Status,
        PickListRepository PickLists,
        PackageRepository Packages,
        GoodsIssueRepository GoodsIssue,
        FakeStockReservationService Reservations,
        FakeGoodsIssuePoster Poster,
        Mock<ISaleOrderFulfillmentService> Fulfillment);

    private static Mock<ISaleOrderFulfillmentService> MockFulfillment()
    {
        var mock = new Mock<ISaleOrderFulfillmentService>();
        mock.Setup(f => f.RecordDeliveryCompletedAsync(It.IsAny<DeliveryCompletion>()))
            .ReturnsAsync((DeliveryCompletion c) => new FulfillmentResult(true, "FULFILLED", true, 100m, c.Lines.Sum(l => l.QtyDelivered)));
        return mock;
    }

    private static Harness NewHarness()
    {
        var (db, tenant, dbName) = LogisticsTestDb.New();
        var demand       = LogisticsTestDb.Demand(dbName, tenant);
        var reservations = new FakeStockReservationService();
        var poster       = new FakeGoodsIssuePoster(reservations);
        var fulfillment  = MockFulfillment();

        return new Harness(
            db,
            demand,
            new DeliveryRepository(db, new DocumentNumberGenerator(db, tenant),
                                   new AddressNormalizer(new FakeCityLookup())),
            new DeliveryReleaseRepository(db, reservations, new DocumentNumberGenerator(db, tenant)),
            new DeliveryStatusRepository(db, reservations),
            new PickListRepository(db, reservations, new DocumentNumberGenerator(db, tenant)),
            new PackageRepository(db, new DocumentNumberGenerator(db, tenant)),
            new GoodsIssueRepository(db, demand, reservations, poster, fulfillment.Object, NullLogger<GoodsIssueRepository>.Instance),
            reservations,
            poster,
            fulfillment);
    }

    /// <summary>
    /// Takes a delivery of one line the whole way to STAGED: released, picked, packed, staged.
    /// </summary>
    private static async Task<Guid> Staged(
        Harness h,
        string sourceType = "MANUAL",
        decimal qty = 100m,
        Guid? shipToWarehouse = null)
    {
        var variantUuid = Guid.NewGuid();
        h.Reservations.SetAvailable(variantUuid, 1000m);

        var uuid = await h.Deliveries.CreateAsync(new CreateDeliveryRequest
        {
            SourceType   = sourceType,
            SourceUuid   = sourceType == "MANUAL" ? null : Guid.NewGuid(),
            SourceNumber = sourceType == "MANUAL" ? null : $"{sourceType}-2026-00001",
            Direction    = sourceType == "MANUAL" ? "OUTBOUND" : null,
            ShipFromWarehouseUuid = CentralUuid,
            ShipToWarehouseUuid   = shipToWarehouse ?? (sourceType == "TRANSFER" ? NorthUuid : null),
            Lines = [new CreateDeliveryLineRequest
            {
                ItemDescription = "4mm cable", UnitOfMeasure = "M",
                QtyOrdered = qty, VariantUuid = variantUuid
            }]
        }, User);

        await h.Release.ReleaseAsync(uuid, null, User);

        var pickList = await h.PickLists.GenerateAsync(uuid, null, User);
        var pickLine = (await h.PickLists.GetByUuidAsync(pickList))!.Lines.Single();

        await h.PickLists.ConfirmAsync(pickList, new ConfirmPickRequest
        {
            Lines = [new ConfirmPickLineRequest { LineUuid = pickLine.UUID, QtyPicked = qty }]
        }, Picker);

        var packLine = (await h.Packages.GetForDeliveryAsync(uuid))!.Lines.Single();

        await h.Packages.PackAsync(uuid, new PackRequest
        {
            GrossWeightKg = 12m,
            Contents = [new PackContentRequest
            {
                DeliveryLineUuid = packLine.DeliveryLineUuid, Qty = qty
            }]
        }, User);

        (await h.GoodsIssue.StageAsync(uuid, User)).Should().BeTrue();
        h.Db.ChangeTracker.Clear();

        return uuid;
    }

    private static async Task<string> StatusOf(Harness h, Guid uuid) =>
        (await h.Db.DeliveryOrders.AsNoTracking().SingleAsync(d => d.UUID == uuid)).Status;

    // ── Staging ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Staging_moves_a_packed_delivery_to_the_dock()
    {
        var h = NewHarness();
        var uuid = await Staged(h);

        (await StatusOf(h, uuid)).Should().Be("STAGED");
    }

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("RELEASED")]
    [InlineData("PICKING")]
    [InlineData("PICKED")]
    public async Task Only_a_packed_delivery_can_be_staged(string status)
    {
        var h = NewHarness();
        var uuid = await Staged(h);

        var delivery = await h.Db.DeliveryOrders.SingleAsync(d => d.UUID == uuid);
        delivery.Status = status;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var act = async () => await h.GoodsIssue.StageAsync(uuid, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage($"*{status}*");
    }

    [Fact]
    public async Task An_unknown_delivery_cannot_be_staged_or_issued()
    {
        var h = NewHarness();

        (await h.GoodsIssue.StageAsync(Guid.NewGuid(), User)).Should().BeFalse();
        (await h.GoodsIssue.IssueAsync(Guid.NewGuid(), User)).Should().BeNull();
    }

    // ── Posting the movement ──────────────────────────────────────────────────

    [Fact]
    public async Task A_manual_delivery_posts_the_stock_movement()
    {
        // Nothing else stands behind a manual delivery, so it is the document that moves the stock.
        var h = NewHarness();
        var uuid = await Staged(h, "MANUAL", qty: 100m);

        var result = await h.GoodsIssue.IssueAsync(uuid, User);

        result!.PostedStock.Should().BeTrue();
        result.QtyShipped.Should().Be(100m);
        result.QtyOut.Should().Be(100m);
        result.QtyIn.Should().Be(0m);
        result.Note.Should().BeNull();

        h.Poster.CallCount.Should().Be(1);
        h.Poster.Last.ReferenceType.Should().Be("DELIVERY");
        h.Poster.Last.ReferenceNumber.Should().Be(result.DeliveryNumber);

        (await StatusOf(h, uuid)).Should().Be("GOODS_ISSUED");
    }

    [Fact]
    public async Task Issuing_records_when_the_stock_left_and_who_sent_it()
    {
        var h = NewHarness();
        var uuid = await Staged(h);

        await h.GoodsIssue.IssueAsync(uuid, User);

        var delivery = await h.Db.DeliveryOrders.AsNoTracking().SingleAsync(d => d.UUID == uuid);
        delivery.GoodsIssuedAt.Should().NotBeNull();
        delivery.GoodsIssuedBy.Should().Be(User);
    }

    [Fact]
    public async Task What_was_in_the_boxes_becomes_what_was_shipped()
    {
        var h = NewHarness();
        var uuid = await Staged(h, qty: 100m);

        await h.GoodsIssue.IssueAsync(uuid, User);

        var line = await h.Db.DeliveryOrderLines.AsNoTracking()
            .SingleAsync(l => l.DeliveryOrder.UUID == uuid);

        line.QtyShipped.Should().Be(100m);
        line.QtyShipped.Should().Be(line.QtyPacked);
    }

    [Fact]
    public async Task Issuing_ends_the_stock_reservation()
    {
        // The units are gone. A hold that survives makes stock permanently unavailable that has
        // already left the building.
        var h = NewHarness();
        var uuid = await Staged(h, qty: 100m);

        h.Reservations.ActiveFor(uuid).Should().Be(100m);

        await h.GoodsIssue.IssueAsync(uuid, User);

        h.Reservations.ActiveFor(uuid).Should().Be(0m);
    }

    // ── Not deducting twice ───────────────────────────────────────────────────

    [Theory]
    [InlineData("MIV")]
    [InlineData("SRO")]
    public async Task A_delivery_whose_source_already_posted_the_movement_does_not_post_again(
        string sourceType)
    {
        // The whole point of the task. MIV deducts when material is issued to a project; SRO
        // dispatch deducts on its own. Posting again would understate inventory by exactly the
        // quantity shipped, with nothing pointing at the cause.
        var h = NewHarness();
        var uuid = await Staged(h, sourceType, qty: 100m);

        var result = await h.GoodsIssue.IssueAsync(uuid, User);

        result!.PostedStock.Should().BeFalse();
        result.MovementsPosted.Should().Be(0);
        result.QtyOut.Should().Be(0m);
        result.Note.Should().Contain(sourceType);

        h.Poster.CallCount.Should().Be(0, "the poster must never be asked");
        (await StatusOf(h, uuid)).Should().Be("GOODS_ISSUED");
    }

    [Theory]
    [InlineData("MIV")]
    [InlineData("SRO")]
    public async Task A_movement_reference_delivery_still_ends_its_hold(string sourceType)
    {
        // Not posting is not the same as doing nothing. The stock has physically gone, so the
        // reservation has to end or those units stay unavailable forever.
        var h = NewHarness();
        var uuid = await Staged(h, sourceType, qty: 100m);

        h.Reservations.ActiveFor(uuid).Should().Be(100m);

        var result = await h.GoodsIssue.IssueAsync(uuid, User);

        result!.ReservationsClosed.Should().Be(1);
        h.Reservations.ActiveFor(uuid).Should().Be(0m);
    }

    [Fact]
    public async Task Issuing_twice_cannot_deduct_twice()
    {
        // The state machine refuses it outright — GOODS_ISSUED leads only onward — and the poster
        // would find no active holds even if it were reached.
        var h = NewHarness();
        var uuid = await Staged(h, qty: 100m);

        await h.GoodsIssue.IssueAsync(uuid, User);

        var act = async () => await h.GoodsIssue.IssueAsync(uuid, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*GOODS_ISSUED*");
        h.Poster.CallCount.Should().Be(1, "posted exactly once");
    }

    [Fact]
    public async Task Every_source_type_declares_whether_it_posts()
    {
        // A source type with no rule would have to be defaulted, and either default is a silent
        // ledger bug. The rule table refuses to answer rather than guess.
        foreach (var code in SMS.Modules.Logistics.Domain.LogisticsCode
                     .Codes<SMS.Modules.Logistics.Domain.DeliverySourceType>())
        {
            var act = () => SMS.Modules.Logistics.Domain.DeliverySourceTypeInfo.PostsGoodsIssue(
                SMS.Modules.Logistics.Domain.LogisticsCode
                    .Parse<SMS.Modules.Logistics.Domain.DeliverySourceType>(code));

            act.Should().NotThrow($"'{code}' must declare whether it posts goods issue");
        }
    }

    // ── Transfers ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_transfer_posts_both_legs()
    {
        // A transfer that posts only the outbound leg makes stock vanish between two warehouses.
        var h = NewHarness();
        var uuid = await Staged(h, "TRANSFER", qty: 60m);

        var result = await h.GoodsIssue.IssueAsync(uuid, User);

        result!.PostedStock.Should().BeTrue();
        result.QtyOut.Should().Be(60m);
        result.QtyIn.Should().Be(60m, "the same units arrive somewhere else");
        result.MovementsPosted.Should().Be(2);

        h.Poster.Last.ToWarehouseUuid.Should().Be(NorthUuid);
    }

    [Fact]
    public async Task A_transfer_with_nowhere_to_land_posts_nothing()
    {
        var h = NewHarness();
        var uuid = await Staged(h, "TRANSFER", qty: 60m);

        var delivery = await h.Db.DeliveryOrders.SingleAsync(d => d.UUID == uuid);
        delivery.ShipToWarehouseUuid = null;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var act = async () => await h.GoodsIssue.IssueAsync(uuid, User);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*no destination warehouse*")
            .WithMessage("*Nothing was posted*");

        h.Poster.CallCount.Should().Be(0);
        (await StatusOf(h, uuid)).Should().Be("STAGED", "the delivery is untouched");
    }

    [Fact]
    public async Task A_plain_delivery_is_not_treated_as_a_transfer()
    {
        var h = NewHarness();
        var uuid = await Staged(h, "MANUAL");

        await h.GoodsIssue.IssueAsync(uuid, User);

        h.Poster.Last.ToWarehouseUuid.Should().BeNull();
        (await h.Db.DeliveryOrders.AsNoTracking().SingleAsync(d => d.UUID == uuid))
            .Status.Should().Be("GOODS_ISSUED");
    }

    // ── What cannot be issued ─────────────────────────────────────────────────

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("RELEASED")]
    [InlineData("PICKING")]
    [InlineData("PICKED")]
    [InlineData("PACKED")]
    [InlineData("CANCELLED")]
    public async Task A_delivery_that_is_not_staged_cannot_be_issued(string status)
    {
        var h = NewHarness();
        var uuid = await Staged(h);

        var delivery = await h.Db.DeliveryOrders.SingleAsync(d => d.UUID == uuid);
        delivery.Status = status;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var act = async () => await h.GoodsIssue.IssueAsync(uuid, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage($"*{status}*");
        h.Poster.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task An_approved_delivery_can_be_issued()
    {
        // PENDING_APPROVAL is the conditional path — a shipping rule diverted it — and it leads to
        // GOODS_ISSUED just as STAGED does.
        var h = NewHarness();
        var uuid = await Staged(h);

        var delivery = await h.Db.DeliveryOrders.SingleAsync(d => d.UUID == uuid);
        delivery.Status = "PENDING_APPROVAL";
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        (await h.GoodsIssue.IssueAsync(uuid, User))!.PostedStock.Should().BeTrue();
        (await StatusOf(h, uuid)).Should().Be("GOODS_ISSUED");
    }

    [Fact]
    public async Task A_delivery_with_nothing_packed_cannot_be_issued()
    {
        // A goods issue for zero is a movement that did not happen.
        var h = NewHarness();
        var uuid = await Staged(h, qty: 100m);

        var line = await h.Db.DeliveryOrderLines.SingleAsync(l => l.DeliveryOrder.UUID == uuid);
        line.QtyPacked = 0m;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var act = async () => await h.GoodsIssue.IssueAsync(uuid, User);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*Nothing is packed*")
            .WithMessage("*Cancel it instead*");

        h.Poster.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Partly_packing_leaves_the_delivery_short_of_the_dock()
    {
        // The normal route by which unpacked stock never reaches staging: packing only part of
        // what was picked leaves the delivery in PICKED, and PICKED cannot be staged at all.
        var h = NewHarness();
        var variantUuid = Guid.NewGuid();
        h.Reservations.SetAvailable(variantUuid, 1000m);

        var uuid = await h.Deliveries.CreateAsync(new CreateDeliveryRequest
        {
            SourceType = "MANUAL", Direction = "OUTBOUND", ShipFromWarehouseUuid = CentralUuid,
            Lines = [new CreateDeliveryLineRequest
            {
                ItemDescription = "Cable", QtyOrdered = 100m, VariantUuid = variantUuid
            }]
        }, User);

        await h.Release.ReleaseAsync(uuid, null, User);

        var pickList = await h.PickLists.GenerateAsync(uuid, null, User);
        var pickLine = (await h.PickLists.GetByUuidAsync(pickList))!.Lines.Single();

        await h.PickLists.ConfirmAsync(pickList, new ConfirmPickRequest
        {
            Lines = [new ConfirmPickLineRequest { LineUuid = pickLine.UUID, QtyPicked = 100m }]
        }, Picker);

        var packLine = (await h.Packages.GetForDeliveryAsync(uuid))!.Lines.Single();
        await h.Packages.PackAsync(uuid, new PackRequest
        {
            Contents = [new PackContentRequest { DeliveryLineUuid = packLine.DeliveryLineUuid, Qty = 60m }]
        }, User);

        (await StatusOf(h, uuid)).Should().Be("PICKED");

        var act = async () => await h.GoodsIssue.StageAsync(uuid, User);
        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*PICKED*");
    }

    [Fact]
    public async Task A_packed_delivery_hiding_unpacked_picked_stock_is_still_refused()
    {
        // The backstop behind the status check. Packing keeps a delivery out of PACKED until
        // every picked unit is boxed, so this state is not reachable through the API — but a data
        // fix or a future path could produce it, and staging it would send goods to the dock that
        // are still on the floor.
        var h = NewHarness();
        var uuid = await Staged(h, qty: 100m);

        var delivery = await h.Db.DeliveryOrders.Include(d => d.Lines)
            .SingleAsync(d => d.UUID == uuid);
        delivery.Status = "PACKED";
        delivery.Lines.Single().QtyPacked = 0m;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var act = async () => await h.GoodsIssue.StageAsync(uuid, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*not in a carton*");
    }

    // ── After the point of no return ──────────────────────────────────────────

    [Fact]
    public async Task An_issued_delivery_can_no_longer_be_cancelled()
    {
        // The stock has left the books. Cancelling would leave the ledger asserting a movement the
        // document denies.
        var h = NewHarness();
        var uuid = await Staged(h);

        await h.GoodsIssue.IssueAsync(uuid, User);

        var act = async () => await h.Status.CancelAsync(
            uuid, new DeliveryReasonRequest { Reason = "Changed my mind" }, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*GOODS_ISSUED*");
    }

    [Fact]
    public async Task An_issued_deliverys_packages_can_no_longer_be_changed()
    {
        var h = NewHarness();
        var uuid = await Staged(h);

        var packageUuid = (await h.Packages.GetForDeliveryAsync(uuid))!.Packages.Single().UUID;

        await h.GoodsIssue.IssueAsync(uuid, User);

        var act = async () => await h.Packages.PatchAsync(
            packageUuid, new PatchPackageRequest { GrossWeightKg = 99m }, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*GOODS_ISSUED*");
    }

    [Fact]
    public async Task A_short_picked_delivery_issues_only_what_was_packed()
    {
        // The end-to-end number: ordered 100, picked 60, packed 60, shipped 60 — and the 40 that
        // were never found stopped being reserved back at pick confirmation.
        var h = NewHarness();
        var variantUuid = Guid.NewGuid();
        h.Reservations.SetAvailable(variantUuid, 1000m);

        var uuid = await h.Deliveries.CreateAsync(new CreateDeliveryRequest
        {
            SourceType = "MANUAL", Direction = "OUTBOUND", ShipFromWarehouseUuid = CentralUuid,
            Lines = [new CreateDeliveryLineRequest
            {
                ItemDescription = "Cable", QtyOrdered = 100m, VariantUuid = variantUuid
            }]
        }, User);

        await h.Release.ReleaseAsync(uuid, null, User);

        var pickList = await h.PickLists.GenerateAsync(uuid, null, User);
        var pickLine = (await h.PickLists.GetByUuidAsync(pickList))!.Lines.Single();

        await h.PickLists.ConfirmAsync(pickList, new ConfirmPickRequest
        {
            Lines = [new ConfirmPickLineRequest
            {
                LineUuid = pickLine.UUID, QtyPicked = 60m, ShortReasonCode = "SHORT_ON_SHELF"
            }]
        }, Picker);

        h.Reservations.ActiveFor(uuid).Should().Be(60m, "40 were handed back at confirmation");

        var packLine = (await h.Packages.GetForDeliveryAsync(uuid))!.Lines.Single();
        await h.Packages.PackAsync(uuid, new PackRequest
        {
            Contents = [new PackContentRequest { DeliveryLineUuid = packLine.DeliveryLineUuid, Qty = 60m }]
        }, User);

        await h.GoodsIssue.StageAsync(uuid, User);

        var result = await h.GoodsIssue.IssueAsync(uuid, User);

        result!.QtyShipped.Should().Be(60m);
        result.QtyOut.Should().Be(60m, "never the 100 that were ordered");
        h.Reservations.ActiveFor(uuid).Should().Be(0m);
    }

    [Fact]
    public async Task Another_organizations_delivery_cannot_be_issued()
    {
        var (db, tenant, dbName) = LogisticsTestDb.New();
        var demand       = LogisticsTestDb.Demand(dbName, tenant);
        var reservations = new FakeStockReservationService();
        var poster       = new FakeGoodsIssuePoster(reservations);
        var fulfillment  = MockFulfillment();

        var h = new Harness(
            db,
            demand,
            new DeliveryRepository(db, new DocumentNumberGenerator(db, tenant),
                                   new AddressNormalizer(new FakeCityLookup())),
            new DeliveryReleaseRepository(db, reservations, new DocumentNumberGenerator(db, tenant)),
            new DeliveryStatusRepository(db, reservations),
            new PickListRepository(db, reservations, new DocumentNumberGenerator(db, tenant)),
            new PackageRepository(db, new DocumentNumberGenerator(db, tenant)),
            new GoodsIssueRepository(db, demand, reservations, poster, fulfillment.Object, NullLogger<GoodsIssueRepository>.Instance),
            reservations, poster, fulfillment);

        var uuid = await Staged(h);

        var otherDb = LogisticsTestDb.OpenAs(dbName, Guid.NewGuid());
        var other = new GoodsIssueRepository(
            otherDb, demand, reservations, poster, fulfillment.Object, NullLogger<GoodsIssueRepository>.Instance);

        (await other.IssueAsync(uuid, User)).Should().BeNull();
        (await other.StageAsync(uuid, User)).Should().BeFalse();
        poster.CallCount.Should().Be(0);
    }

    // ── Sale orders (A29-P6-03 §7.5) ──────────────────────────────────────────
    //
    // Confirming an order held its stock under SALES_ORDER; release handed that hold to the
    // delivery. Issue is where the units finally leave, so it names the sales movement, closes
    // the (now delivery-owned) hold, and credits the order line with what went.

    private sealed record SaleOrderFlow(Guid DeliveryUuid, Guid OrderUuid, Guid OrderLineUuid, Guid VariantUuid);

    /// <summary>
    /// A confirmed order holding <paramref name="ordered"/> units, and a delivery for
    /// <paramref name="deliveryQty"/> of them taken all the way to STAGED — picking
    /// <paramref name="picked"/> if the picker found less.
    /// </summary>
    private static async Task<SaleOrderFlow> StagedSaleOrder(
        Harness h, string mode = "SHIP", decimal ordered = 100m, decimal deliveryQty = 100m, decimal? picked = null,
        bool stage = true)
    {
        var variantUuid = Guid.NewGuid();
        var order = new SaleOrder
        {
            SoNumber = "SO-2026-00042", PartnerId = Guid.NewGuid(), OrderDate = DateTime.UtcNow.Date,
            CurrencyId = Guid.NewGuid(), Status = "CONFIRMED", DeliveryMode = mode, CreatedBy = 1,
            Lines = { new SaleOrderLine { VariantUuid = variantUuid, Quantity = ordered, UnitPrice = 40m, LineTotal = ordered * 40m, FulfillmentMode = "IN_STOCK", Status = "RESERVED" } }
        };
        h.Demand.SaleOrders.Add(order);
        await h.Demand.SaveChangesAsync();
        h.Demand.ChangeTracker.Clear();
        var orderLine = order.Lines.Single();

        // Exactly what the order holds is on the shelf — so a release that reserved again, rather
        // than taking the order's hold over, would find nothing free and fail.
        h.Reservations.SetLayout(variantUuid,
            (new FakeStockReservationService.StockLocation(CentralUuid, "Central"), ordered));
        (await h.Reservations.ReserveAsync(ReservationSourceType.SalesOrder, order.UUID,
            [new ReservationRequest(variantUuid, CentralUuid, ordered, orderLine.UUID)], User)).Succeeded.Should().BeTrue();

        var uuid = await h.Deliveries.CreateAsync(new CreateDeliveryRequest
        {
            SourceType = "SALE_ORDER", SourceUuid = order.UUID, SourceNumber = order.SoNumber,
            ShipFromWarehouseUuid = CentralUuid,
            Lines = [new CreateDeliveryLineRequest { ItemDescription = "4mm cable", QtyOrdered = deliveryQty, VariantUuid = variantUuid }]
        }, User);

        var delivery = await h.Db.DeliveryOrders.Include(d => d.Lines).SingleAsync(d => d.UUID == uuid);
        delivery.SaleOrderUuid = order.UUID;
        delivery.DeliveryMode  = mode;
        delivery.Lines.Single().SoLineUuid = orderLine.UUID;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        await h.Release.ReleaseAsync(uuid, null, User);

        var pickQty  = picked ?? deliveryQty;
        var pickList = await h.PickLists.GenerateAsync(uuid, null, User);
        var pickLine = (await h.PickLists.GetByUuidAsync(pickList))!.Lines.Single();
        await h.PickLists.ConfirmAsync(pickList, new ConfirmPickRequest
        {
            Lines = [new ConfirmPickLineRequest
            {
                LineUuid = pickLine.UUID, QtyPicked = pickQty,
                ShortReasonCode = pickQty < deliveryQty ? "SHORT_ON_SHELF" : null
            }]
        }, Picker);

        var packLine = (await h.Packages.GetForDeliveryAsync(uuid))!.Lines.Single();
        await h.Packages.PackAsync(uuid, new PackRequest
        {
            GrossWeightKg = 12m,
            Contents = [new PackContentRequest { DeliveryLineUuid = packLine.DeliveryLineUuid, Qty = pickQty }]
        }, User);

        if (stage) (await h.GoodsIssue.StageAsync(uuid, User)).Should().BeTrue();
        h.Db.ChangeTracker.Clear();

        return new SaleOrderFlow(uuid, order.UUID, orderLine.UUID, variantUuid);
    }

    private static Task<SaleOrderLine> OrderLine(Harness h, SaleOrderFlow flow) =>
        h.Demand.SaleOrderLines.AsNoTracking().SingleAsync(l => l.UUID == flow.OrderLineUuid);

    [Fact]
    public async Task A_shipped_sale_order_delivery_posts_a_sales_ship_movement_against_the_delivery()
    {
        var h    = NewHarness();
        var flow = await StagedSaleOrder(h, mode: "SHIP");

        var result = await h.GoodsIssue.IssueAsync(flow.DeliveryUuid, User);

        result!.PostedStock.Should().BeTrue("the order only reserved — this is the movement");
        result.QtyOut.Should().Be(100m);
        result.QtyShipped.Should().Be(100m);

        h.Poster.CallCount.Should().Be(1);
        h.Poster.Last.TransactionType.Should().Be("SALES_SHIP");
        h.Poster.Last.SourceUuid.Should().Be(flow.DeliveryUuid, "posted against the delivery's own hold");
        h.Poster.Last.ReferenceType.Should().Be("DELIVERY");
        h.Poster.Last.ReferenceNumber.Should().Be(result.DeliveryNumber);
        h.Poster.Last.ToWarehouseUuid.Should().BeNull();

        (await StatusOf(h, flow.DeliveryUuid)).Should().Be("GOODS_ISSUED");
    }

    [Fact]
    public async Task A_self_pickup_sale_order_delivery_posts_a_sales_handover()
    {
        // TC-08: no shipment — the customer collects, and the ledger says so.
        var h    = NewHarness();
        var flow = await StagedSaleOrder(h, mode: "SELF_PICKUP");

        var result = await h.GoodsIssue.IssueAsync(flow.DeliveryUuid, User);

        result!.PostedStock.Should().BeTrue();
        h.Poster.Last.TransactionType.Should().Be("SALES_HANDOVER");
    }

    [Fact]
    public async Task The_order_was_never_reserved_twice_and_its_hold_ends_when_the_stock_leaves()
    {
        var h    = NewHarness();
        var flow = await StagedSaleOrder(h);

        // One ReserveAsync in the whole flow: the order's own, at confirmation. Release took that
        // hold over instead of asking for the same units again.
        h.Reservations.ReserveCallCount.Should().Be(1);
        h.Reservations.ActiveFor(flow.OrderUuid).Should().Be(0m, "handed to the delivery at release");
        h.Reservations.ActiveFor(flow.DeliveryUuid).Should().Be(100m);

        await h.GoodsIssue.IssueAsync(flow.DeliveryUuid, User);

        h.Reservations.ActiveFor(flow.DeliveryUuid).Should().Be(0m, "consumed — the units have left");
        h.Reservations.ActiveFor(flow.OrderUuid).Should().Be(0m);
    }

    [Fact]
    public async Task Issuing_credits_the_order_line_with_what_left_and_marks_it_fulfilled()
    {
        var h    = NewHarness();
        var flow = await StagedSaleOrder(h, ordered: 100m, deliveryQty: 100m);

        await h.GoodsIssue.IssueAsync(flow.DeliveryUuid, User);

        var line = await OrderLine(h, flow);
        line.FulfilledQty.Should().Be(100m);
        line.Status.Should().Be("FULFILLED");
    }

    [Fact]
    public async Task A_partial_delivery_credits_only_its_share_and_leaves_the_rest_held_for_the_order()
    {
        // TC-09: 40 of 100 go out on the first delivery; the order keeps holding the other 60.
        var h    = NewHarness();
        var flow = await StagedSaleOrder(h, ordered: 100m, deliveryQty: 40m);

        h.Reservations.ActiveFor(flow.OrderUuid).Should().Be(60m, "only the delivery's share changed hands");

        await h.GoodsIssue.IssueAsync(flow.DeliveryUuid, User);

        var line = await OrderLine(h, flow);
        line.FulfilledQty.Should().Be(40m);
        line.Status.Should().Be("PARTIALLY_FULFILLED");
        h.Reservations.ActiveFor(flow.OrderUuid).Should().Be(60m, "still promised to the customer");
    }

    [Fact]
    public async Task A_short_picked_delivery_credits_what_actually_shipped()
    {
        // Ordered 100, found 70. The order must still see 30 outstanding.
        var h    = NewHarness();
        var flow = await StagedSaleOrder(h, ordered: 100m, deliveryQty: 100m, picked: 70m);

        var result = await h.GoodsIssue.IssueAsync(flow.DeliveryUuid, User);

        result!.QtyOut.Should().Be(70m);
        var line = await OrderLine(h, flow);
        line.FulfilledQty.Should().Be(70m);
        line.Status.Should().Be("PARTIALLY_FULFILLED");
    }

    [Fact]
    public async Task Fulfilled_qty_accumulates_across_deliveries()
    {
        var h     = NewHarness();
        var first = await StagedSaleOrder(h, ordered: 100m, deliveryQty: 40m);
        await h.GoodsIssue.IssueAsync(first.DeliveryUuid, User);

        // A second delivery for the balance, against the same order line.
        var uuid = await h.Deliveries.CreateAsync(new CreateDeliveryRequest
        {
            SourceType = "SALE_ORDER", SourceUuid = first.OrderUuid, ShipFromWarehouseUuid = CentralUuid,
            Lines = [new CreateDeliveryLineRequest { ItemDescription = "4mm cable", QtyOrdered = 60m, VariantUuid = first.VariantUuid }]
        }, User);
        var delivery = await h.Db.DeliveryOrders.Include(d => d.Lines).SingleAsync(d => d.UUID == uuid);
        delivery.SaleOrderUuid = first.OrderUuid;
        delivery.DeliveryMode  = "SELF_PICKUP";
        delivery.Lines.Single().SoLineUuid = first.OrderLineUuid;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        await h.Release.ReleaseAsync(uuid, null, User);
        var pickList = await h.PickLists.GenerateAsync(uuid, null, User);
        var pickLine = (await h.PickLists.GetByUuidAsync(pickList))!.Lines.Single();
        await h.PickLists.ConfirmAsync(pickList, new ConfirmPickRequest
        {
            Lines = [new ConfirmPickLineRequest { LineUuid = pickLine.UUID, QtyPicked = 60m }]
        }, Picker);
        var packLine = (await h.Packages.GetForDeliveryAsync(uuid))!.Lines.Single();
        await h.Packages.PackAsync(uuid, new PackRequest
        {
            Contents = [new PackContentRequest { DeliveryLineUuid = packLine.DeliveryLineUuid, Qty = 60m }]
        }, User);
        await h.GoodsIssue.StageAsync(uuid, User);
        h.Db.ChangeTracker.Clear();

        await h.GoodsIssue.IssueAsync(uuid, User);

        var line = await OrderLine(h, first);
        line.FulfilledQty.Should().Be(100m);
        line.Status.Should().Be("FULFILLED");
        // Explicit list: the params overload of Equal would read a trailing "because" as a third item.
        h.Poster.Postings.Select(p => p.TransactionType).Should().Equal(
            new List<string?> { "SALES_SHIP", "SALES_HANDOVER" },
            "each delivery of one order classifies its own movement");
        h.Reservations.ActiveFor(first.OrderUuid).Should().Be(0m);
    }

    [Fact]
    public async Task A_sale_order_delivery_with_no_mode_is_refused_before_anything_is_posted()
    {
        // The movement kind depends on the mode; guessing it would file the ledger entry under the
        // wrong heading with nothing to flag it.
        var h    = NewHarness();
        var flow = await StagedSaleOrder(h);

        var delivery = await h.Db.DeliveryOrders.SingleAsync(d => d.UUID == flow.DeliveryUuid);
        delivery.DeliveryMode = null;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var act = async () => await h.GoodsIssue.IssueAsync(flow.DeliveryUuid, User);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*delivery mode*")
            .WithMessage("*Nothing was posted*");

        h.Poster.CallCount.Should().Be(0);
        (await StatusOf(h, flow.DeliveryUuid)).Should().Be("STAGED");
        (await OrderLine(h, flow)).FulfilledQty.Should().Be(0m);
    }

    [Fact]
    public async Task Deliveries_that_are_not_for_a_sale_order_still_post_a_plain_delivery_issue()
    {
        var h = NewHarness();
        var uuid = await Staged(h, "MANUAL");

        await h.GoodsIssue.IssueAsync(uuid, User);

        h.Poster.Last.TransactionType.Should().Be("DELIVERY_ISSUE");
    }

    // ── Self-pickup collection (A29-P6-04 §8.2) ───────────────────────────────

    private static RecordPickupRequest Collector(string idType = "CNIC", string? authorization = null) => new()
    {
        PickupPersonName     = "Ahmed Raza",
        PickupPersonIdType   = idType,
        PickupPersonIdNumber = "35202-1234567-1",
        PickupAuthorization  = authorization
    };

    private static Task<SMS.Modules.Logistics.Domain.DeliveryOrder> Delivery(Harness h, Guid uuid) =>
        h.Db.DeliveryOrders.Include(d => d.Lines).AsNoTracking().SingleAsync(d => d.UUID == uuid);

    [Fact]
    public async Task Recording_a_collection_issues_the_stock_as_a_handover_and_marks_the_delivery_delivered()
    {
        // TC-08 end to end: confirm → delivery → pick, pack → record pickup person → DELIVERED,
        // with the stock leaving as SALES_HANDOVER.
        var h    = NewHarness();
        var flow = await StagedSaleOrder(h, mode: "SELF_PICKUP");

        var result = await h.GoodsIssue.RecordPickupAsync(
            flow.DeliveryUuid, Collector(authorization: "Letter AL-2026-114"), User);

        result!.Status.Should().Be("DELIVERED");
        result.PickupPersonName.Should().Be("Ahmed Raza");
        result.GoodsIssue.Should().NotBeNull("this call is what issued the stock");
        result.GoodsIssue!.PostedStock.Should().BeTrue();
        result.GoodsIssue.QtyOut.Should().Be(100m);

        h.Poster.CallCount.Should().Be(1);
        h.Poster.Last.TransactionType.Should().Be("SALES_HANDOVER");

        var delivery = await Delivery(h, flow.DeliveryUuid);
        delivery.Status.Should().Be("DELIVERED");
        delivery.PickupPersonName.Should().Be("Ahmed Raza");
        delivery.PickupPersonIdType.Should().Be("CNIC");
        delivery.PickupPersonIdNumber.Should().Be("35202-1234567-1");
        delivery.PickupAuthorization.Should().Be("Letter AL-2026-114");
        delivery.PickedUpAt.Should().NotBeNull();
        delivery.PickedUpBy.Should().Be(User);
        delivery.GoodsIssuedAt.Should().NotBeNull();
        delivery.Lines.Single().QtyDelivered.Should().Be(100m, "what was issued is what the customer walked out with");

        var line = await OrderLine(h, flow);
        line.FulfilledQty.Should().Be(100m);
        line.Status.Should().Be("FULFILLED");
        h.Reservations.ActiveFor(flow.DeliveryUuid).Should().Be(0m);
    }

    [Fact]
    public async Task A_delivery_already_issued_is_simply_marked_delivered()
    {
        // The store posted the issue when it staged the goods; the customer arrives the next day.
        var h    = NewHarness();
        var flow = await StagedSaleOrder(h, mode: "SELF_PICKUP");
        await h.GoodsIssue.IssueAsync(flow.DeliveryUuid, User);

        var result = await h.GoodsIssue.RecordPickupAsync(flow.DeliveryUuid, Collector(), User);

        result!.Status.Should().Be("DELIVERED");
        result.GoodsIssue.Should().BeNull("nothing was issued by this call");
        h.Poster.CallCount.Should().Be(1, "issued exactly once");
        (await OrderLine(h, flow)).FulfilledQty.Should().Be(100m, "credited once, not twice");
    }

    [Fact]
    public async Task A_packed_delivery_is_staged_issued_and_delivered_in_one_go()
    {
        // §8.2: staging is optional on the self-pickup path — the counter is the dock.
        var h    = NewHarness();
        var flow = await StagedSaleOrder(h, mode: "SELF_PICKUP", stage: false);
        (await StatusOf(h, flow.DeliveryUuid)).Should().Be("PACKED");

        var result = await h.GoodsIssue.RecordPickupAsync(flow.DeliveryUuid, Collector(), User);

        result!.Status.Should().Be("DELIVERED");
        result.GoodsIssue.Should().NotBeNull();
        h.Poster.Last.TransactionType.Should().Be("SALES_HANDOVER");
    }

    [Fact]
    public async Task A_shipped_delivery_cannot_be_collected()
    {
        var h    = NewHarness();
        var flow = await StagedSaleOrder(h, mode: "SHIP");

        var act = async () => await h.GoodsIssue.RecordPickupAsync(flow.DeliveryUuid, Collector(), User);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*not a self-pickup*")
            .WithMessage("*SHIP*");

        h.Poster.CallCount.Should().Be(0);
        (await StatusOf(h, flow.DeliveryUuid)).Should().Be("STAGED");
    }

    [Fact]
    public async Task A_delivery_with_no_mode_at_all_cannot_be_collected()
    {
        var h    = NewHarness();
        var uuid = await Staged(h, "MANUAL");

        var act = async () => await h.GoodsIssue.RecordPickupAsync(uuid, Collector(), User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*not a self-pickup*");
        h.Poster.CallCount.Should().Be(0);
    }

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("RELEASED")]
    [InlineData("PICKING")]
    [InlineData("PICKED")]
    public async Task Goods_not_yet_ready_cannot_be_handed_over(string status)
    {
        var h    = NewHarness();
        var flow = await StagedSaleOrder(h, mode: "SELF_PICKUP");

        var delivery = await h.Db.DeliveryOrders.SingleAsync(d => d.UUID == flow.DeliveryUuid);
        delivery.Status = status;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var act = async () => await h.GoodsIssue.RecordPickupAsync(flow.DeliveryUuid, Collector(), User);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage($"*{status}*")
            .WithMessage("*nothing to hand over*");
        h.Poster.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task A_collection_is_recorded_once()
    {
        var h    = NewHarness();
        var flow = await StagedSaleOrder(h, mode: "SELF_PICKUP");
        await h.GoodsIssue.RecordPickupAsync(flow.DeliveryUuid, Collector(), User);

        var act = async () => await h.GoodsIssue.RecordPickupAsync(flow.DeliveryUuid, Collector(), User);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*already collected*")
            .WithMessage("*Ahmed Raza*");
        h.Poster.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task The_collector_must_be_named_and_identified()
    {
        var h    = NewHarness();
        var flow = await StagedSaleOrder(h, mode: "SELF_PICKUP");

        var noName = Collector(); noName.PickupPersonName = "  ";
        var noId   = Collector(); noId.PickupPersonIdNumber = "";
        var badId  = Collector(idType: "VOTER_CARD");

        (await ((Func<Task>)(() => h.GoodsIssue.RecordPickupAsync(flow.DeliveryUuid, noName, User)))
            .Should().ThrowAsync<BadRequestException>()).WithMessage("*name*");
        (await ((Func<Task>)(() => h.GoodsIssue.RecordPickupAsync(flow.DeliveryUuid, noId, User)))
            .Should().ThrowAsync<BadRequestException>()).WithMessage("*ID number*");
        (await ((Func<Task>)(() => h.GoodsIssue.RecordPickupAsync(flow.DeliveryUuid, badId, User)))
            .Should().ThrowAsync<BadRequestException>())
            .WithMessage("*VOTER_CARD*")
            .WithMessage("*CNIC, LICENSE, PASSPORT*");

        h.Poster.CallCount.Should().Be(0, "validation comes before anything leaves the books");
        (await StatusOf(h, flow.DeliveryUuid)).Should().Be("STAGED");
    }

    [Fact]
    public async Task The_id_type_is_stored_as_its_code_whatever_case_it_arrived_in()
    {
        var h    = NewHarness();
        var flow = await StagedSaleOrder(h, mode: "SELF_PICKUP");

        await h.GoodsIssue.RecordPickupAsync(flow.DeliveryUuid, Collector(idType: "passport"), User);

        var delivery = await Delivery(h, flow.DeliveryUuid);
        delivery.PickupPersonIdType.Should().Be("PASSPORT");
        delivery.PickupAuthorization.Should().BeNull("a customer collecting their own order needs none");
    }

    [Fact]
    public async Task An_unknown_delivery_has_no_collection_to_record()
    {
        var h = NewHarness();

        (await h.GoodsIssue.RecordPickupAsync(Guid.NewGuid(), Collector(), User)).Should().BeNull();
    }

    [Fact]
    public async Task A_short_picked_collection_hands_over_what_was_found()
    {
        var h    = NewHarness();
        var flow = await StagedSaleOrder(h, mode: "SELF_PICKUP", deliveryQty: 100m, picked: 70m);

        var result = await h.GoodsIssue.RecordPickupAsync(flow.DeliveryUuid, Collector(), User);

        result!.GoodsIssue!.QtyOut.Should().Be(70m);
        (await Delivery(h, flow.DeliveryUuid)).Lines.Single().QtyDelivered.Should().Be(70m);
        (await OrderLine(h, flow)).FulfilledQty.Should().Be(70m);
    }

    // ── Telling the sale order (A29-P6-06 §7.6) ───────────────────────────────

    [Fact]
    public async Task A_completed_collection_tells_the_sale_order_what_was_handed_over()
    {
        var h    = NewHarness();
        var flow = await StagedSaleOrder(h, mode: "SELF_PICKUP", ordered: 100m, deliveryQty: 100m, picked: 70m);

        var result = await h.GoodsIssue.RecordPickupAsync(flow.DeliveryUuid, Collector(), User);

        h.Fulfillment.Verify(f => f.RecordDeliveryCompletedAsync(It.Is<DeliveryCompletion>(c =>
            c.SaleOrderUuid == flow.OrderUuid
            && c.DeliveryUuid == flow.DeliveryUuid
            && c.DeliveryNumber == result!.DeliveryNumber
            && c.UserId == User
            && c.Lines.Count == 1
            && c.Lines[0].SoLineUuid == flow.OrderLineUuid
            && c.Lines[0].QtyDelivered == 70m)), Times.Once);

        result!.SaleOrderStatus.Should().Be("FULFILLED", "what the order became is reported back to the counter");
    }

    [Fact]
    public async Task The_sale_order_is_told_after_the_delivery_is_saved_as_delivered()
    {
        // The order's bookkeeping runs on a delivery that is already DELIVERED, so a re-derivation
        // that reads back the delivery sees the final state.
        var h    = NewHarness();
        var flow = await StagedSaleOrder(h, mode: "SELF_PICKUP");

        string? statusWhenTold = null;
        h.Fulfillment.Setup(f => f.RecordDeliveryCompletedAsync(It.IsAny<DeliveryCompletion>()))
            .Returns(async (DeliveryCompletion c) =>
            {
                statusWhenTold = await StatusOf(h, c.DeliveryUuid);
                return new FulfillmentResult(true, "PARTIALLY_FULFILLED", false, 100m, 40m);
            });

        var result = await h.GoodsIssue.RecordPickupAsync(flow.DeliveryUuid, Collector(), User);

        statusWhenTold.Should().Be("DELIVERED");
        result!.SaleOrderStatus.Should().Be("PARTIALLY_FULFILLED");
    }

    [Fact]
    public async Task A_failure_on_the_sale_order_side_does_not_undo_the_collection()
    {
        // The customer has the goods whatever the order's bookkeeping does next.
        var h    = NewHarness();
        var flow = await StagedSaleOrder(h, mode: "SELF_PICKUP");
        h.Fulfillment.Setup(f => f.RecordDeliveryCompletedAsync(It.IsAny<DeliveryCompletion>()))
            .ThrowsAsync(new InvalidOperationException("demand database unavailable"));

        var result = await h.GoodsIssue.RecordPickupAsync(flow.DeliveryUuid, Collector(), User);

        result!.Status.Should().Be("DELIVERED");
        result.SaleOrderStatus.Should().BeNull("logged for reconciliation rather than surfaced as a failed collection");
        (await StatusOf(h, flow.DeliveryUuid)).Should().Be("DELIVERED");
        (await Delivery(h, flow.DeliveryUuid)).PickupPersonName.Should().Be("Ahmed Raza");
    }

    [Fact]
    public async Task A_collection_that_is_not_for_a_sale_order_tells_nobody()
    {
        var h    = NewHarness();
        var uuid = await Staged(h, "MANUAL");
        var delivery = await h.Db.DeliveryOrders.SingleAsync(d => d.UUID == uuid);
        delivery.DeliveryMode = "SELF_PICKUP";
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var result = await h.GoodsIssue.RecordPickupAsync(uuid, Collector(), User);

        result!.Status.Should().Be("DELIVERED");
        result.SaleOrderStatus.Should().BeNull();
        h.Fulfillment.Verify(f => f.RecordDeliveryCompletedAsync(It.IsAny<DeliveryCompletion>()), Times.Never);
    }
}

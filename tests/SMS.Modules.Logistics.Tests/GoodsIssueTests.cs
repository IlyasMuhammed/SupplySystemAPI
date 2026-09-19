using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;
using SMS.Modules.Logistics.Services;
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
        DeliveryRepository Deliveries,
        DeliveryReleaseRepository Release,
        DeliveryStatusRepository Status,
        PickListRepository PickLists,
        PackageRepository Packages,
        GoodsIssueRepository GoodsIssue,
        FakeStockReservationService Reservations,
        FakeGoodsIssuePoster Poster);

    private static Harness NewHarness()
    {
        var (db, tenant, _) = LogisticsTestDb.New();
        var reservations = new FakeStockReservationService();
        var poster       = new FakeGoodsIssuePoster(reservations);

        return new Harness(
            db,
            new DeliveryRepository(db, new DocumentNumberGenerator(db, tenant),
                                   new AddressNormalizer(new FakeCityLookup())),
            new DeliveryReleaseRepository(db, reservations, new DocumentNumberGenerator(db, tenant)),
            new DeliveryStatusRepository(db, reservations),
            new PickListRepository(db, reservations, new DocumentNumberGenerator(db, tenant)),
            new PackageRepository(db, new DocumentNumberGenerator(db, tenant)),
            new GoodsIssueRepository(db, reservations, poster),
            reservations,
            poster);
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
        var reservations = new FakeStockReservationService();
        var poster       = new FakeGoodsIssuePoster(reservations);

        var h = new Harness(
            db,
            new DeliveryRepository(db, new DocumentNumberGenerator(db, tenant),
                                   new AddressNormalizer(new FakeCityLookup())),
            new DeliveryReleaseRepository(db, reservations, new DocumentNumberGenerator(db, tenant)),
            new DeliveryStatusRepository(db, reservations),
            new PickListRepository(db, reservations, new DocumentNumberGenerator(db, tenant)),
            new PackageRepository(db, new DocumentNumberGenerator(db, tenant)),
            new GoodsIssueRepository(db, reservations, poster),
            reservations, poster);

        var uuid = await Staged(h);

        var otherDb = LogisticsTestDb.OpenAs(dbName, Guid.NewGuid());
        var other = new GoodsIssueRepository(otherDb, reservations, poster);

        (await other.IssueAsync(uuid, User)).Should().BeNull();
        (await other.StageAsync(uuid, User)).Should().BeFalse();
        poster.CallCount.Should().Be(0);
    }
}

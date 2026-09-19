using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Logistics.Tests;

// T-24 — pick list generation.
public class PickListTests
{
    private const int User   = 42;
    private const int Picker = 99;

    private sealed record Harness(
        LogisticsDbContext Db,
        DeliveryRepository Deliveries,
        DeliveryReleaseRepository Release,
        DeliveryStatusRepository Status,
        PickListRepository PickLists,
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
            new PickListRepository(db, reservations, new DocumentNumberGenerator(db, tenant)),
            reservations);
    }

    private static FakeStockReservationService.StockLocation Bin(
        string zone, string bin, string? batch = null, DateTime? expiry = null) =>
        new(WarehouseUuid: CentralUuid, WarehouseName: "Central",
            ZoneName: zone, BinCode: bin, BatchNumber: batch, ExpiryDate: expiry);

    private static readonly Guid CentralUuid = Guid.NewGuid();

    /// <summary>An outbound delivery, released, so its stock is held and it is ready to pick.</summary>
    private static async Task<(Guid Uuid, List<Guid> Variants)> ReleasedDelivery(
        Harness h, params (string Desc, decimal Qty, decimal Stock)[] lines)
    {
        var variants = new List<Guid>();
        var requests = new List<CreateDeliveryLineRequest>();

        foreach (var (desc, qty, stock) in lines)
        {
            var variantUuid = Guid.NewGuid();
            variants.Add(variantUuid);

            // Only set a flat availability when the test has not declared a layout itself.
            if (stock > 0) h.Reservations.SetAvailable(variantUuid, stock);

            requests.Add(new CreateDeliveryLineRequest
            {
                ItemDescription = desc, UnitOfMeasure = "EA",
                QtyOrdered = qty, VariantUuid = variantUuid
            });
        }

        var uuid = await h.Deliveries.CreateAsync(new CreateDeliveryRequest
        {
            SourceType = "MANUAL", Direction = "OUTBOUND",
            ShipFromWarehouseUuid = CentralUuid,
            Lines = requests
        }, User);

        await h.Release.ReleaseAsync(uuid, null, User);
        h.Db.ChangeTracker.Clear();

        return (uuid, variants);
    }

    private static async Task<string> DeliveryStatus(Harness h, Guid uuid) =>
        (await h.Db.DeliveryOrders.AsNoTracking().SingleAsync(d => d.UUID == uuid)).Status;

    // ── Generating ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Generating_turns_the_held_stock_into_instructions_and_starts_picking()
    {
        var h = NewHarness();
        var (uuid, _) = await ReleasedDelivery(h, ("4mm cable", 100m, 500m));

        var pickListUuid = await h.PickLists.GenerateAsync(uuid, null, User);

        var pickList = await h.PickLists.GetByUuidAsync(pickListUuid);

        pickList!.Status.Should().Be("OPEN");
        pickList.PickListNumber.Should().StartWith("PCK-");
        pickList.DeliveryUuid.Should().Be(uuid);
        pickList.Lines.Should().ContainSingle();
        pickList.Lines[0].QtyToPick.Should().Be(100m);
        pickList.Lines[0].ItemDescription.Should().Be("4mm cable");
        pickList.Lines[0].SeqNo.Should().Be(1);

        (await DeliveryStatus(h, uuid)).Should().Be("PICKING");
    }

    [Fact]
    public async Task One_delivery_line_held_in_three_bins_becomes_three_instructions()
    {
        // The reason a pick list is its own document rather than a rendering of the delivery's
        // lines: what a picker walks is locations, not order lines.
        var h = NewHarness();
        var variantUuid = Guid.NewGuid();

        h.Reservations.SetLayout(variantUuid,
            (Bin("A", "A-01"), 30m),
            (Bin("A", "A-02"), 30m),
            (Bin("B", "B-01"), 40m));

        var uuid = await h.Deliveries.CreateAsync(new CreateDeliveryRequest
        {
            SourceType = "MANUAL", Direction = "OUTBOUND", ShipFromWarehouseUuid = CentralUuid,
            Lines = [new CreateDeliveryLineRequest
            {
                ItemDescription = "4mm cable", QtyOrdered = 100m, VariantUuid = variantUuid
            }]
        }, User);

        await h.Release.ReleaseAsync(uuid, null, User);

        var pickList = await h.PickLists.GetByUuidAsync(
            await h.PickLists.GenerateAsync(uuid, null, User));

        pickList!.Lines.Should().HaveCount(3);
        pickList.Lines.Sum(l => l.QtyToPick).Should().Be(100m);
        pickList.Lines.Select(l => l.BinCode).Should().Equal(["A-01", "A-02", "B-01"]);
        pickList.Lines.Select(l => l.SeqNo).Should().Equal([1, 2, 3]);

        // Every instruction still points back at the one delivery line it fulfils.
        pickList.Lines.Select(l => l.DeliveryLineNo).Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task The_batch_and_expiry_the_reservation_chose_are_on_the_instruction()
    {
        // The picker is told which carton to take. Without it, FEFO is decided at the shelf by
        // whoever is holding the trolley.
        var h = NewHarness();
        var variantUuid = Guid.NewGuid();

        h.Reservations.SetLayout(variantUuid,
            (Bin("A", "A-01", batch: "B-SOON", expiry: new DateTime(2026, 6, 1)), 30m),
            (Bin("A", "A-02", batch: "B-LATE", expiry: new DateTime(2028, 6, 1)), 30m));

        var uuid = await h.Deliveries.CreateAsync(new CreateDeliveryRequest
        {
            SourceType = "MANUAL", Direction = "OUTBOUND", ShipFromWarehouseUuid = CentralUuid,
            Lines = [new CreateDeliveryLineRequest
            {
                ItemDescription = "Vaccine", QtyOrdered = 45m, VariantUuid = variantUuid
            }]
        }, User);

        await h.Release.ReleaseAsync(uuid, null, User);

        var pickList = await h.PickLists.GetByUuidAsync(
            await h.PickLists.GenerateAsync(uuid, null, User));

        pickList!.Lines.Should().HaveCount(2);
        pickList.Lines[0].BatchNumber.Should().Be("B-SOON");
        pickList.Lines[0].QtyToPick.Should().Be(30m);
        pickList.Lines[0].ExpiryDate.Should().Be(new DateTime(2026, 6, 1));
        pickList.Lines[1].BatchNumber.Should().Be("B-LATE");
        pickList.Lines[1].QtyToPick.Should().Be(15m);
    }

    [Fact]
    public async Task Instructions_are_sequenced_as_a_walk_not_as_the_order_was_entered()
    {
        // Two delivery lines interleaved across the same two zones. Sequencing by line would send
        // the picker back and forth; sequencing by location walks each zone once.
        var h = NewHarness();
        var cable = Guid.NewGuid();
        var boxes = Guid.NewGuid();

        h.Reservations.SetLayout(cable, (Bin("Z2", "Z2-05"), 10m), (Bin("Z1", "Z1-02"), 10m));
        h.Reservations.SetLayout(boxes, (Bin("Z1", "Z1-01"), 10m), (Bin("Z2", "Z2-09"), 10m));

        var uuid = await h.Deliveries.CreateAsync(new CreateDeliveryRequest
        {
            SourceType = "MANUAL", Direction = "OUTBOUND", ShipFromWarehouseUuid = CentralUuid,
            Lines =
            [
                new CreateDeliveryLineRequest { ItemDescription = "Cable", QtyOrdered = 20m, VariantUuid = cable },
                new CreateDeliveryLineRequest { ItemDescription = "Boxes", QtyOrdered = 20m, VariantUuid = boxes }
            ]
        }, User);

        await h.Release.ReleaseAsync(uuid, null, User);

        var pickList = await h.PickLists.GetByUuidAsync(
            await h.PickLists.GenerateAsync(uuid, null, User));

        pickList!.Lines.Select(l => l.BinCode)
            .Should().Equal(["Z1-01", "Z1-02", "Z2-05", "Z2-09"]);
    }

    [Fact]
    public async Task Stock_with_no_bin_is_listed_last_rather_than_sent_to_a_blank_location()
    {
        // Goods received but not yet put away have no bin. Saying so at the end of the walk is
        // honest; sorting a blank code to the front sends the picker nowhere first.
        var h = NewHarness();
        var variantUuid = Guid.NewGuid();

        h.Reservations.SetLayout(variantUuid,
            (new FakeStockReservationService.StockLocation(CentralUuid, "Central"), 10m),
            (Bin("A", "A-01"), 10m));

        var uuid = await h.Deliveries.CreateAsync(new CreateDeliveryRequest
        {
            SourceType = "MANUAL", Direction = "OUTBOUND", ShipFromWarehouseUuid = CentralUuid,
            Lines = [new CreateDeliveryLineRequest
            {
                ItemDescription = "Cable", QtyOrdered = 20m, VariantUuid = variantUuid
            }]
        }, User);

        await h.Release.ReleaseAsync(uuid, null, User);

        var pickList = await h.PickLists.GetByUuidAsync(
            await h.PickLists.GenerateAsync(uuid, null, User));

        pickList!.Lines.Select(l => l.BinCode).Should().Equal(["A-01", null]);
    }

    [Fact]
    public async Task Each_instruction_names_the_reservation_it_draws_down()
    {
        // The link pick confirmation (T-25) consumes against. Without it, confirming a pick has
        // to guess which hold it closed.
        var h = NewHarness();
        var (uuid, _) = await ReleasedDelivery(h, ("Cable", 50m, 500m));

        var pickList = await h.PickLists.GetByUuidAsync(
            await h.PickLists.GenerateAsync(uuid, null, User));

        var stored = await h.Db.PickListLines.AsNoTracking().ToListAsync();

        stored.Should().ContainSingle();
        stored[0].ReservationUuid.Should().NotBeEmpty();
        _ = pickList;
    }

    [Fact]
    public async Task A_pick_list_can_be_assigned_when_it_is_generated()
    {
        var h = NewHarness();
        var (uuid, _) = await ReleasedDelivery(h, ("Cable", 50m, 500m));

        var pickListUuid = await h.PickLists.GenerateAsync(
            uuid, new GeneratePickListRequest { AssignedToUserId = Picker, Notes = "Dock 3" }, User);

        var pickList = await h.PickLists.GetByUuidAsync(pickListUuid);

        pickList!.AssignedToUserId.Should().Be(Picker);
        pickList.Notes.Should().Be("Dock 3");
    }

    // ── What cannot be generated ──────────────────────────────────────────────

    [Fact]
    public async Task A_delivery_that_is_not_released_has_nothing_to_pick()
    {
        var h = NewHarness();
        var variantUuid = Guid.NewGuid();
        h.Reservations.SetAvailable(variantUuid, 500m);

        var uuid = await h.Deliveries.CreateAsync(new CreateDeliveryRequest
        {
            SourceType = "MANUAL", Direction = "OUTBOUND",
            Lines = [new CreateDeliveryLineRequest
            {
                ItemDescription = "Cable", QtyOrdered = 10m, VariantUuid = variantUuid
            }]
        }, User);

        var act = async () => await h.PickLists.GenerateAsync(uuid, null, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*DRAFT*");
        (await h.Db.PickLists.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_inbound_delivery_cannot_be_picked()
    {
        // Goods are arriving. There is nothing on a shelf to walk to, and the reservation holds
        // nothing either — so the message has to name the real reason, not "no stock held".
        var h = NewHarness();

        var uuid = await h.Deliveries.CreateAsync(new CreateDeliveryRequest
        {
            SourceType = "PO", SourceUuid = Guid.NewGuid(), SourceNumber = "PO-1",
            Lines = [new CreateDeliveryLineRequest
            {
                ItemDescription = "Cable", QtyOrdered = 10m, VariantUuid = Guid.NewGuid()
            }]
        }, User);

        await h.Release.ReleaseAsync(uuid, null, User);

        var act = async () => await h.PickLists.GenerateAsync(uuid, null, User);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*nothing to pick*")
            .WithMessage("*GRN*");
    }

    [Fact]
    public async Task A_delivery_holding_no_stock_is_refused()
    {
        var h = NewHarness();
        var (uuid, _) = await ReleasedDelivery(h, ("Cable", 50m, 500m));

        // Something returned the hold behind the delivery's back.
        await h.Reservations.ReleaseBySourceAsync(
            SMS.Shared.Common.ReservationSourceType.Delivery, uuid, "Taken back", User);

        var act = async () => await h.PickLists.GenerateAsync(uuid, null, User);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*No stock is currently held*");
    }

    [Fact]
    public async Task An_unknown_delivery_is_reported_as_not_found()
    {
        var h = NewHarness();

        var act = async () => await h.PickLists.GenerateAsync(Guid.NewGuid(), null, User);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Generating_twice_is_refused_because_the_delivery_is_already_being_picked()
    {
        // Two sets of paper for one set of units. A picker working the stale one short-picks
        // against instructions that were already superseded. The delivery's own status is the
        // first thing that stops it.
        var h = NewHarness();
        var (uuid, _) = await ReleasedDelivery(h, ("Cable", 50m, 500m));

        await h.PickLists.GenerateAsync(uuid, null, User);

        var act = async () => await h.PickLists.GenerateAsync(uuid, null, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*PICKING*");
        (await h.Db.PickLists.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_live_pick_list_blocks_a_second_one_even_if_the_delivery_says_otherwise()
    {
        // The backstop behind the status check. If a delivery is ever returned to RELEASED with a
        // live list still against it — a bad import, a manual fix, a future code path — the status
        // check would wave a second list through and two pickers would be sent for the same units.
        var h = NewHarness();
        var (uuid, _) = await ReleasedDelivery(h, ("Cable", 50m, 500m));

        await h.PickLists.GenerateAsync(uuid, null, User);

        var delivery = await h.Db.DeliveryOrders.SingleAsync(d => d.UUID == uuid);
        delivery.Status = "RELEASED";
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var act = async () => await h.PickLists.GenerateAsync(uuid, null, User);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*already has pick list*")
            .WithMessage("*PCK-*");

        (await h.Db.PickLists.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Holding_stock_in_two_warehouses_is_not_one_walk()
    {
        var h = NewHarness();
        var variantUuid = Guid.NewGuid();

        h.Reservations.SetLayout(variantUuid,
            (new FakeStockReservationService.StockLocation(CentralUuid, "Central", "A", "A-01"), 10m),
            (new FakeStockReservationService.StockLocation(Guid.NewGuid(), "North", "A", "A-01"), 10m));

        var uuid = await h.Deliveries.CreateAsync(new CreateDeliveryRequest
        {
            SourceType = "MANUAL", Direction = "OUTBOUND",
            Lines = [new CreateDeliveryLineRequest
            {
                ItemDescription = "Cable", QtyOrdered = 20m, VariantUuid = variantUuid
            }]
        }, User);

        await h.Release.ReleaseAsync(uuid, null, User);

        var act = async () => await h.PickLists.GenerateAsync(uuid, null, User);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*2 warehouses*");
    }

    // ── Reading ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_delivery_can_be_asked_for_its_pick_list()
    {
        var h = NewHarness();
        var (uuid, _) = await ReleasedDelivery(h, ("Cable", 50m, 500m));

        var generated = await h.PickLists.GenerateAsync(uuid, null, User);

        (await h.PickLists.GetForDeliveryAsync(uuid))!.UUID.Should().Be(generated);
    }

    [Fact]
    public async Task A_delivery_with_no_pick_list_reports_none()
    {
        var h = NewHarness();
        var (uuid, _) = await ReleasedDelivery(h, ("Cable", 50m, 500m));

        (await h.PickLists.GetForDeliveryAsync(uuid)).Should().BeNull();
        (await h.PickLists.GetForDeliveryAsync(Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task The_list_view_totals_what_there_is_to_pick()
    {
        var h = NewHarness();
        var (uuid, _) = await ReleasedDelivery(h, ("Cable", 50m, 500m), ("Boxes", 20m, 500m));

        await h.PickLists.GenerateAsync(uuid, new GeneratePickListRequest { AssignedToUserId = Picker }, User);

        var page = await h.PickLists.GetListAsync(new PickListFilter());

        page.TotalRecords.Should().Be(1);
        page.Data[0].LineCount.Should().Be(2);
        page.Data[0].QtyToPick.Should().Be(70m);
        page.Data[0].QtyPicked.Should().Be(0m);
        page.Data[0].AssignedToUserId.Should().Be(Picker);
    }

    [Fact]
    public async Task The_list_can_be_narrowed_to_one_picker()
    {
        var h = NewHarness();
        var (mine,   _) = await ReleasedDelivery(h, ("Cable", 10m, 500m));
        var (theirs, _) = await ReleasedDelivery(h, ("Boxes", 10m, 500m));

        await h.PickLists.GenerateAsync(mine,   new GeneratePickListRequest { AssignedToUserId = Picker }, User);
        await h.PickLists.GenerateAsync(theirs, new GeneratePickListRequest { AssignedToUserId = 1 },      User);

        var page = await h.PickLists.GetListAsync(new PickListFilter { AssignedToUserId = Picker });

        page.TotalRecords.Should().Be(1);
        page.Data[0].DeliveryUuid.Should().Be(mine);
    }

    // ── Handing it on, and tearing it up ──────────────────────────────────────

    [Fact]
    public async Task A_pick_list_can_be_handed_to_somebody()
    {
        var h = NewHarness();
        var (uuid, _) = await ReleasedDelivery(h, ("Cable", 50m, 500m));
        var pickListUuid = await h.PickLists.GenerateAsync(uuid, null, User);

        (await h.PickLists.AssignAsync(pickListUuid, Picker, User)).Should().BeTrue();

        (await h.PickLists.GetByUuidAsync(pickListUuid))!.AssignedToUserId.Should().Be(Picker);
    }

    [Fact]
    public async Task Cancelling_returns_the_delivery_to_released_and_keeps_the_stock_held()
    {
        // The delivery is still going out — only the paper is being torn up. Releasing the hold
        // here would let something else take units this delivery has already promised.
        var h = NewHarness();
        var (uuid, _) = await ReleasedDelivery(h, ("Cable", 50m, 500m));
        var pickListUuid = await h.PickLists.GenerateAsync(uuid, null, User);

        var cancelled = await h.PickLists.CancelAsync(
            pickListUuid, new DeliveryReasonRequest { Reason = "Wrong dock" }, User);

        cancelled.Should().BeTrue();
        (await DeliveryStatus(h, uuid)).Should().Be("RELEASED");
        h.Reservations.ActiveFor(uuid).Should().Be(50m, "the units are still promised");

        var pickList = await h.PickLists.GetByUuidAsync(pickListUuid);
        pickList!.Status.Should().Be("CANCELLED");
        pickList.CancelReason.Should().Be("Wrong dock");
    }

    [Fact]
    public async Task A_cancelled_pick_list_can_be_replaced()
    {
        var h = NewHarness();
        var (uuid, _) = await ReleasedDelivery(h, ("Cable", 50m, 500m));
        var first = await h.PickLists.GenerateAsync(uuid, null, User);

        await h.PickLists.CancelAsync(first, new DeliveryReasonRequest { Reason = "Wrong dock" }, User);

        var second = await h.PickLists.GenerateAsync(uuid, null, User);

        second.Should().NotBe(first);
        (await h.PickLists.GetForDeliveryAsync(uuid))!.UUID.Should().Be(second, "the live one wins");
        (await DeliveryStatus(h, uuid)).Should().Be("PICKING");
    }

    [Fact]
    public async Task Cancelling_needs_a_reason()
    {
        var h = NewHarness();
        var (uuid, _) = await ReleasedDelivery(h, ("Cable", 50m, 500m));
        var pickListUuid = await h.PickLists.GenerateAsync(uuid, null, User);

        var act = async () => await h.PickLists.CancelAsync(
            pickListUuid, new DeliveryReasonRequest { Reason = "  " }, User);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task A_pick_list_with_stock_already_taken_off_the_shelf_cannot_be_cancelled()
    {
        // Cancelling would erase the record of what was picked, and the goods are already on a
        // trolley. Short-closing the delivery is the honest route.
        var h = NewHarness();
        var (uuid, _) = await ReleasedDelivery(h, ("Cable", 50m, 500m));
        var pickListUuid = await h.PickLists.GenerateAsync(uuid, null, User);

        var line = await h.Db.PickListLines.SingleAsync();
        line.QtyPicked = 20m;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var act = async () => await h.PickLists.CancelAsync(
            pickListUuid, new DeliveryReasonRequest { Reason = "Changed my mind" }, User);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*already picked*")
            .WithMessage("*short-close*");
    }

    [Fact]
    public async Task Cancelling_an_unknown_pick_list_is_reported_as_not_found()
    {
        var h = NewHarness();

        (await h.PickLists.CancelAsync(
            Guid.NewGuid(), new DeliveryReasonRequest { Reason = "None" }, User)).Should().BeFalse();
        (await h.PickLists.AssignAsync(Guid.NewGuid(), Picker, User)).Should().BeFalse();
    }

    [Fact]
    public async Task A_cancelled_pick_list_cannot_be_cancelled_again()
    {
        var h = NewHarness();
        var (uuid, _) = await ReleasedDelivery(h, ("Cable", 50m, 500m));
        var pickListUuid = await h.PickLists.GenerateAsync(uuid, null, User);

        await h.PickLists.CancelAsync(pickListUuid, new DeliveryReasonRequest { Reason = "One" }, User);

        var act = async () => await h.PickLists.CancelAsync(
            pickListUuid, new DeliveryReasonRequest { Reason = "Two" }, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*CANCELLED*");
    }

    // ── Tenancy ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Another_organizations_pick_list_does_not_exist_here()
    {
        var (db, tenant, dbName) = LogisticsTestDb.New();
        var reservations = new FakeStockReservationService();
        var h = new Harness(
            db,
            new DeliveryRepository(db, new DocumentNumberGenerator(db, tenant),
                                   new AddressNormalizer(new FakeCityLookup())),
            new DeliveryReleaseRepository(db, reservations, new DocumentNumberGenerator(db, tenant)),
            new DeliveryStatusRepository(db, reservations),
            new PickListRepository(db, reservations, new DocumentNumberGenerator(db, tenant)),
            reservations);

        var (uuid, _) = await ReleasedDelivery(h, ("Cable", 50m, 500m));
        var pickListUuid = await h.PickLists.GenerateAsync(uuid, null, User);

        var otherDb = LogisticsTestDb.OpenAs(dbName, Guid.NewGuid());
        var other = new PickListRepository(
            otherDb, reservations, new DocumentNumberGenerator(otherDb, tenant));

        (await other.GetByUuidAsync(pickListUuid)).Should().BeNull();
        (await other.GetListAsync(new PickListFilter())).TotalRecords.Should().Be(0);
    }
}

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

// T-25 — pick confirmation and short reasons.
public class PickConfirmationTests
{
    private const int User   = 42;
    private const int Picker = 99;

    private static readonly Guid CentralUuid = Guid.NewGuid();

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

    private static FakeStockReservationService.StockLocation Bin(string zone, string bin) =>
        new(CentralUuid, "Central", ZoneName: zone, BinCode: bin);

    /// <summary>A delivery released and turned into a pick list, ready to be walked.</summary>
    private static async Task<(Guid DeliveryUuid, Guid PickListUuid)> ReadyToPick(
        Harness h, params (string Desc, decimal Qty, decimal Stock)[] lines)
    {
        var requests = new List<CreateDeliveryLineRequest>();

        foreach (var (desc, qty, stock) in lines)
        {
            var variantUuid = Guid.NewGuid();
            h.Reservations.SetAvailable(variantUuid, stock);
            requests.Add(new CreateDeliveryLineRequest
            {
                ItemDescription = desc, UnitOfMeasure = "EA", QtyOrdered = qty, VariantUuid = variantUuid
            });
        }

        var deliveryUuid = await h.Deliveries.CreateAsync(new CreateDeliveryRequest
        {
            SourceType = "MANUAL", Direction = "OUTBOUND", ShipFromWarehouseUuid = CentralUuid,
            Lines = requests
        }, User);

        await h.Release.ReleaseAsync(deliveryUuid, null, User);
        var pickListUuid = await h.PickLists.GenerateAsync(deliveryUuid, null, User);
        h.Db.ChangeTracker.Clear();

        return (deliveryUuid, pickListUuid);
    }

    private static async Task<List<PickListLineModel>> LinesOf(Harness h, Guid pickListUuid) =>
        (await h.PickLists.GetByUuidAsync(pickListUuid))!.Lines;

    private static ConfirmPickRequest Confirm(params ConfirmPickLineRequest[] lines) =>
        new() { Lines = [.. lines] };

    private static ConfirmPickLineRequest Line(
        Guid uuid, decimal qty, string? reason = null, string? note = null) =>
        new() { LineUuid = uuid, QtyPicked = qty, ShortReasonCode = reason, ShortNote = note };

    private static async Task<string> DeliveryStatus(Harness h, Guid uuid) =>
        (await h.Db.DeliveryOrders.AsNoTracking().SingleAsync(d => d.UUID == uuid)).Status;

    // ── Picking it all ────────────────────────────────────────────────────────

    [Fact]
    public async Task Confirming_every_line_in_full_completes_the_walk()
    {
        var h = NewHarness();
        var (delivery, pickList) = await ReadyToPick(h, ("Cable", 100m, 500m));
        var line = (await LinesOf(h, pickList)).Single();

        var result = await h.PickLists.ConfirmAsync(
            pickList, Confirm(Line(line.UUID, 100m)), Picker);

        result!.Completed.Should().BeTrue();
        result.PickListStatus.Should().Be("COMPLETED");
        result.DeliveryStatus.Should().Be("PICKED");
        result.QtyPicked.Should().Be(100m);
        result.QtyShort.Should().Be(0m);
        result.QtyReturnedToStock.Should().Be(0m);
        result.LinesOutstanding.Should().Be(0);

        (await DeliveryStatus(h, delivery)).Should().Be("PICKED");
        h.Reservations.ActiveFor(delivery).Should().Be(100m, "nothing was handed back");
    }

    [Fact]
    public async Task The_picked_quantity_rolls_up_onto_the_delivery_line()
    {
        var h = NewHarness();
        var (delivery, pickList) = await ReadyToPick(h, ("Cable", 100m, 500m));
        var line = (await LinesOf(h, pickList)).Single();

        await h.PickLists.ConfirmAsync(pickList, Confirm(Line(line.UUID, 100m)), Picker);

        var deliveryLine = await h.Db.DeliveryOrderLines.AsNoTracking()
            .SingleAsync(l => l.DeliveryOrder.UUID == delivery);

        deliveryLine.QtyPicked.Should().Be(100m);
    }

    [Fact]
    public async Task Several_instructions_for_one_delivery_line_sum_onto_it()
    {
        // The roll-up has to be a sum, not a copy: stock split across bins is normal.
        var h = NewHarness();
        var variantUuid = Guid.NewGuid();

        h.Reservations.SetLayout(variantUuid,
            (Bin("A", "A-01"), 40m), (Bin("A", "A-02"), 60m));

        var delivery = await h.Deliveries.CreateAsync(new CreateDeliveryRequest
        {
            SourceType = "MANUAL", Direction = "OUTBOUND", ShipFromWarehouseUuid = CentralUuid,
            Lines = [new CreateDeliveryLineRequest
            {
                ItemDescription = "Cable", QtyOrdered = 100m, VariantUuid = variantUuid
            }]
        }, User);

        await h.Release.ReleaseAsync(delivery, null, User);
        var pickList = await h.PickLists.GenerateAsync(delivery, null, User);

        var lines = await LinesOf(h, pickList);
        lines.Should().HaveCount(2);

        await h.PickLists.ConfirmAsync(
            pickList, Confirm(Line(lines[0].UUID, 40m), Line(lines[1].UUID, 60m)), Picker);

        var deliveryLine = await h.Db.DeliveryOrderLines.AsNoTracking()
            .SingleAsync(l => l.DeliveryOrder.UUID == delivery);

        deliveryLine.QtyPicked.Should().Be(100m);
    }

    [Fact]
    public async Task A_walk_can_be_confirmed_a_line_at_a_time()
    {
        // An RF gun reports each stop as it happens; the list stays open until the last one.
        var h = NewHarness();
        var (delivery, pickList) = await ReadyToPick(h, ("Cable", 50m, 500m), ("Boxes", 20m, 500m));
        var lines = await LinesOf(h, pickList);

        var first = await h.PickLists.ConfirmAsync(
            pickList, Confirm(Line(lines[0].UUID, 50m)), Picker);

        first!.Completed.Should().BeFalse();
        first.PickListStatus.Should().Be("IN_PROGRESS");
        first.LinesConfirmed.Should().Be(1);
        first.LinesOutstanding.Should().Be(1);
        (await DeliveryStatus(h, delivery)).Should().Be("PICKING", "the walk is not finished");

        var second = await h.PickLists.ConfirmAsync(
            pickList, Confirm(Line(lines[1].UUID, 20m)), Picker);

        second!.Completed.Should().BeTrue();
        (await DeliveryStatus(h, delivery)).Should().Be("PICKED");
    }

    [Fact]
    public async Task Confirming_records_who_walked_it_and_when()
    {
        var h = NewHarness();
        var (_, pickList) = await ReadyToPick(h, ("Cable", 50m, 500m));
        var line = (await LinesOf(h, pickList)).Single();

        await h.PickLists.ConfirmAsync(pickList, Confirm(Line(line.UUID, 50m)), Picker);

        var stored = await h.Db.PickListLines.AsNoTracking().SingleAsync();
        stored.PickedBy.Should().Be(Picker);
        stored.PickedAt.Should().NotBeNull();

        var detail = await h.PickLists.GetByUuidAsync(pickList);
        detail!.Lines[0].IsConfirmed.Should().BeTrue();
        detail.StartedAt.Should().NotBeNull();
        detail.CompletedAt.Should().NotBeNull();
    }

    // ── Coming back short ─────────────────────────────────────────────────────

    [Fact]
    public async Task A_short_pick_records_the_shortfall_and_its_reason()
    {
        var h = NewHarness();
        var (_, pickList) = await ReadyToPick(h, ("Cable", 100m, 500m));
        var line = (await LinesOf(h, pickList)).Single();

        var result = await h.PickLists.ConfirmAsync(
            pickList,
            Confirm(Line(line.UUID, 78m, "SHORT_ON_SHELF", "Two cartons water damaged")),
            Picker);

        result!.QtyPicked.Should().Be(78m);
        result.QtyShort.Should().Be(22m);

        var stored = await h.Db.PickListLines.AsNoTracking().SingleAsync();
        stored.QtyShort.Should().Be(22m);
        stored.ShortReasonCode.Should().Be("SHORT_ON_SHELF");
        stored.ShortReason.Should().Be("Two cartons water damaged");
    }

    [Fact]
    public async Task What_was_not_picked_stops_being_promised()
    {
        // The reason this matters: goods issue consumes the remaining hold. Leave 22 held and it
        // would deduct 22 units that never moved.
        var h = NewHarness();
        var (delivery, pickList) = await ReadyToPick(h, ("Cable", 100m, 500m));
        var line = (await LinesOf(h, pickList)).Single();

        var result = await h.PickLists.ConfirmAsync(
            pickList, Confirm(Line(line.UUID, 78m, "NOT_FOUND")), Picker);

        result!.QtyReturnedToStock.Should().Be(22m);
        h.Reservations.ActiveFor(delivery).Should().Be(78m, "the hold now matches what will ship");
    }

    [Fact]
    public async Task Picking_nothing_at_all_is_a_valid_answer()
    {
        // "I looked and there was none" has to be recordable, or the line hangs unanswered and
        // the delivery never leaves PICKING.
        var h = NewHarness();
        var (delivery, pickList) = await ReadyToPick(h, ("Cable", 100m, 500m));
        var line = (await LinesOf(h, pickList)).Single();

        var result = await h.PickLists.ConfirmAsync(
            pickList, Confirm(Line(line.UUID, 0m, "NOT_FOUND")), Picker);

        result!.Completed.Should().BeTrue();
        result.QtyPicked.Should().Be(0m);
        result.QtyReturnedToStock.Should().Be(100m);
        (await DeliveryStatus(h, delivery)).Should().Be("PICKED");
        h.Reservations.ActiveFor(delivery).Should().Be(0m);
    }

    [Fact]
    public async Task Only_the_bin_that_came_up_short_hands_stock_back()
    {
        var h = NewHarness();
        var variantUuid = Guid.NewGuid();

        h.Reservations.SetLayout(variantUuid, (Bin("A", "A-01"), 40m), (Bin("A", "A-02"), 60m));

        var delivery = await h.Deliveries.CreateAsync(new CreateDeliveryRequest
        {
            SourceType = "MANUAL", Direction = "OUTBOUND", ShipFromWarehouseUuid = CentralUuid,
            Lines = [new CreateDeliveryLineRequest
            {
                ItemDescription = "Cable", QtyOrdered = 100m, VariantUuid = variantUuid
            }]
        }, User);

        await h.Release.ReleaseAsync(delivery, null, User);
        var pickList = await h.PickLists.GenerateAsync(delivery, null, User);
        var lines = await LinesOf(h, pickList);

        var result = await h.PickLists.ConfirmAsync(
            pickList,
            Confirm(Line(lines[0].UUID, 30m, "SHORT_ON_SHELF"), Line(lines[1].UUID, 60m)),
            Picker);

        result!.QtyReturnedToStock.Should().Be(10m, "only the short bin");
        h.Reservations.ActiveFor(delivery).Should().Be(90m);
    }

    [Fact]
    public async Task A_shortfall_without_a_reason_is_refused()
    {
        var h = NewHarness();
        var (_, pickList) = await ReadyToPick(h, ("Cable", 100m, 500m));
        var line = (await LinesOf(h, pickList)).Single();

        var act = async () => await h.PickLists.ConfirmAsync(
            pickList, Confirm(Line(line.UUID, 78m)), Picker);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*short by 22*")
            .WithMessage("*NOT_FOUND*");
    }

    [Fact]
    public async Task An_invalid_short_reason_is_rejected_with_the_valid_values()
    {
        var h = NewHarness();
        var (_, pickList) = await ReadyToPick(h, ("Cable", 100m, 500m));
        var line = (await LinesOf(h, pickList)).Single();

        var act = async () => await h.PickLists.ConfirmAsync(
            pickList, Confirm(Line(line.UUID, 78m, "CBA")), Picker);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*CBA*")
            .WithMessage("*QUALITY_HOLD*");
    }

    [Theory]
    [InlineData("NOT_FOUND")]
    [InlineData("SHORT_ON_SHELF")]
    [InlineData("DAMAGED")]
    [InlineData("EXPIRED")]
    [InlineData("QUALITY_HOLD")]
    [InlineData("WRONG_ITEM")]
    [InlineData("OTHER")]
    public async Task Every_short_reason_in_the_vocabulary_is_accepted(string code)
    {
        var h = NewHarness();
        var (_, pickList) = await ReadyToPick(h, ("Cable", 100m, 500m));
        var line = (await LinesOf(h, pickList)).Single();

        var result = await h.PickLists.ConfirmAsync(
            pickList, Confirm(Line(line.UUID, 50m, code)), Picker);

        result!.QtyShort.Should().Be(50m);
        (await h.Db.PickListLines.AsNoTracking().SingleAsync()).ShortReasonCode.Should().Be(code);
    }

    // ── Correcting a miscount ─────────────────────────────────────────────────

    [Fact]
    public async Task A_line_can_be_answered_again_while_the_walk_is_open()
    {
        var h = NewHarness();
        var (_, pickList) = await ReadyToPick(h, ("Cable", 50m, 500m), ("Boxes", 20m, 500m));
        var lines = await LinesOf(h, pickList);

        await h.PickLists.ConfirmAsync(
            pickList, Confirm(Line(lines[0].UUID, 30m, "SHORT_ON_SHELF")), Picker);

        await h.PickLists.ConfirmAsync(pickList, Confirm(Line(lines[0].UUID, 50m)), Picker);

        var corrected = (await LinesOf(h, pickList))[0];
        corrected.QtyPicked.Should().Be(50m);
        corrected.QtyShort.Should().Be(0m);
        corrected.ShortReasonCode.Should().BeNull("a line that is no longer short keeps no excuse");
    }

    [Fact]
    public async Task A_correction_before_completion_hands_back_only_the_final_shortfall()
    {
        // The reason the reconciliation is deferred to completion: stock handed back cannot be
        // re-held on demand, so a picker who under-reported and then corrected it would otherwise
        // find the units gone.
        var h = NewHarness();
        var (delivery, pickList) = await ReadyToPick(h, ("Cable", 50m, 500m), ("Boxes", 20m, 500m));
        var lines = await LinesOf(h, pickList);

        await h.PickLists.ConfirmAsync(
            pickList, Confirm(Line(lines[0].UUID, 10m, "NOT_FOUND")), Picker);

        h.Reservations.ActiveFor(delivery).Should().Be(70m, "nothing handed back mid-walk");

        await h.PickLists.ConfirmAsync(pickList, Confirm(Line(lines[0].UUID, 50m)), Picker);

        var result = await h.PickLists.ConfirmAsync(
            pickList, Confirm(Line(lines[1].UUID, 20m)), Picker);

        result!.QtyReturnedToStock.Should().Be(0m);
        h.Reservations.ActiveFor(delivery).Should().Be(70m);
    }

    [Fact]
    public async Task A_completed_walk_cannot_be_reopened()
    {
        var h = NewHarness();
        var (_, pickList) = await ReadyToPick(h, ("Cable", 50m, 500m));
        var line = (await LinesOf(h, pickList)).Single();

        await h.PickLists.ConfirmAsync(pickList, Confirm(Line(line.UUID, 50m)), Picker);

        var act = async () => await h.PickLists.ConfirmAsync(
            pickList, Confirm(Line(line.UUID, 40m, "DAMAGED")), Picker);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*COMPLETED*");
    }

    // ── What is refused ───────────────────────────────────────────────────────

    [Fact]
    public async Task Picking_more_than_is_reserved_is_refused()
    {
        // The surplus is not held by this delivery, so accepting it would ship units another
        // document has already promised — and goods issue would deduct stock never reserved.
        var h = NewHarness();
        var (delivery, pickList) = await ReadyToPick(h, ("Cable", 100m, 500m));
        var line = (await LinesOf(h, pickList)).Single();

        var act = async () => await h.PickLists.ConfirmAsync(
            pickList, Confirm(Line(line.UUID, 120m)), Picker);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*120*")
            .WithMessage("*only 100*")
            .WithMessage("*another document*");

        (await DeliveryStatus(h, delivery)).Should().Be("PICKING", "nothing was recorded");
    }

    [Fact]
    public async Task A_negative_quantity_is_refused()
    {
        var h = NewHarness();
        var (_, pickList) = await ReadyToPick(h, ("Cable", 100m, 500m));
        var line = (await LinesOf(h, pickList)).Single();

        var act = async () => await h.PickLists.ConfirmAsync(
            pickList, Confirm(Line(line.UUID, -5m, "OTHER")), Picker);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task A_line_from_another_pick_list_is_refused()
    {
        var h = NewHarness();
        var (_, mine)   = await ReadyToPick(h, ("Cable", 50m, 500m));
        var (_, theirs) = await ReadyToPick(h, ("Boxes", 50m, 500m));

        var theirLine = (await LinesOf(h, theirs)).Single();

        var act = async () => await h.PickLists.ConfirmAsync(
            mine, Confirm(Line(theirLine.UUID, 50m)), Picker);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*does not belong to*");
    }

    [Fact]
    public async Task The_same_line_twice_in_one_call_is_refused()
    {
        // One would silently overwrite the other, and the picker would never know which stuck.
        var h = NewHarness();
        var (_, pickList) = await ReadyToPick(h, ("Cable", 50m, 500m));
        var line = (await LinesOf(h, pickList)).Single();

        var act = async () => await h.PickLists.ConfirmAsync(
            pickList, Confirm(Line(line.UUID, 20m, "DAMAGED"), Line(line.UUID, 50m)), Picker);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*more than once*");
    }

    [Fact]
    public async Task Confirming_nothing_is_refused()
    {
        var h = NewHarness();
        var (_, pickList) = await ReadyToPick(h, ("Cable", 50m, 500m));

        var act = async () => await h.PickLists.ConfirmAsync(pickList, Confirm(), Picker);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Nothing_can_be_picked_against_a_delivery_on_hold()
    {
        // The list is still live, but the delivery is not being picked. Recording against it
        // would move it to PICKED straight out of ON_HOLD.
        var h = NewHarness();
        var (delivery, pickList) = await ReadyToPick(h, ("Cable", 50m, 500m));
        var line = (await LinesOf(h, pickList)).Single();

        await h.Status.HoldAsync(delivery, new DeliveryReasonRequest { Reason = "Credit stop" }, User);

        var act = async () => await h.PickLists.ConfirmAsync(
            pickList, Confirm(Line(line.UUID, 50m)), Picker);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*ON_HOLD*")
            .WithMessage("*not PICKING*");
    }

    [Fact]
    public async Task A_cancelled_pick_list_cannot_be_confirmed()
    {
        var h = NewHarness();
        var (_, pickList) = await ReadyToPick(h, ("Cable", 50m, 500m));
        var line = (await LinesOf(h, pickList)).Single();

        await h.PickLists.CancelAsync(pickList, new DeliveryReasonRequest { Reason = "Wrong dock" }, User);

        var act = async () => await h.PickLists.ConfirmAsync(
            pickList, Confirm(Line(line.UUID, 50m)), Picker);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*CANCELLED*");
    }

    [Fact]
    public async Task An_unknown_pick_list_is_reported_as_not_found()
    {
        var h = NewHarness();

        (await h.PickLists.ConfirmAsync(
            Guid.NewGuid(), Confirm(Line(Guid.NewGuid(), 1m)), Picker)).Should().BeNull();
    }

    // ── What happens next ─────────────────────────────────────────────────────

    [Fact]
    public async Task A_short_picked_delivery_can_be_short_closed_and_frees_nothing_twice()
    {
        // The end-to-end point: the shortfall was already handed back at completion, so the
        // short close frees only what is genuinely still held.
        var h = NewHarness();
        var (delivery, pickList) = await ReadyToPick(h, ("Cable", 100m, 500m));
        var line = (await LinesOf(h, pickList)).Single();

        await h.PickLists.ConfirmAsync(
            pickList, Confirm(Line(line.UUID, 60m, "SHORT_ON_SHELF")), Picker);

        h.Reservations.ActiveFor(delivery).Should().Be(60m);

        await h.Status.ShortCloseAsync(
            delivery, new DeliveryReasonRequest { Reason = "Supplier could not fulfil" }, User);

        h.Reservations.ActiveFor(delivery).Should().Be(0m);
        (await DeliveryStatus(h, delivery)).Should().Be("SHORT_CLOSED");
    }

    [Fact]
    public async Task The_pick_shortfall_and_the_delivery_shortfall_are_different_numbers()
    {
        // PickListLine.QtyShort is "not on the shelf". DeliveryOrderLine.QtyShort is "never
        // delivered", written by short-close. Confirming must not touch the second.
        var h = NewHarness();
        var (delivery, pickList) = await ReadyToPick(h, ("Cable", 100m, 500m));
        var line = (await LinesOf(h, pickList)).Single();

        await h.PickLists.ConfirmAsync(
            pickList, Confirm(Line(line.UUID, 60m, "SHORT_ON_SHELF")), Picker);

        var deliveryLine = await h.Db.DeliveryOrderLines.AsNoTracking()
            .SingleAsync(l => l.DeliveryOrder.UUID == delivery);

        deliveryLine.QtyPicked.Should().Be(60m);
        deliveryLine.QtyShort.Should().Be(0m, "nothing has failed to be delivered yet");
        deliveryLine.ShortReason.Should().BeNull();

        (await h.Db.PickListLines.AsNoTracking().SingleAsync()).QtyShort.Should().Be(40m);
    }

    [Fact]
    public async Task The_list_view_shows_progress_as_the_walk_goes_on()
    {
        var h = NewHarness();
        var (_, pickList) = await ReadyToPick(h, ("Cable", 50m, 500m), ("Boxes", 20m, 500m));
        var lines = await LinesOf(h, pickList);

        await h.PickLists.ConfirmAsync(pickList, Confirm(Line(lines[0].UUID, 50m)), Picker);

        var page = await h.PickLists.GetListAsync(new PickListFilter());

        page.Data[0].QtyToPick.Should().Be(70m);
        page.Data[0].QtyPicked.Should().Be(50m);
        page.Data[0].Status.Should().Be("IN_PROGRESS");
    }

    [Fact]
    public async Task A_completed_pick_list_is_still_the_one_the_delivery_reports()
    {
        var h = NewHarness();
        var (delivery, pickList) = await ReadyToPick(h, ("Cable", 50m, 500m));
        var line = (await LinesOf(h, pickList)).Single();

        await h.PickLists.ConfirmAsync(pickList, Confirm(Line(line.UUID, 50m)), Picker);

        var found = await h.PickLists.GetForDeliveryAsync(delivery);

        found!.UUID.Should().Be(pickList);
        found.Status.Should().Be("COMPLETED");
    }

    [Fact]
    public async Task Cancelling_a_delivery_mid_walk_still_returns_everything_held()
    {
        var h = NewHarness();
        var (delivery, pickList) = await ReadyToPick(h, ("Cable", 50m, 500m), ("Boxes", 20m, 500m));
        var lines = await LinesOf(h, pickList);

        await h.PickLists.ConfirmAsync(
            pickList, Confirm(Line(lines[0].UUID, 40m, "DAMAGED")), Picker);

        await h.Status.CancelAsync(
            delivery, new DeliveryReasonRequest { Reason = "Order withdrawn" }, User);

        h.Reservations.ActiveFor(delivery).Should().Be(0m);
        h.Reservations.RemainingAvailable(
            (await h.Db.DeliveryOrderLines.AsNoTracking()
                .Where(l => l.DeliveryOrder.UUID == delivery)
                .Select(l => l.VariantUuid!.Value).FirstAsync())).Should().Be(500m);
    }

    [Fact]
    public async Task Picking_against_another_organizations_list_finds_nothing()
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

        var (_, pickList) = await ReadyToPick(h, ("Cable", 50m, 500m));
        var line = (await LinesOf(h, pickList)).Single();

        var otherDb = LogisticsTestDb.OpenAs(dbName, Guid.NewGuid());
        var other = new PickListRepository(
            otherDb, reservations, new DocumentNumberGenerator(otherDb, tenant));

        (await other.ConfirmAsync(pickList, Confirm(Line(line.UUID, 50m)), Picker))
            .Should().BeNull();
    }
}

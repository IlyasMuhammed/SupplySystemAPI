using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Logistics.Tests;

// T-23 — the availability check, and splitting out what stock cannot cover.
public class DeliveryAvailabilityAndSplitTests
{
    private const int User = 42;

    private sealed record Harness(
        LogisticsDbContext Db,
        DeliveryRepository Deliveries,
        DeliveryReleaseRepository Release,
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
            reservations);
    }

    /// <summary>
    /// An outbound delivery against a source document, from (description, ordered, in stock)
    /// triples. SRO is used because it is outbound <em>and</em> carries a source, which is what
    /// makes the split's source reference worth asserting.
    /// </summary>
    private static async Task<(Guid Uuid, List<Guid> Variants, List<Guid> SourceLines)> NewDelivery(
        Harness h, params (string Desc, decimal Ordered, decimal InStock)[] lines)
    {
        var variants    = new List<Guid>();
        var sourceLines = new List<Guid>();
        var requests    = new List<CreateDeliveryLineRequest>();

        foreach (var (desc, ordered, inStock) in lines)
        {
            var variantUuid    = Guid.NewGuid();
            var sourceLineUuid = Guid.NewGuid();

            variants.Add(variantUuid);
            sourceLines.Add(sourceLineUuid);
            h.Reservations.SetAvailable(variantUuid, inStock);

            requests.Add(new CreateDeliveryLineRequest
            {
                ItemDescription = desc,
                UnitOfMeasure   = "EA",
                QtyOrdered      = ordered,
                VariantUuid     = variantUuid,
                SourceLineUuid  = sourceLineUuid
            });
        }

        var uuid = await h.Deliveries.CreateAsync(new CreateDeliveryRequest
        {
            SourceType   = "SRO",
            SourceUuid   = Guid.NewGuid(),
            SourceNumber = "SRO-2026-00042",
            ShipFromWarehouseUuid = Guid.NewGuid(),
            Lines = requests
        }, User);

        return (uuid, variants, sourceLines);
    }

    private static ReleaseDeliveryRequest Split() => new() { OnShortage = ShortageAction.Split };

    private static Task<DeliveryOrder> Reload(Harness h, Guid uuid) =>
        h.Db.DeliveryOrders.Include(d => d.Lines).AsNoTracking().SingleAsync(d => d.UUID == uuid);

    private static Task<DeliveryOrder> Backorder(Harness h, Guid originalUuid) =>
        h.Db.DeliveryOrders.Include(d => d.Lines).AsNoTracking().SingleAsync(d => d.UUID != originalUuid);

    // ── The availability check ────────────────────────────────────────────────

    [Fact]
    public async Task Availability_reports_every_line_against_the_stock_that_could_cover_it()
    {
        var h = NewHarness();
        var (uuid, _, _) = await NewDelivery(h, ("Cable", 100m, 500m), ("Boxes", 40m, 15m));

        var result = await h.Release.GetAvailabilityAsync(uuid);

        result!.RequiresStock.Should().BeTrue();
        result.Lines.Should().HaveCount(2);

        result.Lines[0].QtyOrdered.Should().Be(100m);
        result.Lines[0].QtyAvailable.Should().Be(500m);
        result.Lines[0].Shortfall.Should().Be(0m);
        result.Lines[0].WarehouseName.Should().NotBeNullOrWhiteSpace("a picker needs to know where");

        result.Lines[1].QtyAvailable.Should().Be(15m);
        result.Lines[1].Shortfall.Should().Be(25m);

        result.CanReleaseInFull.Should().BeFalse();
        result.CanReleasePartially.Should().BeTrue();
    }

    [Fact]
    public async Task Availability_says_a_fully_covered_delivery_can_be_released()
    {
        var h = NewHarness();
        var (uuid, _, _) = await NewDelivery(h, ("Cable", 100m, 500m));

        var result = await h.Release.GetAvailabilityAsync(uuid);

        result!.CanReleaseInFull.Should().BeTrue();
        result.CanReleasePartially.Should().BeFalse("there is nothing to split");
    }

    [Fact]
    public async Task Availability_counts_what_another_delivery_is_already_holding()
    {
        // A preview that ignored existing holds would show stock that is already spoken for.
        var h = NewHarness();
        var variantUuid = Guid.NewGuid();
        h.Reservations.SetAvailable(variantUuid, 100m);

        async Task<Guid> Delivery(decimal qty) => await h.Deliveries.CreateAsync(new CreateDeliveryRequest
        {
            SourceType = "MANUAL", Direction = "OUTBOUND",
            Lines = [new CreateDeliveryLineRequest
            {
                ItemDescription = "Cable", QtyOrdered = qty, VariantUuid = variantUuid
            }]
        }, User);

        var first  = await Delivery(80m);
        var second = await Delivery(80m);

        await h.Release.ReleaseAsync(first, null, User);

        var result = await h.Release.GetAvailabilityAsync(second);

        result!.Lines.Single().QtyAvailable.Should().Be(20m);
        result.CanReleaseInFull.Should().BeFalse();
    }

    [Fact]
    public async Task Availability_agrees_with_what_the_release_actually_does()
    {
        // The check and the reservation must apply the same rule. A preview that offers a release
        // the server then refuses is worse than showing nothing at all.
        var h = NewHarness();
        var (uuid, _, _) = await NewDelivery(h, ("Cable", 100m, 30m));

        (await h.Release.GetAvailabilityAsync(uuid))!.CanReleaseInFull.Should().BeFalse();

        var act = async () => await h.Release.ReleaseAsync(uuid, null, User);
        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task An_inbound_delivery_needs_no_stock_and_says_so()
    {
        var h = NewHarness();

        var uuid = await h.Deliveries.CreateAsync(new CreateDeliveryRequest
        {
            SourceType = "PO", SourceUuid = Guid.NewGuid(), SourceNumber = "PO-1",
            Lines = [new CreateDeliveryLineRequest
            {
                ItemDescription = "Cable", QtyOrdered = 100m, VariantUuid = Guid.NewGuid()
            }]
        }, User);

        var result = await h.Release.GetAvailabilityAsync(uuid);

        result!.RequiresStock.Should().BeFalse();
        result.CanReleaseInFull.Should().BeTrue();
        result.Lines.Should().BeEmpty("goods are arriving, so there is nothing to be short of");
    }

    [Fact]
    public async Task A_line_with_no_resolved_item_is_reported_rather_than_shown_as_out_of_stock()
    {
        // "0 available" would send someone to the warehouse to look for stock that is there. The
        // real problem is the line, not the shelf.
        var h = NewHarness();

        var uuid = await h.Deliveries.CreateAsync(new CreateDeliveryRequest
        {
            SourceType = "MANUAL", Direction = "OUTBOUND",
            Lines = [new CreateDeliveryLineRequest
            {
                ItemDescription = "Unresolved", QtyOrdered = 10m, ProductUuid = Guid.NewGuid()
            }]
        }, User);

        var line = (await h.Release.GetAvailabilityAsync(uuid))!.Lines.Single();

        line.QtyAvailable.Should().Be(0m);
        line.Warning.Should().Contain("No stock item is resolved");
    }

    [Fact]
    public async Task Availability_for_an_unknown_delivery_is_not_found()
    {
        var h = NewHarness();

        (await h.Release.GetAvailabilityAsync(Guid.NewGuid())).Should().BeNull();
    }

    // ── Splitting ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Splitting_releases_what_stock_covers_and_moves_the_rest_to_a_new_delivery()
    {
        var h = NewHarness();
        var (uuid, _, _) = await NewDelivery(h, ("Cable", 100m, 30m));

        (await h.Release.ReleaseAsync(uuid, Split(), User)).Should().BeTrue();

        var released = await Reload(h, uuid);
        released.Status.Should().Be("RELEASED");
        released.Lines.Single().QtyOrdered.Should().Be(30m, "trimmed to what could be held");
        h.Reservations.ActiveFor(uuid).Should().Be(30m);

        var backorder = await Backorder(h, uuid);
        backorder.Status.Should().Be("DRAFT");
        backorder.Lines.Single().QtyOrdered.Should().Be(70m);
        backorder.Notes.Should().Contain(released.DeliveryNumber, "the balance says where it came from");
        backorder.DeliveryNumber.Should().NotBe(released.DeliveryNumber);
    }

    [Fact]
    public async Task The_balance_keeps_the_same_source_so_it_stays_outstanding_against_it()
    {
        // Without this the shortfall would vanish from the source document's point of view, and
        // the over-advising guard (T-11) would let the same quantity be advised a second time.
        var h = NewHarness();
        var (uuid, _, sourceLines) = await NewDelivery(h, ("Cable", 100m, 30m));

        var original = await Reload(h, uuid);

        await h.Release.ReleaseAsync(uuid, Split(), User);

        var backorder = await Backorder(h, uuid);

        backorder.SourceType.Should().Be(original.SourceType);
        backorder.SourceUuid.Should().Be(original.SourceUuid);
        backorder.SourceNumber.Should().Be(original.SourceNumber);
        backorder.TraceId.Should().Be(original.TraceId, "the balance shares the original lineage");
        backorder.Lines.Single().SourceLineUuid.Should().Be(sourceLines[0]);
    }

    [Fact]
    public async Task The_two_documents_together_still_advise_the_original_quantity_and_no_more()
    {
        // The split must not inflate what the source document sees as advised: 30 + 70 = 100.
        var h = NewHarness();
        var (uuid, _, _) = await NewDelivery(h, ("Cable", 100m, 30m));

        var sourceUuid = (await Reload(h, uuid)).SourceUuid;

        await h.Release.ReleaseAsync(uuid, Split(), User);

        var advised = await h.Db.DeliveryOrders
            .Where(d => d.SourceUuid == sourceUuid && !d.IsDelete)
            .SelectMany(d => d.Lines)
            .SumAsync(l => l.QtyOrdered);

        advised.Should().Be(100m);
    }

    [Fact]
    public async Task A_line_with_no_stock_at_all_moves_wholly_to_the_balance()
    {
        // It cannot stay behind as a line for zero: that is not pickable, and CreateAsync would
        // reject such a line outright.
        var h = NewHarness();
        var (uuid, _, _) = await NewDelivery(h, ("Covered", 20m, 100m), ("None", 50m, 0m));

        await h.Release.ReleaseAsync(uuid, Split(), User);

        var released  = await Reload(h, uuid);
        var backorder = await Backorder(h, uuid);

        released.Lines.Should().ContainSingle().Which.ItemDescription.Should().Be("Covered");
        backorder.Lines.Should().ContainSingle().Which.ItemDescription.Should().Be("None");
        backorder.Lines.Single().QtyOrdered.Should().Be(50m);
        h.Reservations.ActiveFor(uuid).Should().Be(20m);
    }

    [Fact]
    public async Task Remaining_lines_are_renumbered_without_gaps()
    {
        var h = NewHarness();
        var (uuid, _, _) = await NewDelivery(h,
            ("First", 10m, 0m), ("Second", 10m, 100m), ("Third", 10m, 100m));

        await h.Release.ReleaseAsync(uuid, Split(), User);

        var released = await Reload(h, uuid);

        released.Lines.OrderBy(l => l.LineNo).Select(l => l.LineNo).Should().Equal([1, 2]);
        released.Lines.OrderBy(l => l.LineNo).Select(l => l.ItemDescription)
                .Should().Equal(["Second", "Third"]);
    }

    [Fact]
    public async Task Splitting_a_fully_covered_delivery_creates_no_second_document()
    {
        var h = NewHarness();
        var (uuid, _, _) = await NewDelivery(h, ("Cable", 100m, 500m));

        await h.Release.ReleaseAsync(uuid, Split(), User);

        (await h.Db.DeliveryOrders.CountAsync()).Should().Be(1, "there was nothing to split");
        h.Reservations.ActiveFor(uuid).Should().Be(100m);
    }

    [Fact]
    public async Task Splitting_when_nothing_at_all_is_available_is_refused()
    {
        // Otherwise it would release an empty delivery and clone the whole thing — two useless
        // documents in place of one honest refusal.
        var h = NewHarness();
        var (uuid, _, _) = await NewDelivery(h, ("Cable", 100m, 0m), ("Boxes", 20m, 0m));

        var act = async () => await h.Release.ReleaseAsync(uuid, Split(), User);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*Splitting would only duplicate the document*");

        (await h.Db.DeliveryOrders.CountAsync()).Should().Be(1);
        (await Reload(h, uuid)).Status.Should().Be("DRAFT");
        h.Reservations.ReserveCallCount.Should().Be(0);
    }

    [Fact]
    public async Task The_balance_can_be_released_on_its_own_once_stock_arrives()
    {
        var h = NewHarness();
        var (uuid, variants, _) = await NewDelivery(h, ("Cable", 100m, 30m));

        await h.Release.ReleaseAsync(uuid, Split(), User);

        var backorderUuid = (await Backorder(h, uuid)).UUID;

        // Replenished: the 30 still held by the released delivery, plus the 70 that were missing.
        h.Reservations.SetAvailable(variants[0], 130m);

        (await h.Release.ReleaseAsync(backorderUuid, null, User)).Should().BeTrue();

        h.Reservations.ActiveFor(backorderUuid).Should().Be(70m);
        (await Reload(h, backorderUuid)).Status.Should().Be("RELEASED");
    }

    // ── Blocking remains the default ──────────────────────────────────────────

    [Fact]
    public async Task Without_asking_for_a_split_a_shortfall_still_blocks()
    {
        var h = NewHarness();
        var (uuid, _, _) = await NewDelivery(h, ("Cable", 100m, 30m));

        var act = async () => await h.Release.ReleaseAsync(uuid, null, User);

        await act.Should().ThrowAsync<ConflictException>();
        (await h.Db.DeliveryOrders.CountAsync()).Should().Be(1, "no balance document is created");
        (await Reload(h, uuid)).Lines.Single().QtyOrdered.Should().Be(100m, "the lines are untouched");
    }

    [Theory]
    [InlineData("BLOCK")]
    [InlineData("block")]
    [InlineData(" Split ")]
    public async Task A_shortage_action_is_accepted_however_it_is_cased_or_spaced(string action)
    {
        var h = NewHarness();
        var (uuid, _, _) = await NewDelivery(h, ("Cable", 100m, 500m));

        var act = async () => await h.Release.ReleaseAsync(
            uuid, new ReleaseDeliveryRequest { OnShortage = action }, User);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task An_unknown_shortage_action_is_rejected_with_the_valid_values()
    {
        var h = NewHarness();
        var (uuid, _, _) = await NewDelivery(h, ("Cable", 100m, 500m));

        var act = async () => await h.Release.ReleaseAsync(
            uuid, new ReleaseDeliveryRequest { OnShortage = "IGNORE" }, User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*IGNORE*")
            .WithMessage("*BLOCK, SPLIT*");

        h.Reservations.ReserveCallCount.Should().Be(0, "a bad request holds nothing");
    }
}

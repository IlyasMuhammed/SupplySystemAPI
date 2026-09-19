using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Logistics.Tests;

// T-26 — the pack API.
public class PackagingTests
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
        PackageRepository Packages,
        FakeStockReservationService Reservations);

    private static Harness NewHarness()
    {
        var (db, tenant, _) = LogisticsTestDb.New();
        return Build(db, tenant);
    }

    private static Harness Build(LogisticsDbContext db, SMS.Shared.Common.ITenantContext tenant)
    {
        var reservations = new FakeStockReservationService();

        return new Harness(
            db,
            new DeliveryRepository(db, new DocumentNumberGenerator(db, tenant),
                                   new AddressNormalizer(new FakeCityLookup())),
            new DeliveryReleaseRepository(db, reservations, new DocumentNumberGenerator(db, tenant)),
            new DeliveryStatusRepository(db, reservations),
            new PickListRepository(db, reservations, new DocumentNumberGenerator(db, tenant)),
            new PackageRepository(db, new DocumentNumberGenerator(db, tenant)),
            reservations);
    }

    /// <summary>A delivery taken all the way to PICKED, so there are goods in front of a packer.</summary>
    private static async Task<Guid> Picked(
        Harness h, params (string Desc, decimal Ordered, decimal Picked)[] lines)
    {
        var requests = new List<CreateDeliveryLineRequest>();

        foreach (var (desc, ordered, _) in lines)
        {
            var variantUuid = Guid.NewGuid();
            h.Reservations.SetAvailable(variantUuid, 1000m);
            requests.Add(new CreateDeliveryLineRequest
            {
                ItemDescription = desc, UnitOfMeasure = "EA", QtyOrdered = ordered, VariantUuid = variantUuid
            });
        }

        var uuid = await h.Deliveries.CreateAsync(new CreateDeliveryRequest
        {
            SourceType = "MANUAL", Direction = "OUTBOUND", ShipFromWarehouseUuid = CentralUuid,
            Lines = requests
        }, User);

        await h.Release.ReleaseAsync(uuid, null, User);
        var pickList = await h.PickLists.GenerateAsync(uuid, null, User);

        var pickLines = (await h.PickLists.GetByUuidAsync(pickList))!.Lines;

        var confirmations = pickLines.Select((pl, i) => new ConfirmPickLineRequest
        {
            LineUuid        = pl.UUID,
            QtyPicked       = lines[i].Picked,
            ShortReasonCode = lines[i].Picked < pl.QtyToPick ? "SHORT_ON_SHELF" : null
        }).ToList();

        await h.PickLists.ConfirmAsync(pickList, new ConfirmPickRequest { Lines = confirmations }, Picker);
        h.Db.ChangeTracker.Clear();

        return uuid;
    }

    private static async Task<List<PackingLineModel>> PackingLines(Harness h, Guid deliveryUuid) =>
        (await h.Packages.GetForDeliveryAsync(deliveryUuid))!.Lines;

    private static PackRequest Pack(params (Guid Line, decimal Qty)[] contents) => new()
    {
        Contents = [.. contents.Select(c => new PackContentRequest
        {
            DeliveryLineUuid = c.Line, Qty = c.Qty
        })]
    };

    private static async Task<string> DeliveryStatus(Harness h, Guid uuid) =>
        (await h.Db.DeliveryOrders.AsNoTracking().SingleAsync(d => d.UUID == uuid)).Status;

    // ── Packing ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Packing_everything_picked_creates_a_carton_and_completes_the_delivery()
    {
        var h = NewHarness();
        var delivery = await Picked(h, ("Cable", 100m, 100m));
        var line = (await PackingLines(h, delivery)).Single();

        var packageUuid = await h.Packages.PackAsync(delivery, Pack((line.DeliveryLineUuid, 100m)), User);

        var package = await h.Packages.GetByUuidAsync(packageUuid);

        package!.PackageBarcode.Should().StartWith("HU-");
        package.PackageType.Should().Be("BOX", "the default");
        package.Contents.Should().ContainSingle();
        package.Contents[0].Qty.Should().Be(100m);
        package.Contents[0].ItemDescription.Should().Be("Cable");
        package.IsVoided.Should().BeFalse();

        (await DeliveryStatus(h, delivery)).Should().Be("PACKED");
    }

    [Fact]
    public async Task Dimensions_and_weights_are_recorded_and_the_volume_computed()
    {
        var h = NewHarness();
        var delivery = await Picked(h, ("Cable", 10m, 10m));
        var line = (await PackingLines(h, delivery)).Single();

        var packageUuid = await h.Packages.PackAsync(delivery, new PackRequest
        {
            PackageType   = "CRATE",
            LengthCm      = 100m, WidthCm = 50m, HeightCm = 40m,
            GrossWeightKg = 22.5m, NetWeightKg = 20m,
            DeclaredValue = 1500m, SealNumber = "SEAL-9",
            Contents      = [new PackContentRequest { DeliveryLineUuid = line.DeliveryLineUuid, Qty = 10m }]
        }, User);

        var package = await h.Packages.GetByUuidAsync(packageUuid);

        package!.PackageType.Should().Be("CRATE");
        package.GrossWeightKg.Should().Be(22.5m);
        package.NetWeightKg.Should().Be(20m);
        package.SealNumber.Should().Be("SEAL-9");
        package.DeclaredValue.Should().Be(1500m);

        // 100 × 50 × 40 cm = 200,000 cm³ = 0.2 m³.
        package.VolumeM3.Should().Be(0.2m);
        package.DimWeightKg.Should().BeNull("no carrier has rated it yet");
    }

    [Fact]
    public async Task A_delivery_can_be_packed_into_several_cartons()
    {
        var h = NewHarness();
        var delivery = await Picked(h, ("Cable", 100m, 100m));
        var line = (await PackingLines(h, delivery)).Single();

        await h.Packages.PackAsync(delivery, Pack((line.DeliveryLineUuid, 60m)), User);

        (await DeliveryStatus(h, delivery)).Should().Be("PICKED", "40 are still on the floor");

        var packing = await h.Packages.GetForDeliveryAsync(delivery);
        packing!.QtyPacked.Should().Be(60m);
        packing.QtyUnpacked.Should().Be(40m);
        packing.IsFullyPacked.Should().BeFalse();

        await h.Packages.PackAsync(delivery, Pack((line.DeliveryLineUuid, 40m)), User);

        (await DeliveryStatus(h, delivery)).Should().Be("PACKED");
        (await h.Packages.GetForDeliveryAsync(delivery))!.IsFullyPacked.Should().BeTrue();
    }

    [Fact]
    public async Task One_carton_can_hold_several_delivery_lines()
    {
        var h = NewHarness();
        var delivery = await Picked(h, ("Cable", 10m, 10m), ("Boxes", 5m, 5m));
        var lines = await PackingLines(h, delivery);

        var packageUuid = await h.Packages.PackAsync(
            delivery, Pack((lines[0].DeliveryLineUuid, 10m), (lines[1].DeliveryLineUuid, 5m)), User);

        var package = await h.Packages.GetByUuidAsync(packageUuid);

        package!.Contents.Should().HaveCount(2);
        package.Contents.Select(c => c.DeliveryLineNo).Should().Equal([1, 2]);
        (await DeliveryStatus(h, delivery)).Should().Be("PACKED");
    }

    [Fact]
    public async Task The_packed_quantity_rolls_up_onto_the_delivery_line()
    {
        var h = NewHarness();
        var delivery = await Picked(h, ("Cable", 100m, 100m));
        var line = (await PackingLines(h, delivery)).Single();

        await h.Packages.PackAsync(delivery, Pack((line.DeliveryLineUuid, 40m)), User);
        await h.Packages.PackAsync(delivery, Pack((line.DeliveryLineUuid, 60m)), User);

        var deliveryLine = await h.Db.DeliveryOrderLines.AsNoTracking()
            .SingleAsync(l => l.DeliveryOrder.UUID == delivery);

        deliveryLine.QtyPacked.Should().Be(100m);
    }

    [Fact]
    public async Task A_supplied_barcode_is_kept_for_a_pre_printed_label()
    {
        var h = NewHarness();
        var delivery = await Picked(h, ("Cable", 10m, 10m));
        var line = (await PackingLines(h, delivery)).Single();

        var packageUuid = await h.Packages.PackAsync(delivery, new PackRequest
        {
            PackageBarcode = "  00340434000000000123  ",
            Contents = [new PackContentRequest { DeliveryLineUuid = line.DeliveryLineUuid, Qty = 10m }]
        }, User);

        (await h.Packages.GetByUuidAsync(packageUuid))!
            .PackageBarcode.Should().Be("00340434000000000123");
    }

    [Fact]
    public async Task The_batch_the_line_was_picked_from_is_inherited()
    {
        // A packing list has to print batch numbers, and the picker already recorded them.
        // Retyping a regulated field invites a typo.
        var h = NewHarness();
        var variantUuid = Guid.NewGuid();

        h.Reservations.SetLayout(variantUuid,
            (new FakeStockReservationService.StockLocation(
                CentralUuid, "Central", "A", "A-01", BatchNumber: "B-2026-07"), 50m));

        var delivery = await h.Deliveries.CreateAsync(new CreateDeliveryRequest
        {
            SourceType = "MANUAL", Direction = "OUTBOUND", ShipFromWarehouseUuid = CentralUuid,
            Lines = [new CreateDeliveryLineRequest
            {
                ItemDescription = "Vaccine", QtyOrdered = 50m, VariantUuid = variantUuid
            }]
        }, User);

        await h.Release.ReleaseAsync(delivery, null, User);
        var pickList = await h.PickLists.GenerateAsync(delivery, null, User);
        var pickLine = (await h.PickLists.GetByUuidAsync(pickList))!.Lines.Single();

        await h.PickLists.ConfirmAsync(pickList, new ConfirmPickRequest
        {
            Lines = [new ConfirmPickLineRequest { LineUuid = pickLine.UUID, QtyPicked = 50m }]
        }, Picker);

        var line = (await PackingLines(h, delivery)).Single();
        var packageUuid = await h.Packages.PackAsync(delivery, Pack((line.DeliveryLineUuid, 50m)), User);

        (await h.Packages.GetByUuidAsync(packageUuid))!
            .Contents[0].BatchNumber.Should().Be("B-2026-07");
    }

    [Fact]
    public async Task A_batch_is_not_guessed_when_the_line_was_picked_from_two()
    {
        // Which batch went in which carton is a question only the packer can answer.
        var h = NewHarness();
        var variantUuid = Guid.NewGuid();

        h.Reservations.SetLayout(variantUuid,
            (new FakeStockReservationService.StockLocation(
                CentralUuid, "Central", "A", "A-01", BatchNumber: "B-1"), 20m),
            (new FakeStockReservationService.StockLocation(
                CentralUuid, "Central", "A", "A-02", BatchNumber: "B-2"), 30m));

        var delivery = await h.Deliveries.CreateAsync(new CreateDeliveryRequest
        {
            SourceType = "MANUAL", Direction = "OUTBOUND", ShipFromWarehouseUuid = CentralUuid,
            Lines = [new CreateDeliveryLineRequest
            {
                ItemDescription = "Vaccine", QtyOrdered = 50m, VariantUuid = variantUuid
            }]
        }, User);

        await h.Release.ReleaseAsync(delivery, null, User);
        var pickList = await h.PickLists.GenerateAsync(delivery, null, User);
        var pickLines = (await h.PickLists.GetByUuidAsync(pickList))!.Lines;

        await h.PickLists.ConfirmAsync(pickList, new ConfirmPickRequest
        {
            Lines = [.. pickLines.Select(l => new ConfirmPickLineRequest
            {
                LineUuid = l.UUID, QtyPicked = l.QtyToPick
            })]
        }, Picker);

        var line = (await PackingLines(h, delivery)).Single();
        var packageUuid = await h.Packages.PackAsync(delivery, Pack((line.DeliveryLineUuid, 20m)), User);

        (await h.Packages.GetByUuidAsync(packageUuid))!.Contents[0].BatchNumber.Should().BeNull();
    }

    // ── Only what was picked ──────────────────────────────────────────────────

    [Fact]
    public async Task Packing_more_than_was_picked_is_refused()
    {
        // Units in a box that no reservation was consumed for would put the goods issue out of
        // step with the stock ledger.
        var h = NewHarness();
        var delivery = await Picked(h, ("Cable", 100m, 60m));
        var line = (await PackingLines(h, delivery)).Single();

        line.QtyPicked.Should().Be(60m);

        var act = async () => await h.Packages.PackAsync(
            delivery, Pack((line.DeliveryLineUuid, 61m)), User);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*61*")
            .WithMessage("*60*picked*");

        (await h.Db.ShipmentPackages.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Packing_more_than_is_left_over_is_refused()
    {
        var h = NewHarness();
        var delivery = await Picked(h, ("Cable", 100m, 100m));
        var line = (await PackingLines(h, delivery)).Single();

        await h.Packages.PackAsync(delivery, Pack((line.DeliveryLineUuid, 70m)), User);

        var act = async () => await h.Packages.PackAsync(
            delivery, Pack((line.DeliveryLineUuid, 40m)), User);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*already in a carton*");
    }

    [Fact]
    public async Task A_short_picked_line_can_only_be_packed_to_what_was_picked()
    {
        var h = NewHarness();
        var delivery = await Picked(h, ("Cable", 100m, 60m));
        var line = (await PackingLines(h, delivery)).Single();

        await h.Packages.PackAsync(delivery, Pack((line.DeliveryLineUuid, 60m)), User);

        // Fully packed against what was picked, so packing is finished even though the order is not.
        (await DeliveryStatus(h, delivery)).Should().Be("PACKED");
        (await h.Packages.GetForDeliveryAsync(delivery))!.QtyUnpacked.Should().Be(0m);
    }

    [Fact]
    public async Task An_empty_carton_is_refused()
    {
        var h = NewHarness();
        var delivery = await Picked(h, ("Cable", 10m, 10m));

        var act = async () => await h.Packages.PackAsync(delivery, Pack(), User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*nothing to label*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_content_quantity_must_be_more_than_zero(decimal qty)
    {
        var h = NewHarness();
        var delivery = await Picked(h, ("Cable", 10m, 10m));
        var line = (await PackingLines(h, delivery)).Single();

        var act = async () => await h.Packages.PackAsync(
            delivery, Pack((line.DeliveryLineUuid, qty)), User);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task The_same_line_twice_in_one_carton_is_refused()
    {
        var h = NewHarness();
        var delivery = await Picked(h, ("Cable", 100m, 100m));
        var line = (await PackingLines(h, delivery)).Single();

        var act = async () => await h.Packages.PackAsync(
            delivery, Pack((line.DeliveryLineUuid, 10m), (line.DeliveryLineUuid, 20m)), User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*more than once*");
    }

    [Fact]
    public async Task A_line_from_another_delivery_is_refused()
    {
        var h = NewHarness();
        var mine   = await Picked(h, ("Cable", 10m, 10m));
        var theirs = await Picked(h, ("Boxes", 10m, 10m));

        var theirLine = (await PackingLines(h, theirs)).Single();

        var act = async () => await h.Packages.PackAsync(
            mine, Pack((theirLine.DeliveryLineUuid, 10m)), User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*does not belong to*");
    }

    [Fact]
    public async Task A_duplicate_barcode_is_refused_with_an_explanation()
    {
        var h = NewHarness();
        var delivery = await Picked(h, ("Cable", 100m, 100m));
        var line = (await PackingLines(h, delivery)).Single();

        await h.Packages.PackAsync(delivery, new PackRequest
        {
            PackageBarcode = "HU-SHARED",
            Contents = [new PackContentRequest { DeliveryLineUuid = line.DeliveryLineUuid, Qty = 10m }]
        }, User);

        var act = async () => await h.Packages.PackAsync(delivery, new PackRequest
        {
            PackageBarcode = "HU-SHARED",
            Contents = [new PackContentRequest { DeliveryLineUuid = line.DeliveryLineUuid, Qty = 10m }]
        }, User);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*already on another package*");
    }

    [Fact]
    public async Task Net_weight_cannot_exceed_gross_weight()
    {
        var h = NewHarness();
        var delivery = await Picked(h, ("Cable", 10m, 10m));
        var line = (await PackingLines(h, delivery)).Single();

        var act = async () => await h.Packages.PackAsync(delivery, new PackRequest
        {
            GrossWeightKg = 10m, NetWeightKg = 12m,
            Contents = [new PackContentRequest { DeliveryLineUuid = line.DeliveryLineUuid, Qty = 10m }]
        }, User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*gross includes the packaging*");
    }

    [Fact]
    public async Task A_negative_dimension_is_refused()
    {
        var h = NewHarness();
        var delivery = await Picked(h, ("Cable", 10m, 10m));
        var line = (await PackingLines(h, delivery)).Single();

        var act = async () => await h.Packages.PackAsync(delivery, new PackRequest
        {
            LengthCm = -5m,
            Contents = [new PackContentRequest { DeliveryLineUuid = line.DeliveryLineUuid, Qty = 10m }]
        }, User);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task An_invalid_package_type_is_rejected_with_the_valid_values()
    {
        var h = NewHarness();
        var delivery = await Picked(h, ("Cable", 10m, 10m));
        var line = (await PackingLines(h, delivery)).Single();

        var act = async () => await h.Packages.PackAsync(delivery, new PackRequest
        {
            PackageType = "SACK",
            Contents = [new PackContentRequest { DeliveryLineUuid = line.DeliveryLineUuid, Qty = 10m }]
        }, User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*SACK*")
            .WithMessage("*PALLET*");
    }

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("RELEASED")]
    [InlineData("PICKING")]
    [InlineData("GOODS_ISSUED")]
    public async Task A_delivery_that_is_not_picked_yet_cannot_be_packed(string status)
    {
        var h = NewHarness();
        var delivery = await Picked(h, ("Cable", 10m, 10m));
        var line = (await PackingLines(h, delivery)).Single();

        var order = await h.Db.DeliveryOrders.SingleAsync(d => d.UUID == delivery);
        order.Status = status;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var act = async () => await h.Packages.PackAsync(
            delivery, Pack((line.DeliveryLineUuid, 10m)), User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage($"*{status}*");
    }

    [Fact]
    public async Task An_unknown_delivery_is_reported_as_not_found()
    {
        var h = NewHarness();

        var act = async () => await h.Packages.PackAsync(
            Guid.NewGuid(), Pack((Guid.NewGuid(), 1m)), User);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    // ── Pallets ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_carton_can_be_loaded_onto_a_pallet()
    {
        var h = NewHarness();
        var delivery = await Picked(h, ("Cable", 100m, 100m));
        var line = (await PackingLines(h, delivery)).Single();

        var pallet = await h.Packages.PackAsync(delivery, new PackRequest
        {
            PackageType = "PALLET",
            Contents = [new PackContentRequest { DeliveryLineUuid = line.DeliveryLineUuid, Qty = 40m }]
        }, User);

        var carton = await h.Packages.PackAsync(delivery, new PackRequest
        {
            ParentPackageUuid = pallet,
            Contents = [new PackContentRequest { DeliveryLineUuid = line.DeliveryLineUuid, Qty = 60m }]
        }, User);

        var loaded = await h.Packages.GetByUuidAsync(carton);
        loaded!.ParentPackageUuid.Should().Be(pallet);

        var onIt = await h.Packages.GetByUuidAsync(pallet);
        onIt!.ChildPackageBarcodes.Should().ContainSingle().Which.Should().Be(loaded.PackageBarcode);
    }

    [Fact]
    public async Task Nesting_stops_at_one_level()
    {
        // Refusing a parent that is itself on something makes a cycle impossible by construction,
        // rather than by a graph walk that has to be right every time.
        var h = NewHarness();
        var delivery = await Picked(h, ("Cable", 90m, 90m));
        var line = (await PackingLines(h, delivery)).Single();

        var pallet = await h.Packages.PackAsync(delivery, new PackRequest
        {
            PackageType = "PALLET",
            Contents = [new PackContentRequest { DeliveryLineUuid = line.DeliveryLineUuid, Qty = 30m }]
        }, User);

        var carton = await h.Packages.PackAsync(delivery, new PackRequest
        {
            ParentPackageUuid = pallet,
            Contents = [new PackContentRequest { DeliveryLineUuid = line.DeliveryLineUuid, Qty = 30m }]
        }, User);

        var act = async () => await h.Packages.PackAsync(delivery, new PackRequest
        {
            ParentPackageUuid = carton,
            Contents = [new PackContentRequest { DeliveryLineUuid = line.DeliveryLineUuid, Qty = 30m }]
        }, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*one level deep*");
    }

    [Fact]
    public async Task A_pallet_from_another_delivery_cannot_be_used()
    {
        var h = NewHarness();
        var mine   = await Picked(h, ("Cable", 10m, 10m));
        var theirs = await Picked(h, ("Boxes", 10m, 10m));

        var theirLine = (await PackingLines(h, theirs)).Single();
        var theirPallet = await h.Packages.PackAsync(
            theirs, Pack((theirLine.DeliveryLineUuid, 10m)), User);

        var myLine = (await PackingLines(h, mine)).Single();

        var act = async () => await h.Packages.PackAsync(mine, new PackRequest
        {
            ParentPackageUuid = theirPallet,
            Contents = [new PackContentRequest { DeliveryLineUuid = myLine.DeliveryLineUuid, Qty = 10m }]
        }, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*another delivery*");
    }

    [Fact]
    public async Task An_unknown_pallet_is_refused()
    {
        var h = NewHarness();
        var delivery = await Picked(h, ("Cable", 10m, 10m));
        var line = (await PackingLines(h, delivery)).Single();

        var act = async () => await h.Packages.PackAsync(delivery, new PackRequest
        {
            ParentPackageUuid = Guid.NewGuid(),
            Contents = [new PackContentRequest { DeliveryLineUuid = line.DeliveryLineUuid, Qty = 10m }]
        }, User);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    // ── Amending ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Weights_can_be_corrected_after_the_carton_is_closed()
    {
        var h = NewHarness();
        var delivery = await Picked(h, ("Cable", 10m, 10m));
        var line = (await PackingLines(h, delivery)).Single();
        var packageUuid = await h.Packages.PackAsync(delivery, Pack((line.DeliveryLineUuid, 10m)), User);

        (await h.Packages.PatchAsync(packageUuid, new PatchPackageRequest
        {
            GrossWeightKg = 31.4m, LengthCm = 60m, SealNumber = "SEAL-12"
        }, User)).Should().BeTrue();

        var package = await h.Packages.GetByUuidAsync(packageUuid);
        package!.GrossWeightKg.Should().Be(31.4m);
        package.LengthCm.Should().Be(60m);
        package.SealNumber.Should().Be("SEAL-12");
        package.PackageType.Should().Be("BOX", "untouched fields stay put");
    }

    [Fact]
    public async Task A_carton_can_be_taken_off_a_pallet()
    {
        var h = NewHarness();
        var delivery = await Picked(h, ("Cable", 60m, 60m));
        var line = (await PackingLines(h, delivery)).Single();

        var pallet = await h.Packages.PackAsync(delivery, new PackRequest
        {
            PackageType = "PALLET",
            Contents = [new PackContentRequest { DeliveryLineUuid = line.DeliveryLineUuid, Qty = 30m }]
        }, User);

        var carton = await h.Packages.PackAsync(delivery, new PackRequest
        {
            ParentPackageUuid = pallet,
            Contents = [new PackContentRequest { DeliveryLineUuid = line.DeliveryLineUuid, Qty = 30m }]
        }, User);

        await h.Packages.PatchAsync(carton, new PatchPackageRequest { ClearParent = true }, User);

        (await h.Packages.GetByUuidAsync(carton))!.ParentPackageUuid.Should().BeNull();
        (await h.Packages.GetByUuidAsync(pallet))!.ChildPackageBarcodes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_pallet_carrying_cartons_cannot_be_put_on_something_else()
    {
        var h = NewHarness();
        var delivery = await Picked(h, ("Cable", 90m, 90m));
        var line = (await PackingLines(h, delivery)).Single();

        var pallet = await h.Packages.PackAsync(delivery, new PackRequest
        {
            PackageType = "PALLET",
            Contents = [new PackContentRequest { DeliveryLineUuid = line.DeliveryLineUuid, Qty = 30m }]
        }, User);

        await h.Packages.PackAsync(delivery, new PackRequest
        {
            ParentPackageUuid = pallet,
            Contents = [new PackContentRequest { DeliveryLineUuid = line.DeliveryLineUuid, Qty = 30m }]
        }, User);

        var other = await h.Packages.PackAsync(delivery, Pack((line.DeliveryLineUuid, 30m)), User);

        var act = async () => await h.Packages.PatchAsync(
            pallet, new PatchPackageRequest { ParentPackageUuid = other }, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*already carries*");
    }

    [Fact]
    public async Task A_package_cannot_be_loaded_onto_itself()
    {
        var h = NewHarness();
        var delivery = await Picked(h, ("Cable", 10m, 10m));
        var line = (await PackingLines(h, delivery)).Single();
        var packageUuid = await h.Packages.PackAsync(delivery, Pack((line.DeliveryLineUuid, 10m)), User);

        var act = async () => await h.Packages.PatchAsync(
            packageUuid, new PatchPackageRequest { ParentPackageUuid = packageUuid }, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*onto itself*");
    }

    [Fact]
    public async Task A_package_on_a_dispatched_delivery_cannot_be_changed()
    {
        var h = NewHarness();
        var delivery = await Picked(h, ("Cable", 10m, 10m));
        var line = (await PackingLines(h, delivery)).Single();
        var packageUuid = await h.Packages.PackAsync(delivery, Pack((line.DeliveryLineUuid, 10m)), User);

        var order = await h.Db.DeliveryOrders.SingleAsync(d => d.UUID == delivery);
        order.Status = "GOODS_ISSUED";
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var act = async () => await h.Packages.PatchAsync(
            packageUuid, new PatchPackageRequest { GrossWeightKg = 99m }, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*GOODS_ISSUED*");
    }

    [Fact]
    public async Task An_unknown_package_is_reported_as_not_found()
    {
        var h = NewHarness();

        (await h.Packages.GetByUuidAsync(Guid.NewGuid())).Should().BeNull();
        (await h.Packages.PatchAsync(Guid.NewGuid(), new PatchPackageRequest(), User)).Should().BeFalse();
        (await h.Packages.VoidAsync(
            Guid.NewGuid(), new DeliveryReasonRequest { Reason = "x" }, User)).Should().BeFalse();
    }

    // ── Voiding ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Voiding_a_carton_unpacks_its_contents_and_keeps_its_barcode()
    {
        // The label may already be on a real box. Reissuing the number would make two cartons
        // indistinguishable on a dock, which is the failure this layer exists to prevent.
        var h = NewHarness();
        var delivery = await Picked(h, ("Cable", 100m, 100m));
        var line = (await PackingLines(h, delivery)).Single();
        var packageUuid = await h.Packages.PackAsync(delivery, Pack((line.DeliveryLineUuid, 100m)), User);

        var barcode = (await h.Packages.GetByUuidAsync(packageUuid))!.PackageBarcode;

        (await h.Packages.VoidAsync(
            packageUuid, new DeliveryReasonRequest { Reason = "Carton split open" }, User))
            .Should().BeTrue();

        var voided = await h.Packages.GetByUuidAsync(packageUuid);
        voided!.IsVoided.Should().BeTrue();
        voided.VoidReason.Should().Be("Carton split open");
        voided.PackageBarcode.Should().Be(barcode, "the row is kept, not deleted");

        var packing = await h.Packages.GetForDeliveryAsync(delivery);
        packing!.QtyPacked.Should().Be(0m);
        packing.QtyUnpacked.Should().Be(100m);
        packing.TotalGrossWeightKg.Should().Be(0m);
    }

    [Fact]
    public async Task A_carton_can_be_voided_by_a_request_that_did_nothing_else_first()
    {
        // Voiding reads the delivery's status to decide whether the package may still be changed.
        // Every other test packs first, which leaves the delivery in the change tracker and the
        // navigation populated by fixup — so a missing Include stayed invisible until a test
        // cleared the tracker. A real request that opens by voiding has no such luck.
        var h = NewHarness();
        var delivery = await Picked(h, ("Cable", 10m, 10m));
        var line = (await PackingLines(h, delivery)).Single();
        var packageUuid = await h.Packages.PackAsync(delivery, Pack((line.DeliveryLineUuid, 10m)), User);

        h.Db.ChangeTracker.Clear();

        var act = async () => await h.Packages.VoidAsync(
            packageUuid, new DeliveryReasonRequest { Reason = "Crushed" }, User);

        await act.Should().NotThrowAsync();
        (await h.Packages.GetByUuidAsync(packageUuid))!.IsVoided.Should().BeTrue();
    }

    [Fact]
    public async Task Voiding_what_completed_the_packing_puts_the_delivery_back_to_picked()
    {
        var h = NewHarness();
        var delivery = await Picked(h, ("Cable", 100m, 100m));
        var line = (await PackingLines(h, delivery)).Single();

        await h.Packages.PackAsync(delivery, Pack((line.DeliveryLineUuid, 40m)), User);
        var last = await h.Packages.PackAsync(delivery, Pack((line.DeliveryLineUuid, 60m)), User);

        (await DeliveryStatus(h, delivery)).Should().Be("PACKED");

        await h.Packages.VoidAsync(last, new DeliveryReasonRequest { Reason = "Wrong box" }, User);

        (await DeliveryStatus(h, delivery)).Should().Be("PICKED");

        var deliveryLine = await h.Db.DeliveryOrderLines.AsNoTracking()
            .SingleAsync(l => l.DeliveryOrder.UUID == delivery);
        deliveryLine.QtyPacked.Should().Be(40m);
    }

    [Fact]
    public async Task Voided_units_can_be_packed_again()
    {
        var h = NewHarness();
        var delivery = await Picked(h, ("Cable", 100m, 100m));
        var line = (await PackingLines(h, delivery)).Single();

        var first = await h.Packages.PackAsync(delivery, Pack((line.DeliveryLineUuid, 100m)), User);
        await h.Packages.VoidAsync(first, new DeliveryReasonRequest { Reason = "Wrong box" }, User);

        var second = await h.Packages.PackAsync(delivery, Pack((line.DeliveryLineUuid, 100m)), User);

        second.Should().NotBe(first);
        (await DeliveryStatus(h, delivery)).Should().Be("PACKED");
        (await h.Db.ShipmentPackages.CountAsync()).Should().Be(2, "the voided row is kept");
    }

    [Fact]
    public async Task A_pallet_still_carrying_cartons_cannot_be_voided()
    {
        var h = NewHarness();
        var delivery = await Picked(h, ("Cable", 60m, 60m));
        var line = (await PackingLines(h, delivery)).Single();

        var pallet = await h.Packages.PackAsync(delivery, new PackRequest
        {
            PackageType = "PALLET",
            Contents = [new PackContentRequest { DeliveryLineUuid = line.DeliveryLineUuid, Qty = 30m }]
        }, User);

        await h.Packages.PackAsync(delivery, new PackRequest
        {
            ParentPackageUuid = pallet,
            Contents = [new PackContentRequest { DeliveryLineUuid = line.DeliveryLineUuid, Qty = 30m }]
        }, User);

        var act = async () => await h.Packages.VoidAsync(
            pallet, new DeliveryReasonRequest { Reason = "Broken" }, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*still carries*");
    }

    [Fact]
    public async Task Voiding_needs_a_reason_and_cannot_be_done_twice()
    {
        var h = NewHarness();
        var delivery = await Picked(h, ("Cable", 10m, 10m));
        var line = (await PackingLines(h, delivery)).Single();
        var packageUuid = await h.Packages.PackAsync(delivery, Pack((line.DeliveryLineUuid, 10m)), User);

        var noReason = async () => await h.Packages.VoidAsync(
            packageUuid, new DeliveryReasonRequest { Reason = " " }, User);
        await noReason.Should().ThrowAsync<BadRequestException>();

        await h.Packages.VoidAsync(packageUuid, new DeliveryReasonRequest { Reason = "Once" }, User);

        var twice = async () => await h.Packages.VoidAsync(
            packageUuid, new DeliveryReasonRequest { Reason = "Twice" }, User);
        (await twice.Should().ThrowAsync<ConflictException>()).WithMessage("*already voided*");
    }

    // ── Reading ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_packing_view_totals_weight_across_live_cartons_only()
    {
        var h = NewHarness();
        var delivery = await Picked(h, ("Cable", 100m, 100m));
        var line = (await PackingLines(h, delivery)).Single();

        await h.Packages.PackAsync(delivery, new PackRequest
        {
            GrossWeightKg = 12m,
            Contents = [new PackContentRequest { DeliveryLineUuid = line.DeliveryLineUuid, Qty = 50m }]
        }, User);

        var scrapped = await h.Packages.PackAsync(delivery, new PackRequest
        {
            GrossWeightKg = 100m,
            Contents = [new PackContentRequest { DeliveryLineUuid = line.DeliveryLineUuid, Qty = 50m }]
        }, User);

        await h.Packages.VoidAsync(scrapped, new DeliveryReasonRequest { Reason = "Crushed" }, User);

        var packing = await h.Packages.GetForDeliveryAsync(delivery);

        packing!.TotalGrossWeightKg.Should().Be(12m);
        packing.Packages.Should().HaveCount(2, "the voided one is still listed");
        packing.Packages.Count(p => p.IsVoided).Should().Be(1);
    }

    [Fact]
    public async Task A_delivery_with_nothing_packed_reports_what_is_waiting()
    {
        var h = NewHarness();
        var delivery = await Picked(h, ("Cable", 100m, 80m));

        var packing = await h.Packages.GetForDeliveryAsync(delivery);

        packing!.QtyPicked.Should().Be(80m);
        packing.QtyPacked.Should().Be(0m);
        packing.QtyUnpacked.Should().Be(80m);
        packing.IsFullyPacked.Should().BeFalse();
        packing.Packages.Should().BeEmpty();
        packing.Lines.Single().QtyToPack.Should().Be(80m);
    }

    [Fact]
    public async Task An_unpicked_delivery_is_not_reported_as_fully_packed()
    {
        // Zero packed against zero picked is a delivery nobody has touched, not a finished one.
        var h = NewHarness();
        var variantUuid = Guid.NewGuid();
        h.Reservations.SetAvailable(variantUuid, 500m);

        var delivery = await h.Deliveries.CreateAsync(new CreateDeliveryRequest
        {
            SourceType = "MANUAL", Direction = "OUTBOUND",
            Lines = [new CreateDeliveryLineRequest
            {
                ItemDescription = "Cable", QtyOrdered = 10m, VariantUuid = variantUuid
            }]
        }, User);

        (await h.Packages.GetForDeliveryAsync(delivery))!.IsFullyPacked.Should().BeFalse();
    }

    [Fact]
    public async Task Another_organizations_package_does_not_exist_here()
    {
        var (db, tenant, dbName) = LogisticsTestDb.New();
        var h = Build(db, tenant);

        var delivery = await Picked(h, ("Cable", 10m, 10m));
        var line = (await PackingLines(h, delivery)).Single();
        var packageUuid = await h.Packages.PackAsync(delivery, Pack((line.DeliveryLineUuid, 10m)), User);

        var otherDb = LogisticsTestDb.OpenAs(dbName, Guid.NewGuid());
        var other = new PackageRepository(otherDb, new DocumentNumberGenerator(otherDb, tenant));

        (await other.GetByUuidAsync(packageUuid)).Should().BeNull();
        (await other.GetForDeliveryAsync(delivery)).Should().BeNull();
    }
}

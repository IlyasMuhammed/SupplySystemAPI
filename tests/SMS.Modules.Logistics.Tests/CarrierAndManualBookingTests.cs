using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Domain.StateMachines;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Logistics.Tests;

// T-15 — carrier integration settings and the manual booking path.
public class CarrierAndManualBookingTests
{
    private const int User = 42;

    private sealed record Harness(
        LogisticsDbContext Db,
        ConsignmentRepository Consignments,
        DeliveryRepository Deliveries);

    private static Harness NewHarness()
    {
        var (db, tenant, _) = LogisticsTestDb.New();
        var numbers = new DocumentNumberGenerator(db, tenant);

        return new Harness(db,
            new ConsignmentRepository(db, numbers),
            new DeliveryRepository(db, numbers, new AddressNormalizer(new FakeCityLookup())));
    }

    private static async Task<Carrier> SeedCarrier(
        Harness h,
        string? integrationMode = null,
        string? template = "https://track.example.test/{tracking}")
    {
        var carrier = TestData.Carrier(code: $"C{Guid.NewGuid():N}"[..8]);
        carrier.IntegrationMode     = integrationMode;
        carrier.TrackingUrlTemplate = template;

        h.Db.Carriers.Add(carrier);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
        return carrier;
    }

    private static async Task<Guid> NewConsignment(Harness h, Guid? carrierUuid) =>
        await h.Consignments.CreateAsync(new CreateConsignmentRequest { CarrierUuid = carrierUuid }, User);

    // TC-15.1 — the migration's backfill of existing carriers is asserted in MigrationTests,
    // where the migration-inspection helpers already live.

    // ── TC-15.4 — null and nonsense both mean manual ────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SOMETHING_ELSE")]
    public void An_unset_or_unrecognised_integration_mode_reads_as_manual(string? mode)
    {
        // "We do not know how to talk to this carrier" must resolve to "a person does it",
        // never to "call an adapter that may not exist".
        var carrier = TestData.Carrier();
        carrier.IntegrationMode = mode;

        ConsignmentRepository.IntegrationModeOf(carrier).Should().Be(CarrierIntegrationMode.Manual);
    }

    [Fact]
    public void A_consignment_with_no_carrier_at_all_reads_as_manual() =>
        ConsignmentRepository.IntegrationModeOf(null).Should().Be(CarrierIntegrationMode.Manual);

    // ── TC-15.2 — the existing tracking template still works ────────────────

    [Fact]
    public async Task The_tracking_url_is_built_from_the_carriers_existing_template()
    {
        // The {tracking} placeholder is what the carrier rows already contain and what the
        // legacy ShipmentRepository already substitutes. Changing it would silently break every
        // configured carrier.
        var h       = NewHarness();
        var carrier = await SeedCarrier(h, "MANUAL");
        var uuid    = await NewConsignment(h, carrier.UUID);

        await h.Consignments.BookManuallyAsync(uuid, new ManualBookingRequest { Awb = "TCS123456" }, User);

        var detail = await h.Consignments.GetByUuidAsync(uuid);
        detail!.TrackingUrl.Should().Be("https://track.example.test/TCS123456");
    }

    [Theory]
    [InlineData(null, "AWB1")]
    [InlineData("https://track.example.test/{tracking}", null)]
    public void A_tracking_url_needs_both_a_template_and_a_number(string? template, string? awb)
    {
        var carrier = TestData.Carrier();
        carrier.TrackingUrlTemplate = template;

        ConsignmentRepository.BuildTrackingUrl(carrier, awb).Should().BeNull();
    }

    [Fact]
    public async Task A_carrier_with_no_template_simply_has_no_tracking_url()
    {
        var h       = NewHarness();
        var carrier = await SeedCarrier(h, "MANUAL", template: null);
        var uuid    = await NewConsignment(h, carrier.UUID);

        await h.Consignments.BookManuallyAsync(uuid, new ManualBookingRequest { Awb = "X1" }, User);

        var detail = await h.Consignments.GetByUuidAsync(uuid);
        detail!.TrackingUrl.Should().BeNull();
        detail.MasterAwb.Should().Be("X1", "the AWB is still recorded — only the link is missing");
    }

    // ── TC-15.3 — a manual booking needs the number ─────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_manual_booking_without_an_airway_bill_is_rejected(string? awb)
    {
        // Booking with no number leaves a consignment that claims to be with a carrier but has
        // nothing to track it by and nothing to prove it.
        var h       = NewHarness();
        var carrier = await SeedCarrier(h, "MANUAL");
        var uuid    = await NewConsignment(h, carrier.UUID);

        var act = async () => await h.Consignments.BookManuallyAsync(
            uuid, new ManualBookingRequest { Awb = awb! }, User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*airway bill*");

        var detail = await h.Consignments.GetByUuidAsync(uuid);
        detail!.Status.Should().Be("DRAFT", "a refused booking leaves the consignment alone");
    }

    [Fact]
    public async Task A_manual_booking_records_the_number_and_marks_it_booked()
    {
        var h       = NewHarness();
        var carrier = await SeedCarrier(h, "MANUAL");
        var uuid    = await NewConsignment(h, carrier.UUID);

        (await h.Consignments.BookManuallyAsync(
            uuid, new ManualBookingRequest { Awb = "  TCS-99  ", CarrierReference = "REF-7" }, User))
            .Should().BeTrue();

        var detail = await h.Consignments.GetByUuidAsync(uuid);
        detail!.Status.Should().Be("BOOKED");
        detail.MasterAwb.Should().Be("TCS-99", "the number is trimmed");
        detail.CarrierReference.Should().Be("REF-7");
        detail.IntegrationMode.Should().Be("MANUAL");
    }

    [Fact]
    public async Task A_consignment_with_no_carrier_cannot_be_booked()
    {
        var h    = NewHarness();
        var uuid = await NewConsignment(h, carrierUuid: null);

        var act = async () => await h.Consignments.BookManuallyAsync(
            uuid, new ManualBookingRequest { Awb = "X1" }, User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*no carrier*");
    }

    [Theory]
    [InlineData("API")]
    [InlineData("FILE")]
    public async Task A_carrier_with_an_integration_must_not_be_booked_by_hand(string mode)
    {
        // Hand-booking an integrated carrier bypasses the idempotency ledger, which is the only
        // thing standing between a retry and a second real parcel.
        var h       = NewHarness();
        var carrier = await SeedCarrier(h, mode);
        var uuid    = await NewConsignment(h, carrier.UUID);

        var act = async () => await h.Consignments.BookManuallyAsync(
            uuid, new ManualBookingRequest { Awb = "X1" }, User);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage($"*{mode}*")
            .WithMessage("*idempotent*");
    }

    [Fact]
    public async Task A_consignment_already_booked_cannot_be_booked_again()
    {
        var h       = NewHarness();
        var carrier = await SeedCarrier(h, "MANUAL");
        var uuid    = await NewConsignment(h, carrier.UUID);

        await h.Consignments.BookManuallyAsync(uuid, new ManualBookingRequest { Awb = "X1" }, User);

        var act = async () => await h.Consignments.BookManuallyAsync(
            uuid, new ManualBookingRequest { Awb = "X2" }, User);

        await act.Should().ThrowAsync<ConflictException>();

        (await h.Consignments.GetByUuidAsync(uuid))!.MasterAwb
            .Should().Be("X1", "the first number stands");
    }

    // ── The manual path exists in the state machine ─────────────────────────

    [Fact]
    public void A_manual_booking_goes_straight_from_draft_to_booked()
    {
        // There is no carrier call to guard, so routing it through BOOKING would claim a request
        // happened that never did — and leave the idempotency ledger holding a key for nothing.
        ShipmentStateMachine.Instance
            .CanTransition(ShipmentStatus.Draft, ShipmentStatus.Booked).Should().BeTrue();

        ShipmentStateMachine.Instance
            .From(ShipmentStatus.Booking).Should().NotContain(ShipmentStatus.Draft);
    }

    // ── Consignments carry deliveries ───────────────────────────────────────

    [Fact]
    public async Task A_consignment_can_be_created_with_several_deliveries_on_it()
    {
        var h       = NewHarness();
        var carrier = await SeedCarrier(h, "MANUAL");

        var deliveries = new List<Guid>();
        for (var i = 0; i < 3; i++)
            deliveries.Add(await h.Deliveries.CreateAsync(new CreateDeliveryRequest
            {
                SourceType = "MANUAL", Direction = "OUTBOUND",
                Lines = [new CreateDeliveryLineRequest
                {
                    ItemDescription = $"Item {i}", QtyOrdered = 5m
                }]
            }, User));

        var uuid = await h.Consignments.CreateAsync(new CreateConsignmentRequest
        {
            CarrierUuid = carrier.UUID, DeliveryUuids = deliveries
        }, User);

        var detail = await h.Consignments.GetByUuidAsync(uuid);

        detail!.ConsignmentNumber.Should().MatchRegex(@"^SHP-\d{4}-\d{5}$");
        detail.Deliveries.Should().HaveCount(3);
        detail.Deliveries.Select(d => d.Sequence).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task The_same_delivery_cannot_be_loaded_onto_one_consignment_twice()
    {
        var h        = NewHarness();
        var carrier  = await SeedCarrier(h, "MANUAL");
        var delivery = await h.Deliveries.CreateAsync(new CreateDeliveryRequest
        {
            SourceType = "MANUAL", Direction = "OUTBOUND",
            Lines = [new CreateDeliveryLineRequest { ItemDescription = "Item", QtyOrdered = 1m }]
        }, User);

        var uuid = await h.Consignments.CreateAsync(new CreateConsignmentRequest
        {
            CarrierUuid = carrier.UUID, DeliveryUuids = [delivery]
        }, User);

        var act = async () => await h.Consignments.AttachDeliveryAsync(uuid, delivery, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*double the load*");
    }

    [Fact]
    public async Task An_unknown_consignment_or_carrier_is_reported_honestly()
    {
        var h = NewHarness();

        (await h.Consignments.GetByUuidAsync(Guid.NewGuid())).Should().BeNull();
        (await h.Consignments.BookManuallyAsync(
            Guid.NewGuid(), new ManualBookingRequest { Awb = "X" }, User)).Should().BeFalse();

        var act = async () => await h.Consignments.CreateAsync(
            new CreateConsignmentRequest { CarrierUuid = Guid.NewGuid() }, User);
        await act.Should().ThrowAsync<NotFoundException>();
    }
}

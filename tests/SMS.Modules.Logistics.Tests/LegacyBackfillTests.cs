using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Logistics.Tests;

// T-16 — migrating the legacy logistics.shipments rows into the four-layer model.
public class LegacyBackfillTests
{
    private static LegacyShipmentBackfillService Backfill(LogisticsDbContext db, StaticTenantContext tenant) =>
        new(db, new DocumentNumberGenerator(db, tenant),
            NullLogger<LegacyShipmentBackfillService>.Instance);

    private static Shipment Legacy(
        Guid organizationId,
        string number = "SHP-2026-00001",
        string status = "Preparing",
        string address = "Plot 12, Korangi Industrial Area, Karachi",
        DateTime? created = null)
    {
        var shipment = TestData.Shipment(number, organizationId: organizationId);
        shipment.Status             = status;
        shipment.DestinationAddress = address;
        shipment.CreatedDate        = created ?? new DateTime(2026, 6, 1);
        shipment.TrackingNumber     = "TCS-778899";
        return shipment;
    }

    // ── TC-16.1 / TC-16.2 ────────────────────────────────────────────────────

    [Fact]
    public async Task Each_legacy_shipment_becomes_a_delivery_a_consignment_and_the_link_between_them()
    {
        var (db, tenant, _) = LogisticsTestDb.New();

        for (var i = 1; i <= 3; i++)
            db.Shipments.Add(Legacy(tenant.OrganizationId, $"SHP-2026-0000{i}"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await Backfill(db, tenant).BackfillAsync();

        result.ShipmentsRead.Should().Be(3);
        result.Migrated.Should().Be(3);

        (await db.DeliveryOrders.CountAsync()).Should().Be(3);
        (await db.Consignments.CountAsync()).Should().Be(3);
        (await db.ConsignmentDeliveries.CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task A_backfilled_delivery_is_flagged_as_having_no_known_lines()
    {
        // The legacy table recorded a weight and a PO number and never what was in the box. A
        // header-only delivery must not be indistinguishable from a complete one.
        var (db, tenant, _) = LogisticsTestDb.New();
        db.Shipments.Add(Legacy(tenant.OrganizationId));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Backfill(db, tenant).BackfillAsync();

        var delivery = await db.DeliveryOrders.Include(d => d.Lines).SingleAsync();
        delivery.LinesUnknown.Should().BeTrue();
        delivery.Lines.Should().BeEmpty();
        delivery.Direction.Should().Be("INBOUND");
        delivery.SourceType.Should().Be("PO");
        delivery.SourceNumber.Should().Be("PO-2026-00001");
    }

    [Fact]
    public async Task The_cockpit_can_see_that_a_backfilled_delivery_is_incomplete()
    {
        var (db, tenant, _) = LogisticsTestDb.New();
        db.Shipments.Add(Legacy(tenant.OrganizationId));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Backfill(db, tenant).BackfillAsync();

        var repo = new DeliveryRepository(db, new DocumentNumberGenerator(db, tenant),
            new AddressNormalizer(new FakeCityLookup()));

        var row = (await repo.GetListAsync(new DeliveryFilter())).Data.Single();
        row.LinesUnknown.Should().BeTrue();
        row.LineCount.Should().Be(0);
    }

    [Fact]
    public async Task The_legacy_shipment_number_is_carried_across_rather_than_reissued()
    {
        // People recognise these numbers; reissuing them would orphan every reference on paper.
        var (db, tenant, _) = LogisticsTestDb.New();
        db.Shipments.Add(Legacy(tenant.OrganizationId, "SHP-2026-00042"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Backfill(db, tenant).BackfillAsync();

        (await db.Consignments.SingleAsync()).ConsignmentNumber.Should().Be("SHP-2026-00042");
    }

    // ── The counter has to learn about the numbers it inherited ─────────────

    [Fact]
    public async Task The_next_new_consignment_does_not_collide_with_a_migrated_number()
    {
        // Carrying legacy numbers across is only safe if the counter is advanced past them.
        // Otherwise the very next consignment is issued SHP-2026-00001, which a legacy row
        // already holds, and it fails on the unique index.
        var (db, tenant, _) = LogisticsTestDb.New();
        db.Shipments.Add(Legacy(tenant.OrganizationId, "SHP-2026-00007"));
        db.Shipments.Add(Legacy(tenant.OrganizationId, "SHP-2026-00042"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Backfill(db, tenant).BackfillAsync();

        var consignments = new ConsignmentRepository(db, new DocumentNumberGenerator(db, tenant));
        var uuid = await consignments.CreateAsync(new CreateConsignmentRequest(), 1);

        var issued = (await consignments.GetByUuidAsync(uuid))!.ConsignmentNumber;

        issued.Should().Be("SHP-2026-00043", "the counter resumes after the highest inherited number");

        var numbers = await db.Consignments.Select(c => c.ConsignmentNumber).ToListAsync();
        numbers.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Each_organizations_counter_is_advanced_independently()
    {
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();

        var (db, tenant, dbName) = LogisticsTestDb.New(orgA, isSuperAdmin: true);

        db.Shipments.Add(Legacy(orgA, "SHP-2026-00090"));
        db.Shipments.Add(Legacy(orgB, "SHP-2026-00005"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Backfill(db, tenant).BackfillAsync();

        var counters = await db.DocumentNumberSequences.IgnoreQueryFilters()
            .Where(s => s.Prefix == "SHP")
            .ToDictionaryAsync(s => s.OrganizationId, s => s.NextValue);

        counters[orgA].Should().Be(91);
        counters[orgB].Should().Be(6, "one organization's legacy numbering must not move another's");
    }

    [Fact]
    public async Task Delivery_numbers_are_issued_in_the_year_the_shipment_was_created()
    {
        // So migrated documents sit in chronological order instead of all landing in the year
        // the migration happened to run.
        var (db, tenant, _) = LogisticsTestDb.New();
        db.Shipments.Add(Legacy(tenant.OrganizationId, created: new DateTime(2024, 3, 1)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Backfill(db, tenant).BackfillAsync();

        (await db.DeliveryOrders.SingleAsync()).DeliveryNumber.Should().StartWith("DLV-2024-");
    }

    // ── TC-16.3 — idempotency ────────────────────────────────────────────────

    [Fact]
    public async Task Running_the_backfill_again_migrates_nothing_further()
    {
        // It runs on every application start, so a second pass has to be a no-op.
        var (db, tenant, _) = LogisticsTestDb.New();
        db.Shipments.Add(Legacy(tenant.OrganizationId, "SHP-2026-00001"));
        db.Shipments.Add(Legacy(tenant.OrganizationId, "SHP-2026-00002"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var service = Backfill(db, tenant);

        (await service.BackfillAsync()).Migrated.Should().Be(2);

        var second = await service.BackfillAsync();
        second.Migrated.Should().Be(0);
        second.AlreadyPresent.Should().Be(2);

        (await db.DeliveryOrders.CountAsync()).Should().Be(2);
        (await db.Consignments.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task A_shipment_added_after_the_first_run_is_picked_up_by_the_next()
    {
        var (db, tenant, _) = LogisticsTestDb.New();
        db.Shipments.Add(Legacy(tenant.OrganizationId, "SHP-2026-00001"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var service = Backfill(db, tenant);
        await service.BackfillAsync();

        db.Shipments.Add(Legacy(tenant.OrganizationId, "SHP-2026-00002"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        (await service.BackfillAsync()).Migrated.Should().Be(1);
        (await db.Consignments.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Nothing_happens_when_there_are_no_legacy_shipments()
    {
        var (db, tenant, _) = LogisticsTestDb.New();

        var result = await Backfill(db, tenant).BackfillAsync();

        result.Should().Be(new BackfillResult(0, 0, 0));
        result.DidWork.Should().BeFalse();
    }

    // ── TC-16.4 — the free-text address ──────────────────────────────────────

    [Fact]
    public async Task The_free_text_destination_becomes_an_address_that_is_flagged_for_review()
    {
        var (db, tenant, _) = LogisticsTestDb.New();
        db.Shipments.Add(Legacy(tenant.OrganizationId, address: "Plot 12, Korangi, Karachi"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Backfill(db, tenant).BackfillAsync();

        var address = await db.Addresses.SingleAsync();
        address.Line1.Should().Be("Plot 12, Korangi, Karachi");
        address.ValidationStatus.Should().Be("UNVALIDATED");
        address.ValidationNotes.Should().Contain("City and country");
        address.CityName.Should().Be("UNKNOWN",
            "guessing a city would put wrong data into a field courier booking will rely on");
    }

    [Fact]
    public async Task A_long_address_is_split_across_two_lines_rather_than_truncated()
    {
        // The legacy column allows 300 characters and Line1 allows 200. Losing the tail during a
        // migration would be silent and unrecoverable.
        var (db, tenant, _) = LogisticsTestDb.New();
        var raw = new string('A', 200) + new string('B', 100);

        db.Shipments.Add(Legacy(tenant.OrganizationId, address: raw));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Backfill(db, tenant).BackfillAsync();

        var address = await db.Addresses.SingleAsync();
        (address.Line1 + address.Line2).Should().Be(raw);
        address.Line1.Should().HaveLength(200);
        address.Line2.Should().HaveLength(100);
    }

    [Fact]
    public async Task A_shipment_with_no_destination_produces_no_address_rather_than_an_empty_one()
    {
        var (db, tenant, _) = LogisticsTestDb.New();
        db.Shipments.Add(Legacy(tenant.OrganizationId, address: "   "));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Backfill(db, tenant).BackfillAsync();

        (await db.Addresses.ToListAsync()).Should().BeEmpty();
        (await db.DeliveryOrders.SingleAsync()).ShipToAddressId.Should().BeNull();
    }

    // ── Status mapping ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("Preparing",  "DRAFT",      "DRAFT")]
    [InlineData("Dispatched", "IN_TRANSIT", "PICKED_UP")]
    [InlineData("In Transit", "IN_TRANSIT", "IN_TRANSIT")]
    [InlineData("Delivered",  "DELIVERED",  "DELIVERED")]
    [InlineData("Returned",   "CANCELLED",  "RETURNED_TO_ORIGIN")]
    public async Task Legacy_statuses_map_onto_states_that_can_be_honestly_asserted(
        string legacy, string expectedDelivery, string expectedConsignment)
    {
        var (db, tenant, _) = LogisticsTestDb.New();
        db.Shipments.Add(Legacy(tenant.OrganizationId, status: legacy));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Backfill(db, tenant).BackfillAsync();

        (await db.DeliveryOrders.SingleAsync()).Status.Should().Be(expectedDelivery);
        (await db.Consignments.SingleAsync()).Status.Should().Be(expectedConsignment);
    }

    [Fact]
    public async Task An_unrecognised_legacy_status_falls_back_to_draft()
    {
        var (db, tenant, _) = LogisticsTestDb.New();
        db.Shipments.Add(Legacy(tenant.OrganizationId, status: "Something Odd"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Backfill(db, tenant).BackfillAsync();

        (await db.DeliveryOrders.SingleAsync()).Status.Should().Be("DRAFT");
    }

    // ── The legacy table itself is untouched ────────────────────────────────

    [Fact]
    public async Task The_legacy_rows_are_left_exactly_as_they_were()
    {
        // SMS.Modules.Reports queries logistics.shipments directly and four Angular screens read
        // it. The backfill copies; it does not move.
        var (db, tenant, _) = LogisticsTestDb.New();
        db.Shipments.Add(Legacy(tenant.OrganizationId));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Backfill(db, tenant).BackfillAsync();

        var legacy = await db.Shipments.SingleAsync();
        legacy.Status.Should().Be("Preparing");
        legacy.ShipmentNumber.Should().Be("SHP-2026-00001");
        legacy.DestinationAddress.Should().Be("Plot 12, Korangi Industrial Area, Karachi");
    }

    [Fact]
    public async Task The_carrier_and_tracking_number_come_across()
    {
        var (db, tenant, _) = LogisticsTestDb.New();
        var carrier = TestData.Carrier();
        db.Carriers.Add(carrier);
        await db.SaveChangesAsync();

        var shipment = Legacy(tenant.OrganizationId);
        shipment.CarrierId   = carrier.Id;
        shipment.CarrierName = carrier.Name;
        db.Shipments.Add(shipment);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Backfill(db, tenant).BackfillAsync();

        var consignment = await db.Consignments.SingleAsync();
        consignment.CarrierId.Should().Be(carrier.Id);
        consignment.CarrierName.Should().Be(carrier.Name);
        consignment.MasterAwb.Should().Be("TCS-778899");
    }

    // ── Number parsing ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("SHP-2026-00042", 2026, 42)]
    [InlineData("SHP-1999-00001", 1999, 1)]
    public void A_well_formed_number_parses_into_its_year_and_sequence(string number, int year, int seq) =>
        LegacyShipmentBackfillService.ParseNumber(number).Should().Be((year, seq));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SHP-2026")]
    [InlineData("SHP/2026/00042")]
    [InlineData("SHP-YEAR-00042")]
    public void A_number_in_another_shape_is_ignored_rather_than_guessed_at(string? number) =>
        LegacyShipmentBackfillService.ParseNumber(number).Should().BeNull();
}

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

// A carrier cannot book a parcel it has no collection address for, and nothing in the app lets a person
// type one onto a delivery. What a delivery does have is the warehouse it leaves from — and a warehouse
// has an address. Twenty-three of twenty-four real deliveries had a warehouse and no address.
public class ConsignmentShipFromTests
{
    private const int User = 42;

    private static readonly DateTime T0 = new(2026, 9, 21, 9, 0, 0, DateTimeKind.Utc);

    private static readonly Guid Faisalabad = Guid.NewGuid();
    private static readonly Guid Lahore     = Guid.NewGuid();

    internal sealed class FakeWarehouses : IWarehouseDirectory
    {
        public Dictionary<Guid, WarehouseContact> ByUuid { get; } = [];
        public bool Throws  { get; set; }
        public int  Lookups { get; private set; }

        public Task<WarehouseContact?> FindAsync(Guid warehouseUuid, CancellationToken ct = default)
        {
            Lookups++;
            if (Throws) throw new InvalidOperationException("the inventory database is down");
            return Task.FromResult(ByUuid.GetValueOrDefault(warehouseUuid));
        }

        public FakeWarehouses With(
            Guid uuid, string? address = "Gulberg Road", string? city = "Faisalabad", string? country = "Pakistan")
        {
            ByUuid[uuid] = new WarehouseContact(
                uuid, "WH", "Faisalabad", address, city, country, "Asif", "987876765", true);
            return this;
        }
    }

    private sealed record Harness(
        LogisticsDbContext Db, FakeWarehouses Warehouses, ConsignmentShipFrom ShipFrom,
        StaticTenantContext Tenant);

    private static Harness NewHarness()
    {
        var (db, tenant, _) = LogisticsTestDb.New();
        var warehouses = new FakeWarehouses().With(Faisalabad);

        var shipFrom = new ConsignmentShipFrom(
            db, warehouses, new AddressNormalizer(new FakeCityLookup()), NullLogger<ConsignmentShipFrom>.Instance);

        return new Harness(db, warehouses, shipFrom, tenant);
    }

    private static async Task<Guid> NewDelivery(
        Harness h, Guid? warehouse = null, bool ownAddress = false)
    {
        var delivery = new DeliveryOrder
        {
            UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(),
            DeliveryNumber = $"DLV-2026-{Random.Shared.Next(1, 99_999):D5}",
            Direction = "OUTBOUND", SourceType = "SALE_ORDER", DeliveryMode = "SHIP",
            Status = "GOODS_ISSUED", ShipFromWarehouseUuid = warehouse,
            CreatedBy = User, CreatedDate = T0,
            ShipFromAddress = ownAddress
                ? new Address
                {
                    UUID = Guid.NewGuid(), Line1 = "Dock 4", CityName = "Karachi", CountryName = "Pakistan",
                    CreatedBy = User, CreatedDate = T0
                }
                : null
        };

        h.Db.DeliveryOrders.Add(delivery);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return delivery.UUID;
    }

    /// <summary>A consignment carrying the given deliveries, loaded tracked as the booking would have it.</summary>
    private static async Task<Consignment> NewConsignment(
        Harness h, IEnumerable<Guid> deliveries, bool ownAddress = false)
    {
        var consignment = new Consignment
        {
            UUID = Guid.NewGuid(), ConsignmentNumber = $"SHP-2026-{Random.Shared.Next(1, 99_999):D5}",
            Status = "DRAFT", CreatedBy = User, CreatedDate = T0,
            ShipFromAddress = ownAddress
                ? new Address
                {
                    UUID = Guid.NewGuid(), Line1 = "Typed by hand", CityName = "Multan", CountryName = "Pakistan",
                    CreatedBy = User, CreatedDate = T0
                }
                : null
        };

        var sequence = 1;
        foreach (var uuid in deliveries)
        {
            var order = await h.Db.DeliveryOrders.SingleAsync(d => d.UUID == uuid);
            consignment.Deliveries.Add(new ConsignmentDelivery
            {
                UUID = Guid.NewGuid(), DeliveryOrder = order, Sequence = sequence++, CreatedBy = User, CreatedDate = T0
            });
        }

        h.Db.Consignments.Add(consignment);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return await h.Db.Consignments.SingleAsync(c => c.UUID == consignment.UUID);
    }

    private static Task<int> AddressCount(Harness h) => h.Db.Addresses.CountAsync();

    // ── Taking the address from the warehouse ─────────────────────────────────

    [Fact]
    public async Task A_consignment_with_no_address_is_collected_from_its_deliveries_warehouse()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h, [await NewDelivery(h, Faisalabad)]);

        var ok = await h.ShipFrom.EnsureAsync(consignment, User);
        await h.Db.SaveChangesAsync();

        ok.Should().BeTrue();

        var stored = await h.Db.Consignments.AsNoTracking().Include(c => c.ShipFromAddress)
            .SingleAsync(c => c.UUID == consignment.UUID);

        var address = stored.ShipFromAddress!;
        address.Line1.Should().Be("Gulberg Road");
        address.CityName.Should().Be("Faisalabad");
        address.CountryName.Should().Be("Pakistan");
        address.ContactName.Should().Be("Asif");
        address.ContactPhone.Should().Be("987876765", "the phone is kept exactly as the warehouse holds it");
        address.AddressType.Should().Be("WAREHOUSE");
        address.ConsigneeUuid.Should().Be(Faisalabad, "the address says which warehouse it is");
        address.CreatedBy.Should().Be(User);
    }

    [Fact]
    public async Task The_country_code_a_carrier_books_against_is_worked_out_from_the_country_name()
    {
        // The warehouse master keeps "Pakistan" as free text and has no code to give.
        var h = NewHarness();
        var consignment = await NewConsignment(h, [await NewDelivery(h, Faisalabad)]);

        await h.ShipFrom.EnsureAsync(consignment, User);
        await h.Db.SaveChangesAsync();

        var stored = await h.Db.Consignments.AsNoTracking().Include(c => c.ShipFromAddress)
            .SingleAsync(c => c.UUID == consignment.UUID);

        stored.ShipFromAddress!.CountryIsoCode.Should().Be("PK");
    }

    [Fact]
    public async Task A_country_name_the_runtime_does_not_know_is_kept_with_no_code_rather_than_a_guess()
    {
        var h = NewHarness();
        h.Warehouses.With(Lahore, "Bay Road", "Johansbarg", "South Afriqa");
        var consignment = await NewConsignment(h, [await NewDelivery(h, Lahore)]);

        (await h.ShipFrom.EnsureAsync(consignment, User)).Should().BeTrue();
        await h.Db.SaveChangesAsync();

        var stored = await h.Db.Consignments.AsNoTracking().Include(c => c.ShipFromAddress)
            .SingleAsync(c => c.UUID == consignment.UUID);

        stored.ShipFromAddress!.CountryName.Should().Be("South Afriqa");
        stored.ShipFromAddress.CountryIsoCode.Should().BeNull();
        stored.ShipFromAddress.ValidationStatus.Should().Be("UNVALIDATED");
    }

    [Fact]
    public async Task The_address_is_a_snapshot_not_a_link_to_the_warehouse()
    {
        // The warehouse can be edited tomorrow; this consignment keeps where it was actually collected from.
        var h = NewHarness();
        var consignment = await NewConsignment(h, [await NewDelivery(h, Faisalabad)]);

        await h.ShipFrom.EnsureAsync(consignment, User);
        await h.Db.SaveChangesAsync();

        h.Warehouses.With(Faisalabad, address: "Somewhere else entirely");

        var stored = await h.Db.Consignments.AsNoTracking().Include(c => c.ShipFromAddress)
            .SingleAsync(c => c.UUID == consignment.UUID);

        stored.ShipFromAddress!.Line1.Should().Be("Gulberg Road");
    }

    [Fact]
    public async Task Asking_twice_builds_one_address()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h, [await NewDelivery(h, Faisalabad)]);

        await h.ShipFrom.EnsureAsync(consignment, User);
        await h.Db.SaveChangesAsync();
        var afterFirst = await AddressCount(h);

        (await h.ShipFrom.EnsureAsync(consignment, User)).Should().BeTrue();
        await h.Db.SaveChangesAsync();

        (await AddressCount(h)).Should().Be(afterFirst);
    }

    [Fact]
    public async Task Two_deliveries_from_the_same_warehouse_share_one_collection_address()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h, [await NewDelivery(h, Faisalabad), await NewDelivery(h, Faisalabad)]);

        (await h.ShipFrom.EnsureAsync(consignment, User)).Should().BeTrue();
        await h.Db.SaveChangesAsync();

        (await AddressCount(h)).Should().Be(1);
    }

    // ── What it leaves alone ──────────────────────────────────────────────────

    [Fact]
    public async Task An_address_somebody_gave_the_consignment_is_never_replaced()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h, [await NewDelivery(h, Faisalabad)], ownAddress: true);
        var before = await AddressCount(h);

        (await h.ShipFrom.EnsureAsync(consignment, User)).Should().BeTrue();
        await h.Db.SaveChangesAsync();

        (await AddressCount(h)).Should().Be(before);
        h.Warehouses.Lookups.Should().Be(0, "there was nothing to look up");
    }

    [Fact]
    public async Task A_delivery_with_its_own_ship_from_address_is_used_as_it_is()
    {
        // That delivery was said to leave from there, and the booking takes it from the delivery.
        var h = NewHarness();
        var consignment = await NewConsignment(h, [await NewDelivery(h, Faisalabad, ownAddress: true)]);
        var before = await AddressCount(h);

        (await h.ShipFrom.EnsureAsync(consignment, User)).Should().BeTrue();
        await h.Db.SaveChangesAsync();

        (await AddressCount(h)).Should().Be(before);
        consignment.ShipFromAddressId.Should().BeNull();
        h.Warehouses.Lookups.Should().Be(0);
    }

    // ── When it has to say no ─────────────────────────────────────────────────

    [Fact]
    public async Task A_delivery_that_names_no_warehouse_cannot_supply_one()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h, [await NewDelivery(h, warehouse: null)]);

        (await h.ShipFrom.EnsureAsync(consignment, User)).Should().BeFalse();
        consignment.ShipFromAddress.Should().BeNull();
    }

    [Fact]
    public async Task A_warehouse_that_does_not_exist_cannot_supply_one()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h, [await NewDelivery(h, Guid.NewGuid())]);

        (await h.ShipFrom.EnsureAsync(consignment, User)).Should().BeFalse();
        consignment.ShipFromAddress.Should().BeNull();
    }

    [Theory]
    [InlineData(null, "Faisalabad", "Pakistan")]
    [InlineData("   ", "Faisalabad", "Pakistan")]
    [InlineData("Gulberg Road", null, "Pakistan")]
    [InlineData("Gulberg Road", "Faisalabad", null)]
    public async Task A_warehouse_missing_a_part_of_its_address_cannot_supply_one(
        string? address, string? city, string? country)
    {
        // A carrier cannot collect from "Gulberg Road" with no city, and inventing one would send a van
        // to the wrong town. Better to say the warehouse is incomplete.
        var h = NewHarness();
        h.Warehouses.With(Lahore, address, city, country);
        var consignment = await NewConsignment(h, [await NewDelivery(h, Lahore)]);

        (await h.ShipFrom.EnsureAsync(consignment, User)).Should().BeFalse();
        consignment.ShipFromAddress.Should().BeNull();
    }

    [Fact]
    public async Task Deliveries_from_different_warehouses_have_no_single_collection_point()
    {
        var h = NewHarness();
        h.Warehouses.With(Lahore, "Ferozepur Road", "Lahore");
        var consignment = await NewConsignment(h, [await NewDelivery(h, Faisalabad), await NewDelivery(h, Lahore)]);

        (await h.ShipFrom.EnsureAsync(consignment, User)).Should().BeFalse();
        consignment.ShipFromAddress.Should().BeNull("picking the first would put a van at the wrong warehouse");
    }

    [Fact]
    public async Task A_failing_warehouse_lookup_is_answered_with_no_and_not_an_exception()
    {
        // Creating a consignment must not fail because Inventory is having a bad minute; the booking
        // will say what is missing.
        var h = NewHarness();
        h.Warehouses.Throws = true;
        var consignment = await NewConsignment(h, [await NewDelivery(h, Faisalabad)]);

        var act = async () => await h.ShipFrom.EnsureAsync(consignment, User);

        (await act.Should().NotThrowAsync()).Subject.Should().BeFalse();
        consignment.ShipFromAddress.Should().BeNull();
    }

    // ── Where it happens ──────────────────────────────────────────────────────

    [Fact]
    public async Task Creating_a_consignment_from_a_delivery_gives_it_the_warehouse_address()
    {
        var h = NewHarness();
        var delivery = await NewDelivery(h, Faisalabad);

        var repo = new ConsignmentRepository(h.Db, new DocumentNumberGenerator(h.Db, h.Tenant), h.ShipFrom);

        var uuid = await repo.CreateAsync(new CreateConsignmentRequest { DeliveryUuids = [delivery] }, User);

        var stored = await h.Db.Consignments.AsNoTracking().Include(c => c.ShipFromAddress)
            .SingleAsync(c => c.UUID == uuid);

        stored.ShipFromAddress.Should().NotBeNull(
            "rating and shipping rules read the consignment's own address, not just the booking");
        stored.ShipFromAddress!.CityName.Should().Be("Faisalabad");
    }

    [Fact]
    public async Task A_consignment_is_still_created_when_the_warehouse_cannot_supply_an_address()
    {
        var h = NewHarness();
        var delivery = await NewDelivery(h, Guid.NewGuid());

        var repo = new ConsignmentRepository(h.Db, new DocumentNumberGenerator(h.Db, h.Tenant), h.ShipFrom);

        var uuid = await repo.CreateAsync(new CreateConsignmentRequest { DeliveryUuids = [delivery] }, User);

        var stored = await h.Db.Consignments.AsNoTracking().SingleAsync(c => c.UUID == uuid);
        stored.ShipFromAddressId.Should().BeNull();
    }

    [Fact]
    public async Task Loading_a_delivery_onto_an_existing_consignment_fills_the_address_in_too()
    {
        var h = NewHarness();
        var first  = await NewDelivery(h, warehouse: null);
        var second = await NewDelivery(h, Faisalabad);
        var consignment = await NewConsignment(h, [first]);

        var repo = new ConsignmentRepository(h.Db, new DocumentNumberGenerator(h.Db, h.Tenant), h.ShipFrom);

        await repo.AttachDeliveryAsync(consignment.UUID, second, User);

        var stored = await h.Db.Consignments.AsNoTracking().Include(c => c.ShipFromAddress)
            .SingleAsync(c => c.UUID == consignment.UUID);

        stored.ShipFromAddress!.CityName.Should().Be("Faisalabad");
    }
}

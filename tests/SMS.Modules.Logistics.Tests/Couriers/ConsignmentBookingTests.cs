using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Logistics.Controllers;
using SMS.Modules.Logistics.Couriers;
using SMS.Modules.Logistics.Couriers.Booking;
using SMS.Modules.Logistics.Couriers.Labels;
using SMS.Modules.Logistics.Couriers.Manual;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Logistics.Tests.Couriers;

// T-37 — booking through an adapter: DRAFT → BOOKING → BOOKED, resolved by the ledger.
public class ConsignmentBookingTests
{
    private const int User = 42;

    private static readonly DateTime T0 = new(2026, 9, 17, 9, 0, 0, DateTimeKind.Utc);

    private sealed class RecordingScheduler : IConsignmentBookingScheduler
    {
        public List<Guid> Enqueued { get; } = [];
        public List<TimeSpan> Retries { get; } = [];

        public void Enqueue(Guid consignmentUuid, Guid organizationId) => Enqueued.Add(consignmentUuid);

        public void ScheduleRetry(Guid consignmentUuid, Guid organizationId, TimeSpan delay) => Retries.Add(delay);
    }

    private sealed class Harness
    {
        public required LogisticsDbContext         Db        { get; init; }
        public required StaticTenantContext        Tenant    { get; init; }
        public required string                     DbName    { get; init; }
        public required ScriptedCourierProvider    Carrier   { get; init; }
        public required CourierProviderRegistry    Registry  { get; init; }
        public required RecordingScheduler         Scheduler { get; init; }
        public required ConsignmentBookingService  Booking   { get; init; }
        public required CarrierAccountRepository   Accounts  { get; init; }
        public required CarrierCredentialVault     Vault     { get; init; }
        public required ConsignmentBookingSweepJob Sweep     { get; init; }
        public required IConfiguration             Config    { get; init; }

        public Guid Org => Tenant.OrganizationId;

        /// <summary>A second, independent service over the same database — another worker.</summary>
        public ConsignmentBookingService AnotherWorker(out LogisticsDbContext db)
        {
            db = LogisticsTestDb.Open(DbName, Tenant);
            return Build(db, Tenant, Registry, Scheduler, Config).booking;
        }
    }

    private static (ConsignmentBookingService booking, CarrierAccountRepository accounts, CarrierCredentialVault vault,
                    ConsignmentBookingSweepJob sweep) Build(
        LogisticsDbContext db, ITenantContext tenant, CourierProviderRegistry registry,
        IConsignmentBookingScheduler scheduler, IConfiguration config, IConsignmentShipFrom? shipFrom = null)
    {
        var vault    = new CarrierCredentialVault(db, TestEncryption.New());
        var resolver = new CarrierAccountResolver(db, registry, vault);
        var ledger   = new CarrierCommandLedger(db, tenant, registry);
        var labels   = new ConsignmentLabelService(db, resolver, config);

        return (new ConsignmentBookingService(db, resolver, ledger, registry, scheduler, labels, config,
                                              NullLogger<ConsignmentBookingService>.Instance, shipFrom),
                new CarrierAccountRepository(db, registry),
                vault,
                new ConsignmentBookingSweepJob(db, ledger, registry, scheduler, NullLogger<ConsignmentBookingSweepJob>.Instance));
    }

    private static Harness NewHarness(
        bool deduplicates = false, bool cod = true, bool multiPiece = true, int? callTimeoutSeconds = null,
        IWarehouseDirectory? warehouses = null)
    {
        var (db, tenant, dbName) = LogisticsTestDb.New();
        var carrier   = new ScriptedCourierProvider("SCRIPTED", deduplicates, cod, multiPiece);
        var registry  = new CourierProviderRegistry([new ManualCourierProvider(), carrier]);
        var scheduler = new RecordingScheduler();
        var config    = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Logistics:Booking:CarrierCallTimeoutSeconds"] = callTimeoutSeconds?.ToString()
        }).Build();

        IConsignmentShipFrom? shipFrom = warehouses is null
            ? null
            : new ConsignmentShipFrom(db, warehouses, new AddressNormalizer(new FakeCityLookup()),
                                      NullLogger<ConsignmentShipFrom>.Instance);

        var (booking, accounts, vault, sweep) = Build(db, tenant, registry, scheduler, config, shipFrom);

        return new Harness
        {
            Db = db, Tenant = tenant, DbName = dbName, Carrier = carrier, Registry = registry, Scheduler = scheduler,
            Booking = booking, Accounts = accounts, Vault = vault, Sweep = sweep, Config = config
        };
    }

    private sealed record Seeded(Guid Consignment, Guid Carrier, Guid? Account, int DeliveryId);

    /// <summary>A packed, addressed consignment on an API carrier with one default account.</summary>
    private static async Task<Seeded> Seed(
        Harness h, int packages = 1, string? integrationMode = "API", bool account = true,
        decimal? codAmount = null, string? trackingTemplate = null,
        bool shipFromAddress = true, Guid? shipFromWarehouse = null)
    {
        var carrier = new Carrier
        {
            UUID = Guid.NewGuid(), Name = "Scripted Express", Code = $"C{Guid.NewGuid():N}"[..6],
            IntegrationMode = integrationMode, ProviderKey = h.Carrier.Key, IsActive = true,
            TrackingUrlTemplate = trackingTemplate
        };
        h.Db.Carriers.Add(carrier);
        await h.Db.SaveChangesAsync();

        Guid? accountUuid = null;
        if (account)
        {
            accountUuid = await h.Accounts.CreateAsync(new CreateCarrierAccountRequest
            {
                CarrierUuid = carrier.UUID, AccountName = "Main", DefaultServiceCode = "EXPRESS"
            }, User);
            await h.Vault.SetAsync(accountUuid.Value, "ApiKey", "sk_live_abc123", null, null, User);
        }

        var delivery = new DeliveryOrder
        {
            UUID = Guid.NewGuid(), DeliveryNumber = $"DLV-2026-{Random.Shared.Next(1, 99_999):D5}",
            Direction  = LogisticsCode.Of(DeliveryDirection.Outbound),
            SourceType = LogisticsCode.Of(DeliverySourceType.Manual),
            SourceNumber    = "PO-2026-00042",
            ShipFromAddress = shipFromAddress ? Address("Plot 1, SITE", "Karachi") : null,
            ShipFromWarehouseUuid = shipFromWarehouse,
            ShipToAddress   = Address("Road 2, Gulberg", "Lahore"),
            CreatedBy = User, CreatedDate = T0
        };

        for (var i = 1; i <= packages; i++)
            delivery.Packages.Add(Package($"PKG-{i:D3}"));

        var consignment = new Consignment
        {
            UUID = Guid.NewGuid(), ConsignmentNumber = $"SHP-2026-{Random.Shared.Next(1, 99_999):D5}",
            CarrierId = carrier.Id, CarrierName = carrier.Name,
            CodAmount = codAmount, CodCurrency = codAmount is null ? null : "PKR",
            CreatedBy = User, CreatedDate = T0
        };
        consignment.Deliveries.Add(new ConsignmentDelivery
        {
            UUID = Guid.NewGuid(), DeliveryOrder = delivery, Sequence = 1, CreatedBy = User, CreatedDate = T0
        });

        h.Db.Consignments.Add(consignment);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return new Seeded(consignment.UUID, carrier.UUID, accountUuid, delivery.Id);
    }

    private static Address Address(string line1, string city) => new()
    {
        UUID = Guid.NewGuid(), Line1 = line1, CityName = city, CountryName = "Pakistan", CountryIsoCode = "PK",
        ContactName = "Receiving", ContactPhone = "+923001234567", CreatedBy = User, CreatedDate = T0
    };

    private static ShipmentPackage Package(string barcode) => new()
    {
        UUID = Guid.NewGuid(), PackageBarcode = barcode, GrossWeightKg = 5m, LengthCm = 40m, WidthCm = 30m,
        HeightCm = 20m, CreatedBy = User, CreatedDate = T0
    };

    private static async Task<ConsignmentBookingStatusModel> Request(Harness h, Guid uuid, BookConsignmentRequest? req = null)
    {
        var status = await h.Booking.RequestBookingAsync(uuid, req ?? new BookConsignmentRequest(), User);
        h.Db.ChangeTracker.Clear();
        return status!;
    }

    /// <summary>One job run, in a fresh unit of work as Hangfire would give it.</summary>
    private static async Task<BookingRunResult> Run(Harness h, Guid uuid, DateTime at)
    {
        h.Db.ChangeTracker.Clear();
        var result = await h.Booking.ExecuteAsync(uuid, h.Org, at);
        h.Db.ChangeTracker.Clear();
        return result;
    }

    private static Task<Consignment> Reload(Harness h, Guid uuid) =>
        h.Db.Consignments.AsNoTracking().IgnoreQueryFilters().SingleAsync(c => c.UUID == uuid);

    // ── Where the parcel is collected from ────────────────────────────────────
    //
    // Most deliveries name the warehouse they leave from and carry no address of their own, and nothing in
    // the app lets a person type one. Without this, such a delivery could never be booked.

    private static readonly Guid FaisalabadWarehouse = Guid.NewGuid();

    private static ConsignmentShipFromTests.FakeWarehouses Warehouses() =>
        new ConsignmentShipFromTests.FakeWarehouses().With(FaisalabadWarehouse);

    [Fact]
    public async Task A_delivery_that_only_names_its_warehouse_is_booked_from_that_warehouses_address()
    {
        var h = NewHarness(warehouses: Warehouses());
        var s = await Seed(h, shipFromAddress: false, shipFromWarehouse: FaisalabadWarehouse);

        var status = await Request(h, s.Consignment);
        status.Status.Should().Be("BOOKING");

        var c = await h.Db.Consignments.AsNoTracking().IgnoreQueryFilters()
            .Include(x => x.ShipFromAddress).SingleAsync(x => x.UUID == s.Consignment);
        c.ShipFromAddress!.CityName.Should().Be("Faisalabad", "it is saved before the job runs, so the job sends what was checked");

        await Run(h, s.Consignment, T0.AddSeconds(5));

        var sent = h.Carrier.Calls.Should().ContainSingle().Subject;
        sent.ShipFrom.Line1.Should().Be("Gulberg Road");
        sent.ShipFrom.City.Should().Be("Faisalabad");
        sent.ShipFrom.CountryIsoCode.Should().Be("PK", "the normaliser resolves the country from its name");
    }

    [Fact]
    public async Task A_delivery_with_no_address_and_no_usable_warehouse_is_refused_with_what_to_fix()
    {
        var h = NewHarness(warehouses: Warehouses());
        var s = await Seed(h, shipFromAddress: false, shipFromWarehouse: Guid.NewGuid());

        var act = async () => await h.Booking.RequestBookingAsync(s.Consignment, new BookConsignmentRequest(), User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*ship-from warehouse*Inventory*Warehouses*");

        h.Scheduler.Enqueued.Should().BeEmpty("nothing was booked");
        (await Reload(h, s.Consignment)).Status.Should().Be("DRAFT");
    }

    [Fact]
    public async Task A_delivery_that_already_has_its_own_ship_from_address_is_booked_exactly_as_before()
    {
        var h = NewHarness(warehouses: Warehouses());
        var s = await Seed(h, shipFromAddress: true, shipFromWarehouse: FaisalabadWarehouse);

        await Request(h, s.Consignment);
        await Run(h, s.Consignment, T0.AddSeconds(5));

        h.Carrier.Calls.Should().ContainSingle().Which.ShipFrom.City.Should().Be("Karachi");
    }

    // ── Requesting ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Requesting_a_booking_pins_what_will_be_sent_and_hands_the_call_to_the_background()
    {
        var h = NewHarness();
        var s = await Seed(h);

        var status = await Request(h, s.Consignment);

        status.Status.Should().Be("BOOKING");
        h.Carrier.Calls.Should().BeEmpty("the request never calls the carrier itself");
        h.Scheduler.Enqueued.Should().Equal(s.Consignment);

        var c = await Reload(h, s.Consignment);
        c.BookingIdempotencyKey.Should().StartWith(c.ConsignmentNumber + "-");
        c.CarrierServiceCode.Should().Be("EXPRESS", "the account's default service is pinned at request time");
        c.CarrierAccountId.Should().NotBeNull();
        status.CarrierAccountUuid.Should().Be(s.Account);
    }

    [Fact]
    public async Task Asking_again_while_a_booking_is_under_way_sends_nothing_twice()
    {
        var h = NewHarness();
        var s = await Seed(h);
        await Request(h, s.Consignment);
        var key = (await Reload(h, s.Consignment)).BookingIdempotencyKey;

        var again = await Request(h, s.Consignment);

        again.Status.Should().Be("BOOKING");
        h.Scheduler.Enqueued.Should().HaveCount(1);
        (await Reload(h, s.Consignment)).BookingIdempotencyKey.Should().Be(key);
    }

    [Fact]
    public async Task A_booked_consignment_cannot_be_booked_again()
    {
        var h = NewHarness();
        var s = await Seed(h);
        await Request(h, s.Consignment);
        await Run(h, s.Consignment, T0);

        await h.Booking.Invoking(b => b.RequestBookingAsync(s.Consignment, new BookConsignmentRequest(), User))
            .Should().ThrowAsync<ConflictException>().WithMessage("*already booked, airway bill AWB-1*");
    }

    [Theory]
    [InlineData("MANUAL", "manual booking")]
    [InlineData(null,     "manual booking")]
    [InlineData("FILE",   "FILE integration")]
    public async Task A_carrier_without_an_api_adapter_is_pointed_at_manual_booking(string? mode, string expected)
    {
        var h = NewHarness();
        var s = await Seed(h, integrationMode: mode);

        (await h.Booking.Invoking(b => b.RequestBookingAsync(s.Consignment, new BookConsignmentRequest(), User))
            .Should().ThrowAsync<ConflictException>()).WithMessage($"*{expected}*");

        (await Reload(h, s.Consignment)).Status.Should().Be("DRAFT");
        h.Scheduler.Enqueued.Should().BeEmpty();
    }

    [Fact]
    public async Task Nothing_packed_is_refused_before_anything_changes()
    {
        var h = NewHarness();
        var s = await Seed(h, packages: 0);

        (await h.Booking.Invoking(b => b.RequestBookingAsync(s.Consignment, new BookConsignmentRequest(), User))
            .Should().ThrowAsync<BadRequestException>()).WithMessage("*Nothing on this consignment has been packed*");

        var c = await Reload(h, s.Consignment);
        c.Status.Should().Be("DRAFT");
        c.BookingIdempotencyKey.Should().BeNull();
        h.Scheduler.Enqueued.Should().BeEmpty();
    }

    [Fact]
    public async Task Cash_on_delivery_through_an_account_that_does_not_collect_cash_is_refused_up_front()
    {
        var h = NewHarness(cod: false);
        var s = await Seed(h, codAmount: 12_500m);

        await h.Booking.Invoking(b => b.RequestBookingAsync(s.Consignment, new BookConsignmentRequest(), User))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*does not offer COD*");
    }

    [Fact]
    public async Task Several_pieces_on_a_single_piece_carrier_are_refused_up_front()
    {
        var h = NewHarness(multiPiece: false);
        var s = await Seed(h, packages: 2);

        await h.Booking.Invoking(b => b.RequestBookingAsync(s.Consignment, new BookConsignmentRequest(), User))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*one piece per consignment*2*");
    }

    [Fact]
    public async Task Deliveries_going_to_different_places_cannot_share_one_airway_bill()
    {
        var h = NewHarness();
        var s = await Seed(h);

        var other = new DeliveryOrder
        {
            UUID = Guid.NewGuid(), DeliveryNumber = "DLV-2026-99998",
            Direction = "OUTBOUND", SourceType = "MANUAL",
            ShipFromAddress = Address("Plot 1, SITE", "Karachi"),
            ShipToAddress   = Address("Blue Area", "Islamabad"),
            CreatedBy = User, CreatedDate = T0
        };
        other.Packages.Add(Package("PKG-900"));
        var consignment = await h.Db.Consignments.SingleAsync(c => c.UUID == s.Consignment);
        h.Db.ConsignmentDeliveries.Add(new ConsignmentDelivery
        {
            UUID = Guid.NewGuid(), Consignment = consignment, DeliveryOrder = other, Sequence = 2, CreatedBy = User, CreatedDate = T0
        });
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        await h.Booking.Invoking(b => b.RequestBookingAsync(s.Consignment, new BookConsignmentRequest(), User))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*2 different ship-to addresses*");
    }

    [Fact]
    public async Task An_api_carrier_with_no_account_is_refused_before_anything_changes()
    {
        var h = NewHarness();
        var s = await Seed(h, account: false);

        await h.Booking.Invoking(b => b.RequestBookingAsync(s.Consignment, new BookConsignmentRequest(), User))
            .Should().ThrowAsync<ConflictException>().WithMessage("*no active account*");
        (await Reload(h, s.Consignment)).Status.Should().Be("DRAFT");
    }

    [Fact]
    public async Task Another_organizations_consignment_does_not_exist_here()
    {
        var h = NewHarness();
        var s = await Seed(h);

        var otherDb = LogisticsTestDb.OpenAs(h.DbName, Guid.NewGuid());
        var other = Build(otherDb, new StaticTenantContext { OrganizationId = Guid.NewGuid() }, h.Registry, h.Scheduler, h.Config).booking;

        (await other.RequestBookingAsync(s.Consignment, new BookConsignmentRequest(), User)).Should().BeNull();
        (await h.Booking.ExecuteAsync(s.Consignment, Guid.NewGuid(), T0)).Should().Be(BookingRunResult.NotFound);
    }

    // ── The job ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_successful_run_books_the_consignment_and_a_second_run_sends_nothing()
    {
        var h = NewHarness();
        var s = await Seed(h);
        await Request(h, s.Consignment);

        (await Run(h, s.Consignment, T0)).Should().Be(BookingRunResult.Booked);

        var c = await Reload(h, s.Consignment);
        c.Status.Should().Be("BOOKED");
        c.MasterAwb.Should().Be("AWB-1");
        c.CarrierReference.Should().Be("REF-AWB-1");
        c.BookingFailureReason.Should().BeNull();

        (await Run(h, s.Consignment, T0.AddMinutes(1))).Should().Be(BookingRunResult.NotApplicable);
        h.Carrier.Calls.Should().HaveCount(1);
    }

    [Fact]
    public async Task The_carrier_is_sent_the_packed_consignment_under_its_own_key()
    {
        var h = NewHarness();
        var s = await Seed(h, packages: 2);

        // A voided carton and a carton inside a pallet must not be declared as pieces.
        var delivery = await h.Db.DeliveryOrders.Include(d => d.Packages).SingleAsync(d => d.Id == s.DeliveryId);
        var pallet = delivery.Packages.First();
        delivery.Packages.Add(new ShipmentPackage
        {
            UUID = Guid.NewGuid(), PackageBarcode = "PKG-INSIDE", ParentPackage = pallet, CreatedBy = User, CreatedDate = T0
        });
        delivery.Packages.Add(new ShipmentPackage
        {
            UUID = Guid.NewGuid(), PackageBarcode = "PKG-VOID", IsVoided = true, CreatedBy = User, CreatedDate = T0
        });
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        await Request(h, s.Consignment);
        await Run(h, s.Consignment, T0);

        var sent = h.Carrier.Calls.Single();
        var c = await Reload(h, s.Consignment);

        sent.IdempotencyKey.Should().Be(c.BookingIdempotencyKey);
        sent.ConsignmentNumber.Should().Be(c.ConsignmentNumber);
        sent.ServiceCode.Should().Be("EXPRESS");
        sent.Packages.Select(p => p.Barcode).Should().Equal("PKG-001", "PKG-002");
        sent.ShipTo.City.Should().Be("Lahore");
        sent.ShipFrom.City.Should().Be("Karachi");
        sent.ReferenceNumbers.Should().Contain("PO-2026-00042");
        sent.Credentials["ApiKey"].Should().Be("sk_live_abc123", "decrypted from the vault only for the call");
    }

    [Fact]
    public async Task A_refusal_fails_the_booking_retires_the_key_and_a_new_request_books_under_a_new_one()
    {
        var h = NewHarness();
        var s = await Seed(h);
        h.Carrier.ThenRefuses("Postcode not serviced").ThenBooks("AWB-OK");

        await Request(h, s.Consignment);
        var firstKey = (await Reload(h, s.Consignment)).BookingIdempotencyKey;

        (await Run(h, s.Consignment, T0)).Should().Be(BookingRunResult.Failed);

        var failed = await Reload(h, s.Consignment);
        failed.Status.Should().Be("BOOKING_FAILED");
        failed.BookingFailureReason.Should().Contain("Postcode not serviced");
        failed.BookingIdempotencyKey.Should().BeNull();

        await Request(h, s.Consignment);
        (await Reload(h, s.Consignment)).BookingIdempotencyKey.Should().NotBe(firstKey).And.NotBeNull();

        (await Run(h, s.Consignment, T0.AddMinutes(5))).Should().Be(BookingRunResult.Booked);
        (await Reload(h, s.Consignment)).MasterAwb.Should().Be("AWB-OK");
    }

    [Fact]
    public async Task An_unknown_outcome_on_a_carrier_that_does_not_deduplicate_waits_for_a_person()
    {
        var h = NewHarness(deduplicates: false);
        var s = await Seed(h);
        h.Carrier.ThenThrows(new HttpRequestException("Connection reset"));
        await Request(h, s.Consignment);

        (await Run(h, s.Consignment, T0)).Should().Be(BookingRunResult.NeedsResolution);

        var c = await Reload(h, s.Consignment);
        c.Status.Should().Be("BOOKING", "failing it would invite a second booking under a new key");
        c.BookingFailureReason.Should().Contain("does not deduplicate").And.Contain("resolve");
        h.Scheduler.Retries.Should().BeEmpty();

        var status = await h.Booking.GetStatusAsync(s.Consignment);
        status!.NeedsResolution.Should().BeTrue();
        status.WillRetryAutomatically.Should().BeFalse();
        status.CommandStatus.Should().Be("UNKNOWN");

        (await Run(h, s.Consignment, T0.AddHours(1))).Should().Be(BookingRunResult.NeedsResolution);
        h.Carrier.Calls.Should().HaveCount(1, "running the job again must not re-send it");
    }

    [Fact]
    public async Task An_unknown_outcome_on_a_deduplicating_carrier_is_retried_under_the_same_key()
    {
        var h = NewHarness(deduplicates: true);
        var s = await Seed(h);
        h.Carrier.ThenTimesOut().ThenBooks("AWB-7");
        await Request(h, s.Consignment);

        (await Run(h, s.Consignment, T0)).Should().Be(BookingRunResult.AwaitingRetry);
        h.Scheduler.Retries.Should().Equal(TimeSpan.FromMinutes(1));
        (await h.Booking.GetStatusAsync(s.Consignment))!.WillRetryAutomatically.Should().BeTrue();

        (await Run(h, s.Consignment, T0.AddMinutes(1))).Should().Be(BookingRunResult.Booked);

        h.Carrier.Calls.Should().HaveCount(2);
        h.Carrier.Calls.Select(c => c.IdempotencyKey).Distinct().Should().HaveCount(1,
            "the retry must present the same key, so the carrier returns the original booking");
        (await Reload(h, s.Consignment)).MasterAwb.Should().Be("AWB-7");
    }

    [Fact]
    public async Task Automatic_retries_back_off_and_stop_after_five_attempts()
    {
        var h = NewHarness(deduplicates: true);
        var s = await Seed(h);
        for (var i = 0; i < 6; i++) h.Carrier.ThenTimesOut();
        await Request(h, s.Consignment);

        var at = T0;
        for (var i = 0; i < 4; i++)
        {
            (await Run(h, s.Consignment, at)).Should().Be(BookingRunResult.AwaitingRetry);
            at += h.Scheduler.Retries.Last();
        }

        (await Run(h, s.Consignment, at)).Should().Be(BookingRunResult.NeedsResolution);
        (await Run(h, s.Consignment, at.AddHours(2))).Should().Be(BookingRunResult.NeedsResolution);

        h.Scheduler.Retries.Should().Equal(
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60));
        h.Carrier.Calls.Should().HaveCount(5);
        (await Reload(h, s.Consignment)).BookingFailureReason.Should().Contain("Check with the carrier");
    }

    [Fact]
    public async Task A_second_worker_arriving_while_the_call_is_out_does_not_call_the_carrier()
    {
        var h = NewHarness();
        var s = await Seed(h);
        BookingRunResult? second = null;

        h.Carrier.Then(async (_, _) =>
        {
            // Mid-call, a duplicate job starts in its own unit of work.
            var worker = h.AnotherWorker(out var db);
            await using (db) second = await worker.ExecuteAsync(s.Consignment, h.Org, T0.AddSeconds(5));
            return h.Carrier.Booked("AWB-1");
        });

        await Request(h, s.Consignment);
        (await Run(h, s.Consignment, T0)).Should().Be(BookingRunResult.Booked);

        second.Should().Be(BookingRunResult.InFlight);
        h.Carrier.Calls.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_carrier_call_that_hangs_is_abandoned_and_recorded_as_unknown()
    {
        var h = NewHarness(deduplicates: false, callTimeoutSeconds: 1);
        var s = await Seed(h);
        h.Carrier.Then(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });
        await Request(h, s.Consignment);

        (await Run(h, s.Consignment, T0)).Should().Be(BookingRunResult.NeedsResolution);

        var command = await h.Db.CarrierCommands.AsNoTracking().SingleAsync();
        command.Status.Should().Be("UNKNOWN");
        command.FailureDetail.Should().Contain("Canceled");
    }

    [Fact]
    public async Task A_run_for_a_consignment_that_is_not_booking_does_nothing()
    {
        var h = NewHarness();
        var s = await Seed(h);

        (await Run(h, s.Consignment, T0)).Should().Be(BookingRunResult.NotApplicable);
        h.Carrier.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Something_changing_before_any_call_was_sent_fails_the_booking_cleanly()
    {
        var h = NewHarness();
        var s = await Seed(h);
        await Request(h, s.Consignment);

        var package = await h.Db.ShipmentPackages.SingleAsync();
        package.IsVoided = true;
        await h.Db.SaveChangesAsync();

        (await Run(h, s.Consignment, T0)).Should().Be(BookingRunResult.Failed);
        (await Reload(h, s.Consignment)).BookingFailureReason.Should().Contain("Nothing on this consignment has been packed");
        h.Carrier.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Something_changing_after_an_unknown_call_does_not_fail_the_booking()
    {
        // The carrier may already hold a booking; failing here would invite a second one.
        var h = NewHarness(deduplicates: true);
        var s = await Seed(h);
        h.Carrier.ThenTimesOut();
        await Request(h, s.Consignment);
        await Run(h, s.Consignment, T0);

        var package = await h.Db.ShipmentPackages.SingleAsync();
        package.IsVoided = true;
        await h.Db.SaveChangesAsync();

        (await Run(h, s.Consignment, T0.AddMinutes(1))).Should().Be(BookingRunResult.NeedsResolution);

        var c = await Reload(h, s.Consignment);
        c.Status.Should().Be("BOOKING");
        c.BookingFailureReason.Should().Contain("earlier attempt's outcome is still unknown");
    }

    [Fact]
    public async Task An_answer_already_on_the_ledger_is_applied_without_calling_the_carrier_again()
    {
        // The worker recorded the carrier's answer and then died before saving the consignment.
        var h = NewHarness();
        var s = await Seed(h);
        await Request(h, s.Consignment);
        await Run(h, s.Consignment, T0);

        var consignment = await h.Db.Consignments.SingleAsync(c => c.UUID == s.Consignment);
        consignment.Status    = "BOOKING";
        consignment.MasterAwb = null;
        await h.Db.SaveChangesAsync();

        (await Run(h, s.Consignment, T0.AddMinutes(10))).Should().Be(BookingRunResult.Booked);

        (await Reload(h, s.Consignment)).MasterAwb.Should().Be("AWB-1");
        h.Carrier.Calls.Should().HaveCount(1);
    }

    // ── Resolution ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_person_confirming_the_carrier_booked_it_books_the_consignment()
    {
        var h = NewHarness();
        var s = await Seed(h);
        h.Carrier.ThenThrows(new TimeoutException());
        await Request(h, s.Consignment);
        await Run(h, s.Consignment, T0);

        var status = await h.Booking.ResolveAsync(s.Consignment,
            new ResolveBookingRequest { CarrierBooked = true, Awb = "AWB-PORTAL", Note = "Carrier portal shows it" }, User);

        status!.Status.Should().Be("BOOKED");
        status.MasterAwb.Should().Be("AWB-PORTAL");
        status.NeedsResolution.Should().BeFalse();

        var command = await h.Db.CarrierCommands.AsNoTracking().SingleAsync();
        command.ResolvedBy.Should().Be(User);
        command.ResolutionNote.Should().Be("Carrier portal shows it");
    }

    [Fact]
    public async Task A_person_confirming_it_was_never_booked_fails_it_so_it_can_be_booked_afresh()
    {
        var h = NewHarness();
        var s = await Seed(h);
        h.Carrier.ThenThrows(new TimeoutException()).ThenBooks("AWB-2");
        await Request(h, s.Consignment);
        await Run(h, s.Consignment, T0);

        var status = await h.Booking.ResolveAsync(s.Consignment,
            new ResolveBookingRequest { CarrierBooked = false, Note = "Rang them; no record" }, User);
        h.Db.ChangeTracker.Clear();

        status!.Status.Should().Be("BOOKING_FAILED");
        (await Reload(h, s.Consignment)).BookingIdempotencyKey.Should().BeNull();

        await Request(h, s.Consignment);
        (await Run(h, s.Consignment, T0.AddHours(1))).Should().Be(BookingRunResult.Booked);
        (await h.Db.CarrierCommands.CountAsync()).Should().Be(2, "a new command under a new key; the old one's history is kept");
    }

    [Fact]
    public async Task There_is_nothing_to_resolve_before_a_call_was_sent_or_once_it_is_booked()
    {
        var h = NewHarness();
        var s = await Seed(h);
        var resolve = new ResolveBookingRequest { CarrierBooked = false, Note = "n" };

        await h.Booking.Invoking(b => b.ResolveAsync(s.Consignment, resolve, User))
            .Should().ThrowAsync<ConflictException>().WithMessage("*not waiting on a booking*");

        await Request(h, s.Consignment);
        await h.Booking.Invoking(b => b.ResolveAsync(s.Consignment, resolve, User))
            .Should().ThrowAsync<ConflictException>().WithMessage("*No call has been sent*");
    }

    // ── Status ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_tracking_link_comes_from_the_carrier_template_and_otherwise_from_the_carrier_answer()
    {
        var withTemplate = NewHarness();
        var a = await Seed(withTemplate, trackingTemplate: "https://carrier.example/t/{tracking}");
        await Request(withTemplate, a.Consignment);
        await Run(withTemplate, a.Consignment, T0);

        (await withTemplate.Booking.GetStatusAsync(a.Consignment))!.TrackingUrl
            .Should().Be("https://carrier.example/t/AWB-1");

        var without = NewHarness();
        var b = await Seed(without);
        await Request(without, b.Consignment);
        await Run(without, b.Consignment, T0);

        (await without.Booking.GetStatusAsync(b.Consignment))!.TrackingUrl
            .Should().Be("https://scripted.invalid/track/AWB-1");
    }

    // ── The sweep ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_sweep_re_runs_only_bookings_that_stalled_and_can_safely_run()
    {
        var h = NewHarness(deduplicates: true);
        var neverRan       = await Seed(h);
        var answered       = await Seed(h);
        var retryDue       = await Seed(h);
        var retryNotDue    = await Seed(h);
        var freshlyQueued  = await Seed(h);

        foreach (var s in new[] { neverRan, answered, retryDue, retryNotDue, freshlyQueued })
            await Request(h, s.Consignment);

        // answered: the carrier answered, the consignment save was lost.
        await Run(h, answered.Consignment, T0);
        await Revert(answered.Consignment);

        // retryDue / retryNotDue: unknown, one attempt, last tried at different times.
        h.Carrier.ThenTimesOut().ThenTimesOut();
        await Run(h, retryDue.Consignment, T0);
        await Run(h, retryNotDue.Consignment, T0.AddMinutes(9) + TimeSpan.FromSeconds(30));

        var now = T0.AddMinutes(10);
        await Age(now, neverRan, answered, retryDue, retryNotDue);
        await SetModified(freshlyQueued.Consignment, now.AddSeconds(-30));
        h.Scheduler.Enqueued.Clear();

        var (_, requeued) = await h.Sweep.SweepAsync(now);

        requeued.Should().Be(3);
        h.Scheduler.Enqueued.Should().BeEquivalentTo([neverRan.Consignment, answered.Consignment, retryDue.Consignment]);

        async Task Revert(Guid uuid)
        {
            var c = await h.Db.Consignments.SingleAsync(x => x.UUID == uuid);
            c.Status = "BOOKING";
            await h.Db.SaveChangesAsync();
            h.Db.ChangeTracker.Clear();
        }

        async Task Age(DateTime at, params Seeded[] seeded)
        {
            foreach (var s in seeded) await SetModified(s.Consignment, at - TimeSpan.FromMinutes(3));
        }

        async Task SetModified(Guid uuid, DateTime at)
        {
            var c = await h.Db.Consignments.SingleAsync(x => x.UUID == uuid);
            c.ModifiedDate = at;
            await h.Db.SaveChangesAsync();
            h.Db.ChangeTracker.Clear();
        }
    }

    [Fact]
    public async Task The_sweep_leaves_alone_what_is_waiting_for_a_person()
    {
        var h = NewHarness(deduplicates: false);
        var s = await Seed(h);
        h.Carrier.ThenTimesOut();
        await Request(h, s.Consignment);
        await Run(h, s.Consignment, T0);

        var c = await h.Db.Consignments.SingleAsync();
        c.ModifiedDate = T0.AddMinutes(-30);
        await h.Db.SaveChangesAsync();
        h.Scheduler.Enqueued.Clear();

        (await h.Sweep.SweepAsync(T0.AddHours(3))).requeued.Should().Be(0);
    }

    [Fact]
    public async Task The_sweep_expires_the_claims_of_workers_that_went_quiet()
    {
        var h = NewHarness(deduplicates: true);
        var s = await Seed(h);
        await Request(h, s.Consignment);

        // A worker claims the call and dies before the carrier answers — so no result is ever recorded.
        var ledger = new CarrierCommandLedger(h.Db, h.Tenant, h.Registry);
        var key = (await Reload(h, s.Consignment)).BookingIdempotencyKey!;
        var c = await h.Db.Consignments.IgnoreQueryFilters().SingleAsync();
        await ledger.BeginAsync(new CarrierCommandClaim(CarrierCommandType.Book, key, new string('a', 64), c.Id, c.CarrierAccountId, h.Carrier.Key, User), h.Org, T0);
        c.ModifiedDate = T0;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var (expired, _) = await h.Sweep.SweepAsync(T0.AddMinutes(6));

        expired.Should().Be(1);
        (await h.Db.CarrierCommands.AsNoTracking().SingleAsync()).Status.Should().Be("UNKNOWN");
    }

    // ── The HTTP surface ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(nameof(ConsignmentsController.Book),           "Permission:SHIPMENT_BOOK")]
    [InlineData(nameof(ConsignmentsController.ResolveBooking), "Permission:SHIPMENT_BOOK")]
    [InlineData(nameof(ConsignmentsController.GetBooking),     "Permission:DELIVERY_VIEW")]
    public void Booking_and_confirming_a_booking_commit_money_and_need_the_booking_permission(string action, string policy)
    {
        var method = typeof(ConsignmentsController).GetMethod(action)!;

        method.GetCustomAttributes<HttpMethodAttribute>().Should().ContainSingle();
        method.GetCustomAttribute<RequirePermissionAttribute>()!.Policy.Should().Be(policy);
    }
}

using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Logistics.Controllers;
using SMS.Modules.Logistics.Couriers;
using SMS.Modules.Logistics.Couriers.Manual;
using SMS.Modules.Logistics.Couriers.Simulator;
using SMS.Modules.Logistics.Couriers.Tracking;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Logistics.Tests.Couriers;

// T-40 — the tracking poll and the stuck-shipment sweep.
public class TrackingPollTests
{
    private const int User = 42;
    private static readonly DateTime Now = new(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);

    // ── Policy: the numbers ───────────────────────────────────────────────────

    [Theory]
    [InlineData("OUT_FOR_DELIVERY",   30)]
    [InlineData("DELIVERY_ATTEMPTED", 60)]
    [InlineData("EXCEPTION",          60)]
    [InlineData("IN_TRANSIT",         120)]
    [InlineData("PICKED_UP",          120)]
    [InlineData("BOOKED",             240)]
    [InlineData("LABEL_READY",        240)]
    public void Closer_to_delivery_is_polled_more_often(string status, int minutes) =>
        TrackingPolicy.IntervalFor(LogisticsCode.Parse<ShipmentStatus>(status)).Should().Be(TimeSpan.FromMinutes(minutes));

    [Theory]
    [InlineData("DELIVERED")]
    [InlineData("CANCELLED")]
    [InlineData("RETURNED_TO_ORIGIN")]
    [InlineData("DRAFT")]
    [InlineData("BOOKING")]
    public void Finished_or_unbooked_consignments_are_never_polled(string status) =>
        TrackingPolicy.NextPollAt(LogisticsCode.Parse<ShipmentStatus>(status), 0, false, Now).Should().BeNull();

    [Fact]
    public void A_carrier_that_pushes_webhooks_is_only_polled_as_a_backstop()
    {
        TrackingPolicy.NextPollAt(ShipmentStatus.InTransit, 0, receivesWebhooks: true, Now).Should().Be(Now.AddHours(6));
        TrackingPolicy.NextPollAt(ShipmentStatus.InTransit, 0, receivesWebhooks: false, Now).Should().Be(Now.AddHours(2));
    }

    [Theory]
    [InlineData(1, 60)]
    [InlineData(2, 120)]
    [InlineData(3, 240)]
    [InlineData(6, 1440)]
    [InlineData(20, 1440)]
    public void Failures_back_off_exponentially_and_stop_at_a_day(int failures, int minutes) =>
        TrackingPolicy.NextPollAt(ShipmentStatus.OutForDelivery, failures, false, Now).Should().Be(Now.AddMinutes(minutes));

    [Theory]
    [InlineData("LABEL_READY",        72,  true,  "Not collected")]
    [InlineData("LABEL_READY",        71,  false, null)]
    [InlineData("IN_TRANSIT",         120, true,  "No carrier scan for 5 days")]
    [InlineData("IN_TRANSIT",         119, false, null)]
    [InlineData("OUT_FOR_DELIVERY",   48,  true,  "Out for delivery for 2 days")]
    [InlineData("DELIVERY_ATTEMPTED", 72,  true,  "not reattempted")]
    [InlineData("EXCEPTION",          48,  true,  "In exception for 2 days")]
    [InlineData("DELIVERED",          999, false, null)]
    [InlineData("CANCELLED",          999, false, null)]
    public void Silence_is_measured_against_what_the_status_allows(string status, int hoursQuiet, bool stuck, string? expected)
    {
        var reason = TrackingPolicy.StuckReason(LogisticsCode.Parse<ShipmentStatus>(status), Now.AddHours(-hoursQuiet), 0, null, Now);

        if (!stuck) reason.Should().BeNull();
        else reason.Should().Contain(expected!);
    }

    [Fact]
    public void Tracking_that_keeps_failing_is_stuck_however_recent_the_last_news()
    {
        TrackingPolicy.StuckReason(ShipmentStatus.InTransit, Now, 5, "HTTP 503", Now).Should().Contain("failed 5 times").And.Contain("HTTP 503");
        TrackingPolicy.StuckReason(ShipmentStatus.InTransit, Now, 4, "HTTP 503", Now).Should().BeNull();
    }

    // ── The simulator dates its scans stably ──────────────────────────────────

    [Fact]
    public async Task Given_a_booking_time_the_simulator_returns_the_same_timestamps_every_time()
    {
        var simulator = new SimulatorCourierProvider();
        var booking = await simulator.BookAsync(SimulatorBooking("key-stable", "SIM-DELIVERED"));
        var bookedAt = DateTime.UtcNow.AddHours(-20);

        var first  = await simulator.TrackAsync(new CourierTrackingRequest(booking.AwbNumber!, null, new Dictionary<string, string>(), bookedAt));
        await Task.Delay(1100);
        var second = await simulator.TrackAsync(new CourierTrackingRequest(booking.AwbNumber!, null, new Dictionary<string, string>(), bookedAt));

        first.Events.Select(e => e.OccurredAt).Should().Equal(second.Events.Select(e => e.OccurredAt),
            "timestamps that move between polls are stored as new events every poll");
    }

    [Fact]
    public async Task Given_a_booking_time_the_journey_unfolds_and_only_scans_already_made_are_returned()
    {
        var simulator = new SimulatorCourierProvider();
        var booking = await simulator.BookAsync(SimulatorBooking("key-unfold", "SIM-DELIVERED"));
        var awb = booking.AwbNumber!;
        var none = new Dictionary<string, string>();

        (await simulator.TrackAsync(new CourierTrackingRequest(awb, null, none, DateTime.UtcNow.AddMinutes(-10)))).Events
            .Should().BeEmpty("the first scan is an hour after booking");

        var bookedAt = DateTime.UtcNow.AddHours(-14);
        var partial = await simulator.TrackAsync(new CourierTrackingRequest(awb, null, none, bookedAt));
        partial.Events.Should().HaveCount(3, "scans at +1h, +7h and +13h have happened; +19h has not");
        partial.Events[0].OccurredAt.Should().Be(new DateTime(bookedAt.Ticks - bookedAt.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc).AddHours(1));

        var complete = await simulator.TrackAsync(new CourierTrackingRequest(awb, null, none, DateTime.UtcNow.AddDays(-5)));
        complete.Events[^1].Milestone.Should().Be("DELIVERED");
    }

    private static CourierBookingRequest SimulatorBooking(string key, string service) =>
        new(key, "SHP-2026-00001", service,
            new CourierAddress("A", null, null, "1", null, "Karachi", null, null, "PK"),
            new CourierAddress("B", null, null, "2", null, "Lahore", null, null, "PK"),
            [new CourierPackage("PKG-1", "BOX", null, null, null, 1m, null)],
            "PREPAID", null, null, null, null, [], new Dictionary<string, string>());

    // ── Harness ───────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        public required LogisticsDbContext        Db       { get; init; }
        public required StaticTenantContext       Tenant   { get; init; }
        public required string                    DbName   { get; init; }
        public required ScriptedCourierProvider   Carrier  { get; init; }
        public required ConsignmentTrackingPoller Poller   { get; init; }
        public required ConsignmentTrackingService Service { get; init; }
        public required CarrierAccountRepository  Accounts { get; init; }
    }

    private static Harness NewHarness(int? batchSize = null, int? timeoutSeconds = null)
    {
        // Recurring jobs run with no tenant, which the system treats as unfiltered.
        var (db, tenant, dbName) = LogisticsTestDb.New(isSuperAdmin: true);
        var carrier  = new ScriptedCourierProvider("SCRIPTED", tracking: true);
        var registry = new CourierProviderRegistry([new ManualCourierProvider(), new SimulatorCourierProvider(), carrier]);
        var resolver = new CarrierAccountResolver(db, registry, new CarrierCredentialVault(db, TestEncryption.New()));
        var config   = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Logistics:Tracking:PollBatchSize"]             = batchSize?.ToString(),
            ["Logistics:Tracking:CarrierCallTimeoutSeconds"] = timeoutSeconds?.ToString()
        }).Build();

        var poller = new ConsignmentTrackingPoller(db, resolver, new TrackingEventRecorder(db), config,
                                                   NullLogger<ConsignmentTrackingPoller>.Instance);

        return new Harness
        {
            Db = db, Tenant = tenant, DbName = dbName, Carrier = carrier, Poller = poller,
            Service = new ConsignmentTrackingService(db, poller), Accounts = new CarrierAccountRepository(db, registry)
        };
    }

    private static async Task<Guid> Seed(
        Harness h, string status = "IN_TRANSIT", string awb = "AWB-1", string? mode = "API", string providerKey = "SCRIPTED",
        DateTime? nextPollAt = null, bool? trackingEnabled = null, DateTime? modified = null, Guid? organizationId = null)
    {
        var carrier = new Carrier
        {
            UUID = Guid.NewGuid(), Name = "Scripted Express", Code = $"C{Guid.NewGuid():N}"[..6],
            IntegrationMode = mode, ProviderKey = providerKey, IsActive = true,
            OrganizationId = organizationId ?? h.Tenant.OrganizationId
        };
        h.Db.Carriers.Add(carrier);
        await h.Db.SaveChangesAsync();

        int? accountId = null;
        if (mode == "API")
        {
            var uuid = await h.Accounts.CreateAsync(new CreateCarrierAccountRequest
            {
                CarrierUuid = carrier.UUID, AccountName = "Main", TrackingEnabled = trackingEnabled
            }, User);
            accountId = await h.Db.CarrierAccounts.Where(a => a.UUID == uuid).Select(a => a.Id).SingleAsync();
        }

        var consignment = new Consignment
        {
            UUID = Guid.NewGuid(), ConsignmentNumber = $"SHP-2026-{Random.Shared.Next(1, 99_999):D5}",
            CarrierId = carrier.Id, CarrierName = carrier.Name, CarrierAccountId = accountId,
            Status = status, MasterAwb = awb, TrackingNextPollAt = nextPollAt,
            CreatedBy = User, CreatedDate = Now.AddDays(-1), ModifiedDate = modified ?? Now.AddHours(-1),
            OrganizationId = organizationId ?? h.Tenant.OrganizationId
        };
        h.Db.Consignments.Add(consignment);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return consignment.UUID;
    }

    private static Task<Consignment> Reload(Harness h, Guid uuid) =>
        h.Db.Consignments.AsNoTracking().IgnoreQueryFilters().SingleAsync(c => c.UUID == uuid);

    private static CourierTrackingEvent Scan(string milestone, DateTime at) => new(at, milestone, "S_" + milestone, "Scan", "Lahore");

    // ── The poll ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_due_consignment_is_polled_its_events_recorded_and_its_next_poll_scheduled_for_its_new_status()
    {
        var h = NewHarness();
        var uuid = await Seed(h, status: "IN_TRANSIT");
        h.Carrier.ThenTracks(Scan("OUT_FOR_DELIVERY", Now.AddMinutes(-20)));

        var result = await h.Poller.SweepAsync(Now);

        result.Polled.Should().Be(1);
        h.Carrier.TrackCalls.Single().AwbNumber.Should().Be("AWB-1");

        var c = await Reload(h, uuid);
        c.Status.Should().Be("OUT_FOR_DELIVERY");
        c.TrackingLastPolledAt.Should().Be(Now);
        c.TrackingNextPollAt.Should().Be(Now.AddMinutes(30), "out for delivery is polled every half hour");
        c.LastTrackingEventAt.Should().Be(Now.AddMinutes(-20));
        (await h.Db.ConsignmentTrackingEvents.AsNoTracking().SingleAsync()).Source.Should().Be("POLL");
    }

    [Fact]
    public async Task Only_live_booked_api_consignments_that_are_due_are_asked_about()
    {
        var h = NewHarness();
        var due        = await Seed(h, awb: "DUE");
        await Seed(h, awb: "NOT-YET", nextPollAt: Now.AddMinutes(5));
        await Seed(h, awb: "DELIVERED", status: "DELIVERED");
        await Seed(h, awb: "CANCELLED", status: "CANCELLED");
        await Seed(h, awb: "MANUAL", mode: "MANUAL", providerKey: ManualCourierProvider.ProviderKey);
        await Seed(h, awb: null!);

        await h.Poller.SweepAsync(Now);

        h.Carrier.TrackCalls.Select(c => c.AwbNumber).Should().Equal("DUE");
        (await Reload(h, due)).TrackingLastPolledAt.Should().Be(Now);
    }

    [Fact]
    public async Task A_backlog_drains_never_polled_first_then_longest_overdue_within_the_batch()
    {
        var h = NewHarness(batchSize: 2);
        await Seed(h, awb: "OVERDUE-1H", nextPollAt: Now.AddHours(-1));
        await Seed(h, awb: "OVERDUE-5H", nextPollAt: Now.AddHours(-5));
        await Seed(h, awb: "NEVER");

        await h.Poller.SweepAsync(Now);

        h.Carrier.TrackCalls.Select(c => c.AwbNumber).Should().Equal("NEVER", "OVERDUE-5H");
    }

    [Fact]
    public async Task Polling_an_unchanged_simulator_journey_again_stores_nothing_new()
    {
        // The defect this task found: without a stable booking time the simulator's scans moved on
        // every poll, and every poll stored the whole journey again.
        var h = NewHarness();
        var simulator = new SimulatorCourierProvider();
        var awb = (await simulator.BookAsync(SimulatorBooking("key-poll-twice", "SIM-DELIVERED"))).AwbNumber!;
        var uuid = await Seed(h, status: "LABEL_READY", awb: awb, providerKey: SimulatorCourierProvider.ProviderKey);

        var consignment = await h.Db.Consignments.SingleAsync();
        h.Db.CarrierCommands.Add(new CarrierCommand
        {
            UUID = Guid.NewGuid(), OrganizationId = h.Tenant.OrganizationId, CommandType = "BOOK", IdempotencyKey = "k",
            RequestFingerprint = new string('a', 64), ConsignmentId = consignment.Id, ProviderKey = "SIMULATOR",
            Status = "SUCCEEDED", AttemptCount = 1, FirstAttemptAt = DateTime.UtcNow.AddHours(-14),
            LastAttemptAt = DateTime.UtcNow.AddHours(-14), CompletedAt = DateTime.UtcNow.AddHours(-14),
            CreatedBy = User, CreatedDate = DateTime.UtcNow.AddHours(-14)
        });
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var first  = await h.Poller.PollAsync(consignment.Id, DateTime.UtcNow);
        h.Db.ChangeTracker.Clear();
        var second = await h.Poller.PollAsync(consignment.Id, DateTime.UtcNow.AddMinutes(1));

        first.Recorded.Should().Be(3);
        second.Recorded.Should().Be(0);
        (await h.Db.ConsignmentTrackingEvents.CountAsync()).Should().Be(3);
        (await Reload(h, uuid)).Status.Should().Be("IN_TRANSIT");
    }

    [Fact]
    public async Task A_failing_carrier_is_backed_off_and_a_success_resets_the_count()
    {
        var h = NewHarness();
        var uuid = await Seed(h, status: "IN_TRANSIT");
        h.Carrier.ThenTracks((_, _) => Task.FromException<CourierTrackingResult>(new HttpRequestException("503")))
                 .ThenTracks((_, _) => Task.FromResult(new CourierTrackingResult(CourierOutcome.Refused, "SCRIPTED", [], "AWB unknown")))
                 .ThenTracks(Scan("OUT_FOR_DELIVERY", Now));

        var c = await Reload(h, uuid);

        (await h.Poller.PollAsync(c.Id, Now)).Outcome.Should().Be(PollOutcome.Failed);
        c = await Reload(h, uuid);
        c.TrackingPollFailures.Should().Be(1);
        c.TrackingLastError.Should().Contain("did not answer").And.Contain("HttpRequestException");
        c.TrackingNextPollAt.Should().Be(Now.AddHours(4), "in transit is two hours, doubled once");

        h.Db.ChangeTracker.Clear();
        (await h.Poller.PollAsync(c.Id, Now.AddHours(4))).Outcome.Should().Be(PollOutcome.Failed);
        c = await Reload(h, uuid);
        c.TrackingPollFailures.Should().Be(2);
        c.TrackingLastError.Should().Be("AWB unknown");

        h.Db.ChangeTracker.Clear();
        (await h.Poller.PollAsync(c.Id, Now.AddHours(9))).Outcome.Should().Be(PollOutcome.Polled);
        c = await Reload(h, uuid);
        c.TrackingPollFailures.Should().Be(0);
        c.TrackingLastError.Should().BeNull();
    }

    [Fact]
    public async Task A_broken_carrier_configuration_is_a_failure_not_a_crash()
    {
        var h = NewHarness();
        var uuid = await Seed(h);
        var account = await h.Db.CarrierAccounts.SingleAsync();
        account.IsActive = false;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var result = await h.Poller.SweepAsync(Now);

        result.Failed.Should().Be(1);
        (await Reload(h, uuid)).TrackingLastError.Should().StartWith("Carrier configuration:");
        h.Carrier.TrackCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_account_with_tracking_switched_off_is_not_asked_and_is_rechecked_tomorrow()
    {
        var h = NewHarness();
        var uuid = await Seed(h, trackingEnabled: false);

        var c = await Reload(h, uuid);
        (await h.Poller.PollAsync(c.Id, Now)).Outcome.Should().Be(PollOutcome.TrackingUnavailable);

        h.Carrier.TrackCalls.Should().BeEmpty();
        c = await Reload(h, uuid);
        c.TrackingNextPollAt.Should().Be(Now.AddHours(24));
        c.TrackingPollFailures.Should().Be(0, "not being able to track is not a failure");
    }

    [Fact]
    public async Task One_consignments_failure_does_not_stop_the_others()
    {
        var h = NewHarness();
        await Seed(h, awb: "BAD", nextPollAt: Now.AddHours(-2));
        var good = await Seed(h, awb: "GOOD", nextPollAt: Now.AddHours(-1));
        h.Carrier.ThenTracks((_, _) => Task.FromException<CourierTrackingResult>(new TimeoutException()))
                 .ThenTracks(Scan("DELIVERED", Now.AddMinutes(-5)));

        var result = await h.Poller.SweepAsync(Now);

        result.Failed.Should().Be(1);
        result.Polled.Should().Be(1);
        (await Reload(h, good)).Status.Should().Be("DELIVERED");
    }

    [Fact]
    public async Task A_consignment_fed_by_webhooks_is_polled_only_as_a_backstop()
    {
        var h = NewHarness();
        var uuid = await Seed(h, status: "IN_TRANSIT");
        var c = await h.Db.Consignments.SingleAsync();
        h.Db.ConsignmentTrackingEvents.Add(new ConsignmentTrackingEvent
        {
            UUID = Guid.NewGuid(), OrganizationId = h.Tenant.OrganizationId, ConsignmentId = c.Id, Milestone = "IN_TRANSIT",
            OccurredAt = Now.AddHours(-3), ReceivedAt = Now.AddHours(-3), Source = "WEBHOOK", EventKey = new string('b', 64), CreatedDate = Now
        });
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        await h.Poller.PollAsync(c.Id, Now);

        (await Reload(h, uuid)).TrackingNextPollAt.Should().Be(Now.AddHours(6));
    }

    [Fact]
    public async Task A_carrier_call_that_hangs_is_abandoned_as_a_failure()
    {
        var h = NewHarness(timeoutSeconds: 1);
        var uuid = await Seed(h);
        h.Carrier.ThenTracks(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null!;
        });

        var c = await Reload(h, uuid);
        (await h.Poller.PollAsync(c.Id, Now)).Outcome.Should().Be(PollOutcome.Failed);
        (await Reload(h, uuid)).TrackingLastError.Should().Contain("Canceled");
    }

    // ── Stuck ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_quiet_consignment_is_flagged_stays_flagged_from_the_same_moment_and_clears_when_it_moves()
    {
        var h = NewHarness();
        var uuid = await Seed(h, status: "LABEL_READY", modified: Now.AddDays(-4), nextPollAt: Now.AddDays(1));

        (await h.Poller.EvaluateStuckAsync(Now)).nowStuck.Should().Be(1);
        var c = await Reload(h, uuid);
        c.StuckSince.Should().Be(Now);
        c.StuckReason.Should().Be("Not collected by the carrier 4 days after booking.");

        await h.Poller.EvaluateStuckAsync(Now.AddDays(1));
        c = await Reload(h, uuid);
        c.StuckSince.Should().Be(Now, "stuck since when it was first noticed, not since the latest check");
        c.StuckReason.Should().Contain("5 days");

        h.Carrier.ThenTracks(Scan("PICKED_UP", Now.AddDays(1).AddHours(-1)));
        await h.Poller.PollAsync(c.Id, Now.AddDays(1));
        h.Db.ChangeTracker.Clear();

        (await h.Poller.EvaluateStuckAsync(Now.AddDays(1))).noLongerStuck.Should().Be(1);
        c = await Reload(h, uuid);
        c.StuckSince.Should().BeNull();
        c.StuckReason.Should().BeNull();
    }

    [Fact]
    public async Task Manual_carriers_are_never_flagged_and_a_finished_consignment_is_cleared()
    {
        var h = NewHarness();
        var manual = await Seed(h, status: "BOOKED", mode: "MANUAL", providerKey: ManualCourierProvider.ProviderKey, modified: Now.AddDays(-30));
        var delivered = await Seed(h, status: "DELIVERED", modified: Now.AddDays(-30));

        var d = await h.Db.Consignments.SingleAsync(c => c.UUID == delivered);
        d.StuckSince = Now.AddDays(-10);
        d.StuckReason = "old";
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var (nowStuck, noLongerStuck) = await h.Poller.EvaluateStuckAsync(Now);

        nowStuck.Should().Be(0);
        noLongerStuck.Should().Be(1);
        (await Reload(h, manual)).StuckSince.Should().BeNull("with no tracking feed every manual consignment would look uncollected");
    }

    [Fact]
    public async Task The_stuck_list_is_one_organizations_longest_stuck_first()
    {
        var h = NewHarness();
        var older = await Seed(h, modified: Now.AddDays(-8));
        var newer = await Seed(h, modified: Now.AddHours(-132));   // 5.5 days: under 5 at the first check, over at the second
        await Seed(h, modified: Now.AddDays(-9), organizationId: Guid.NewGuid());

        await h.Poller.EvaluateStuckAsync(Now.AddDays(-1).AddHours(2));   // only `older` is quiet enough yet
        await h.Poller.EvaluateStuckAsync(Now);

        var scoped = new ConsignmentTrackingService(
            LogisticsTestDb.OpenAs(h.DbName, h.Tenant.OrganizationId), h.Poller);

        (await scoped.GetStuckAsync()).Select(s => s.ConsignmentUuid).Should().Equal(older, newer);
    }

    // ── Refresh now ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Refresh_asks_the_carrier_now_whatever_the_schedule()
    {
        var h = NewHarness();
        var uuid = await Seed(h, status: "IN_TRANSIT", nextPollAt: Now.AddHours(3));
        h.Carrier.ThenTracks(Scan("DELIVERED", Now.AddMinutes(-2)));

        var result = await h.Service.RefreshAsync(uuid, Now);

        result!.Polled.Should().BeTrue();
        result.Status.Should().Be("DELIVERED");
        result.NewEvents.Should().Be(1);
        result.NextPollAt.Should().BeNull("a delivered consignment is not polled again");
    }

    [Fact]
    public async Task Refresh_pressed_twice_within_a_minute_asks_the_carrier_once()
    {
        var h = NewHarness();
        var uuid = await Seed(h);

        await h.Service.RefreshAsync(uuid, Now);
        var again = await h.Service.RefreshAsync(uuid, Now.AddSeconds(30));

        again!.Polled.Should().BeFalse();
        h.Carrier.TrackCalls.Should().HaveCount(1);
    }

    [Theory]
    [InlineData("not booked",  "not booked")]
    [InlineData("manual",      "no API integration")]
    [InlineData("delivered",   "nothing more to track")]
    public async Task Refresh_refuses_what_cannot_be_tracked(string situation, string expected)
    {
        var h = NewHarness();
        var uuid = situation switch
        {
            "not booked" => await Seed(h, awb: null!, status: "DRAFT"),
            "manual"     => await Seed(h, mode: "MANUAL", providerKey: ManualCourierProvider.ProviderKey),
            _            => await Seed(h, status: "DELIVERED")
        };

        await h.Service.Invoking(s => s.RefreshAsync(uuid, Now))
            .Should().ThrowAsync<ConflictException>().WithMessage($"*{expected}*");
    }

    [Fact]
    public async Task Refresh_cannot_reach_another_organizations_consignment()
    {
        var h = NewHarness();
        var uuid = await Seed(h);

        var other = new ConsignmentTrackingService(LogisticsTestDb.OpenAs(h.DbName, Guid.NewGuid()), h.Poller);

        (await other.RefreshAsync(uuid, Now)).Should().BeNull();
        h.Carrier.TrackCalls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(nameof(ConsignmentsController.GetStuck),        "Permission:DELIVERY_VIEW")]
    [InlineData(nameof(ConsignmentsController.RefreshTracking), "Permission:DELIVERY_EDIT")]
    [InlineData(nameof(ConsignmentsController.GetTracking),     "Permission:DELIVERY_VIEW")]
    public void Reading_tracking_needs_view_and_asking_the_carrier_needs_edit(string action, string policy) =>
        typeof(ConsignmentsController).GetMethod(action)!.GetCustomAttribute<RequirePermissionAttribute>()!.Policy.Should().Be(policy);
}

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Logistics.Tests.Couriers;

// T-39 — tracking events, and the webhooks that deliver them.
public class TrackingAndWebhookTests
{
    private const int    User   = 42;
    private const string Secret = "whsec_test_0123456789";
    private const string Awb    = "SIM-A0123456789";

    private static readonly DateTime T0 = new(2026, 9, 17, 9, 0, 0, DateTimeKind.Utc);

    // ── Harness ───────────────────────────────────────────────────────────────

    private sealed class FakeTenants : ITenantSnapshotProvider
    {
        public Dictionary<Guid, TenantSnapshot> Snapshots { get; } = [];
        public TenantSnapshot Default { get; set; } = new(true, new HashSet<string> { "MODULE_LOGISTICS" });

        public Task<TenantSnapshot?> GetSnapshotAsync(Guid organizationId) =>
            Task.FromResult<TenantSnapshot?>(Snapshots.TryGetValue(organizationId, out var s) ? s : Default);

        public void Invalidate(Guid organizationId) { }
    }

    /// <summary>A recorder that throws once, then behaves — to prove a failed delivery is retried on resend.</summary>
    private sealed class FlakyRecorder(ITrackingEventRecorder inner) : ITrackingEventRecorder
    {
        public int FailuresLeft { get; set; }

        public Task<TrackingRecordResult> RecordAsync(
            Consignment consignment, IEnumerable<CourierTrackingEvent> events, TrackingEventSource source,
            DateTime now, CancellationToken ct = default)
        {
            if (FailuresLeft-- > 0) throw new InvalidOperationException("database hiccup");
            return inner.RecordAsync(consignment, events, source, now, ct);
        }
    }

    private sealed class Harness
    {
        public required LogisticsDbContext     Db        { get; init; }
        public required StaticTenantContext    Tenant    { get; init; }
        public required string                 DbName    { get; init; }
        public required FakeTenants            Tenants   { get; init; }
        public required FlakyRecorder          Recorder  { get; init; }
        public required CarrierWebhookService  Webhooks  { get; init; }
        public required CarrierCredentialVault Vault     { get; init; }
        public required CarrierAccountRepository Accounts { get; init; }

        public Guid AccountUuid     { get; set; }
        public Guid ConsignmentUuid { get; set; }
        public int  CarrierId       { get; set; }
        public Guid Org => Tenant.OrganizationId;
    }

    private static Harness NewHarness(LogisticsDbContext? db = null, StaticTenantContext? tenant = null)
    {
        var dbName = "";
        if (db is null) (db, tenant, dbName) = LogisticsTestDb.New();

        var registry = new CourierProviderRegistry(
            [new ManualCourierProvider(), new SimulatorCourierProvider(), new ScriptedCourierProvider("SCRIPTED")]);
        var vault    = new CarrierCredentialVault(db, TestEncryption.New());
        var tenants  = new FakeTenants();
        var recorder = new FlakyRecorder(new TrackingEventRecorder(db));

        return new Harness
        {
            Db = db, Tenant = tenant!, DbName = dbName, Tenants = tenants, Recorder = recorder, Vault = vault,
            Accounts = new CarrierAccountRepository(db, registry),
            Webhooks = new CarrierWebhookService(db, registry, vault, recorder, tenants, NullLogger<CarrierWebhookService>.Instance)
        };
    }

    /// <summary>A simulator carrier with a webhook secret, and a booked consignment on it.</summary>
    private static async Task<Harness> Seeded(
        string status = "LABEL_READY", string providerKey = SimulatorCourierProvider.ProviderKey, string? mode = "API",
        bool secret = true, LogisticsDbContext? db = null, StaticTenantContext? tenant = null)
    {
        var h = NewHarness(db, tenant);

        var carrier = new Carrier
        {
            UUID = Guid.NewGuid(), Name = "Simcourier", Code = $"C{Guid.NewGuid():N}"[..6],
            IntegrationMode = mode, ProviderKey = providerKey, IsActive = true
        };
        h.Db.Carriers.Add(carrier);
        await h.Db.SaveChangesAsync();
        h.CarrierId = carrier.Id;

        h.AccountUuid = await h.Accounts.CreateAsync(new CreateCarrierAccountRequest { CarrierUuid = carrier.UUID, AccountName = "Main" }, User);
        if (secret) await h.Vault.SetAsync(h.AccountUuid, "WebhookSecret", Secret, null, null, User);

        var consignment = new Consignment
        {
            UUID = Guid.NewGuid(), ConsignmentNumber = "SHP-2026-00001", CarrierId = carrier.Id, CarrierName = carrier.Name,
            Status = status, MasterAwb = Awb, CreatedBy = User, CreatedDate = T0
        };
        h.Db.Consignments.Add(consignment);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
        h.ConsignmentUuid = consignment.UUID;

        return h;
    }

    private static byte[] Body(string? deliveryId, params (string awb, string milestone, DateTime at)[] events) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            deliveryId,
            events = events.Select(e => new
            {
                awb = e.awb, milestone = e.milestone, occurredAt = e.at.ToString("O"),
                carrierStatus = "SIM_" + e.milestone, description = "Scan", location = "Lahore"
            })
        });

    private static Dictionary<string, string> Signed(byte[] body, DateTime signedAt, string secret = Secret)
    {
        var ts = new DateTimeOffset(signedAt).ToUnixTimeSeconds();
        return new Dictionary<string, string>
        {
            [SimulatorWebhook.TimestampHeader] = ts.ToString(),
            [SimulatorWebhook.SignatureHeader] = SimulatorWebhook.Sign(secret, ts, body)
        };
    }

    private static async Task<WebhookReceipt> Deliver(
        Harness h, byte[] body, DateTime? at = null, Dictionary<string, string>? headers = null,
        string providerKey = SimulatorCourierProvider.ProviderKey, Guid? account = null)
    {
        var now = at ?? T0.AddHours(1);
        h.Db.ChangeTracker.Clear();
        var receipt = await h.Webhooks.ReceiveAsync(providerKey, account ?? h.AccountUuid, headers ?? Signed(body, now), body, now);
        h.Db.ChangeTracker.Clear();
        return receipt;
    }

    private static Task<Consignment> Reload(Harness h) =>
        h.Db.Consignments.AsNoTracking().IgnoreQueryFilters().SingleAsync(c => c.UUID == h.ConsignmentUuid);

    /// <summary>Records events directly through the recorder, bypassing webhooks.</summary>
    private static async Task<TrackingRecordResult> Record(Harness h, DateTime now, params (string milestone, DateTime at)[] events)
    {
        h.Db.ChangeTracker.Clear();
        var consignment = await h.Db.Consignments.SingleAsync(c => c.UUID == h.ConsignmentUuid);
        var result = await new TrackingEventRecorder(h.Db).RecordAsync(
            consignment,
            events.Select(e => new CourierTrackingEvent(e.at, e.milestone, "SIM_" + e.milestone, "Scan", "Lahore")),
            TrackingEventSource.Poll, now);
        h.Db.ChangeTracker.Clear();
        return result;
    }

    // ── The recorder: what events mean ────────────────────────────────────────

    [Fact]
    public async Task A_carrier_skipping_steps_walks_the_consignment_forward_along_legal_transitions()
    {
        var h = await Seeded(status: "BOOKED");

        var result = await Record(h, T0.AddHours(5), ("IN_TRANSIT", T0.AddHours(3)));

        result.Recorded.Should().Be(1);
        result.StatusChangedTo.Should().Be("IN_TRANSIT");
        (await Reload(h)).Status.Should().Be("IN_TRANSIT");

        var stored = await h.Db.ConsignmentTrackingEvents.AsNoTracking().SingleAsync();
        stored.AppliedStatus.Should().Be("IN_TRANSIT");
        stored.Source.Should().Be("POLL");
        stored.Location.Should().Be("Lahore");
    }

    [Fact]
    public async Task The_same_event_arriving_twice_is_stored_once()
    {
        var h = await Seeded();

        await Record(h, T0.AddHours(5), ("PICKED_UP", T0.AddHours(1)));
        var again = await Record(h, T0.AddHours(6), ("PICKED_UP", T0.AddHours(1).AddMilliseconds(400)));

        again.Recorded.Should().Be(0);
        again.Duplicates.Should().Be(1);
        (await h.Db.ConsignmentTrackingEvents.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_late_event_never_undoes_a_later_one_that_arrived_first()
    {
        var h = await Seeded();

        await Record(h, T0.AddHours(5), ("DELIVERED", T0.AddHours(4)));
        var late = await Record(h, T0.AddHours(6), ("OUT_FOR_DELIVERY", T0.AddHours(3)));

        late.Recorded.Should().Be(1, "it still belongs on the timeline");
        late.StatusChangedTo.Should().BeNull();
        (await Reload(h)).Status.Should().Be("DELIVERED");
    }

    [Fact]
    public async Task A_late_event_does_not_move_status_even_where_the_state_machine_would_allow_it()
    {
        // EXCEPTION → IN_TRANSIT is legal; an older in-transit scan arriving after the exception must
        // still not clear it.
        var h = await Seeded();

        await Record(h, T0.AddHours(5), ("CUSTOMS_HOLD", T0.AddHours(3)));
        await Record(h, T0.AddHours(6), ("IN_TRANSIT", T0.AddHours(2)));
        (await Reload(h)).Status.Should().Be("EXCEPTION");

        await Record(h, T0.AddHours(7), ("IN_TRANSIT", T0.AddHours(4)));
        (await Reload(h)).Status.Should().Be("IN_TRANSIT", "a newer scan does clear it");
    }

    [Fact]
    public async Task An_event_that_implies_no_status_does_not_block_an_older_one_that_does()
    {
        var h = await Seeded();

        await Record(h, T0.AddHours(5), ("INFO_RECEIVED", T0.AddHours(3)));
        await Record(h, T0.AddHours(6), ("PICKED_UP", T0.AddHours(2)));

        (await Reload(h)).Status.Should().Be("PICKED_UP");
    }

    [Fact]
    public async Task Pickup_and_delivery_times_are_taken_from_the_carrier_scans()
    {
        var h = await Seeded();

        await Record(h, T0.AddHours(9),
            ("PICKED_UP", T0.AddHours(1)), ("IN_TRANSIT", T0.AddHours(2)), ("OUT_FOR_DELIVERY", T0.AddHours(5)), ("DELIVERED", T0.AddHours(6)));

        var c = await Reload(h);
        c.Status.Should().Be("DELIVERED");
        c.ActualDispatchAt.Should().Be(T0.AddHours(1));
        c.ActualArrivalAt.Should().Be(T0.AddHours(6));
        c.LastStatusEventAt.Should().Be(T0.AddHours(6));
    }

    [Fact]
    public async Task Scans_stamped_with_the_same_minute_resolve_to_the_one_further_along()
    {
        var h = await Seeded();

        await Record(h, T0.AddHours(5), ("IN_TRANSIT", T0.AddHours(2)), ("PICKED_UP", T0.AddHours(2)));

        (await Reload(h)).Status.Should().Be("IN_TRANSIT");
    }

    [Theory]
    [InlineData("CANCELLED")]
    [InlineData("DRAFT")]
    public async Task Events_for_a_consignment_not_in_the_carriers_hands_are_recorded_but_change_nothing(string status)
    {
        var h = await Seeded(status: status);

        var result = await Record(h, T0.AddHours(5), ("DELIVERED", T0.AddHours(4)));

        result.Recorded.Should().Be(1);
        (await Reload(h)).Status.Should().Be(status);
    }

    [Fact]
    public async Task Future_dated_events_and_unknown_milestones_are_dropped_not_trusted()
    {
        // A future date would pin the status clock and make every genuine later scan look old.
        var h = await Seeded();

        var result = await Record(h, T0, ("DELIVERED", T0.AddDays(2)), ("TELEPORTED", T0), ("PICKED_UP", T0.AddMinutes(30)));

        result.Invalid.Should().Be(2);
        result.Recorded.Should().Be(1);
        (await Reload(h)).Status.Should().Be("PICKED_UP");
    }

    [Theory]
    [InlineData("BOOKED",      "DELIVERED",          true)]
    [InlineData("LABEL_READY", "IN_TRANSIT",         true)]
    [InlineData("EXCEPTION",   "RETURNED_TO_ORIGIN", true)]
    [InlineData("DELIVERED",   "IN_TRANSIT",         false)]
    [InlineData("CANCELLED",   "IN_TRANSIT",         false)]
    [InlineData("BOOKING",     "PICKED_UP",          false)]
    public void Paths_follow_the_state_machine_or_do_not_exist(string from, string to, bool exists)
    {
        var start = LogisticsCode.Parse<ShipmentStatus>(from);
        var path = TrackingEventRecorder.PathBetween(start, LogisticsCode.Parse<ShipmentStatus>(to));

        if (!exists)
        {
            path.Should().BeNull();
            return;
        }

        path.Should().NotBeNull().And.EndWith(LogisticsCode.Parse<ShipmentStatus>(to));
        var current = start;
        foreach (var step in path!)
        {
            Domain.StateMachines.ShipmentStateMachine.Instance.CanTransition(current, step).Should().BeTrue();
            current = step;
        }
    }

    // ── The simulator's signature ─────────────────────────────────────────────

    private static CourierWebhookResult Verify(
        byte[] body, Dictionary<string, string> headers, DateTime receivedAt, string? secret = Secret) =>
        SimulatorWebhook.Receive(
            new CourierWebhookRequest(new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase), body, receivedAt),
            secret is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["WebhookSecret"] = secret });

    [Fact]
    public void A_correctly_signed_delivery_is_accepted_and_read()
    {
        var body = Body("dlv-1", (Awb, "PICKED_UP", T0), (Awb, "IN_TRANSIT", T0.AddHours(1)));

        var result = Verify(body, Signed(body, T0.AddHours(2)), T0.AddHours(2));

        result.Verdict.Should().Be(CourierWebhookVerdict.Accepted);
        result.DeliveryId.Should().Be("dlv-1");
        result.Events.Select(e => e.Event.Milestone).Should().Equal("PICKED_UP", "IN_TRANSIT");
        result.Events.Should().OnlyContain(e => e.AwbNumber == Awb);
    }

    [Theory]
    [InlineData("tampered body")]
    [InlineData("wrong secret")]
    [InlineData("no signature")]
    [InlineData("no timestamp")]
    [InlineData("timestamp changed")]
    [InlineData("signature without prefix")]
    [InlineData("short signature")]
    [InlineData("no secret configured")]
    public void Anything_not_signed_by_the_carrier_is_refused(string attack)
    {
        var at      = T0.AddHours(2);
        var body    = Body("dlv-1", (Awb, "DELIVERED", T0));
        var headers = Signed(body, at);
        string? secret = Secret;

        switch (attack)
        {
            case "tampered body":            body = Body("dlv-1", ("SIM-OTHER", "DELIVERED", T0)); break;
            case "wrong secret":             headers = Signed(body, at, "not-the-secret"); break;
            case "no signature":             headers.Remove(SimulatorWebhook.SignatureHeader); break;
            case "no timestamp":             headers.Remove(SimulatorWebhook.TimestampHeader); break;
            case "timestamp changed":        headers[SimulatorWebhook.TimestampHeader] = (long.Parse(headers[SimulatorWebhook.TimestampHeader]) + 1).ToString(); break;
            case "signature without prefix": headers[SimulatorWebhook.SignatureHeader] = headers[SimulatorWebhook.SignatureHeader][3..]; break;
            case "short signature":          headers[SimulatorWebhook.SignatureHeader] = "v1=abcd"; break;
            case "no secret configured":     secret = null; headers = Signed(body, at, ""); break;
        }

        var result = Verify(body, headers, at, secret);

        result.Verdict.Should().Be(CourierWebhookVerdict.InvalidSignature);
        result.Events.Should().BeEmpty();
    }

    [Theory]
    [InlineData(-6, "STALE")]
    [InlineData(6,  "STALE")]
    [InlineData(-4, "ACCEPTED")]
    [InlineData(4,  "ACCEPTED")]
    public void A_signed_timestamp_outside_five_minutes_is_a_replay(int minutesFromNow, string expected)
    {
        var body = Body("dlv-1", (Awb, "DELIVERED", T0));

        var result = Verify(body, Signed(body, T0.AddMinutes(minutesFromNow)), T0);

        result.Verdict.Should().Be(expected == "STALE" ? CourierWebhookVerdict.Stale : CourierWebhookVerdict.Accepted);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"deliveryId\":\"x\"}")]
    [InlineData("{\"events\":[{\"milestone\":\"DELIVERED\",\"occurredAt\":\"2026-09-17T09:00:00Z\"}]}")]
    [InlineData("{\"events\":[{\"awb\":\"A\",\"milestone\":\"TELEPORTED\",\"occurredAt\":\"2026-09-17T09:00:00Z\"}]}")]
    [InlineData("{\"events\":[{\"awb\":\"A\",\"milestone\":\"DELIVERED\",\"occurredAt\":\"yesterday-ish\"}]}")]
    public void A_signed_but_unreadable_delivery_is_malformed(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);

        Verify(body, Signed(body, T0), T0).Verdict.Should().Be(CourierWebhookVerdict.Malformed);
    }

    // ── The webhook service ───────────────────────────────────────────────────

    [Fact]
    public async Task A_verified_delivery_is_kept_applied_and_acknowledged()
    {
        var h = await Seeded();
        var body = Body("dlv-1", (Awb, "PICKED_UP", T0), (Awb, "IN_TRANSIT", T0.AddMinutes(30)));

        var receipt = await Deliver(h, body);

        receipt.StatusCode.Should().Be(200);
        receipt.Recorded.Should().Be(2);
        (await Reload(h)).Status.Should().Be("IN_TRANSIT");

        var delivery = await h.Db.CarrierWebhookDeliveries.AsNoTracking().IgnoreQueryFilters().SingleAsync();
        delivery.Status.Should().Be("PROCESSED");
        delivery.DedupeKey.Should().Be("id:dlv-1");
        delivery.Body.Should().Be(Encoding.UTF8.GetString(body));
        delivery.OrganizationId.Should().Be(h.Org);

        (await h.Db.ConsignmentTrackingEvents.AsNoTracking().Select(e => e.Source).Distinct().ToListAsync())
            .Should().Equal("WEBHOOK");
    }

    [Fact]
    public async Task A_resent_delivery_is_acknowledged_without_being_applied_again()
    {
        var h = await Seeded();
        var body = Body("dlv-1", (Awb, "PICKED_UP", T0));
        await Deliver(h, body);

        var again = await Deliver(h, body, at: T0.AddHours(2));

        again.StatusCode.Should().Be(200);
        again.Message.Should().Be("Already received.");
        (await h.Db.CarrierWebhookDeliveries.IgnoreQueryFilters().SingleAsync()).AttemptCount.Should().Be(1);
        (await h.Db.ConsignmentTrackingEvents.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Without_a_delivery_id_the_body_itself_identifies_a_resend()
    {
        var h = await Seeded();

        await Deliver(h, Body(null, (Awb, "PICKED_UP", T0)));
        await Deliver(h, Body(null, (Awb, "PICKED_UP", T0)), at: T0.AddHours(2));
        await Deliver(h, Body(null, (Awb, "IN_TRANSIT", T0.AddMinutes(20))), at: T0.AddHours(2));

        (await h.Db.CarrierWebhookDeliveries.IgnoreQueryFilters().CountAsync()).Should().Be(2);
        (await h.Db.CarrierWebhookDeliveries.IgnoreQueryFilters().Select(d => d.DedupeKey).ToListAsync())
            .Should().OnlyContain(k => k.StartsWith("sha256:"));
    }

    [Fact]
    public async Task An_unverified_delivery_is_refused_and_leaves_no_trace()
    {
        var h = await Seeded();
        var body = Body("dlv-1", (Awb, "DELIVERED", T0));

        var receipt = await Deliver(h, body, headers: Signed(body, T0.AddHours(1), "forged"));

        receipt.StatusCode.Should().Be(401);
        receipt.Message.Should().Be("Signature verification failed.", "which check failed is for our log, not the caller");
        (await h.Db.CarrierWebhookDeliveries.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await h.Db.ConsignmentTrackingEvents.CountAsync()).Should().Be(0);
        (await Reload(h)).Status.Should().Be("LABEL_READY");
    }

    [Fact]
    public async Task An_account_with_no_webhook_secret_accepts_nothing()
    {
        var h = await Seeded(secret: false);
        var body = Body("dlv-1", (Awb, "DELIVERED", T0));

        (await Deliver(h, body, headers: Signed(body, T0.AddHours(1), ""))).StatusCode.Should().Be(401);
        (await Reload(h)).Status.Should().Be("LABEL_READY");
    }

    [Theory]
    [InlineData("unknown account")]
    [InlineData("provider in url does not match")]
    [InlineData("adapter has no webhook support")]
    [InlineData("carrier is manual")]
    [InlineData("account inactive")]
    [InlineData("module switched off")]
    [InlineData("organization inactive")]
    public async Task Every_way_of_not_being_a_valid_endpoint_answers_the_same_404(string mismatch)
    {
        var provider = mismatch == "adapter has no webhook support" ? "SCRIPTED" : SimulatorCourierProvider.ProviderKey;
        var mode     = mismatch == "carrier is manual" ? "MANUAL" : "API";
        var h        = await Seeded(providerKey: provider, mode: mode);
        var body     = Body("dlv-1", (Awb, "DELIVERED", T0));
        var urlKey   = provider;
        Guid? account = null;

        switch (mismatch)
        {
            case "unknown account":                account = Guid.NewGuid(); break;
            case "provider in url does not match": urlKey = "SCRIPTED"; break;
            case "account inactive":
                var a = await h.Db.CarrierAccounts.SingleAsync();
                a.IsActive = false;
                await h.Db.SaveChangesAsync();
                break;
            case "module switched off":            h.Tenants.Default = new TenantSnapshot(true, new HashSet<string>()); break;
            case "organization inactive":          h.Tenants.Default = new TenantSnapshot(false, new HashSet<string> { "MODULE_LOGISTICS" }); break;
        }

        var receipt = await Deliver(h, body, providerKey: urlKey, account: account);

        receipt.Should().Be(new WebhookReceipt(404, "Unknown webhook endpoint."));
        (await h.Db.CarrierWebhookDeliveries.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_signed_but_unreadable_delivery_is_kept_as_rejected_and_answered_the_same_on_resend()
    {
        var h = await Seeded();
        var body = Encoding.UTF8.GetBytes("{\"deliveryId\":\"dlv-9\",\"events\":[{\"awb\":\"A\",\"milestone\":\"TELEPORTED\",\"occurredAt\":\"2026-09-17T09:00:00Z\"}]}");

        var first  = await Deliver(h, body);
        var second = await Deliver(h, body, at: T0.AddHours(2));

        first.StatusCode.Should().Be(400);
        second.StatusCode.Should().Be(400);
        first.Message.Should().Contain("TELEPORTED");

        var delivery = await h.Db.CarrierWebhookDeliveries.AsNoTracking().IgnoreQueryFilters().SingleAsync();
        delivery.Status.Should().Be("REJECTED");
    }

    [Fact]
    public async Task Events_for_parcels_that_are_not_this_carriers_consignments_here_are_counted_not_applied()
    {
        var h = await Seeded();

        // Another carrier's consignment in the same organization, sharing the airway bill number.
        var other = new Carrier { UUID = Guid.NewGuid(), Name = "Other", Code = "OTH001", IntegrationMode = "API", ProviderKey = "SCRIPTED" };
        h.Db.Carriers.Add(other);
        await h.Db.SaveChangesAsync();
        h.Db.Consignments.Add(new Consignment
        {
            UUID = Guid.NewGuid(), ConsignmentNumber = "SHP-2026-00002", CarrierId = other.Id, Status = "LABEL_READY",
            MasterAwb = "SHARED-AWB", CreatedBy = User, CreatedDate = T0
        });
        await h.Db.SaveChangesAsync();

        var receipt = await Deliver(h, Body("dlv-1", ("SHARED-AWB", "DELIVERED", T0), ("NEVER-BOOKED-HERE", "DELIVERED", T0)));

        receipt.StatusCode.Should().Be(200);
        receipt.Unmatched.Should().Be(2);
        receipt.Recorded.Should().Be(0);
        (await h.Db.Consignments.IgnoreQueryFilters().SingleAsync(c => c.MasterAwb == "SHARED-AWB")).Status.Should().Be("LABEL_READY");
    }

    [Fact]
    public async Task A_delivery_that_failed_to_apply_asks_for_a_resend_and_the_resend_applies_it()
    {
        var h = await Seeded();
        h.Recorder.FailuresLeft = 1;
        var body = Body("dlv-1", (Awb, "DELIVERED", T0));

        var failed = await Deliver(h, body);

        failed.StatusCode.Should().Be(500);
        (await h.Db.CarrierWebhookDeliveries.AsNoTracking().IgnoreQueryFilters().SingleAsync()).Status.Should().Be("FAILED");
        (await Reload(h)).Status.Should().Be("LABEL_READY");

        var resent = await Deliver(h, body, at: T0.AddHours(2));

        resent.StatusCode.Should().Be(200);
        var delivery = await h.Db.CarrierWebhookDeliveries.AsNoTracking().IgnoreQueryFilters().SingleAsync();
        delivery.Status.Should().Be("PROCESSED");
        delivery.AttemptCount.Should().Be(2);
        (await Reload(h)).Status.Should().Be("DELIVERED");
    }

    [Theory]
    [InlineData(1,  202, "RECEIVED")]
    [InlineData(10, 200, "PROCESSED")]
    public async Task A_delivery_still_being_processed_is_not_taken_over_until_it_is_abandoned(int minutesAgo, int expectedCode, string expectedStatus)
    {
        var h = await Seeded();
        var body = Body("dlv-1", (Awb, "PICKED_UP", T0));
        var now = T0.AddHours(1);
        var account = await h.Db.CarrierAccounts.SingleAsync();

        h.Db.CarrierWebhookDeliveries.Add(new CarrierWebhookDelivery
        {
            UUID = Guid.NewGuid(), OrganizationId = h.Org, CarrierAccountId = account.Id, ProviderKey = "SIMULATOR",
            DedupeKey = "id:dlv-1", BodySha256 = new string('0', 64), Body = "{}", Status = "RECEIVED", AttemptCount = 1,
            ReceivedAt = now.AddMinutes(-minutesAgo)
        });
        await h.Db.SaveChangesAsync();

        var receipt = await Deliver(h, body, at: now);

        receipt.StatusCode.Should().Be(expectedCode);
        (await h.Db.CarrierWebhookDeliveries.AsNoTracking().IgnoreQueryFilters().SingleAsync()).Status.Should().Be(expectedStatus);
    }

    [Fact]
    public async Task Future_dated_events_are_noted_on_the_delivery()
    {
        var h = await Seeded();

        var receipt = await Deliver(h, Body("dlv-1", (Awb, "DELIVERED", T0.AddDays(3))));

        receipt.StatusCode.Should().Be(200);
        receipt.Recorded.Should().Be(0);
        (await h.Db.CarrierWebhookDeliveries.AsNoTracking().IgnoreQueryFilters().SingleAsync()).Detail
            .Should().Contain("1 event(s) ignored");
    }

    [Fact]
    public async Task Unreadable_credentials_are_a_server_error_and_nothing_is_kept()
    {
        var h = await Seeded();
        var credential = await h.Db.CarrierCredentials.SingleAsync();
        credential.EncryptedValue = "v2:" + Convert.ToBase64String(new byte[40]);
        await h.Db.SaveChangesAsync();

        var receipt = await Deliver(h, Body("dlv-1", (Awb, "DELIVERED", T0)));

        receipt.StatusCode.Should().Be(500);
        (await h.Db.CarrierWebhookDeliveries.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_timeline_is_latest_first_and_belongs_to_one_organization()
    {
        var h = await Seeded();
        await Deliver(h, Body("dlv-1", (Awb, "PICKED_UP", T0), (Awb, "IN_TRANSIT", T0.AddHours(1))));

        var timeline = await TimelineService(h.Db).GetTimelineAsync(h.ConsignmentUuid);

        timeline!.Select(e => e.Milestone).Should().Equal("IN_TRANSIT", "PICKED_UP");
        timeline![0].AppliedStatus.Should().Be("IN_TRANSIT");

        var other = TimelineService(LogisticsTestDb.OpenAs(h.DbName, Guid.NewGuid()));
        (await other.GetTimelineAsync(h.ConsignmentUuid)).Should().BeNull();
    }

    private static ConsignmentTrackingService TimelineService(LogisticsDbContext db)
    {
        var registry = new CourierProviderRegistry([new ManualCourierProvider(), new SimulatorCourierProvider()]);
        var resolver = new CarrierAccountResolver(db, registry, new CarrierCredentialVault(db, TestEncryption.New()));
        return new ConsignmentTrackingService(db, new ConsignmentTrackingPoller(
            db, resolver, new TrackingEventRecorder(db),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            NullLogger<ConsignmentTrackingPoller>.Instance));
    }

    // ── The controller ────────────────────────────────────────────────────────

    private sealed class CapturingWebhooks(WebhookReceipt answer) : ICarrierWebhookService
    {
        public byte[]? Body { get; private set; }
        public IReadOnlyDictionary<string, string>? Headers { get; private set; }

        public Task<WebhookReceipt> ReceiveAsync(
            string providerKey, Guid accountUuid, IReadOnlyDictionary<string, string> headers, byte[] body,
            DateTime? utcNow = null, CancellationToken ct = default)
        {
            Body = body;
            Headers = headers;
            return Task.FromResult(answer);
        }
    }

    private static CarrierWebhooksController Controller(ICarrierWebhookService service, byte[] body)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(body);
        context.Request.Headers[SimulatorWebhook.SignatureHeader] = "v1=abc";

        return new CarrierWebhooksController(service) { ControllerContext = new ControllerContext { HttpContext = context } };
    }

    [Fact]
    public async Task The_controller_passes_the_raw_bytes_and_headers_through_and_answers_with_the_receipts_code()
    {
        var raw = Encoding.UTF8.GetBytes("{ \"exact\" :  \"bytes\" }");
        var service = new CapturingWebhooks(new WebhookReceipt(401, "Signature verification failed."));

        var result = await Controller(service, raw).Receive("SIMULATOR", Guid.NewGuid(), CancellationToken.None);

        service.Body.Should().Equal(raw, "a signature is over bytes; a re-serialised body never verifies");
        service.Headers![SimulatorWebhook.SignatureHeader.ToLowerInvariant()].Should().Be("v1=abc");
        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task An_oversized_body_is_refused_before_the_service_sees_it()
    {
        var service = new CapturingWebhooks(new WebhookReceipt(200, "Received."));

        var result = await Controller(service, new byte[CarrierWebhooksController.MaxBodyBytes + 1])
            .Receive("SIMULATOR", Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(413);
        service.Body.Should().BeNull();
    }

    // ── Against a real database ───────────────────────────────────────────────

    [SqlServerFact]
    public async Task The_same_delivery_arriving_twice_at_once_is_applied_once()
    {
        await using var server = await SqlServerHarness.CreateAsync();
        var org    = Guid.NewGuid();
        var tenant = new StaticTenantContext { OrganizationId = org };

        Guid accountUuid;
        await using (var setup = server.NewContext(org))
            accountUuid = (await Seeded(db: setup, tenant: tenant)).AccountUuid;

        var body = Body("dlv-race", (Awb, "PICKED_UP", T0), (Awb, "IN_TRANSIT", T0.AddMinutes(30)));
        var now  = T0.AddHours(1);

        async Task<WebhookReceipt> Send()
        {
            await using var db = server.NewContext(org);
            return await NewHarness(db, tenant).Webhooks.ReceiveAsync("SIMULATOR", accountUuid, Signed(body, now), body, now);
        }

        var receipts = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Send()));

        receipts.Select(r => r.StatusCode).Should().OnlyContain(c => c == 200 || c == 202);
        receipts.Should().Contain(r => r.StatusCode == 200);

        await using var check = server.NewContext(org);
        (await check.CarrierWebhookDeliveries.IgnoreQueryFilters().CountAsync()).Should().Be(1);
        (await check.ConsignmentTrackingEvents.IgnoreQueryFilters().CountAsync()).Should().Be(2);
        (await check.Consignments.IgnoreQueryFilters().SingleAsync()).Status.Should().Be("IN_TRANSIT");
    }
}

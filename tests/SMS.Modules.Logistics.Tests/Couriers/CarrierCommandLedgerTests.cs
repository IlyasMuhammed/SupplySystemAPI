using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Couriers;
using SMS.Modules.Logistics.Couriers.Manual;
using SMS.Modules.Logistics.Couriers.Simulator;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Domain.StateMachines;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Logistics.Tests.Couriers;

// T-36 — the command ledger a retry consults instead of booking a second real parcel.
public class CarrierCommandLedgerTests
{
    private const int User  = 42;
    private const int Other = 77;

    private const string Deduplicating    = SimulatorCourierProvider.ProviderKey;  // honours the key
    private const string NonDeduplicating = ManualCourierProvider.ProviderKey;     // does not

    private static readonly DateTime T0 = new(2026, 9, 17, 9, 0, 0, DateTimeKind.Utc);

    private sealed record Harness(LogisticsDbContext Db, StaticTenantContext Tenant, string DbName, CarrierCommandLedger Ledger);

    private static CourierProviderRegistry Registry() =>
        new([new ManualCourierProvider(), new SimulatorCourierProvider()]);

    private static Harness NewHarness()
    {
        var (db, tenant, dbName) = LogisticsTestDb.New();
        return new Harness(db, tenant, dbName, new CarrierCommandLedger(db, tenant, Registry()));
    }

    private static async Task<int> NewConsignment(LogisticsDbContext db)
    {
        var consignment = new Consignment
        {
            UUID = Guid.NewGuid(), ConsignmentNumber = $"SHP-2026-{Random.Shared.Next(1, 99_999):D5}",
            CreatedBy = User, CreatedDate = T0
        };
        db.Consignments.Add(consignment);
        await db.SaveChangesAsync();
        return consignment.Id;
    }

    private static string Fingerprint(string seed = "a") =>
        CourierRequestFingerprint.For(Booking(consignmentNumber: $"SHP-{seed}"));

    private static CarrierCommandClaim Claim(
        int consignmentId, string key = "book-1", string provider = Deduplicating,
        string? fingerprint = null, CarrierCommandType type = CarrierCommandType.Book) =>
        new(type, key, fingerprint ?? Fingerprint(), consignmentId, null, provider, User);

    private static CourierBookingRequest Booking(
        string key = "book-1", string consignmentNumber = "SHP-2026-00001", decimal weight = 10m,
        IReadOnlyDictionary<string, string>? credentials = null, DateTime? pickup = null) =>
        new(key, consignmentNumber, "EXPRESS",
            new CourierAddress("Warehouse", "+923001234567", null, "Plot 1", null, "Karachi", null, "74000", "PK"),
            new CourierAddress("Site", "+923007654321", null, "Road 2", null, "Lahore", null, "54000", "PK"),
            [new CourierPackage("PKG-0001", "CARTON", 40m, 30m, 20m, weight, 5000m)],
            "PREPAID", null, null, pickup ?? T0.AddHours(2), null, ["PO-1"],
            credentials ?? new Dictionary<string, string> { ["ApiKey"] = "secret" });

    private static CourierBookingResult Booked(string awb = "AWB-1", string provider = Deduplicating) =>
        new(CourierOutcome.Succeeded, provider, AwbNumber: awb, TrackingUrl: "https://track.invalid/" + awb,
            Cost: 850m, CostCurrency: "pkr", RawResponse: "{\"ok\":true}");

    private static Task<CarrierCommand> Row(Harness h) =>
        h.Db.CarrierCommands.AsNoTracking().IgnoreQueryFilters().SingleAsync();

    /// <summary>A claim whose call went out and never resolved.</summary>
    private static async Task<(Guid uuid, int consignment)> Unknown(Harness h, string provider)
    {
        var consignment = await NewConsignment(h.Db);
        var claim = await h.Ledger.BeginAsync(Claim(consignment, provider: provider), utcNow: T0);
        await h.Ledger.RecordBookingResultAsync(claim.Command.Uuid,
            new CourierBookingResult(CourierOutcome.Failed, provider, Message: "Gateway timeout"), T0.AddSeconds(30));
        return (claim.Command.Uuid, consignment);
    }

    // ── Claiming ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_first_claim_is_written_in_flight_before_any_call()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h.Db);

        var decision = await h.Ledger.BeginAsync(Claim(consignment), utcNow: T0);

        decision.Kind.Should().Be(LedgerDecisionKind.Proceed);
        decision.ShouldCallCarrier.Should().BeTrue();
        decision.IsRetry.Should().BeFalse();

        var row = await Row(h);
        row.Status.Should().Be("IN_FLIGHT");
        row.AttemptCount.Should().Be(1);
        row.LeaseExpiresAt.Should().Be(T0 + CarrierCommandLedger.LeaseDuration);
        row.OrganizationId.Should().Be(h.Tenant.OrganizationId);
        row.CreatedBy.Should().Be(User);
    }

    [Fact]
    public async Task A_second_claim_while_the_call_is_out_is_told_to_wait_not_to_call()
    {
        // The double-click, and the two workers picking up one job.
        var h = NewHarness();
        var consignment = await NewConsignment(h.Db);
        await h.Ledger.BeginAsync(Claim(consignment), utcNow: T0);

        var second = await h.Ledger.BeginAsync(Claim(consignment), utcNow: T0.AddMinutes(1));

        second.Kind.Should().Be(LedgerDecisionKind.InFlight);
        second.ShouldCallCarrier.Should().BeFalse();
        second.Explanation.Should().Contain("already with the carrier");
        (await Row(h)).AttemptCount.Should().Be(1);
    }

    [Fact]
    public async Task A_key_reused_for_a_different_request_is_refused()
    {
        // A deduplicating carrier would hand back the old parcel for the new booking.
        var h = NewHarness();
        var consignment = await NewConsignment(h.Db);
        await h.Ledger.BeginAsync(Claim(consignment, fingerprint: Fingerprint("a")), utcNow: T0);

        var act = () => h.Ledger.BeginAsync(Claim(consignment, fingerprint: Fingerprint("b")), utcNow: T0);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*different BOOK request*new key*");
        (await h.Db.CarrierCommands.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_key_first_sent_through_one_provider_cannot_be_retried_through_another()
    {
        // The other carrier has never seen the key, and would book a second parcel.
        var h = NewHarness();
        var (_, consignment) = await Unknown(h, Deduplicating);

        var act = () => h.Ledger.BeginAsync(Claim(consignment, provider: NonDeduplicating), utcNow: T0.AddMinutes(10));

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*first sent through provider 'SIMULATOR'*");
    }

    [Fact]
    public async Task The_same_key_in_two_organizations_is_two_commands()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h.Db);
        var otherOrg = Guid.NewGuid();

        (await h.Ledger.BeginAsync(Claim(consignment), utcNow: T0)).Kind.Should().Be(LedgerDecisionKind.Proceed);
        (await h.Ledger.BeginAsync(Claim(consignment), organizationId: otherOrg, utcNow: T0))
            .Kind.Should().Be(LedgerDecisionKind.Proceed);

        (await h.Db.CarrierCommands.IgnoreQueryFilters().Select(c => c.OrganizationId).ToListAsync())
            .Should().BeEquivalentTo([h.Tenant.OrganizationId, otherOrg]);
    }

    [Fact]
    public async Task A_booking_and_a_cancellation_may_share_a_key()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h.Db);

        await h.Ledger.BeginAsync(Claim(consignment, key: "k"), utcNow: T0);
        var cancel = await h.Ledger.BeginAsync(
            Claim(consignment, key: "k", type: CarrierCommandType.Cancel,
                  fingerprint: CourierRequestFingerprint.For(new CourierCancelRequest("k", "SHP-1", "AWB-1", new Dictionary<string, string>()))),
            utcNow: T0);

        cancel.Kind.Should().Be(LedgerDecisionKind.Proceed);
    }

    // Cases are named rather than passed as claims: CarrierCommandClaim is internal, and a public
    // theory method cannot take an internal parameter type (see the T-04 note).
    [Theory]
    [InlineData("blank key")]
    [InlineData("key over 100")]
    [InlineData("short fingerprint")]
    [InlineData("non-hex fingerprint")]
    [InlineData("no provider")]
    [InlineData("no consignment")]
    public async Task An_incomplete_claim_is_refused_before_anything_is_written(string defect)
    {
        var valid = Claim(consignmentId: 1, key: "k");
        var claim = defect switch
        {
            "blank key"           => valid with { IdempotencyKey = "  " },
            "key over 100"        => valid with { IdempotencyKey = new string('k', 101) },
            "short fingerprint"   => valid with { RequestFingerprint = "abc" },
            "non-hex fingerprint" => valid with { RequestFingerprint = new string('z', 64) },
            "no provider"         => valid with { ProviderKey = " " },
            "no consignment"      => valid with { ConsignmentId = 0 },
            _ => throw new ArgumentOutOfRangeException(nameof(defect))
        };

        var h = NewHarness();

        await h.Ledger.Invoking(l => l.BeginAsync(claim, utcNow: T0)).Should().ThrowAsync<BadRequestException>();
        (await h.Db.CarrierCommands.CountAsync()).Should().Be(0);
    }

    // ── Recording answers ─────────────────────────────────────────────────────

    [Fact]
    public async Task A_success_is_recorded_and_a_retry_gets_it_back_without_calling()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h.Db);
        var claim = await h.Ledger.BeginAsync(Claim(consignment), utcNow: T0);

        var recorded = await h.Ledger.RecordBookingResultAsync(claim.Command.Uuid, Booked(), T0.AddSeconds(3));

        recorded.Status.Should().Be(CarrierCommandStatus.Succeeded);
        recorded.AwbNumber.Should().Be("AWB-1");
        recorded.Cost.Should().Be(850m);
        recorded.CostCurrency.Should().Be("PKR");
        recorded.LeaseExpiresAt.Should().BeNull();
        recorded.CompletedAt.Should().Be(T0.AddSeconds(3));
        (await Row(h)).RawResponse.Should().Be("{\"ok\":true}");

        var retry = await h.Ledger.BeginAsync(Claim(consignment), utcNow: T0.AddHours(1));

        retry.Kind.Should().Be(LedgerDecisionKind.AlreadySucceeded);
        retry.ShouldCallCarrier.Should().BeFalse();
        retry.Command.AwbNumber.Should().Be("AWB-1");
        retry.Explanation.Should().Contain("AWB-1");
    }

    [Theory]
    [InlineData(CourierOutcome.Refused)]
    [InlineData(CourierOutcome.Unsupported)]
    public async Task A_refusal_is_an_answer_and_a_retry_earns_the_same_answer(CourierOutcome outcome)
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h.Db);
        var claim = await h.Ledger.BeginAsync(Claim(consignment), utcNow: T0);

        await h.Ledger.RecordBookingResultAsync(claim.Command.Uuid,
            new CourierBookingResult(outcome, Deduplicating, Message: "Postcode not serviced", CarrierErrorCode: "E-POST"), T0);

        var retry = await h.Ledger.BeginAsync(Claim(consignment), utcNow: T0.AddMinutes(1));

        retry.Kind.Should().Be(LedgerDecisionKind.AlreadyRefused);
        retry.Explanation.Should().Contain("Postcode not serviced").And.Contain("new key");
        retry.Command.CarrierErrorCode.Should().Be("E-POST");
    }

    [Fact]
    public async Task A_call_that_did_not_resolve_is_recorded_as_unknown_never_as_not_booked()
    {
        var h = NewHarness();
        var (uuid, _) = await Unknown(h, Deduplicating);

        var row = await Row(h);
        row.Status.Should().Be("UNKNOWN");
        row.Message.Should().Be("Gateway timeout");
        row.FailureDetail.Should().Contain("Whether a parcel was booked is unknown");
        row.CompletedAt.Should().BeNull();
        row.UUID.Should().Be(uuid);
    }

    [Fact]
    public async Task A_success_without_an_airway_bill_is_not_trusted_as_a_success()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h.Db);
        var claim = await h.Ledger.BeginAsync(Claim(consignment), utcNow: T0);

        var recorded = await h.Ledger.RecordBookingResultAsync(claim.Command.Uuid,
            new CourierBookingResult(CourierOutcome.Succeeded, Deduplicating, AwbNumber: "  "), T0);

        recorded.Status.Should().Be(CarrierCommandStatus.Unknown);
        recorded.FailureDetail.Should().Contain("without an airway bill");
    }

    [Fact]
    public async Task An_adapter_exception_is_recorded_as_unknown()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h.Db);
        var claim = await h.Ledger.BeginAsync(Claim(consignment), utcNow: T0);

        var recorded = await h.Ledger.RecordExceptionAsync(claim.Command.Uuid,
            new HttpRequestException("Connection reset by peer"), T0);

        recorded.Status.Should().Be(CarrierCommandStatus.Unknown);
        recorded.FailureDetail.Should().Contain("HttpRequestException").And.Contain("Connection reset by peer");
        recorded.LeaseExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task An_exception_never_overwrites_an_answer_already_recorded()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h.Db);
        var claim = await h.Ledger.BeginAsync(Claim(consignment), utcNow: T0);
        await h.Ledger.RecordBookingResultAsync(claim.Command.Uuid, Booked(), T0);

        var after = await h.Ledger.RecordExceptionAsync(claim.Command.Uuid, new TimeoutException("late"), T0);

        after.Status.Should().Be(CarrierCommandStatus.Succeeded);
        after.FailureDetail.Should().BeNull();
    }

    [Fact]
    public async Task Over_long_carrier_text_is_clipped_so_the_record_of_a_real_booking_still_saves()
    {
        // Failing this save straight after a real booking would lose the only record of it. The
        // in-memory provider ignores column lengths, so the clipping itself is what is asserted.
        var h = NewHarness();
        var consignment = await NewConsignment(h.Db);
        var claim = await h.Ledger.BeginAsync(Claim(consignment), utcNow: T0);

        var recorded = await h.Ledger.RecordBookingResultAsync(claim.Command.Uuid,
            new CourierBookingResult(CourierOutcome.Succeeded, Deduplicating,
                AwbNumber: new string('A', 300), Message: new string('m', 5_000),
                CarrierErrorCode: new string('e', 500), TrackingUrl: new string('u', 900),
                RawResponse: new string('r', 250_000)), T0);

        recorded.AwbNumber.Should().HaveLength(100);
        recorded.Message.Should().HaveLength(1000);
        recorded.CarrierErrorCode.Should().HaveLength(100);
        recorded.TrackingUrl.Should().HaveLength(500);
        (await Row(h)).RawResponse.Should().HaveLength(100_000);
    }

    [Fact]
    public async Task A_booking_result_cannot_be_recorded_against_a_cancellation()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h.Db);
        var claim = await h.Ledger.BeginAsync(Claim(consignment, type: CarrierCommandType.Cancel), utcNow: T0);

        await h.Ledger.Invoking(l => l.RecordBookingResultAsync(claim.Command.Uuid, Booked(), T0))
            .Should().ThrowAsync<ConflictException>().WithMessage("*is a CANCEL, not a BOOK*");
    }

    [Fact]
    public async Task A_cancellation_succeeds_without_an_airway_bill()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h.Db);
        var claim = await h.Ledger.BeginAsync(Claim(consignment, type: CarrierCommandType.Cancel), utcNow: T0);

        var recorded = await h.Ledger.RecordCancelResultAsync(claim.Command.Uuid,
            new CourierCancelResult(CourierOutcome.Succeeded, Deduplicating, "Cancelled"), T0);

        recorded.Status.Should().Be(CarrierCommandStatus.Succeeded);
    }

    // ── The unresolved call — the reason the ledger exists ────────────────────

    [Fact]
    public async Task An_unknown_booking_on_a_carrier_that_does_not_deduplicate_waits_for_a_person()
    {
        var h = NewHarness();
        var (_, consignment) = await Unknown(h, NonDeduplicating);

        var retry = await h.Ledger.BeginAsync(Claim(consignment, provider: NonDeduplicating), utcNow: T0.AddMinutes(10));

        retry.Kind.Should().Be(LedgerDecisionKind.NeedsResolution);
        retry.ShouldCallCarrier.Should().BeFalse();
        retry.Explanation.Should().Contain("second real parcel");
        (await Row(h)).AttemptCount.Should().Be(1, "nothing was sent again");
    }

    [Fact]
    public async Task An_unknown_booking_on_a_deduplicating_carrier_is_retried_under_the_same_key()
    {
        var h = NewHarness();
        var (_, consignment) = await Unknown(h, Deduplicating);
        var later = T0.AddMinutes(10);

        var retry = await h.Ledger.BeginAsync(Claim(consignment), utcNow: later);

        retry.Kind.Should().Be(LedgerDecisionKind.Proceed);
        retry.IsRetry.Should().BeTrue();
        retry.Command.IdempotencyKey.Should().Be("book-1");
        retry.Command.AttemptCount.Should().Be(2);
        retry.Command.LeaseExpiresAt.Should().Be(later + CarrierCommandLedger.LeaseDuration);
        retry.Command.FailureDetail.Should().BeNull();
        retry.Command.FirstAttemptAt.Should().Be(T0, "the history of when it was first asked is kept");
    }

    [Theory]
    [InlineData(NonDeduplicating, nameof(LedgerDecisionKind.NeedsResolution), 1)]
    [InlineData(Deduplicating,    nameof(LedgerDecisionKind.Proceed),         2)]
    public async Task A_worker_that_never_reported_back_is_treated_as_unknown_once_its_lease_runs_out(
        string provider, string expectedKind, int attempts)
    {
        var expected = Enum.Parse<LedgerDecisionKind>(expectedKind);
        var h = NewHarness();
        var consignment = await NewConsignment(h.Db);
        await h.Ledger.BeginAsync(Claim(consignment, provider: provider), utcNow: T0);

        var afterLease = T0 + CarrierCommandLedger.LeaseDuration + TimeSpan.FromSeconds(1);
        var decision = await h.Ledger.BeginAsync(Claim(consignment, provider: provider), utcNow: afterLease);

        decision.Kind.Should().Be(expected);
        var row = await Row(h);
        row.AttemptCount.Should().Be(attempts);

        if (expected == LedgerDecisionKind.NeedsResolution)
        {
            // Persisted, so the exception queue sees it without waiting for the sweep.
            row.Status.Should().Be("UNKNOWN");
            row.FailureDetail.Should().Contain("claim expired");
        }
    }

    [Fact]
    public async Task An_unknown_call_through_a_provider_no_longer_registered_waits_for_a_person()
    {
        var (db, tenant, _) = LogisticsTestDb.New();
        var withSimulator = new CarrierCommandLedger(db, tenant, Registry());
        var consignment = await NewConsignment(db);
        var claim = await withSimulator.BeginAsync(Claim(consignment), utcNow: T0);
        await withSimulator.RecordExceptionAsync(claim.Command.Uuid, new TimeoutException(), T0);

        var simulatorSwitchedOff = new CarrierCommandLedger(db, tenant,
            new CourierProviderRegistry([new ManualCourierProvider()]));

        var retry = await simulatorSwitchedOff.BeginAsync(Claim(consignment), utcNow: T0.AddMinutes(10));

        retry.Kind.Should().Be(LedgerDecisionKind.NeedsResolution);
        retry.Explanation.Should().Contain("no longer registered");
    }

    // ── Late and contradictory answers ────────────────────────────────────────

    [Fact]
    public async Task A_late_success_settles_a_call_already_marked_unknown()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h.Db);
        var claim = await h.Ledger.BeginAsync(Claim(consignment), utcNow: T0);

        await h.Ledger.ExpireStaleLeasesAsync(T0.AddMinutes(6));
        var recorded = await h.Ledger.RecordBookingResultAsync(claim.Command.Uuid, Booked(), T0.AddMinutes(7));

        recorded.Status.Should().Be(CarrierCommandStatus.Succeeded);
        recorded.AwbNumber.Should().Be("AWB-1");
        recorded.FailureDetail.Should().BeNull();
    }

    [Fact]
    public async Task The_same_airway_bill_reported_by_the_call_and_its_retry_is_one_booking()
    {
        var h = NewHarness();
        var (uuid, consignment) = await Unknown(h, Deduplicating);
        await h.Ledger.BeginAsync(Claim(consignment), utcNow: T0.AddMinutes(10));

        await h.Ledger.RecordBookingResultAsync(uuid, Booked("AWB-1"), T0.AddMinutes(10));
        var again = await h.Ledger.RecordBookingResultAsync(uuid, Booked(" awb-1 "), T0.AddMinutes(11));

        again.Status.Should().Be(CarrierCommandStatus.Succeeded);
        again.AwbNumber.Should().Be("AWB-1");
        again.FailureDetail.Should().BeNull();
    }

    [Fact]
    public async Task A_second_airway_bill_for_one_booking_is_raised_and_kept_as_evidence()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h.Db);
        var claim = await h.Ledger.BeginAsync(Claim(consignment), utcNow: T0);
        await h.Ledger.RecordBookingResultAsync(claim.Command.Uuid, Booked("AWB-1"), T0);

        var act = () => h.Ledger.RecordBookingResultAsync(claim.Command.Uuid, Booked("AWB-2"), T0.AddMinutes(1));

        (await act.Should().ThrowAsync<CarrierCommandContradictionException>())
            .WithMessage("*AWB-1*AWB-2*duplicate booking*");

        var row = await Row(h);
        row.AwbNumber.Should().Be("AWB-1", "the first answer is not overwritten");
        row.FailureDetail.Should().Contain("AWB-2", "the second parcel is not forgotten either");
    }

    [Fact]
    public async Task A_success_arriving_after_a_person_said_it_never_happened_is_raised()
    {
        var h = NewHarness();
        var (uuid, _) = await Unknown(h, NonDeduplicating);
        await h.Ledger.ResolveAsNotPerformedAsync(uuid, "Rang the carrier; no record", User, T0.AddMinutes(10));

        var act = () => h.Ledger.RecordBookingResultAsync(uuid, Booked("AWB-9", NonDeduplicating), T0.AddMinutes(20));

        await act.Should().ThrowAsync<CarrierCommandContradictionException>().WithMessage("*NOT_PERFORMED*AWB-9*");
        (await Row(h)).FailureDetail.Should().Contain("AWB-9");
    }

    [Fact]
    public async Task A_late_failure_after_a_success_changes_nothing()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h.Db);
        var claim = await h.Ledger.BeginAsync(Claim(consignment), utcNow: T0);
        await h.Ledger.RecordBookingResultAsync(claim.Command.Uuid, Booked(), T0);

        var after = await h.Ledger.RecordBookingResultAsync(claim.Command.Uuid,
            new CourierBookingResult(CourierOutcome.Failed, Deduplicating, Message: "timeout"), T0.AddMinutes(1));

        after.Status.Should().Be(CarrierCommandStatus.Succeeded);
        after.Message.Should().BeNull();
    }

    // ── Resolution by a person ────────────────────────────────────────────────

    [Fact]
    public async Task A_person_can_confirm_the_carrier_did_book_and_retries_then_get_that_booking()
    {
        var h = NewHarness();
        var (uuid, consignment) = await Unknown(h, NonDeduplicating);

        var resolved = await h.Ledger.ResolveAsPerformedAsync(
            uuid, " AWB-PORTAL-7 ", "Carrier portal shows it booked at 09:00", Other, T0.AddMinutes(30));

        resolved.Status.Should().Be(CarrierCommandStatus.Succeeded);
        resolved.AwbNumber.Should().Be("AWB-PORTAL-7");
        resolved.WasResolvedByPerson.Should().BeTrue();
        resolved.ResolvedBy.Should().Be(Other);
        resolved.ResolutionNote.Should().Be("Carrier portal shows it booked at 09:00");

        (await h.Ledger.BeginAsync(Claim(consignment, provider: NonDeduplicating), utcNow: T0.AddHours(1)))
            .Kind.Should().Be(LedgerDecisionKind.AlreadySucceeded);
    }

    [Fact]
    public async Task Confirming_a_booking_needs_the_airway_bill_but_confirming_a_cancellation_does_not()
    {
        var h = NewHarness();
        var (booking, _) = await Unknown(h, NonDeduplicating);

        await h.Ledger.Invoking(l => l.ResolveAsPerformedAsync(booking, null, "Rang them", User, T0))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*airway bill*");

        var consignment = await NewConsignment(h.Db);
        var cancel = await h.Ledger.BeginAsync(Claim(consignment, key: "c", type: CarrierCommandType.Cancel), utcNow: T0);
        await h.Ledger.RecordExceptionAsync(cancel.Command.Uuid, new TimeoutException(), T0);

        (await h.Ledger.ResolveAsPerformedAsync(cancel.Command.Uuid, null, "Rang them; cancelled", User, T0))
            .Status.Should().Be(CarrierCommandStatus.Succeeded);
    }

    [Fact]
    public async Task A_person_can_confirm_it_never_happened_and_the_key_is_then_retired()
    {
        var h = NewHarness();
        var (uuid, consignment) = await Unknown(h, NonDeduplicating);

        (await h.Ledger.ResolveAsNotPerformedAsync(uuid, "No record at the carrier", User, T0.AddMinutes(30)))
            .Status.Should().Be(CarrierCommandStatus.NotPerformed);

        var retry = await h.Ledger.BeginAsync(Claim(consignment, provider: NonDeduplicating), utcNow: T0.AddHours(1));

        retry.Kind.Should().Be(LedgerDecisionKind.KeyRetired);
        retry.ShouldCallCarrier.Should().BeFalse();
        retry.Explanation.Should().Contain("new key");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Resolving_needs_a_note_saying_how_it_was_confirmed(string? note)
    {
        var h = NewHarness();
        var (uuid, _) = await Unknown(h, NonDeduplicating);

        await h.Ledger.Invoking(l => l.ResolveAsNotPerformedAsync(uuid, note!, User, T0))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*how this was confirmed*");
        (await Row(h)).Status.Should().Be("UNKNOWN");
    }

    [Fact]
    public async Task A_call_still_within_its_lease_cannot_be_resolved_by_hand()
    {
        // The answer may be about to arrive, and a guess would contradict it.
        var h = NewHarness();
        var consignment = await NewConsignment(h.Db);
        var claim = await h.Ledger.BeginAsync(Claim(consignment), utcNow: T0);

        await h.Ledger.Invoking(l => l.ResolveAsNotPerformedAsync(claim.Command.Uuid, "Guessing", User, T0.AddMinutes(1)))
            .Should().ThrowAsync<ConflictException>().WithMessage("*still with the carrier*");

        (await h.Ledger.ResolveAsNotPerformedAsync(claim.Command.Uuid, "Checked after the lease", User, T0.AddMinutes(6)))
            .Status.Should().Be(CarrierCommandStatus.NotPerformed);
    }

    [Fact]
    public async Task An_answered_call_cannot_be_resolved_by_hand()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h.Db);
        var claim = await h.Ledger.BeginAsync(Claim(consignment), utcNow: T0);
        await h.Ledger.RecordBookingResultAsync(claim.Command.Uuid, Booked(), T0);

        await h.Ledger.Invoking(l => l.ResolveAsNotPerformedAsync(claim.Command.Uuid, "Overriding", User, T0))
            .Should().ThrowAsync<ConflictException>().WithMessage("*already SUCCEEDED*");
    }

    [Fact]
    public async Task A_person_cannot_resolve_another_organizations_call()
    {
        var h = NewHarness();
        var (uuid, _) = await Unknown(h, NonDeduplicating);

        var otherDb = LogisticsTestDb.OpenAs(h.DbName, Guid.NewGuid());
        var other = new CarrierCommandLedger(otherDb, new StaticTenantContext { OrganizationId = Guid.NewGuid() }, Registry());

        await other.Invoking(l => l.ResolveAsNotPerformedAsync(uuid, "Not mine", Other, T0))
            .Should().ThrowAsync<NotFoundException>();
        (await Row(h)).Status.Should().Be("UNKNOWN");
    }

    // ── Sweep and exception queue ─────────────────────────────────────────────

    [Fact]
    public async Task The_sweep_moves_only_expired_claims_and_does_so_across_organizations()
    {
        var h = NewHarness();
        var one = await NewConsignment(h.Db);

        await h.Ledger.BeginAsync(Claim(one, key: "old"), utcNow: T0);
        await h.Ledger.BeginAsync(Claim(one, key: "other-org"), organizationId: Guid.NewGuid(), utcNow: T0);
        await h.Ledger.BeginAsync(Claim(one, key: "fresh"), utcNow: T0.AddMinutes(4));
        var answered = await h.Ledger.BeginAsync(Claim(one, key: "answered"), utcNow: T0);
        await h.Ledger.RecordBookingResultAsync(answered.Command.Uuid, Booked(), T0);

        var moved = await h.Ledger.ExpireStaleLeasesAsync(T0.AddMinutes(6));

        moved.Should().Be(2);
        var statuses = await h.Db.CarrierCommands.IgnoreQueryFilters()
            .ToDictionaryAsync(c => c.IdempotencyKey, c => c.Status);
        statuses.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["old"] = "UNKNOWN", ["other-org"] = "UNKNOWN", ["fresh"] = "IN_FLIGHT", ["answered"] = "SUCCEEDED"
        });

        (await h.Ledger.ExpireStaleLeasesAsync(T0.AddMinutes(6))).Should().Be(0, "running it twice changes nothing");
    }

    [Fact]
    public async Task The_exception_queue_lists_this_organizations_unknown_calls_oldest_first()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h.Db);

        foreach (var (key, at) in new[] { ("second", T0.AddMinutes(5)), ("first", T0), ("third", T0.AddMinutes(9)) })
        {
            var claim = await h.Ledger.BeginAsync(Claim(consignment, key: key), utcNow: at);
            await h.Ledger.RecordExceptionAsync(claim.Command.Uuid, new TimeoutException(), at);
        }

        var otherOrg = await h.Ledger.BeginAsync(Claim(consignment, key: "theirs"), organizationId: Guid.NewGuid(), utcNow: T0);
        await h.Ledger.RecordExceptionAsync(otherOrg.Command.Uuid, new TimeoutException(), T0);

        var queue = await h.Ledger.ListUnresolvedAsync();

        queue.Select(c => c.IdempotencyKey).Should().Equal("first", "second", "third");
    }

    [Fact]
    public async Task Find_returns_a_command_by_its_key_and_nothing_for_another_organization()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h.Db);
        await h.Ledger.BeginAsync(Claim(consignment, key: "k-1"), utcNow: T0);

        (await h.Ledger.FindAsync(CarrierCommandType.Book, " k-1 "))!.Status.Should().Be(CarrierCommandStatus.InFlight);
        (await h.Ledger.FindAsync(CarrierCommandType.Cancel, "k-1")).Should().BeNull();
        (await h.Ledger.FindAsync(CarrierCommandType.Book, "k-1", Guid.NewGuid())).Should().BeNull();
    }

    // ── Fingerprints ──────────────────────────────────────────────────────────

    [Fact]
    public void A_fingerprint_ignores_the_key_and_the_credentials()
    {
        // Rotating an API key between a timeout and its retry is still the same booking.
        CourierRequestFingerprint.For(Booking(key: "k-1", credentials: new Dictionary<string, string> { ["ApiKey"] = "old" }))
            .Should().Be(CourierRequestFingerprint.For(Booking(key: "k-2", credentials: new Dictionary<string, string> { ["ApiKey"] = "new" })));
    }

    [Fact]
    public void A_fingerprint_does_not_contain_a_secret()
    {
        var fingerprint = CourierRequestFingerprint.For(Booking(credentials: new Dictionary<string, string> { ["ApiKey"] = "sk_live_abc123" }));

        fingerprint.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void A_fingerprint_compares_numbers_by_value_and_times_by_instant()
    {
        CourierRequestFingerprint.For(Booking(weight: 10m))
            .Should().Be(CourierRequestFingerprint.For(Booking(weight: 10.000m)));

        var utc = new DateTime(2026, 9, 17, 11, 0, 0, DateTimeKind.Utc);
        CourierRequestFingerprint.For(Booking(pickup: utc))
            .Should().Be(CourierRequestFingerprint.For(Booking(pickup: DateTime.SpecifyKind(utc, DateTimeKind.Unspecified))));
    }

    [Fact]
    public void Any_real_change_to_the_request_changes_the_fingerprint()
    {
        var baseline = CourierRequestFingerprint.For(Booking());

        CourierRequestFingerprint.For(Booking(weight: 10.5m)).Should().NotBe(baseline);
        CourierRequestFingerprint.For(Booking(consignmentNumber: "SHP-2026-00002")).Should().NotBe(baseline);
        CourierRequestFingerprint.For(Booking(pickup: T0.AddHours(3))).Should().NotBe(baseline);
        CourierRequestFingerprint.For(Booking() with { ServiceCode = "ECONOMY" }).Should().NotBe(baseline);
        CourierRequestFingerprint.For(Booking() with { SuppliedAwbNumber = "AWB-X" }).Should().NotBe(baseline);
        CourierRequestFingerprint.For(Booking() with { ShipTo = Booking().ShipTo with { City = "Islamabad" } })
            .Should().NotBe(baseline);
    }

    // ── The state machine ─────────────────────────────────────────────────────

    [Fact]
    public void Only_an_answer_or_a_confirmed_absence_is_final()
    {
        var machine = CarrierCommandStateMachine.Instance;

        machine.AllStatuses.Where(machine.IsTerminal).Should().BeEquivalentTo(
            [CarrierCommandStatus.Succeeded, CarrierCommandStatus.Refused, CarrierCommandStatus.NotPerformed]);
    }

    [Theory]
    [InlineData("IN_FLIGHT",     "NOT_PERFORMED")] // only a person, and only once it is unknown
    [InlineData("SUCCEEDED",     "IN_FLIGHT")]     // an answer is never re-sent
    [InlineData("REFUSED",       "IN_FLIGHT")]
    [InlineData("NOT_PERFORMED", "IN_FLIGHT")]     // a retired key stays retired
    [InlineData("UNKNOWN",       "UNKNOWN")]
    public void Transitions_that_would_hide_or_repeat_a_carrier_call_are_refused(string from, string to) =>
        CarrierCommandStateMachine.Instance
            .CanTransition(LogisticsCode.Parse<CarrierCommandStatus>(from), LogisticsCode.Parse<CarrierCommandStatus>(to))
            .Should().BeFalse();

    [Fact]
    public void Every_status_is_reachable_from_a_fresh_claim()
    {
        var machine = CarrierCommandStateMachine.Instance;
        var seen = new HashSet<CarrierCommandStatus> { CarrierCommandStatus.InFlight };
        var queue = new Queue<CarrierCommandStatus>(seen);

        while (queue.TryDequeue(out var status))
            foreach (var next in machine.From(status))
                if (seen.Add(next)) queue.Enqueue(next);

        seen.Should().BeEquivalentTo(Enum.GetValues<CarrierCommandStatus>());
    }

    // ── Against a real database ───────────────────────────────────────────────

    [SqlServerFact]
    public async Task Twenty_workers_claiming_one_key_at_once_send_exactly_one_call()
    {
        // Meaningless in memory: the in-memory provider enforces neither the unique index nor the
        // row version, so every worker would be told to proceed.
        await using var harness = await SqlServerHarness.CreateAsync();
        var org = Guid.NewGuid();

        int consignmentId;
        await using (var setup = harness.NewContext(org)) consignmentId = await NewConsignment(setup);

        var decisions = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            await using var db = harness.NewContext(org);
            var ledger = new CarrierCommandLedger(db, new StaticTenantContext { OrganizationId = org }, Registry());
            return await ledger.BeginAsync(Claim(consignmentId), utcNow: T0);
        }));

        decisions.Count(d => d.ShouldCallCarrier).Should().Be(1, "one real booking call, however many workers");
        decisions.Where(d => !d.ShouldCallCarrier).Should().OnlyContain(d => d.Kind == LedgerDecisionKind.InFlight);

        await using var check = harness.NewContext(org);
        (await check.CarrierCommands.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    [SqlServerFact]
    public async Task Twenty_workers_retrying_one_unknown_call_at_once_send_exactly_one_retry()
    {
        await using var harness = await SqlServerHarness.CreateAsync();
        var org = Guid.NewGuid();
        var tenant = new StaticTenantContext { OrganizationId = org };

        Guid uuid;
        int consignmentId;
        await using (var setup = harness.NewContext(org))
        {
            consignmentId = await NewConsignment(setup);
            var ledger = new CarrierCommandLedger(setup, tenant, Registry());
            uuid = (await ledger.BeginAsync(Claim(consignmentId), utcNow: T0)).Command.Uuid;
            await ledger.RecordExceptionAsync(uuid, new TimeoutException(), T0);
        }

        var decisions = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            await using var db = harness.NewContext(org);
            return await new CarrierCommandLedger(db, tenant, Registry())
                .BeginAsync(Claim(consignmentId), utcNow: T0.AddMinutes(10));
        }));

        decisions.Count(d => d.ShouldCallCarrier).Should().Be(1);

        await using var check = harness.NewContext(org);
        (await check.CarrierCommands.IgnoreQueryFilters().SingleAsync()).AttemptCount.Should().Be(2);
    }

    [SqlServerFact]
    public async Task Over_long_carrier_text_really_saves_against_the_real_column_sizes()
    {
        await using var harness = await SqlServerHarness.CreateAsync();
        var org = Guid.NewGuid();
        await using var db = harness.NewContext(org);
        var ledger = new CarrierCommandLedger(db, new StaticTenantContext { OrganizationId = org }, Registry());

        var claim = await ledger.BeginAsync(Claim(await NewConsignment(db)), utcNow: T0);

        var act = () => ledger.RecordBookingResultAsync(claim.Command.Uuid,
            new CourierBookingResult(CourierOutcome.Succeeded, Deduplicating,
                AwbNumber: new string('A', 300), Message: new string('m', 5_000), CostCurrency: "pkrx",
                CarrierErrorCode: new string('e', 500), TrackingUrl: new string('u', 900),
                CarrierReference: new string('r', 400), RawResponse: new string('r', 250_000)), T0);

        await act.Should().NotThrowAsync();
    }
}

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Visibility;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Logistics.Tests.Visibility;

// T-60 — what has gone wrong with a movement, named and owned until it is settled. This is what
// finally reaches DeliveryExceptionType, which had held eight named causes since T-04 with nothing
// in src/ using one (F46).
public class DeliveryExceptionTests
{
    private const int User  = 42;
    private const int Other = 77;

    private static readonly DateTime T0 = new(2026, 9, 18, 9, 0, 0, DateTimeKind.Utc);

    private sealed record Harness(LogisticsDbContext Db, DeliveryExceptionService Exceptions, string DbName);

    private static Harness NewHarness()
    {
        var (db, _, dbName) = LogisticsTestDb.New();
        return new Harness(db, new DeliveryExceptionService(db), dbName);
    }

    private static async Task<Guid> NewCarrier(Harness h, string name = "Beta Road")
    {
        var carrier = new Carrier
        {
            UUID = Guid.NewGuid(), Name = name, Code = $"C{Guid.NewGuid():N}"[..6], IsActive = true
        };
        h.Db.Carriers.Add(carrier);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return carrier.UUID;
    }

    private static async Task<Guid> NewConsignment(
        Harness h, Guid? carrier = null, string status = "IN_TRANSIT", string? masterAwb = "AWB-1")
    {
        var carrierRow = carrier is null
            ? null
            : await h.Db.Carriers.AsNoTracking().SingleAsync(c => c.UUID == carrier);

        var consignment = new Consignment
        {
            UUID = Guid.NewGuid(), ConsignmentNumber = $"SHP-2026-{Random.Shared.Next(1, 99_999):D5}",
            CarrierId = carrierRow?.Id, CarrierName = carrierRow?.Name,
            Status = status, MasterAwb = masterAwb,
            CreatedBy = User, CreatedDate = T0
        };

        h.Db.Consignments.Add(consignment);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return consignment.UUID;
    }

    /// <summary>A carrier scan on the timeline — the news an exception is raised from.</summary>
    private static async Task<int> NewTrackingEvent(
        Harness h, Guid consignmentUuid, string milestone,
        string? description = null, string? carrierStatus = null, DateTime? occurredAt = null)
    {
        var consignment = await h.Db.Consignments.AsNoTracking().SingleAsync(c => c.UUID == consignmentUuid);

        var evt = new ConsignmentTrackingEvent
        {
            UUID = Guid.NewGuid(), ConsignmentId = consignment.Id,
            Milestone = milestone, Description = description, CarrierStatus = carrierStatus,
            OccurredAt = occurredAt ?? T0, ReceivedAt = occurredAt ?? T0,
            Source = LogisticsCode.Of(TrackingEventSource.Webhook),
            EventKey = Guid.NewGuid().ToString("N").PadRight(64, '0')[..64],
            CreatedDate = T0
        };

        h.Db.ConsignmentTrackingEvents.Add(evt);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return evt.Id;
    }

    private static RaiseExceptionRequest Raise(
        string type = "DAMAGED", string? severity = null, string description = "Carton crushed in transit.",
        int? assignTo = null) =>
        new()
        {
            ExceptionType = type, Severity = severity, Description = description,
            AssignToUserId = assignTo
        };

    // ── Raising by hand ───────────────────────────────────────────────────────

    [Fact]
    public async Task An_exception_raised_by_hand_names_the_cause_and_carries_the_consignment_forward()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await NewConsignment(h, carrier);

        var uuid = await h.Exceptions.RaiseAsync(consignment, Raise(), User);

        var raised = await h.Exceptions.GetAsync(uuid!.Value);

        raised!.ExceptionType.Should().Be("DAMAGED");
        raised.Status.Should().Be("OPEN");
        raised.Source.Should().Be("MANUAL");
        raised.CarrierName.Should().Be("Beta Road");
        raised.MasterAwb.Should().Be("AWB-1");
        raised.ConsignmentUuid.Should().Be(consignment);
    }

    [Fact]
    public async Task Severity_defaults_to_normal_rather_than_to_the_loudest_option()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);

        var uuid = await h.Exceptions.RaiseAsync(consignment, Raise(), User);

        (await h.Exceptions.GetAsync(uuid!.Value))!.Severity.Should().Be("NORMAL");
    }

    [Fact]
    public async Task An_exception_with_no_description_is_refused()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);

        var raise = async () => await h.Exceptions.RaiseAsync(consignment, Raise(description: "   "), User);

        await raise.Should().ThrowAsync<BadRequestException>()
            .WithMessage("*nobody can restate*");
    }

    [Fact]
    public async Task A_type_the_module_does_not_know_is_refused_with_the_ones_it_does()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);

        var raise = async () => await h.Exceptions.RaiseAsync(consignment, Raise(type: "GONE_MISSING"), User);

        var thrown = await raise.Should().ThrowAsync<BadRequestException>();

        thrown.WithMessage("*ADDRESS_INVALID*");
        thrown.WithMessage("*COD_MISMATCH*");
    }

    [Fact]
    public async Task Raising_against_a_consignment_that_is_not_there_returns_nothing_rather_than_throwing()
    {
        var h = NewHarness();

        (await h.Exceptions.RaiseAsync(Guid.NewGuid(), Raise(), User)).Should().BeNull();
    }

    /// <summary>
    /// Every named cause has to be reachable. An enum member with no way to reach it is a defect —
    /// this is the third time that pattern has turned up (F38, T-27, F46), so it is now asserted.
    /// </summary>
    [Theory]
    [InlineData("ADDRESS_INVALID")]
    [InlineData("CONSIGNEE_UNREACHABLE")]
    [InlineData("REFUSED")]
    [InlineData("DAMAGED")]
    [InlineData("CUSTOMS_HOLD")]
    [InlineData("LOST")]
    [InlineData("DELAYED")]
    [InlineData("COD_MISMATCH")]
    public async Task Every_named_cause_can_actually_be_raised(string type)
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);

        var uuid = await h.Exceptions.RaiseAsync(consignment, Raise(type: type), User);

        (await h.Exceptions.GetAsync(uuid!.Value))!.ExceptionType.Should().Be(type);
    }

    // ── Raising from what the carrier said ────────────────────────────────────

    [Fact]
    public async Task A_customs_hold_scan_raises_a_critical_exception_in_the_carriers_own_words()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await NewConsignment(h, carrier);
        await NewTrackingEvent(h, consignment, "CUSTOMS_HOLD",
            description: "Held pending commercial invoice", carrierStatus: "CH01");

        var raised = await h.Exceptions.SweepFromTrackingAsync(0);

        raised.Should().Be(1);

        var queue = await h.Exceptions.GetQueueAsync(new ExceptionFilter());
        var exception = queue.Data.Single();

        exception.ExceptionType.Should().Be("CUSTOMS_HOLD");
        exception.Severity.Should().Be("CRITICAL", "goods that cannot move are not a normal day");
        exception.Source.Should().Be("CARRIER");
        exception.Description.Should().Be("Held pending commercial invoice");
        exception.CarrierStatus.Should().Be("CH01");
    }

    [Fact]
    public async Task A_failed_delivery_attempt_raises_consignee_unreachable_at_normal_severity()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        await NewTrackingEvent(h, consignment, "DELIVERY_ATTEMPTED", description: "Nobody at address");

        await h.Exceptions.SweepFromTrackingAsync(0);

        var exception = (await h.Exceptions.GetQueueAsync(new ExceptionFilter())).Data.Single();

        exception.ExceptionType.Should().Be("CONSIGNEE_UNREACHABLE");
        exception.Severity.Should().Be("NORMAL");
    }

    [Fact]
    public async Task The_generic_exception_milestone_raises_nothing_because_a_guessed_type_is_a_lie()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        await NewTrackingEvent(h, consignment, "EXCEPTION", carrierStatus: "XX99");

        var raised = await h.Exceptions.SweepFromTrackingAsync(0);

        raised.Should().Be(0,
            "the carrier said something was wrong without saying what, and picking one of eight "
          + "causes for it would be a guess recorded as a fact");
    }

    [Fact]
    public async Task Ordinary_scans_raise_nothing()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        await NewTrackingEvent(h, consignment, "IN_TRANSIT");
        await NewTrackingEvent(h, consignment, "ARRIVED_AT_HUB");
        await NewTrackingEvent(h, consignment, "OUT_FOR_DELIVERY");

        (await h.Exceptions.SweepFromTrackingAsync(0)).Should().Be(0);
    }

    [Fact]
    public async Task The_same_scan_swept_twice_opens_one_piece_of_work()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        await NewTrackingEvent(h, consignment, "CUSTOMS_HOLD");

        await h.Exceptions.SweepFromTrackingAsync(0);
        var second = await h.Exceptions.SweepFromTrackingAsync(0);

        second.Should().Be(0, "carriers resend deliveries they think failed, and T-39 proved it");
        (await h.Exceptions.GetQueueAsync(new ExceptionFilter())).TotalRecords.Should().Be(1);
    }

    [Fact]
    public async Task A_second_hold_on_the_same_consignment_is_a_second_exception()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        await NewTrackingEvent(h, consignment, "CUSTOMS_HOLD", occurredAt: T0);
        await NewTrackingEvent(h, consignment, "CUSTOMS_HOLD", occurredAt: T0.AddDays(3));

        var raised = await h.Exceptions.SweepFromTrackingAsync(0);

        raised.Should().Be(2, "two holds three days apart are two problems, not one resent scan");
    }

    // ── What has gone quiet ───────────────────────────────────────────────────

    private static async Task MarkStuck(Harness h, Guid consignmentUuid, string reason, DateTime since)
    {
        var consignment = await h.Db.Consignments.SingleAsync(c => c.UUID == consignmentUuid);
        consignment.StuckSince  = since;
        consignment.StuckReason = reason;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task A_consignment_that_has_gone_quiet_becomes_work_somebody_owns()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await NewConsignment(h, carrier);
        await MarkStuck(h, consignment, "No carrier scan for 6 days while in transit.", T0);

        var raised = await h.Exceptions.SweepStuckAsync(0);

        raised.Should().Be(1);

        var exception = (await h.Exceptions.GetQueueAsync(new ExceptionFilter())).Data.Single();

        exception.Source.Should().Be("SYSTEM");
        exception.ExceptionType.Should().Be("DELAYED",
            "nothing has been reported, which is the whole complaint — naming a cause would invent one");
        exception.Description.Should().Be("No carrier scan for 6 days while in transit.");
        exception.OccurredAt.Should().Be(T0, "it has been a problem since it went quiet, not since the sweep ran");
    }

    [Fact]
    public async Task A_consignment_that_is_not_stuck_raises_nothing()
    {
        var h = NewHarness();
        await NewConsignment(h);

        (await h.Exceptions.SweepStuckAsync(0)).Should().Be(0);
    }

    [Fact]
    public async Task The_same_silence_raises_one_exception_however_often_the_sweep_runs()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        await MarkStuck(h, consignment, "Not collected by the carrier 4 days after booking.", T0);

        await h.Exceptions.SweepStuckAsync(0);
        await h.Exceptions.SweepStuckAsync(0);

        (await h.Exceptions.SweepStuckAsync(0)).Should().Be(0);
        (await h.Exceptions.GetQueueAsync(new ExceptionFilter())).TotalRecords.Should().Be(1);
    }

    [Fact]
    public async Task Resolving_one_does_not_make_the_sweep_raise_it_again()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        await MarkStuck(h, consignment, "Out for delivery for 3 days with no outcome.", T0);
        await h.Exceptions.SweepStuckAsync(0);

        var raised = (await h.Exceptions.GetQueueAsync(new ExceptionFilter())).Data.Single();
        await h.Exceptions.ResolveAsync(raised.UUID, new ResolveExceptionRequest
        {
            Resolution = "Carrier confirmed by telephone; scan was missed."
        }, User);

        (await h.Exceptions.SweepStuckAsync(0)).Should().Be(0,
            "an exception somebody has finished with must not come straight back");
    }

    [Fact]
    public async Task A_second_silence_after_it_moved_again_is_a_second_exception()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);

        await MarkStuck(h, consignment, "No carrier scan for 6 days while in transit.", T0);
        await h.Exceptions.SweepStuckAsync(0);

        // It moved, then went quiet again a fortnight later. T-40 clears StuckSince on movement and
        // sets it fresh, so the second silence is a different problem.
        await MarkStuck(h, consignment, "Out for delivery for 3 days with no outcome.", T0.AddDays(14));

        (await h.Exceptions.SweepStuckAsync(0)).Should().Be(1);
        (await h.Exceptions.GetQueueAsync(new ExceptionFilter())).TotalRecords.Should().Be(2);
    }

    // ── The queue ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_queue_defaults_to_what_still_needs_work()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);

        var open     = await h.Exceptions.RaiseAsync(consignment, Raise(), User);
        var resolved = await h.Exceptions.RaiseAsync(consignment, Raise(type: "DELAYED"), User);
        await h.Exceptions.ResolveAsync(resolved!.Value, new ResolveExceptionRequest
        {
            Resolution = "Arrived the next morning."
        }, User);

        var queue = await h.Exceptions.GetQueueAsync(new ExceptionFilter());

        queue.Data.Should().ContainSingle().Which.UUID.Should().Be(open!.Value);
    }

    [Fact]
    public async Task Critical_comes_first_and_then_the_oldest()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);

        await h.Exceptions.RaiseAsync(consignment, new RaiseExceptionRequest
        {
            ExceptionType = "DELAYED", Description = "Two days late.", OccurredAt = T0.AddDays(-5)
        }, User);

        await h.Exceptions.RaiseAsync(consignment, new RaiseExceptionRequest
        {
            ExceptionType = "LOST", Severity = "CRITICAL", Description = "Carrier cannot find it.",
            OccurredAt = T0
        }, User);

        await h.Exceptions.RaiseAsync(consignment, new RaiseExceptionRequest
        {
            ExceptionType = "REFUSED", Description = "Consignee refused.", OccurredAt = T0.AddDays(-1)
        }, User);

        var queue = await h.Exceptions.GetQueueAsync(new ExceptionFilter());

        queue.Data.Select(e => e.ExceptionType)
            .Should().Equal("LOST", "DELAYED", "REFUSED");
    }

    [Fact]
    public async Task The_queue_can_be_narrowed_to_what_nobody_owns()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);

        await h.Exceptions.RaiseAsync(consignment, Raise(), User);
        await h.Exceptions.RaiseAsync(consignment, Raise(type: "DELAYED", assignTo: Other), User);

        var unowned = await h.Exceptions.GetQueueAsync(new ExceptionFilter { Unassigned = true });

        unowned.Data.Should().ContainSingle().Which.ExceptionType.Should().Be("DAMAGED");
    }

    [Fact]
    public async Task An_exception_nobody_owns_says_so_on_its_face()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);

        var uuid = await h.Exceptions.RaiseAsync(consignment, Raise(), User);

        (await h.Exceptions.GetAsync(uuid!.Value))!.Warnings
            .Should().Contain(w => w.Contains("Nobody owns this"));
    }

    [Fact]
    public async Task The_queue_can_be_narrowed_to_one_carrier()
    {
        var h = NewHarness();
        var beta  = await NewCarrier(h, "Beta Road");
        var delta = await NewCarrier(h, "Delta Air");

        await h.Exceptions.RaiseAsync(await NewConsignment(h, beta),  Raise(), User);
        await h.Exceptions.RaiseAsync(await NewConsignment(h, delta), Raise(type: "LOST"), User);

        var mine = await h.Exceptions.GetQueueAsync(new ExceptionFilter { CarrierUuid = delta });

        mine.Data.Should().ContainSingle().Which.CarrierName.Should().Be("Delta Air");
    }

    // ── Working one ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Assigning_one_records_who_has_it_and_when_they_got_it()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        var uuid = await h.Exceptions.RaiseAsync(consignment, Raise(), User);

        await h.Exceptions.PatchAsync(uuid!.Value, new PatchExceptionRequest
        {
            AssignToUserId = Other, Severity = "CRITICAL", Status = "WAITING"
        }, User);

        var patched = await h.Exceptions.GetAsync(uuid.Value);

        patched!.AssignedToUserId.Should().Be(Other);
        patched.AssignedAt.Should().NotBeNull();
        patched.Severity.Should().Be("CRITICAL");
        patched.Status.Should().Be("WAITING");
        patched.Warnings.Should().NotContain(w => w.Contains("Nobody owns this"));
    }

    [Fact]
    public async Task Handing_one_back_to_nobody_clears_both_the_owner_and_the_time()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        var uuid = await h.Exceptions.RaiseAsync(consignment, Raise(assignTo: Other), User);

        await h.Exceptions.PatchAsync(uuid!.Value, new PatchExceptionRequest { ClearAssignee = true }, User);

        var patched = await h.Exceptions.GetAsync(uuid.Value);

        patched!.AssignedToUserId.Should().BeNull();
        patched.AssignedAt.Should().BeNull();
    }

    [Fact]
    public async Task A_patch_cannot_quietly_close_one()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        var uuid = await h.Exceptions.RaiseAsync(consignment, Raise(), User);

        var patch = async () => await h.Exceptions.PatchAsync(
            uuid!.Value, new PatchExceptionRequest { Status = "RESOLVED" }, User);

        await patch.Should().ThrowAsync<BadRequestException>()
            .WithMessage("*their own reason*");
    }

    // ── Closing ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Resolving_records_how_it_was_settled_and_by_whom()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        var uuid = await h.Exceptions.RaiseAsync(consignment, Raise(), User);

        await h.Exceptions.ResolveAsync(uuid!.Value, new ResolveExceptionRequest
        {
            Resolution = "Replacement despatched; carrier credited the freight."
        }, Other);

        var resolved = await h.Exceptions.GetAsync(uuid.Value);

        resolved!.Status.Should().Be("RESOLVED");
        resolved.Resolution.Should().StartWith("Replacement despatched");
        resolved.ResolvedAt.Should().NotBeNull();

        var stored = await h.Db.DeliveryExceptions.AsNoTracking().SingleAsync(e => e.UUID == uuid.Value);
        stored.ResolvedBy.Should().Be(Other);
    }

    [Fact]
    public async Task Resolving_without_saying_how_is_refused()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        var uuid = await h.Exceptions.RaiseAsync(consignment, Raise(), User);

        var resolve = async () => await h.Exceptions.ResolveAsync(
            uuid!.Value, new ResolveExceptionRequest { Resolution = "" }, User);

        await resolve.Should().ThrowAsync<BadRequestException>()
            .WithMessage("*teaches nobody anything*");
    }

    [Fact]
    public async Task Withdrawing_is_kept_apart_from_resolving()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        var uuid = await h.Exceptions.RaiseAsync(consignment, Raise(), User);

        await h.Exceptions.WithdrawAsync(uuid!.Value, new WithdrawExceptionRequest
        {
            Reason = "Duplicate of the hold raised yesterday."
        }, User);

        var withdrawn = await h.Exceptions.GetAsync(uuid.Value);

        withdrawn!.Status.Should().Be("WITHDRAWN",
            "counting a mistaken exception as one that was fixed flatters every figure a scorecard produces");
        withdrawn.Resolution.Should().StartWith("Duplicate of");
    }

    [Fact]
    public async Task A_closed_exception_cannot_be_reopened_or_edited()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        var uuid = await h.Exceptions.RaiseAsync(consignment, Raise(), User);
        await h.Exceptions.ResolveAsync(uuid!.Value, new ResolveExceptionRequest
        {
            Resolution = "Cleared customs."
        }, User);

        var patch = async () => await h.Exceptions.PatchAsync(
            uuid.Value, new PatchExceptionRequest { Severity = "CRITICAL" }, User);

        var resolveAgain = async () => await h.Exceptions.ResolveAsync(
            uuid.Value, new ResolveExceptionRequest { Resolution = "Again." }, User);

        await patch.Should().ThrowAsync<ConflictException>().WithMessage("*Raise a new one*");
        await resolveAgain.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task One_settled_here_while_the_carrier_still_says_otherwise_is_flagged()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h, status: "EXCEPTION");
        var uuid = await h.Exceptions.RaiseAsync(consignment, Raise(), User);
        await h.Exceptions.ResolveAsync(uuid!.Value, new ResolveExceptionRequest
        {
            Resolution = "Consignee collected it themselves."
        }, User);

        (await h.Exceptions.GetAsync(uuid.Value))!.Warnings
            .Should().Contain(w => w.Contains("still in exception with the carrier"));
    }

    // ── The summary ───────────────────────────────────────────────────────────

    [Fact]
    public async Task The_summary_counts_by_type_with_the_oldest_still_open()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);

        await h.Exceptions.RaiseAsync(consignment, new RaiseExceptionRequest
        {
            ExceptionType = "DELAYED", Description = "Late.", OccurredAt = DateTime.UtcNow.AddHours(-48)
        }, User);

        await h.Exceptions.RaiseAsync(consignment, new RaiseExceptionRequest
        {
            ExceptionType = "DELAYED", Description = "Also late.", OccurredAt = DateTime.UtcNow.AddHours(-2)
        }, User);

        await h.Exceptions.RaiseAsync(consignment, new RaiseExceptionRequest
        {
            ExceptionType = "LOST", Severity = "CRITICAL", Description = "Gone.",
            OccurredAt = DateTime.UtcNow.AddHours(-1), AssignToUserId = Other
        }, User);

        var summary = await h.Exceptions.GetSummaryAsync();

        summary.Open.Should().Be(3);
        summary.Critical.Should().Be(1);
        summary.Unassigned.Should().Be(2);

        // Critical first: a count of one can outrank a count of two.
        summary.ByType.First().ExceptionType.Should().Be("LOST");

        var delayed = summary.ByType.Single(t => t.ExceptionType == "DELAYED");
        delayed.Open.Should().Be(2);
        delayed.OldestHours.Should().BeApproximately(48, 1,
            "a small count of something two days old is what a total hides");
    }

    [Fact]
    public async Task The_summary_says_how_many_belong_to_nobody()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        await h.Exceptions.RaiseAsync(consignment, Raise(), User);

        (await h.Exceptions.GetSummaryAsync()).Warnings
            .Should().Contain(w => w.Contains("belong to nobody"));
    }

    [Fact]
    public async Task Consignments_in_exception_with_nothing_recorded_are_reported_rather_than_guessed_at()
    {
        var h = NewHarness();
        await NewConsignment(h, status: "EXCEPTION");
        await NewConsignment(h, status: "EXCEPTION");

        var summary = await h.Exceptions.GetSummaryAsync();

        summary.Open.Should().Be(0);
        summary.Warnings.Should().Contain(w => w.Contains("nothing recorded against them"));
    }

    [Fact]
    public async Task A_consignment_in_exception_that_has_one_recorded_is_not_reported_again()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h, status: "EXCEPTION");
        await h.Exceptions.RaiseAsync(consignment, Raise(), User);

        (await h.Exceptions.GetSummaryAsync()).Warnings
            .Should().NotContain(w => w.Contains("nothing recorded against them"));
    }

    [Fact]
    public async Task How_long_it_has_been_open_stops_at_the_moment_it_closed()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        var uuid = await h.Exceptions.RaiseAsync(consignment, new RaiseExceptionRequest
        {
            ExceptionType = "DELAYED", Description = "Late.", OccurredAt = DateTime.UtcNow.AddHours(-6)
        }, User);

        await h.Exceptions.ResolveAsync(uuid!.Value, new ResolveExceptionRequest
        {
            Resolution = "Delivered."
        }, User);

        var resolved = await h.Exceptions.GetAsync(uuid.Value);

        resolved!.OpenForHours.Should().BeApproximately(6, 0.5,
            "a closed exception does not keep ageing");
    }

    // ── Tenancy ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task One_organization_never_sees_anothers_exceptions()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        await h.Exceptions.RaiseAsync(consignment, Raise(), User);

        var stranger = new DeliveryExceptionService(LogisticsTestDb.OpenAs(h.DbName, Guid.NewGuid()));

        (await stranger.GetQueueAsync(new ExceptionFilter())).TotalRecords.Should().Be(0);
        (await stranger.GetSummaryAsync()).Open.Should().Be(0);
    }
}

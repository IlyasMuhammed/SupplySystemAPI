using FluentAssertions;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Domain.StateMachines;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Logistics.Tests;

// T-05 — both lifecycles. Pure logic: no database, no services.
//
// The enums are internal, so theory methods name statuses by their persisted code and parse.
// Assertions that sweep whole tables use private generic helpers rather than [MemberData].
public class StateMachineTests
{
    private static readonly DeliveryStateMachine Delivery = DeliveryStateMachine.Instance;
    private static readonly ShipmentStateMachine Shipment = ShipmentStateMachine.Instance;

    // ── TC-05.1 / TC-05.2 — the table is the whole specification ──────────────

    private static void AssertTableIsExhaustive<TStatus>(IStateMachine<TStatus> machine)
        where TStatus : struct, Enum
    {
        var all = machine.AllStatuses;

        foreach (var from in all)
        {
            var declared = machine.From(from);

            // TC-05.1 — everything in the table is allowed.
            foreach (var to in declared)
            {
                machine.CanTransition(from, to).Should().BeTrue(
                    $"{LogisticsCode.Of(from)} → {LogisticsCode.Of(to)} is declared");
                machine.Validate(from, to).IsAllowed.Should().BeTrue();
                machine.Validate(from, to).Reason.Should().BeNull();
            }

            // TC-05.2 — everything else is refused, and says so usefully.
            foreach (var to in all.Except(declared))
            {
                var fromCode = LogisticsCode.Of(from);
                var toCode   = LogisticsCode.Of(to);

                var result = machine.Validate(from, to);

                result.IsAllowed.Should().BeFalse($"{fromCode} → {toCode} is not declared");
                result.Reason.Should().NotBeNullOrWhiteSpace();
                result.Reason.Should().Contain(fromCode).And.Contain(toCode,
                    "a refusal has to name both ends or it is useless in a log");
            }
        }
    }

    [Fact]
    public void Delivery_transitions_are_exactly_the_declared_table() =>
        AssertTableIsExhaustive(Delivery);

    [Fact]
    public void Shipment_transitions_are_exactly_the_declared_table() =>
        AssertTableIsExhaustive(Shipment);

    [Fact]
    public void No_status_may_transition_to_itself()
    {
        // Covers TC-05.7 for every state, not just BOOKING. A self-transition is how a repeated
        // action hides: the status looks unchanged, so nothing downstream notices it happened
        // twice.
        foreach (var status in Delivery.AllStatuses)
            Delivery.CanTransition(status, status).Should().BeFalse();

        foreach (var status in Shipment.AllStatuses)
            Shipment.CanTransition(status, status).Should().BeFalse();
    }

    // ── TC-05.3 — the happy path cannot be short-circuited ────────────────────

    [Theory]
    [InlineData("DRAFT",    "GOODS_ISSUED")] // skips release, picking, packing entirely
    [InlineData("DRAFT",    "PICKING")]      // skips the stock reservation at RELEASED
    [InlineData("RELEASED", "PACKED")]       // skips picking
    [InlineData("PICKING",  "STAGED")]       // skips packing
    [InlineData("PACKED",   "DELIVERED")]    // skips goods issue — stock would never leave the books
    public void Delivery_cannot_skip_steps(string fromCode, string toCode)
    {
        var from = LogisticsCode.Parse<DeliveryStatus>(fromCode);
        var to   = LogisticsCode.Parse<DeliveryStatus>(toCode);

        Delivery.CanTransition(from, to).Should().BeFalse();
    }

    [Fact]
    public void Issued_goods_reach_delivered_by_transit_or_by_collection()
    {
        // A29 §7.2: a shipped delivery goes GOODS_ISSUED → IN_TRANSIT → DELIVERED; a self-pickup
        // one has no transit — the customer walks out with it — so GOODS_ISSUED → DELIVERED is
        // legal. The step it must never skip is the goods issue itself (PACKED → DELIVERED above).
        Delivery.CanTransition(DeliveryStatus.GoodsIssued, DeliveryStatus.InTransit).Should().BeTrue();
        Delivery.CanTransition(DeliveryStatus.GoodsIssued, DeliveryStatus.Delivered).Should().BeTrue();
        Delivery.CanTransition(DeliveryStatus.Staged, DeliveryStatus.Delivered).Should().BeFalse();
    }

    // ── TC-05.4 — hold and resume ─────────────────────────────────────────────

    [Theory]
    [InlineData("RELEASED")]
    [InlineData("PICKING")]
    [InlineData("PICKED")]
    [InlineData("PACKED")]
    [InlineData("STAGED")]
    [InlineData("PENDING_APPROVAL")]
    public void A_delivery_in_progress_can_be_held_and_resumed(string statusCode)
    {
        var status = LogisticsCode.Parse<DeliveryStatus>(statusCode);

        Delivery.CanHold(status).Should().BeTrue();
        Delivery.ValidateResume(status).IsAllowed.Should().BeTrue(
            "a resume must return to the status the hold interrupted");
    }

    [Fact]
    public void Resuming_a_held_delivery_cannot_rewind_it_to_draft()
    {
        // The specific regression this guards: resuming to DRAFT would strip the delivery of a
        // released state while its stock reservation is still held.
        Delivery.ValidateResume(DeliveryStatus.Draft).IsAllowed.Should().BeFalse();
        Delivery.CanTransition(DeliveryStatus.Released, DeliveryStatus.OnHold).Should().BeTrue();
        Delivery.CanTransition(DeliveryStatus.OnHold, DeliveryStatus.Released).Should().BeTrue();
    }

    [Theory]
    [InlineData("DRAFT")]        // nothing reserved, nothing to hold
    [InlineData("GOODS_ISSUED")] // stock already gone
    [InlineData("IN_TRANSIT")]
    [InlineData("DELIVERED")]
    [InlineData("CLOSED")]
    [InlineData("CANCELLED")]
    public void A_delivery_outside_the_execution_window_cannot_be_held(string statusCode) =>
        Delivery.CanHold(LogisticsCode.Parse<DeliveryStatus>(statusCode)).Should().BeFalse();

    // ── TC-05.5 — terminal states ─────────────────────────────────────────────

    [Fact]
    public void Delivery_terminal_states_have_no_exits()
    {
        Delivery.IsTerminal(DeliveryStatus.Closed).Should().BeTrue();
        Delivery.IsTerminal(DeliveryStatus.Cancelled).Should().BeTrue();

        Delivery.AllStatuses
            .Where(Delivery.IsTerminal)
            .Should().BeEquivalentTo([DeliveryStatus.Closed, DeliveryStatus.Cancelled],
                "nothing else in the delivery lifecycle is an end state");
    }

    [Fact]
    public void Shipment_terminal_states_have_no_exits() =>
        Shipment.AllStatuses
            .Where(Shipment.IsTerminal)
            .Should().BeEquivalentTo(
            [
                ShipmentStatus.Delivered,
                ShipmentStatus.ReturnedToOrigin,
                ShipmentStatus.Cancelled,
                ShipmentStatus.Lost
            ]);

    [Fact]
    public void A_terminal_status_says_it_is_final_rather_than_listing_alternatives()
    {
        var reason = Delivery.Validate(DeliveryStatus.Closed, DeliveryStatus.Draft).Reason;

        reason.Should().Contain("final").And.Contain("CLOSED").And.Contain("DRAFT");
    }

    // ── TC-05.6 / TC-05.7 — the booking window ────────────────────────────────

    [Fact]
    public void An_in_flight_booking_resolves_to_exactly_one_of_two_outcomes() =>
        Shipment.From(ShipmentStatus.Booking).Should().BeEquivalentTo(
            [ShipmentStatus.Booked, ShipmentStatus.BookingFailed],
            "a booking either produced an AWB or it did not — there is no third answer, and any "
          + "other exit would leave a real parcel moving with no record of it");

    [Fact]
    public void An_in_flight_booking_cannot_be_re_entered()
    {
        // TC-05.7 — the highest-consequence transition in the module. Re-entering BOOKING is
        // how a shipment gets booked twice and a second parcel gets paid for.
        Shipment.CanTransition(ShipmentStatus.Booking, ShipmentStatus.Booking).Should().BeFalse();

        var act = () => Shipment.EnsureCanTransition(ShipmentStatus.Booking, ShipmentStatus.Booking);
        act.Should().Throw<ConflictException>().WithMessage("*BOOKING*");
    }

    [Fact]
    public void An_in_flight_booking_cannot_be_cancelled()
    {
        // Cancelling a call whose outcome is unknown is how a parcel ends up in the carrier's
        // network with no shipment pointing at it. The ledger resolves BOOKING, not the user.
        Shipment.CanTransition(ShipmentStatus.Booking, ShipmentStatus.Cancelled).Should().BeFalse();
    }

    [Fact]
    public void A_failed_booking_can_be_retried() =>
        Shipment.CanTransition(ShipmentStatus.BookingFailed, ShipmentStatus.Booking).Should().BeTrue();

    // ── TC-05.8 — approval is conditional, not mandatory ──────────────────────

    [Fact]
    public void Staged_can_reach_goods_issue_with_and_without_approval()
    {
        Delivery.CanTransition(DeliveryStatus.Staged, DeliveryStatus.GoodsIssued).Should().BeTrue(
            "PENDING_APPROVAL is entered only when a shipping rule demands it");
        Delivery.CanTransition(DeliveryStatus.Staged, DeliveryStatus.PendingApproval).Should().BeTrue();
        Delivery.CanTransition(DeliveryStatus.PendingApproval, DeliveryStatus.GoodsIssued).Should().BeTrue();
    }

    [Fact]
    public void A_rejected_approval_returns_to_staged() =>
        Delivery.CanTransition(DeliveryStatus.PendingApproval, DeliveryStatus.Staged).Should().BeTrue();

    // ── TC-05.9 — nothing is cancellable once the stock has left ──────────────

    [Theory]
    [InlineData("GOODS_ISSUED")]
    [InlineData("IN_TRANSIT")]
    [InlineData("PARTIALLY_DELIVERED")]
    [InlineData("DELIVERED")]
    [InlineData("SHORT_CLOSED")]
    [InlineData("CLOSED")]
    public void A_delivery_cannot_be_cancelled_after_goods_issue(string statusCode)
    {
        var from = LogisticsCode.Parse<DeliveryStatus>(statusCode);

        Delivery.CanTransition(from, DeliveryStatus.Cancelled).Should().BeFalse(
            "the stock has already left the books — cancelling would leave the ledger asserting "
          + "a movement the document denies");
    }

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("RELEASED")]
    [InlineData("PICKING")]
    [InlineData("PICKED")]
    [InlineData("PACKED")]
    [InlineData("STAGED")]
    [InlineData("PENDING_APPROVAL")]
    [InlineData("ON_HOLD")]
    public void A_delivery_can_be_cancelled_before_goods_issue(string statusCode) =>
        Delivery.CanTransition(LogisticsCode.Parse<DeliveryStatus>(statusCode), DeliveryStatus.Cancelled)
                .Should().BeTrue();

    // ── Short-close ───────────────────────────────────────────────────────────

    [Fact]
    public void Short_close_requires_something_to_have_been_picked()
    {
        // Short-closing a delivery that picked nothing is a cancellation wearing another name,
        // and it would leave a "delivered short" document with no movement behind it.
        Delivery.CanTransition(DeliveryStatus.Released, DeliveryStatus.ShortClosed).Should().BeFalse();
        Delivery.CanTransition(DeliveryStatus.Picking,  DeliveryStatus.ShortClosed).Should().BeFalse();

        Delivery.CanTransition(DeliveryStatus.Picked,             DeliveryStatus.ShortClosed).Should().BeTrue();
        Delivery.CanTransition(DeliveryStatus.PartiallyDelivered, DeliveryStatus.ShortClosed).Should().BeTrue();
    }

    // ── Reachability ──────────────────────────────────────────────────────────

    private static void AssertEveryStatusIsReachable<TStatus>(IStateMachine<TStatus> machine, TStatus start)
        where TStatus : struct, Enum
    {
        var reached = new HashSet<TStatus> { start };
        var queue   = new Queue<TStatus>([start]);

        while (queue.Count > 0)
            foreach (var next in machine.From(queue.Dequeue()))
                if (reached.Add(next)) queue.Enqueue(next);

        var orphans = machine.AllStatuses.Except(reached).Select(LogisticsCode.Of).ToList();

        orphans.Should().BeEmpty(
            "a status no path can reach is dead vocabulary — either the table is missing a "
          + "transition or the status should not exist");
    }

    [Fact]
    public void Every_delivery_status_is_reachable_from_draft() =>
        AssertEveryStatusIsReachable(Delivery, DeliveryStatus.Draft);

    [Fact]
    public void Every_shipment_status_is_reachable_from_draft() =>
        AssertEveryStatusIsReachable(Shipment, ShipmentStatus.Draft);

    // ── EnsureCanTransition ───────────────────────────────────────────────────

    [Fact]
    public void Ensure_throws_a_conflict_naming_both_states_and_the_way_out()
    {
        var act = () => Delivery.EnsureCanTransition(DeliveryStatus.Draft, DeliveryStatus.GoodsIssued);

        act.Should().Throw<ConflictException>()
           .WithMessage("*DRAFT*")
           .WithMessage("*GOODS_ISSUED*")
           .WithMessage("*RELEASED*", "the message should tell the caller what it can do instead");
    }

    [Fact]
    public void Ensure_is_silent_when_the_move_is_legal()
    {
        var act = () => Delivery.EnsureCanTransition(DeliveryStatus.Draft, DeliveryStatus.Released);
        act.Should().NotThrow();
    }
}

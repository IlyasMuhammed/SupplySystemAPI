using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Logistics.Tests;

// T-13 — hold, resume, cancel and short close.
public class DeliveryStatusTests
{
    private const int User = 42;

    private sealed record Harness(
        LogisticsDbContext Db,
        DeliveryRepository Deliveries,
        DeliveryStatusRepository Status,
        FakeStockReservationService Reservations);

    private static Harness NewHarness()
    {
        var (db, tenant, _) = LogisticsTestDb.New();
        var repo = new DeliveryRepository(
            db,
            new DocumentNumberGenerator(db, tenant),
            new AddressNormalizer(new FakeCityLookup()));

        var reservations = new FakeStockReservationService();
        return new Harness(db, repo, new DeliveryStatusRepository(db, reservations), reservations);
    }

    private static CreateDeliveryRequest NewRequest(decimal qty = 100m) => new()
    {
        SourceType = "MANUAL",
        Direction  = "OUTBOUND",
        Lines = [new CreateDeliveryLineRequest
        {
            ItemDescription = "4mm cable", UnitOfMeasure = "M", QtyOrdered = qty
        }]
    };

    private static async Task<Guid> NewDelivery(Harness h, string status = "DRAFT", decimal qty = 100m)
    {
        var uuid = await h.Deliveries.CreateAsync(NewRequest(qty), User);
        if (status != "DRAFT") await SetStatus(h, uuid, status);
        return uuid;
    }

    private static async Task SetStatus(Harness h, Guid uuid, string status, decimal delivered = 0m)
    {
        var delivery = await h.Db.DeliveryOrders.Include(d => d.Lines).SingleAsync(d => d.UUID == uuid);
        delivery.Status = status;
        foreach (var line in delivery.Lines) line.QtyDelivered = delivered;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
    }

    private static DeliveryReasonRequest Reason(string reason = "Customer asked us to wait") =>
        new() { Reason = reason };

    // ── TC-13.1 / TC-13.2 — hold and resume ──────────────────────────────────

    [Theory]
    [InlineData("RELEASED")]
    [InlineData("PICKING")]
    [InlineData("PICKED")]
    [InlineData("PACKED")]
    [InlineData("STAGED")]
    [InlineData("PENDING_APPROVAL")]
    public async Task A_delivery_in_progress_can_be_held_and_resumes_exactly_where_it_paused(string status)
    {
        var h    = NewHarness();
        var uuid = await NewDelivery(h, status);

        (await h.Status.HoldAsync(uuid, Reason("Site access blocked"), User)).Should().BeTrue();

        var held = await h.Db.DeliveryOrders.AsNoTracking().SingleAsync(d => d.UUID == uuid);
        held.Status.Should().Be("ON_HOLD");
        held.StatusBeforeHold.Should().Be(status);
        held.HoldReason.Should().Be("Site access blocked");
        held.HeldBy.Should().Be(User);
        held.HeldAt.Should().NotBeNull();

        (await h.Status.ResumeAsync(uuid, User)).Should().BeTrue();

        var resumed = await h.Db.DeliveryOrders.AsNoTracking().SingleAsync(d => d.UUID == uuid);
        resumed.Status.Should().Be(status, "a resume returns to the status the hold interrupted");
        resumed.StatusBeforeHold.Should().BeNull();
        resumed.HoldReason.Should().BeNull();
        resumed.HeldAt.Should().BeNull();
        resumed.HeldBy.Should().BeNull();
    }

    [Fact]
    public async Task Resuming_never_rewinds_a_released_delivery_to_draft()
    {
        // The specific regression: DRAFT is the only status a resume could safely guess at, and
        // guessing it would strip a released delivery of its released state while its stock is
        // still reserved.
        var h    = NewHarness();
        var uuid = await NewDelivery(h, "RELEASED");

        await h.Status.HoldAsync(uuid, Reason(), User);
        await h.Status.ResumeAsync(uuid, User);

        (await h.Db.DeliveryOrders.AsNoTracking().SingleAsync(d => d.UUID == uuid))
            .Status.Should().Be("RELEASED");
    }

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("GOODS_ISSUED")]
    [InlineData("IN_TRANSIT")]
    [InlineData("DELIVERED")]
    [InlineData("CANCELLED")]
    public async Task A_delivery_outside_the_execution_window_cannot_be_held(string status)
    {
        var h    = NewHarness();
        var uuid = await NewDelivery(h, status);

        var act = async () => await h.Status.HoldAsync(uuid, Reason(), User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage($"*{status}*");
    }

    [Fact]
    public async Task Resuming_a_delivery_that_is_not_held_is_refused()
    {
        var h    = NewHarness();
        var uuid = await NewDelivery(h, "PICKING");

        var act = async () => await h.Status.ResumeAsync(uuid, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*nothing to resume*");
    }

    [Fact]
    public async Task A_held_delivery_with_no_recorded_prior_status_refuses_to_guess()
    {
        // Backfilled or hand-edited rows can reach ON_HOLD without the field set. Picking a
        // status for them would be a guess with real consequences, so it asks for one instead.
        var h    = NewHarness();
        var uuid = await NewDelivery(h, "ON_HOLD");

        var act = async () => await h.Status.ResumeAsync(uuid, User);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*does not record the status*");
    }

    // ── TC-13.3 — a reason is always required ────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Holding_cancelling_and_short_closing_all_demand_a_reason(string? reason)
    {
        var h    = NewHarness();
        var held = await NewDelivery(h, "RELEASED");
        var open = await NewDelivery(h, "DRAFT");
        var part = await NewDelivery(h, "PICKED");

        var req = new DeliveryReasonRequest { Reason = reason! };

        (await ((Func<Task>)(() => h.Status.HoldAsync(held, req, User)))
            .Should().ThrowAsync<BadRequestException>()).WithMessage("*reason*");

        (await ((Func<Task>)(() => h.Status.CancelAsync(open, req, User)))
            .Should().ThrowAsync<BadRequestException>()).WithMessage("*reason*");

        (await ((Func<Task>)(() => h.Status.ShortCloseAsync(part, req, User)))
            .Should().ThrowAsync<BadRequestException>()).WithMessage("*reason*");
    }

    [Fact]
    public async Task A_reason_is_trimmed_before_it_is_stored()
    {
        var h    = NewHarness();
        var uuid = await NewDelivery(h);

        await h.Status.CancelAsync(uuid, Reason("  Duplicate request  "), User);

        (await h.Db.DeliveryOrders.AsNoTracking().SingleAsync(d => d.UUID == uuid))
            .ClosureReason.Should().Be("Duplicate request");
    }

    // ── TC-13.4 / TC-13.5 — cancel ───────────────────────────────────────────

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("RELEASED")]
    [InlineData("PICKING")]
    [InlineData("PICKED")]
    [InlineData("PACKED")]
    [InlineData("STAGED")]
    [InlineData("PENDING_APPROVAL")]
    [InlineData("ON_HOLD")]
    public async Task A_delivery_can_be_cancelled_until_its_stock_leaves(string status)
    {
        var h    = NewHarness();
        var uuid = await NewDelivery(h, status);

        (await h.Status.CancelAsync(uuid, Reason("Order withdrawn"), User)).Should().BeTrue();

        var cancelled = await h.Db.DeliveryOrders.AsNoTracking().SingleAsync(d => d.UUID == uuid);
        cancelled.Status.Should().Be("CANCELLED");
        cancelled.ClosureReason.Should().Be("Order withdrawn");
        cancelled.ClosedBy.Should().Be(User);
        cancelled.ClosedAt.Should().NotBeNull();
        cancelled.IsActive.Should().BeFalse();
    }

    [Theory]
    [InlineData("GOODS_ISSUED")]
    [InlineData("IN_TRANSIT")]
    [InlineData("PARTIALLY_DELIVERED")]
    [InlineData("DELIVERED")]
    [InlineData("CLOSED")]
    public async Task A_delivery_cannot_be_cancelled_once_its_stock_has_left(string status)
    {
        // Cancelling would leave the ledger asserting a movement the document denies.
        var h    = NewHarness();
        var uuid = await NewDelivery(h, status);

        var act = async () => await h.Status.CancelAsync(uuid, Reason(), User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage($"*{status}*");
    }

    [Fact]
    public async Task Cancelling_a_held_delivery_clears_the_hold()
    {
        var h    = NewHarness();
        var uuid = await NewDelivery(h, "RELEASED");
        await h.Status.HoldAsync(uuid, Reason(), User);

        await h.Status.CancelAsync(uuid, Reason("Not needed after all"), User);

        var cancelled = await h.Db.DeliveryOrders.AsNoTracking().SingleAsync(d => d.UUID == uuid);
        cancelled.Status.Should().Be("CANCELLED");
        cancelled.StatusBeforeHold.Should().BeNull("a cancelled delivery has nothing to resume to");
        cancelled.HoldReason.Should().BeNull();
    }

    // ── TC-13.6 / TC-13.7 — short close ──────────────────────────────────────

    [Fact]
    public async Task Short_closing_records_the_shortfall_on_every_line()
    {
        var h    = NewHarness();
        var uuid = await NewDelivery(h, "DRAFT", qty: 100m);
        await SetStatus(h, uuid, "PARTIALLY_DELIVERED", delivered: 60m);

        (await h.Status.ShortCloseAsync(uuid, Reason("Supplier could not fulfil the balance"), User))
            .Should().BeTrue();

        var closed = await h.Db.DeliveryOrders
            .Include(d => d.Lines).AsNoTracking().SingleAsync(d => d.UUID == uuid);

        closed.Status.Should().Be("SHORT_CLOSED");
        closed.Lines.Single().QtyShort.Should().Be(40m);
        closed.Lines.Single().ShortReason.Should().Be("Supplier could not fulfil the balance");
        closed.ClosureReason.Should().Be("Supplier could not fulfil the balance");
    }

    [Fact]
    public async Task A_line_delivered_in_full_records_no_shortfall()
    {
        var h    = NewHarness();
        var uuid = await NewDelivery(h, "DRAFT", qty: 100m);
        await SetStatus(h, uuid, "PARTIALLY_DELIVERED", delivered: 100m);

        await h.Status.ShortCloseAsync(uuid, Reason("Closing the balance"), User);

        var closed = await h.Db.DeliveryOrders
            .Include(d => d.Lines).AsNoTracking().SingleAsync(d => d.UUID == uuid);

        closed.Lines.Single().QtyShort.Should().Be(0m);
        closed.Lines.Single().ShortReason.Should().BeNull();
    }

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("RELEASED")]
    [InlineData("PICKING")]
    public async Task A_delivery_that_has_picked_nothing_cannot_be_short_closed(string status)
    {
        // Short-closing a delivery that picked nothing is a cancellation wearing another name,
        // and would leave a "delivered short" document with no movement behind it.
        var h    = NewHarness();
        var uuid = await NewDelivery(h, status);

        var act = async () => await h.Status.ShortCloseAsync(uuid, Reason(), User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage($"*{status}*");
    }

    [Theory]
    [InlineData("PICKED")]
    [InlineData("PACKED")]
    [InlineData("STAGED")]
    [InlineData("PARTIALLY_DELIVERED")]
    public async Task A_delivery_that_has_picked_something_can_be_short_closed(string status)
    {
        var h    = NewHarness();
        var uuid = await NewDelivery(h, status);

        var act = async () => await h.Status.ShortCloseAsync(uuid, Reason(), User);

        await act.Should().NotThrowAsync();
    }

    // ── Not found ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Every_operation_reports_not_found_for_an_unknown_delivery()
    {
        var h       = NewHarness();
        var unknown = Guid.NewGuid();

        (await h.Status.HoldAsync(unknown, Reason(), User)).Should().BeFalse();
        (await h.Status.ResumeAsync(unknown, User)).Should().BeFalse();
        (await h.Status.CancelAsync(unknown, Reason(), User)).Should().BeFalse();
        (await h.Status.ShortCloseAsync(unknown, Reason(), User)).Should().BeFalse();
    }

    [Fact]
    public async Task A_soft_deleted_delivery_is_invisible_to_every_operation()
    {
        var h    = NewHarness();
        var uuid = await NewDelivery(h);
        await h.Deliveries.DeleteAsync(uuid);

        (await h.Status.CancelAsync(uuid, Reason(), User)).Should().BeFalse();
        (await h.Status.HoldAsync(uuid, Reason(), User)).Should().BeFalse();
    }

    // ── The detail keeps advertising the truth ───────────────────────────────

    [Fact]
    public async Task A_held_delivery_advertises_only_what_it_can_actually_do()
    {
        var h    = NewHarness();
        var uuid = await NewDelivery(h, "PACKED");
        await h.Status.HoldAsync(uuid, Reason(), User);

        var detail = await h.Deliveries.GetByUuidAsync(uuid);

        detail!.Status.Should().Be("ON_HOLD");
        detail.StatusBeforeHold.Should().Be("PACKED");
        detail.AllowedNextStatuses.Should().Contain("PACKED").And.Contain("CANCELLED");
        detail.AllowedNextStatuses.Should().NotContain("DRAFT");
    }
}

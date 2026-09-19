using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Services;
using SMS.Shared.Common;
using SMS.WorkflowEngine.Models;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>A29-P6-06 §7.6 — what a delivery reaching the customer does to its sale order: the
/// status re-derived from the lines' fulfilled_qty (credited at goods issue, P6-03), and on the last
/// one SO_FULFILLED on the timeline plus the fulfilment email. TC-09's three-delivery order and
/// TC-14's event chain both end here.</summary>
public class SaleOrderFulfillmentServiceTests
{
    private const int Counter = 42;

    private sealed record Harness(
        DemandDbContext Db, SaleOrderFulfillmentService Service, Guid OrgId,
        Mock<ISaleOrderEmailService> Email, List<Job> CapturedJobs);

    private sealed record L(decimal Qty, decimal Fulfilled, string Status = "RESERVED");

    private static Harness NewHarness(Guid? orgId = null, string? dbName = null)
    {
        orgId  ??= Guid.NewGuid();
        dbName ??= Guid.NewGuid().ToString();
        var db = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(dbName).Options,
            new StaticTenantContext { OrganizationId = orgId.Value });

        var captured = new List<Job>();
        var jobs = new Mock<IBackgroundJobClient>();
        jobs.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()))
            .Callback<Job, IState>((job, _) => captured.Add(job))
            .Returns("fake-job-id");
        var email = new Mock<ISaleOrderEmailService>();

        return new Harness(db,
            new SaleOrderFulfillmentService(db, email.Object, jobs.Object, NullLogger<SaleOrderFulfillmentService>.Instance),
            orgId.Value, email, captured);
    }

    private static async Task<SaleOrder> SeedOrder(Harness h, string status = "CONFIRMED", params L[] lines)
    {
        var order = new SaleOrder
        {
            SoNumber = "SO-2026-00042", PartnerId = Guid.NewGuid(), OrderDate = DateTime.UtcNow.Date,
            CurrencyId = Guid.NewGuid(), Status = status, DeliveryMode = "SHIP", CreatedBy = 11, TraceId = Guid.NewGuid()
        };
        foreach (var l in lines.Length > 0 ? lines : [new L(100m, 100m)])
            order.Lines.Add(new SaleOrderLine
            {
                VariantUuid = Guid.NewGuid(), Quantity = l.Qty, UnitPrice = 10m, LineTotal = l.Qty * 10m,
                FulfilledQty = l.Fulfilled, FulfillmentMode = "IN_STOCK", Status = l.Status
            });
        h.Db.SaleOrders.Add(order);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
        return order;
    }

    private static DeliveryCompletion Completed(SaleOrder order, decimal delivered = 100m, string number = "DLV-2026-00003") =>
        new(order.UUID, Guid.NewGuid(), number,
            [new DeliveredLine(order.Lines.First().UUID, delivered)], Counter);

    private static Task<SaleOrder> Reload(Harness h, SaleOrder order) =>
        h.Db.SaleOrders.AsNoTracking().SingleAsync(o => o.UUID == order.UUID);

    private static List<(TimelineEvent Event, object[] Args)> Timeline(Harness h) =>
        h.CapturedJobs.Where(j => j.Method.Name == "AppendAsync").Select(j => ((TimelineEvent)j.Args[1], j.Args.ToArray())).ToList();

    // ── Status ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_first_delivery_of_part_of_the_order_makes_it_partially_fulfilled()
    {
        // TC-09: 40 of 100 delivered.
        var h  = NewHarness();
        var so = await SeedOrder(h, "CONFIRMED", new L(100m, 40m));

        var result = await h.Service.RecordDeliveryCompletedAsync(Completed(so, 40m));

        result.Should().BeEquivalentTo(new { Found = true, Status = "PARTIALLY_FULFILLED", BecameFulfilled = false, OrderedQty = 100m, FulfilledQty = 40m });
        (await Reload(h, so)).Status.Should().Be("PARTIALLY_FULFILLED");
        (await Reload(h, so)).ModifiedBy.Should().Be(Counter);
    }

    [Fact]
    public async Task The_delivery_that_completes_every_line_makes_the_order_fulfilled()
    {
        var h  = NewHarness();
        var so = await SeedOrder(h, "PARTIALLY_FULFILLED", new L(100m, 100m), new L(5m, 5m));

        var result = await h.Service.RecordDeliveryCompletedAsync(Completed(so, 30m));

        result.Status.Should().Be("FULFILLED");
        result.BecameFulfilled.Should().BeTrue();
        (await Reload(h, so)).Status.Should().Be("FULFILLED");
    }

    [Fact]
    public async Task A_line_still_short_holds_the_order_at_partially_fulfilled()
    {
        var h  = NewHarness();
        var so = await SeedOrder(h, "CONFIRMED", new L(100m, 100m), new L(5m, 3m));

        var result = await h.Service.RecordDeliveryCompletedAsync(Completed(so));

        result.Status.Should().Be("PARTIALLY_FULFILLED");
        result.FulfilledQty.Should().Be(103m);
        result.OrderedQty.Should().Be(105m);
    }

    [Fact]
    public async Task A_cancelled_line_is_not_waited_for()
    {
        var h  = NewHarness();
        var so = await SeedOrder(h, "CONFIRMED", new L(100m, 100m), new L(50m, 0m, Status: "CANCELLED"));

        var result = await h.Service.RecordDeliveryCompletedAsync(Completed(so));

        result.Status.Should().Be("FULFILLED");
        result.OrderedQty.Should().Be(100m, "the cancelled line is not part of what was owed");
    }

    [Fact]
    public async Task Over_delivery_counts_as_fulfilled_not_more()
    {
        var h  = NewHarness();
        var so = await SeedOrder(h, "CONFIRMED", new L(100m, 110m));

        var result = await h.Service.RecordDeliveryCompletedAsync(Completed(so, 110m));

        result.Status.Should().Be("FULFILLED");
        result.FulfilledQty.Should().Be(100m, "capped at what was ordered");
    }

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("FULFILLED")]
    [InlineData("INVOICED")]
    [InlineData("CLOSED")]
    [InlineData("CANCELLED")]
    public async Task An_order_not_being_fulfilled_is_left_exactly_as_it_is(string status)
    {
        var h  = NewHarness();
        var so = await SeedOrder(h, status, new L(100m, 100m));

        var result = await h.Service.RecordDeliveryCompletedAsync(Completed(so));

        result.Found.Should().BeTrue();
        result.Status.Should().Be(status);
        result.BecameFulfilled.Should().BeFalse();
        (await Reload(h, so)).Status.Should().Be(status);
        h.CapturedJobs.Should().BeEmpty();
        h.Email.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_delivery_that_credited_nothing_leaves_a_confirmed_order_confirmed()
    {
        var h  = NewHarness();
        var so = await SeedOrder(h, "CONFIRMED", new L(100m, 0m));

        var result = await h.Service.RecordDeliveryCompletedAsync(Completed(so, 0m));

        result.Status.Should().Be("CONFIRMED");
        h.CapturedJobs.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_or_another_organizations_order_is_reported_not_found()
    {
        var orgA = NewHarness();
        var so   = await SeedOrder(orgA);
        var orgB = NewHarness(Guid.NewGuid(), dbName: Guid.NewGuid().ToString());

        (await orgB.Service.RecordDeliveryCompletedAsync(Completed(so))).Found.Should().BeFalse();
        (await orgA.Service.RecordDeliveryCompletedAsync(Completed(so) with { SaleOrderUuid = Guid.NewGuid() })).Found.Should().BeFalse();
        (await Reload(orgA, so)).Status.Should().Be("CONFIRMED");
    }

    // ── The record of it (§13.3 / §7.6) ──────────────────────────────────────

    [Fact]
    public async Task Becoming_fulfilled_records_SO_FULFILLED_on_the_orders_trace_with_the_org_named_explicitly()
    {
        var h  = NewHarness();
        var so = await SeedOrder(h, "PARTIALLY_FULFILLED", new L(100m, 100m));

        await h.Service.RecordDeliveryCompletedAsync(Completed(so, 60m, "DLV-2026-00003"));

        var (evt, args) = Timeline(h).Should().ContainSingle().Subject;
        evt.EventType.Should().Be(SaleOrderTimelineEventTypes.SoFulfilled);
        evt.InterfaceCode.Should().Be("SO");
        evt.DocumentId.Should().Be(so.UUID);
        evt.DocumentNumber.Should().Be("SO-2026-00042");
        evt.PerformedBy.Should().Be(Counter);
        evt.Notes.Should().Contain("100 of 100").And.Contain("DLV-2026-00003").And.Contain("60");
        args[0].Should().Be(so.TraceId);
        args[2].Should().Be("DELIVERY");
        args[3].Should().Be("DLV-2026-00003");
        args[4].Should().Be(h.OrgId, "§13.7: the org travels with the job, not with a request that may not exist");
    }

    [Fact]
    public async Task Becoming_fulfilled_sends_the_fulfilment_email_once()
    {
        var h  = NewHarness();
        var so = await SeedOrder(h, "PARTIALLY_FULFILLED", new L(100m, 100m));

        await h.Service.RecordDeliveryCompletedAsync(Completed(so, 60m, "DLV-2026-00003"));

        h.Email.Verify(e => e.SendFulfilledAsync(so.UUID, "DLV-2026-00003", 60m), Times.Once);
        h.Email.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_partial_delivery_records_and_sends_nothing_yet()
    {
        // SO_FULFILLED means fulfilled; a delivery of part of the order is visible on the timeline
        // through the delivery's own trace, not as a fulfilment that has not happened.
        var h  = NewHarness();
        var so = await SeedOrder(h, "CONFIRMED", new L(100m, 40m));

        await h.Service.RecordDeliveryCompletedAsync(Completed(so, 40m));

        h.CapturedJobs.Should().BeEmpty();
        h.Email.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_second_report_after_fulfilment_does_not_fulfil_twice()
    {
        // A retried collection, or a late-closing delivery of an order already complete.
        var h  = NewHarness();
        var so = await SeedOrder(h, "PARTIALLY_FULFILLED", new L(100m, 100m));

        await h.Service.RecordDeliveryCompletedAsync(Completed(so));
        var again = await h.Service.RecordDeliveryCompletedAsync(Completed(so));

        again.Status.Should().Be("FULFILLED");
        again.BecameFulfilled.Should().BeFalse();
        Timeline(h).Should().ContainSingle();
        h.Email.Verify(e => e.SendFulfilledAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<decimal>()), Times.Once);
    }

    [Fact]
    public async Task Three_deliveries_take_an_order_from_confirmed_to_fulfilled_with_one_event()
    {
        // TC-09: 40 shipped, 30 shipped, 30 collected — each credited at its goods issue.
        var h  = NewHarness();
        var so = await SeedOrder(h, "CONFIRMED", new L(100m, 0m));
        var lineUuid = so.Lines.Single().UUID;

        async Task Credit(decimal qty)
        {
            var line = await h.Db.SaleOrderLines.SingleAsync(l => l.UUID == lineUuid);
            line.FulfilledQty += qty;
            await h.Db.SaveChangesAsync();
            h.Db.ChangeTracker.Clear();
        }

        await Credit(40m);
        (await h.Service.RecordDeliveryCompletedAsync(Completed(so, 40m, "DLV-2026-00001"))).Status.Should().Be("PARTIALLY_FULFILLED");
        await Credit(30m);
        (await h.Service.RecordDeliveryCompletedAsync(Completed(so, 30m, "DLV-2026-00002"))).Status.Should().Be("PARTIALLY_FULFILLED");
        await Credit(30m);
        var last = await h.Service.RecordDeliveryCompletedAsync(Completed(so, 30m, "DLV-2026-00003"));

        last.Status.Should().Be("FULFILLED");
        last.BecameFulfilled.Should().BeTrue();
        (await Reload(h, so)).Status.Should().Be("FULFILLED");
        Timeline(h).Should().ContainSingle().Which.Event.Notes.Should().Contain("DLV-2026-00003");
        h.Email.Verify(e => e.SendFulfilledAsync(so.UUID, "DLV-2026-00003", 30m), Times.Once);
    }
}

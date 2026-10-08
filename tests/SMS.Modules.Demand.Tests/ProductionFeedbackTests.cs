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

/// <summary>
/// A34 PE-05 Demand side (D-21, D-21a, D-23), T-C6-02/03: ISaleOrderProductionFeedback records a make-to-order production
/// outcome on the sale order line — the shortfall only for a final outcome (COMPLETED, or zero yield at QI), the timeline
/// entries, and the notifications to the SO creator — idempotently, so a replayed outcome changes and sends nothing.
/// </summary>
public class ProductionFeedbackTests
{
    private const int SoCreator = 42;

    private sealed record H(DemandDbContext Db, SaleOrderProductionFeedback Feedback, Guid Org, Guid So, Guid Line,
        List<Job> Jobs, List<NotificationRequest> Notifications)
    {
        public IEnumerable<TimelineEvent> Timeline => Jobs.SelectMany(j => j.Args.OfType<TimelineEvent>());
    }

    private static async Task<H> NewAsync()
    {
        var tenant = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var db = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);
        var order = new SaleOrder
        {
            SoNumber = "SO-2026-00007", PartnerId = Guid.NewGuid(), OrderDate = DateTime.UtcNow.Date, CurrencyId = Guid.NewGuid(),
            Status = "CONFIRMED", CreatedBy = SoCreator,
            Lines = { new SaleOrderLine { VariantUuid = Guid.NewGuid(), Quantity = 100m, UnitPrice = 1m, Status = "OPEN", FulfillmentMode = "MAKE_TO_ORDER" } }
        };
        db.SaleOrders.Add(order);
        await db.SaveChangesAsync();

        var jobs = new List<Job>();
        var jobClient = new Mock<IBackgroundJobClient>();
        jobClient.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>())).Callback<Job, IState>((j, _) => jobs.Add(j)).Returns("job");
        var notifications = new List<NotificationRequest>();
        var notifier = new Mock<INotificationService>();
        notifier.Setup(x => x.TryCreateAsync(It.IsAny<NotificationRequest>())).Callback<NotificationRequest>(notifications.Add).Returns(Task.CompletedTask);
        notifier.Setup(x => x.CreateAsync(It.IsAny<NotificationRequest>())).Callback<NotificationRequest>(notifications.Add).Returns(Task.CompletedTask);

        var feedback = new SaleOrderProductionFeedback(db, jobClient.Object, NullLogger<SaleOrderProductionFeedback>.Instance, notifier.Object);
        return new H(db, feedback, tenant.OrganizationId, order.UUID, order.Lines.Single().UUID, jobs, notifications);
    }

    private static ProductionOutcome Outcome(H h, decimal accepted, string? delivery = null, bool completed = true, bool zero = false) =>
        new(h.So, h.Line, Guid.NewGuid(), "PROD-2026-00003", 100m, accepted, delivery, zero, completed);

    private static async Task<decimal?> ShortfallAsync(H h) =>
        (await h.Db.SaleOrderLines.AsNoTracking().SingleAsync(l => l.UUID == h.Line)).ProductionShortfallQty;

    [Fact]
    public async Task T_C6_02_a_completed_order_short_of_the_line_sets_the_shortfall_and_tells_the_SO_creator()
    {
        var h = await NewAsync();

        await h.Feedback.RecordOutcomeAsync(h.Org, Outcome(h, 95m, delivery: "DLV-2026-00011"), 0);

        (await ShortfallAsync(h)).Should().Be(5m);
        h.Timeline.Select(e => e.EventType).Should().BeEquivalentTo(["SO_DELIVERY_FROM_PRODUCTION", "SO_PRODUCTION_SHORTFALL"]);
        h.Timeline.Single(e => e.EventType == "SO_DELIVERY_FROM_PRODUCTION").Notes.Should().Contain("DLV-2026-00011").And.Contain("PROD-2026-00003");
        h.Timeline.Single(e => e.EventType == "SO_PRODUCTION_SHORTFALL").Notes.Should().Contain("5");
        h.Notifications.Select(n => (n.Type, n.UserId)).Should().BeEquivalentTo(
            [("SO_DELIVERY_FROM_PRODUCTION", SoCreator), ("PROD_SHORTFALL", SoCreator)]);
    }

    [Fact]
    public async Task D_21a_a_replayed_outcome_changes_and_sends_nothing()
    {
        var h = await NewAsync();
        await h.Feedback.RecordOutcomeAsync(h.Org, Outcome(h, 95m, delivery: "DLV-2026-00011"), 0);
        h.Jobs.Clear();
        h.Notifications.Clear();

        await h.Feedback.RecordOutcomeAsync(h.Org, Outcome(h, 95m, delivery: null), 0);

        (await ShortfallAsync(h)).Should().Be(5m, "set, not added to");
        h.Timeline.Should().BeEmpty();
        h.Notifications.Should().BeEmpty();
    }

    [Fact]
    public async Task REV_02_an_early_delivery_before_completion_records_no_shortfall()
    {
        var h = await NewAsync();

        await h.Feedback.RecordOutcomeAsync(h.Org, Outcome(h, 40m, delivery: "DLV-2026-00012", completed: false), 0);

        (await ShortfallAsync(h)).Should().BeNull();
        h.Timeline.Select(e => e.EventType).Should().Equal("SO_DELIVERY_FROM_PRODUCTION");
        h.Notifications.Select(n => n.Type).Should().Equal("SO_DELIVERY_FROM_PRODUCTION");
    }

    [Fact]
    public async Task T_C6_03_zero_yield_makes_the_whole_line_short_and_is_sent_once()
    {
        var h = await NewAsync();

        await h.Feedback.RecordOutcomeAsync(h.Org, Outcome(h, 0m, completed: false, zero: true), 0);
        await h.Feedback.RecordOutcomeAsync(h.Org, Outcome(h, 0m, completed: false, zero: true), 0);

        (await ShortfallAsync(h)).Should().Be(100m);
        h.Timeline.Should().ContainSingle().Which.EventType.Should().Be("SO_PRODUCTION_SHORTFALL");
        h.Notifications.Should().ContainSingle().Which.Should().Match<NotificationRequest>(n => n.Type == "PROD_ZERO_YIELD" && n.UserId == SoCreator);
    }

    [Fact]
    public async Task A_full_yield_records_no_shortfall_and_clears_an_earlier_one()
    {
        var h = await NewAsync();
        await h.Feedback.RecordOutcomeAsync(h.Org, Outcome(h, 0m, completed: false, zero: true), 0);
        h.Notifications.Clear();

        await h.Feedback.RecordOutcomeAsync(h.Org, Outcome(h, 100m, delivery: "DLV-2026-00013"), 0);

        (await ShortfallAsync(h)).Should().BeNull();
        h.Notifications.Select(n => n.Type).Should().Equal("SO_DELIVERY_FROM_PRODUCTION");
    }

    [Fact]
    public async Task Another_organizations_or_an_unknown_order_is_left_alone()
    {
        var h = await NewAsync();

        await h.Feedback.RecordOutcomeAsync(Guid.NewGuid(), Outcome(h, 10m, delivery: "DLV-1"), 0);
        await h.Feedback.RecordOutcomeAsync(h.Org, Outcome(h, 10m, delivery: "DLV-1") with { SaleOrderUuid = Guid.NewGuid() }, 0);
        await h.Feedback.RecordOutcomeAsync(h.Org, Outcome(h, 10m, delivery: "DLV-1") with { SoLineUuid = Guid.NewGuid() }, 0);

        (await ShortfallAsync(h)).Should().BeNull();
        h.Timeline.Should().BeEmpty();
        h.Notifications.Should().BeEmpty();
    }
}

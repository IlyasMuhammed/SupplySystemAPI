using System.Reflection;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Material.Controllers;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Modules.Material.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Models;
using Xunit;

namespace SMS.Modules.Material.Tests;

/// <summary>
/// A34 PE-03/PE-04 Material side (D-20, D-21, analysis §4.7, API-CONTRACT §7/§8.4): the handoff from a make-to-order
/// production order to Logistics' delivery creator, its sweep, and "Create delivery now".
/// </summary>
public class ProductionDeliveryHandoffTests
{
    private const int PoCreator = 5;
    private const int Clicker   = 9;

    // ── Fakes ────────────────────────────────────────────────────────────────

    internal sealed class FakeCreator : IProductionDeliveryCreator
    {
        public readonly List<(Guid Org, ProductionDeliveryRequest Request, int User, Guid? Scope)> Calls = [];
        public Func<ProductionDeliveryRequest, ProductionDeliveryResult>? Respond;
        public Exception? Throw;
        public Func<Task>? During;
        private int _seq;

        public async Task<ProductionDeliveryResult> CreateForProductionOrderAsync(
            Guid organizationId, ProductionDeliveryRequest request, int userId, CancellationToken ct = default)
        {
            Calls.Add((organizationId, request, userId, HangfireTenantScope.OrganizationId));
            if (During is not null) await During();
            if (Throw is not null) throw Throw;
            return Respond?.Invoke(request) ?? Created(request.AcceptedQuantity, $"DLV-2026-{++_seq:D5}");
        }

        public static ProductionDeliveryResult Created(decimal qty, string number)
        {
            var d = new CreatedSaleOrderDelivery(Guid.NewGuid(), number, Guid.NewGuid(), "MFG_PICK_SHIP", "SHIP", null, 1);
            return new ProductionDeliveryResult(d, qty, null, new SaleOrderDeliveryRef(d.DeliveryUuid, number, "DRAFT"), [d]);
        }

        public static ProductionDeliveryResult Replay(Guid latestUuid, string latestNumber) =>
            new(null, 0m, "Nothing left to deliver.", new SaleOrderDeliveryRef(latestUuid, latestNumber, "DRAFT"), []);
    }

    internal sealed class FakeFeedback : ISaleOrderProductionFeedback
    {
        public readonly List<(Guid Org, ProductionOutcome Outcome)> Outcomes = [];
        public Task RecordOutcomeAsync(Guid organizationId, ProductionOutcome outcome, int userId, CancellationToken ct = default)
        {
            Outcomes.Add((organizationId, outcome));
            return Task.CompletedTask;
        }
    }

    internal sealed class FakeNotifications : INotificationService
    {
        public readonly List<NotificationRequest> Sent = [];
        public Task CreateAsync(NotificationRequest request) { Sent.Add(request); return Task.CompletedTask; }
        public Task TryCreateAsync(NotificationRequest request) { Sent.Add(request); return Task.CompletedTask; }
        public Task BroadcastAsync(string type, string category, string title, string message, int createdBy = 0) => Task.CompletedTask;
        public Task SendEmailAsync(string to, string subject, string htmlBody) => Task.CompletedTask;
    }

    internal sealed class Harness
    {
        public required MaterialDbContext Db;
        public required StaticTenantContext Tenant;
        public required FakeCreator Creator;
        public required FakeFeedback Feedback;
        public required FakeNotifications Notifications;
        public required List<Job> Jobs;
        public required ProductionDeliveryHandoff Handoff;

        public IEnumerable<TimelineEvent> Timeline => Jobs.SelectMany(j => j.Args).OfType<TimelineEvent>();
    }

    internal static Harness New(Guid? tenantOrg = null, bool superAdmin = false, string? dbName = null)
    {
        var tenant = new StaticTenantContext { OrganizationId = tenantOrg ?? Guid.NewGuid(), IsSuperAdmin = superAdmin };
        var db = new MaterialDbContext(new DbContextOptionsBuilder<MaterialDbContext>()
            .UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString()).Options, tenant);

        var jobs = new List<Job>();
        var client = new Mock<IBackgroundJobClient>();
        client.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()))
              .Callback<Job, IState>((job, _) => jobs.Add(job))
              .Returns("job");

        var creator = new FakeCreator();
        var feedback = new FakeFeedback();
        var notifications = new FakeNotifications();
        return new Harness
        {
            Db = db, Tenant = tenant, Creator = creator, Feedback = feedback, Notifications = notifications, Jobs = jobs,
            Handoff = new ProductionDeliveryHandoff(db, tenant, creator, feedback, notifications, client.Object)
        };
    }

    internal static async Task<ProductionOrder> SeedPo(
        Harness h, Guid? org = null, string status = ProductionOrderStatus.Completed, decimal planned = 100m,
        decimal accepted = 100m, bool pending = true, bool makeToOrder = true, Guid? output = null,
        DateTime? pendingSince = null)
    {
        var po = new ProductionOrder
        {
            OrganizationId       = org ?? h.Tenant.OrganizationId,
            ProductionNumber     = $"PROD-2026-{Random.Shared.Next(10000, 99999)}",
            ProductUuid          = Guid.NewGuid(),
            ProductVariantUuid   = Guid.NewGuid(),
            BomId                = 1,
            BomVersion           = 1,
            PlannedQuantity      = planned,
            AcceptedQuantity     = accepted,
            WarehouseUuid        = Guid.NewGuid(),
            OutputWarehouseUuid  = output,
            SourceType           = ProductionSourceType.SalesOrder,
            SourceUuid           = Guid.NewGuid(),
            SourceLineUuid       = Guid.NewGuid(),
            SourceReference      = "SO-2026-00340",
            FulfillmentRouteUuid = makeToOrder ? Guid.NewGuid() : null,
            RequiredDate         = DateTime.UtcNow.Date.AddDays(10),
            Status               = status,
            DeliveryCreationPendingSince = pending ? (pendingSince ?? DateTime.UtcNow.AddMinutes(-1)) : null,
            CreatedBy            = PoCreator
        };
        h.Db.ProductionOrders.Add(po);
        await h.Db.SaveChangesAsync();
        return po;
    }

    private static async Task<ProductionOrder> Reload(Harness h, Guid uuid)
    {
        h.Db.ChangeTracker.Clear();
        return await h.Db.ProductionOrders.IgnoreQueryFilters().SingleAsync(p => p.UUID == uuid);
    }

    // ── RunAsync: the FGR hook / sweep path ─────────────────────────────────

    [Fact]
    public async Task A_completed_po_gets_its_delivery_stamped_the_flag_cleared_and_the_outcome_reported_as_final()
    {
        // The caller's tenant is another organization (a super admin confirming the FGR): the PO's own org is used (R-11).
        var h = await Task.FromResult(New(superAdmin: true));
        var poOrg = Guid.NewGuid();
        var output = Guid.NewGuid();
        var po = await SeedPo(h, org: poOrg, output: output);

        var result = await h.Handoff.RunAsync(po.UUID, Clicker);

        result.Ran.Should().BeTrue();
        result.Failed.Should().BeFalse();
        result.QuantityCreated.Should().Be(100m);
        result.DeliveryNumber.Should().Be("DLV-2026-00001");

        var call = h.Creator.Calls.Should().ContainSingle().Subject;
        call.Org.Should().Be(poOrg, "the production order's own organization, never the caller's tenant");
        call.User.Should().Be(Clicker);
        call.Request.ProductionOrderUuid.Should().Be(po.UUID);
        call.Request.ProductionNumber.Should().Be(po.ProductionNumber);
        call.Request.SaleOrderUuid.Should().Be(po.SourceUuid!.Value);
        call.Request.SoLineUuid.Should().Be(po.SourceLineUuid!.Value);
        call.Request.RouteUuid.Should().Be(po.FulfillmentRouteUuid!.Value);
        call.Request.AcceptedQuantity.Should().Be(100m);
        call.Request.FallbackWarehouseUuid.Should().Be(output, "the output warehouse first");

        var stored = await Reload(h, po.UUID);
        stored.DeliveryOrderUuid.Should().Be(result.DeliveryUuid);
        stored.DeliveryNumber.Should().Be("DLV-2026-00001");
        stored.DeliveryCreationPendingSince.Should().BeNull();

        var outcome = h.Feedback.Outcomes.Should().ContainSingle().Subject;
        outcome.Org.Should().Be(poOrg);
        outcome.Outcome.Should().Be(new ProductionOutcome(
            po.SourceUuid!.Value, po.SourceLineUuid!.Value, po.UUID, po.ProductionNumber, 100m, 100m, "DLV-2026-00001",
            ZeroYield: false, Completed: true));

        h.Timeline.Should().ContainSingle(e => e.EventType == "PROD_DELIVERY_CREATED")
            .Which.Notes.Should().Contain("DLV-2026-00001").And.Contain("SO-2026-00340");
        h.Notifications.Sent.Should().BeEmpty("nothing short");
    }

    [Fact]
    public async Task With_no_output_warehouse_the_fallback_is_the_production_warehouse()
    {
        var h = New();
        var po = await SeedPo(h);

        await h.Handoff.RunAsync(po.UUID, Clicker);

        h.Creator.Calls.Single().Request.FallbackWarehouseUuid.Should().Be(po.WarehouseUuid);
    }

    [Fact]
    public async Task A_replay_stamps_the_earlier_delivery_clears_the_flag_and_reports_no_new_delivery()
    {
        var h = New();
        var po = await SeedPo(h);
        var earlier = Guid.NewGuid();
        h.Creator.Respond = _ => FakeCreator.Replay(earlier, "DLV-2026-00042");

        var result = await h.Handoff.RunAsync(po.UUID, Clicker);

        result.Ran.Should().BeTrue();
        result.QuantityCreated.Should().Be(0m);
        result.DeliveryUuid.Should().BeNull();
        result.LatestDeliveryNumber.Should().Be("DLV-2026-00042");
        result.SkippedReason.Should().Contain("Nothing left");

        var stored = await Reload(h, po.UUID);
        stored.DeliveryOrderUuid.Should().Be(earlier, "a lost reply is repaired by the replay");
        stored.DeliveryNumber.Should().Be("DLV-2026-00042");
        stored.DeliveryCreationPendingSince.Should().BeNull("any returned result clears the flag (REV-05)");

        h.Feedback.Outcomes.Single().Outcome.DeliveryNumber.Should().BeNull("only a call that created a delivery names one");
        h.Timeline.Should().NotContain(e => e.EventType == "PROD_DELIVERY_CREATED");
    }

    [Fact]
    public async Task A_creator_failure_never_throws_and_leaves_the_po_pending_for_the_sweep()
    {
        var h = New();
        var po = await SeedPo(h);
        var since = (await Reload(h, po.UUID)).DeliveryCreationPendingSince;
        h.Creator.Throw = new TimeoutException("lock timeout");

        var result = await h.Handoff.RunAsync(po.UUID, Clicker);

        result.Failed.Should().BeTrue();
        result.Ran.Should().BeFalse();
        result.SkippedReason.Should().Contain("lock timeout");
        var stored = await Reload(h, po.UUID);
        stored.DeliveryCreationPendingSince.Should().Be(since);
        stored.DeliveryOrderUuid.Should().BeNull();
        h.Feedback.Outcomes.Should().BeEmpty();
        h.Timeline.Should().BeEmpty();
    }

    [Fact]
    public async Task A_shortfall_at_completion_notifies_the_po_creator_once_and_reports_the_final_quantity()
    {
        var h = New();
        var po = await SeedPo(h, planned: 100m, accepted: 95m);

        await h.Handoff.RunAsync(po.UUID, Clicker);

        var note = h.Notifications.Sent.Should().ContainSingle().Subject;
        note.Type.Should().Be("PROD_SHORTFALL");
        note.UserId.Should().Be(PoCreator, "D-23: Material tells the PO creator, Demand the SO creator");
        note.Message.Should().Contain(po.ProductionNumber).And.Contain("95").And.Contain("100");
        note.EntityUuid.Should().Be(po.UUID.ToString());
        h.Feedback.Outcomes.Single().Outcome.Completed.Should().BeTrue();
        h.Feedback.Outcomes.Single().Outcome.AcceptedQuantity.Should().Be(95m);

        // "Create delivery now" later on the same COMPLETED order: the flag is gone, so no second notification.
        h.Creator.Respond = _ => FakeCreator.Replay(Guid.NewGuid(), "DLV-2026-00001");
        await h.Handoff.RunAsync(po.UUID, Clicker);
        h.Notifications.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task An_early_delivery_mid_inspection_is_not_final_and_raises_no_shortfall()
    {
        var h = New();
        var po = await SeedPo(h, status: ProductionOrderStatus.QualityInspection, planned: 100m, accepted: 40m, pending: false);

        var result = await h.Handoff.RunAsync(po.UUID, Clicker);

        result.QuantityCreated.Should().Be(40m);
        h.Feedback.Outcomes.Single().Outcome.Completed.Should().BeFalse("REV-02: accepted is not final before COMPLETED");
        h.Notifications.Sent.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, 100)]   // A30 make-to-shortage / standalone PO: no route (BR-C6-02, T-C6-04)
    [InlineData(true, 0)]      // nothing accepted yet
    public async Task A_po_that_cannot_give_a_delivery_never_calls_logistics_and_stops_waiting(bool makeToOrder, int accepted)
    {
        var h = New();
        var po = await SeedPo(h, makeToOrder: makeToOrder, accepted: accepted);

        var result = await h.Handoff.RunAsync(po.UUID, Clicker);

        result.Ran.Should().BeFalse();
        result.Failed.Should().BeFalse();
        h.Creator.Calls.Should().BeEmpty();
        h.Feedback.Outcomes.Should().BeEmpty();
        (await Reload(h, po.UUID)).DeliveryCreationPendingSince.Should().BeNull("nothing will ever change, so the sweep stops");
    }

    [Fact]
    public async Task An_unknown_po_is_a_quiet_no_op()
    {
        var h = New();
        var result = await h.Handoff.RunAsync(Guid.NewGuid(), Clicker);
        result.Ran.Should().BeFalse();
        result.Failed.Should().BeFalse();
        h.Creator.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_logistics_the_handoff_does_nothing_and_stops_waiting()
    {
        var h = New();
        var po = await SeedPo(h);
        var bare = new ProductionDeliveryHandoff(h.Db, h.Tenant);

        var result = await bare.RunAsync(po.UUID, Clicker);

        result.Ran.Should().BeFalse();
        result.Failed.Should().BeFalse();
        (await Reload(h, po.UUID)).DeliveryCreationPendingSince.Should().BeNull();
    }

    // ── "Create delivery now" (API-CONTRACT §7) ──────────────────────────────

    [Fact]
    public async Task Create_now_runs_the_handoff_and_returns_the_contract_model()
    {
        var h = New();
        var po = await SeedPo(h, status: ProductionOrderStatus.QualityInspection, accepted: 40m, pending: false);

        var model = await h.Handoff.CreateNowAsync(po.UUID, Clicker);

        model.ProductionOrderUuid.Should().Be(po.UUID);
        model.QuantityCreated.Should().Be(40m);
        model.DeliveryNumber.Should().Be("DLV-2026-00001");
        model.DeliveryUuid.Should().NotBeNull();
        model.LatestDeliveryNumber.Should().Be("DLV-2026-00001");
        model.LatestDeliveryUuid.Should().Be(model.DeliveryUuid);
        model.SkippedReason.Should().BeNull();
        h.Creator.Calls.Single().User.Should().Be(Clicker);
    }

    [Fact]
    public async Task Create_now_on_another_organizations_po_is_a_404_for_a_super_admin_too()
    {
        var h = New(superAdmin: true);
        var po = await SeedPo(h, org: Guid.NewGuid());

        var act = () => h.Handoff.CreateNowAsync(po.UUID, Clicker);

        await act.Should().ThrowAsync<NotFoundException>();
        h.Creator.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_now_refuses_a_po_that_is_not_made_to_order_or_has_nothing_accepted()
    {
        var h = New();
        var standalone = await SeedPo(h, makeToOrder: false);
        var empty      = await SeedPo(h, status: ProductionOrderStatus.QualityInspection, accepted: 0m, pending: false);

        (await FluentActions.Invoking(() => h.Handoff.CreateNowAsync(standalone.UUID, Clicker))
                .Should().ThrowAsync<BadRequestException>())
            .Which.Message.Should().Be($"{standalone.ProductionNumber} is not made to order for a sale order, so no delivery is created from it.");
        (await FluentActions.Invoking(() => h.Handoff.CreateNowAsync(empty.UUID, Clicker))
                .Should().ThrowAsync<BadRequestException>())
            .Which.Message.Should().Be($"{empty.ProductionNumber} has no accepted quantity yet.");
        h.Creator.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_now_twice_is_a_200_with_nothing_created_the_second_time()
    {
        var h = New();
        var po = await SeedPo(h, pending: false);
        await h.Handoff.CreateNowAsync(po.UUID, Clicker);
        h.Creator.Respond = _ => FakeCreator.Replay(Guid.NewGuid(), "DLV-2026-00001");

        var again = await h.Handoff.CreateNowAsync(po.UUID, Clicker);

        again.QuantityCreated.Should().Be(0m);
        again.DeliveryUuid.Should().BeNull();
        again.SkippedReason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Create_now_turns_an_unexpected_failure_into_a_409_with_the_reason()
    {
        var h = New();
        var po = await SeedPo(h, pending: false);
        h.Creator.Throw = new InvalidOperationException("database unavailable");

        (await FluentActions.Invoking(() => h.Handoff.CreateNowAsync(po.UUID, Clicker))
                .Should().ThrowAsync<ConflictException>())
            .Which.Message.Should().Contain(po.ProductionNumber).And.Contain("database unavailable");
    }

    [Fact]
    public void The_endpoint_is_a_post_gated_by_delivery_create_behind_the_manufacturing_feature()
    {
        var action = typeof(ProductionOrdersController).GetMethod("CreateDelivery")!;
        action.GetCustomAttribute<HttpPostAttribute>()!.Template.Should().Be("{uuid:guid}/create-delivery");
        action.GetCustomAttribute<RequirePermissionAttribute>()!.AnyOf.Should().Equal([PermissionCodes.DELIVERY_CREATE]);
        typeof(ProductionOrdersController).GetCustomAttribute<RequiresFeatureAttribute>()!.FeatureCode.Should().Be("MODULE_MANUFACTURING");
    }

    // ── The sweep (D-20 recovery) ────────────────────────────────────────────

    [Fact]
    public async Task The_sweep_settles_due_pos_per_organization_leaves_recent_ones_and_requeues_a_failure()
    {
        var dbName = Guid.NewGuid().ToString();
        var h = New(dbName: dbName);
        Guid orgA = Guid.NewGuid(), orgB = Guid.NewGuid();
        var old = DateTime.UtcNow.AddMinutes(-15);
        var dueA    = await SeedPo(h, org: orgA, pendingSince: old.AddMinutes(-1));
        var dueB    = await SeedPo(h, org: orgB, pendingSince: old);
        var recent  = await SeedPo(h, org: orgA, pendingSince: DateTime.UtcNow.AddMinutes(-2));
        var failing = await SeedPo(h, org: orgB, pendingSince: old.AddMinutes(-5));
        h.Creator.Respond = r => r.ProductionOrderUuid == failing.UUID
            ? throw new TimeoutException("still locked")
            : FakeCreator.Created(r.AcceptedQuantity, "DLV-2026-00077");
        h.Db.ChangeTracker.Clear();

        var settled = await new ProductionDeliverySweepJob(h.Db, h.Handoff).RunAsync();

        settled.Should().Be(2);
        h.Creator.Calls.Select(c => c.Request.ProductionOrderUuid)
            .Should().BeEquivalentTo([dueA.UUID, dueB.UUID, failing.UUID], "the 2-minute-old one is left for the FGR's own call");
        h.Creator.Calls.Should().OnlyContain(c => c.User == 0, "the system acts, not a person");
        h.Creator.Calls.Should().OnlyContain(c => c.Scope == c.Org, "each organization runs under its own HangfireTenantScope");
        HangfireTenantScope.OrganizationId.Should().BeNull();

        (await Reload(h, dueA.UUID)).DeliveryCreationPendingSince.Should().BeNull();
        (await Reload(h, dueB.UUID)).DeliveryCreationPendingSince.Should().BeNull();
        (await Reload(h, recent.UUID)).DeliveryCreationPendingSince.Should().NotBeNull();
        (await Reload(h, failing.UUID)).DeliveryCreationPendingSince.Should()
            .BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1), "a failure goes to the back of the queue");
    }

    [Fact]
    public async Task The_sweep_is_registered_every_fifteen_minutes()
    {
        ProductionDeliverySweepJob.Cron.Should().Be("*/15 * * * *");
        ProductionDeliverySweepJob.SweepDelay.Should().Be(TimeSpan.FromMinutes(10));
        ProductionDeliverySweepJob.RecurringJobId.Should().NotBeNullOrWhiteSpace();
        await Task.CompletedTask;
    }
}

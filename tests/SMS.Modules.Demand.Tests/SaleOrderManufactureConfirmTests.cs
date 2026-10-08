using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>
/// A34 PD-01/PD-03/PD-04 (D-1, D-2, D-17, D-17a, D-19), T-C5-01..05, T-C6-09: confirm splits an order into stock lines
/// (A32 reservation, A29/A30 deficit jobs, A33 deliveries — unchanged) and make-to-order lines (nothing reserved,
/// MAKE_TO_ORDER, no deficit job, no delivery; one DRAFT production order each, created after the commit and planned
/// after the lock, with the D-19 dates). Recovery: the pending flag, the button and the sweep.
/// </summary>
public class SaleOrderManufactureConfirmTests
{
    private const int User = A34SoHarness.User;
    private static DateTime Today => DateTime.UtcNow.Date;

    // ── T-C5-01: all stock, as before ──────────────────────────────────────

    [Fact]
    public async Task T_C5_01_an_all_stock_order_reserves_and_gets_its_deliveries_and_no_production()
    {
        var h = A34SoHarness.Create();
        var stock = Guid.NewGuid();
        h.Available[stock] = 100m;
        var so = await h.DraftAsync((stock, null));

        var result = (await h.Service.ConfirmWithResultAsync(so, User))!;

        h.Reserved.Should().ContainSingle().Which.Qty.Should().Be(10m);
        h.Calls.Should().Contain("create-deliveries");
        result.Deliveries.Should().ContainSingle();
        result.ProductionOrders.Should().BeEmpty();
        result.ProductionCreationFailed.Should().BeFalse();
        h.Production.CreateCalls.Should().BeEmpty();
        var saved = await h.ReadAsync(so);
        saved.ProductionCreationPendingSince.Should().BeNull();
        saved.Lines.Single().FulfillmentRouteCategory.Should().Be("STOCK");
    }

    // ── T-C5-02/04/05: all make to order ───────────────────────────────────

    [Fact]
    public async Task T_C5_02_04_05_a_make_to_order_line_reserves_nothing_and_gets_one_production_order_created_then_planned()
    {
        var h = A34SoHarness.Create();
        var mto = h.MakeToOrderVariant();
        h.Available[mto] = 30m;
        var so = await h.DraftAsync((mto, null));

        var result = (await h.Service.ConfirmWithResultAsync(so, User))!;

        h.Reserved.Should().BeEmpty("C-2: nothing is reserved for a make-to-order line");
        h.Calls.Should().NotContain("create-deliveries", "C-3: no delivery at confirm for an all make-to-order order");
        h.AutoPoJobs.Should().BeEmpty("C-1: no deficit job");

        var saved = await h.ReadAsync(so);
        saved.Status.Should().Be("CONFIRMED");
        var line = saved.Lines.Single();
        line.FulfillmentMode.Should().Be("MAKE_TO_ORDER");
        line.DeficitQty.Should().Be(10m);
        line.AvailableQtyAtConfirm.Should().Be(30m);
        line.Status.Should().Be("OPEN");
        line.FulfillmentRouteUuid.Should().Be(h.MfgShip.Uuid);
        line.FulfillmentRouteCategory.Should().Be("MANUFACTURE");
        saved.ProductionCreationPendingSince.Should().BeNull("creation ran and succeeded");

        var create = h.Production.CreateCalls.Should().ContainSingle().Subject;
        create.Org.Should().Be(h.OrgId);
        create.User.Should().Be(User);
        create.Request.SaleOrderUuid.Should().Be(so);
        create.Request.SoNumber.Should().Be(saved.SoNumber);
        create.Request.TraceId.Should().Be(saved.TraceId);
        create.Request.Priority.Should().Be(AllocationPriority.Normal);
        var pl = create.Request.Lines.Should().ContainSingle().Subject;
        pl.SoLineUuid.Should().Be(line.UUID);
        pl.VariantUuid.Should().Be(mto);
        pl.Quantity.Should().Be(10m, "D-1: the full quantity, whatever stock is free");
        pl.FulfillmentRouteUuid.Should().Be(h.MfgShip.Uuid);

        var po = h.Production.Pos.Single();
        h.Production.PlanCalls.Should().ContainSingle().Which.Uuids.Should().Equal(po.Uuid);
        po.Status.Should().Be("PLANNED");

        var made = result.ProductionOrders.Should().ContainSingle().Subject;
        made.ProductionOrderUuid.Should().Be(po.Uuid);
        made.ProductionNumber.Should().Be(po.Number);
        made.Created.Should().BeTrue();
        made.IsMakeToOrder.Should().BeTrue();
        made.SoLineUuid.Should().Be(line.UUID);
        made.LineNumber.Should().Be(1);
        made.FulfillmentRouteCode.Should().Be("MFG_PICK_SHIP");
        result.ProductionCreationFailed.Should().BeFalse();

        h.Allocation.Verify(a => a.RegisterDemandAsync(It.IsAny<AllocationDemandRegistration>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never, "D-17a: no SALES_ORDER demand at creation");
        h.Timeline.Select(e => e.EventType).Should().Contain("SO_PRODUCTION_CREATED");
        h.Timeline.Single(e => e.EventType == "SO_PRODUCTION_CREATED").Notes.Should().Contain(po.Number);
    }

    [Fact]
    public async Task T_C5_03_a_mixed_order_splits_into_reservation_delivery_deficit_job_and_production()
    {
        var h = A34SoHarness.Create();
        var stock = Guid.NewGuid();
        h.Available[stock] = 4m;
        var mto = h.MakeToOrderVariant();
        var so = await h.DraftAsync((stock, null), (mto, null));

        var result = (await h.Service.ConfirmWithResultAsync(so, User))!;

        var saved = await h.ReadAsync(so);
        var stockLine = saved.Lines.Single(l => l.VariantUuid == stock);
        var mtoLine = saved.Lines.Single(l => l.VariantUuid == mto);
        h.Reserved.Should().ContainSingle().Which.Should().Be((stock, 4m, (Guid?)stockLine.UUID));
        stockLine.FulfillmentMode.Should().Be("SPLIT");
        mtoLine.FulfillmentMode.Should().Be("MAKE_TO_ORDER");

        h.AutoPoJobs.Should().ContainSingle("D-2: only the stock line's deficit gets a job")
            .Which.Args.Should().Contain(stockLine.UUID);
        h.Calls.Should().Contain("create-deliveries");
        h.Production.CreateCalls.Single().Request.Lines.Select(l => l.SoLineUuid).Should().Equal(mtoLine.UUID);
        result.ProductionOrders.Should().ContainSingle();
        result.Deliveries.Should().NotBeEmpty();
    }

    [Fact]
    public async Task T_C6_09_a_line_overridden_to_a_stock_route_is_a_stock_line()
    {
        var h = A34SoHarness.Create();
        var mto = h.MakeToOrderVariant();
        h.Available[mto] = 50m;
        var so = await h.DraftAsync((mto, h.PickAndShip.Uuid));

        await h.Service.ConfirmWithResultAsync(so, User);

        h.Reserved.Should().ContainSingle();
        h.Calls.Should().Contain("create-deliveries");
        h.Production.CreateCalls.Should().BeEmpty();
        (await h.ReadAsync(so)).Lines.Single().FulfillmentRouteCategory.Should().Be("STOCK");
    }

    [Fact]
    public async Task Production_is_created_after_the_confirm_has_committed()
    {
        var h = A34SoHarness.Create();
        var mto = h.MakeToOrderVariant();
        var so = await h.DraftAsync((mto, null));
        string? statusSeen = null;
        h.Production.OnCreate = async () =>
        {
            await using var fresh = new DemandDbContext(
                new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(h.DbName).Options, new StaticTenantContext { OrganizationId = h.OrgId });
            statusSeen = await fresh.SaleOrders.Where(o => o.UUID == so).Select(o => o.Status).SingleAsync();
        };

        await h.Service.ConfirmWithResultAsync(so, User);

        statusSeen.Should().Be("CONFIRMED");
    }

    // ── PD-04 / D-19: planned dates ───────────────────────────────────────

    private static IReadOnlyList<LeadTimeComponentResult> Components(int mfg = 6, int buffer = 1, int qc = 1, int transfer = 1, int pick = 1, int ship = 3, int sales = 1) =>
    [
        new(LeadTimeComponentCode.Manufacturing, "Manufacturing", mfg, LeadTimeSource.Bom),
        new(LeadTimeComponentCode.MfgBuffer, "Manufacturing buffer", buffer, LeadTimeSource.OrgDefault),
        new(LeadTimeComponentCode.Qc, "QC", qc, LeadTimeSource.OrgDefault),
        new(LeadTimeComponentCode.Transfer, "Transfer", transfer, LeadTimeSource.OrgDefault),
        new(LeadTimeComponentCode.PickPack, "Pick & pack", pick, LeadTimeSource.OrgDefault),
        new(LeadTimeComponentCode.Shipping, "Shipping", ship, LeadTimeSource.OrgDefault),
        new(LeadTimeComponentCode.SalesBuffer, "Sales buffer", sales, LeadTimeSource.OrgDefault)
    ];

    private static async Task SetManualDateAsync(A34SoHarness h, Guid so, DateTime? date)
    {
        var line = await h.Db.SaleOrderLines.SingleAsync(l => l.SaleOrder.UUID == so);
        line.ManualDeliveryDate = date;
        await h.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task D_19_with_an_effective_date_the_order_is_required_by_it_less_the_post_production_days()
    {
        var h = A34SoHarness.Create();
        var mto = h.MakeToOrderVariant();
        h.LeadTimes.Components[mto] = Components();
        h.LevelDays.Days[mto] = 3;
        var so = await h.DraftAsync((mto, null));
        await SetManualDateAsync(h, so, Today.AddDays(30));

        await h.Service.ConfirmWithResultAsync(so, User);

        var call = h.LeadTimes.Calls.Single();
        call.Request.RouteUuid.Should().Be(h.MfgShip.Uuid);
        call.Request.Quantity.Should().Be(10m);
        var pl = h.Production.CreateCalls.Single().Request.Lines.Single();
        // post = pick 1 + ship 3 + sales 1 + QC 1 + transfer 1 = 7; start = required − (this level's 3 days + buffer 1).
        pl.RequiredDate.Should().Be(Today.AddDays(23));
        pl.PlannedStartDate.Should().Be(Today.AddDays(19));
    }

    [Fact]
    public async Task REV_08_a_long_component_wait_does_not_pull_the_start_to_today()
    {
        var h = A34SoHarness.Create();
        var mto = h.MakeToOrderVariant();
        h.LeadTimes.Components[mto] = Components(mfg: 40);   // BOM-aware total: 2 days here + a 38-day purchased component
        h.LevelDays.Days[mto] = 2;
        var so = await h.DraftAsync((mto, null));
        await SetManualDateAsync(h, so, Today.AddDays(60));

        await h.Service.ConfirmWithResultAsync(so, User);

        var pl = h.Production.CreateCalls.Single().Request.Lines.Single();
        pl.RequiredDate.Should().Be(Today.AddDays(53));
        pl.PlannedStartDate.Should().Be(Today.AddDays(50), "D-19: this level's days + buffer, so materials are needed by the start, not today");
    }

    [Fact]
    public async Task D_19_an_effective_date_too_close_is_held_at_today()
    {
        var h = A34SoHarness.Create();
        var mto = h.MakeToOrderVariant();
        h.LeadTimes.Components[mto] = Components();
        var so = await h.DraftAsync((mto, null));
        await SetManualDateAsync(h, so, Today.AddDays(3));

        await h.Service.ConfirmWithResultAsync(so, User);

        var pl = h.Production.CreateCalls.Single().Request.Lines.Single();
        pl.RequiredDate.Should().Be(Today);
        pl.PlannedStartDate.Should().Be(Today);
    }

    [Fact]
    public async Task D_19_without_a_date_it_is_required_after_the_manufacturing_total_and_starts_tomorrow()
    {
        var h = A34SoHarness.Create();
        var mto = h.MakeToOrderVariant();
        h.LeadTimes.Components[mto] = Components(mfg: 6);
        var so = await h.DraftAsync((mto, null));

        await h.Service.ConfirmWithResultAsync(so, User);

        var pl = h.Production.CreateCalls.Single().Request.Lines.Single();
        pl.RequiredDate.Should().Be(Today.AddDays(6));
        pl.PlannedStartDate.Should().Be(Today.AddDays(1));
        pl.RequiredDate.TimeOfDay.Should().Be(TimeSpan.Zero, "date-only");
    }

    [Fact]
    public async Task D_19_the_header_expected_date_stands_in_when_the_line_has_none()
    {
        var h = A34SoHarness.Create();
        var mto = h.MakeToOrderVariant();
        h.LeadTimes.Components[mto] = Components();
        var so = await h.DraftAsync("SHIP", Guid.NewGuid(), Today.AddDays(40), (mto, null));

        await h.Service.ConfirmWithResultAsync(so, User);

        h.Production.CreateCalls.Single().Request.Lines.Single().RequiredDate.Should().Be(Today.AddDays(33));
    }

    [Fact]
    public async Task A_calculator_failure_still_creates_the_order_with_plain_dates()
    {
        var h = A34SoHarness.Create();
        var mto = h.MakeToOrderVariant();
        h.LeadTimes.Throw = new InvalidOperationException("calculator down");
        var so = await h.DraftAsync((mto, null));

        var result = (await h.Service.ConfirmWithResultAsync(so, User))!;

        result.ProductionCreationFailed.Should().BeFalse();
        var pl = h.Production.CreateCalls.Single().Request.Lines.Single();
        pl.RequiredDate.Should().Be(Today);
        pl.PlannedStartDate.Should().Be(Today);
    }

    // ── failures and recovery ─────────────────────────────────────────────

    [Fact]
    public async Task An_unexpected_failure_leaves_the_order_confirmed_and_pending_and_tells_the_user_once()
    {
        var h = A34SoHarness.Create();
        var mto = h.MakeToOrderVariant();
        h.Production.ThrowOnCreate = new InvalidOperationException("Material is down");
        var so = await h.DraftAsync((mto, null));

        var result = (await h.Service.ConfirmWithResultAsync(so, User))!;

        result.Status.Should().Be("CONFIRMED");
        result.ProductionCreationFailed.Should().BeTrue();
        result.ProductionMessage.Should().Contain("Create production orders");
        var saved = await h.ReadAsync(so);
        saved.Status.Should().Be("CONFIRMED");
        saved.ProductionCreationPendingSince.Should().NotBeNull("the sweep retries it");
        h.Timeline.Select(e => e.EventType).Should().Contain("SO_PRODUCTION_FAILED");
        h.Notifications.Should().ContainSingle(n => n.Type == "SO_PRODUCTION_FAILED").Which.UserId.Should().Be(User);
    }

    [Fact]
    public async Task A_business_refusal_clears_the_flag_and_names_the_reason()
    {
        var h = A34SoHarness.Create();
        var mto = h.MakeToOrderVariant();
        h.Production.ThrowOnCreate = new BadRequestException("Widget — Red has no active BOM.");
        var so = await h.DraftAsync((mto, null));

        var result = (await h.Service.ConfirmWithResultAsync(so, User))!;

        result.ProductionCreationFailed.Should().BeTrue();
        result.ProductionMessage.Should().Contain("Widget — Red has no active BOM.");
        (await h.ReadAsync(so)).ProductionCreationPendingSince.Should().BeNull("a 400 will not fix itself; the button retries");
    }

    [Fact]
    public async Task A_planning_failure_keeps_the_created_orders_and_the_flag_for_the_sweep()
    {
        var h = A34SoHarness.Create();
        var mto = h.MakeToOrderVariant();
        h.Production.ThrowOnPlan = new InvalidOperationException("planning failed");
        var so = await h.DraftAsync((mto, null));

        var result = (await h.Service.ConfirmWithResultAsync(so, User))!;

        result.ProductionOrders.Should().ContainSingle();
        result.ProductionCreationFailed.Should().BeTrue();
        (await h.ReadAsync(so)).ProductionCreationPendingSince.Should().NotBeNull();
        h.Production.Pos.Single().Status.Should().Be("DRAFT");
    }

    [Fact]
    public async Task The_button_creates_once_plans_and_clears_and_a_second_press_creates_nothing()
    {
        var h = A34SoHarness.Create();
        var mto = h.MakeToOrderVariant();
        h.Production.ThrowOnCreate = new InvalidOperationException("Material is down");
        var so = await h.DraftAsync((mto, null));
        await h.Service.ConfirmWithResultAsync(so, User);
        h.Production.ThrowOnCreate = null;

        var first = (await h.Service.CreateProductionOrdersAsync(so, User))!;
        var second = (await h.Service.CreateProductionOrdersAsync(so, User))!;

        first.ProductionCreationFailed.Should().BeFalse();
        first.ProductionOrders.Should().ContainSingle().Which.Created.Should().BeTrue();
        second.ProductionOrders.Should().ContainSingle().Which.Created.Should().BeFalse();
        h.Production.Pos.Should().ContainSingle();
        h.Production.Pos.Single().Status.Should().Be("PLANNED");
        (await h.ReadAsync(so)).ProductionCreationPendingSince.Should().BeNull();
    }

    [Fact]
    public async Task The_button_needs_a_confirmed_order_manufacturing_and_the_callers_own_order()
    {
        var h = A34SoHarness.Create();
        var mto = h.MakeToOrderVariant();
        var so = await h.DraftAsync((mto, null));

        var draft = await FluentActions.Awaiting(() => h.Service.CreateProductionOrdersAsync(so, User)).Should().ThrowAsync<BadRequestException>();
        draft.Which.Message.Should().Be("Sale order SO-2026-00001 is DRAFT: production orders are created once it is confirmed.");

        await h.Service.ConfirmWithResultAsync(so, User);
        h.Tenants.Disable(h.OrgId, A34.Manufacturing);
        var off = await FluentActions.Awaiting(() => h.Service.CreateProductionOrdersAsync(so, User)).Should().ThrowAsync<BadRequestException>();
        off.Which.Message.Should().Be("Manufacturing is not enabled for your organization.");

        var other = A34SoHarness.Create(dbName: h.DbName);
        (await other.Service.CreateProductionOrdersAsync(so, User)).Should().BeNull();
    }

    [Fact]
    public async Task Without_Materials_production_service_a_make_to_order_order_is_not_confirmed()
    {
        var h = A34SoHarness.Create(production: false);
        var mto = h.MakeToOrderVariant();
        var so = await h.DraftAsync((mto, null));

        await FluentActions.Awaiting(() => h.Service.ConfirmWithResultAsync(so, User)).Should().ThrowAsync<BadRequestException>();
        (await h.ReadAsync(so)).Status.Should().Be("DRAFT");
        h.Reserved.Should().BeEmpty();
    }

    // ── the sweep (D-17, REV-05) ──────────────────────────────────────────

    private static SaleOrderProductionSweepJob Sweep(A34SoHarness h, bool production = true) =>
        new(h.Db, Mock.Of<Hangfire.IBackgroundJobClient>(), NullLogger<SaleOrderProductionSweepJob>.Instance,
            production ? h.Production : null, h.LeadTimes, h.Tenants);

    private static async Task<Guid> PendingConfirmedAsync(A34SoHarness h, TimeSpan age)
    {
        var mto = h.MakeToOrderVariant();
        h.Production.ThrowOnCreate = new InvalidOperationException("down");
        var so = await h.DraftAsync((mto, null));
        await h.Service.ConfirmWithResultAsync(so, User);
        h.Production.ThrowOnCreate = null;
        var order = await h.Db.SaleOrders.SingleAsync(o => o.UUID == so);
        order.ProductionCreationPendingSince = DateTime.UtcNow - age;
        await h.Db.SaveChangesAsync();
        h.Notifications.Clear();
        return so;
    }

    [Fact]
    public async Task The_sweep_finishes_orders_pending_ten_minutes_and_leaves_younger_ones()
    {
        var h = A34SoHarness.Create();
        var old = await PendingConfirmedAsync(h, TimeSpan.FromMinutes(11));
        var young = await PendingConfirmedAsync(h, TimeSpan.FromMinutes(2));

        var done = await Sweep(h).RunAsync();

        done.Should().Be(1);
        h.Production.Pos.Should().ContainSingle().Which.SaleOrder.Should().Be(old);
        h.Production.Pos.Single().Status.Should().Be("PLANNED");
        (await h.ReadAsync(old)).ProductionCreationPendingSince.Should().BeNull();
        (await h.ReadAsync(young)).ProductionCreationPendingSince.Should().NotBeNull();
        h.Production.CreateCalls.Last().User.Should().Be(0, "the sweep acts as the system");
    }

    [Fact]
    public async Task A_sweep_retry_that_fails_again_goes_to_the_back_of_the_queue_without_notifying_again()
    {
        var h = A34SoHarness.Create();
        var so = await PendingConfirmedAsync(h, TimeSpan.FromMinutes(30));
        h.Production.ThrowOnCreate = new InvalidOperationException("still down");

        var done = await Sweep(h).RunAsync();

        done.Should().Be(0);
        (await h.ReadAsync(so)).ProductionCreationPendingSince.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        h.Notifications.Should().BeEmpty("SO_PRODUCTION_FAILED went out once, at confirm");
    }

    [Fact]
    public async Task The_sweep_stops_waiting_for_an_organization_without_manufacturing_and_skips_cancelled_orders()
    {
        var h = A34SoHarness.Create();
        var so = await PendingConfirmedAsync(h, TimeSpan.FromMinutes(30));
        h.Tenants.Disable(h.OrgId, A34.Manufacturing);

        (await Sweep(h).RunAsync()).Should().Be(1);
        (await h.ReadAsync(so)).ProductionCreationPendingSince.Should().BeNull();
        h.Production.Pos.Should().BeEmpty();

        var h2 = A34SoHarness.Create();
        var cancelled = await PendingConfirmedAsync(h2, TimeSpan.FromMinutes(30));
        var order = await h2.Db.SaleOrders.SingleAsync(o => o.UUID == cancelled);
        order.Status = "CANCELLED";
        await h2.Db.SaveChangesAsync();
        await Sweep(h2).RunAsync();
        h2.Production.Pos.Should().BeEmpty();
    }

    // ── the deficit job (D-2) ─────────────────────────────────────────────

    [Fact]
    public async Task D_2_the_deficit_job_skips_a_make_to_order_line_even_when_replayed()
    {
        var h = A34SoHarness.Create();
        var mto = h.MakeToOrderVariant();
        var so = await h.DraftAsync((mto, null));
        await h.Service.ConfirmWithResultAsync(so, User);
        var line = (await h.ReadAsync(so)).Lines.Single();
        var config = new Mock<ISaleOrderConfigService>();
        config.Setup(c => c.GetConfigAsync()).ReturnsAsync(new SaleOrderConfigModel { AutoPoEnabled = true });
        var selection = new Mock<ISupplierSelectionService>();
        var manufacturing = new Mock<ISaleOrderManufacturingService>();
        var job = new AutoPoCreationJob(h.Db, config.Object, selection.Object, Mock.Of<IAutoPurchaseOrderService>(),
            Mock.Of<ISaleOrderEmailService>(), NullLogger<AutoPoCreationJob>.Instance, manufacturing: manufacturing.Object);

        await job.CreateForDeficitAsync(so, line.UUID, User);

        selection.Verify(s => s.SelectAsync(It.IsAny<Guid>(), It.IsAny<decimal>(), It.IsAny<int>()), Times.Never);
        manufacturing.Verify(m => m.FulfillDeficitAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

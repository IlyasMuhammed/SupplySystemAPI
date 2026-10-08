using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Material.Domain;
using SMS.Modules.Material.Models;
using SMS.Modules.Material.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Material.Tests.RouteClassification;

/// <summary>
/// A34 PE-03 / D-17a / PE-05 (Material side) — the FGR completion hook (pending flag in the completing commit, the line's
/// SALES_ORDER demand registered up to what production received, then the delivery handoff with nothing held) and the QI
/// zero-yield hook. BR-C6-02: a standalone or A30 make-to-shortage PO never hands off. T-C6-01/03/04 (MFG side).
/// </summary>
public class FgrMakeToOrderHookTests
{
    private static readonly DateTime Today = A34Harness.Today;

    private sealed class FakeHandoff : IProductionDeliveryHandoff
    {
        private readonly Func<Guid, Task> _onRun;
        public readonly List<Guid> Runs = [];
        public FakeHandoff(Func<Guid, Task> onRun) => _onRun = onRun;

        public async Task<ProductionDeliveryHandoffResult> RunAsync(Guid productionOrderUuid, int userId, CancellationToken ct = default)
        {
            Runs.Add(productionOrderUuid);
            await _onRun(productionOrderUuid);
            return new ProductionDeliveryHandoffResult(true, 0m, null, null, null, null, null, false);
        }

        public Task<ProductionDeliveryHandoffModel> CreateNowAsync(Guid productionOrderUuid, int userId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeFeedback : ISaleOrderProductionFeedback
    {
        public readonly List<(Guid Org, ProductionOutcome Outcome)> Calls = [];
        public Task RecordOutcomeAsync(Guid organizationId, ProductionOutcome outcome, int userId, CancellationToken ct = default)
        {
            Calls.Add((organizationId, outcome));
            return Task.CompletedTask;
        }
    }

    private sealed class World
    {
        public A34Harness H { get; }
        public DemandDbContext Demand { get; }
        public FakeHandoff Handoff { get; }
        public FakeFeedback Feedback { get; } = new();
        public FakeRouteLookup Routes { get; } = new();
        public Guid Route { get; private set; }
        public Guid Kit { get; }
        public Guid KitV { get; }
        public Guid RodV { get; }
        /// <summary>At each handoff call: was the pending flag set, and what did the line's demand hold?</summary>
        public readonly List<(DateTime? Pending, decimal? DemandReserved)> SeenAtHandoff = [];

        public World()
        {
            var tenant = new StaticTenantContext();
            DemandDbContext? demand = null;
            FakeHandoff? handoff = null;
            var feedback = Feedback;
            var routes = Routes;
            H = new A34Harness(s =>
            {
                s.AddSingleton(sp => demand!);
                s.AddSingleton<IProductionDeliveryHandoff>(sp => handoff!);
                s.AddSingleton<ISaleOrderProductionFeedback>(feedback);
                s.AddSingleton<IFulfillmentRouteLookup>(routes);
                s.AddSingleton<ISaleOrderProductionService, SaleOrderProductionService>();
            });
            Demand = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, H.Tenant);
            demand = Demand;
            Handoff = new FakeHandoff(async uuid =>
            {
                var po = H.Material.ProductionOrders.IgnoreQueryFilters().AsNoTracking().Single(p => p.UUID == uuid);
                var demands = await H.Get<IAllocationEngine>().GetDemandsAsync(demandType: AllocationDemandType.SalesOrder, demandUuid: po.SourceUuid);
                SeenAtHandoff.Add((po.DeliveryCreationPendingSince, demands.SingleOrDefault()?.ReservedQty));
            });
            handoff = Handoff;

            Route = Routes.Add(H.Org, FulfillmentRouteCategory.Manufacture);
            (Kit, KitV) = H.Product("Bolt Kit", manufactured: true);
            var (_, rodV) = H.Product("Steel Rod", manufactured: false);
            RodV = rodV;
            H.ActiveBomAsync(Kit, 1m, (RodV, 1m, 0m)).GetAwaiter().GetResult();
            H.Stock(RodV, H.Plant, 1000m);
        }

        public SaleOrder SaleOrder(decimal quantity, SaleOrderStatus status = SaleOrderStatus.Confirmed, decimal fulfilled = 0m)
        {
            var so = new SaleOrder
            {
                OrganizationId = H.Org, SoNumber = $"SO-{Guid.NewGuid():N}"[..12], TraceId = Guid.NewGuid(), OrderDate = Today,
                Status = EnumCode<SaleOrderStatus>.Of(status), CreatedBy = 3,
                Lines = [new SaleOrderLine { OrganizationId = H.Org, VariantUuid = KitV, Quantity = quantity, FulfilledQty = fulfilled, FulfillmentMode = "MAKE_TO_ORDER" }]
            };
            Demand.SaleOrders.Add(so);
            Demand.SaveChanges();
            return so;
        }

        /// <summary>A make-to-order PO for the order's line, taken through plan, start, output and complete (QUALITY_INSPECTION).</summary>
        public async Task<Guid> MakeToOrderInQiAsync(SaleOrder so, decimal quantity)
        {
            var po = (await H.Get<ISaleOrderProductionService>().CreateDraftsAsync(H.Org,
                new SaleOrderProductionRequest(so.UUID, so.SoNumber, so.TraceId, AllocationPriority.Normal,
                    [new SaleOrderProductionLine(so.Lines.First().UUID, KitV, quantity, Route, Today.AddDays(5), null)]),
                A34Harness.Author))[0].ProductionOrderUuid;
            await ThroughToQiAsync(po, quantity, plan: true);
            return po;
        }

        public async Task ThroughToQiAsync(Guid po, decimal quantity, bool plan)
        {
            if (plan) await H.Orders.PlanAsync(po, A34Harness.Author);
            await H.Orders.StartAsync(po, A34Harness.Author);
            await H.Orders.ReportOutputAsync(po, new ReportOutputRequest { Quantity = quantity }, A34Harness.Author);
            await H.Orders.CompleteAsync(po, A34Harness.Author);
        }

        public Task Inspect(Guid po, decimal pass, decimal fail = 0m)
        {
            var lines = new List<CreateQualityInspectionLineRequest>();
            if (pass > 0) lines.Add(new() { CheckName = "Visual", Result = "PASS", QuantityChecked = pass });
            if (fail > 0) lines.Add(new() { CheckName = "Torque", Result = "FAIL", QuantityChecked = fail });
            return H.Qi.CreateAsync(po, new CreateQualityInspectionRequest { Lines = lines }, A34Harness.Operator);
        }

        public Task Receive(Guid po, decimal qty) =>
            H.Fgr.CreateAsync(po, new CreateFinishedGoodsReceiptRequest { Quantity = qty, Confirm = true }, A34Harness.Operator);

        public ProductionOrder Po(Guid uuid) => H.Material.ProductionOrders.IgnoreQueryFilters().AsNoTracking().Single(p => p.UUID == uuid);

        public async Task<DemandAllocationSummary?> LineDemandAsync(SaleOrder so) =>
            (await H.Get<IAllocationEngine>().GetDemandsAsync(demandType: AllocationDemandType.SalesOrder, demandUuid: so.UUID))
                .SingleOrDefault(d => d.DemandLineUuid == so.Lines.First().UUID);
    }

    // ── PE-03 / D-20 ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Completion_sets_the_pending_flag_in_its_commit_and_hands_off_once_after_allocation()
    {
        var w = new World();
        var so = w.SaleOrder(10m);
        var po = await w.MakeToOrderInQiAsync(so, 10m);
        await w.Inspect(po, pass: 10m);

        await w.Receive(po, 4m);
        w.Handoff.Runs.Should().BeEmpty("a partial receipt does not complete the order");
        w.Po(po).DeliveryCreationPendingSince.Should().BeNull();

        await w.Receive(po, 6m);

        w.Po(po).Status.Should().Be(ProductionOrderStatus.Completed);
        w.Handoff.Runs.Should().Equal(po);
        w.SeenAtHandoff.Should().ContainSingle();
        w.SeenAtHandoff[0].Pending.Should().NotBeNull("D-20: set in the same commit that turned the order COMPLETED");
        w.SeenAtHandoff[0].DemandReserved.Should().Be(10m, "the handoff runs after the post-commit allocation");
    }

    [Fact]
    public async Task A_standalone_or_make_to_shortage_po_never_hands_off_or_registers_a_line_demand()
    {
        var w = new World();
        // Standalone (MANUAL) order.
        var manual = await w.H.Orders.CreateAsync(new CreateProductionOrderRequest
        {
            ProductUuid = w.Kit, PlannedQuantity = 3m, RequiredDate = Today.AddDays(3), Plan = true
        }, A34Harness.Author);
        await w.ThroughToQiAsync(manual, 3m, plan: false);
        await w.Inspect(manual, pass: 3m);
        await w.Receive(manual, 3m);

        // A30 make-to-shortage order for a sale order line (no route).
        var so = w.SaleOrder(5m);
        var mts = await w.H.Get<IProductionDemandService>().EnsureForSourceAsync(w.KitV, 5m, Today.AddDays(3), 1,
            ProductionSourceType.SalesOrder, so.UUID, so.Lines.First().UUID, so.SoNumber, A34Harness.Author);
        await w.ThroughToQiAsync(mts, 5m, plan: false);
        await w.Inspect(mts, pass: 5m);
        await w.Receive(mts, 5m);

        w.Po(manual).Status.Should().Be(ProductionOrderStatus.Completed);
        w.Po(mts).Status.Should().Be(ProductionOrderStatus.Completed);
        w.Handoff.Runs.Should().BeEmpty("BR-C6-02");
        w.Po(manual).DeliveryCreationPendingSince.Should().BeNull();
        w.Po(mts).DeliveryCreationPendingSince.Should().BeNull();
        (await w.LineDemandAsync(so)).Should().BeNull("D-17a applies to make-to-order POs only");
    }

    // ── D-17a ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Each_receipt_extends_the_lines_demand_only_by_what_production_received_and_free_stock_cannot_overfill_it()
    {
        var w = new World();
        w.H.Stock(w.KitV, w.H.Plant, 500m); // plenty of free finished goods that must not be pulled onto the line
        var so = w.SaleOrder(10m);
        var po = await w.MakeToOrderInQiAsync(so, 10m);
        await w.Inspect(po, pass: 10m);

        await w.Receive(po, 4m);
        var demand = await w.LineDemandAsync(so);
        demand!.RequiredQty.Should().Be(4m);
        demand.ReservedQty.Should().Be(4m);
        demand.WarehouseUuid.Should().Be(w.H.Plant, "the receipt's warehouse");
        demand.Reference.Should().Be(so.SoNumber);

        // A later allocation run for the variant, with 500 free: still only what production received.
        await w.H.Get<IAllocationEngine>().AllocateAsync(w.KitV, null, A34Harness.Author);
        (await w.LineDemandAsync(so))!.ReservedQty.Should().Be(4m);

        await w.Receive(po, 6m);
        demand = await w.LineDemandAsync(so);
        demand!.RequiredQty.Should().Be(10m);
        demand.ReservedQty.Should().Be(10m);
    }

    [Fact]
    public async Task The_demand_never_exceeds_what_the_line_still_needs()
    {
        var w = new World();
        var so = w.SaleOrder(10m, fulfilled: 3m); // 3 already delivered some other way (the D-28 escape)
        var po = await w.MakeToOrderInQiAsync(so, 10m);
        await w.Inspect(po, pass: 10m);

        await w.Receive(po, 10m);

        (await w.LineDemandAsync(so))!.RequiredQty.Should().Be(7m);
    }

    [Fact]
    public async Task No_demand_is_registered_for_a_sale_order_that_is_no_longer_open_but_the_handoff_still_runs()
    {
        var w = new World();
        var so = w.SaleOrder(5m, SaleOrderStatus.Cancelled);
        var po = await w.MakeToOrderInQiAsync(so, 5m);
        await w.Inspect(po, pass: 5m);

        await w.Receive(po, 5m);

        (await w.LineDemandAsync(so)).Should().BeNull();
        w.Handoff.Runs.Should().Equal([po], "the creator itself finds the order cancelled and records why (D-22)");
    }

    [Fact]
    public async Task A_receipt_confirmed_from_another_organization_registers_no_demand_but_still_hands_off()
    {
        // REV-09 / R-11: the engine would stamp the demand with the caller's tenant, not the PO's.
        var w = new World();
        var so = w.SaleOrder(5m);
        var po = await w.MakeToOrderInQiAsync(so, 5m);
        await w.Inspect(po, pass: 5m);
        w.H.Tenant.OrganizationId = w.H.OtherOrg;   // a super admin of another organization
        w.H.Tenant.IsSuperAdmin   = true;

        await w.Receive(po, 5m);

        (await w.LineDemandAsync(so)).Should().BeNull();
        w.Handoff.Runs.Should().Equal([po], "the handoff works in the PO's own organization");
    }
    // ── PE-05 zero yield at QI (D-21) ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_qi_accepting_nothing_on_a_make_to_order_po_reports_zero_yield_once()
    {
        var w = new World();
        var so = w.SaleOrder(6m);
        var po = await w.MakeToOrderInQiAsync(so, 6m);

        await w.Inspect(po, pass: 0m, fail: 6m);

        w.Feedback.Calls.Should().ContainSingle();
        var (org, outcome) = w.Feedback.Calls[0];
        org.Should().Be(w.H.Org);
        outcome.Should().Be(new ProductionOutcome(so.UUID, so.Lines.First().UUID, po, w.Po(po).ProductionNumber,
            6m, 0m, null, ZeroYield: true, Completed: false));
        w.H.Notify.Verify(n => n.ProductionZeroYieldAsync(It.Is<ProductionOrder>(p => p.UUID == po)), Times.Once);
        w.Po(po).Status.Should().Be(ProductionOrderStatus.QualityInspection, "unchanged A30 behaviour");
    }

    [Fact]
    public async Task A_partial_yield_or_a_po_not_made_to_order_reports_nothing_at_qi()
    {
        var w = new World();
        var so = w.SaleOrder(6m);
        var po = await w.MakeToOrderInQiAsync(so, 6m);
        var mts = await w.H.Get<IProductionDemandService>().EnsureForSourceAsync(w.KitV, 2m, Today.AddDays(3), 1,
            ProductionSourceType.SalesOrder, so.UUID, Guid.NewGuid(), so.SoNumber, A34Harness.Author);
        await w.ThroughToQiAsync(mts, 2m, plan: false);

        await w.Inspect(po, pass: 1m, fail: 5m);
        await w.Inspect(mts, pass: 0m, fail: 2m);

        w.Feedback.Calls.Should().BeEmpty();
        w.H.Notify.Verify(n => n.ProductionZeroYieldAsync(It.IsAny<ProductionOrder>()), Times.Never);
    }

    // ── PE-02 / API-CONTRACT §7 — the production order models ─────────────────────────────────────────────

    [Fact]
    public async Task The_models_carry_route_names_line_number_delivery_and_pending_state()
    {
        var w = new World();
        var so = w.SaleOrder(10m);
        so.Lines.Add(new SaleOrderLine { OrganizationId = w.H.Org, VariantUuid = w.KitV, Quantity = 2m });
        w.Demand.SaveChanges();
        var second = so.Lines.OrderBy(l => l.Id).Last();
        var po = (await w.H.Get<ISaleOrderProductionService>().CreateDraftsAsync(w.H.Org,
            new SaleOrderProductionRequest(so.UUID, so.SoNumber, so.TraceId, 1,
                [new SaleOrderProductionLine(second.UUID, w.KitV, 2m, w.Route, Today.AddDays(5), null)]), A34Harness.Author))[0].ProductionOrderUuid;
        var tracked = w.H.Material.ProductionOrders.IgnoreQueryFilters().Single(p => p.UUID == po);
        tracked.DeliveryOrderUuid = Guid.NewGuid(); tracked.DeliveryNumber = "DLV-2026-00009"; tracked.DeliveryCreationPendingSince = DateTime.UtcNow;
        w.H.Material.SaveChanges();

        var detail = await w.H.Orders.GetByUuidAsync(po);
        var item   = (await w.H.Orders.GetListAsync(new ProductionOrderListFilter())).Data.Single(i => i.UUID == po);

        foreach (var m in new ProductionOrderListItemModel[] { detail!, item })
        {
            m.IsMakeToOrder.Should().BeTrue();
            m.FulfillmentRouteUuid.Should().Be(w.Route);
            m.FulfillmentRouteCode.Should().Be("MFG_PICK_SHIP");
            m.FulfillmentRouteName.Should().Be("MFG_PICK_SHIP");
            m.FulfillmentRouteCategory.Should().Be(FulfillmentRouteCategory.Manufacture);
            m.DeliveryOrderUuid.Should().Be(tracked.DeliveryOrderUuid);
            m.DeliveryNumber.Should().Be("DLV-2026-00009");
            m.DeliveryCreationPending.Should().BeTrue();
            m.SaleOrderLineNumber.Should().Be(2);
            m.ShortfallQuantity.Should().BeNull("not completed and no zero yield");
        }
    }

    [Fact]
    public async Task Shortfall_shows_once_completed_short_or_at_zero_yield_and_a_standalone_order_has_no_make_to_order_fields()
    {
        var w = new World();
        var short1 = await w.MakeToOrderInQiAsync(w.SaleOrder(10m), 10m);
        await w.Inspect(short1, pass: 8m, fail: 2m);
        await w.Receive(short1, 8m);
        var zero = await w.MakeToOrderInQiAsync(w.SaleOrder(4m), 4m);
        await w.Inspect(zero, pass: 0m, fail: 4m);
        var full = await w.MakeToOrderInQiAsync(w.SaleOrder(3m), 3m);
        await w.Inspect(full, pass: 3m);
        await w.Receive(full, 3m);
        var manual = await w.H.Orders.CreateAsync(new CreateProductionOrderRequest
        {
            ProductUuid = w.Kit, PlannedQuantity = 3m, RequiredDate = Today.AddDays(3)
        }, A34Harness.Author);

        (await w.H.Orders.GetByUuidAsync(short1))!.ShortfallQuantity.Should().Be(2m);
        (await w.H.Orders.GetByUuidAsync(zero))!.ShortfallQuantity.Should().Be(4m);
        (await w.H.Orders.GetByUuidAsync(full))!.ShortfallQuantity.Should().BeNull();
        var standalone = (await w.H.Orders.GetByUuidAsync(manual))!;
        standalone.IsMakeToOrder.Should().BeFalse();
        standalone.FulfillmentRouteUuid.Should().BeNull();
        standalone.FulfillmentRouteCode.Should().BeNull();
        standalone.SaleOrderLineNumber.Should().BeNull();
        standalone.ShortfallQuantity.Should().BeNull();
        standalone.DeliveryCreationPending.Should().BeFalse();
    }
}
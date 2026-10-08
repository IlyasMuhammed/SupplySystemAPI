using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SMS.Modules.Material.Domain;
using SMS.Modules.Material.Models;
using SMS.Modules.Material.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Material.Tests.RouteClassification;

/// <summary>
/// A34 PD-01 / PD-05 / PD-06 (Material side) — <see cref="ISaleOrderProductionService"/>: make-to-order DRAFT creation
/// (idempotent per line, org explicit, route stamped), planning of drafts only, the D-22 cancel cascade and the D-25 read.
/// T-C5-02/04/05, T-C5-07 (MFG).
/// </summary>
public class SaleOrderProductionServiceTests
{
    private static readonly DateTime Today = A34Harness.Today;

    private sealed class World
    {
        public A34Harness H { get; }
        public FakeRouteLookup Routes { get; } = new();
        public Guid Route { get; }
        public Guid Kit { get; }
        public Guid KitV { get; }
        public Guid RodV { get; }
        public ISaleOrderProductionService Service => H.Get<ISaleOrderProductionService>();

        public World()
        {
            var routes = Routes;
            H = new A34Harness(s =>
            {
                s.AddSingleton<IFulfillmentRouteLookup>(routes);
                s.AddSingleton<ISaleOrderProductionService, SaleOrderProductionService>();
            });
            Route = Routes.Add(H.Org, FulfillmentRouteCategory.Manufacture);
            (Kit, KitV) = H.Product("Bolt Kit", manufactured: true);
            var (_, rodV) = H.Product("Steel Rod", manufactured: false);
            RodV = rodV;
            H.ActiveBomAsync(Kit, 1m, (RodV, 2m, 0m)).GetAwaiter().GetResult();
        }

        public SaleOrderProductionRequest Request(Guid so, params (Guid Line, decimal Qty)[] lines) =>
            new(so, "SO-2026-00042", TraceId, AllocationPriority.High,
                lines.Select(l => new SaleOrderProductionLine(l.Line, KitV, l.Qty, Route, Today.AddDays(10), Today.AddDays(4))).ToList());

        public Guid TraceId { get; } = Guid.NewGuid();

        public ProductionOrder Po(Guid uuid) => H.Material.ProductionOrders.IgnoreQueryFilters().AsNoTracking().Single(p => p.UUID == uuid);
    }

    // ── Create ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task One_draft_per_line_carrying_the_route_the_sale_order_link_its_dates_priority_trace_and_org()
    {
        var w = new World();
        var so = Guid.NewGuid(); var l1 = Guid.NewGuid(); var l2 = Guid.NewGuid();

        var refs = await w.Service.CreateDraftsAsync(w.H.Org, w.Request(so, (l1, 5m), (l2, 3m)), A34Harness.Author);

        refs.Should().HaveCount(2).And.OnlyContain(r => r.Created && r.Status == ProductionOrderStatus.Draft && r.FulfillmentRouteUuid == w.Route);
        refs.Select(r => r.SoLineUuid).Should().Equal(l1, l2);
        var po = w.Po(refs[0].ProductionOrderUuid);
        po.OrganizationId.Should().Be(w.H.Org);
        po.SourceType.Should().Be(ProductionSourceType.SalesOrder);
        po.SourceUuid.Should().Be(so);
        po.SourceLineUuid.Should().Be(l1);
        po.SourceReference.Should().Be("SO-2026-00042");
        po.FulfillmentRouteUuid.Should().Be(w.Route);
        po.IsMakeToOrder.Should().BeTrue();
        po.PlannedQuantity.Should().Be(5m);
        po.RequiredDate.Should().Be(Today.AddDays(10));
        po.PlannedStartDate.Should().Be(Today.AddDays(4));
        po.Priority.Should().Be(AllocationPriority.High);
        po.TraceId.Should().Be(w.TraceId, "A30-P5-07: the SO's trace runs through its production orders");
        po.Status.Should().Be(ProductionOrderStatus.Draft);
        po.WarehouseUuid.Should().Be(w.H.Plant, "the product's default production warehouse");
        w.H.Notify.Verify(n => n.ProductionOrderCreatedAsync(It.Is<ProductionOrder>(p => p.UUID == po.UUID), "Bolt Kit", A34Harness.Author), Times.Once);
    }

    [Fact]
    public async Task Creating_again_returns_the_same_orders_and_creates_nothing()
    {
        var w = new World();
        var so = Guid.NewGuid(); var l1 = Guid.NewGuid();
        var first = await w.Service.CreateDraftsAsync(w.H.Org, w.Request(so, (l1, 5m)), A34Harness.Author);

        var again = await w.Service.CreateDraftsAsync(w.H.Org, w.Request(so, (l1, 5m), (l1, 5m)), A34Harness.Author);

        again.Should().ContainSingle().Which.Should().Be(first[0] with { Created = false });
        w.H.Material.ProductionOrders.IgnoreQueryFilters().Count().Should().Be(1);
    }

    [Fact]
    public async Task A_make_to_shortage_or_cancelled_order_on_the_same_line_is_not_the_lines_make_to_order_po()
    {
        var w = new World();
        var so = Guid.NewGuid(); var l1 = Guid.NewGuid();
        // A30 make-to-shortage for the same line: no route.
        var mts = await w.H.Get<IProductionDemandService>().EnsureForSourceAsync(w.KitV, 2m, Today.AddDays(3), AllocationPriority.Normal,
            ProductionSourceType.SalesOrder, so, l1, "SO-2026-00042", A34Harness.Author);
        var cancelled = (await w.Service.CreateDraftsAsync(w.H.Org, w.Request(so, (l1, 5m)), A34Harness.Author))[0];
        await w.H.Orders.CancelAsync(cancelled.ProductionOrderUuid, new CancelProductionOrderRequest { Reason = "test" }, A34Harness.Author);

        var refs = await w.Service.CreateDraftsAsync(w.H.Org, w.Request(so, (l1, 5m)), A34Harness.Author);

        refs.Should().ContainSingle().Which.Created.Should().BeTrue();
        refs[0].ProductionOrderUuid.Should().NotBe(mts).And.NotBe(cancelled.ProductionOrderUuid);
        w.Po(mts).FulfillmentRouteUuid.Should().BeNull("C-4: A30 make-to-shortage POs never carry a route");
    }

    [Fact]
    public async Task Dates_are_never_in_the_past_and_the_start_never_after_the_required_date()
    {
        var w = new World();
        var request = new SaleOrderProductionRequest(Guid.NewGuid(), "SO-1", Guid.NewGuid(), 99,
            [new SaleOrderProductionLine(Guid.NewGuid(), w.KitV, 1m, w.Route, Today.AddDays(-3), Today.AddDays(5))]);

        var po = w.Po((await w.Service.CreateDraftsAsync(w.H.Org, request, A34Harness.Author))[0].ProductionOrderUuid);

        po.RequiredDate.Should().Be(Today);
        po.PlannedStartDate.Should().Be(Today);
        po.Priority.Should().Be(AllocationPriority.Normal, "an unknown priority falls back to normal rather than failing the order");
    }

    [Fact]
    public async Task The_organization_is_explicit_another_orgs_variant_is_refused_and_their_own_call_is_stamped_with_their_org()
    {
        var w = new World();
        var theirRoute = w.Routes.Add(w.H.OtherOrg, FulfillmentRouteCategory.Manufacture);
        var (theirKit, theirKitV) = w.H.Product("Their Kit", manufactured: true, organizationId: w.H.OtherOrg);
        w.H.TheirPlant(theirKit);
        var (theirRod, theirRodV) = w.H.Product("Their Rod", manufactured: false, organizationId: w.H.OtherOrg);
        w.H.RawBom(theirKit, null, 1, organizationId: w.H.OtherOrg, lines: (theirRodV, theirRod, 1m, 0m));
        var line = new SaleOrderProductionLine(Guid.NewGuid(), theirKitV, 2m, theirRoute, Today.AddDays(5), null);

        // Asked for our org: their variant reads as absent.
        var mine = () => w.Service.CreateDraftsAsync(w.H.Org, new SaleOrderProductionRequest(Guid.NewGuid(), "SO-X", Guid.NewGuid(), 1,
            [line with { FulfillmentRouteUuid = w.Route }]), A34Harness.Author);
        await mine.Should().ThrowAsync<BadRequestException>();

        // Asked for theirs (a sweep, a super admin), it is created and stamped with their org, not the ambient tenant.
        var refs = await w.Service.CreateDraftsAsync(w.H.OtherOrg,
            new SaleOrderProductionRequest(Guid.NewGuid(), "SO-Y", Guid.NewGuid(), 1, [line]), A34Harness.Author);
        w.Po(refs[0].ProductionOrderUuid).OrganizationId.Should().Be(w.H.OtherOrg);
        w.H.Numbers.Verify(n => n.NextAsync(ManufacturingDocumentPrefix.ProductionOrder, It.IsAny<DateTime?>(), w.H.OtherOrg, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task The_route_must_be_a_make_to_order_route_of_the_organization()
    {
        var w = new World();
        var stock = w.Routes.Add(w.H.Org, FulfillmentRouteCategory.Stock, "PICK_SHIP");
        var foreign = w.Routes.Add(w.H.OtherOrg, FulfillmentRouteCategory.Manufacture);

        foreach (var route in new[] { stock, foreign, Guid.NewGuid() })
        {
            var request = new SaleOrderProductionRequest(Guid.NewGuid(), "SO-1", Guid.NewGuid(), 1,
                [new SaleOrderProductionLine(Guid.NewGuid(), w.KitV, 1m, route, Today.AddDays(5), null)]);
            var act = () => w.Service.CreateDraftsAsync(w.H.Org, request, A34Harness.Author);
            await act.Should().ThrowAsync<BadRequestException>();
        }
        w.H.Material.ProductionOrders.IgnoreQueryFilters().Should().BeEmpty();
    }

    // ── Plan ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Planning_explodes_drafts_and_skips_orders_that_are_no_longer_drafts_or_not_the_orgs()
    {
        var w = new World();
        var so = Guid.NewGuid();
        var refs = await w.Service.CreateDraftsAsync(w.H.Org, w.Request(so, (Guid.NewGuid(), 4m), (Guid.NewGuid(), 1m)), A34Harness.Author);
        await w.H.Orders.PlanAsync(refs[1].ProductionOrderUuid, A34Harness.Author); // already planned by hand

        await w.Service.PlanDraftsAsync(w.H.OtherOrg, [refs[0].ProductionOrderUuid], A34Harness.Author);
        w.Po(refs[0].ProductionOrderUuid).Status.Should().Be(ProductionOrderStatus.Draft, "another organization's call touches nothing");

        await w.Service.PlanDraftsAsync(w.H.Org, refs.Select(r => r.ProductionOrderUuid).ToList(), A34Harness.Author);

        var planned = await w.H.Material.ProductionOrders.IgnoreQueryFilters().Include(p => p.Materials)
            .SingleAsync(p => p.UUID == refs[0].ProductionOrderUuid);
        planned.Status.Should().NotBe(ProductionOrderStatus.Draft);
        planned.Materials.Should().ContainSingle().Which.RequiredQuantity.Should().Be(8m, "T-C5-05: PMRs exist after planning");
    }

    // ── Cancel cascade (D-22) ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancel_cancels_every_not_started_sales_order_po_and_reports_the_ones_kept_running()
    {
        var w = new World();
        w.H.Stock(w.RodV, w.H.Plant, 1000m);
        var so = Guid.NewGuid();
        var refs = await w.Service.CreateDraftsAsync(w.H.Org,
            w.Request(so, (Guid.NewGuid(), 1m), (Guid.NewGuid(), 1m), (Guid.NewGuid(), 1m), (Guid.NewGuid(), 1m)), A34Harness.Author);
        var (draft, planned, started, issued) = (refs[0].ProductionOrderUuid, refs[1].ProductionOrderUuid, refs[2].ProductionOrderUuid, refs[3].ProductionOrderUuid);
        await w.Service.PlanDraftsAsync(w.H.Org, [planned, started, issued], A34Harness.Author);
        await w.H.Orders.StartAsync(started, A34Harness.Author);
        var pmr = w.H.Material.ProductionMaterialRequirements.IgnoreQueryFilters().AsNoTracking()
            .Single(m => m.ProductionOrder.UUID == issued).UUID;
        await w.H.Get<IProductionMaterialIssueService>().CreateAsync(issued, new CreateProductionIssueRequest
        {
            IssueType = ProductionIssueType.Standard, Confirm = true,
            Lines = [new CreateProductionIssueLineRequest { RequirementUuid = pmr, Quantity = 1m }]
        }, A34Harness.Author);
        // An A30 make-to-shortage PO of the same order is covered too (C-10), and another order's PO is not touched.
        var mts   = await w.H.Get<IProductionDemandService>().EnsureForSourceAsync(w.KitV, 1m, Today.AddDays(3), 1, ProductionSourceType.SalesOrder, so, Guid.NewGuid(), "SO-2026-00042", A34Harness.Author);
        var other = (await w.Service.CreateDraftsAsync(w.H.Org, w.Request(Guid.NewGuid(), (Guid.NewGuid(), 1m)), A34Harness.Author))[0].ProductionOrderUuid;

        var result = await w.Service.CancelForSaleOrderAsync(w.H.Org, so, "Customer withdrew", A34Harness.Author);

        result.Cancelled.Select(r => r.ProductionOrderUuid).Should().BeEquivalentTo([draft, planned, mts]);
        result.Cancelled.Should().OnlyContain(r => r.Status == ProductionOrderStatus.Cancelled);
        result.KeptRunning.Select(r => r.ProductionOrderUuid).Should().BeEquivalentTo([started, issued]);
        w.Po(started).Status.Should().Be(ProductionOrderStatus.InProgress);
        w.Po(issued).Status.Should().NotBe(ProductionOrderStatus.Cancelled);
        w.Po(other).Status.Should().Be(ProductionOrderStatus.Draft);
        w.Po(planned).Notes.Should().Contain("Customer withdrew");

        var openDemands = await w.H.Get<IAllocationEngine>().GetDemandsAsync(demandType: AllocationDemandType.ProductionMaterial, demandUuid: planned);
        openDemands.Should().BeEmpty();

        // Idempotent: a second cancel reports nothing new as cancelled.
        var again = await w.Service.CancelForSaleOrderAsync(w.H.Org, so, "again", A34Harness.Author);
        again.Cancelled.Should().BeEmpty();
        again.KeptRunning.Should().HaveCount(2);
    }

    [Fact]
    public async Task Cancel_also_closes_material_demands_a_concurrent_plan_registered_but_never_recorded()
    {
        var w = new World();
        var so = Guid.NewGuid();
        var po = (await w.Service.CreateDraftsAsync(w.H.Org, w.Request(so, (Guid.NewGuid(), 2m)), A34Harness.Author))[0].ProductionOrderUuid;
        await w.Service.PlanDraftsAsync(w.H.Org, [po], A34Harness.Author);
        // What the REV race leaves: a PRODUCTION_MATERIAL demand on the PO that no requirement points at.
        await w.H.Get<IAllocationEngine>().RegisterDemandAsync(new AllocationDemandRegistration(
            AllocationDemandType.ProductionMaterial, po, Guid.NewGuid(), "PROD-X", w.RodV, w.H.Plant, 3m, Today.AddDays(2)), A34Harness.Author);

        await w.Service.CancelForSaleOrderAsync(w.H.Org, so, "x", A34Harness.Author);

        (await w.H.Get<IAllocationEngine>().GetDemandsAsync(demandType: AllocationDemandType.ProductionMaterial, demandUuid: po))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Another_organizations_sale_order_is_neither_cancelled_nor_read()
    {
        var w = new World();
        var so = Guid.NewGuid();
        var po = (await w.Service.CreateDraftsAsync(w.H.Org, w.Request(so, (Guid.NewGuid(), 2m)), A34Harness.Author))[0].ProductionOrderUuid;

        (await w.Service.CancelForSaleOrderAsync(w.H.OtherOrg, so, "x", A34Harness.Author)).Cancelled.Should().BeEmpty();
        (await w.Service.GetForSaleOrderAsync(w.H.OtherOrg, so)).Should().BeEmpty();
        w.Po(po).Status.Should().Be(ProductionOrderStatus.Draft);
    }

    // ── Read (D-25) ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_read_lists_every_sales_order_po_of_the_order_oldest_first_in_every_status()
    {
        var w = new World();
        var so = Guid.NewGuid(); var l1 = Guid.NewGuid();
        var mts = await w.H.Get<IProductionDemandService>().EnsureForSourceAsync(w.KitV, 1m, Today.AddDays(3), 1, ProductionSourceType.SalesOrder, so, l1, "SO-2026-00042", A34Harness.Author);
        var mto = (await w.Service.CreateDraftsAsync(w.H.Org, w.Request(so, (Guid.NewGuid(), 2m)), A34Harness.Author))[0];
        await w.H.Orders.CancelAsync(mto.ProductionOrderUuid, new CancelProductionOrderRequest { Reason = "x" }, A34Harness.Author);

        var list = await w.Service.GetForSaleOrderAsync(w.H.Org, so);

        list.Select(r => r.ProductionOrderUuid).Should().Equal(mts, mto.ProductionOrderUuid);
        list[0].FulfillmentRouteUuid.Should().BeNull();
        list[1].Status.Should().Be(ProductionOrderStatus.Cancelled);
        list.Should().OnlyContain(r => !r.Created);
    }
}

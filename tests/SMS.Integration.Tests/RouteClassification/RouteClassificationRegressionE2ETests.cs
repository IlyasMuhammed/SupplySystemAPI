using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using Xunit;
using static SMS.Integration.Tests.FulfillmentRoutes.Routes;
using static SMS.Integration.Tests.RouteClassification.Rc;

namespace SMS.Integration.Tests.RouteClassification;

/// <summary>
/// A34-PF-06 on the real host (LocalDB): what A34 must not break, and the cancel cascade it adds.
/// <list type="bullet">
/// <item><b>R-2:</b> an order whose routes are all STOCK confirms exactly as in A33 — deliveries, no production, no
/// production lines, nothing pending.</item>
/// <item><b>D-2 / C-4 / T-C6-04:</b> an A30 make-to-shortage production order (a manufactured product on a STOCK route)
/// is still raised by the deficit job, carries no route, and creates no delivery at FGR; the line's A33 delivery
/// takes the goods instead. A standalone production order creates no delivery either (even for a variant with a
/// MANUFACTURE route).</item>
/// <item><b>C-3 / PD-02:</b> A33's recovery button and sweep skip make-to-order lines.</item>
/// <item><b>T-C5-07 / D-22 / C-10:</b> SO cancel cancels the open production orders with nothing issued (make-to-order
/// and A30 alike), keeps the IN_PROGRESS one and the READY one with issued material, cancels every open SALES_ORDER
/// demand; the kept order finishes into stock with no delivery and no hold on the cancelled order. While it runs, its
/// custom route's category can't change (D-8, "open production orders").</item>
/// </list>
/// <para>Run alone: <c>dotnet test &lt;out&gt;\SMS.Integration.Tests.dll --filter FullyQualifiedName~RouteClassificationRegressionE2ETests</c>.</para>
/// </summary>
public sealed class RouteClassificationRegressionE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _k;

    public RouteClassificationRegressionE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _k = new SapKit(factory, "RG");
    }

    /// <summary>A manufactured product with an active BOM and no route of its own (the org's STOCK default applies).</summary>
    private async Task<Product> MakeToShortageProductAsync(MtoWorld w, string label)
    {
        var p = await _k.FinishedGoodAsync(label, w.WhId);
        await _k.CreateActiveBomAsync(p, 1m, (w.Raw, 1m));
        return p;
    }

    /// <summary>The production order the A30 deficit job raises for the order's line of <paramref name="product"/>.</summary>
    private async Task<Guid> WaitForDeficitPoAsync(Guid so, Product product) =>
        (await SapKit.WaitForAsync(() => _k.ProductionOrdersOfAsync(so),
            l => l.Any(p => p.G("productUuid") == product.ProductUuid && p.S("status") != "CANCELLED"),
            "the deficit job's production order (A30)", 60)).Single(p => p.G("productUuid") == product.ProductUuid).G("uuid");

    [Fact]
    public async Task An_order_with_only_stock_routes_confirms_exactly_as_in_A33()
    {
        var w = await _k.MtoWorldAsync("R2", rawStock: 0m);
        var a = await _k.CreateProductAsync("R2 A", 10m, 20m);
        var b = await _k.CreateProductAsync("R2 B", 10m, 20m);
        await _k.StockUpAsync(w.Vendor, w.Wh, (a, 10m, 10m), (b, 10m, 10m));
        var so = await _k.CreateOrderAsync(w.Customer, w.Pkr, "SHIP", w.Address, RLine(a, 2m), RLine(b, 3m, route: await _k.RouteUuidAsync(PickPackShip)));

        var draft = await _k.GetSaleOrderAsync(so);
        draft.A("lines").Should().OnlyContain(l => l.S("effectiveRouteCategory") == Stock);
        (draft.A("productionOrders").Count, draft.B("productionCreationPending")).Should().Be((0, false));
        var preview = await _k.Ok(_k.Get($"/api/sale-orders/{so}/delivery-preview"), "preview");
        (preview.I("deliveryCount"), preview.A("productionLines").Count, preview.B("canConfirm")).Should().Be((2, 0, true));

        var confirmed = await _k.ConfirmAsync(so);
        confirmed.A("deliveries").Select(d => d.S("routeCode")).Should().BeEquivalentTo(new[] { PickAndShip, PickPackShip });
        (confirmed.A("skippedLines").Count, confirmed.A("productionOrders").Count, confirmed.B("productionCreationFailed")).Should().Be((0, 0, false));
        var done = await _k.GetSaleOrderAsync(so);
        done.A("lines").Should().OnlyContain(l => l.S("fulfillmentMode") != "MAKE_TO_ORDER" && l.D("reservedQty") == l.D("quantity"));
        done.B("productionCreationPending").Should().BeFalse();
        (await _k.ProductionPendingSinceAsync(so)).Should().BeNull();
        (await _k.ProductionOrdersOfAsync(so)).Should().BeEmpty();
        (await _k.SoTimelineEventsAsync(so)).Should().NotContain(e => e.StartsWith("SO_PRODUCTION"));
        await _k.ShipNoPackAsync(confirmed.A("deliveries").Single(d => d.S("routeCode") == PickAndShip).G("deliveryUuid"), await _k.ManualCarrierAsync());
        (await _k.GetSaleOrderAsync(so)).S("status").Should().Be("PARTIALLY_FULFILLED");
    }

    [Fact]
    public async Task An_A30_make_to_shortage_production_order_on_a_stock_route_creates_no_delivery_at_FGR()
    {
        await _k.SetSaleOrderConfigAsync(("autoPoEnabled", true));
        var w = await _k.MtoWorldAsync("A30", rawStock: 50m);
        var p = await MakeToShortageProductAsync(w, "A30 Made To Stock");
        var so = await _k.CreateOrderAsync(w.Customer, w.Pkr, "SHIP", w.Address, RLine(p, 2m));
        var line = (await _k.GetSaleOrderAsync(so)).LineOf(p);
        (line.S("effectiveRouteCategory"), line.S("routeSource")).Should().Be((Stock, "ORG_DEFAULT"));

        // A33 at confirm: one DRAFT delivery for the whole line; A30: the deficit job raises a production order.
        var confirmed = await _k.ConfirmAsync(so);
        var d = confirmed.A("deliveries").Should().ContainSingle().Which.G("deliveryUuid");
        confirmed.A("productionOrders").Should().BeEmpty("D-2: the confirm itself raises no production for a STOCK line");
        (await _k.GetSaleOrderAsync(so)).LineOf(p).S("fulfillmentMode").Should().NotBe("MAKE_TO_ORDER");
        var po = await WaitForDeficitPoAsync(so, p);
        var detail = await _k.ProductionOrderAsync(po);
        (detail.S("sourceType"), detail.B("isMakeToOrder"), detail.IsNull("fulfillmentRouteUuid"), detail.D("plannedQuantity"))
            .Should().Be(("SALES_ORDER", false, true, 2m), "D-18: only the make-to-order path sets a route; no backfill (C-4)");
        var view = (await _k.GetSaleOrderAsync(so)).A("productionOrders").Should().ContainSingle("D-25 lists make-to-shortage orders too").Which;
        (view.G("productionOrderUuid"), view.B("isMakeToOrder")).Should().Be((po, false));

        // FGR → no delivery from the PO; the A33 delivery gets the goods.
        await _k.FloorToFgrAsync(po, 2m, 2m, 0m);
        var done = await _k.ProductionOrderAsync(po);
        (done.S("status"), done.IsNull("deliveryOrderUuid"), done.B("deliveryCreationPending")).Should().Be(("COMPLETED", true, false));
        (await _k.DeliveriesFromPoAsync(po)).Should().BeEmpty("BR-C6-02: not made to order");
        (await _k.SoDeliveriesAsync(so)).Select(x => x.G("uuid")).Should().Equal(d);
        var refused = await _k.TryCreateDeliveryNow(po);
        refused.ShouldBe(HttpStatusCode.BadRequest, "not made to order for a sale order");
        refused.Message.Should().Contain("is not made to order for a sale order, so no delivery is created from it.");

        var held = await SapKit.WaitForAsync(() => _k.GetSaleOrderAsync(so), s => s.LineOf(p).D("reservedQty") == 2m, "A30: the FGR's allocation holds the goods for the line", 30);
        held.LineOf(p).ND("deficitQty").Should().Be(0m);
        await _k.ShipNoPackAsync(d, await _k.ManualCarrierAsync());
        (await _k.GetSaleOrderAsync(so)).S("status").Should().Be("FULFILLED");
    }

    [Fact]
    public async Task A_standalone_production_order_creates_no_delivery_even_for_a_make_to_order_variant()
    {
        var w = await _k.MtoWorldAsync("SOLO", rawStock: 20m);
        var po = await _k.ProduceToStockAsync(w, 2m);
        var p = await _k.ProductionOrderAsync(po);
        (p.B("isMakeToOrder"), p.IsNull("fulfillmentRouteUuid"), p.IsNull("deliveryOrderUuid"), p.B("deliveryCreationPending"))
            .Should().Be((false, true, true, false), "the variant's route never reaches a standalone PO");
        (await _k.DeliveriesFromPoAsync(po)).Should().BeEmpty("T-C6-04");
        (await _k.TryCreateDeliveryNow(po)).ShouldBe(HttpStatusCode.BadRequest, "BR-C6-02");
        var rows = await _f.QueryAsync("SELECT COUNT(*) AS N FROM logistics.delivery_orders WHERE ProductionOrderUuid = @po", ("@po", po));
        Convert.ToInt32(rows[0]["N"]).Should().Be(0);
    }

    [Fact]
    public async Task A33s_recovery_button_and_sweep_skip_make_to_order_lines()
    {
        var w = await _k.MtoWorldAsync("C3", rawStock: 20m);
        var so = await _k.CreateOrderAsync(w.Customer, w.Pkr, "SHIP", w.Address, RLine(w.Fg, 2m));
        await _k.ConfirmAsync(so);

        var recovery = await _k.Ok(_k.Post($"/api/sale-orders/{so}/create-deliveries"), "A33 create-deliveries");
        recovery.A("created").Should().BeEmpty("C-3: a make-to-order line gets its delivery from production");
        if (recovery.Has("skippedLines"))
            recovery.A("skippedLines").Select(s => s.S("reason")).Should().Equal(MtoSkipReason(1));
        await _f.ExecuteAsync("UPDATE demand.sale_orders SET DeliveryCreationPendingSince = DATEADD(HOUR, -1, SYSUTCDATETIME()) WHERE UUID = @so", ("@so", so));
        await RunA33DeliverySweepAsync();
        (await _k.SoDeliveriesAsync(so)).Should().BeEmpty("the A33 sweep skips it too");
    }

    private async Task RunA33DeliverySweepAsync()
    {
        var asm = typeof(SMS.Modules.Demand.Data.DemandDbContext).Assembly;
        var type = asm.GetTypes().Single(t => t is { IsClass: true, IsAbstract: false } && t.Name.EndsWith("Job")
                                              && t.Name.Contains("Delivery") && t.Name.Contains("Sweep") && !t.Name.Contains("Production"));
        var method = type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .First(m => typeof(Task).IsAssignableFrom(m.ReturnType) && m.DeclaringType == type && m.GetParameters().All(p => p.IsOptional));
        await using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateAsyncScope(_f.Services);
        var job = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService(scope.ServiceProvider, type);
        await (Task)method.Invoke(job, method.GetParameters().Select(p => p.DefaultValue).ToArray())!;
    }

    [Fact]
    public async Task Cancel_cancels_open_production_keeps_running_production_and_cancels_the_orders_demands()
    {
        await _k.SetSaleOrderConfigAsync(("autoPoEnabled", true));
        var w = await _k.MtoWorldAsync("T507", rawStock: 100m);
        var a30 = await MakeToShortageProductAsync(w, "T507 Made To Stock");
        var custom = await _k.CreateCategoryRouteAsync("RUN", Manufacture, "PICK", "GOODS_ISSUE", "SHIP");

        // L1 issued but not started (READY + issued), L2 started on the custom route (IN_PROGRESS), L3 untouched (open),
        // L4 an A30 make-to-shortage line (its PO from the deficit job).
        var so = await _k.CreateOrderAsync(w.Customer, w.Pkr, "SHIP", w.Address,
            RLine(w.Fg, 1m), RLine(w.Fg, 2m, route: custom.G("uuid")), RLine(w.Fg, 3m), RLine(a30, 1m));
        var confirmed = await _k.ConfirmAsync(so);
        var mto = confirmed.A("productionOrders").OrderBy(p => p.I("lineNumber")).Select(p => p.G("productionOrderUuid")).ToList();
        mto.Should().HaveCount(3);
        var (issued, running, open) = (mto[0], mto[1], mto[2]);
        var shortage = await WaitForDeficitPoAsync(so, a30);
        shortage.Should().NotBe(issued).And.NotBe(running).And.NotBe(open);

        await _k.ReadyAsync(issued);
        var lines = (await _k.MaterialsAsync(issued)).Where(m => m.D("outstanding") > 0)
            .Select(m => new { RequirementUuid = m.G("uuid"), Quantity = m.D("outstanding") }).ToArray();
        await _k.Ok(_k.Post($"/api/production-orders/{issued}/issues", new { IssueType = "STANDARD", Lines = lines, Confirm = true }), "issue without starting");
        await _k.IssueAndStartAsync(running);
        (await _k.PoStatusAsync(running)).Should().Be("IN_PROGRESS");
        var openBefore = (await _k.SoDemandsAsync(so)).Count(d => (string?)d["Status"] == "OPEN");

        // ── Cancel ──
        var result = await _k.Ok(_k.TryCancelOrder(so, "customer withdrew"), "cancel the order");
        result.A("cancelledProductionOrders").Select(p => p.G("productionOrderUuid")).Should().BeEquivalentTo(new[] { open, shortage },
            "D-22: DRAFT..READY with nothing issued, make-to-order and A30 alike");
        result.A("cancelledProductionOrders").Should().OnlyContain(p => p.S("status") == "CANCELLED");
        result.A("runningProductionOrders").Select(p => p.G("productionOrderUuid")).Should().BeEquivalentTo(new[] { issued, running },
            "IN_PROGRESS, or material issued: kept and reported");
        result.I("cancelledAllocationDemands").Should().Be(openBefore, "every open SALES_ORDER demand of the order (D-17a: the A30 line's, none yet for make-to-order lines)");
        (await _k.SoDemandsAsync(so)).Should().NotContain(d => (string?)d["Status"] == "OPEN", "C-10: no open SALES_ORDER demand on a cancelled order");

        (await _k.PoStatusAsync(open), await _k.PoStatusAsync(shortage)).Should().Be(("CANCELLED", "CANCELLED"));
        (await _k.PoStatusAsync(running)).Should().Be("IN_PROGRESS");
        (await _k.PoStatusAsync(issued)).Should().NotBe("CANCELLED");
        var after = await _k.GetSaleOrderAsync(so);
        (after.S("status"), after.B("productionCreationPending")).Should().Be(("CANCELLED", false));
        (await _k.SoTimelineEventsAsync(so)).Should().Contain("SO_PRODUCTION_CANCELLED");
        (await _k.TryCreateProductionOrders(so)).ShouldBe(HttpStatusCode.BadRequest, "nothing is re-created for a cancelled order");

        // D-8: the kept PO still uses the custom route ("open production orders").
        var inUse = await _k.TryUpdateRouteCategory(await _k.RouteByUuidAsync(custom.G("uuid")), Stock);
        inUse.ShouldBe(HttpStatusCode.Conflict, "an open production order uses the route");
        inUse.Message.Should().Contain("open production orders");

        // The kept order finishes into stock: no delivery, no hold on the cancelled order (C-10).
        await _k.ReportAndCompleteAsync(running, 2m);
        await _k.Ok(_k.TryInspect(running, await _k.InspectorAsync(), 2m, 0m), "QI");
        await _k.Ok(_k.TryFgr(running, 2m), "FGR");
        (await _k.PoStatusAsync(running)).Should().Be("COMPLETED");
        await _k.BackdateDeliveryPendingIfSetAsync(running);
        await _k.RunProductionDeliverySweepAsync();
        (await _k.DeliveriesFromPoAsync(running)).Should().BeEmpty("D-22: its hand-over finds the order cancelled");
        (await _k.SoDeliveriesAsync(so)).Where(x => x.S("status") != "CANCELLED").Should().BeEmpty();
        (await _k.SoLedgerHeldAsync(so)).Should().Be(0m, "C-10: finished goods are not re-parented onto a cancelled order");

        // Nothing open uses the custom route any more.
        await _k.Ok(_k.TryUpdateRouteCategory(await _k.RouteByUuidAsync(custom.G("uuid")), Stock), "the route is free again");
    }
}

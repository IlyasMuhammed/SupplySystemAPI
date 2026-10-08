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
/// A34 C5 on the real host (LocalDB): what confirming does with make-to-order lines (D-1, D-5, D-17), and the
/// make-to-stock escape hatch (PF-03).
/// <list type="bullet">
/// <item><b>T-C5-02/04/05, D-1, D-2, D-28:</b> an all-make-to-order order reserves nothing although free stock exists,
/// raises one production order per line (route, Source*, planned with its material requirements), registers the line's
/// SALES_ORDER demand, creates no delivery and no deficit job; a recovery call returns the same order; a manual reserve
/// afterwards is allowed and does not shrink the production order.</item>
/// <item><b>T-C5-03/06:</b> a mixed order makes deliveries for its stock lines and a production order for the other.</item>
/// <item><b>T-C2-03, D-5:</b> the four confirm-gate blockers, one per line, in the detail, the preview and the 400.</item>
/// <item><b>R-17 / C-16:</b> SELF_PICKUP + a make-to-order route with SHIP → SHIPPING_ADDRESS_REQUIRED with the hint;
/// a MANUFACTURE route without SHIP confirms.</item>
/// <item><b>T-C6-09 (PF-03):</b> a line overridden to a stock route ships from stock at confirm, with no production order.</item>
/// </list>
/// <para>Run alone: <c>dotnet test &lt;out&gt;\SMS.Integration.Tests.dll --filter FullyQualifiedName~ManufactureConfirmE2ETests</c>.</para>
/// </summary>
public sealed class ManufactureConfirmE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _k;

    public ManufactureConfirmE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _k = new SapKit(factory, "C5");
    }

    [Fact]
    public async Task An_all_make_to_order_order_reserves_nothing_and_raises_one_planned_production_order_per_line()
    {
        var w = await _k.MtoWorldAsync("MTO", rawStock: 100m);
        await _k.ProduceToStockAsync(w, 5m);   // free finished goods the make-to-order line must not take (C-2)
        var so = await _k.CreateOrderAsync(w.Customer, w.Pkr, "SHIP", w.Address, RLine(w.Fg, 4m));

        // ── Draft: the line's live category, no blocker; the preview lists it as production ──
        var draft = await _k.GetSaleOrderAsync(so);
        var dl = draft.LineOf(w.Fg);
        (dl.S("effectiveRouteCategory"), dl.S("effectiveRouteCode"), dl.S("routeSource")).Should().Be((Manufacture, w.RouteCode, "VARIANT"));
        draft.A("confirmBlockers").Should().BeEmpty();
        draft.A("productionOrders").Should().BeEmpty();
        draft.B("productionCreationPending").Should().BeFalse();

        var preview = await _k.Ok(_k.Get($"/api/sale-orders/{so}/delivery-preview"), "delivery preview");
        (preview.B("canConfirm"), preview.I("deliveryCount"), preview.A("groups").Count).Should().Be((true, 0, 0), "a make-to-order line is not a delivery group");
        var pl = preview.A("productionLines").Should().ContainSingle().Which;
        (pl.I("lineNumber"), pl.D("quantity"), pl.G("variantUuid"), pl.G("routeUuid"), pl.S("routeCode")).Should().Be((1, 4m, w.Fg.VariantUuid, w.Route, w.RouteCode));
        pl.A("steps").Select(s => s.GetString()).Should().Equal("PICK", "GOODS_ISSUE", "SHIP");
        pl.S("message").Should().Contain("A production order will be created");
        preview.A("lines").Single().S("effectiveRouteCategory").Should().Be(Manufacture);

        // ── Confirm ──
        var confirmed = await _k.ConfirmAsync(so);
        confirmed.A("deliveries").Should().BeEmpty("D-1: no delivery at confirm for a make-to-order line");
        confirmed.B("deliveryCreationFailed").Should().BeFalse();
        // Analysis §4.6 2.6: with no routable STOCK line the A33 creator isn't called at all, so the skip may be absent.
        confirmed.A("skippedLines").Select(s => s.S("reason")).Should().BeSubsetOf(new[] { MtoSkipReason(1) });
        confirmed.B("productionCreationFailed").Should().BeFalse($"production creation ran — {J.Short(confirmed)}");
        var created = confirmed.A("productionOrders").Should().ContainSingle().Which;
        var poUuid = created.G("productionOrderUuid");
        (created.B("created"), created.B("isMakeToOrder"), created.D("plannedQuantity"), created.I("lineNumber"), created.NG("fulfillmentRouteUuid"))
            .Should().Be((true, true, 4m, 1, (Guid?)w.Route), "T-C5-02: one production order for the full quantity");
        created.S("productionNumber").Should().StartWith("PROD");

        var so1 = await _k.GetSaleOrderAsync(so);
        var line = so1.LineOf(w.Fg);
        (line.S("fulfillmentMode"), line.ND("deficitQty"), line.D("reservedQty"), line.S("effectiveRouteCategory"))
            .Should().Be(("MAKE_TO_ORDER", (decimal?)4m, 0m, Manufacture), "D-1: nothing reserved, the deficit is the whole line");
        (await _k.SoLedgerHeldAsync(so)).Should().Be(0m, "C-2: the 5 free units stay free");
        so1.B("productionCreationPending").Should().BeFalse("creation finished");
        (await _k.ProductionPendingSinceAsync(so)).Should().BeNull();
        var detailPo = so1.A("productionOrders").Should().ContainSingle().Which;
        (detailPo.G("productionOrderUuid"), detailPo.NG("soLineUuid"), detailPo.B("isMakeToOrder"), detailPo.S("fulfillmentRouteCode"))
            .Should().Be((poUuid, (Guid?)line.G("uuid"), true, w.RouteCode), "D-25: the detail lists it");
        detailPo.IsNull("deliveryOrderUuid").Should().BeTrue("Delivery: pending");

        // ── T-C5-04/05: the production order ──
        var po = await _k.ProductionOrderAsync(poUuid);
        (po.S("sourceType"), po.NG("sourceUuid"), po.NG("sourceLineUuid"), po.NG("fulfillmentRouteUuid"), po.S("fulfillmentRouteCode"), po.B("isMakeToOrder"))
            .Should().Be(("SALES_ORDER", (Guid?)so, (Guid?)line.G("uuid"), (Guid?)w.Route, w.RouteCode, true));
        (po.I("saleOrderLineNumber"), po.D("plannedQuantity"), po.B("deliveryCreationPending"), po.IsNull("deliveryOrderUuid")).Should().Be((1, 4m, false, true));
        po.S("sourceReference").Should().Be(so1.S("soNumber"));
        po.S("status").Should().BeOneOf(PlannedOrLater, "BR-C5-04: planned straight after creation");
        var materials = await _k.MaterialsAsync(poUuid);
        materials.Should().ContainSingle("the BOM's one input").Which.D("requiredQuantity").Should().Be(8m, "4 × 2 raw");

        // D-17a (contract v1.4, REV-01): production creation registers no allocation demand; the FGR hook does.
        (await _k.SoDemandsAsync(so)).Should().BeEmpty("D-17a: no SALES_ORDER demand until the FGR");

        // D-2: no deficit job runs for a MAKE_TO_ORDER line — no second production order, no purchase order.
        await Task.Delay(3000);
        (await _k.ProductionOrdersOfAsync(so)).Select(p => p.G("uuid")).Should().Equal(poUuid);
        (await _k.GetSaleOrderAsync(so)).LineOf(w.Fg).IsNull("linkedPoId").Should().BeTrue("no auto-PO for a make-to-order line");
        (await _k.SoTimelineEventsAsync(so)).Should().Contain("SO_PRODUCTION_CREATED");

        // ── Recovery is idempotent (D-17) ──
        var again = await _k.Ok(_k.TryCreateProductionOrders(so), "create production orders again");
        again.B("productionCreationFailed").Should().BeFalse();
        var same = again.A("productionOrders").Should().ContainSingle().Which;
        (same.G("productionOrderUuid"), same.B("created")).Should().Be((poUuid, false));
        (await _k.TryConfirm(so)).Status.Should().NotBe(HttpStatusCode.OK, "an order confirms once");

        // ── D-28: a manual reserve on the make-to-order line is allowed; the production order is not reduced ──
        var reserve = await _k.Ok(_k.Post($"/api/sale-orders/{so}/lines/{line.G("uuid")}/reserve", new { }), "manual reserve on the MTO line");
        reserve.S("outcome").Should().Be("RESERVED", $"D-28 — {J.Short(reserve)}");
        (await _k.SoLedgerHeldAsync(so)).Should().Be(4m);
        (await _k.ProductionOrderAsync(poUuid)).D("plannedQuantity").Should().Be(4m, "D-28: the production order still makes the full quantity");
    }

    [Fact]
    public async Task A_mixed_order_makes_deliveries_for_its_stock_lines_and_a_production_order_for_the_made_line()
    {
        var w = await _k.MtoWorldAsync("MIX", rawStock: 50m);
        var a = await _k.CreateProductAsync("Mix Stock A", 10m, 20m);
        var b = await _k.CreateProductAsync("Mix Stock B", 10m, 20m);
        await _k.StockUpAsync(w.Vendor, w.Wh, (a, 10m, 10m), (b, 10m, 10m));
        var pps = await _k.RouteUuidAsync(PickPackShip);

        var so = await _k.CreateOrderAsync(w.Customer, w.Pkr, "SHIP", w.Address, RLine(a, 3m), RLine(b, 2m, route: pps), RLine(w.Fg, 5m));
        var draft = await _k.GetSaleOrderAsync(so);
        draft.A("lines").Select(l => l.S("effectiveRouteCategory")).Should().Equal(Stock, Stock, Manufacture);

        var preview = await _k.Ok(_k.Get($"/api/sale-orders/{so}/delivery-preview"), "delivery preview");
        preview.I("deliveryCount").Should().Be(2, "T-C5-03: the stock lines only");
        preview.A("groups").Select(g => g.S("routeCode")).Should().BeEquivalentTo(new[] { PickAndShip, PickPackShip });
        preview.A("productionLines").Select(p => p.I("lineNumber")).Should().Equal(3);

        var confirmed = await _k.ConfirmAsync(so);
        confirmed.A("deliveries").Select(d => d.S("routeCode")).Should().BeEquivalentTo(new[] { PickAndShip, PickPackShip });
        confirmed.A("skippedLines").Select(s => s.S("reason")).Should().Equal(MtoSkipReason(3));
        var po = confirmed.A("productionOrders").Should().ContainSingle().Which;
        (po.I("lineNumber"), po.D("plannedQuantity"), po.B("created")).Should().Be((3, 5m, true));

        var done = await _k.GetSaleOrderAsync(so);
        (done.LineOf(a).D("reservedQty"), done.LineOf(b).D("reservedQty"), done.LineOf(w.Fg).D("reservedQty")).Should().Be((3m, 2m, 0m));
        done.LineOf(w.Fg).S("fulfillmentMode").Should().Be("MAKE_TO_ORDER");
        done.A("lines").Where(l => l.G("variantUuid") != w.Fg.VariantUuid).Should().OnlyContain(l => l.S("fulfillmentMode") != "MAKE_TO_ORDER");
        done.A("lines").Select(l => l.S("effectiveRouteCategory")).Should().Equal(new[] { Stock, Stock, Manufacture }, "the confirm-time snapshot");
        (await _k.SoLedgerHeldAsync(so)).Should().Be(5m, "only the stock lines hold");

        // T-C5-06: the order shows both sides — two deliveries, one production order.
        (await _k.SoDeliveriesAsync(so)).Should().HaveCount(2);
        done.A("productionOrders").Should().ContainSingle().Which.G("productionOrderUuid").Should().Be(po.G("productionOrderUuid"));
        (await _k.ProductionOrdersOfAsync(so)).Should().ContainSingle();
    }

    [Fact]
    public async Task The_four_manufacturing_blockers_show_one_per_line_and_block_confirm()
    {
        var w = await _k.MtoWorldAsync("GATE", rawStock: 20m);
        var mfg = w.Route;
        var plain = await _k.CreateProductAsync("Gate Bought", 5m, 9m);                       // NOT_MANUFACTURED (line override)
        var noBom = await _k.FinishedGoodAsync("Gate No BOM", w.WhId);                         // BOM_MISSING (only a draft BOM)
        await _k.CreateDraftBomAsync(noBom, 1m, (w.Raw, 1m));
        await _k.SetVariantRouteAsync(noBom, mfg);
        var noWh = await _k.FinishedGoodAsync("Gate No WH", productionWarehouseId: null);      // PRODUCTION_WAREHOUSE_MISSING
        await _k.CreateActiveBomAsync(noWh, 1m, (w.Raw, 1m));
        await _k.SetVariantRouteAsync(noWh, mfg);

        var so = await _k.CreateOrderAsync(w.Customer, w.Pkr, "SHIP", w.Address,
            RLine(plain, 1m, route: mfg), RLine(noBom, 1m), RLine(noWh, 1m), RLine(w.Fg, 1m));
        var draft = await _k.GetSaleOrderAsync(so);
        var expected = new (int?, string?)[] { (1, "NOT_MANUFACTURED"), (2, "BOM_MISSING"), (3, "PRODUCTION_WAREHOUSE_MISSING") };
        draft.BlockerCodes().Should().Equal(expected, "D-5: one blocker per line, in line order; line 4 is ready");
        draft.A("lines").Select(l => l.S("routeBlocker")).Should().Equal("NOT_MANUFACTURED", "BOM_MISSING", "PRODUCTION_WAREHOUSE_MISSING", null);

        var preview = await _k.Ok(_k.Get($"/api/sale-orders/{so}/delivery-preview"), "preview");
        preview.B("canConfirm").Should().BeFalse();
        preview.BlockerCodes("blockers").Should().Equal(expected);

        var refused = await _k.TryConfirm(so);
        refused.ShouldBe(HttpStatusCode.BadRequest, "D-5 blocks confirm");
        var lines = refused.Message.Split('\n');
        lines.Should().HaveCount(3, $"one message per blocked line — {refused.Message}");
        lines[0].Should().StartWith("Line 1:").And.Contain("is not a manufactured product").And.Contain($"make-to-order route '{MfgPickShip}'");
        lines[1].Should().StartWith("Line 2:").And.Contain("has no active bill of materials");
        lines[2].Should().StartWith("Line 3:").And.Contain("has no default production warehouse");
        (await _k.GetSaleOrderAsync(so)).S("status").Should().Be("DRAFT");
        (await _k.ProductionOrdersOfAsync(so)).Should().BeEmpty();

        // Line save stays free: a draft keeps them. Dropping them confirms.
        await _k.Ok(_k.Put($"/api/sale-orders/{so}", new
        {
            PartnerId = w.Customer.Uuid, CurrencyId = w.Pkr, DeliveryMode = "SHIP", ShippingAddressId = w.Address, Lines = new[] { RLine(w.Fg, 1m) }
        }), "keep only the ready line");
        (await _k.GetSaleOrderAsync(so)).A("confirmBlockers").Should().BeEmpty();
        (await _k.ConfirmAsync(so)).A("productionOrders").Should().ContainSingle();
    }

    [Fact]
    public async Task Without_MODULE_MANUFACTURING_a_make_to_order_line_blocks_and_the_route_cannot_be_assigned()
    {
        var pkr = await _k.EnsureCurrencyAsync("PKR", "Pakistani Rupee", "Rs");
        var (org2, k2, _) = await _k.SecondOrganizationAsync("NOMFG");
        await _k.SetOrgBaseCurrencyAsync(org2, pkr);
        var customer = await k2.CreateCustomerAsync("No-Mfg Customer");
        var address = await k2.ShippingAddressAsync(customer);
        var whId = await k2.WarehouseIdAsync(await k2.CreateWarehouseAsync());
        var fg = await k2.FinishedGoodAsync("No-Mfg FG", whId);
        var other = await k2.FinishedGoodAsync("No-Mfg FG Two", whId);
        var mfg2 = await k2.RouteUuidAsync(MfgPickShip);
        await k2.SetVariantRouteAsync(fg, mfg2);
        var so = await k2.CreateOrderAsync(customer, pkr, "SHIP", address, RLine(fg, 2m));

        await _k.SetFeatureAsync(org2, "MODULE_MANUFACTURING", false);

        // D-9: the seeds are still there (enabling the module later needs no re-seed)…
        (await k2.RoutesAsync(includeInactive: true)).Select(r => r.S("code")).Should().Contain(new[] { MfgPickShip, MfgPickPackShip });
        // …but assigning one is refused…
        var assign = await k2.AssignVariantRoute(other.VariantUuid, mfg2);
        assign.ShouldBe(HttpStatusCode.BadRequest, "D-9: no make-to-order without the module");
        assign.Message.Should().Contain("Manufacturing is not enabled for your organization");
        // …and the line already carrying one blocks confirm.
        var draft = await k2.GetSaleOrderAsync(so);
        draft.BlockerCodes().Should().Equal(new (int?, string?)[] { (1, "MANUFACTURING_DISABLED") });
        var refused = await k2.TryConfirm(so);
        refused.ShouldBe(HttpStatusCode.BadRequest, "D-5: MANUFACTURING_DISABLED");
        refused.Message.Should().Contain($"Line 1: '{MfgPickShip}' is a make-to-order route, but manufacturing is not enabled for your organization");
        (await k2.TryCreateProductionOrders(so)).Status.Should().Be(HttpStatusCode.BadRequest, "a DRAFT order has no production to create");

        // The escape hatch still works: a stock route on the line confirms (to a delivery, no production).
        await k2.Ok(k2.Put($"/api/sale-orders/{so}/lines/{draft.LineOf(fg).G("uuid")}/fulfillment-route",
            new { FulfillmentRouteUuid = await k2.RouteUuidAsync(PickAndShip) }), "override to a stock route");
        var confirmed = await k2.Ok(k2.TryConfirm(so), "confirm with a stock route");
        confirmed.A("productionOrders").Should().BeEmpty();
        confirmed.B("productionCreationFailed").Should().BeFalse();
    }

    [Fact]
    public async Task A_self_pickup_order_needs_a_manufacture_route_without_SHIP()
    {
        var w = await _k.MtoWorldAsync("PICKUP", rawStock: 20m);
        var so = await _k.CreateOrderAsync(w.Customer, w.Pkr, "SELF_PICKUP", null, RLine(w.Fg, 2m));

        var draft = await _k.GetSaleOrderAsync(so);
        var blocker = draft.A("confirmBlockers").Should().ContainSingle().Which;
        blocker.S("code").Should().Be("SHIPPING_ADDRESS_REQUIRED", "C-16: the MFG seeds have SHIP");
        blocker.S("message").Should().EndWith(" For collection, create a MANUFACTURE route without the SHIP step.");
        (await _k.TryConfirm(so)).ShouldBe(HttpStatusCode.BadRequest, "R-17");

        var collect = await _k.CreateCategoryRouteAsync("MCOL", Manufacture, "PICK", "GOODS_ISSUE");
        await _k.Ok(_k.Put($"/api/sale-orders/{so}/lines/{draft.LineOf(w.Fg).G("uuid")}/fulfillment-route",
            new { FulfillmentRouteUuid = collect.G("uuid") }), "a make-to-order route without SHIP");
        var fixedDraft = await _k.GetSaleOrderAsync(so);
        fixedDraft.A("confirmBlockers").Should().BeEmpty();
        (fixedDraft.LineOf(w.Fg).S("effectiveRouteCategory"), fixedDraft.LineOf(w.Fg).S("routeSource")).Should().Be((Manufacture, "LINE_OVERRIDE"));

        var confirmed = await _k.ConfirmAsync(so);
        var po = confirmed.A("productionOrders").Should().ContainSingle().Which;
        po.NG("fulfillmentRouteUuid").Should().Be(collect.G("uuid"), "the line's own route goes on the production order");
        confirmed.A("deliveries").Should().BeEmpty();
    }

    [Fact]
    public async Task The_escape_hatch_a_stock_route_on_the_line_ships_from_stock_with_no_production_order()
    {
        var w = await _k.MtoWorldAsync("HATCH", rawStock: 40m);
        await _k.ProduceToStockAsync(w, 3m);
        var so = await _k.CreateOrderAsync(w.Customer, w.Pkr, "SHIP", w.Address, RLine(w.Fg, 3m));
        var lineUuid = (await _k.GetSaleOrderAsync(so)).LineOf(w.Fg).G("uuid");

        // BR-C6-09: the salesperson overrides the variant's MANUFACTURE route with a stock route.
        var changed = await _k.Ok(_k.Put($"/api/sale-orders/{so}/lines/{lineUuid}/fulfillment-route",
            new { FulfillmentRouteUuid = await _k.RouteUuidAsync(PickAndShip) }), "override to PICK_AND_SHIP");
        (changed.S("effectiveRouteCategory"), changed.S("routeSource"), changed.S("effectiveRouteCode")).Should().Be((Stock, "LINE_OVERRIDE", PickAndShip));

        var preview = await _k.Ok(_k.Get($"/api/sale-orders/{so}/delivery-preview"), "preview");
        (preview.I("deliveryCount"), preview.A("productionLines").Count).Should().Be((1, 0));

        var confirmed = await _k.ConfirmAsync(so);
        confirmed.A("productionOrders").Should().BeEmpty("T-C6-09: no production order");
        var d = confirmed.A("deliveries").Should().ContainSingle().Which;
        d.S("routeCode").Should().Be(PickAndShip);
        var line = (await _k.GetSaleOrderAsync(so)).LineOf(w.Fg);
        line.S("fulfillmentMode").Should().NotBe("MAKE_TO_ORDER");
        line.D("reservedQty").Should().Be(3m, "reserved from the produced stock at confirm");
        line.S("effectiveRouteCategory").Should().Be(Stock);
        (await _k.ProductionOrdersOfAsync(so)).Should().BeEmpty();
        (await _k.SoDemandsAsync(so)).Where(r => (string?)r["Status"] == "OPEN").Should().BeEmpty("no make-to-order demand was registered");

        // It ships like any A33 stock delivery.
        var carrier = await _k.ManualCarrierAsync();
        await _k.ShipNoPackAsync(d.G("deliveryUuid"), carrier);
        (await _k.GetSaleOrderAsync(so)).S("status").Should().Be("FULFILLED");
    }
}

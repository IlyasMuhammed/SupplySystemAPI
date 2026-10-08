using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using Xunit;
using static SMS.Integration.Tests.FulfillmentRoutes.Routes;
using static SMS.Integration.Tests.RouteClassification.Rc;

namespace SMS.Integration.Tests.RouteClassification;

/// <summary>
/// A34-PF-01 and PF-02 on the real host (LocalDB), in the repo's real statuses (A33: DELIVERED, not COMPLETED).
/// <list type="bullet">
/// <item><b>PF-01, mixed order:</b> two stock lines and one make-to-order line. The stock deliveries are walked to
/// DELIVERED at once (the order goes PARTIALLY_FULFILLED); the production order is run through the floor, its FGR
/// hands a DRAFT delivery over, that one is walked too, the order turns FULFILLED and all three deliveries invoice.</item>
/// <item><b>PF-02 / T-C6-10, the full manufacturing chain:</b> SO → make-to-order PO → its chained sub-assembly PO →
/// the raw material's purchase order (submit, approve, send, GRN with the receiving check, allocation run, A31 C10) →
/// sub-assembly floor → FGR → parent READY → floor → QI → FGR → delivery → pick → pack → goods issue → ship + proof →
/// DELIVERED → invoiced. The chained child (SourceType PRODUCTION, no route) hands nothing over (BR-C6-02).</item>
/// </list>
/// nextActions is checked at every status of each walked delivery (one forward action, A33 agreement).
/// <para>Run alone: <c>dotnet test &lt;out&gt;\SMS.Integration.Tests.dll --filter FullyQualifiedName~RouteClassificationFullChainE2ETests</c>.</para>
/// </summary>
public sealed class RouteClassificationFullChainE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _k;

    public RouteClassificationFullChainE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _k = new SapKit(factory, "FC");
    }

    [Fact]
    public async Task PF01_a_mixed_order_ships_its_stock_lines_at_once_and_its_made_line_after_production()
    {
        var w = await _k.MtoWorldAsync("PF01", rawStock: 30m);
        var gst = await _k.EnsureTaxCodeAsync("GST17", 17m, "SALES", isDefault: true);
        var valve = await _k.CreateProductAsync("PF01 Valve", 40m, 90m);
        var seal = await _k.CreateProductAsync("PF01 Seal", 4m, 10m);
        await _k.StockUpAsync(w.Vendor, w.Wh, (valve, 10m, 40m), (seal, 20m, 4m));
        var pps = await _k.RouteUuidAsync(PickPackShip);

        var so = await _k.CreateOrderAsync(w.Customer, w.Pkr, "SHIP", w.Address,
            RLine(valve, 2m, taxCode: gst), RLine(seal, 6m, route: pps, taxCode: gst), RLine(w.Fg, 4m, taxCode: gst));

        // ── Confirm: two stock deliveries, one production order ──
        var confirmed = await _k.ConfirmAsync(so);
        var stock = confirmed.A("deliveries");
        stock.Select(d => d.S("routeCode")).Should().BeEquivalentTo(new[] { PickAndShip, PickPackShip });
        var po = confirmed.A("productionOrders").Should().ContainSingle().Which.G("productionOrderUuid");
        confirmed.A("skippedLines").Select(s => s.S("reason")).Should().Equal(MtoSkipReason(3));

        // ── The stock lines ship now ──
        var carrier = await _k.ManualCarrierAsync();
        var next = new List<string>();
        var dValve = stock.Single(d => d.S("routeCode") == PickAndShip).G("deliveryUuid");
        var dSeal = stock.Single(d => d.S("routeCode") == PickPackShip).G("deliveryUuid");
        await _k.ShipNoPackAsync(dValve, carrier, next, PickAndShip);

        await _k.CheckNextAsync(next, dSeal, PickPackShip, "DRAFT", "RELEASE");
        await _k.ReleaseAsync(dSeal);
        await _k.PickAllAsync(dSeal);
        await _k.CheckNextAsync(next, dSeal, PickPackShip, "PICKED", "PACK");
        await _k.PackAllAsync(dSeal);
        await _k.CheckNextAsync(next, dSeal, PickPackShip, "STAGED", "GOODS_ISSUE");
        await _k.Ok(_k.GoodsIssue(dSeal), "goods issue");
        await _k.ShipAndProveAsync(dSeal, carrier);
        await _k.CheckNextAsync(next, dSeal, PickPackShip, "DELIVERED", null);

        var midway = await _k.GetSaleOrderAsync(so);
        midway.S("status").Should().Be("PARTIALLY_FULFILLED", "the made line is still in production");
        (midway.LineOf(valve).D("fulfilledQty"), midway.LineOf(seal).D("fulfilledQty"), midway.LineOf(w.Fg).D("fulfilledQty")).Should().Be((2m, 6m, 0m));
        midway.A("productionOrders").Single().IsNull("deliveryOrderUuid").Should().BeTrue("Delivery: pending");

        // ── Production → FGR → the hand-over ──
        await _k.FloorToFgrAsync(po, produced: 4m, accepted: 4m, rejected: 0m);
        var made = (await _k.DeliveriesFromPoAsync(po)).Should().ContainSingle().Which.G("uuid");
        (await _k.DeliveryAsync(made)).S("fulfillmentRouteCode").Should().Be(w.RouteCode);
        (await _k.SoDeliveriesAsync(so)).Should().HaveCount(3);
        await _k.ShipNoPackAsync(made, carrier, next, w.RouteCode);
        next.Should().BeEmpty("nextActions' forward actions at each status:\n" + string.Join("\n", next));

        // ── Fulfilled and invoiced ──
        var done = await _k.GetSaleOrderAsync(so);
        done.S("status").Should().Be("FULFILLED");
        done.A("lines").Should().OnlyContain(l => l.D("fulfilledQty") == l.D("quantity"));
        var vp = done.A("productionOrders").Single();
        (vp.S("status"), vp.NG("deliveryOrderUuid")).Should().Be(("COMPLETED", (Guid?)made));
        foreach (var d in new[] { dValve, dSeal, made })
            (await _k.GetInvoiceAsync(await _k.InvoiceAsync(d))).S("status").Should().NotBe("DRAFT");
        (await _k.GetSaleOrderAsync(so)).A("lines").Should().OnlyContain(l => l.D("invoicedQty") == l.D("quantity"));
    }

    [Fact]
    public async Task PF02_the_full_chain_from_sale_order_through_purchasing_and_two_production_levels_to_delivered_and_invoiced()
    {
        // Raw (PURCHASE, nothing in stock) → Sub-assembly (MANUFACTURE, 2 raw each) → Finished good (MANUFACTURE, 1 sub each), make-to-order.
        var w = await _k.MtoWorldAsync("PF02", rawStock: 0m);
        var sub = await _k.SemiFinishedAsync("PF02 Sub", w.WhId);
        await _k.CreateActiveBomAsync(sub, 1m, (w.Raw, 2m));
        var fg = await _k.FinishedGoodAsync("PF02 Assembly", w.WhId, selling: 500m);
        await _k.CreateActiveBomAsync(fg, 1m, (sub, 1m));
        await _k.SetVariantRouteAsync(fg, await _k.RouteUuidAsync(MfgPickPackShip));

        // ── SO → confirm → make-to-order PO, planned: its sub-assembly shortage becomes a chained PO, the raw a purchase ──
        var so = await _k.CreateOrderAsync(w.Customer, w.Pkr, "SHIP", w.Address, RLine(fg, 3m));
        var confirmed = await _k.ConfirmAsync(so);
        var top = confirmed.A("productionOrders").Should().ContainSingle().Which.G("productionOrderUuid");
        (await _k.SoLedgerHeldAsync(so)).Should().Be(0m);

        var topDetail = await SapKit.WaitForAsync(() => _k.ProductionOrderAsync(top), p => p.A("childOrders").Count == 1, "the chained sub-assembly order", 30);
        topDetail.S("status").Should().BeOneOf(PlannedOrLater);
        topDetail.B("isMakeToOrder").Should().BeTrue();
        var child = topDetail.A("childOrders").Single().G("uuid");
        var childDetail = await _k.ProductionOrderAsync(child);
        childDetail.D("plannedQuantity").Should().Be(3m);
        childDetail.S("sourceType").Should().NotBe("SALES_ORDER", "a chained child is sourced from its parent");
        childDetail.B("isMakeToOrder").Should().BeFalse("only the D-17 path sets a route (D-18)");
        (await _k.ProductionOrdersOfAsync(so)).Select(p => p.G("uuid")).Should().Equal(new[] { top }, "the child is sourced from its parent, not the sale order");

        // ── Materials: the purchase order the plan raised, GRN, allocation run (A31 C10) ──
        var purchases = await _k.BuyShortagesAsync(child, w.Wh);
        purchases.Should().ContainSingle("one purchase order for the raw material");
        (await _k.PoStatusAsync(child)).Should().Be("READY");

        // ── Sub-assembly floor → its FGR feeds the parent; no hand-over from a child ──
        await _k.FloorToFgrAsync(child, produced: 3m, accepted: 3m, rejected: 0m);
        (await _k.PoStatusAsync(child)).Should().Be("COMPLETED");
        (await _k.DeliveriesFromPoAsync(child)).Should().BeEmpty("BR-C6-02: the child is not made to order for a sale order");
        (await _k.ProductionOrderAsync(child)).IsNull("deliveryOrderUuid").Should().BeTrue();

        // ── Top floor → QI → FGR → the delivery ──
        await _k.FloorToFgrAsync(top, produced: 3m, accepted: 3m, rejected: 0m);
        var topDone = await _k.ProductionOrderAsync(top);
        topDone.S("status").Should().Be("COMPLETED");
        var d = topDone.G("deliveryOrderUuid");
        var delivery = await _k.DeliveryAsync(d);
        (delivery.S("status"), delivery.S("fulfillmentRouteCode"), delivery.A("lines").Single().D("qtyOrdered")).Should().Be(("DRAFT", MfgPickPackShip, 3m));
        (await _k.SoLedgerHeldAsync(so)).Should().Be(3m, "the FGR's allocation reserved the goods for the line");

        // ── pick → pack → (auto-stage) → goods issue → ship → DELIVERED ──
        var next = new List<string>();
        await _k.CheckNextAsync(next, d, MfgPickPackShip, "DRAFT", "RELEASE");
        await _k.ReleaseAsync(d);
        await _k.CheckNextAsync(next, d, MfgPickPackShip, "RELEASED", "GENERATE_PICK_LIST");
        var pickList = await _k.GeneratePickListAsync(d);
        await _k.CheckNextAsync(next, d, MfgPickPackShip, "PICKING", "CONFIRM_PICK");
        await _k.ConfirmPickAsync(pickList);
        await _k.CheckNextAsync(next, d, MfgPickPackShip, "PICKED", "PACK");
        await _k.PackAllAsync(d, "CRATE");
        await _k.CheckNextAsync(next, d, MfgPickPackShip, "STAGED", "GOODS_ISSUE");
        await _k.Ok(_k.GoodsIssue(d), "goods issue");
        await _k.CheckNextAsync(next, d, MfgPickPackShip, "GOODS_ISSUED", "CREATE_CONSIGNMENT");
        await _k.ShipAndProveAsync(d, await _k.ManualCarrierAsync());
        var delivered = await _k.DeliveryAsync(d);
        (delivered.S("status"), delivered.Tracker()).Should().Be(("DELIVERED", "PICK:DONE,PACK:DONE,GOODS_ISSUE:DONE,SHIP:DONE,COMPLETE:DONE"));
        await _k.CheckNextAsync(next, d, MfgPickPackShip, "DELIVERED", null);
        next.Should().BeEmpty("nextActions' forward actions at each status:\n" + string.Join("\n", next));

        // ── The order: FULFILLED; the production view; invoiced ──
        var done = await _k.GetSaleOrderAsync(so);
        done.S("status").Should().Be("FULFILLED");
        var view = done.A("productionOrders").Single();
        (view.S("status"), view.D("acceptedQuantity"), view.NG("deliveryOrderUuid")).Should().Be(("COMPLETED", 3m, (Guid?)d));
        var invoice = await _k.InvoiceAsync(d);
        (await _k.GetInvoiceAsync(invoice)).S("status").Should().NotBe("DRAFT");
        (await _k.GetSaleOrderAsync(so)).LineOf(fg).D("invoicedQty").Should().Be(3m);
        (await _k.SoLedgerHeldAsync(so)).Should().Be(0m);
    }
}

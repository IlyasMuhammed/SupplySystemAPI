using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using SMS.Shared.Common;
using Xunit;
using static SMS.Integration.Tests.FulfillmentRoutes.Routes;
using static SMS.Integration.Tests.RouteClassification.Rc;

namespace SMS.Integration.Tests.RouteClassification;

/// <summary>
/// A34 C6 on the real host (LocalDB): a make-to-order production order turning COMPLETED hands its accepted quantity
/// to a DRAFT delivery (D-20, D-29), and the shortfall / zero-yield feedback (D-21, D-23).
/// <list type="bullet">
/// <item><b>T-C6-01/05/06/07/08:</b> the delivery's quantity, route snapshot, source SALE_ORDER, the PO's
/// <c>deliveryOrderUuid</c>, the SO's production view; then walked to DELIVERED and invoiced; a replay creates 0.</item>
/// <item><b>T-C6-02:</b> 8 of 10 accepted → a delivery for 8 even with partial fulfilment off, the line's shortfall,
/// PROD_SHORTFALL, and the order stays PARTIALLY_FULFILLED (C-17).</item>
/// <item><b>T-C6-03:</b> QI accepting nothing raises zero yield; no delivery can be made.</item>
/// <item><b>D-20 "Create delivery now":</b> accepted goods ship before completion; the completion then adds nothing.</item>
/// </list>
/// <para>Run alone: <c>dotnet test &lt;out&gt;\SMS.Integration.Tests.dll --filter FullyQualifiedName~ProductionToDeliveryE2ETests</c>.</para>
/// </summary>
public sealed class ProductionToDeliveryE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _k;

    public ProductionToDeliveryE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _k = new SapKit(factory, "C6");
    }

    private async Task<(MtoWorld W, Guid So, Guid Line, Guid Po, string PoNumber, string SoNumber)> ConfirmedMtoAsync(
        string label, decimal qty, string route = MfgPickShip)
    {
        await _k.EnsureNotificationsTableAsync();
        var w = await _k.MtoWorldAsync(label, rawStock: qty * 2 + 10m, routeCode: route);
        var so = await _k.CreateOrderAsync(w.Customer, w.Pkr, "SHIP", w.Address, RLine(w.Fg, qty));
        var created = (await _k.ConfirmAsync(so)).A("productionOrders").Single();
        var detail = await _k.GetSaleOrderAsync(so);
        return (w, so, detail.LineOf(w.Fg).G("uuid"), created.G("productionOrderUuid"), created.S("productionNumber")!, detail.S("soNumber")!);
    }

    [Fact]
    public async Task A_completed_production_order_hands_its_accepted_quantity_to_a_draft_delivery_on_its_route()
    {
        var (w, so, line, po, poNumber, soNumber) = await ConfirmedMtoAsync("HAND", 5m, MfgPickPackShip);

        // Nothing to hand over before QI.
        (await _k.DeliveriesFromPoAsync(po)).Should().BeEmpty();
        var early = await _k.TryCreateDeliveryNow(po);
        early.ShouldBe(HttpStatusCode.BadRequest, "no accepted quantity yet");
        early.Message.Should().Contain($"{poNumber} has no accepted quantity yet");

        await _k.FloorToFgrAsync(po, produced: 5m, accepted: 5m, rejected: 0m);

        // ── T-C6-07: the PO carries the delivery; nothing pending ──
        var p = await _k.ProductionOrderAsync(po);
        (p.S("status"), p.D("acceptedQuantity"), p.B("deliveryCreationPending")).Should().Be(("COMPLETED", 5m, false));
        var deliveryUuid = p.NG("deliveryOrderUuid");
        deliveryUuid.Should().NotBeNull($"D-20: the completion created a delivery — {J.Short(p)}");
        (await _k.DeliveryPendingSinceAsync(po)).Should().BeNull();

        // ── T-C6-01/05/06: one DRAFT delivery for 5 on the PO's route, sourced from the sale order ──
        var fromPo = await _k.DeliveriesFromPoAsync(po);
        fromPo.Should().ContainSingle().Which.G("uuid").Should().Be(deliveryUuid!.Value);
        var d = await _k.DeliveryAsync(deliveryUuid.Value);
        p.S("deliveryNumber").Should().Be(d.S("deliveryNumber"));
        (d.S("status"), d.S("sourceType"), d.NG("sourceUuid"), d.NG("saleOrderUuid"), d.NG("productionOrderUuid"))
            .Should().Be(("DRAFT", "SALE_ORDER", (Guid?)so, (Guid?)so, (Guid?)po), "T-C6-06 / BR-C6-07");
        (d.S("fulfillmentRouteCode"), d.S("deliveryMode")).Should().Be((MfgPickPackShip, "SHIP"));
        (await _k.ShipFromWarehouseAsync(d.G("uuid"))).Should().Be(w.Wh.Uuid, "D-29: where the line's goods are held");
        d.Tracker().Should().Be("PICK:CURRENT,PACK:PENDING,GOODS_ISSUE:PENDING,SHIP:PENDING,COMPLETE:PENDING", "T-C6-05: the route's steps");
        var dl = d.A("lines").Should().ContainSingle().Which;
        (dl.D("qtyOrdered"), dl.NG("soLineUuid")).Should().Be((5m, (Guid?)line));
        d.S("notes").Should().Contain($"From production order {poNumber}");

        // ── T-C6-08: the order's view; the finished goods are held for its line (C-9) ──
        var view = await _k.GetSaleOrderAsync(so);
        view.S("status").Should().Be("CONFIRMED");
        var vp = view.A("productionOrders").Should().ContainSingle().Which;
        (vp.S("status"), vp.D("acceptedQuantity"), vp.NG("deliveryOrderUuid"), vp.S("deliveryNumber")).Should().Be(("COMPLETED", 5m, deliveryUuid, d.S("deliveryNumber")));
        view.LineOf(w.Fg).IsNull("productionShortfallQty").Should().BeTrue("no shortfall");
        (await _k.SoDeliveriesAsync(so)).Select(x => x.G("uuid")).Should().Equal(deliveryUuid.Value);
        (await _k.SoLedgerHeldAsync(so)).Should().Be(5m, "the FGR's allocation moved the goods onto the line");
        (await _k.SoTimelineEventsAsync(so)).Should().Contain("SO_DELIVERY_FROM_PRODUCTION");
        (await _k.NotificationCountAsync(RouteClassificationNotificationTypes.DeliveryFromProduction, so.ToString(), soNumber, d.S("deliveryNumber")!))
            .Should().BeGreaterThan(0, "D-23: the SO creator hears about it");

        // ── A replay creates nothing (D-20 formula) ──
        var replay = await _k.Ok(_k.TryCreateDeliveryNow(po), "create delivery now after the completion");
        (replay.D("quantityCreated"), replay.IsNull("deliveryUuid"), replay.NG("latestDeliveryUuid")).Should().Be((0m, true, deliveryUuid));
        replay.S("skippedReason").Should().NotBeNullOrWhiteSpace();
        (await _k.DeliveriesFromPoAsync(po)).Should().ContainSingle();

        // ── Walked like any A33 delivery: PICK → PACK → (auto-stage) → GOODS_ISSUE → SHIP → DELIVERED, then invoiced ──
        var next = new List<string>();
        await _k.CheckNextAsync(next, d.G("uuid"), MfgPickPackShip, "DRAFT", "RELEASE");
        await _k.ReleaseAsync(d.G("uuid"));
        (await _k.DeliveryLedgerHeldAsync(d.G("uuid"))).Should().Be(5m, "release took the line's holds");
        await _k.PickAllAsync(d.G("uuid"));
        await _k.CheckNextAsync(next, d.G("uuid"), MfgPickPackShip, "PICKED", "PACK");
        await _k.PackAllAsync(d.G("uuid"));
        await _k.CheckNextAsync(next, d.G("uuid"), MfgPickPackShip, "STAGED", "GOODS_ISSUE");
        await _k.Ok(_k.GoodsIssue(d.G("uuid")), "goods issue");
        await _k.CheckNextAsync(next, d.G("uuid"), MfgPickPackShip, "GOODS_ISSUED", "CREATE_CONSIGNMENT");
        await _k.ShipAndProveAsync(d.G("uuid"), await _k.ManualCarrierAsync());
        await _k.CheckNextAsync(next, d.G("uuid"), MfgPickPackShip, "DELIVERED", null);
        next.Should().BeEmpty("nextActions' forward actions at each status:\n" + string.Join("\n", next));

        (await _k.GetSaleOrderAsync(so)).S("status").Should().Be("FULFILLED");
        await _k.InvoiceAsync(d.G("uuid"));
        (await _k.GetSaleOrderAsync(so)).LineOf(w.Fg).D("invoicedQty").Should().Be(5m);
    }

    [Fact]
    public async Task A_yield_shortfall_delivers_what_was_accepted_flags_the_line_and_notifies()
    {
        await _k.SetSaleOrderConfigAsync(("partialFulfillmentAllowed", false));
        try
        {
            var (w, so, _, po, poNumber, soNumber) = await ConfirmedMtoAsync("SHORT", 10m);
            await _k.FloorToFgrAsync(po, produced: 10m, accepted: 8m, rejected: 2m);

            var p = await _k.ProductionOrderAsync(po);
            (p.S("status"), p.D("acceptedQuantity"), p.ND("shortfallQuantity")).Should().Be(("COMPLETED", 8m, (decimal?)2m));
            var d = (await _k.DeliveriesFromPoAsync(po)).Should().ContainSingle().Which;
            var detail = await _k.DeliveryAsync(d.G("uuid"));
            detail.A("lines").Single().D("qtyOrdered").Should().Be(8m, "D-21: production ignores PartialFulfillmentAllowed");

            var line = (await _k.GetSaleOrderAsync(so)).LineOf(w.Fg);
            line.ND("productionShortfallQty").Should().Be(2m, "T-C6-02: the line is flagged");
            (await _k.SoTimelineEventsAsync(so)).Should().Contain("SO_PRODUCTION_SHORTFALL");
            (await _k.NotificationCountAsync(RouteClassificationNotificationTypes.ProductionShortfall, so.ToString(), po.ToString(), soNumber, poNumber))
                .Should().BeGreaterThan(0, "D-23: PROD_SHORTFALL");

            await _k.ShipNoPackAsync(d.G("uuid"), await _k.ManualCarrierAsync());
            var after = await _k.GetSaleOrderAsync(so);
            after.S("status").Should().Be("PARTIALLY_FULFILLED", "C-17: the shortfall can't be short-closed");
            after.LineOf(w.Fg).D("fulfilledQty").Should().Be(8m);
            (await _k.Ok(_k.TryCreateDeliveryNow(po), "nothing more from this PO")).D("quantityCreated").Should().Be(0m);
        }
        finally
        {
            await _k.SetSaleOrderConfigAsync(("partialFulfillmentAllowed", true));
        }
    }

    [Fact]
    public async Task Zero_yield_at_QI_raises_the_shortfall_and_no_delivery_can_be_made()
    {
        var (w, so, _, po, poNumber, soNumber) = await ConfirmedMtoAsync("ZERO", 3m);
        await _k.IssueAndStartAsync(po);
        await _k.ReportAndCompleteAsync(po, 3m);
        await _k.Ok(_k.TryInspect(po, await _k.InspectorAsync(), accepted: 0m, rejected: 3m), "QI rejecting everything");

        var p = await _k.ProductionOrderAsync(po);
        (p.S("status"), p.D("acceptedQuantity")).Should().Be(("QUALITY_INSPECTION", 0m), "D-21: unchanged A30 behaviour");
        p.ND("shortfallQuantity").Should().Be(3m, "contract §7 (REV-02): zero yield = planned");
        (await _k.TryFgr(po, 1m)).Status.Should().NotBe(HttpStatusCode.OK, "nothing was accepted");
        (await _k.DeliveriesFromPoAsync(po)).Should().BeEmpty("T-C6-03");
        (await _k.TryCreateDeliveryNow(po)).ShouldBe(HttpStatusCode.BadRequest, "no accepted quantity");

        (await _k.GetSaleOrderAsync(so)).LineOf(w.Fg).ND("productionShortfallQty").Should().Be(3m, "zero yield = the whole line");
        (await _k.SoTimelineEventsAsync(so)).Should().Contain("SO_PRODUCTION_SHORTFALL");
        (await _k.NotificationCountAsync(RouteClassificationNotificationTypes.ProductionZeroYield, so.ToString(), po.ToString(), soNumber, poNumber))
            .Should().BeGreaterThan(0, "D-23: PROD_ZERO_YIELD");
        (await _k.SoDeliveriesAsync(so)).Should().BeEmpty();
    }

    [Fact]
    public async Task Create_delivery_now_ships_accepted_goods_before_completion_and_the_completion_adds_nothing()
    {
        var (w, so, line, po, _, _) = await ConfirmedMtoAsync("EARLY", 6m);
        await _k.IssueAndStartAsync(po);
        await _k.ReportAndCompleteAsync(po, 6m);
        await _k.Ok(_k.TryInspect(po, await _k.InspectorAsync(), accepted: 6m, rejected: 0m), "QI");

        // The PO's accepted quantity is what FGR has received (A30: AcceptedQuantity += each FGR), so after QI alone
        // there is nothing to ship yet.
        (await _k.TryCreateDeliveryNow(po)).ShouldBe(HttpStatusCode.BadRequest, "QI accepted 6, but nothing is received yet");

        // A first, partial FGR: the PO is not COMPLETED, so nothing is handed over automatically.
        await _k.Ok(_k.TryFgr(po, 4m), "FGR 4 of 6");
        (await _k.PoStatusAsync(po)).Should().NotBe("COMPLETED");
        (await _k.DeliveriesFromPoAsync(po)).Should().BeEmpty("D-20: the automatic hand-over waits for COMPLETED");

        // "Create delivery now" (DELIVERY_CREATE) ships the 4 received early: min(accepted 4 − 0 on deliveries, outstanding 6).
        var now = await _k.Ok(_k.TryCreateDeliveryNow(po), "create delivery now");
        (now.D("quantityCreated"), now.S("skippedReason")).Should().Be((4m, null));
        var d1 = now.G("deliveryUuid");
        now.NG("latestDeliveryUuid").Should().Be(d1);
        (await _k.ProductionOrderAsync(po)).NG("deliveryOrderUuid").Should().Be(d1, "the button stamps the PO too");
        (await _k.DeliveryAsync(d1)).A("lines").Single().NG("soLineUuid").Should().Be(line);
        (await _k.Ok(_k.TryCreateDeliveryNow(po), "a second click")).D("quantityCreated").Should().Be(0m, "nothing more is received");

        // The completion hands over only the rest: min(6 − 4, 6 − 4 in flight) = 2, on a second delivery.
        await _k.Ok(_k.TryFgr(po, 2m), "FGR the last 2");
        (await _k.PoStatusAsync(po)).Should().Be("COMPLETED");
        var all = await _k.DeliveriesFromPoAsync(po);
        all.Should().HaveCount(2);
        var d2 = all.Single(x => x.G("uuid") != d1).G("uuid");
        (await _k.DeliveryAsync(d2)).A("lines").Single().D("qtyOrdered").Should().Be(2m);
        (await _k.QtyOnLiveDeliveriesFromPoAsync(po)).Should().Be(6m, "the D-20 formula makes the button and the hook safe together");
        var p = await _k.ProductionOrderAsync(po);
        (p.NG("deliveryOrderUuid"), p.B("deliveryCreationPending")).Should().Be(((Guid?)d2, false), "the PO names its latest delivery");
        (await _k.Ok(_k.TryCreateDeliveryNow(po), "again")).D("quantityCreated").Should().Be(0m);
        (await _k.GetSaleOrderAsync(so)).A("productionOrders").Single().NG("deliveryOrderUuid").Should().Be(d2);
        _ = w;
    }
}

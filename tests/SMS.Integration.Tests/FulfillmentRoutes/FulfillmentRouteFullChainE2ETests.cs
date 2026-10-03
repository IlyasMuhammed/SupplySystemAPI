using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using Xunit;
using static SMS.Integration.Tests.FulfillmentRoutes.Routes;

namespace SMS.Integration.Tests.FulfillmentRoutes;

/// <summary>
/// A33-PF-01: the whole chain on the real host (LocalDB). It runs route setup → variant routes → a SHIP sale order
/// whose lines take their route from the variant, a line override and the org default → the delivery preview →
/// confirm → the deliveries auto-created per route (D-1, BR-C4-01..05) → each delivery walked through its route
/// in the repo's real statuses (D-2) with the steps it lacks auto-completed (D-3) → the order FULFILLED and
/// every delivery invoiced.
/// <list type="bullet">
/// <item>A custom route with every step (PICK, PACK, STAGE, APPROVAL, GOODS_ISSUE, SHIP) is packed by hand, staged
/// and approved through <c>advance</c> / <c>approve</c> (D-7). Goods issue is refused before the approval.</item>
/// <item>PICK_AND_SHIP (the org's SHIP default, L-1) gets an auto LOOSE unit and auto-stage at confirm-pick.</item>
/// <item>Both ship through a manual carrier's consignment and a proof of delivery. Record collection is refused
/// on both, because their routes have SHIP.</item>
/// </list>
/// A second fact covers T-C4-01: three lines on the same route make one delivery with three lines.
/// <para>Run alone: <c>dotnet test &lt;out&gt;\SMS.Integration.Tests.dll --filter FullyQualifiedName~FulfillmentRouteFullChainE2ETests</c>.</para>
/// </summary>
public sealed class FulfillmentRouteFullChainE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _k;

    public FulfillmentRouteFullChainE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _k = new SapKit(factory, "R1");
    }

    private async Task<(Guid Pkr, Guid Gst, Warehouse Wh, Partner Vendor, Partner Customer)> SetupAsync()
    {
        var pkr = await _k.PkrBaseAsync();
        var gst = await _k.EnsureTaxCodeAsync("GST17", 17m, "SALES", isDefault: true);
        await _k.EnsureTaxCodeAsync("PGST17", 17m, "PURCHASE", isDefault: true);
        await _k.CreateApproverPlaceholdersAsync();
        return (pkr, gst, await _k.CreateWarehouseAsync(), await _k.CreateVendorAsync("Chain Vendor"), await _k.CreateCustomerAsync("Chain Customer"));
    }

    [Fact]
    public async Task Routes_to_variants_to_a_routed_order_to_auto_created_deliveries_walked_to_delivered_and_invoiced()
    {
        var (pkr, gst, wh, vendor, customer) = await SetupAsync();
        var address = await _k.ShippingAddressAsync(customer);

        // ── Routes: the three seeds (D-6, L-1), and a custom route with every step ──
        var seeds = await _k.RoutesAsync();
        seeds.Select(r => r.S("code")).Should().Contain(new[] { PickOnly, PickAndShip, PickPackShip });
        var pickAndShip = seeds.Single(r => r.S("code") == PickAndShip);
        (pickAndShip.B("isDefault"), pickAndShip.B("isSystem"), pickAndShip.B("requiresShipping"), pickAndShip.B("requiresPacking"))
            .Should().Be((true, true, true, false), "PICK_AND_SHIP is the SHIP-class default seed");
        seeds.Single(r => r.S("code") == PickOnly).B("isDefault").Should().BeTrue("PICK_ONLY is the SELF_PICKUP-class default (L-1)");

        var full = await _k.CreateRouteAsync("FULL", "PICK", "PACK", "STAGE", "APPROVAL", "GOODS_ISSUE", "SHIP");
        var fullUuid = full.G("uuid");
        var fullCode = full.S("code")!;
        (full.B("requiresPacking"), full.B("requiresShipping"), full.B("isDefault"), full.B("isSystem")).Should().Be((true, true, false, false));
        full.A("steps").Select(s => s.S("stepCode")).Should().Equal("PICK", "PACK", "STAGE", "APPROVAL", "GOODS_ISSUE", "SHIP");

        // ── Variants: A carries the custom route; B and C have none ──
        var a = await _k.CreateProductAsync("Valve Body", purchasePrice: 40m, sellingPrice: 90m);
        var b = await _k.CreateProductAsync("Valve Seal", purchasePrice: 4m, sellingPrice: 10m);
        var c = await _k.CreateProductAsync("Hydraulic Drum", purchasePrice: 200m, sellingPrice: 350m);
        await _k.StockUpAsync(vendor, wh, (a, 20m, 40m), (b, 50m, 4m), (c, 10m, 200m));
        await _k.SetVariantRouteAsync(a, fullUuid);
        var variant = (await _k.Ok(_k.Get($"/api/products/{a.ProductId}"), "read product")).A("variants").Single();
        (variant.NG("fulfillmentRouteUuid"), variant.S("fulfillmentRouteCode")).Should().Be((fullUuid, fullCode), "the variant read shows its route");

        // ── The order: A by its variant, B by a line override, C by the org's SHIP default ──
        var so = await _k.CreateOrderAsync(customer, pkr, "SHIP", address,
            RLine(a, 6m, taxCode: gst), RLine(b, 12m, route: fullUuid, taxCode: gst), RLine(c, 2m, taxCode: gst));
        var draft = await _k.GetSaleOrderAsync(so);
        draft.B("routesEnabled").Should().BeTrue();
        draft.A("confirmBlockers").Should().BeEmpty();
        JsonElement LineOf(JsonElement doc, Product p) => doc.A("lines").Single(l => l.G("variantUuid") == p.VariantUuid);
        (LineOf(draft, a).S("routeSource"), LineOf(draft, a).S("effectiveRouteCode")).Should().Be(("VARIANT", fullCode), "T-C3-01");
        (LineOf(draft, b).S("routeSource"), LineOf(draft, b).S("effectiveRouteCode"), LineOf(draft, b).NG("fulfillmentRouteUuid"))
            .Should().Be(("LINE_OVERRIDE", fullCode, fullUuid), "T-C3-02");
        (LineOf(draft, c).S("routeSource"), LineOf(draft, c).S("effectiveRouteCode")).Should().Be(("ORG_DEFAULT", PickAndShip), "T-C3-03");
        LineOf(draft, c).A("effectiveRouteSteps").Select(s => s.GetString()).Should().Equal("PICK", "GOODS_ISSUE", "SHIP");

        // ── Preview (BR-C3-05): two groups, A+B on the custom route, C on PICK_AND_SHIP, both SHIP ──
        var preview = await _k.Ok(_k.Get($"/api/sale-orders/{so}/delivery-preview"), "delivery preview");
        (preview.B("routesEnabled"), preview.B("canConfirm"), preview.I("deliveryCount")).Should().Be((true, true, 2));
        var groups = preview.A("groups");
        groups.Single(g => g.S("routeCode") == fullCode).A("lineNumbers").Select(n => n.GetInt32()).Should().BeEquivalentTo(new[] { 1, 2 });
        groups.Single(g => g.S("routeCode") == PickAndShip).A("lineNumbers").Select(n => n.GetInt32()).Should().Equal(3);
        groups.Should().OnlyContain(g => g.S("deliveryMode") == "SHIP");

        // ── Confirm → two DRAFT deliveries, one per route (T-C4-03) ──
        var confirmed = await _k.ConfirmAsync(so);
        confirmed.B("deliveryCreationFailed").Should().BeFalse($"auto-create ran — {J.Short(confirmed)}");
        var created = confirmed.A("deliveries");
        created.Should().HaveCount(2);
        created.Single(d => d.S("routeCode") == fullCode).I("lineCount").Should().Be(2);
        created.Single(d => d.S("routeCode") == PickAndShip).I("lineCount").Should().Be(1);
        created.Should().OnlyContain(d => d.S("deliveryMode") == "SHIP" && d.NG("shipFromWarehouseUuid") == wh.Uuid);

        var soDeliveries = await _k.SoDeliveriesAsync(so);
        soDeliveries.Should().HaveCount(2);
        soDeliveries.Should().OnlyContain(d => d.S("status") == "DRAFT" && d.S("sourceType") == "SALE_ORDER", "BR-C4-03/04");
        soDeliveries.Should().OnlyContain(d => d.NG("saleOrderUuid") == so);

        var dFull = created.Single(d => d.S("routeCode") == fullCode).G("deliveryUuid");
        var dPas  = created.Single(d => d.S("routeCode") == PickAndShip).G("deliveryUuid");

        // Each delivery line links back to its SO line (T-C4-05), and the route snapshot is on the delivery (D-10).
        var confirmedSo = await _k.GetSaleOrderAsync(so);
        var fullDetail = await _k.DeliveryAsync(dFull);
        fullDetail.A("lines").Select(l => l.NG("soLineUuid")).Should()
            .BeEquivalentTo(new Guid?[] { LineOf(confirmedSo, a).G("uuid"), LineOf(confirmedSo, b).G("uuid") });
        (await _k.DeliveryAsync(dPas)).A("lines").Single().NG("soLineUuid").Should().Be(LineOf(confirmedSo, c).G("uuid"));
        (fullDetail.NG("fulfillmentRouteUuid"), fullDetail.S("fulfillmentRouteCode"), fullDetail.B("requiresApproval")).Should().Be((fullUuid, fullCode, true));

        // D-16: the confirmed lines keep the resolved route and its source; T-C3-08: they are locked now.
        (LineOf(confirmedSo, a).S("routeSource"), LineOf(confirmedSo, b).S("routeSource"), LineOf(confirmedSo, c).S("routeSource"))
            .Should().Be(("VARIANT", "LINE_OVERRIDE", "ORG_DEFAULT"));
        LineOf(confirmedSo, c).NG("fulfillmentRouteUuid").Should().Be(pickAndShip.G("uuid"), "the snapshot is the confirmed route");
        (await _k.Put($"/api/sale-orders/{so}", new
        {
            PartnerId = customer.Uuid, CurrencyId = pkr, DeliveryMode = "SHIP", ShippingAddressId = address,
            Lines = new[] { RLine(a, 6m, route: pickAndShip.G("uuid"), taxCode: gst) }
        })).ShouldBe(HttpStatusCode.BadRequest, "T-C3-08: a confirmed order's line routes are locked");

        // C-2: DRAFT deliveries hold nothing, so the lines are still held by the order alone.
        foreach (var l in confirmedSo.A("lines"))
            (l.D("reservedQty"), l.D("reservableQty"), l.S("deliveryIndicator")).Should().Be((l.D("quantity"), 0m, "BLUE"));
        (await _k.SoLedgerHeldAsync(so)).Should().Be(20m);

        // ── Walk the custom route: every step by hand ──
        fullDetail.Tracker().Should().Be("PICK:CURRENT,PACK:PENDING,STAGE:PENDING,APPROVAL:PENDING,GOODS_ISSUE:PENDING,SHIP:PENDING,COMPLETE:PENDING");
        // nextActions must name exactly one forward action at every status (the screen's one forward button).
        var next = new List<string>();
        var R = fullCode;
        await _k.CheckNextAsync(next, dFull, R, "DRAFT", "RELEASE");

        var adv = await _k.Ok(_k.Advance(dFull, "DRAFT"), "advance DRAFT → release");
        (adv.S("previousStatus"), adv.S("status")).Should().Be(("DRAFT", "RELEASED"));
        (await _k.DeliveryLedgerHeldAsync(dFull)).Should().Be(18m, "release moves the order's hold onto the delivery");
        await _k.CheckNextAsync(next, dFull, R, "RELEASED", "GENERATE_PICK_LIST");

        var pickList = await _k.GeneratePickListAsync(dFull);
        await _k.CheckNextAsync(next, dFull, R, "PICKING", "CONFIRM_PICK");
        await _k.ConfirmPickAsync(pickList);
        await _k.CheckNextAsync(next, dFull, R, "PICKED", "PACK");
        var picked = await _k.DeliveryAsync(dFull);
        picked.S("status").Should().Be("PICKED", "T-C5-05: a route with PACK stops at PICKED for packing");
        picked.Tracker().Should().StartWith("PICK:DONE,PACK:CURRENT");
        (await _k.Advance(dFull)).ShouldBe(HttpStatusCode.Conflict, "PICKED → packing needs the pack station");

        await _k.PackAllAsync(dFull, "BOX");
        var packed = await _k.DeliveryAsync(dFull);
        packed.S("status").Should().Be("PACKED", "T-C5-07: a route with STAGE is not auto-staged");
        packed.Tracker().Should().StartWith("PICK:DONE,PACK:DONE,STAGE:CURRENT");
        await _k.CheckNextAsync(next, dFull, R, "PACKED", "STAGE");
        (await _k.Advance(dFull, "PICKED")).ShouldBe(HttpStatusCode.Conflict, "a stale expectedStatus is a 409");

        adv = await _k.Ok(_k.Advance(dFull, "PACKED"), "advance PACKED → stage");
        (adv.S("previousStatus"), adv.S("status")).Should().Be(("PACKED", "STAGED"));
        await _k.CheckNextAsync(next, dFull, R, "STAGED", "APPROVE");

        (await _k.GoodsIssue(dFull)).ShouldBe(HttpStatusCode.BadRequest, "D-7: no goods issue before the dispatch is approved");
        var staged = await _k.DeliveryAsync(dFull);
        staged.Tracker().Should().StartWith("PICK:DONE,PACK:DONE,STAGE:DONE,APPROVAL:CURRENT");
        staged.NextActions().Should().Contain("APPROVE");

        await _k.Ok(_k.Approve(dFull), "approve dispatch");
        var approved = await _k.DeliveryAsync(dFull);
        (approved.S("status"), approved.IsNull("approvedAt"), approved.IsNull("approvedBy")).Should().Be(("STAGED", false, false));
        approved.Tracker().Should().StartWith("PICK:DONE,PACK:DONE,STAGE:DONE,APPROVAL:DONE,GOODS_ISSUE:CURRENT");
        await _k.Ok(_k.Approve(dFull), "approving again is a 200 no-op");
        await _k.CheckNextAsync(next, dFull, R, "STAGED", "GOODS_ISSUE");

        adv = await _k.Ok(_k.Advance(dFull, "STAGED"), "advance STAGED → goods issue");
        (adv.S("previousStatus"), adv.S("status")).Should().Be(("STAGED", "GOODS_ISSUED"));
        var issued = await _k.DeliveryAsync(dFull);
        issued.Tracker().Should().EndWith("GOODS_ISSUE:DONE,SHIP:CURRENT,COMPLETE:PENDING");
        await _k.CheckNextAsync(next, dFull, R, "GOODS_ISSUED", "CREATE_CONSIGNMENT");
        (await _k.RecordCollection(dFull)).ShouldBe(HttpStatusCode.BadRequest, "a route with SHIP is not collected");
        (await _k.Advance(dFull)).ShouldBe(HttpStatusCode.Conflict, "GOODS_ISSUED on a SHIP route needs a consignment");

        var carrier = await _k.ManualCarrierAsync();
        await _k.ShipAndProveAsync(dFull, carrier);
        var delivered = await _k.DeliveryAsync(dFull);
        delivered.S("status").Should().Be("DELIVERED");
        delivered.Tracker().Should().Be("PICK:DONE,PACK:DONE,STAGE:DONE,APPROVAL:DONE,GOODS_ISSUE:DONE,SHIP:DONE,COMPLETE:DONE");
        await _k.CheckNextAsync(next, dFull, R, "DELIVERED", null);

        (await _k.GetSaleOrderAsync(so)).S("status").Should().Be("PARTIALLY_FULFILLED", "one of the two deliveries is in");

        // ── Walk PICK_AND_SHIP: no PACK, no STAGE → auto LOOSE unit + auto-stage at confirm-pick (D-3) ──
        (await _k.DeliveryAsync(dPas)).Tracker().Should().Be("PICK:CURRENT,GOODS_ISSUE:PENDING,SHIP:PENDING,COMPLETE:PENDING");
        await _k.ReleaseAsync(dPas);
        await _k.PickAllAsync(dPas);
        var autoStaged = await _k.DeliveryAsync(dPas);
        autoStaged.S("status").Should().Be("STAGED", "T-C5-03: PICKED passes PACKED and STAGED automatically");
        autoStaged.Tracker().Should().Be("PICK:DONE,GOODS_ISSUE:CURRENT,SHIP:PENDING,COMPLETE:PENDING");
        autoStaged.A("lines").Single().D("qtyPacked").Should().Be(2m);
        var units = await _k.PackagesAsync(dPas);
        units.Should().ContainSingle().Which.S("packageType").Should().Be("LOOSE");

        await _k.Ok(_k.GoodsIssue(dPas), "goods issue");
        (await _k.DeliveryAsync(dPas)).Tracker().Should().Be("PICK:DONE,GOODS_ISSUE:DONE,SHIP:CURRENT,COMPLETE:PENDING", "T-C5-04: next is the SHIP step");
        await _k.ShipAndProveAsync(dPas, carrier);
        (await _k.DeliveryStatusAsync(dPas)).Should().Be("DELIVERED");
        next.Should().BeEmpty("nextActions' forward actions at each status (first = the primary button):\n" + string.Join("\n", next));

        // ── The order is fulfilled in full and both deliveries invoice ──
        var done = await _k.GetSaleOrderAsync(so);
        done.S("status").Should().Be("FULFILLED");
        done.A("lines").Should().OnlyContain(l => l.D("fulfilledQty") == l.D("quantity"));
        (await _k.SoLedgerHeldAsync(so)).Should().Be(0m);

        var inv1 = await _k.InvoiceAsync(dFull);
        var inv2 = await _k.InvoiceAsync(dPas);
        (await _k.GetInvoiceAsync(inv1)).S("status").Should().NotBe("DRAFT");
        (await _k.GetInvoiceAsync(inv2)).S("status").Should().NotBe("DRAFT");
        (await _k.GetSaleOrderAsync(so)).A("lines").Should().OnlyContain(l => l.D("invoicedQty") == l.D("quantity"));
    }

    [Fact]
    public async Task Three_lines_on_one_route_make_one_delivery_with_three_lines()
    {
        var (pkr, _, wh, vendor, customer) = await SetupAsync();
        var p1 = await _k.CreateProductAsync("Bolt", 1m, 3m);
        var p2 = await _k.CreateProductAsync("Nut", 1m, 2m);
        var p3 = await _k.CreateProductAsync("Washer", 1m, 1m);
        await _k.StockUpAsync(vendor, wh, (p1, 30m, 1m), (p2, 30m, 1m), (p3, 30m, 1m));

        // SELF_PICKUP, no variant routes, no overrides → all three resolve PICK_ONLY, the SELF_PICKUP default (L-1).
        var so = await _k.CreateOrderAsync(customer, pkr, "SELF_PICKUP", null, RLine(p1, 5m), RLine(p2, 5m), RLine(p3, 5m));
        (await _k.GetSaleOrderAsync(so)).A("lines").Should().OnlyContain(l => l.S("routeSource") == "ORG_DEFAULT" && l.S("effectiveRouteCode") == PickOnly);

        var confirmed = await _k.ConfirmAsync(so);
        var only = confirmed.A("deliveries").Should().ContainSingle("T-C4-01").Which;
        (only.S("routeCode"), only.I("lineCount"), only.S("deliveryMode")).Should().Be((PickOnly, 3, "SELF_PICKUP"));
        var d = only.G("deliveryUuid");
        (await _k.DeliveryAsync(d)).A("lines").Should().HaveCount(3);
        (await _k.DeliveryAsync(d)).Tracker().Should().Be("PICK:CURRENT,GOODS_ISSUE:PENDING,COMPLETE:PENDING", "T-C5-09");

        // Walk it: release → pick (auto-pack + auto-stage) → goods issue → record collection → DELIVERED.
        await _k.ReleaseAsync(d);
        await _k.PickAllAsync(d);
        (await _k.DeliveryStatusAsync(d)).Should().Be("STAGED", "T-C5-01: PICKED → next route step is GOODS_ISSUE");
        await _k.Ok(_k.GoodsIssue(d), "goods issue");
        var issued = await _k.DeliveryAsync(d);
        issued.Tracker().Should().Be("PICK:DONE,GOODS_ISSUE:DONE,COMPLETE:CURRENT");
        issued.NextActions().Should().Contain("RECORD_COLLECTION").And.NotContain("CREATE_CONSIGNMENT");
        await _k.Ok(_k.RecordCollection(d), "record collection");
        var collected = await _k.DeliveryAsync(d);
        (collected.S("status"), collected.Tracker()).Should().Be(("DELIVERED", "PICK:DONE,GOODS_ISSUE:DONE,COMPLETE:DONE"), "T-C5-02");

        (await _k.GetSaleOrderAsync(so)).S("status").Should().Be("FULFILLED");
        await _k.InvoiceAsync(d);
    }

    /// <summary>
    /// D-10 / R-9 / L-6: a delivery keeps the steps its route had when it was created. Editing the route later
    /// changes only new deliveries. While open lines use the route it can't be deactivated, and once deliveries carry
    /// it, it can't be deleted (deactivate instead). ON_HOLD keeps the tracker where it was (statusBeforeHold), and
    /// RESUME goes on from there.
    /// </summary>
    [Fact]
    public async Task A_route_edited_or_retired_after_its_deliveries_exist_leaves_them_on_their_snapshot()
    {
        var (pkr, _, wh, vendor, customer) = await SetupAsync();
        var address = await _k.ShippingAddressAsync(customer);
        var p = await _k.CreateProductAsync("Snapshot Widget", 5m, 9m);
        await _k.StockUpAsync(vendor, wh, (p, 20m, 5m));
        var route = await _k.CreateRouteAsync("SNAP", "PICK", "GOODS_ISSUE", "SHIP");
        var r = route.G("uuid");

        var so1 = await _k.CreateOrderAsync(customer, pkr, "SHIP", address, RLine(p, 2m, route: r));
        var d1 = (await _k.ConfirmAsync(so1)).A("deliveries").Single().G("deliveryUuid");

        // The route gains PACK after d1 exists.
        var edited = await _k.Ok(_k.Put($"/api/fulfillment-routes/{r}", new
        {
            Name = route.S("name"), DisplayOrder = route.I("displayOrder"), Steps = Steps("PICK", "PACK", "GOODS_ISSUE", "SHIP")
        }), "add PACK to the route");
        edited.B("requiresPacking").Should().BeTrue();

        var so2 = await _k.CreateOrderAsync(customer, pkr, "SHIP", address, RLine(p, 3m, route: r));
        var d2 = (await _k.ConfirmAsync(so2)).A("deliveries").Single().G("deliveryUuid");
        (await _k.DeliveryAsync(d1)).Tracker().Should().Be("PICK:CURRENT,GOODS_ISSUE:PENDING,SHIP:PENDING,COMPLETE:PENDING", "D-10: d1 keeps its snapshot");
        (await _k.DeliveryAsync(d2)).Tracker().Should().Be("PICK:CURRENT,PACK:PENDING,GOODS_ISSUE:PENDING,SHIP:PENDING,COMPLETE:PENDING");

        // d1 still runs its own steps: no PACK → auto LOOSE unit + auto-stage.
        await _k.ReleaseAsync(d1);
        await _k.PickAllAsync(d1);
        (await _k.DeliveryStatusAsync(d1)).Should().Be("STAGED", "the snapshot has no PACK");
        await _k.ReleaseAsync(d2);
        await _k.PickAllAsync(d2);
        (await _k.DeliveryStatusAsync(d2)).Should().Be("PICKED", "the new delivery has PACK");

        // ON_HOLD: the tracker stays where it was, and resume goes back to STAGED.
        var staged = (await _k.DeliveryAsync(d1)).Tracker();
        await _k.Ok(_k.Post($"/api/logistics/deliveries/{d1}/hold", new { Reason = "customer asked to wait" }), "hold");
        var held = await _k.DeliveryAsync(d1);
        (held.S("status"), held.S("statusBeforeHold"), held.Tracker()).Should().Be(("ON_HOLD", "STAGED", staged));
        held.NextActions().Where(ForwardActions.Contains).Should().BeEmpty("nothing moves forward while on hold");
        held.NextActions().Should().Contain("RESUME");
        (await _k.GoodsIssue(d1)).Status.Should().NotBe(HttpStatusCode.OK, "no goods issue while on hold");
        await _k.Ok(_k.Post($"/api/logistics/deliveries/{d1}/resume", new { }), "resume");
        (await _k.DeliveryStatusAsync(d1)).Should().Be("STAGED");

        // BR-C1-07: open lines use it, so it can't be deactivated; L-6: deliveries carry it, so it can't be deleted.
        (await _k.RoutePatch(r, "deactivate")).ShouldBe(HttpStatusCode.Conflict, "open sale order lines use the route");
        (await _k.Delete($"/api/fulfillment-routes/{r}")).ShouldBe(HttpStatusCode.Conflict, "in use");
        await _k.Ok(_k.Post($"/api/sale-orders/{so1}/cancel", new { Reason = "retire the route" }), "cancel order 1");
        await _k.Ok(_k.Post($"/api/sale-orders/{so2}/cancel", new { Reason = "retire the route" }), "cancel order 2");
        (await _k.DeliveryStatusAsync(d1)).Should().Be("CANCELLED");
        await _k.Ok(_k.RoutePatch(r, "deactivate"), "nothing open uses it now");
        var del = await _k.Delete($"/api/fulfillment-routes/{r}");
        del.ShouldBe(HttpStatusCode.Conflict, "L-6: deliveries still carry the route, so it is kept for history");
        (await _k.DeliveryAsync(d1)).S("fulfillmentRouteCode").Should().Be(route.S("code"));
    }

    /// <summary>
    /// D-7 with no SHIP step (REV): Record collection issues the stock itself, so it is a second way past the
    /// approval. Until the dispatch is approved, both goods issue and collection are refused, and nextActions offers
    /// only APPROVE. Once approved, the counter collection from STAGED (issue + DELIVERED in one call, as today)
    /// works on a routed delivery.
    /// </summary>
    [Fact]
    public async Task An_approval_route_without_SHIP_cannot_be_collected_or_issued_before_the_approval()
    {
        var (pkr, _, wh, vendor, customer) = await SetupAsync();
        var p = await _k.CreateProductAsync("Controlled Reagent", 50m, 80m);
        await _k.StockUpAsync(vendor, wh, (p, 10m, 50m));
        var route = await _k.CreateRouteAsync("APCOL", "PICK", "APPROVAL", "GOODS_ISSUE");
        (route.B("requiresShipping"), route.B("requiresPacking")).Should().Be((false, false));
        var code = route.S("code")!;

        var so = await _k.CreateOrderAsync(customer, pkr, "SELF_PICKUP", null, RLine(p, 3m, route: route.G("uuid")));
        var created = (await _k.ConfirmAsync(so)).A("deliveries").Should().ContainSingle().Which;
        created.S("deliveryMode").Should().Be("SELF_PICKUP");
        var d = created.G("deliveryUuid");

        var next = new List<string>();
        await _k.CheckNextAsync(next, d, code, "DRAFT", "RELEASE");
        await _k.ReleaseAsync(d);
        await _k.PickAllAsync(d);
        await _k.CheckNextAsync(next, d, code, "STAGED", "APPROVE");
        (await _k.DeliveryAsync(d)).Tracker().Should().Be("PICK:DONE,APPROVAL:CURRENT,GOODS_ISSUE:PENDING,COMPLETE:PENDING");

        (await _k.GoodsIssue(d)).ShouldBe(HttpStatusCode.BadRequest, "D-7: no goods issue before the approval");
        (await _k.RecordCollection(d)).ShouldBe(HttpStatusCode.BadRequest, "D-7: collection issues the stock too, so it waits for the approval");
        var stillStaged = await _k.DeliveryAsync(d);
        (stillStaged.S("status"), stillStaged.IsNull("pickedUpAt"), stillStaged.A("lines").Single().D("qtyShipped")).Should().Be(("STAGED", true, 0m), "nothing left the books");
        (await _k.DeliveryLedgerHeldAsync(d)).Should().Be(3m, "the delivery still holds its stock");

        await _k.Ok(_k.Approve(d), "approve dispatch");
        await _k.CheckNextAsync(next, d, code, "STAGED", "GOODS_ISSUE", "RECORD_COLLECTION");

        // Counter collection straight from STAGED: issue + DELIVERED in one call.
        var pickup = await _k.Ok(_k.RecordCollection(d), "record collection after the approval");
        pickup.IsNull("goodsIssue").Should().BeFalse("this call is what took the stock off the books");
        var done = await _k.DeliveryAsync(d);
        (done.S("status"), done.Tracker()).Should().Be(("DELIVERED", "PICK:DONE,APPROVAL:DONE,GOODS_ISSUE:DONE,COMPLETE:DONE"));
        await _k.CheckNextAsync(next, d, code, "DELIVERED");
        next.Should().BeEmpty("nextActions' forward actions at each status:\n" + string.Join("\n", next));

        (await _k.GetSaleOrderAsync(so)).S("status").Should().Be("FULFILLED");
        await _k.InvoiceAsync(d);
    }
}

using System.Net;
using FluentAssertions;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using Xunit;
using static SMS.Integration.Tests.FulfillmentRoutes.Routes;

namespace SMS.Integration.Tests.FulfillmentRoutes;

/// <summary>
/// A33-PF-02: the spec's Punjab example (§6.4) on the real host (LocalDB). One SHIP order has three lines on three
/// routes:
/// <list type="bullet">
/// <item>Steel Pipes 500, PICK_ONLY (the variant's route);</item>
/// <item>Copper Wire 600, PICK_PACK_SHIP (the variant's route);</item>
/// <item>Synth Hyd Oil 100, PICK_AND_SHIP (a line override over the variant's PICK_PACK_SHIP).</item>
/// </list>
/// The preview shows three groups (T-C3-09), and confirm creates three deliveries with one line each (T-C4-02).
/// Each is walked in the real statuses:
/// <list type="bullet">
/// <item>PICK_ONLY is a SELF_PICKUP delivery (D-4). Pick auto-packs and auto-stages it, goods issue follows, then
/// Record collection makes it DELIVERED. A consignment is refused (no SHIP).</item>
/// <item>PICK_PACK_SHIP stops at PICKED (T-C5-05) and is packed by hand into two boxes. Reaching PACKED auto-stages
/// it (T-C5-06), then goods issue, and a consignment with a proof of delivery makes it DELIVERED.</item>
/// <item>PICK_AND_SHIP gets an auto LOOSE unit holding the 100 drums and is auto-staged (T-C5-03), then goods
/// issue (T-C5-04: next is SHIP), and a consignment carries it to DELIVERED.</item>
/// </list>
/// The order ends FULFILLED, and all three deliveries invoice.
/// <para>Run alone: <c>dotnet test &lt;out&gt;\SMS.Integration.Tests.dll --filter FullyQualifiedName~PunjabMixedRoutesE2ETests</c>.</para>
/// </summary>
public sealed class PunjabMixedRoutesE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapKit _k;

    public PunjabMixedRoutesE2ETests(SapWebApplicationFactory factory) => _k = new SapKit(factory, "PJB");

    [Fact]
    public async Task One_order_three_routes_three_deliveries_each_walked_its_own_way_to_delivered_and_invoiced()
    {
        var pkr = await _k.PkrBaseAsync();
        var gst = await _k.EnsureTaxCodeAsync("GST17", 17m, "SALES", isDefault: true);
        await _k.EnsureTaxCodeAsync("PGST17", 17m, "PURCHASE", isDefault: true);
        await _k.CreateApproverPlaceholdersAsync();
        var wh       = await _k.CreateWarehouseAsync();
        var vendor   = await _k.CreateVendorAsync("Punjab Mills");
        var customer = await _k.CreateCustomerAsync("Punjab Group");
        var address  = await _k.ShippingAddressAsync(customer, "Faisalabad");

        var pickOnly     = await _k.RouteUuidAsync(PickOnly);
        var pickPackShip = await _k.RouteUuidAsync(PickPackShip);
        var pickAndShip  = await _k.RouteUuidAsync(PickAndShip);

        var pipes  = await _k.CreateProductAsync("Steel Pipes", purchasePrice: 300m, sellingPrice: 450m);
        var copper = await _k.CreateProductAsync("Copper Wire", purchasePrice: 600m, sellingPrice: 820m);
        var oil    = await _k.CreateProductAsync("Synth Hyd Oil", purchasePrice: 2000m, sellingPrice: 2800m);
        await _k.StockUpAsync(vendor, wh, (pipes, 500m, 300m), (copper, 600m, 600m), (oil, 100m, 2000m));
        await _k.SetVariantRouteAsync(pipes, pickOnly);
        await _k.SetVariantRouteAsync(copper, pickPackShip);
        await _k.SetVariantRouteAsync(oil, pickPackShip);

        var so = await _k.CreateOrderAsync(customer, pkr, "SHIP", address,
            RLine(pipes, 500m, taxCode: gst), RLine(copper, 600m, taxCode: gst), RLine(oil, 100m, route: pickAndShip, taxCode: gst));

        // ── Preview: three groups, one line each; the PICK_ONLY group is a SELF_PICKUP (D-4) ──
        var preview = await _k.Ok(_k.Get($"/api/sale-orders/{so}/delivery-preview"), "delivery preview");
        (preview.B("canConfirm"), preview.I("deliveryCount")).Should().Be((true, 3), "T-C3-09");
        var groups = preview.A("groups").ToDictionary(g => g.S("routeCode")!);
        groups.Keys.Should().BeEquivalentTo(new[] { PickOnly, PickPackShip, PickAndShip });
        groups[PickOnly].S("deliveryMode").Should().Be("SELF_PICKUP");
        groups[PickPackShip].S("deliveryMode").Should().Be("SHIP");
        groups[PickAndShip].S("deliveryMode").Should().Be("SHIP");
        groups[PickOnly].A("lineNumbers").Select(n => n.GetInt32()).Should().Equal(1);
        groups[PickPackShip].A("lineNumbers").Select(n => n.GetInt32()).Should().Equal(2);
        groups[PickAndShip].A("lineNumbers").Select(n => n.GetInt32()).Should().Equal(3);
        preview.A("lines").Single(l => l.I("lineNumber") == 3).S("routeSource").Should().Be("LINE_OVERRIDE");

        // ── Confirm → three deliveries, one line each (T-C4-02) ──
        var confirmed = await _k.ConfirmAsync(so);
        var created = confirmed.A("deliveries");
        created.Should().HaveCount(3);
        created.Should().OnlyContain(d => d.I("lineCount") == 1);
        var dPipes  = created.Single(d => d.S("routeCode") == PickOnly).G("deliveryUuid");
        var dCopper = created.Single(d => d.S("routeCode") == PickPackShip).G("deliveryUuid");
        var dOil    = created.Single(d => d.S("routeCode") == PickAndShip).G("deliveryUuid");
        created.Single(d => d.G("deliveryUuid") == dPipes).S("deliveryMode").Should().Be("SELF_PICKUP");

        // Every delivery number is unique; the list endpoint filters by order and by route.
        created.Select(d => d.S("deliveryNumber")).Should().OnlyHaveUniqueItems();
        var bySo = (await _k.Ok(_k.Get($"/api/logistics/deliveries?saleOrderUuid={so}&pageSize=50"), "deliveries of the order")).P("data").Items();
        bySo.Select(d => d.G("uuid")).Should().BeEquivalentTo(new[] { dPipes, dCopper, dOil });
        var byRoute = (await _k.Ok(_k.Get($"/api/logistics/deliveries?saleOrderUuid={so}&fulfillmentRouteUuid={pickAndShip}"), "the order's PICK_AND_SHIP delivery")).P("data").Items();
        byRoute.Should().ContainSingle().Which.G("uuid").Should().Be(dOil);
        bySo.Should().OnlyContain(d => d.S("lineSummary") != null);

        // Step trackers show only each route's steps (T-C5-09, T-C5-10).
        (await _k.DeliveryAsync(dPipes)).Tracker().Should().Be("PICK:CURRENT,GOODS_ISSUE:PENDING,COMPLETE:PENDING");
        (await _k.DeliveryAsync(dCopper)).Tracker().Should().Be("PICK:CURRENT,PACK:PENDING,GOODS_ISSUE:PENDING,SHIP:PENDING,COMPLETE:PENDING");
        (await _k.DeliveryAsync(dOil)).Tracker().Should().Be("PICK:CURRENT,GOODS_ISSUE:PENDING,SHIP:PENDING,COMPLETE:PENDING");

        var carrier = await _k.ManualCarrierAsync();

        // nextActions must name exactly one forward action at every status the delivery passes (the screen's one
        // forward button); mismatches are collected over the three walks and asserted after them.
        var next = new List<string>();
        async Task ReleaseAndPickAsync(Guid d, string route)
        {
            await _k.CheckNextAsync(next, d, route, "DRAFT", "RELEASE");
            await _k.ReleaseAsync(d);
            await _k.CheckNextAsync(next, d, route, "RELEASED", "GENERATE_PICK_LIST");
            var pickList = await _k.GeneratePickListAsync(d);
            await _k.CheckNextAsync(next, d, route, "PICKING", "CONFIRM_PICK");
            await _k.ConfirmPickAsync(pickList);
        }

        // ── PICK_ONLY: the customer's truck collects the pipes ──
        await ReleaseAndPickAsync(dPipes, PickOnly);
        (await _k.DeliveryStatusAsync(dPipes)).Should().Be("STAGED", "T-C5-01: auto-pack + auto-stage after the pick");
        await _k.CheckNextAsync(next, dPipes, PickOnly, "STAGED", "GOODS_ISSUE", "RECORD_COLLECTION");
        (await _k.PackagesAsync(dPipes)).Should().ContainSingle().Which.S("packageType").Should().Be("LOOSE");
        (await _k.TryCreateConsignment(carrier, dPipes)).ShouldBe(HttpStatusCode.BadRequest, "a route without SHIP never goes on a consignment");
        await _k.Ok(_k.GoodsIssue(dPipes), "goods issue the pipes");
        (await _k.DeliveryAsync(dPipes)).Tracker().Should().Be("PICK:DONE,GOODS_ISSUE:DONE,COMPLETE:CURRENT");
        await _k.CheckNextAsync(next, dPipes, PickOnly, "GOODS_ISSUED", "RECORD_COLLECTION");
        (await _k.Advance(dPipes)).ShouldBe(HttpStatusCode.Conflict, "GOODS_ISSUED on PICK_ONLY needs Record collection, which takes the collector's details");
        await _k.Ok(_k.RecordCollection(dPipes), "record collection");
        var pipesDone = await _k.DeliveryAsync(dPipes);
        (pipesDone.S("status"), pipesDone.S("pickupPersonName"), pipesDone.Tracker())
            .Should().Be(("DELIVERED", "A33 Collector", "PICK:DONE,GOODS_ISSUE:DONE,COMPLETE:DONE"), "T-C5-02");
        await _k.CheckNextAsync(next, dPipes, PickOnly, "DELIVERED", null);

        // ── PICK_PACK_SHIP: the copper wire is spooled into two boxes ──
        await ReleaseAndPickAsync(dCopper, PickPackShip);
        (await _k.DeliveryStatusAsync(dCopper)).Should().Be("PICKED", "T-C5-05: a route with PACK waits for the pack station");
        await _k.CheckNextAsync(next, dCopper, PickPackShip, "PICKED", "PACK");
        var line = (await _k.DeliveryAsync(dCopper)).A("lines").Single();
        await _k.Ok(_k.Post($"/api/logistics/deliveries/{dCopper}/packages", new
        {
            PackageType = "BOX", Contents = new[] { new { DeliveryLineUuid = line.G("uuid"), Qty = 350m } }
        }), "first box");
        (await _k.DeliveryStatusAsync(dCopper)).Should().Be("PICKED", "250 still unpacked");
        await _k.CheckNextAsync(next, dCopper, PickPackShip, "PICKED", "PACK");
        await _k.Ok(_k.Post($"/api/logistics/deliveries/{dCopper}/packages", new
        {
            PackageType = "BOX", Contents = new[] { new { DeliveryLineUuid = line.G("uuid"), Qty = 250m } }
        }), "second box");
        var copperPacked = await _k.DeliveryAsync(dCopper);
        copperPacked.S("status").Should().Be("STAGED", "T-C5-06: PACKED is auto-staged on a route without STAGE");
        copperPacked.Tracker().Should().Be("PICK:DONE,PACK:DONE,GOODS_ISSUE:CURRENT,SHIP:PENDING,COMPLETE:PENDING");
        (await _k.PackagesAsync(dCopper)).Select(p => p.S("packageType")).Should().Equal("BOX", "BOX");
        await _k.CheckNextAsync(next, dCopper, PickPackShip, "STAGED", "GOODS_ISSUE");
        await _k.Ok(_k.GoodsIssue(dCopper), "goods issue the wire");
        await _k.CheckNextAsync(next, dCopper, PickPackShip, "GOODS_ISSUED", "CREATE_CONSIGNMENT");
        (await _k.RecordCollection(dCopper)).ShouldBe(HttpStatusCode.BadRequest, "a route with SHIP is not collected");
        await _k.ShipAndProveAsync(dCopper, carrier);
        var copperDone = await _k.DeliveryAsync(dCopper);
        (copperDone.S("status"), copperDone.Tracker()).Should().Be(("DELIVERED", "PICK:DONE,PACK:DONE,GOODS_ISSUE:DONE,SHIP:DONE,COMPLETE:DONE"));
        copperDone.A("consignments").Should().ContainSingle();
        await _k.CheckNextAsync(next, dCopper, PickPackShip, "DELIVERED", null);

        // ── PICK_AND_SHIP: the drums ship as they are, in one auto LOOSE unit ──
        await ReleaseAndPickAsync(dOil, PickAndShip);
        await _k.CheckNextAsync(next, dOil, PickAndShip, "STAGED", "GOODS_ISSUE");
        var oilStaged = await _k.DeliveryAsync(dOil);
        oilStaged.S("status").Should().Be("STAGED", "T-C5-03");
        oilStaged.A("lines").Single().D("qtyPacked").Should().Be(100m);
        var loose = (await _k.PackagesAsync(dOil)).Should().ContainSingle().Which;
        loose.S("packageType").Should().Be("LOOSE");
        await _k.Ok(_k.GoodsIssue(dOil), "goods issue the drums");
        (await _k.DeliveryAsync(dOil)).Tracker().Should().Be("PICK:DONE,GOODS_ISSUE:DONE,SHIP:CURRENT,COMPLETE:PENDING", "T-C5-04");
        await _k.CheckNextAsync(next, dOil, PickAndShip, "GOODS_ISSUED", "CREATE_CONSIGNMENT");
        await _k.ShipAndProveAsync(dOil, carrier);
        (await _k.DeliveryStatusAsync(dOil)).Should().Be("DELIVERED");
        await _k.CheckNextAsync(next, dOil, PickAndShip, "DELIVERED", null);
        next.Should().BeEmpty("nextActions' forward actions at each status (first = the primary button):\n" + string.Join("\n", next));

        // ── The order: fulfilled, nothing still held, and all three deliveries invoice ──
        var done = await _k.GetSaleOrderAsync(so);
        done.S("status").Should().Be("FULFILLED");
        done.A("lines").Should().OnlyContain(l => l.D("fulfilledQty") == l.D("quantity"));
        (await _k.SoLedgerHeldAsync(so)).Should().Be(0m);
        foreach (var d in new[] { dPipes, dCopper, dOil })
            await _k.InvoiceAsync(d);
        (await _k.GetSaleOrderAsync(so)).A("lines").Should().OnlyContain(l => l.D("invoicedQty") == l.D("quantity"));
    }
}

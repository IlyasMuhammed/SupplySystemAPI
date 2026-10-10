using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using Xunit;
using static SMS.Integration.Tests.FulfillmentRoutes.Routes;

namespace SMS.Integration.Tests.FulfillmentRoutes;

/// <summary>
/// A33-PF-03: the confirmation gate (BR-C3-02, T-C3-06/07) on the real host (LocalDB).
/// <list type="bullet">
/// <item>With no route on a line (no override, no variant route, no default of its class), confirm is refused with
/// the line list. Nothing is reserved and no delivery is created. An inactive route blocks with its own
/// per-line message. Fixing the lines unblocks.</item>
/// <item>The org default is chosen by class (contract L-1). set-default swaps only within its class (T-C1-07), a
/// default can't be deactivated (L-5), and clear-default leaves that class with nothing.</item>
/// <item>D-4: a SHIP route needs a shipping address, and a route without SHIP is refused while self-pickup is off.</item>
/// <item>D-5: DROP_SHIP lines need no route.</item>
/// <item>D-11: without MODULE_LOGISTICS there is no gate and no delivery.</item>
/// </list>
/// Each fact that changes an org-wide setting or default puts it back in <c>finally</c> (facts in a class run
/// one after another).
/// <para>Run alone: <c>dotnet test &lt;out&gt;\SMS.Integration.Tests.dll --filter FullyQualifiedName~FulfillmentRouteGateE2ETests</c>.</para>
/// </summary>
public sealed class FulfillmentRouteGateE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _k;

    public FulfillmentRouteGateE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _k = new SapKit(factory, "GATE");
    }

    private async Task<(Guid Pkr, Warehouse Wh, Partner Vendor, Partner Customer, Guid Address)> SetupAsync()
    {
        var pkr = await _k.PkrBaseAsync();
        await _k.EnsureTaxCodeAsync("PGST17", 17m, "PURCHASE", isDefault: true);
        await _k.CreateApproverPlaceholdersAsync();
        var customer = await _k.CreateCustomerAsync("Gate Customer");
        return (pkr, await _k.CreateWarehouseAsync(), await _k.CreateVendorAsync("Gate Vendor"), customer, await _k.ShippingAddressAsync(customer));
    }

    private Task<Api> UpdateOrder(Guid so, Guid currency, string mode, Guid? address, params object[] lines) =>
        _k.Put($"/api/sale-orders/{so}", new { CurrencyId = currency, DeliveryMode = mode, ShippingAddressId = address, Lines = lines });

    private static List<(int? Line, string? Code)> Blockers(JsonElement doc, string name = "confirmBlockers") =>
        doc.A(name).Select(b => (b.P("lineNumber").ValueKind == JsonValueKind.Null ? (int?)null : b.I("lineNumber"), b.S("code"))).ToList();

    [Fact]
    public async Task A_line_without_a_route_blocks_with_the_line_list_an_inactive_route_blocks_and_fixing_the_lines_unblocks()
    {
        var (pkr, wh, vendor, customer, address) = await SetupAsync();
        var p1 = await _k.CreateProductAsync("Gate One", 5m, 9m);
        var p2 = await _k.CreateProductAsync("Gate Two", 5m, 9m);
        var p3 = await _k.CreateProductAsync("Gate Three", 5m, 9m);
        await _k.StockUpAsync(vendor, wh, (p1, 10m, 5m), (p2, 10m, 5m), (p3, 10m, 5m));
        await _k.SetVariantRouteAsync(p2, await _k.RouteUuidAsync(PickPackShip));
        var shipDefault = await _k.RouteUuidAsync(PickAndShip);

        await _k.Ok(_k.RoutePatch(shipDefault, "clear-default"), "clear the SHIP-class default");
        try
        {
            // ── No route anywhere on lines 1 and 3 (T-C3-04 / T-C3-06) ──
            var so = await _k.CreateOrderAsync(customer, pkr, "SHIP", address, RLine(p1, 2m), RLine(p2, 2m), RLine(p3, 2m));
            var draft = await _k.GetSaleOrderAsync(so);
            draft.A("lines").Select(l => (l.S("routeSource"), l.S("routeBlocker")))
                .Should().Equal(("NONE", "ROUTE_MISSING"), ("VARIANT", null), ("NONE", "ROUTE_MISSING"));
            // The missing lines come together in one blocker (contract §5: "then all missing lines together").
            Blockers(draft).Should().Equal(new (int?, string?)[] { (null, "ROUTE_MISSING") });
            draft.A("confirmBlockers").Single().S("message").Should().Contain("lines 1, 3 have no fulfillment route");
            var preview = await _k.Ok(_k.Get($"/api/sale-orders/{so}/delivery-preview"), "preview");
            preview.B("canConfirm").Should().BeFalse();
            Blockers(preview, "blockers").Should().Equal(new (int?, string?)[] { (null, "ROUTE_MISSING") });

            var refused = await _k.TryConfirm(so);
            refused.ShouldBe(HttpStatusCode.BadRequest, "two lines have no route");
            refused.Message.Should().Contain("lines 1, 3 have no fulfillment route");
            (await _k.GetSaleOrderAsync(so)).S("status").Should().Be("DRAFT");
            (await _k.SoLedgerHeldAsync(so)).Should().Be(0m, "the gate runs before anything is reserved");
            (await _k.SoDeliveriesAsync(so)).Should().BeEmpty();

            // ── An inactive route (T-C3-07) ──
            // A route that an open line uses can't be deactivated (BR-C1-07), so the only way a draft meets an
            // inactive route is the race R-8 describes (deactivated between the line's save and confirm). Simulated in SQL.
            var custom = await _k.CreateRouteAsync("GATEX", "PICK", "GOODS_ISSUE", "SHIP");
            var x = custom.G("uuid");
            await _k.Ok(UpdateOrder(so, pkr, "SHIP", address, RLine(p1, 2m, route: x), RLine(p2, 2m), RLine(p3, 2m, route: x)), "override lines 1 and 3");
            var inUse = await _k.RoutePatch(x, "deactivate");
            inUse.ShouldBe(HttpStatusCode.Conflict, "BR-C1-07: two open sale order lines use it");
            inUse.Message.Should().Contain("open sale order lines");

            await _f.ExecuteAsync("UPDATE logistics.fulfillment_routes SET IsActive = 0 WHERE UUID = @x", ("@x", x));
            Blockers(await _k.GetSaleOrderAsync(so)).Should().BeEquivalentTo(new (int?, string?)[] { (1, "ROUTE_INACTIVE"), (3, "ROUTE_INACTIVE") });
            refused = await _k.TryConfirm(so);
            refused.ShouldBe(HttpStatusCode.BadRequest, "an inactive route blocks");
            refused.Message.Should().Contain($"Line 1: fulfillment route '{custom.S("code")}' is inactive")
                .And.Contain($"Line 3: fulfillment route '{custom.S("code")}' is inactive");
            (await _k.SoLedgerHeldAsync(so)).Should().Be(0m);

            // A line override must name an active route when it is saved (BR-C3-01).
            (await UpdateOrder(so, pkr, "SHIP", address, RLine(p1, 2m, route: x), RLine(p2, 2m), RLine(p3, 2m)))
                .ShouldBe(HttpStatusCode.BadRequest, "an inactive route can't be put on a line");

            // ── Fix: line 1 and 3 overridden to PICK_AND_SHIP → confirm → two deliveries ──
            await _k.Ok(UpdateOrder(so, pkr, "SHIP", address, RLine(p1, 2m, route: shipDefault), RLine(p2, 2m), RLine(p3, 2m, route: shipDefault)), "fix the lines");
            (await _k.GetSaleOrderAsync(so)).A("confirmBlockers").Should().BeEmpty();
            var confirmed = await _k.ConfirmAsync(so);
            confirmed.A("deliveries").Select(d => (d.S("routeCode"), d.I("lineCount")))
                .Should().BeEquivalentTo(new[] { (PickAndShip, 2), (PickPackShip, 1) });
        }
        finally
        {
            await _k.Ok(_k.RoutePatch(shipDefault, "set-default"), "restore the SHIP-class default");
        }
    }

    [Fact]
    public async Task The_org_default_is_chosen_by_class_and_set_default_swaps_only_within_its_class()
    {
        var (pkr, wh, vendor, customer, address) = await SetupAsync();
        var p = await _k.CreateProductAsync("Class Widget", 5m, 9m);
        await _k.StockUpAsync(vendor, wh, (p, 10m, 5m));
        var pickOnly = await _k.RouteUuidAsync(PickOnly);
        var pickAndShip = await _k.RouteUuidAsync(PickAndShip);
        var pickPackShip = await _k.RouteUuidAsync(PickPackShip);

        var pickup = await _k.CreateOrderAsync(customer, pkr, "SELF_PICKUP", null, RLine(p, 1m));
        var ship   = await _k.CreateOrderAsync(customer, pkr, "SHIP", address, RLine(p, 1m));
        async Task<(string?, string?)> Resolved(Guid so)
        {
            var l = (await _k.GetSaleOrderAsync(so)).A("lines").Single();
            return (l.S("routeSource"), l.S("effectiveRouteCode"));
        }
        (await Resolved(pickup)).Should().Be(("ORG_DEFAULT", PickOnly), "SELF_PICKUP orders take the non-shipping default");
        (await Resolved(ship)).Should().Be(("ORG_DEFAULT", PickAndShip), "SHIP orders take the shipping default");

        // L-5: a default can't be deactivated; BR-C1-06: a system route can't be deleted.
        (await _k.RoutePatch(pickAndShip, "deactivate")).ShouldBe(HttpStatusCode.Conflict, "a default route stays active");
        (await _k.Delete($"/api/fulfillment-routes/{pickPackShip}")).ShouldBe(HttpStatusCode.Conflict, "system routes can't be deleted (T-C1-09)");

        try
        {
            // T-C1-07: set-default clears the previous default of the same class only.
            var swapped = await _k.Ok(_k.RoutePatch(pickPackShip, "set-default"), "make PICK_PACK_SHIP the SHIP default");
            swapped.B("isDefault").Should().BeTrue();
            (await _k.RouteAsync(PickAndShip)).B("isDefault").Should().BeFalse("the previous SHIP-class default is cleared");
            (await _k.RouteAsync(PickOnly)).B("isDefault").Should().BeTrue("the SELF_PICKUP-class default is untouched");
            (await Resolved(ship)).Should().Be(("ORG_DEFAULT", PickPackShip), "a draft resolves live");
            (await Resolved(pickup)).Should().Be(("ORG_DEFAULT", PickOnly));

            // clear-default leaves the SHIP class with nothing: the SHIP order blocks, the pickup order doesn't.
            await _k.Ok(_k.RoutePatch(pickPackShip, "clear-default"), "clear the SHIP-class default");
            (await _k.GetSaleOrderAsync(ship)).A("lines").Single().S("routeBlocker").Should().Be("ROUTE_MISSING");
            (await _k.GetSaleOrderAsync(pickup)).A("confirmBlockers").Should().BeEmpty();
            (await _k.TryConfirm(ship)).ShouldBe(HttpStatusCode.BadRequest, "no SHIP-class default and no other route");
        }
        finally
        {
            await _k.Ok(_k.RoutePatch(pickAndShip, "set-default"), "restore PICK_AND_SHIP as the SHIP default");
        }

        (await _k.RouteAsync(PickPackShip)).B("isDefault").Should().BeFalse();
        var confirmed = await _k.ConfirmAsync(ship);
        confirmed.A("deliveries").Should().ContainSingle().Which.S("routeCode").Should().Be(PickAndShip);
        (await _k.ConfirmAsync(pickup)).A("deliveries").Should().ContainSingle().Which.S("routeCode").Should().Be(PickOnly);
        _ = pickOnly;
    }

    [Fact]
    public async Task A_shipping_route_needs_an_address_and_a_collect_route_needs_self_pickup_on()
    {
        var (pkr, wh, vendor, customer, address) = await SetupAsync();
        var p = await _k.CreateProductAsync("Mode Widget", 5m, 9m);
        await _k.StockUpAsync(vendor, wh, (p, 10m, 5m));

        // D-4 / REV-03: a SELF_PICKUP order whose line routes to SHIP (here by the variant's default route) needs a
        // shipping address; the server must accept one on a SELF_PICKUP order, keep it, and clear the gate.
        await _k.SetVariantRouteAsync(p, await _k.RouteUuidAsync(PickAndShip));
        var pickup = await _k.CreateOrderAsync(customer, pkr, "SELF_PICKUP", null, RLine(p, 1m));
        var noAddress = await _k.GetSaleOrderAsync(pickup);
        noAddress.A("lines").Single().S("routeSource").Should().Be("VARIANT");
        Blockers(noAddress).Should().ContainSingle().Which.Code.Should().Be("SHIPPING_ADDRESS_REQUIRED");
        (await _k.TryConfirm(pickup)).ShouldBe(HttpStatusCode.BadRequest, "nowhere to ship the line");
        await _k.Ok(UpdateOrder(pickup, pkr, "SELF_PICKUP", address, RLine(p, 1m)), "add an address to the SELF_PICKUP order");
        var withAddress = await _k.GetSaleOrderAsync(pickup);
        (withAddress.S("deliveryMode"), withAddress.NG("shippingAddressId")).Should().Be(("SELF_PICKUP", (Guid?)address), "REV-03: the address is kept on a SELF_PICKUP order");
        withAddress.A("confirmBlockers").Should().BeEmpty();
        (await _k.ConfirmAsync(pickup)).A("deliveries").Should().ContainSingle().Which.S("deliveryMode").Should().Be("SHIP", "the mode comes from the route");

        // …and an address given at creation is kept too.
        var created = await _k.CreateOrderAsync(customer, pkr, "SELF_PICKUP", address, RLine(p, 1m));
        var c = await _k.GetSaleOrderAsync(created);
        (c.NG("shippingAddressId"), c.A("confirmBlockers").Count).Should().Be(((Guid?)address, 0));
        await _k.SetVariantRouteAsync(p, null);

        // D-4: with self-pickup off, a route without SHIP is refused even on a SHIP order.
        var ship = await _k.CreateOrderAsync(customer, pkr, "SHIP", address, RLine(p, 1m, route: await _k.RouteUuidAsync(PickOnly)));
        await _k.SetSaleOrderConfigAsync(("selfPickupEnabled", false));
        try
        {
            Blockers(await _k.GetSaleOrderAsync(ship)).Should().ContainSingle().Which.Code.Should().Be("SELF_PICKUP_DISABLED");
            (await _k.TryConfirm(ship)).ShouldBe(HttpStatusCode.BadRequest, "nobody may collect while self-pickup is off");
        }
        finally
        {
            await _k.SetSaleOrderConfigAsync(("selfPickupEnabled", true));
        }
        (await _k.ConfirmAsync(ship)).A("deliveries").Should().ContainSingle().Which.S("deliveryMode").Should().Be("SELF_PICKUP");
    }

    [Fact]
    public async Task Drop_ship_lines_need_no_route_and_get_no_delivery()
    {
        var (pkr, _, _, customer, address) = await SetupAsync();
        var p = await _k.CreateProductAsync("Drop Widget", 5m, 9m);
        var shipDefault = await _k.RouteUuidAsync(PickAndShip);

        await _k.SetSaleOrderConfigAsync(("dropShipEnabled", true), ("defaultFulfillmentMode", "DROP_SHIP"), ("autoPoEnabled", false));
        await _k.Ok(_k.RoutePatch(shipDefault, "clear-default"), "clear the SHIP-class default");
        try
        {
            var so = await _k.CreateOrderAsync(customer, pkr, "SHIP", address, RLine(p, 3m));
            var draft = await _k.GetSaleOrderAsync(so);
            var line = draft.A("lines").Single();
            (line.S("fulfillmentMode"), line.S("routeSource"), line.S("routeBlocker")).Should().Be(("DROP_SHIP", "NONE", null), "D-5: exempt");
            draft.A("confirmBlockers").Should().BeEmpty();

            var confirmed = await _k.ConfirmAsync(so);
            confirmed.A("deliveries").Should().BeEmpty("a drop-ship line never gets a delivery");
            confirmed.B("deliveryCreationFailed").Should().BeFalse();
            (await _k.SoDeliveriesAsync(so)).Should().BeEmpty();
        }
        finally
        {
            await _k.SetSaleOrderConfigAsync(("defaultFulfillmentMode", "IN_STOCK"), ("dropShipEnabled", false), ("autoPoEnabled", true));
            await _k.Ok(_k.RoutePatch(shipDefault, "set-default"), "restore the SHIP-class default");
        }
    }

    [Fact]
    public async Task Without_MODULE_LOGISTICS_there_is_no_gate_and_no_delivery()
    {
        var pkr = await _k.EnsureCurrencyAsync("PKR", "Pakistani Rupee", "Rs");
        var (org2, k2, _) = await _k.SecondOrganizationAsync("NOLOG");
        await _k.SetOrgBaseCurrencyAsync(org2, pkr);
        var customer = await k2.CreateCustomerAsync("No-Log Customer");
        var p = await k2.CreateProductAsync("No-Log Widget", 5m, 9m);

        // With Logistics on, clearing org 2's SELF_PICKUP default makes the line block…
        var pickOnly2 = (await k2.RoutesAsync()).Single(r => r.S("code") == PickOnly).G("uuid");
        await k2.Ok(k2.RoutePatch(pickOnly2, "clear-default"), "clear org 2's SELF_PICKUP default");
        var so = await k2.CreateOrderAsync(customer, pkr, "SELF_PICKUP", null, RLine(p, 2m));
        (await k2.GetSaleOrderAsync(so)).A("lines").Single().S("routeBlocker").Should().Be("ROUTE_MISSING");

        // …and with Logistics off there is no gate at all (D-11).
        await _k.Ok(_k.Put($"/api/system/organizations/{org2}/features",
            new { features = new[] { new { featureCode = "MODULE_LOGISTICS", isEnabled = false } } }), "switch Logistics off for org 2");
        // A37 D-6: the organization once had Logistics, so its routes stay readable; writes are refused.
        (await k2.Get("/api/fulfillment-routes")).ShouldBe(HttpStatusCode.OK, "A37 D-6: read-only once unlicensed");
        (await k2.TryCreateRouteAsync("NOLOGX", "No-Log route", ["PICK", "GOODS_ISSUE"])).ShouldBe(HttpStatusCode.Forbidden, "the routes API is a Logistics feature");
        var off = await k2.GetSaleOrderAsync(so);
        off.B("routesEnabled").Should().BeFalse();
        off.A("confirmBlockers").Should().BeEmpty();
        off.A("lines").Single().IsNull("routeBlocker").Should().BeTrue();

        var confirmed = await k2.Ok(k2.TryConfirm(so), "confirm without Logistics");
        confirmed.S("status").Should().Be("CONFIRMED");
        confirmed.A("deliveries").Should().BeEmpty();
        confirmed.B("deliveryCreationFailed").Should().BeFalse("no auto-create was attempted");
        var rows = await _f.QueryAsync("SELECT COUNT(*) AS N FROM logistics.delivery_orders WHERE SaleOrderUuid = @so", ("@so", so));
        Convert.ToInt32(rows[0]["N"]).Should().Be(0);
    }
}

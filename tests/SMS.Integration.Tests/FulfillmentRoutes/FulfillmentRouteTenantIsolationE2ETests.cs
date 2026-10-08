using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using Xunit;
using static SMS.Integration.Tests.FulfillmentRoutes.Routes;

namespace SMS.Integration.Tests.FulfillmentRoutes;

/// <summary>
/// A33-PF-05: multi-tenant isolation of routes and everything that points at one (R-11, R-12, R-13).
/// <list type="bullet">
/// <item><b>Routes:</b> every organization is seeded its own three routes (provisioning). A route code is unique
/// per org, not globally. Another org's route reads and writes as 404, <b>super admin included</b> (the EF filter
/// is off for them). set-default in one org leaves the other's default alone.</item>
/// <item><b>References:</b> another org's route can't go on a variant (T-C2-02, 400) or an SO line (BR-C3-01,
/// 400). Another org's variant, order or delivery answers the new endpoints (variant route, preview,
/// create-deliveries, approve, advance, cancel) with 404, super admin included. An org's auto-created
/// deliveries carry its own org and its own route.</item>
/// <item><b>Sweep:</b> after all of it, no variant, SO line or delivery in the database points at another org's
/// route (SQL).</item>
/// </list>
/// The seeded admin is the platform super admin acting in org 1; org 2 is created through the platform API.
/// <para>Run alone: <c>dotnet test &lt;out&gt;\SMS.Integration.Tests.dll --filter FullyQualifiedName~FulfillmentRouteTenantIsolationE2ETests</c>.</para>
/// </summary>
public sealed class FulfillmentRouteTenantIsolationE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _k;

    public FulfillmentRouteTenantIsolationE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _k = new SapKit(factory, "TEN");
    }

    private static readonly HttpStatusCode NotFound = HttpStatusCode.NotFound;

    [Fact]
    public async Task Routes_are_seeded_per_organization_and_another_organizations_route_is_a_404_even_for_the_super_admin()
    {
        var (org2, k2, _) = await _k.SecondOrganizationAsync("RT2");

        // Provisioning seeded org 2 its own system routes, defaults per class (L-1). A34 D-9 adds the two MANUFACTURE
        // seeds (never default) to every org, so there are five.
        var mine = await _k.RoutesAsync(includeInactive: true);
        var theirs = await k2.RoutesAsync(includeInactive: true);
        theirs.Where(r => r.B("isSystem")).Select(r => r.S("code")).Should()
            .BeEquivalentTo(new[] { PickOnly, PickAndShip, PickPackShip, "MFG_PICK_SHIP", "MFG_PICK_PACK_SHIP" });
        theirs.Single(r => r.S("code") == PickOnly).B("isDefault").Should().BeTrue();
        theirs.Single(r => r.S("code") == PickAndShip).B("isDefault").Should().BeTrue();
        theirs.Select(r => r.G("uuid")).Should().NotIntersectWith(mine.Select(r => r.G("uuid")), "each org has its own rows");
        var seeded = await _f.QueryAsync(
            "SELECT COUNT(*) AS N FROM logistics.fulfillment_routes WHERE OrganizationId = @o AND IsSystem = 1", ("@o", org2));
        Convert.ToInt32(seeded[0]["N"]).Should().Be(5, "3 stock seeds (A33) + 2 MANUFACTURE seeds (A34 D-9)");

        // The super admin's list shows only its own org's routes (the EF filter is off for it).
        mine.Select(r => r.G("uuid")).Should().NotIntersectWith(theirs.Select(r => r.G("uuid")), "the super admin sees only org 1's routes");

        // Codes are unique per org: org 2 may reuse org 1's custom code.
        var custom1 = await _k.CreateRouteAsync("SAME", "PICK", "GOODS_ISSUE");
        var code = custom1.S("code")!;
        var custom2 = await k2.Ok(k2.TryCreateRouteAsync(code, "org 2's own", ["PICK", "GOODS_ISSUE", "SHIP"]), "org 2 reuses the code");
        (await k2.TryCreateRouteAsync(code, "dup", ["PICK", "GOODS_ISSUE"])).ShouldBe(HttpStatusCode.Conflict, "BR-C1-01 inside one org");

        var r1 = custom1.G("uuid");
        var r2 = custom2.G("uuid");
        var pickOnly2 = theirs.Single(r => r.S("code") == PickOnly).G("uuid");

        // org 2 → org 1's route, and the super admin → org 2's routes: every route endpoint is a 404.
        foreach (var (kit, route, who) in new[] { (k2, r1, "org 2 on org 1's route"), (_k, r2, "super admin on org 2's route"), (_k, pickOnly2, "super admin on org 2's seed") })
        {
            (await kit.Get($"/api/fulfillment-routes/{route}")).ShouldBe(NotFound, who);
            (await kit.Put($"/api/fulfillment-routes/{route}", new { Name = "hijack", DisplayOrder = 1 })).ShouldBe(NotFound, who);
            foreach (var action in new[] { "deactivate", "activate", "set-default", "clear-default" })
                (await kit.RoutePatch(route, action)).ShouldBe(NotFound, $"{who}: {action}");
            (await kit.Delete($"/api/fulfillment-routes/{route}")).ShouldBe(NotFound, who);
            (await kit.Post($"/api/fulfillment-routes/{route}/assign-by-category", new { CategoryId = 1 })).ShouldBe(NotFound, $"{who}: bulk assign");
        }

        // Nothing moved in org 2 or org 1.
        var after2 = await k2.RoutesAsync(includeInactive: true);
        after2.Single(r => r.G("uuid") == pickOnly2).B("isDefault").Should().BeTrue("org 2's SELF_PICKUP default survived");
        after2.Single(r => r.G("uuid") == r2).S("name").Should().Be("org 2's own");
        (await _k.RouteAsync(code)).S("name").Should().Be(custom1.S("name"));

        // set-default in org 1 leaves org 2's default of that class alone.
        await _k.Ok(_k.RoutePatch(r1, "set-default"), "org 1's new SELF_PICKUP default");
        try
        {
            (await k2.RoutesAsync()).Single(r => r.G("uuid") == pickOnly2).B("isDefault").Should().BeTrue();
        }
        finally
        {
            await _k.Ok(_k.RoutePatch(await _k.RouteUuidAsync(PickOnly), "set-default"), "restore org 1's default");
        }
    }

    [Fact]
    public async Task No_cross_organization_route_on_a_variant_a_sale_order_line_or_a_delivery_and_the_super_admin_gets_404()
    {
        var pkr = await _k.PkrBaseAsync();
        await _k.EnsureTaxCodeAsync("PGST17", 17m, "PURCHASE", isDefault: true);
        await _k.CreateApproverPlaceholdersAsync();
        var (org2, k2, _) = await _k.SecondOrganizationAsync("RF2");
        await _k.SetOrgBaseCurrencyAsync(org2, pkr);

        var org1Route = (await _k.CreateRouteAsync("O1R", "PICK", "PACK", "APPROVAL", "GOODS_ISSUE", "SHIP")).G("uuid");
        var org2Routes = await k2.RoutesAsync();
        var org2PickOnly = org2Routes.Single(r => r.S("code") == PickOnly).G("uuid");

        // ── Variants ──
        var v1 = await _k.CreateProductAsync("Org1 Widget", 5m, 9m);
        var v2 = await k2.CreateProductAsync("Org2 Widget", 5m, 9m);
        var refused = await k2.AssignVariantRoute(v2.VariantUuid, org1Route);
        refused.ShouldBe(HttpStatusCode.BadRequest, "T-C2-02: another org's route");
        refused.Message.Should().Contain("organization");
        (await k2.AssignVariantRoute(v1.VariantUuid, org2PickOnly)).ShouldBe(NotFound, "org 2 can't reach org 1's variant");
        (await _k.AssignVariantRoute(v2.VariantUuid, org1Route)).ShouldBe(NotFound, "the super admin can't reach org 2's variant");
        (await _k.AssignVariantRoute(v2.VariantUuid, org2PickOnly)).ShouldBe(NotFound, "the super admin can't reach org 2's variant, even with org 2's route");
        await k2.SetVariantRouteAsync(v2, org2PickOnly);

        // ── Sale order lines ──
        var c2 = await k2.CreateCustomerAsync("Org2 Customer");
        (await k2.TryCreateOrderAsync(c2, pkr, "SELF_PICKUP", null, RLine(v2, 1m, route: org1Route)))
            .ShouldBe(HttpStatusCode.BadRequest, "BR-C3-01: another org's route on a line");
        var preview = await k2.Post("/api/sale-orders/delivery-preview", new
        {
            DeliveryMode = "SELF_PICKUP", Lines = new[] { new { VariantUuid = v2.VariantUuid, Quantity = 1m, FulfillmentRouteUuid = (Guid?)org1Route } }
        });
        if (preview.Status == HttpStatusCode.OK)
        {
            var pl = preview.Result.A("lines").Single();
            (pl.S("routeBlocker"), pl.NG("effectiveRouteUuid")).Should().Be(("ROUTE_UNKNOWN", (Guid?)null), "another org's route is unknown here");
        }
        else preview.ShouldBe(HttpStatusCode.BadRequest, "another org's route in the preview");

        // org 2 stocks up and confirms its own order: its delivery is org 2's, on org 2's route.
        var wh2 = await k2.CreateWarehouseAsync();
        var vendor2 = await k2.CreateVendorAsync("Org2 Vendor");
        await k2.EnsureTaxCodeAsync("PGST17", 17m, "PURCHASE", isDefault: true);
        await k2.CreateApproverPlaceholdersAsync();
        await k2.StockUpAsync(vendor2, wh2, (v2, 10m, 5m));
        var so2 = await k2.CreateOrderAsync(c2, pkr, "SELF_PICKUP", null, RLine(v2, 2m));
        (await k2.GetSaleOrderAsync(so2)).A("lines").Single().S("routeSource").Should().Be("VARIANT");
        var d2 = (await k2.ConfirmAsync(so2)).A("deliveries").Single();
        d2.G("routeUuid").Should().Be(org2PickOnly);
        var d2Uuid = d2.G("deliveryUuid");
        var row = (await _f.QueryAsync("SELECT OrganizationId, FulfillmentRouteUuid FROM logistics.delivery_orders WHERE UUID = @d", ("@d", d2Uuid))).Single();
        ((Guid)row["OrganizationId"]!, (Guid?)row["FulfillmentRouteUuid"]).Should().Be((org2, (Guid?)org2PickOnly), "the creator stamps the order's org (R-13)");

        // org 1 confirms its own order on its APPROVAL route.
        var wh1 = await _k.CreateWarehouseAsync();
        var c1 = await _k.CreateCustomerAsync("Org1 Customer");
        await _k.StockUpAsync(await _k.CreateVendorAsync("Org1 Vendor"), wh1, (v1, 10m, 5m));
        var so1 = await _k.CreateOrderAsync(c1, pkr, "SHIP", await _k.ShippingAddressAsync(c1), RLine(v1, 2m, route: org1Route));
        var d1Uuid = (await _k.ConfirmAsync(so1)).A("deliveries").Single().G("deliveryUuid");

        // ── The new SO / delivery endpoints across orgs: 404 both ways, super admin included ──
        foreach (var (kit, so, d, who) in new[] { (k2, so1, d1Uuid, "org 2 on org 1's"), (_k, so2, d2Uuid, "super admin on org 2's") })
        {
            (await kit.Get($"/api/sale-orders/{so}/delivery-preview")).ShouldBe(NotFound, $"{who} preview");
            (await kit.Post($"/api/sale-orders/{so}/create-deliveries")).ShouldBe(NotFound, $"{who} recovery");
            (await kit.Approve(d)).ShouldBe(NotFound, $"{who} approve");
            (await kit.Advance(d)).ShouldBe(NotFound, $"{who} advance");
            (await kit.Post($"/api/logistics/deliveries/{d}/cancel", new { Reason = "hijack" })).ShouldBe(NotFound, $"{who} cancel (own-org filter, contract §6)");
            (await kit.Post($"/api/sale-orders/{so}/cancel", new { Reason = "hijack" })).ShouldBe(NotFound, $"{who} SO cancel");
        }
        (await k2.Get($"/api/logistics/deliveries/{d1Uuid}")).ShouldBe(NotFound, "org 2 can't read org 1's delivery");
        // The order's delivery list (contract §6, new fields) and the delivery detail (route, tracker) for another
        // org: 404, super admin included (contract §1).
        (await _k.Get($"/api/sale-orders/{so2}/deliveries")).ShouldBe(NotFound, "super admin on org 2's order deliveries");
        (await _k.Get($"/api/logistics/deliveries/{d2Uuid}")).ShouldBe(NotFound, "super admin on org 2's delivery detail");
        (await k2.Get($"/api/sale-orders/{so1}/deliveries")).ShouldBe(NotFound, "org 2 on org 1's order deliveries");
        (await k2.Get($"/api/logistics/deliveries?saleOrderUuid={so1}")).Result.P("data").Items().Should().BeEmpty("the new filter stays inside the org");
        (await k2.Get($"/api/logistics/deliveries?fulfillmentRouteUuid={org1Route}")).Result.P("data").Items().Should().BeEmpty();

        (await k2.DeliveryStatusAsync(d2Uuid)).Should().Be("DRAFT", "nothing the super admin tried moved it");
        (await k2.GetSaleOrderAsync(so2)).S("status").Should().Be("CONFIRMED");
        (await _k.DeliveryStatusAsync(d1Uuid)).Should().Be("DRAFT");

        // ── Sweep: no row anywhere points at another org's route ──
        foreach (var (table, what) in new[]
                 {
                     ("inventory.ProductVariants", "variant"), ("demand.sale_order_lines", "SO line"), ("logistics.delivery_orders", "delivery")
                 })
        {
            var cross = await _f.QueryAsync(
                $"SELECT COUNT(*) AS N FROM {table} x JOIN logistics.fulfillment_routes r ON r.UUID = x.FulfillmentRouteUuid WHERE r.OrganizationId <> x.OrganizationId");
            Convert.ToInt32(cross[0]["N"]).Should().Be(0, $"no {what} points at another org's route");
        }
    }
}

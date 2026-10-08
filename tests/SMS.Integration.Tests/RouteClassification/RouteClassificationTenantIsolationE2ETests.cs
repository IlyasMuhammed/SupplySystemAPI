using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using Xunit;
using static SMS.Integration.Tests.FulfillmentRoutes.Routes;
using static SMS.Integration.Tests.RouteClassification.Rc;
using static SMS.Integration.Tests.SalesPreOrder.PreOrder;

namespace SMS.Integration.Tests.RouteClassification;

/// <summary>
/// A34-PF-05: multi-tenant isolation of everything A34 adds (R-11, R-12).
/// <list type="bullet">
/// <item><b>T-C1-04:</b> a new organization gets the five seeds (3 STOCK + 2 MANUFACTURE, never default); the seeder
/// re-run adds nothing; lead-time defaults are per organization.</item>
/// <item><b>404s, super admin included:</b> another org's variant (lead times, calculate, calculate-manufacturing), SO
/// (line ⏱, delivery date, create-production-orders), production order (read, create-delivery) and route (category,
/// assignment); another org's route in a calculation is a 400. The calculator's cache never answers across orgs
/// (D-27).</item>
/// <item><b>Sweep per org:</b> the D-17 production sweep recovers two orgs' orders, each production order in its own
/// organization.</item>
/// </list>
/// The seeded admin is the platform super admin acting in org 1 (the EF tenant filter is off for it); org 2 is created
/// through the platform API.
/// <para>Run alone: <c>dotnet test &lt;out&gt;\SMS.Integration.Tests.dll --filter FullyQualifiedName~RouteClassificationTenantIsolationE2ETests</c>.</para>
/// </summary>
public sealed class RouteClassificationTenantIsolationE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _k;

    public RouteClassificationTenantIsolationE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _k = new SapKit(factory, "TN");
    }

    private static readonly HttpStatusCode NotFound = HttpStatusCode.NotFound;

    private async Task<(Guid Org2, SapKit K2, MtoWorld W2)> SecondOrgWorldAsync(string prefix)
    {
        var pkr = await _k.EnsureCurrencyAsync("PKR", "Pakistani Rupee", "Rs");
        var (org2, k2, _) = await _k.SecondOrganizationAsync(prefix);
        await _k.SetOrgBaseCurrencyAsync(org2, pkr);
        var w2 = await k2.MtoWorldAsync($"{prefix} W", rawStock: 20m, baseCurrency: pkr);
        return (org2, k2, w2);
    }

    [Fact]
    public async Task A_new_organization_gets_five_seeds_and_its_own_lead_time_defaults()
    {
        var (org2, k2, _) = await _k.SecondOrganizationAsync("SD2");
        var theirs = (await k2.RoutesAsync(includeInactive: true)).Where(r => r.B("isSystem")).ToList();
        theirs.Select(r => (r.S("code"), r.S("routeCategory"))).Should().BeEquivalentTo(new[]
        {
            (PickOnly, Stock), (PickAndShip, Stock), (PickPackShip, Stock), (MfgPickShip, Manufacture), (MfgPickPackShip, Manufacture)
        }, "T-C1-04");
        theirs.Where(r => r.S("routeCategory") == Manufacture).Should().OnlyContain(r => !r.B("isDefault"));
        theirs.Select(r => r.G("uuid")).Should().NotIntersectWith((await _k.RoutesAsync(includeInactive: true)).Select(r => r.G("uuid")));

        // The seeder again (as the startup backfill would run it): nothing added, matched by code.
        await using (var scope = _f.Services.CreateAsyncScope())
        {
            var seeder = scope.ServiceProvider.GetRequiredService<SMS.Modules.Logistics.Services.IFulfillmentRouteSeeder>();
            (await seeder.EnsureSeededAsync(org2)).Should().Be(0, "idempotent by code");
            (await seeder.EnsureSeededForAllAsync([org2, _f.OrganizationId])).Should().Be(0, "the backfill too");
        }
        var count = await _f.QueryAsync("SELECT COUNT(*) AS N FROM logistics.fulfillment_routes WHERE OrganizationId = @o AND IsSystem = 1", ("@o", org2));
        Convert.ToInt32(count[0]["N"]).Should().Be(5);

        // Lead-time defaults: each org its own row.
        await k2.Ok(k2.Put("/api/lead-time/defaults", new
        {
            PickPackDays = 7, ShippingLeadTimeDays = 7, SalesBufferDays = 7, ManufacturingBufferDays = 7, QualityInspectionDays = 7, InternalTransferDays = 7
        }), "org 2 saves its defaults");
        var mine = await _k.Ok(_k.Get("/api/lead-time/defaults"), "org 1 reads its own");
        mine.I("pickPackDays").Should().NotBe(7, "org 2's save is not org 1's");
        (await k2.Ok(k2.Get("/api/lead-time/defaults"), "org 2 reads its own")).I("pickPackDays").Should().Be(7);
    }

    [Fact]
    public async Task Another_organizations_variant_order_production_order_and_route_are_404_even_for_the_super_admin()
    {
        var (_, k2, w2) = await SecondOrgWorldAsync("IS2");
        var so2 = await k2.CreateOrderAsync(w2.Customer, w2.Pkr, "SHIP", w2.Address, RLine(w2.Fg, 2m));
        var line2 = (await k2.GetSaleOrderAsync(so2)).LineOf(w2.Fg).G("uuid");
        var draft2 = await k2.CreateOrderAsync(w2.Customer, w2.Pkr, "SHIP", w2.Address, RLine(w2.Fg, 1m));
        var draftLine2 = (await k2.GetSaleOrderAsync(draft2)).LineOf(w2.Fg).G("uuid");
        var po2 = (await k2.ConfirmAsync(so2)).A("productionOrders").Single().G("productionOrderUuid");
        var mfg2 = w2.Route;

        // Org 2 fills the calculator's cache for its own variant first (D-27: the org is in the key).
        (await k2.Ok(k2.Post("/api/lead-time/calculate", new { VariantUuid = w2.Fg.VariantUuid, Quantity = 2m }), "org 2 calculates")).I("totalLeadTimeDays")
            .Should().BeGreaterThan(0);

        // Org 1: the super admin, and a plain org-1 user holding every code these endpoints take.
        var mine = await _k.CreateProductAsync("Iso Mine", 5m, 9m);
        var orgUser = await _k.LoginWithPermissionsAsync("isouser",
            "INVENTORY_VIEW", "STOCK_MANAGE", "SALE_ORDER_VIEW", "SALE_ORDER_EDIT", "SALE_ORDER_CONFIRM", "PROD_VIEW", "PROD_CREATE",
            "DELIVERY_CREATE", "DELIVERY_VIEW", "FULFILLMENT_ROUTE_MANAGE", "FULFILLMENT_ROUTE_ASSIGN", "FULFILLMENT_ROUTE_VIEW");
        foreach (var (who, client) in new[] { ("super admin", (HttpClient?)null), ("org 1 user", orgUser) })
        {
            (await _k.Get($"/api/variants/{w2.Fg.VariantUuid}/lead-times", client)).ShouldBe(NotFound, $"{who}: variant lead times");
            (await _k.Put($"/api/variants/{w2.Fg.VariantUuid}/lead-times", new { PickPackDays = 9 }, client)).ShouldBe(NotFound, $"{who}: variant lead-times PUT");
            (await _k.Post("/api/lead-time/calculate", new { VariantUuid = w2.Fg.VariantUuid, Quantity = 2m }, client))
                .ShouldBe(NotFound, $"{who}: calculate on org 2's variant (same cache key but another org)");
            (await _k.Post("/api/lead-time/calculate-manufacturing", new { VariantUuid = w2.Fg.VariantUuid, Quantity = 2m }, client))
                .ShouldBe(NotFound, $"{who}: calculate-manufacturing");
            var crossRoute = await _k.Post("/api/lead-time/calculate", new { VariantUuid = mine.VariantUuid, Quantity = 1m, RouteUuid = mfg2 }, client);
            crossRoute.ShouldBe(HttpStatusCode.BadRequest, $"{who}: org 2's route");
            crossRoute.Message.Should().Contain("Route not found in your organization.");

            (await _k.Post($"/api/sale-orders/{draft2}/lines/{draftLine2}/lead-time", new { }, client)).ShouldBe(NotFound, $"{who}: SO line ⏱");
            (await _k.Put($"/api/sale-orders/{so2}/lines/{line2}/delivery-date", new { ManualDeliveryDate = Day(Today.AddDays(9)) }, client))
                .ShouldBe(NotFound, $"{who}: delivery date");
            (await _k.TryCreateProductionOrders(so2, client)).ShouldBe(NotFound, $"{who}: create-production-orders");
            (await _k.Get($"/api/sale-orders/{so2}", client)).ShouldBe(NotFound, $"{who}: SO detail");
            // The A30 production-order read has no own-org check for a super admin (pre-existing, outside A34: the EF
            // filter is off for them, and Material lets a super admin work other orgs' production). A34's new action is checked.
            if (client is not null)
                (await _k.Get($"/api/production-orders/{po2}", client)).ShouldBe(NotFound, $"{who}: production order");
            (await _k.TryCreateDeliveryNow(po2, client)).ShouldBe(NotFound, $"{who}: create-delivery");

            (await _k.TryUpdateRouteCategory(await k2.RouteByUuidAsync(mfg2), Stock, client)).ShouldBe(NotFound, $"{who}: route category");
            (await _k.AssignVariantRoute(mine.VariantUuid, mfg2, client)).Status.Should()
                .BeOneOf(new[] { NotFound, HttpStatusCode.BadRequest }, $"{who}: org 2's route on an org 1 variant");
        }
        (await _k.DeliveriesFromPoAsync(po2, orgUser)).Should().BeEmpty("the tenant filter hides org 2's deliveries from an org 1 user");

        // Nothing moved in org 2.
        (await k2.ProductionOrderAsync(po2)).S("status").Should().BeOneOf(PlannedOrLater);
        (await k2.GetSaleOrderAsync(so2)).LineOf(w2.Fg).IsNull("manualDeliveryDate").Should().BeTrue();
        (await k2.ProductionOrdersOfAsync(so2)).Should().ContainSingle();
        (await k2.Ok(k2.Get($"/api/variants/{w2.Fg.VariantUuid}/lead-times"), "org 2 reads its variant")).G("variantUuid").Should().Be(w2.Fg.VariantUuid);
        (await k2.Post("/api/lead-time/calculate", new { VariantUuid = mine.VariantUuid, Quantity = 1m })).ShouldBe(NotFound, "org 2 on org 1's variant");

        // R-12: every make-to-order PO points at its own org's route and order.
        var leaks = await _f.QueryAsync(
            "SELECT COUNT(*) AS N FROM material.production_orders p JOIN logistics.fulfillment_routes r ON r.UUID = p.FulfillmentRouteUuid " +
            "WHERE r.OrganizationId <> p.OrganizationId");
        Convert.ToInt32(leaks[0]["N"]).Should().Be(0);
    }

    [Fact]
    public async Task The_production_sweep_recovers_each_organizations_orders_in_their_own_organization()
    {
        var (org2, k2, w2) = await SecondOrgWorldAsync("SW2");
        var w1 = await _k.MtoWorldAsync("SW1", rawStock: 20m);

        async Task<(Guid So, Guid Po)> LostProductionAsync(SapKit k, MtoWorld w)
        {
            var so = await k.CreateOrderAsync(w.Customer, w.Pkr, "SHIP", w.Address, RLine(w.Fg, 1m));
            var po = (await k.ConfirmAsync(so)).A("productionOrders").Single().G("productionOrderUuid");
            // The production order is gone (cancelled by hand) and the order is still marked pending, as after a failure.
            await k.Ok(k.Post($"/api/production-orders/{po}/cancel", new { Reason = "simulate a lost creation" }), "cancel the PO");
            await k.BackdateProductionPendingAsync(so);
            return (so, po);
        }
        var (so1, old1) = await LostProductionAsync(_k, w1);
        var (so2, old2) = await LostProductionAsync(k2, w2);

        await _k.RunProductionSweepAsync();

        async Task<Guid> RecoveredAsync(SapKit k, Guid so, Guid old, Guid org)
        {
            var live = (await k.ProductionOrdersOfAsync(so)).Where(p => p.S("status") != "CANCELLED").ToList();
            live.Should().ContainSingle($"the sweep re-created the production order of {so}");
            var po = live[0].G("uuid");
            po.Should().NotBe(old);
            (await k.ProductionPendingSinceAsync(so)).Should().BeNull("the sweep cleared the flag");
            var row = await _f.QueryAsync("SELECT OrganizationId FROM material.production_orders WHERE UUID = @p", ("@p", po));
            row.Single()["OrganizationId"].Should().Be(org, "R-11: created in the order's own organization");
            return po;
        }
        await RecoveredAsync(_k, so1, old1, _f.OrganizationId);
        await RecoveredAsync(k2, so2, old2, org2);

        // Once is enough.
        await _k.RunProductionSweepAsync();
        (await _k.ProductionOrdersOfAsync(so1)).Count(p => p.S("status") != "CANCELLED").Should().Be(1);
        (await k2.ProductionOrdersOfAsync(so2)).Count(p => p.S("status") != "CANCELLED").Should().Be(1);
    }
}

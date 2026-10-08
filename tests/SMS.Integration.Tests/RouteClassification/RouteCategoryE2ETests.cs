using System.Net;
using FluentAssertions;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using Xunit;
using static SMS.Integration.Tests.FulfillmentRoutes.Routes;
using static SMS.Integration.Tests.RouteClassification.Rc;

namespace SMS.Integration.Tests.RouteClassification;

/// <summary>
/// A34 C1/C2 on the real host (LocalDB): route categories and route-based classification.
/// <list type="bullet">
/// <item><b>T-C1-01/02, D-6, D-7, D-9:</b> five seeds per org (two MANUFACTURE, never default), create / update with a
/// category, the <c>?category=</c> filter, BUY / DROPSHIP / unknown codes refused, set-default refused on MANUFACTURE.</item>
/// <item><b>T-C1-03, D-8:</b> a category change is refused (409) on a system route, a default route and a route in use
/// (a real variant, a real open sale order line); sending the current category again always passes.</item>
/// <item><b>T-C2-01/02, D-3, T-C2-04:</b> a MANUFACTURE route is assigned only to manufactured products (single PUT 400,
/// bulk skips), the product flags are never written, BOMs survive an unassign, and the product list filters by the
/// variant route's category.</item>
/// </list>
/// <para>Run alone: <c>dotnet test &lt;out&gt;\SMS.Integration.Tests.dll --filter FullyQualifiedName~RouteCategoryE2ETests</c>.</para>
/// </summary>
public sealed class RouteCategoryE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _k;

    public RouteCategoryE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _k = new SapKit(factory, "C1");
    }

    [Fact]
    public async Task Seeds_create_update_filter_and_the_reserved_categories()
    {
        // ── Seeds (D-9, contract §3): 3 STOCK + 2 MANUFACTURE, the MFG ones system and never default ──
        var all = await _k.RoutesAsync(includeInactive: true);
        var seeds = all.Where(r => r.B("isSystem")).ToDictionary(r => r.S("code")!);
        seeds.Keys.Should().BeEquivalentTo(new[] { PickOnly, PickAndShip, PickPackShip, MfgPickShip, MfgPickPackShip });
        foreach (var code in new[] { PickOnly, PickAndShip, PickPackShip })
            seeds[code].S("routeCategory").Should().Be(Stock, $"{code} is a stock route");
        (seeds[MfgPickShip].S("routeCategory"), seeds[MfgPickShip].B("isDefault"), seeds[MfgPickShip].S("name"))
            .Should().Be((Manufacture, false, "Manufacture → Pick & Ship"));
        seeds[MfgPickShip].A("steps").OrderBy(s => s.I("stepOrder")).Select(s => s.S("stepCode")).Should().Equal("PICK", "GOODS_ISSUE", "SHIP");
        (seeds[MfgPickPackShip].S("routeCategory"), seeds[MfgPickPackShip].B("isDefault"), seeds[MfgPickPackShip].S("name"))
            .Should().Be((Manufacture, false, "Manufacture → Pick, Pack & Ship"));
        seeds[MfgPickPackShip].A("steps").OrderBy(s => s.I("stepOrder")).Select(s => s.S("stepCode")).Should().Equal("PICK", "PACK", "GOODS_ISSUE", "SHIP");
        (seeds[MfgPickShip].I("displayOrder"), seeds[MfgPickPackShip].I("displayOrder")).Should().Be((40, 50));
        all.Where(r => r.S("routeCategory") == Manufacture).Should().OnlyContain(r => !r.B("isDefault"), "D-6: never a default");

        // ── Create: omitted → STOCK; trimmed and upper-cased; a MANUFACTURE route without SHIP is fine (C-16) ──
        var plain = await _k.CreateCategoryRouteAsync("PLN", null, "PICK", "GOODS_ISSUE");
        plain.S("routeCategory").Should().Be(Stock, "omitted = STOCK");
        var mfg = await _k.CreateCategoryRouteAsync("MTO", " manufacture ", "PICK", "GOODS_ISSUE");
        (mfg.S("routeCategory"), mfg.B("isDefault"), mfg.B("requiresShipping")).Should().Be((Manufacture, false, false));
        (await _k.RouteByUuidAsync(mfg.G("uuid"))).S("routeCategory").Should().Be(Manufacture, "the read shows the stored category");

        // ── D-7 / BR-C1-01: reserved and unknown categories ──
        var buy = await _k.TryCreateCategoryRoute($"BY_{Guid.NewGuid():N}"[..10].ToUpperInvariant(), "buy", "BUY", ["PICK", "GOODS_ISSUE"]);
        buy.ShouldBe(HttpStatusCode.BadRequest, "D-7: BUY is reserved");
        buy.Message.Should().Contain("'BUY' is not yet available");
        var drop = await _k.TryCreateCategoryRoute($"DS_{Guid.NewGuid():N}"[..10].ToUpperInvariant(), "drop", "dropship", ["PICK", "GOODS_ISSUE"]);
        drop.ShouldBe(HttpStatusCode.BadRequest, "D-7: DROPSHIP is reserved");
        drop.Message.Should().Contain("not yet available");
        var unknown = await _k.TryCreateCategoryRoute($"UK_{Guid.NewGuid():N}"[..10].ToUpperInvariant(), "unknown", "FOO", ["PICK", "GOODS_ISSUE"]);
        unknown.ShouldBe(HttpStatusCode.BadRequest, "BR-C1-01");
        unknown.Message.Should().Contain("Unknown route category 'FOO'");

        // ── ?category= (case-insensitive; an unknown code is a 400) ──
        var manufactureOnly = (await _k.Ok(_k.Get("/api/fulfillment-routes?includeInactive=true&category=manufacture"), "filter MANUFACTURE")).Items();
        manufactureOnly.Select(r => r.S("code")).Should().Contain(new[] { MfgPickShip, MfgPickPackShip, mfg.S("code") });
        manufactureOnly.Should().OnlyContain(r => r.S("routeCategory") == Manufacture);
        var stockOnly = (await _k.Ok(_k.Get("/api/fulfillment-routes?category=STOCK"), "filter STOCK")).Items();
        stockOnly.Select(r => r.S("code")).Should().Contain(new[] { PickOnly, PickAndShip, PickPackShip, plain.S("code") }).And.NotContain(MfgPickShip);
        (await _k.Get("/api/fulfillment-routes?category=BOGUS")).ShouldBe(HttpStatusCode.BadRequest, "an unknown filter code");

        // ── D-6: a MANUFACTURE route can't be a default ──
        var setDefault = await _k.RoutePatch(seeds[MfgPickShip].G("uuid"), "set-default");
        setDefault.ShouldBe(HttpStatusCode.BadRequest, "D-6: set-default on a MANUFACTURE seed");
        setDefault.Message.Should().Contain("make-to-order (MANUFACTURE) route and can't be a default");
        (await _k.RoutePatch(mfg.G("uuid"), "set-default")).ShouldBe(HttpStatusCode.BadRequest, "D-6: set-default on a custom MANUFACTURE route");
        (await _k.RouteAsync(PickAndShip)).B("isDefault").Should().BeTrue("the SHIP default is untouched");

        // ── Update: an unused custom route changes freely; the current category again is no change; null = unchanged ──
        var toMfg = await _k.Ok(_k.TryUpdateRouteCategory(plain, "MANUFACTURE"), "STOCK → MANUFACTURE on an unused custom route");
        toMfg.S("routeCategory").Should().Be(Manufacture);
        (await _k.Ok(_k.TryUpdateRouteCategory(toMfg, "MANUFACTURE"), "the same category again")).S("routeCategory").Should().Be(Manufacture);
        (await _k.Ok(_k.TryUpdateRouteCategory(toMfg, null), "category omitted")).S("routeCategory").Should().Be(Manufacture, "null keeps it");
        (await _k.TryUpdateRouteCategory(toMfg, "BUY")).ShouldBe(HttpStatusCode.BadRequest, "D-7 on update too");
        (await _k.TryUpdateRouteCategory(toMfg, "NOPE")).ShouldBe(HttpStatusCode.BadRequest, "BR-C1-01 on update too");
        (await _k.Ok(_k.TryUpdateRouteCategory(toMfg, "stock"), "back to STOCK")).S("routeCategory").Should().Be(Stock);
    }

    [Fact]
    public async Task A_category_change_is_refused_on_system_default_and_in_use_routes()
    {
        var pkr = await _k.PkrBaseAsync();
        var whId = await _k.WarehouseIdAsync(await _k.CreateWarehouseAsync());
        var customer = await _k.CreateCustomerAsync("Usage Customer");

        // ── System routes (D-8) ──
        var pps = await _k.RouteAsync(PickPackShip);
        var sys = await _k.TryUpdateRouteCategory(pps, "MANUFACTURE");
        sys.ShouldBe(HttpStatusCode.Conflict, "D-8: a system route keeps its category");
        sys.Message.Should().Contain($"'{PickPackShip}' is a system route: its category can't be changed");
        (await _k.TryUpdateRouteCategory(await _k.RouteAsync(MfgPickShip), "STOCK")).ShouldBe(HttpStatusCode.Conflict, "D-8: both ways");
        await _k.Ok(_k.TryUpdateRouteCategory(pps, "STOCK"), "the current category on a system route is no change");

        // ── A default route (D-6 / D-8) ──
        var shipDefault = (await _k.RouteAsync(PickAndShip)).G("uuid");
        var custom = await _k.CreateCategoryRouteAsync("DEF", "STOCK", "PICK", "GOODS_ISSUE", "SHIP");
        await _k.Ok(_k.RoutePatch(custom.G("uuid"), "set-default"), "make the custom route the SHIP default");
        try
        {
            var def = await _k.TryUpdateRouteCategory(await _k.RouteByUuidAsync(custom.G("uuid")), "MANUFACTURE");
            def.ShouldBe(HttpStatusCode.Conflict, "D-6: a default route can't become MANUFACTURE");
            def.Message.Should().Contain("is the default route for SHIP orders");
        }
        finally
        {
            await _k.Ok(_k.RoutePatch(shipDefault, "set-default"), "restore the SHIP default");
        }
        await _k.Ok(_k.TryUpdateRouteCategory(await _k.RouteByUuidAsync(custom.G("uuid")), "MANUFACTURE"), "no longer the default: it changes");

        // ── In use by an active variant (T-C1-03, real usage) ──
        var mto = await _k.CreateCategoryRouteAsync("USE", "MANUFACTURE", "PICK", "GOODS_ISSUE", "SHIP");
        var fg = await _k.FinishedGoodAsync("Usage FG", whId);
        await _k.SetVariantRouteAsync(fg, mto.G("uuid"));
        var used = await _k.TryUpdateRouteCategory(mto, "STOCK");
        used.ShouldBe(HttpStatusCode.Conflict, "D-8: a variant uses it");
        used.Message.Should().Contain("still used by 1 active product variants").And.Contain("Reassign them first");
        await _k.SetVariantRouteAsync(fg, null);
        await _k.Ok(_k.TryUpdateRouteCategory(mto, "STOCK"), "unassigned: it changes");

        // ── In use by an open sale order line (a draft's line override) ──
        var stockRoute = await _k.CreateCategoryRouteAsync("LIN", "STOCK", "PICK", "GOODS_ISSUE");
        var plain = await _k.CreateProductAsync("Usage Plain", 5m, 9m);
        var so = await _k.CreateOrderAsync(customer, pkr, "SELF_PICKUP", null, RLine(plain, 1m, route: stockRoute.G("uuid")));
        var lineUse = await _k.TryUpdateRouteCategory(stockRoute, "MANUFACTURE");
        lineUse.ShouldBe(HttpStatusCode.Conflict, "D-8: an open sale order line uses it");
        lineUse.Message.Should().Contain("still used by");
        await _k.Ok(_k.TryCancelOrder(so), "cancel the draft");
        await _k.Ok(_k.TryUpdateRouteCategory(stockRoute, "MANUFACTURE"), "the line is cancelled: it changes");
    }

    [Fact]
    public async Task A_manufacture_route_goes_only_to_manufactured_products_and_never_writes_their_flags()
    {
        var pkr = await _k.PkrBaseAsync();
        await _k.CreateApproverPlaceholdersAsync();
        var whId = await _k.WarehouseIdAsync(await _k.CreateWarehouseAsync());
        var category = await _k.CreateProductCategoryAsync("C2 Category");
        var raw = await _k.RawMaterialAsync("C2 Raw");
        var fg = await _k.FinishedGoodAsync("C2 FG", whId, categoryId: category);
        var fg2 = await _k.FinishedGoodAsync("C2 FG Two", whId, categoryId: category);
        var bought = await _k.CreateClassifiedProductAsync("C2 Bought", SMS.Shared.Common.ProductType.StockItem, SMS.Shared.Common.SupplyMethod.Purchase,
            5m, 9m, categoryId: category);
        var bom = await _k.CreateActiveBomAsync(fg, 1m, (raw, 3m));
        var mfgRoute = await _k.RouteUuidAsync(MfgPickShip);
        _ = pkr;

        async Task<(string? Supply, bool Manufacturable)> FlagsAsync(Product p)
        {
            var d = await _k.ProductDetailAsync(p);
            return (d.S("supplyMethod"), d.B("isManufacturable"));
        }
        var fgFlags = await FlagsAsync(fg);
        fgFlags.Should().Be(("MANUFACTURE", true));

        // ── T-C2-01: assign → "Make to order", flags untouched ──
        await _k.SetVariantRouteAsync(fg, mfgRoute);
        var v = await _k.VariantAsync(fg);
        (v.NG("fulfillmentRouteUuid"), v.S("fulfillmentRouteCategory"), v.B("isMakeToOrder")).Should().Be((mfgRoute, Manufacture, true));
        (await FlagsAsync(fg)).Should().Be(fgFlags, "D-3: a route never writes the product flags");

        // ── D-3: not a manufactured product → 400; nothing written ──
        var refused = await _k.AssignVariantRoute(bought.VariantUuid, mfgRoute);
        refused.ShouldBe(HttpStatusCode.BadRequest, "D-3: only manufactured products take a MANUFACTURE route");
        refused.Message.Should().Contain("Set the product's supply method to MANUFACTURE first");
        (await _k.VariantAsync(bought)).IsNull("fulfillmentRouteUuid").Should().BeTrue();
        (await FlagsAsync(bought)).Should().Be(("PURCHASE", false));

        // ── A stock route on a manufactured product is fine (make-to-stock, D-2) ──
        await _k.SetVariantRouteAsync(fg, await _k.RouteUuidAsync(PickPackShip));
        v = await _k.VariantAsync(fg);
        (v.S("fulfillmentRouteCategory"), v.B("isMakeToOrder")).Should().Be((Stock, false));

        // ── T-C2-02: unassign → no category, flags and BOM kept ──
        await _k.SetVariantRouteAsync(fg, mfgRoute);
        await _k.SetVariantRouteAsync(fg, null);
        v = await _k.VariantAsync(fg);
        (v.IsNull("fulfillmentRouteUuid"), v.IsNull("fulfillmentRouteCategory"), v.B("isMakeToOrder")).Should().Be((true, true, false));
        (await FlagsAsync(fg)).Should().Be(fgFlags);
        (await _k.Ok(_k.Get($"/api/boms/{bom}"), "read BOM")).S("status").Should().Be("ACTIVE", "T-C2-02: the BOM is kept");

        // ── Bulk (contract §4.2): non-manufactured products are skipped, not refused ──
        var bulk = await _k.Ok(_k.Post($"/api/fulfillment-routes/{mfgRoute}/assign-by-category", new { CategoryId = category }), "bulk assign the MANUFACTURE route");
        (bulk.I("updated"), bulk.I("skipped")).Should().Be((2, 1), "fg and fg2 take it; the bought product is skipped");
        (await _k.VariantAsync(fg2)).NG("fulfillmentRouteUuid").Should().Be(mfgRoute);
        (await _k.VariantAsync(bought)).IsNull("fulfillmentRouteUuid").Should().BeTrue("skipped");

        // ── T-C2-04: product list by route category ──
        await _k.SetVariantRouteAsync(bought, await _k.RouteUuidAsync(PickOnly));
        async Task<List<int>> ListAsync(string cat) =>
            (await _k.Ok(_k.Get($"/api/products?categoryId={category}&routeCategory={cat}&pageSize=100"), $"products by route category {cat}"))
            .A("data").Select(p => p.I("id")).ToList();
        (await ListAsync("MANUFACTURE")).Should().BeEquivalentTo(new[] { fg.ProductId, fg2.ProductId });
        (await ListAsync("stock")).Should().BeEquivalentTo(new[] { bought.ProductId });
        (await _k.Get($"/api/products?routeCategory=WHATEVER")).ShouldBe(HttpStatusCode.BadRequest, "unknown route category");

        // Clearing a MANUFACTURE route is always allowed.
        await _k.SetVariantRouteAsync(fg2, null);
        (await ListAsync("MANUFACTURE")).Should().BeEquivalentTo(new[] { fg.ProductId });
    }
}

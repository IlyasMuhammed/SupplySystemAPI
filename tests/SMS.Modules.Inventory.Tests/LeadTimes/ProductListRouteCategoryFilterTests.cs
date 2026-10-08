using FluentAssertions;
using Hangfire;
using Moq;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Repositories;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;
using static SMS.Modules.Inventory.Tests.LeadTimeKit;

namespace SMS.Modules.Inventory.Tests;

/// <summary>A34-PA-10 server side (T-C2-04, API-CONTRACT §4.3): <c>GET api/products?routeCategory=</c>.</summary>
public class ProductListRouteCategoryFilterTests
{
    private static readonly Guid OrgA = Guid.NewGuid();

    private static InventoryService Service(InventoryDbContext db, IFulfillmentRouteLookup? routes) => new(
        new InventoryRepository(db, new Mock<IInventoryLedgerService>().Object), new ProductSearchIndexService(db),
        new Mock<IBackgroundJobClient>().Object, [], routeLookup: routes, tenantContext: db.TenantContext);

    private sealed record Seeded(string Name, FakeFulfillmentRouteLookup Routes);

    private static async Task<Seeded> SeedAsync()
    {
        var name = Guid.NewGuid().ToString();
        var routes = new FakeFulfillmentRouteLookup();
        var mfg = routes.Add(OrgA, "MFG_PICK_SHIP", category: FulfillmentRouteCategory.Manufacture);
        var stock = routes.Add(OrgA, "PICK_AND_SHIP");
        await VariantAsync(Db(name, OrgA), SupplyMethod.Manufacture, route: mfg.Uuid, name: "Made");
        await VariantAsync(Db(name, OrgA), route: stock.Uuid, name: "Stocked");
        await VariantAsync(Db(name, OrgA), name: "Unrouted");
        await VariantAsync(Db(name, OrgA), SupplyMethod.Manufacture, route: mfg.Uuid, name: "Retired", configure: v => v.IsActive = false);
        return new Seeded(name, routes);
    }

    private static async Task<List<string>> NamesAsync(Seeded s, ProductListFilter filter, IFulfillmentRouteLookup? routes = null)
    {
        var result = await Service(Db(s.Name, OrgA), routes ?? s.Routes).GetProductsAsync(filter);
        return result.Data.Select(p => p.Name).OrderBy(n => n).ToList();
    }

    [Fact]
    public async Task T_C2_04_MANUFACTURE_returns_products_with_an_active_variant_on_a_manufacture_route()
    {
        var s = await SeedAsync();

        (await NamesAsync(s, new ProductListFilter { RouteCategory = "MANUFACTURE" })).Should().Equal("Made");
        (await NamesAsync(s, new ProductListFilter { RouteCategory = "manufacture" })).Should().Equal(["Made"], "the code is case-insensitive");
    }

    [Fact]
    public async Task STOCK_returns_only_variants_routed_to_a_stock_route_not_unrouted_ones()
    {
        var s = await SeedAsync();

        (await NamesAsync(s, new ProductListFilter { RouteCategory = "STOCK" })).Should().Equal("Stocked");
    }

    [Fact]
    public async Task It_combines_with_the_other_filters_and_no_filter_returns_everything_active()
    {
        var s = await SeedAsync();

        (await NamesAsync(s, new ProductListFilter { RouteCategory = "MANUFACTURE", SupplyMethod = SupplyMethod.Purchase })).Should().BeEmpty();
        (await NamesAsync(s, new ProductListFilter())).Should().Equal("Made", "Retired", "Stocked", "Unrouted");
    }

    [Fact]
    public async Task An_unknown_category_is_a_400()
    {
        var s = await SeedAsync();

        var act = () => NamesAsync(s, new ProductListFilter { RouteCategory = "SOMETHING" });

        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message
            .Should().Be("Unknown route category 'SOMETHING'. Categories are STOCK, MANUFACTURE, BUY, DROPSHIP.");
    }

    [Fact]
    public async Task Without_routes_in_the_host_a_category_filter_matches_nothing()
    {
        var s = await SeedAsync();

        var result = await Service(Db(s.Name, OrgA), routes: null).GetProductsAsync(new ProductListFilter { RouteCategory = "MANUFACTURE" });

        result.Data.Should().BeEmpty();
    }
}

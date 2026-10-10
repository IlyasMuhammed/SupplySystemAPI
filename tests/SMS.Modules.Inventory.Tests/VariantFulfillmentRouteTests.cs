using System.Reflection;
using FluentAssertions;
using Hangfire;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Inventory.Controllers;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Repositories;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Inventory.Tests;

/// <summary>
/// A33 C2 (A33-PB-05): a variant's default fulfillment route. T-C2-01, T-C2-02 and T-C2-04, BR-C2-01, the cross-org
/// and super-admin 404s, the two shared contracts Inventory implements (<see cref="IVariantFulfillmentRoutes"/> for
/// Demand's resolver, <see cref="IFulfillmentRouteUsage"/> for Logistics' "in use" refusal), and the bulk assign's
/// refusals. The bulk assign's own update (T-C2-03) and its race run on SQL Server in
/// <see cref="VariantFulfillmentRouteBulkAssignTests"/>.
/// </summary>
public class VariantFulfillmentRouteTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private static InventoryDbContext Db(string name, Guid org, bool superAdmin = false) => new(
        new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(name).Options,
        new StaticTenantContext { OrganizationId = org, IsSuperAdmin = superAdmin });

    // ── PUT api/variants/{uuid}/fulfillment-route ─────────────────────────────────────────────────

    [Fact]
    public async Task T_C2_01_an_active_route_of_the_own_organization_is_saved_on_the_variant()
    {
        var name = Guid.NewGuid().ToString();
        var routes = new FakeFulfillmentRouteLookup();
        var route = routes.Add(OrgA, "PICK_AND_SHIP");
        var seeded = await RouteSeed.SeedAsync(Db(name, OrgA));

        var result = await new VariantFulfillmentRouteService(Db(name, OrgA), routes).SetRouteAsync(seeded.VariantUuid, route.Uuid);

        result.VariantUuid.Should().Be(seeded.VariantUuid);
        result.FulfillmentRouteUuid.Should().Be(route.Uuid);
        result.FulfillmentRouteCode.Should().Be("PICK_AND_SHIP");
        result.FulfillmentRouteName.Should().Be(route.Name);
        (await RouteOf(name, seeded.VariantUuid)).Should().Be(route.Uuid);
    }

    [Fact]
    public async Task T_C2_02_a_route_of_another_organization_is_refused_and_the_variant_keeps_its_route()
    {
        var name = Guid.NewGuid().ToString();
        var routes = new FakeFulfillmentRouteLookup();
        var own = routes.Add(OrgA, "PICK_ONLY");
        var foreign = routes.Add(OrgB, "PICK_AND_SHIP");
        var seeded = await RouteSeed.SeedAsync(Db(name, OrgA), route: own.Uuid);

        var act = () => new VariantFulfillmentRouteService(Db(name, OrgA), routes).SetRouteAsync(seeded.VariantUuid, foreign.Uuid);

        (await act.Should().ThrowAsync<BadRequestException>())
            .Which.Message.Should().Contain("route must belong to your organization");
        (await RouteOf(name, seeded.VariantUuid)).Should().Be(own.Uuid);
    }

    [Fact]
    public async Task An_unknown_route_is_refused_the_same_way()
    {
        var name = Guid.NewGuid().ToString();
        var seeded = await RouteSeed.SeedAsync(Db(name, OrgA));

        var act = () => new VariantFulfillmentRouteService(Db(name, OrgA), new FakeFulfillmentRouteLookup())
            .SetRouteAsync(seeded.VariantUuid, Guid.NewGuid());

        (await act.Should().ThrowAsync<BadRequestException>())
            .Which.Message.Should().Contain("route must belong to your organization");
        (await RouteOf(name, seeded.VariantUuid)).Should().BeNull();
    }

    [Fact]
    public async Task BR_C2_01_an_inactive_route_is_refused()
    {
        var name = Guid.NewGuid().ToString();
        var routes = new FakeFulfillmentRouteLookup();
        var inactive = routes.Add(OrgA, "OLD_ROUTE", isActive: false);
        var seeded = await RouteSeed.SeedAsync(Db(name, OrgA));

        var act = () => new VariantFulfillmentRouteService(Db(name, OrgA), routes).SetRouteAsync(seeded.VariantUuid, inactive.Uuid);

        (await act.Should().ThrowAsync<BadRequestException>())
            .Which.Message.Should().Contain("OLD_ROUTE").And.Contain("inactive");
        (await RouteOf(name, seeded.VariantUuid)).Should().BeNull();
    }

    [Fact]
    public async Task T_C2_04_clearing_sets_the_route_back_to_null()
    {
        var name = Guid.NewGuid().ToString();
        var routes = new FakeFulfillmentRouteLookup();
        var route = routes.Add(OrgA, "PICK_AND_SHIP");
        var seeded = await RouteSeed.SeedAsync(Db(name, OrgA), route: route.Uuid);

        var result = await new VariantFulfillmentRouteService(Db(name, OrgA), routes).SetRouteAsync(seeded.VariantUuid, null);

        result.FulfillmentRouteUuid.Should().BeNull();
        result.FulfillmentRouteCode.Should().BeNull();
        (await RouteOf(name, seeded.VariantUuid)).Should().BeNull();
    }

    [Fact]
    public async Task Clearing_still_works_when_the_route_has_since_been_deactivated()
    {
        var name = Guid.NewGuid().ToString();
        var routes = new FakeFulfillmentRouteLookup();
        var route = routes.Add(OrgA, "PICK_AND_SHIP", isActive: false);
        var seeded = await RouteSeed.SeedAsync(Db(name, OrgA), route: route.Uuid);

        await new VariantFulfillmentRouteService(Db(name, OrgA), routes).SetRouteAsync(seeded.VariantUuid, null);

        (await RouteOf(name, seeded.VariantUuid)).Should().BeNull();
    }

    [Fact]
    public async Task Another_organizations_variant_is_not_found()
    {
        var name = Guid.NewGuid().ToString();
        var routes = new FakeFulfillmentRouteLookup();
        var bRoute = routes.Add(OrgB, "PICK_AND_SHIP");
        var seeded = await RouteSeed.SeedAsync(Db(name, OrgA));

        var act = () => new VariantFulfillmentRouteService(Db(name, OrgB), routes).SetRouteAsync(seeded.VariantUuid, bRoute.Uuid);

        await act.Should().ThrowAsync<NotFoundException>();
        (await RouteOf(name, seeded.VariantUuid)).Should().BeNull();
    }

    [Fact]
    public async Task Another_organizations_variant_is_not_found_for_a_super_admin_either()
    {
        var name = Guid.NewGuid().ToString();
        var routes = new FakeFulfillmentRouteLookup();
        var aRoute = routes.Add(OrgA, "PICK_AND_SHIP");
        var bRoute = routes.Add(OrgB, "PICK_AND_SHIP");
        var seeded = await RouteSeed.SeedAsync(Db(name, OrgA), route: aRoute.Uuid);

        // The EF tenant filter is off for a super admin: only the explicit own-org filter stops this.
        var set   = () => new VariantFulfillmentRouteService(Db(name, OrgB, superAdmin: true), routes).SetRouteAsync(seeded.VariantUuid, bRoute.Uuid);
        var clear = () => new VariantFulfillmentRouteService(Db(name, OrgB, superAdmin: true), routes).SetRouteAsync(seeded.VariantUuid, null);

        await set.Should().ThrowAsync<NotFoundException>();
        await clear.Should().ThrowAsync<NotFoundException>();
        (await RouteOf(name, seeded.VariantUuid)).Should().Be(aRoute.Uuid);
    }

    [Fact]
    public async Task Without_a_route_lookup_in_the_host_a_route_cannot_be_set_but_can_be_cleared()
    {
        var name = Guid.NewGuid().ToString();
        var stale = Guid.NewGuid();
        var seeded = await RouteSeed.SeedAsync(Db(name, OrgA), route: stale);

        var set = () => new VariantFulfillmentRouteService(Db(name, OrgA), routeLookup: null).SetRouteAsync(seeded.VariantUuid, Guid.NewGuid());
        await set.Should().ThrowAsync<BadRequestException>();
        (await RouteOf(name, seeded.VariantUuid)).Should().Be(stale);

        await new VariantFulfillmentRouteService(Db(name, OrgA), routeLookup: null).SetRouteAsync(seeded.VariantUuid, null);
        (await RouteOf(name, seeded.VariantUuid)).Should().BeNull();
    }

    // ── The variant dialog's PATCH and the product detail read ───────────────────────────────────

    [Fact]
    public async Task The_ungated_variant_PATCH_leaves_the_route_alone()
    {
        var name = Guid.NewGuid().ToString();
        var route = Guid.NewGuid();
        var seeded = await RouteSeed.SeedAsync(Db(name, OrgA), route: route);
        await using var db = Db(name, OrgA);
        var service = new InventoryService(
            new InventoryRepository(db, new Mock<IInventoryLedgerService>().Object), new ProductSearchIndexService(db),
            new Mock<IBackgroundJobClient>().Object, []);

        var ok = await service.UpdateVariantAsync(seeded.VariantUuid, new CreateProductVariantRequest
        {
            VariantName = "Renamed", PurchasePrice = 12m, IsDefault = true
        });

        ok.Should().BeTrue();
        (await RouteOf(name, seeded.VariantUuid)).Should().Be(route);
        typeof(CreateProductVariantRequest).GetProperties().Select(p => p.Name)
            .Should().NotContain(n => n.Contains("Route"), "route assignment has its own gated endpoint (contract §4)");
    }

    [Fact]
    public async Task The_product_detail_shows_each_variants_route_uuid_code_and_name()
    {
        var name = Guid.NewGuid().ToString();
        var routes = new FakeFulfillmentRouteLookup();
        var route = routes.Add(OrgA, "PICK_PACK_SHIP");
        var seeded = await RouteSeed.SeedAsync(Db(name, OrgA), route: route.Uuid, extraVariantWithoutRoute: true);
        await using var db = Db(name, OrgA);
        var service = new InventoryService(
            new InventoryRepository(db, new Mock<IInventoryLedgerService>().Object), new ProductSearchIndexService(db),
            new Mock<IBackgroundJobClient>().Object, [], routeLookup: routes);

        var detail = await service.GetProductByIdAsync(seeded.ProductId);

        var withRoute = detail!.Variants.Single(v => v.Uuid == seeded.VariantUuid);
        withRoute.FulfillmentRouteUuid.Should().Be(route.Uuid);
        withRoute.FulfillmentRouteCode.Should().Be("PICK_PACK_SHIP");
        withRoute.FulfillmentRouteName.Should().Be(route.Name);
        var without = detail.Variants.Single(v => v.Uuid != seeded.VariantUuid);
        without.FulfillmentRouteUuid.Should().BeNull();
        without.FulfillmentRouteCode.Should().BeNull();
    }

    // ── IVariantFulfillmentRoutes (Demand's resolver) ──────────────────────────────────────────

    [Fact]
    public async Task Route_uuids_come_back_only_for_the_given_organizations_variants_that_have_one()
    {
        var name = Guid.NewGuid().ToString();
        var aRoute = Guid.NewGuid();
        var bRoute = Guid.NewGuid();
        var a = await RouteSeed.SeedAsync(Db(name, OrgA), route: aRoute, extraVariantWithoutRoute: true);
        var b = await RouteSeed.SeedAsync(Db(name, OrgB), route: bRoute);

        // A super admin's context (no tenant filter) proves the explicit organization filter does the work.
        var reader = new VariantFulfillmentRoutes(Db(name, OrgB, superAdmin: true));
        var map = await reader.GetRouteUuidsAsync(OrgA, [a.VariantUuid, a.OtherVariantUuid!.Value, b.VariantUuid, Guid.NewGuid()]);

        map.Should().HaveCount(1);
        map[a.VariantUuid].Should().Be(aRoute);
    }

    // ── IFulfillmentRouteUsage (Logistics' BR-C1-07 refusal) ───────────────────────────────────

    [Fact]
    public async Task Usage_counts_the_active_variants_of_active_products_of_the_organization_only()
    {
        var name = Guid.NewGuid().ToString();
        var route = Guid.NewGuid();
        await RouteSeed.SeedAsync(Db(name, OrgA), route: route);                           // counts
        await RouteSeed.SeedAsync(Db(name, OrgA), route: route);                           // counts
        await RouteSeed.SeedAsync(Db(name, OrgA), route: route, variantActive: false);     // inactive variant
        await RouteSeed.SeedAsync(Db(name, OrgA), route: route, productActive: false);     // inactive product
        await RouteSeed.SeedAsync(Db(name, OrgA), route: Guid.NewGuid());                 // another route
        await RouteSeed.SeedAsync(Db(name, OrgB), route: route);                           // another organization

        var usage = await new VariantFulfillmentRoutes(Db(name, OrgB, superAdmin: true)).CountUsageAsync(OrgA, route);

        usage.Count.Should().Be(2);
        usage.Description.Should().Be("active product variants");
    }

    // ── POST api/fulfillment-routes/{uuid}/assign-by-category: the refusals ────────────────────

    [Fact]
    public async Task Bulk_assign_with_another_organizations_route_is_not_found()
    {
        var name = Guid.NewGuid().ToString();
        var routes = new FakeFulfillmentRouteLookup();
        var foreign = routes.Add(OrgB, "PICK_AND_SHIP");
        var seeded = await RouteSeed.SeedAsync(Db(name, OrgA));

        var act = () => new VariantFulfillmentRouteService(Db(name, OrgA), routes)
            .AssignByCategoryAsync(foreign.Uuid, new AssignRouteByCategoryRequest { CategoryId = seeded.CategoryId });

        await act.Should().ThrowAsync<NotFoundException>();
        (await RouteOf(name, seeded.VariantUuid)).Should().BeNull();
    }

    [Fact]
    public async Task Bulk_assign_by_a_super_admin_with_another_organizations_route_is_not_found()
    {
        var name = Guid.NewGuid().ToString();
        var routes = new FakeFulfillmentRouteLookup();
        var aRoute = routes.Add(OrgA, "PICK_AND_SHIP");
        var seeded = await RouteSeed.SeedAsync(Db(name, OrgA));

        var act = () => new VariantFulfillmentRouteService(Db(name, OrgB, superAdmin: true), routes)
            .AssignByCategoryAsync(aRoute.Uuid, new AssignRouteByCategoryRequest { CategoryId = seeded.CategoryId });

        await act.Should().ThrowAsync<NotFoundException>();
        (await RouteOf(name, seeded.VariantUuid)).Should().BeNull();
    }

    [Fact]
    public async Task Bulk_assign_with_an_inactive_route_is_refused()
    {
        var name = Guid.NewGuid().ToString();
        var routes = new FakeFulfillmentRouteLookup();
        var inactive = routes.Add(OrgA, "OLD_ROUTE", isActive: false);
        var seeded = await RouteSeed.SeedAsync(Db(name, OrgA));

        var act = () => new VariantFulfillmentRouteService(Db(name, OrgA), routes)
            .AssignByCategoryAsync(inactive.Uuid, new AssignRouteByCategoryRequest { CategoryId = seeded.CategoryId });

        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Contain("inactive");
    }

    [Fact]
    public async Task Bulk_assign_with_an_unknown_or_another_organizations_category_is_refused()
    {
        var name = Guid.NewGuid().ToString();
        var routes = new FakeFulfillmentRouteLookup();
        var route = routes.Add(OrgA, "PICK_AND_SHIP");
        await RouteSeed.SeedAsync(Db(name, OrgA));
        var foreign = await RouteSeed.SeedAsync(Db(name, OrgB));
        var service = () => new VariantFulfillmentRouteService(Db(name, OrgA, superAdmin: true), routes);

        var unknown = () => service().AssignByCategoryAsync(route.Uuid, new AssignRouteByCategoryRequest { CategoryId = 987654 });
        var other   = () => service().AssignByCategoryAsync(route.Uuid, new AssignRouteByCategoryRequest { CategoryId = foreign.CategoryId });

        (await unknown.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Contain("category");
        (await other.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Contain("category");
        (await RouteOf(name, foreign.VariantUuid)).Should().BeNull();
    }

    [Fact]
    public async Task Bulk_assign_with_a_sub_category_that_is_not_in_the_category_is_refused()
    {
        var name = Guid.NewGuid().ToString();
        var routes = new FakeFulfillmentRouteLookup();
        var route = routes.Add(OrgA, "PICK_AND_SHIP");
        var first = await RouteSeed.SeedAsync(Db(name, OrgA));
        var second = await RouteSeed.SeedAsync(Db(name, OrgA));

        var act = () => new VariantFulfillmentRouteService(Db(name, OrgA), routes).AssignByCategoryAsync(
            route.Uuid, new AssignRouteByCategoryRequest { CategoryId = first.CategoryId, SubCategoryId = second.SubCategoryId });

        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Contain("sub-category");
    }

    // ── Gating ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(nameof(VariantFulfillmentRoutesController.SetVariantFulfillmentRoute), "PUT", "api/variants/{uuid:guid}/fulfillment-route", "MODULE_INVENTORY")]
    [InlineData(nameof(VariantFulfillmentRoutesController.AssignByCategory), "POST", "api/fulfillment-routes/{uuid:guid}/assign-by-category", "MODULE_LOGISTICS")]
    public void Both_route_actions_require_FULFILLMENT_ROUTE_ASSIGN_and_their_feature(
        string action, string verb, string template, string feature)
    {
        var method = typeof(VariantFulfillmentRoutesController).GetMethod(action)!;

        method.GetCustomAttributes<RequirePermissionAttribute>().SelectMany(a => a.AnyOf)
            .Should().Equal(PermissionCodes.FULFILLMENT_ROUTE_ASSIGN);
        method.GetCustomAttribute<RequiresFeatureAttribute>()!.FeatureCode.Should().Be(feature);
        typeof(VariantFulfillmentRoutesController).GetCustomAttribute<RequiresFeatureAttribute>()
            .Should().BeNull("a class-level feature would shadow the action's own (FeatureAuthorizationFilter takes the first)");
        var route = method.GetCustomAttributes<HttpMethodAttribute>().Single();
        route.HttpMethods.Should().Equal(verb);
        route.Template.Should().Be(template);
        method.GetCustomAttributes().OfType<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>().Should().BeEmpty();
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private static async Task<Guid?> RouteOf(string name, Guid variantUuid)
    {
        await using var db = Db(name, Guid.NewGuid(), superAdmin: true);
        return await db.ProductVariants.Where(v => v.Uuid == variantUuid).Select(v => v.FulfillmentRouteUuid).SingleAsync();
    }
}

internal sealed record RouteSeeded(Guid OrganizationId, int CategoryId, int SubCategoryId, int ProductId, Guid VariantUuid, Guid? OtherVariantUuid);

/// <summary>One category with one sub-category, one product in both, and one (or two) variants — under the context's organization.</summary>
internal static class RouteSeed
{
    internal static async Task<RouteSeeded> SeedAsync(
        InventoryDbContext db, Guid? route = null, bool variantActive = true, bool productActive = true,
        bool extraVariantWithoutRoute = false)
    {
        await using var _ = db;
        var category = new ProductCategory { Name = "Steel", Code = $"C{Guid.NewGuid():N}"[..8], IsActive = true };
        db.ProductCategories.Add(category);
        await db.SaveChangesAsync();
        var sub = new ProductSubCategory { CategoryId = category.Id, Name = "Rods", Code = $"S{Guid.NewGuid():N}"[..8], IsActive = true };
        db.ProductSubCategories.Add(sub);
        await db.SaveChangesAsync();
        var product = new Product
        {
            Uuid = Guid.NewGuid(), Name = "Steel rod 10mm", Sku = $"SKU{Guid.NewGuid():N}"[..12], CategoryId = category.Id,
            SubCategoryId = sub.Id, IsActive = productActive, Status = productActive ? "ACTIVE" : "INACTIVE"
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        var variant = new ProductVariant
        {
            Uuid = Guid.NewGuid(), ProductId = product.Id, Sku = $"V{Guid.NewGuid():N}"[..12], VariantName = "6M",
            IsDefault = true, IsActive = variantActive, FulfillmentRouteUuid = route
        };
        db.ProductVariants.Add(variant);
        ProductVariant? other = null;
        if (extraVariantWithoutRoute)
        {
            other = new ProductVariant
            {
                Uuid = Guid.NewGuid(), ProductId = product.Id, Sku = $"V{Guid.NewGuid():N}"[..12], VariantName = "12M",
                IsActive = true, SortOrder = 1
            };
            db.ProductVariants.Add(other);
        }
        await db.SaveChangesAsync();
        return new RouteSeeded(db.TenantContext.OrganizationId, category.Id, sub.Id, product.Id, variant.Uuid, other?.Uuid);
    }
}

/// <summary>Stands in for Logistics' <see cref="IFulfillmentRouteLookup"/>: each route belongs to one organization.</summary>
internal sealed class FakeFulfillmentRouteLookup : IFulfillmentRouteLookup
{
    private readonly List<(Guid Org, FulfillmentRouteSummary Route)> _routes = [];
    private readonly Dictionary<Guid, FulfillmentRouteSummary> _shippingDefaults = [];

    /// <param name="category">A34 — the route's category (STOCK unless a MANUFACTURE route is wanted).</param>
    /// <param name="isAvailable">A37 D-12 — what Logistics' lookup reports when the route's module is off.</param>
    public FulfillmentRouteSummary Add(Guid org, string code, bool isActive = true, string category = FulfillmentRouteCategory.Stock,
        bool isAvailable = true)
    {
        var ships = code.EndsWith("SHIP", StringComparison.Ordinal);
        IReadOnlyList<string> steps = ships
            ? [FulfillmentStepCode.Pick, FulfillmentStepCode.GoodsIssue, FulfillmentStepCode.Ship]
            : [FulfillmentStepCode.Pick, FulfillmentStepCode.GoodsIssue];
        var route = new FulfillmentRouteSummary(
            Guid.NewGuid(), code, $"Route {code}", isActive, IsDefault: false, IsSystem: false,
            RequiresPacking: false, RequiresShipping: ships, steps)
        {
            Category = category, IsAvailable = isAvailable,
            UnavailableReason = isAvailable ? null : FulfillmentRouteAvailability.ManufacturingOffReason
        };
        _routes.Add((org, route));
        return route;
    }

    /// <summary>A34 — makes <paramref name="route"/> the organization's default for SHIP orders.</summary>
    public void SetShippingDefault(Guid org, FulfillmentRouteSummary route) => _shippingDefaults[org] = route;

    public Task<IReadOnlyDictionary<Guid, FulfillmentRouteSummary>> GetAsync(
        Guid organizationId, IReadOnlyCollection<Guid> routeUuids, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, FulfillmentRouteSummary>>(_routes
            .Where(r => r.Org == organizationId && routeUuids.Contains(r.Route.Uuid))
            .ToDictionary(r => r.Route.Uuid, r => r.Route));

    public Task<FulfillmentRouteDefaults> GetOrgDefaultsAsync(Guid organizationId, CancellationToken ct = default) =>
        Task.FromResult(new FulfillmentRouteDefaults(_shippingDefaults.GetValueOrDefault(organizationId), null));

    public Task<IReadOnlyList<FulfillmentRouteSummary>> ListActiveAsync(Guid organizationId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<FulfillmentRouteSummary>>(_routes
            .Where(r => r.Org == organizationId && r.Route.IsActive).Select(r => r.Route).ToList());
}

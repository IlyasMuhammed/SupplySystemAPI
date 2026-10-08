using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Logistics.Tests.Routes;

/// <summary>
/// A34 PA-01..05 / PA-11 — route category (C1): T-C1-01..04, BR-C1-01..04, D-6, D-7, D-8, D-9.
/// docs/route-classification/API-CONTRACT.md §3.
/// </summary>
public class RouteCategoryServiceTests
{
    private const int User = 7;

    private sealed class FakeUsage(string description) : IFulfillmentRouteUsage
    {
        public readonly Dictionary<Guid, int> Counts = new();
        public Task<FulfillmentRouteUsageCount> CountUsageAsync(Guid organizationId, Guid routeUuid, CancellationToken ct = default) =>
            Task.FromResult(new FulfillmentRouteUsageCount(description, Counts.GetValueOrDefault(routeUuid)));
    }

    private sealed record Harness(
        FulfillmentRouteService Service, LogisticsDbContext Db, StaticTenantContext Tenant, string DbName,
        FakeUsage Variants, FakeUsage Lines, FakeUsage ProductionOrders);

    private static Harness New()
    {
        var (db, tenant, dbName) = LogisticsTestDb.New();
        var variants = new FakeUsage("active product variants");
        var lines    = new FakeUsage("open sale order lines");
        var orders   = new FakeUsage("open production orders");
        return new Harness(new FulfillmentRouteService(db, tenant, [variants, lines, orders]), db, tenant, dbName,
            variants, lines, orders);
    }

    private static CreateFulfillmentRouteRequest Route(string code, string? category, params string[] steps) => new()
    {
        Code = code, Name = code + " route", RouteCategory = category,
        Steps = steps.Select((c, i) => new FulfillmentRouteStepRequest { StepCode = c, StepOrder = i + 1 }).ToList()
    };

    private static UpdateFulfillmentRouteRequest Rename(FulfillmentRouteModel r, string? category) =>
        new() { Name = r.Name, DisplayOrder = r.DisplayOrder, RouteCategory = category };

    // ── Create (T-C1-01, T-C1-02, BR-C1-01) ─────────────────────────────────

    [Fact]
    public async Task T_C1_01_a_manufacture_route_is_created_with_its_category()
    {
        var h = New();
        var route = await h.Service.CreateAsync(Route("MFG_COLLECT", "manufacture", "PICK", "GOODS_ISSUE"), User);

        route.RouteCategory.Should().Be("MANUFACTURE", "the category is trimmed and upper-cased");
        (await h.Db.Set<FulfillmentRoute>().IgnoreQueryFilters().SingleAsync()).RouteCategory.Should().Be("MANUFACTURE");
        (await h.Service.GetByUuidAsync(route.Uuid))!.RouteCategory.Should().Be("MANUFACTURE");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task An_omitted_category_is_stock(string? category)
    {
        var h = New();
        var route = await h.Service.CreateAsync(Route("PLAIN", category, "PICK", "GOODS_ISSUE"), User);
        route.RouteCategory.Should().Be("STOCK");
    }

    [Theory]
    [InlineData("BUY")]
    [InlineData("DROPSHIP")]
    public async Task T_C1_02_reserved_categories_are_refused(string category)
    {
        var h = New();
        var act = () => h.Service.CreateAsync(Route("FUTURE", category, "PICK", "GOODS_ISSUE"), User);

        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Contain("not yet available");
        (await h.Db.Set<FulfillmentRoute>().IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task BR_C1_01_an_unknown_category_is_refused()
    {
        var h = New();
        var act = () => h.Service.CreateAsync(Route("ODD", "TELEPORT", "PICK", "GOODS_ISSUE"), User);
        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Contain("Unknown route category 'TELEPORT'");
    }

    // ── Update (D-7, D-8) ────────────────────────────────────────────────────

    [Fact]
    public async Task A_free_custom_route_can_change_category_and_null_leaves_it_unchanged()
    {
        var h = New();
        var route = await h.Service.CreateAsync(Route("FLEX", null, "PICK", "GOODS_ISSUE", "SHIP"), User);

        var changed = await h.Service.UpdateAsync(route.Uuid, Rename(route, "MANUFACTURE"), User);
        changed!.RouteCategory.Should().Be("MANUFACTURE");

        var kept = await h.Service.UpdateAsync(route.Uuid, Rename(route, null), User);
        kept!.RouteCategory.Should().Be("MANUFACTURE", "null means unchanged");

        var back = await h.Service.UpdateAsync(route.Uuid, Rename(route, "STOCK"), User);
        back!.RouteCategory.Should().Be("STOCK");
    }

    [Fact]
    public async Task D_7_updating_to_a_reserved_category_is_refused()
    {
        var h = New();
        var route = await h.Service.CreateAsync(Route("FLEX", null, "PICK", "GOODS_ISSUE"), User);
        var act = () => h.Service.UpdateAsync(route.Uuid, Rename(route, "BUY"), User);
        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Contain("not yet available");
    }

    [Fact]
    public async Task D_8_a_system_routes_category_is_locked_but_resending_it_passes()
    {
        var h = New();
        await new FulfillmentRouteSeeder(h.Db).EnsureSeededAsync(h.Tenant.OrganizationId);
        var packShip = (await h.Service.GetListAsync(true)).Single(r => r.Code == "PICK_PACK_SHIP");

        var act = () => h.Service.UpdateAsync(packShip.Uuid, Rename(packShip, "MANUFACTURE"), User);
        (await act.Should().ThrowAsync<ConflictException>()).Which.Message.Should().Contain("system route");

        var same = await h.Service.UpdateAsync(packShip.Uuid, Rename(packShip, "STOCK"), User);
        same!.RouteCategory.Should().Be("STOCK");
    }

    [Fact]
    public async Task D_8_a_default_routes_category_is_locked()
    {
        var h = New();
        var route = await h.Service.CreateAsync(Route("OUR_SHIP", null, "PICK", "GOODS_ISSUE", "SHIP"), User);
        await h.Service.SetDefaultAsync(route.Uuid, User);

        var act = () => h.Service.UpdateAsync(route.Uuid, Rename(route, "MANUFACTURE"), User);
        (await act.Should().ThrowAsync<ConflictException>()).Which.Message
            .Should().Contain("default route for SHIP orders").And.Contain("category can't be changed");
    }

    [Theory]
    [InlineData("variants")]
    [InlineData("lines")]
    [InlineData("production")]
    public async Task T_C1_03_a_category_change_on_a_route_in_use_is_refused(string usedBy)
    {
        var h = New();
        var route = await h.Service.CreateAsync(Route("BUSY", null, "PICK", "GOODS_ISSUE", "SHIP"), User);
        var usage = usedBy switch { "variants" => h.Variants, "lines" => h.Lines, _ => h.ProductionOrders };
        usage.Counts[route.Uuid] = 2;

        var act = () => h.Service.UpdateAsync(route.Uuid, Rename(route, "MANUFACTURE"), User);
        var ex = (await act.Should().ThrowAsync<ConflictException>()).Which;
        ex.Message.Should().Contain("still used by 2").And.Contain("category can't be changed");

        (await h.Service.GetByUuidAsync(route.Uuid))!.RouteCategory.Should().Be("STOCK");

        // Renaming the busy route without touching its category still works.
        (await h.Service.UpdateAsync(route.Uuid, new UpdateFulfillmentRouteRequest { Name = "Busy!", DisplayOrder = 1 }, User))!
            .Name.Should().Be("Busy!");
    }

    // ── Defaults (D-6) ───────────────────────────────────────────────────────

    [Fact]
    public async Task D_6_a_manufacture_route_cannot_become_a_default()
    {
        var h = New();
        var route = await h.Service.CreateAsync(Route("MTO", "MANUFACTURE", "PICK", "GOODS_ISSUE", "SHIP"), User);

        var act = () => h.Service.SetDefaultAsync(route.Uuid, User);
        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Contain("can't be a default");
        (await h.Service.GetByUuidAsync(route.Uuid))!.IsDefault.Should().BeFalse();
    }

    // ── List filter (PA-05) ──────────────────────────────────────────────────

    [Fact]
    public async Task The_list_filters_by_category_case_insensitively_and_refuses_an_unknown_one()
    {
        var h = New();
        await new FulfillmentRouteSeeder(h.Db).EnsureSeededAsync(h.Tenant.OrganizationId);

        (await h.Service.GetListAsync(false, "manufacture")).Select(r => r.Code)
            .Should().Equal("MFG_PICK_SHIP", "MFG_PICK_PACK_SHIP");
        (await h.Service.GetListAsync(false, "STOCK")).Select(r => r.Code)
            .Should().Equal("PICK_ONLY", "PICK_AND_SHIP", "PICK_PACK_SHIP");
        (await h.Service.GetListAsync(false)).Should().HaveCount(5);

        var act = () => h.Service.GetListAsync(false, "NOPE");
        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Contain("Unknown route category");
    }

    // ── Seeds (D-9, T-C1-04) ─────────────────────────────────────────────────

    [Fact]
    public async Task T_C1_04_a_new_organization_gets_three_stock_and_two_manufacture_routes()
    {
        var h = New();
        (await new FulfillmentRouteSeeder(h.Db).EnsureSeededAsync(h.Tenant.OrganizationId)).Should().Be(5);

        var routes = (await h.Service.GetListAsync(true)).ToDictionary(r => r.Code);
        routes.Keys.Should().BeEquivalentTo(["PICK_ONLY", "PICK_AND_SHIP", "PICK_PACK_SHIP", "MFG_PICK_SHIP", "MFG_PICK_PACK_SHIP"]);
        routes.Values.Where(r => r.Code.StartsWith("PICK")).Should().OnlyContain(r => r.RouteCategory == "STOCK");

        var mfgShip = routes["MFG_PICK_SHIP"];
        mfgShip.Name.Should().Be("Manufacture → Pick & Ship");
        mfgShip.RouteCategory.Should().Be("MANUFACTURE");
        mfgShip.Steps.Select(s => s.StepCode).Should().Equal("PICK", "GOODS_ISSUE", "SHIP");
        mfgShip.DisplayOrder.Should().Be(40);

        var mfgPack = routes["MFG_PICK_PACK_SHIP"];
        mfgPack.Name.Should().Be("Manufacture → Pick, Pack & Ship");
        mfgPack.RouteCategory.Should().Be("MANUFACTURE");
        mfgPack.Steps.Select(s => s.StepCode).Should().Equal("PICK", "PACK", "GOODS_ISSUE", "SHIP");
        mfgPack.DisplayOrder.Should().Be(50);

        new[] { mfgShip, mfgPack }.Should().OnlyContain(r => r.IsSystem && r.IsActive && !r.IsDefault);
    }

    [Fact]
    public async Task The_backfill_adds_only_the_manufacture_seeds_to_an_org_seeded_by_A33_and_keeps_a_custom_namesake()
    {
        var h = New();
        var org = h.Tenant.OrganizationId;
        var seeder = new FulfillmentRouteSeeder(h.Db);
        await seeder.EnsureSeededAsync(org);

        // An A33-era org: drop the two MFG seeds, then own a custom STOCK route that happens to use one of the codes.
        h.Db.Set<FulfillmentRoute>().RemoveRange(
            await h.Db.Set<FulfillmentRoute>().IgnoreQueryFilters().Where(r => r.Code.StartsWith("MFG_")).ToListAsync());
        await h.Db.SaveChangesAsync();
        await h.Service.CreateAsync(Route("MFG_PICK_SHIP", null, "PICK", "GOODS_ISSUE"), User);

        (await seeder.EnsureSeededForAllAsync([org])).Should().Be(1, "only MFG_PICK_PACK_SHIP was missing");
        (await seeder.EnsureSeededForAllAsync([org])).Should().Be(0, "idempotent");

        var routes = (await h.Service.GetListAsync(true)).ToDictionary(r => r.Code);
        routes["MFG_PICK_SHIP"].RouteCategory.Should().Be("STOCK", "R-1: an existing route of that code is never touched");
        routes["MFG_PICK_SHIP"].IsSystem.Should().BeFalse();
        routes["MFG_PICK_PACK_SHIP"].RouteCategory.Should().Be("MANUFACTURE");
    }

    [Fact]
    public async Task A_new_org_without_a_shipping_default_still_never_gets_a_manufacture_default()
    {
        // Clear PICK_AND_SHIP's default, delete nothing, re-seed: no MFG seed may claim the free SHIP class.
        var h = New();
        var seeder = new FulfillmentRouteSeeder(h.Db);
        await seeder.EnsureSeededAsync(h.Tenant.OrganizationId);
        h.Db.Set<FulfillmentRoute>().RemoveRange(
            await h.Db.Set<FulfillmentRoute>().IgnoreQueryFilters().Where(r => r.Code.StartsWith("MFG_") || r.Code == "PICK_AND_SHIP").ToListAsync());
        await h.Db.SaveChangesAsync();

        await seeder.EnsureSeededAsync(h.Tenant.OrganizationId);

        var routes = (await h.Service.GetListAsync(true)).ToDictionary(r => r.Code);
        routes["PICK_AND_SHIP"].IsDefault.Should().BeTrue();
        routes["MFG_PICK_SHIP"].IsDefault.Should().BeFalse();
        routes["MFG_PICK_PACK_SHIP"].IsDefault.Should().BeFalse();
    }

    // ── Lookup carries the category (Demand's gate, Inventory's assignment) ──

    [Fact]
    public async Task The_lookup_summary_carries_the_category()
    {
        var h = New();
        await new FulfillmentRouteSeeder(h.Db).EnsureSeededAsync(h.Tenant.OrganizationId);
        var byCode = (await h.Service.GetListAsync(true)).ToDictionary(r => r.Code, r => r.Uuid);

        var lookup = new FulfillmentRouteLookup(h.Db);
        var found  = await lookup.GetAsync(h.Tenant.OrganizationId, [byCode["MFG_PICK_SHIP"], byCode["PICK_AND_SHIP"]]);

        found[byCode["MFG_PICK_SHIP"]].Category.Should().Be("MANUFACTURE");
        found[byCode["MFG_PICK_SHIP"]].IsManufacture.Should().BeTrue();
        found[byCode["PICK_AND_SHIP"]].Category.Should().Be("STOCK");

        (await lookup.ListActiveAsync(h.Tenant.OrganizationId)).Single(r => r.Code == "MFG_PICK_PACK_SHIP")
            .Category.Should().Be("MANUFACTURE");
        (await lookup.GetOrgDefaultsAsync(h.Tenant.OrganizationId)).Shipping!.Category.Should().Be("STOCK");
    }
}

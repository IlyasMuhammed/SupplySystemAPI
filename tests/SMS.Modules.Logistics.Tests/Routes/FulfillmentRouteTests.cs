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
/// A33 PA-09 — fulfillment routes (C1): BR-C1-01..09, T-C1-01..09, the per-class defaults (L-1), system routes (D-10),
/// seeding (D-6, PA-03), the cross-module lookup and tenant isolation (R-11, R-12). docs/fulfillment-routes/API-CONTRACT.md §3.
/// </summary>
public class FulfillmentRouteTests
{
    private const int User = 7;

    /// <summary>Stands in for Inventory's and Demand's usage counters (L-7).</summary>
    private sealed class FakeUsage(string description) : IFulfillmentRouteUsage
    {
        public readonly Dictionary<Guid, int> Counts = new();
        public Guid? AskedForOrg { get; private set; }

        public Task<FulfillmentRouteUsageCount> CountUsageAsync(Guid organizationId, Guid routeUuid, CancellationToken ct = default)
        {
            AskedForOrg = organizationId;
            return Task.FromResult(new FulfillmentRouteUsageCount(description, Counts.GetValueOrDefault(routeUuid)));
        }
    }

    private sealed record Harness(
        FulfillmentRouteService Service, LogisticsDbContext Db, StaticTenantContext Tenant, string DbName,
        FakeUsage Variants, FakeUsage Lines);

    private static Harness New(Guid? org = null, bool superAdmin = false)
    {
        var (db, tenant, dbName) = LogisticsTestDb.New(org, superAdmin);
        var variants = new FakeUsage("active product variants");
        var lines    = new FakeUsage("open sale order lines");
        return new Harness(new FulfillmentRouteService(db, tenant, [variants, lines]), db, tenant, dbName, variants, lines);
    }

    private static List<FulfillmentRouteStepRequest> Steps(params string[] codes) =>
        codes.Select((c, i) => new FulfillmentRouteStepRequest { StepCode = c, StepOrder = i + 1 }).ToList();

    private static CreateFulfillmentRouteRequest Route(string code, params string[] steps) =>
        new() { Code = code, Name = code + " route", Steps = Steps(steps) };

    // ── Create: BR-C1-03/04/05/09 (T-C1-01..05) ─────────────────────────────

    [Fact]
    public async Task T_C1_01_a_pick_and_goods_issue_route_needs_neither_packing_nor_shipping()
    {
        var h = New();
        var route = await h.Service.CreateAsync(Route("COLLECT", "PICK", "GOODS_ISSUE"), User);

        route.Code.Should().Be("COLLECT");
        route.RequiresPacking.Should().BeFalse();
        route.RequiresShipping.Should().BeFalse();
        route.IsSystem.Should().BeFalse();
        route.IsDefault.Should().BeFalse();
        route.IsActive.Should().BeTrue();
        route.Steps.Select(s => s.StepCode).Should().Equal("PICK", "GOODS_ISSUE");
        route.Steps.Select(s => s.StepOrder).Should().Equal(1, 2);
        route.StepsText.Should().Be("Pick → Goods Issue");
        route.StatusPath.Should().Equal("DRAFT", "RELEASED", "PICKING", "PICKED", "GOODS_ISSUED", "DELIVERED");

        var stored = await h.Db.Set<FulfillmentRoute>().IgnoreQueryFilters().Include(r => r.Steps).SingleAsync();
        stored.OrganizationId.Should().Be(h.Tenant.OrganizationId);
        stored.Steps.Should().OnlyContain(s => s.OrganizationId == h.Tenant.OrganizationId);
        stored.CreatedBy.Should().Be(User);
    }

    [Fact]
    public async Task T_C1_02_packing_and_shipping_are_derived_from_the_steps()
    {
        var h = New();
        var route = await h.Service.CreateAsync(Route("FULL", "PICK", "PACK", "GOODS_ISSUE", "SHIP"), User);

        route.RequiresPacking.Should().BeTrue();
        route.RequiresShipping.Should().BeTrue();
        route.StatusPath.Should().Equal("DRAFT", "RELEASED", "PICKING", "PICKED", "PACKED", "GOODS_ISSUED", "IN_TRANSIT", "DELIVERED");
    }

    [Fact]
    public async Task A_route_may_use_every_step_in_the_canonical_order()
    {
        var h = New();
        var route = await h.Service.CreateAsync(Route("HIGH_VALUE", "PICK", "PACK", "STAGE", "APPROVAL", "GOODS_ISSUE", "SHIP"), User);
        route.Steps.Should().HaveCount(6);
    }

    [Theory]
    [InlineData("PICK", new[] { "GOODS_ISSUE" })]                       // T-C1-03
    [InlineData("GOODS_ISSUE", new[] { "PICK", "SHIP" })]               // T-C1-04
    [InlineData("before SHIP", new[] { "PICK", "SHIP", "GOODS_ISSUE" })] // T-C1-05
    [InlineData("first", new[] { "PACK", "PICK", "GOODS_ISSUE" })]      // BR-C1-05
    [InlineData("order", new[] { "PICK", "STAGE", "PACK", "GOODS_ISSUE" })] // L-2
    [InlineData("more than once", new[] { "PICK", "PICK", "GOODS_ISSUE" })]
    [InlineData("Unknown", new[] { "PICK", "WRAP", "GOODS_ISSUE" })]
    public async Task Invalid_steps_are_refused(string messagePart, string[] steps)
    {
        var h = New();
        var act = () => h.Service.CreateAsync(Route("BAD", steps), User);
        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Contain(messagePart);
        (await h.Db.Set<FulfillmentRoute>().IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task BR_C1_04_step_order_must_run_from_one_with_no_gaps()
    {
        var h = New();
        var req = new CreateFulfillmentRouteRequest
        {
            Code = "GAP", Name = "Gap",
            Steps = [new() { StepCode = "PICK", StepOrder = 1 }, new() { StepCode = "GOODS_ISSUE", StepOrder = 3 }]
        };
        var act = () => h.Service.CreateAsync(req, User);
        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Contain("no gaps");
    }

    [Fact]
    public async Task Steps_are_taken_in_step_order_not_list_order()
    {
        var h = New();
        var req = new CreateFulfillmentRouteRequest
        {
            Code = "SORTED", Name = "Sorted",
            Steps = [new() { StepCode = "GOODS_ISSUE", StepOrder = 2 }, new() { StepCode = "PICK", StepOrder = 1 }]
        };
        var route = await h.Service.CreateAsync(req, User);
        route.Steps.Select(s => s.StepCode).Should().Equal("PICK", "GOODS_ISSUE");
    }

    [Fact]
    public async Task Pick_and_goods_issue_are_always_mandatory()
    {
        var h = New();
        var req = Route("OPT", "PICK", "PACK", "GOODS_ISSUE");
        req.Steps.ForEach(s => s.IsMandatory = false);

        var route = await h.Service.CreateAsync(req, User);

        route.Steps.Single(s => s.StepCode == "PICK").IsMandatory.Should().BeTrue();
        route.Steps.Single(s => s.StepCode == "GOODS_ISSUE").IsMandatory.Should().BeTrue();
        route.Steps.Single(s => s.StepCode == "PACK").IsMandatory.Should().BeFalse();
    }

    [Theory]
    [InlineData("  my_route ", "MY_ROUTE")]
    [InlineData("route1", "ROUTE1")]
    public async Task The_code_is_trimmed_and_upper_cased(string given, string stored)
    {
        var h = New();
        (await h.Service.CreateAsync(Route(given, "PICK", "GOODS_ISSUE"), User)).Code.Should().Be(stored);
    }

    [Theory]
    [InlineData("")]
    [InlineData("HAS SPACE")]
    [InlineData("DASH-ED")]
    [InlineData("THIRTY_ONE_CHARACTERS_LONG_CODE")]
    public async Task A_bad_code_is_refused(string code)
    {
        var h = New();
        var act = () => h.Service.CreateAsync(Route(code, "PICK", "GOODS_ISSUE"), User);
        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task A_route_needs_a_name()
    {
        var h = New();
        var req = Route("NONAME", "PICK", "GOODS_ISSUE");
        req.Name = "  ";
        var act = () => h.Service.CreateAsync(req, User);
        await act.Should().ThrowAsync<BadRequestException>();
    }

    // ── BR-C1-01 (T-C1-06) ──────────────────────────────────────────────────

    [Fact]
    public async Task T_C1_06_a_code_is_unique_within_the_organization_but_not_across_them()
    {
        var h = New();
        await h.Service.CreateAsync(Route("DUP", "PICK", "GOODS_ISSUE"), User);

        var again = () => h.Service.CreateAsync(Route("dup", "PICK", "GOODS_ISSUE", "SHIP"), User);
        (await again.Should().ThrowAsync<ConflictException>()).Which.Message.Should().Contain("DUP");

        var otherOrg = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var other = new FulfillmentRouteService(LogisticsTestDb.Open(h.DbName, otherOrg), otherOrg, []);
        (await other.CreateAsync(Route("DUP", "PICK", "GOODS_ISSUE"), User)).Code.Should().Be("DUP");
    }

    // ── Defaults: BR-C1-02, T-C1-07, L-1, L-5 ───────────────────────────────

    [Fact]
    public async Task T_C1_07_setting_a_default_clears_the_previous_default_of_the_same_class_only()
    {
        var h = New();
        var shipA   = await h.Service.CreateAsync(Route("SHIP_A", "PICK", "GOODS_ISSUE", "SHIP"), User);
        var shipB   = await h.Service.CreateAsync(Route("SHIP_B", "PICK", "PACK", "GOODS_ISSUE", "SHIP"), User);
        var collect = await h.Service.CreateAsync(Route("COLLECT", "PICK", "GOODS_ISSUE"), User);

        (await h.Service.SetDefaultAsync(shipA.Uuid, User))!.IsDefault.Should().BeTrue();
        await h.Service.SetDefaultAsync(collect.Uuid, User);
        (await h.Service.SetDefaultAsync(shipB.Uuid, User))!.IsDefault.Should().BeTrue();

        (await h.Service.GetByUuidAsync(shipA.Uuid))!.IsDefault.Should().BeFalse("only one SHIP default per organization");
        (await h.Service.GetByUuidAsync(collect.Uuid))!.IsDefault.Should().BeTrue("the pickup default is another class");
    }

    [Fact]
    public async Task Setting_the_current_default_again_changes_nothing()
    {
        var h = New();
        var route = await h.Service.CreateAsync(Route("SHIP_A", "PICK", "GOODS_ISSUE", "SHIP"), User);
        await h.Service.SetDefaultAsync(route.Uuid, User);
        (await h.Service.SetDefaultAsync(route.Uuid, User))!.IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task An_inactive_route_cannot_become_a_default()
    {
        var h = New();
        var route = await h.Service.CreateAsync(Route("OLD", "PICK", "GOODS_ISSUE", "SHIP"), User);
        await h.Service.SetActiveAsync(route.Uuid, false, User);

        var act = () => h.Service.SetDefaultAsync(route.Uuid, User);
        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Clearing_a_default_leaves_the_class_without_one()
    {
        var h = New();
        var route = await h.Service.CreateAsync(Route("SHIP_A", "PICK", "GOODS_ISSUE", "SHIP"), User);
        await h.Service.SetDefaultAsync(route.Uuid, User);

        (await h.Service.ClearDefaultAsync(route.Uuid, User))!.IsDefault.Should().BeFalse();
        (await h.Service.GetListAsync(false)).Should().NotContain(r => r.IsDefault);
    }

    [Fact]
    public async Task L_5_a_default_cannot_be_deactivated_or_deleted()
    {
        var h = New();
        var route = await h.Service.CreateAsync(Route("SHIP_A", "PICK", "GOODS_ISSUE", "SHIP"), User);
        await h.Service.SetDefaultAsync(route.Uuid, User);

        var deactivate = () => h.Service.SetActiveAsync(route.Uuid, false, User);
        (await deactivate.Should().ThrowAsync<ConflictException>()).Which.Message.Should().Contain("default");

        var delete = () => h.Service.DeleteAsync(route.Uuid, User);
        await delete.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task L_5_a_default_cannot_change_class_by_gaining_or_losing_ship()
    {
        var h = New();
        var route = await h.Service.CreateAsync(Route("SHIP_A", "PICK", "GOODS_ISSUE", "SHIP"), User);
        await h.Service.SetDefaultAsync(route.Uuid, User);

        var act = () => h.Service.UpdateAsync(route.Uuid,
            new UpdateFulfillmentRouteRequest { Name = "x", DisplayOrder = 1, Steps = Steps("PICK", "GOODS_ISSUE") }, User);
        await act.Should().ThrowAsync<ConflictException>();
    }

    // ── Deactivate: BR-C1-07 (T-C1-08) ──────────────────────────────────────

    [Fact]
    public async Task T_C1_08_a_route_in_use_cannot_be_deactivated_and_the_refusal_says_by_what()
    {
        var h = New();
        var route = await h.Service.CreateAsync(Route("USED", "PICK", "GOODS_ISSUE", "SHIP"), User);
        h.Variants.Counts[route.Uuid] = 3;
        h.Lines.Counts[route.Uuid] = 2;

        var act = () => h.Service.SetActiveAsync(route.Uuid, false, User);
        var message = (await act.Should().ThrowAsync<ConflictException>()).Which.Message;
        message.Should().Contain("3 active product variants").And.Contain("2 open sale order lines");

        h.Variants.AskedForOrg.Should().Be(h.Tenant.OrganizationId);
        (await h.Service.GetByUuidAsync(route.Uuid))!.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task A_route_nothing_uses_deactivates_and_reactivates()
    {
        var h = New();
        var route = await h.Service.CreateAsync(Route("FREE", "PICK", "GOODS_ISSUE"), User);

        (await h.Service.SetActiveAsync(route.Uuid, false, User))!.IsActive.Should().BeFalse();
        (await h.Service.GetListAsync(false)).Should().BeEmpty();
        (await h.Service.GetListAsync(true)).Should().ContainSingle();
        (await h.Service.SetActiveAsync(route.Uuid, true, User))!.IsActive.Should().BeTrue();
    }

    // ── Delete: BR-C1-06, L-6 (T-C1-09) ─────────────────────────────────────

    [Fact]
    public async Task T_C1_09_a_system_route_cannot_be_deleted()
    {
        var h = New();
        await new FulfillmentRouteSeeder(h.Db).EnsureSeededAsync(h.Tenant.OrganizationId);
        var pickPackShip = (await h.Service.GetListAsync(true)).Single(r => r.Code == "PICK_PACK_SHIP");

        var act = () => h.Service.DeleteAsync(pickPackShip.Uuid, User);
        (await act.Should().ThrowAsync<ConflictException>()).Which.Message.Should().Contain("System routes");
    }

    [Fact]
    public async Task An_unused_custom_route_is_deleted_with_its_steps()
    {
        var h = New();
        var route = await h.Service.CreateAsync(Route("TEMP", "PICK", "GOODS_ISSUE"), User);

        (await h.Service.DeleteAsync(route.Uuid, User)).Should().BeTrue();
        (await h.Db.Set<FulfillmentRoute>().IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await h.Db.Set<FulfillmentRouteStep>().IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_custom_route_in_use_or_on_a_delivery_cannot_be_deleted()
    {
        var h = New();
        var used = await h.Service.CreateAsync(Route("USED", "PICK", "GOODS_ISSUE"), User);
        h.Lines.Counts[used.Uuid] = 1;
        var delete = () => h.Service.DeleteAsync(used.Uuid, User);
        await delete.Should().ThrowAsync<ConflictException>();

        var shipped = await h.Service.CreateAsync(Route("SHIPPED", "PICK", "GOODS_ISSUE", "SHIP"), User);
        h.Db.DeliveryOrders.Add(new DeliveryOrder
        {
            UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), DeliveryNumber = "DLV-2026-00001",
            Direction = "OUTBOUND", SourceType = "SALE_ORDER", Status = "DRAFT",
            FulfillmentRouteUuid = shipped.Uuid, FulfillmentRouteCode = shipped.Code,
            RouteSteps = "PICK,GOODS_ISSUE,SHIP",
            CreatedBy = User, CreatedDate = DateTime.UtcNow
        });
        await h.Db.SaveChangesAsync();

        var deleteShipped = () => h.Service.DeleteAsync(shipped.Uuid, User);
        (await deleteShipped.Should().ThrowAsync<ConflictException>()).Which.Message.Should().Contain("Deactivate");
    }

    // ── Update: D-10, BR-C1-09 ──────────────────────────────────────────────

    [Fact]
    public async Task A_custom_routes_steps_can_change_and_its_flags_follow()
    {
        var h = New();
        var route = await h.Service.CreateAsync(Route("GROW", "PICK", "GOODS_ISSUE"), User);

        var updated = await h.Service.UpdateAsync(route.Uuid, new UpdateFulfillmentRouteRequest
        {
            Name = "Grown", Description = "now ships", DisplayOrder = 50,
            Steps = Steps("PICK", "PACK", "GOODS_ISSUE", "SHIP")
        }, User);

        updated!.Name.Should().Be("Grown");
        updated.Code.Should().Be("GROW");
        updated.RequiresPacking.Should().BeTrue();
        updated.RequiresShipping.Should().BeTrue();
        updated.Steps.Select(s => s.StepCode).Should().Equal("PICK", "PACK", "GOODS_ISSUE", "SHIP");
        (await h.Db.Set<FulfillmentRouteStep>().IgnoreQueryFilters().CountAsync()).Should().Be(4, "the old steps are replaced, not kept");
    }

    [Fact]
    public async Task Leaving_the_steps_out_of_an_update_keeps_them()
    {
        var h = New();
        var route = await h.Service.CreateAsync(Route("KEEP", "PICK", "PACK", "GOODS_ISSUE"), User);

        var updated = await h.Service.UpdateAsync(route.Uuid,
            new UpdateFulfillmentRouteRequest { Name = "Kept", DisplayOrder = 5, Steps = null }, User);

        updated!.Steps.Select(s => s.StepCode).Should().Equal("PICK", "PACK", "GOODS_ISSUE");
    }

    [Fact]
    public async Task D_10_a_system_route_keeps_its_steps_but_its_name_description_and_order_can_change()
    {
        var h = New();
        await new FulfillmentRouteSeeder(h.Db).EnsureSeededAsync(h.Tenant.OrganizationId);
        var seed = (await h.Service.GetListAsync(true)).Single(r => r.Code == "PICK_ONLY");

        var renamed = await h.Service.UpdateAsync(seed.Uuid, new UpdateFulfillmentRouteRequest
        {
            Name = "Customer collects", Description = "At the gate", DisplayOrder = 99,
            Steps = Steps("PICK", "GOODS_ISSUE")   // the same steps are fine
        }, User);
        renamed!.Name.Should().Be("Customer collects");
        renamed.DisplayOrder.Should().Be(99);

        var act = () => h.Service.UpdateAsync(seed.Uuid, new UpdateFulfillmentRouteRequest
        {
            Name = "x", DisplayOrder = 1, Steps = Steps("PICK", "PACK", "GOODS_ISSUE")
        }, User);
        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Contain("system route");
    }

    // ── Tenancy: R-11 ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Another_organizations_route_is_not_found_even_for_a_super_admin(bool superAdmin)
    {
        var owner = New();
        var route = await owner.Service.CreateAsync(Route("MINE", "PICK", "GOODS_ISSUE"), User);

        var stranger = new StaticTenantContext { OrganizationId = Guid.NewGuid(), IsSuperAdmin = superAdmin };
        var svc = new FulfillmentRouteService(LogisticsTestDb.Open(owner.DbName, stranger), stranger, []);

        (await svc.GetByUuidAsync(route.Uuid)).Should().BeNull();
        (await svc.GetListAsync(true)).Should().BeEmpty();
        (await svc.UpdateAsync(route.Uuid, new UpdateFulfillmentRouteRequest { Name = "x", DisplayOrder = 1 }, User)).Should().BeNull();
        (await svc.SetActiveAsync(route.Uuid, false, User)).Should().BeNull();
        (await svc.SetDefaultAsync(route.Uuid, User)).Should().BeNull();
        (await svc.ClearDefaultAsync(route.Uuid, User)).Should().BeNull();
        (await svc.DeleteAsync(route.Uuid, User)).Should().BeFalse();

        (await owner.Service.GetByUuidAsync(route.Uuid))!.Name.Should().Be("MINE route");
    }

    [Fact]
    public async Task The_list_is_by_display_order_then_code()
    {
        var h = New();
        await h.Service.CreateAsync(new CreateFulfillmentRouteRequest { Code = "B", Name = "B", DisplayOrder = 10, Steps = Steps("PICK", "GOODS_ISSUE") }, User);
        await h.Service.CreateAsync(new CreateFulfillmentRouteRequest { Code = "A", Name = "A", DisplayOrder = 10, Steps = Steps("PICK", "GOODS_ISSUE") }, User);
        await h.Service.CreateAsync(new CreateFulfillmentRouteRequest { Code = "C", Name = "C", DisplayOrder = 5, Steps = Steps("PICK", "GOODS_ISSUE") }, User);
        var last = await h.Service.CreateAsync(Route("D", "PICK", "GOODS_ISSUE"), User);

        last.DisplayOrder.Should().Be(20, "an omitted display order goes last + 10");
        (await h.Service.GetListAsync(false)).Select(r => r.Code).Should().Equal("C", "A", "B", "D");
    }

    // ── Seeding: PA-03, D-6, L-1, R-3, R-13 ─────────────────────────────────

    [Fact]
    public async Task Seeding_adds_the_three_system_routes_with_a_default_for_each_class()
    {
        var h = New();
        (await new FulfillmentRouteSeeder(h.Db).EnsureSeededAsync(h.Tenant.OrganizationId)).Should().Be(3);

        var routes = await h.Service.GetListAsync(true);
        routes.Select(r => r.Code).Should().Equal("PICK_ONLY", "PICK_AND_SHIP", "PICK_PACK_SHIP");
        routes.Should().OnlyContain(r => r.IsSystem && r.IsActive);

        var only = routes[0]; var andShip = routes[1]; var packShip = routes[2];
        only.Steps.Select(s => s.StepCode).Should().Equal("PICK", "GOODS_ISSUE");
        andShip.Steps.Select(s => s.StepCode).Should().Equal("PICK", "GOODS_ISSUE", "SHIP");
        packShip.Steps.Select(s => s.StepCode).Should().Equal("PICK", "PACK", "GOODS_ISSUE", "SHIP");
        packShip.RequiresPacking.Should().BeTrue();

        andShip.IsDefault.Should().BeTrue("D-6: PICK_AND_SHIP is the default for SHIP orders");
        only.IsDefault.Should().BeTrue("L-1: PICK_ONLY is the default for SELF_PICKUP orders");
        packShip.IsDefault.Should().BeFalse();
    }

    [Fact]
    public async Task Seeding_twice_adds_nothing_and_never_touches_an_edited_or_deactivated_seed()
    {
        var h = New();
        var seeder = new FulfillmentRouteSeeder(h.Db);
        await seeder.EnsureSeededAsync(h.Tenant.OrganizationId);

        var routes = await h.Service.GetListAsync(true);
        await h.Service.UpdateAsync(routes.Single(r => r.Code == "PICK_PACK_SHIP").Uuid,
            new UpdateFulfillmentRouteRequest { Name = "Boxed", DisplayOrder = 7 }, User);
        await h.Service.SetActiveAsync(routes.Single(r => r.Code == "PICK_PACK_SHIP").Uuid, false, User);
        await h.Service.ClearDefaultAsync(routes.Single(r => r.Code == "PICK_AND_SHIP").Uuid, User);

        (await seeder.EnsureSeededAsync(h.Tenant.OrganizationId)).Should().Be(0);

        var after = await h.Service.GetListAsync(true);
        after.Should().HaveCount(3);
        var boxed = after.Single(r => r.Code == "PICK_PACK_SHIP");
        boxed.Name.Should().Be("Boxed");
        boxed.IsActive.Should().BeFalse();
        after.Single(r => r.Code == "PICK_AND_SHIP").IsDefault.Should().BeFalse("a cleared default stays cleared (D-6)");
    }

    [Fact]
    public async Task A_seed_does_not_take_the_default_from_a_route_the_organization_already_chose()
    {
        var h = New();
        var mine = await h.Service.CreateAsync(Route("OUR_SHIP", "PICK", "GOODS_ISSUE", "SHIP"), User);
        await h.Service.SetDefaultAsync(mine.Uuid, User);

        await new FulfillmentRouteSeeder(h.Db).EnsureSeededAsync(h.Tenant.OrganizationId);

        var routes = await h.Service.GetListAsync(true);
        routes.Single(r => r.Code == "OUR_SHIP").IsDefault.Should().BeTrue();
        routes.Single(r => r.Code == "PICK_AND_SHIP").IsDefault.Should().BeFalse();
        routes.Single(r => r.Code == "PICK_ONLY").IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task The_backfill_seeds_every_organization_it_is_given_and_stamps_each_one()
    {
        // The caller is a super admin of none of them (R-13): rows carry the organization passed in.
        var (db, _, _) = LogisticsTestDb.New(Guid.NewGuid(), isSuperAdmin: true);
        var orgA = Guid.NewGuid(); var orgB = Guid.NewGuid();

        (await new FulfillmentRouteSeeder(db).EnsureSeededForAllAsync([orgA, orgB, orgA, Guid.Empty])).Should().Be(6);

        var rows = await db.Set<FulfillmentRoute>().IgnoreQueryFilters().Include(r => r.Steps).ToListAsync();
        rows.Count(r => r.OrganizationId == orgA).Should().Be(3);
        rows.Count(r => r.OrganizationId == orgB).Should().Be(3);
        rows.SelectMany(r => r.Steps).Should().OnlyContain(s => s.OrganizationId == orgA || s.OrganizationId == orgB);
        rows.Where(r => r.OrganizationId == orgA).SelectMany(r => r.Steps)
            .Should().OnlyContain(s => s.OrganizationId == orgA);
    }

    [Fact]
    public async Task The_provisioning_handler_seeds_the_new_organization()
    {
        var (db, _, _) = LogisticsTestDb.New(Guid.NewGuid(), isSuperAdmin: true);
        var org = Guid.NewGuid();

        await new FulfillmentRouteProvisioningHandler(new FulfillmentRouteSeeder(db)).OnOrganizationProvisionedAsync(org);

        (await db.Set<FulfillmentRoute>().IgnoreQueryFilters().CountAsync(r => r.OrganizationId == org)).Should().Be(3);
    }

    // ── IFulfillmentRouteLookup: R-12 ───────────────────────────────────────

    [Fact]
    public async Task The_lookup_reads_only_the_organization_it_is_given()
    {
        var h = New();
        await new FulfillmentRouteSeeder(h.Db).EnsureSeededAsync(h.Tenant.OrganizationId);
        var otherOrg = Guid.NewGuid();
        await new FulfillmentRouteSeeder(h.Db).EnsureSeededAsync(otherOrg);
        var mine   = (await h.Service.GetListAsync(true)).ToDictionary(r => r.Code, r => r.Uuid);
        var theirs = await h.Db.Set<FulfillmentRoute>().IgnoreQueryFilters()
            .Where(r => r.OrganizationId == otherOrg).Select(r => r.UUID).ToListAsync();

        var inactive = mine["PICK_PACK_SHIP"];
        await h.Service.SetActiveAsync(inactive, false, User);

        // Asked by a context bound to yet another org, as a Hangfire job or a super admin would be.
        var stranger = new StaticTenantContext { OrganizationId = Guid.NewGuid(), IsSuperAdmin = true };
        var lookup = new FulfillmentRouteLookup(LogisticsTestDb.Open(h.DbName, stranger));

        var found = await lookup.GetAsync(h.Tenant.OrganizationId, [.. mine.Values, .. theirs, Guid.NewGuid()]);
        found.Keys.Should().BeEquivalentTo(mine.Values, "another organization's routes and unknown uuids read as absent");
        found[inactive].IsActive.Should().BeFalse("an inactive route is still named");
        found[mine["PICK_PACK_SHIP"]].Steps.Should().Equal("PICK", "PACK", "GOODS_ISSUE", "SHIP");
        found[mine["PICK_PACK_SHIP"]].StepsText.Should().Be("Pick → Pack → Goods Issue → Ship");

        var defaults = await lookup.GetOrgDefaultsAsync(h.Tenant.OrganizationId);
        defaults.Shipping!.Code.Should().Be("PICK_AND_SHIP");
        defaults.NonShipping!.Code.Should().Be("PICK_ONLY");
        defaults.For("SHIP")!.Code.Should().Be("PICK_AND_SHIP");
        defaults.For("SELF_PICKUP")!.Code.Should().Be("PICK_ONLY");

        (await lookup.ListActiveAsync(h.Tenant.OrganizationId)).Select(r => r.Code)
            .Should().Equal("PICK_ONLY", "PICK_AND_SHIP");

        (await lookup.GetOrgDefaultsAsync(Guid.NewGuid())).Should().Be(new FulfillmentRouteDefaults(null, null));
    }

    // ── Vocabulary ──────────────────────────────────────────────────────────

    [Fact]
    public void The_step_enum_persists_as_the_shared_step_codes_in_canonical_order()
    {
        LogisticsCode.Codes<FulfillmentStep>().Should().Equal(FulfillmentStepCode.All);
        FulfillmentStepCode.Parse(FulfillmentStepCode.Format(["PICK", "GOODS_ISSUE", "SHIP"]))
            .Should().Equal("PICK", "GOODS_ISSUE", "SHIP");
        FulfillmentStepCode.Parse(null).Should().BeEmpty("a delivery without a route takes the legacy full path");
    }
}

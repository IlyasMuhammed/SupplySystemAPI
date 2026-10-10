using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.FulfillmentRoutes;
using SMS.Integration.Tests.RouteClassification;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using SMS.Integration.Tests.ServiceOrders;
using Xunit;
using static SMS.Integration.Tests.FulfillmentRoutes.Routes;
using static SMS.Integration.Tests.ServiceOrders.ServiceKit;

namespace SMS.Integration.Tests.ModuleRegistry;

/// <summary>
/// A37 (QA) — the module registry end to end on the real host (LocalDB), each test in organizations of its own created through the
/// platform admin's create path, driven by their admins (docs/module-registry/*.md). Spec codes are mapped by D-2
/// (PRODUCTION → MODULE_MANUFACTURING, PROCUREMENT → MODULE_DEMAND …) and spec jobs by R-3 (the real Manufacturing sweep).
/// <list type="bullet">
/// <item>TS-037-01/02/03/28/30, D-4/D-5/D-40/D-41/D-42 — <see cref="Org_admin_module_switches_follow_the_registry_rules"/>.</item>
/// <item>TS-037-04/05/06/07/08/25/33 — <see cref="Manufacturing_switched_off_with_work_in_progress_has_a_grace_period_then_turns_read_only"/>.</item>
/// <item>TS-037-09/10/11/29/31, MOD-08 — <see cref="Bom_management_is_shared_by_manufacturing_and_services"/>.</item>
/// <item>§1.3 sub-features, core feature — <see cref="A_switched_off_sub_feature_refuses_its_own_endpoints_only"/>.</item>
/// <item>TS-037-15/16/17 — <see cref="Product_module_blocks_follow_the_modules_and_the_data_stays"/>.</item>
/// <item>TS-037-18/19, RTE-01..03 — <see cref="Make_to_order_routes_fall_back_to_stock_while_manufacturing_is_off"/>.</item>
/// <item>TS-037-20/21/22/23, CUST-01..04 — <see cref="The_customer_master_protects_the_walk_in_numbers_and_syncs_customers"/>.</item>
/// <item>TS-037-23 (catalog) — <see cref="Catalog_sync_returns_only_rows_changed_strictly_after_since"/>.</item>
/// <item>TS-037-26/27 — <see cref="Permissions_and_workflow_interfaces_carry_their_module_and_whether_it_is_on"/>.</item>
/// <item>TS-037-12/32 — <see cref="A_gated_job_leaves_an_organization_without_its_module_pending"/>.</item>
/// <item>TS-037-34/35 — <see cref="Edition_flows_licence_modules_and_bom_management_follows"/>.</item>
/// <item>Backward compatibility — <see cref="A_standard_plan_organization_still_uses_every_pre_A37_screen"/>.</item>
/// </list>
/// <para>Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~ModuleRegistryE2ETests</c>.</para>
/// </summary>
public sealed class ModuleRegistryE2ETests : IClassFixture<SapWebApplicationFactory>
{
    // Class members win over the SMS.Integration.Tests.Manufacturing namespace and the Warehouse record in simple-name lookup.
    private const string Manufacturing = Mods.Manufacturing;
    private const string Services      = Mods.Services;
    private const string Inventory     = Mods.Inventory;
    private const string Logistics     = Mods.Logistics;
    private const string Warehouse     = Mods.Warehouse;
    private const string Demand        = Mods.Demand;
    private const string Suppliers     = Mods.Suppliers;
    private const string Customers     = Mods.Customers;
    private const string Finance       = Mods.Finance;
    private const string Bom           = Mods.Bom;

    private static object Customer(string name, string type = "COMPANY", string? phone = null, decimal? creditLimit = null) =>
        Mods.Customer(name, type, phone, creditLimit);

    private readonly SapKit _root;
    private readonly SapWebApplicationFactory _f;

    public ModuleRegistryE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _root = new SapKit(factory, "A37");
    }

    // ── TS-01/02/03/28/30, D-4 ───────────────────────────────────────────────────

    [Fact]
    public async Task Org_admin_module_switches_follow_the_registry_rules()
    {
        var o = await _root.OrgAsync("REG", currency: false);
        var k = o.K;

        // TS-01: the cards
        var cards = await k.ModulesAsync();
        cards.Select(c => c.S("code")).Should().NotContain("MODULE_PROCUREMENT", "the empty placeholder is hidden (D-36)");
        cards.Single(c => c.S("code") == "MODULE_POS").S("status").Should().Be("COMING_SOON");
        cards.Single(c => c.S("code") == Inventory).S("status").Should().Be("ALWAYS_ON");
        cards.Single(c => c.S("code") == Logistics).S("status").Should().Be("ACTIVE");
        var ranks = cards.Select(c => c.S("status") switch { "ALWAYS_ON" => 0, "ACTIVE" => 1, "GRACE" => 2, "DISABLED" => 3, "NOT_LICENSED" => 4, _ => 5 }).ToList();
        ranks.Should().BeInAscendingOrder("always-on, active, available, coming soon");
        var logistics = cards.Single(c => c.S("code") == Logistics);
        logistics.A("dependsOn").Select(d => d.S("code")).Should().Equal(Warehouse);
        cards.Single(c => c.S("code") == Warehouse).A("dependents").Select(d => d.S("code")).Should().Contain(Logistics);

        // always on (MOD-01), coming soon (TS-28)
        (await k.Disable(Inventory)).ShouldFail(HttpStatusCode.BadRequest, "MOD-01", "Inventory is always on.");
        (await k.Disable(Customers)).ShouldFail(HttpStatusCode.BadRequest, "MOD-01", "Customers is always on.");
        (await k.Disable("MODULE_MASTER_DATA")).ShouldFail(HttpStatusCode.BadRequest, "core", "Master Data is always on.");
        (await k.Enable("MODULE_POS")).ShouldFail(HttpStatusCode.BadRequest, "TS-28", "Point of Sale is not yet available.");
        (await k.Enable("MODULE_PROCUREMENT")).Status.Should().Be(HttpStatusCode.NotFound, "a hidden module is not offered");

        // TS-03: disabling a module others depend on
        (await k.Disable(Warehouse, 0)).ShouldFail(HttpStatusCode.BadRequest, "TS-03", "Disable Logistics first.");
        (await k.Disable(Suppliers, 0)).ShouldFail(HttpStatusCode.BadRequest, "TS-03", "Disable Demand & Procurement first.");
        (await k.Disable(Logistics, 400)).ShouldFail(HttpStatusCode.BadRequest, "grace bounds", "Grace period must be between 0 and 365 days.");

        var off = await k.DisableOkAsync(Logistics, 0);
        (off.S("status"), off.B("isEnabled"), off.IsNull("graceEndsAt")).Should().Be(("DISABLED", false, true), "grace 0 = off at once");
        await k.DisableOkAsync(Warehouse, 0);

        // TS-02: enabling a module whose dependency is off
        (await k.Enable(Logistics)).ShouldFail(HttpStatusCode.BadRequest, "TS-02", "Enable Warehouse first.");
        await k.EnableOkAsync(Warehouse);
        var on = await k.EnableOkAsync(Logistics);
        (on.S("status"), on.B("isEnabled")).Should().Be(("ACTIVE", true));
        await k.EnableOkAsync(Logistics);   // idempotent: no history
        (await k.HistoryAsync(Logistics)).Where(h => h.IsNull("featureCode")).Select(h => h.S("action"))
            .Should().Equal(new[] { "ENABLED", "DISABLED" }, "newest first, one row per real change (D-40)");

        // feature rules
        (await k.SetFeature(Inventory, "FEATURE_PRODUCT_VARIANTS", false)).ShouldFail(HttpStatusCode.BadRequest, "core feature", "Core features cannot be switched off.");
        (await k.SetFeature(Demand, "FEATURE_BLANKET_ORDERS", true)).ShouldFail(HttpStatusCode.BadRequest, "coming soon feature", "Blanket Orders is not yet available.");
        await k.FeatureOkAsync(Logistics, "FEATURE_PICK_LISTS", false);
        await k.DisableOkAsync(Logistics, 0);
        (await k.SetFeature(Logistics, "FEATURE_PICK_LISTS", true)).ShouldFail(HttpStatusCode.BadRequest, "module off", "Switch on Logistics first.");
        await k.EnableOkAsync(Logistics);
        await k.FeatureOkAsync(Logistics, "FEATURE_PICK_LISTS", true);
        (await k.HistoryAsync(Logistics)).Where(h => h.S("featureCode") == "FEATURE_PICK_LISTS").Select(h => h.S("action"))
            .Should().Equal("FEATURE_ENABLED", "FEATURE_DISABLED");

        // TS-30: a stale rowVersion
        var svc = await k.CardAsync(Services);
        var stale = svc.S("rowVersion");
        stale.Should().NotBeNullOrEmpty();
        var disabled = await k.Ok(k.Disable(Services, 0, rowVersion: stale), "disable with the current rowVersion");
        disabled.S("rowVersion").Should().NotBe(stale);
        (await k.Enable(Services, stale)).ShouldFail(HttpStatusCode.Conflict, "TS-30: the card changed since it was read");
        (await k.CardAsync(Services)).S("status").Should().Be("DISABLED", "a 409 changes nothing");
        await k.Ok(k.Enable(Services, disabled.S("rowVersion")), "enable with the fresh rowVersion");
        // a feature change moves the module's rowVersion (D-41)
        var cust = (await k.CardAsync(Customers)).S("rowVersion");
        await k.Ok(k.SetFeature(Customers, "FEATURE_CREDIT_MANAGEMENT", false, cust), "credit off");
        (await k.SetFeature(Customers, "FEATURE_CREDIT_MANAGEMENT", true, cust)).ShouldFail(HttpStatusCode.Conflict, "D-41");

        // MODULES_VIEW alone can look, not switch
        var viewer = await k.LoginWithPermissionsAsync("modview", "MODULES_VIEW");
        (await k.Get("/api/tenant/modules", viewer)).Status.Should().Be(HttpStatusCode.OK);
        (await k.Post($"/api/tenant/modules/{Services}/disable", new { graceDays = 0 }, viewer)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await k.Get("/api/tenant/modules/enabled", await k.LoginWithPermissionsAsync("nobody"))).Status.Should().Be(HttpStatusCode.OK, "any signed-in user");

        // D-4: the super admin's licence
        await _root.LicenceAsync(o.OrgId, (Services, false));
        var unlicensed = await k.CardAsync(Services);
        (unlicensed.S("status"), unlicensed.B("isLicensed"), unlicensed.B("isEnabled")).Should().Be(("NOT_LICENSED", false, false));
        (await k.Enable(Services)).ShouldFail(HttpStatusCode.BadRequest, "D-4",
            "Service Orders is not included in your plan — contact your administrator.");
        var superRows = (await _root.Ok(_root.Get($"/api/system/organizations/{o.OrgId}/features"), "super-admin rows")).Items();
        var row = superRows.Single(r => r.S("featureCode") == Services);
        (row.B("isLicensed"), row.S("status")).Should().Be((false, "NOT_LICENSED"));
        await _root.LicenceAsync(o.OrgId, (Services, true));
        (await k.CardAsync(Services)).S("status").Should().Be("ACTIVE");
        var superHistory = await _root.SuperHistoryAsync(o.OrgId);
        superHistory.Where(h => h.S("featureCode") == Services).Select(h => h.S("action")).Take(2)
            .Should().Equal(new[] { "LICENSED", "UNLICENSED" }, "§1.2: newest first, featureCode always set");
        superHistory.Should().OnlyContain(h => !h.IsNull("featureCode"));

        // D-39: a sub-feature endpoint while its module is in grace names the module and its grace end
        var grace = await k.Ok(k.Disable(Logistics), "Logistics off with the default grace");
        grace.S("status").Should().Be("GRACE");
        (await k.EnabledAsync()).Strings("features").Should().NotContain("FEATURE_PICK_LISTS");
        (await k.Post("/api/logistics/consignments", new { CarrierUuid = Guid.NewGuid(), Mode = "COURIER", DeliveryUuids = new[] { Guid.NewGuid() } }))
            .ShouldBeModuleRefusal(Logistics, inGrace: true, "D-39: FEATURE_SHIPMENT_TRACKING create while Logistics is in grace");
        (await k.Get("/api/logistics/pick-lists")).ShouldPassTheGate("reads pass during grace");
        // D-40: a dependent in grace does not block
        await k.DisableOkAsync(Warehouse, 0);
        // D-42: the super admin's "on" = licensed AND on — re-enables what the org admin switched off, clears grace, pulls its dependency
        await _root.LicenceAsync(o.OrgId, (Logistics, true));
        var relicensed = await k.CardAsync(Logistics);
        (relicensed.S("status"), relicensed.IsNull("graceEndsAt")).Should().Be(("ACTIVE", true));
        (await k.CardAsync(Warehouse)).S("status").Should().Be("ACTIVE", "the super-admin path switches the dependency on");

        // apply-plan-template resets the licence and logs it
        await k.Ok(k.Disable(Services), "Services off with grace");
        await _root.Ok(_root.Post($"/api/system/organizations/{o.OrgId}/apply-plan-template"), "apply plan template");
        var reset = await k.CardAsync(Services);
        (reset.S("status"), reset.IsNull("graceEndsAt")).Should().Be(("ACTIVE", true));
        (await k.HistoryAsync(Services)).First().S("action").Should().Be("LICENSED");
    }

    // ── TS-04..08, 25, 33 ────────────────────────────────────────────────────────

    [Fact]
    public async Task Manufacturing_switched_off_with_work_in_progress_has_a_grace_period_then_turns_read_only()
    {
        var o = await _root.OrgAsync("MFG");
        var k = o.K;
        var w = await k.MtoWorldAsync("MFG", rawStock: 40m, baseCurrency: o.Pkr);
        var po = (await k.Ok(k.TryCreateProductionOrder(w.Fg, w.Wh, 2m), "production order")).GetGuid();

        // TS-25: the impact check
        var impact = await k.Ok(k.Get($"/api/tenant/modules/{Manufacturing}/impact"), "impact");
        impact.A("dependents").Should().BeEmpty();
        impact.I("defaultGraceDays").Should().Be(30);
        impact.A("inProgress").Where(l => l.S("label")!.StartsWith("Production orders")).Sum(l => l.I("count"))
            .Should().BeGreaterThan(0, $"the planned order is in progress — {J.Short(impact)}");

        // TS-04: switched off with the default 30 days
        var card = await k.Ok(k.Disable(Manufacturing, null, "winding down"), "disable manufacturing");
        card.S("status").Should().Be("GRACE");
        var graceEnds = card.P("graceEndsAt").GetDateTime().ToUniversalTime();
        graceEnds.Should().BeCloseTo(DateTime.UtcNow.AddDays(30), TimeSpan.FromHours(1));
        var h = (await k.HistoryAsync(Manufacturing)).First();
        (h.S("action"), h.IsNull("featureCode"), h.I("graceDays"), h.S("notes")).Should().Be(("DISABLED", true, 30, "winding down"));
        h.S("performedByName").Should().NotBe("System");
        var enabled = await k.EnabledAsync();
        enabled.Strings("modules").Should().NotContain(Manufacturing);
        enabled.A("grace").Select(g => g.S("code")).Should().Equal(Manufacturing);
        enabled.Strings("features").Should().NotContain("FEATURE_QUALITY_INSPECTION", "a feature of a module in grace is not usable");
        enabled.Strings("features").Should().Contain(Bom, "Service Orders is still on (MOD-08)");

        // TS-05: no new production order
        (await k.TryCreateProductionOrder(w.Fg, w.Wh, 1m)).ShouldBeModuleRefusal(Manufacturing, inGrace: true, "TS-05: a create during grace");
        (await k.Get("/api/production-orders?pageSize=10")).Status.Should().Be(HttpStatusCode.OK, "reads pass during grace");
        // TS-06: the order already in progress moves on
        await k.IssueAndStartAsync(po);
        (await k.PoStatusAsync(po)).Should().Be("IN_PROGRESS", "TS-06: a transition on an existing record passes during grace");

        // TS-07: after the grace period — reads yes, writes no
        await _f.EndGraceNowAsync(o.OrgId, Manufacturing);
        (await k.Get($"/api/production-orders/{po}")).Status.Should().Be(HttpStatusCode.OK, "TS-07: the org once had it, so it still reads");
        (await k.Post($"/api/production-orders/{po}/report-output", new { Quantity = 2m }))
            .ShouldBeModuleRefusal(Manufacturing, inGrace: false, "TS-07: read-only after grace (even before the job runs)");
        (await k.TryCreateProductionOrder(w.Fg, w.Wh, 1m)).ShouldBeModuleRefusal(Manufacturing, inGrace: false, "TS-07");

        // TS-33: the daily job tidies the row and logs it
        await _f.RunGraceExpiryJobAsync();
        (await _f.GraceEndsAtInDbAsync(o.OrgId, Manufacturing)).Should().BeNull();
        var expired = (await k.HistoryAsync(Manufacturing)).First();
        (expired.S("action"), expired.S("performedByName")).Should().Be(("GRACE_EXPIRED", "System"));
        var after = await k.CardAsync(Manufacturing);
        (after.S("status"), after.IsNull("graceEndsAt"), after.IsNull("disabledAt")).Should().Be(("DISABLED", true, false));
        (await k.EnabledAsync()).A("grace").Should().BeEmpty();
        await _f.RunGraceExpiryJobAsync();
        (await k.HistoryAsync(Manufacturing)).Count(x => x.S("action") == "GRACE_EXPIRED").Should().Be(1, "the job runs once per expiry");

        // TS-08: switched back on — everything as before
        var back = await k.EnableOkAsync(Manufacturing);
        (back.S("status"), back.IsNull("disabledAt"), back.IsNull("graceEndsAt")).Should().Be(("ACTIVE", true, true), "MOD-10");
        (await k.HistoryAsync(Manufacturing)).First().S("action").Should().Be("ENABLED");
        (await k.EnabledAsync()).Strings("modules").Should().Contain(Manufacturing);
        await k.Ok(k.Post($"/api/production-orders/{po}/report-output", new { Quantity = 2m }), "TS-08: the in-progress order goes on");
        await k.Ok(k.TryCreateProductionOrder(w.Fg, w.Wh, 1m), "TS-08: new orders again");
    }

    // ── TS-09/10/11/29/31, MOD-08 ────────────────────────────────────────────────

    [Fact]
    public async Task Bom_management_is_shared_by_manufacturing_and_services()
    {
        var w = await _root.WorldAsync("BOM", rawStock: 10m, spareStock: 0m);
        var k = w.K;

        // TS-09/11/29: Manufacturing off, Service Orders on → BOMs fully usable for services
        await k.DisableOkAsync(Manufacturing, 0);
        var bomFeature = await k.FeatureOfAsync(Inventory, Bom);
        (bomFeature.B("isEnabled"), bomFeature.B("autoManaged")).Should().Be((true, true), "MOD-08: Services still drives it");
        (await k.Get("/api/boms")).Status.Should().Be(HttpStatusCode.OK);
        var bom = await w.StandardServiceBomAsync();
        var so = await w.CreateOrderAsync(w.Svc, 1m);
        (await w.DoAsync(so, "plan")).S("status").Should().Be("READY", "TS-29: a service order explodes its BOM with Manufacturing off");

        // TS-31: preferFor ordering (BOM-SHR-04) and the usage switch
        var drafts = new Dictionary<string, Guid>();
        foreach (var usage in new[] { "PRODUCTION_PREFERRED", "SERVICE_PREFERRED", "UNIVERSAL" })
        {
            var p = await k.ServiceProductAsync($"Usage {usage}", "PCS", 0m, 100m);
            await k.ConfigureServiceAsync(p);
            drafts[usage] = (await k.Ok(k.Post("/api/boms", new
            {
                ProductUuid = p.ProductUuid, BaseQuantity = 1m, BomUsage = usage, Lines = new[] { BomLine(w.Raw, 1m) }
            }), $"draft BOM {usage}")).GetGuid();
        }
        static int Rank(string? usage, string preferred) => usage == preferred ? 0 : usage == "UNIVERSAL" ? 1 : 2;
        foreach (var (prefer, preferred) in new[] { ("SERVICE", "SERVICE_PREFERRED"), ("PRODUCTION", "PRODUCTION_PREFERRED") })
        {
            var list = (await k.Ok(k.Get($"/api/boms?preferFor={prefer}&pageSize=50"), $"preferFor={prefer}")).A("data");
            list.Should().HaveCount(4, "nothing is hidden");
            list.Select(b => Rank(b.S("bomUsage"), preferred)).Should().BeInAscendingOrder($"preferFor={prefer}");
        }
        (await k.Get("/api/boms?preferFor=MRP")).Status.Should().Be(HttpStatusCode.BadRequest);
        await k.Ok(k.Put($"/api/boms/{bom}/usage", new { bomUsage = "SERVICE_PREFERRED" }), "usage on an ACTIVE BOM");
        (await k.Ok(k.Get($"/api/boms/{bom}"), "read")).S("bomUsage").Should().Be("SERVICE_PREFERRED");

        // TS-10: both off → BOM management switched off by the system; data and reads stay
        await k.DisableOkAsync(Services, 0);
        var autoOff = await k.FeatureOfAsync(Inventory, Bom);
        autoOff.B("isEnabled").Should().BeFalse("MOD-08: neither driver is on");
        var auto = (await k.HistoryAsync(Inventory)).First(x => x.S("featureCode") == Bom);
        (auto.S("action"), auto.S("performedByName")).Should().Be(("AUTO_DISABLED", "System"));
        (await k.Get("/api/boms")).Status.Should().Be(HttpStatusCode.OK, "BOM-SHR-03: reads need Inventory only");
        var kept = await k.Ok(k.Get($"/api/boms/{bom}"), "read the BOM");
        (kept.S("status"), kept.A("lines").Count).Should().Be(("ACTIVE", 3), "no data is touched");
        (await k.TryCreateServiceBom(w.Svc, BomLine(w.Raw, 1m))).ShouldBeModuleRefusal(Bom, inGrace: false, "BOM-SHR-02: a create");
        (await k.Put($"/api/boms/{bom}/usage", new { bomUsage = "UNIVERSAL" })).ShouldBeModuleRefusal(Bom, inGrace: false, "BOM-SHR-02: an edit");

        // a driver back on → auto-enabled again
        await k.EnableOkAsync(Services);
        var autoOn = await k.FeatureOfAsync(Inventory, Bom);
        (autoOn.B("isEnabled"), autoOn.B("autoManaged")).Should().Be((true, true));
        (await k.HistoryAsync(Inventory)).First(x => x.S("featureCode") == Bom).S("action").Should().Be("AUTO_ENABLED");

        // MOD-08: switched on by hand → the system leaves it alone
        await k.FeatureOkAsync(Inventory, Bom, false);
        await k.FeatureOkAsync(Inventory, Bom, true);
        (await k.FeatureOfAsync(Inventory, Bom)).B("autoManaged").Should().BeFalse();
        await k.DisableOkAsync(Services, 0);
        (await k.FeatureOfAsync(Inventory, Bom)).B("isEnabled").Should().BeTrue("an administrator's choice is kept");
        (await k.Put($"/api/boms/{bom}/usage", new { bomUsage = "UNIVERSAL" })).ShouldPassTheGate("BOM management is on by hand");
    }

    // ── §1.3 sub-features ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_switched_off_sub_feature_refuses_its_own_endpoints_only()
    {
        var o = await _root.OrgAsync("SUB");
        var k = o.K;
        var w = await k.MtoWorldAsync("SUB", rawStock: 20m, baseCurrency: o.Pkr);
        var po = (await k.Ok(k.TryCreateProductionOrder(w.Fg, w.Wh, 2m), "production order")).GetGuid();
        await k.IssueAndStartAsync(po);
        await k.ReportAndCompleteAsync(po, 2m);
        var inspector = await k.InspectorAsync();

        // Quality inspection off
        var card = await k.FeatureOkAsync(Manufacturing, "FEATURE_QUALITY_INSPECTION", false);
        card.A("features").Single(f => f.S("code") == "FEATURE_QUALITY_INSPECTION").B("isEnabled").Should().BeFalse();
        (await k.EnabledAsync()).Strings("features").Should().NotContain("FEATURE_QUALITY_INSPECTION");
        (await k.TryInspect(po, inspector, 2m, 0m)).ShouldBeModuleRefusal("FEATURE_QUALITY_INSPECTION", inGrace: false, "QI is off");
        (await k.Get($"/api/production-orders/{po}")).Status.Should().Be(HttpStatusCode.OK, "the rest of Manufacturing is untouched");
        await k.Ok(k.TryCreateProductionOrder(w.Fg, w.Wh, 1m), "production orders still open");

        // RFQ management off
        await k.FeatureOkAsync(Demand, "FEATURE_RFQ_MANAGEMENT", false);
        var rfq = new { Title = k.Next("RFQ"), SourceType = "STANDALONE", Lines = new[] { new { ItemDescription = "Bolts", Quantity = 10m } } };
        (await k.Post("/api/quotations", rfq)).ShouldBeModuleRefusal("FEATURE_RFQ_MANAGEMENT", inGrace: false, "RFQ is off");
        (await k.Get("/api/quotations")).Status.Should().Be(HttpStatusCode.OK, "D-6: the org once had it, so it still reads");
        await k.CreatePurchaseOrderAsync(w.Vendor, w.Wh, (w.Raw, 1m, 5m));   // the rest of Demand is untouched

        // both back on
        await k.FeatureOkAsync(Manufacturing, "FEATURE_QUALITY_INSPECTION", true);
        await k.FeatureOkAsync(Demand, "FEATURE_RFQ_MANAGEMENT", true);
        await k.Ok(k.TryInspect(po, inspector, 2m, 0m), "QI again");
        await k.Ok(k.Post("/api/quotations", rfq), "RFQ again");

        (await k.SetFeature(Inventory, "FEATURE_PRODUCT_VARIANTS", false)).ShouldFail(HttpStatusCode.BadRequest, "core", "Core features cannot be switched off.");
    }

    // ── TS-15/16/17 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Product_module_blocks_follow_the_modules_and_the_data_stays()
    {
        var w = await _root.WorldAsync("PRD", rawStock: 0m, spareStock: 0m);
        var k = w.K;
        await k.ConfigureServiceAsync(w.Svc);
        var whId = await k.WarehouseIdAsync(w.Wh);
        var fg = await k.FinishedGoodAsync("PRD FG", whId);

        // TS-15: both modules on → both blocks
        await k.Ok(k.PatchService(w.Svc, new { serviceCategory = "REPAIR", requiresSiteVisit = true }), "service category");
        (await k.PatchService(w.Raw, new { serviceCategory = "REPAIR" }))
            .ShouldFail(HttpStatusCode.BadRequest, "D-30", "Service category is only applicable to service products");
        var svc = await k.ProductDetailAsync(w.Svc);
        svc.B("isServiceable").Should().BeTrue();
        var ss = svc.P("serviceSettings");
        (ss.S("invoicingPolicy"), ss.S("serviceCategory"), ss.B("requiresSiteVisit"), ss.B("hasServiceBom")).Should().Be(("FIXED_PRICE", "REPAIR", true, true));
        var fgDetail = await k.ProductDetailAsync(fg);
        (fgDetail.P("productionSettings").S("supplyMethod"), fgDetail.P("productionSettings").I("defaultProductionWarehouseId")).Should().Be(("MANUFACTURE", whId));

        // TS-16: Service Orders off → the key is gone, the data is not
        await k.DisableOkAsync(Services, 0);
        svc = await k.ProductDetailAsync(w.Svc);
        svc.Has("serviceSettings").Should().BeFalse("PRD-CAP-03: key absent when the module is off");
        (svc.S("serviceInvoicingPolicy"), svc.S("serviceCategory"), svc.B("hasServiceBom")).Should().Be(("FIXED_PRICE", "REPAIR", true), "flat fields stay");
        (await k.ProductDetailAsync(fg)).Has("productionSettings").Should().BeTrue("Manufacturing is still on");

        // TS-17: Manufacturing off too
        await k.DisableOkAsync(Manufacturing, 0);
        fgDetail = await k.ProductDetailAsync(fg);
        fgDetail.Has("productionSettings").Should().BeFalse();
        (fgDetail.S("supplyMethod"), fgDetail.B("isManufacturable")).Should().Be(("MANUFACTURE", true), "PRD-CAP-04: nothing deleted");

        // back on → the same values
        await k.EnableOkAsync(Manufacturing);
        await k.EnableOkAsync(Services);
        svc = await k.ProductDetailAsync(w.Svc);
        (svc.P("serviceSettings").S("serviceCategory"), svc.P("serviceSettings").B("requiresSiteVisit")).Should().Be(("REPAIR", true));
        (await k.ProductDetailAsync(fg)).P("productionSettings").I("defaultProductionWarehouseId").Should().Be(whId);

        // grace counts as off (D-30)
        await k.Ok(k.Disable(Services), "disable with the default grace");
        (await k.ProductDetailAsync(w.Svc)).Has("serviceSettings").Should().BeFalse("a module in grace is not usable");
    }

    // ── TS-18/19, RTE-01..03 ─────────────────────────────────────────────────────

    [Fact]
    public async Task Make_to_order_routes_fall_back_to_stock_while_manufacturing_is_off()
    {
        var o = await _root.OrgAsync("RTE");
        var k = o.K;
        var w = await k.MtoWorldAsync("RTE", rawStock: 20m, baseCurrency: o.Pkr);
        await k.ProduceToStockAsync(w, 3m);
        await k.DisableOkAsync(Manufacturing, 0);

        // RTE-01: MANUFACTURE routes unavailable
        var routes = await k.RoutesAsync(includeInactive: true);
        var mfg = routes.Single(r => r.S("code") == w.RouteCode);
        (mfg.B("isAvailable"), mfg.S("unavailableReason")).Should().Be((false, "Manufacturing is switched off"));
        routes.Where(r => r.S("routeCategory") != "MANUFACTURE").Should().OnlyContain(r => r.B("isAvailable"));
        (await k.TryCreateCategoryRoute($"MX{Guid.NewGuid():N}"[..10].ToUpperInvariant(), "MTO", Rc.Manufacture, ["PICK", "GOODS_ISSUE", "SHIP"]))
            .ShouldFail(HttpStatusCode.BadRequest, "RTE-01", "Manufacturing is switched off for your organization.");
        (await k.AssignVariantRoute(w.Fg.VariantUuid, w.Route)).ShouldFail(HttpStatusCode.BadRequest, "D-35", "Manufacturing is switched off");

        // GET api/products/{id}/routes
        foreach (var url in new[] { $"/api/products/{w.Fg.ProductId}/routes", $"/api/products/{w.Fg.ProductUuid}/routes" })
        {
            var row = (await k.Ok(k.Get(url), url)).Items().Single();
            (row.G("variantUuid"), row.NG("routeUuid"), row.B("isAvailable")).Should().Be((w.Fg.VariantUuid, (Guid?)w.Route, false));
            row.NG("effectiveRouteUuid").Should().NotBeNull().And.NotBe(w.Route, "RTE-02: the SHIP default");
            row.S("warning").Should().Contain("is not available");
        }

        // RTE-02/03 on a sale order
        (await k.TryCreateOrderAsync(w.Customer, w.Pkr, "SHIP", w.Address, RLine(w.Fg, 1m, w.Route)))
            .ShouldFail(HttpStatusCode.BadRequest, "an override with an unavailable route",
                $"Line 1: '{w.RouteCode}' — Manufacturing is switched off for your organization.");
        var so = await k.CreateOrderAsync(w.Customer, w.Pkr, "SHIP", w.Address, RLine(w.Fg, 1m));
        var line = (await k.GetSaleOrderAsync(so)).LineOf(w.Fg);
        (line.S("routeSource"), line.S("effectiveRouteCategory")).Should().Be(("ORG_DEFAULT", "STOCK"), J.Short(line));
        line.S("routeWarning").Should().NotBeNullOrEmpty("RTE-03");
        line.NG("effectiveRouteUuid").Should().NotBe(w.Route);

        // TS-19: confirm snapshots the stock fallback and raises no production order
        var confirm = await k.ConfirmAsync(so);
        J.Short(confirm).Should().NotContain("deliveryCreationFailed\":true");
        (await k.ProductionOrdersOfAsync(so)).Should().BeEmpty("TS-19");
        (await k.SoDeliveriesAsync(so)).Should().ContainSingle("the line ships from stock");
        var confirmed = (await k.GetSaleOrderAsync(so)).LineOf(w.Fg);
        confirmed.NG("fulfillmentRouteUuid").Should().NotBe(w.Route, "the stock fallback is what confirm snapshots");

        // D-28: Logistics / Demand impact providers count the open work
        int Count(JsonElement impact, string label) => impact.A("inProgress").Where(l => l.S("label") == label).Sum(l => l.I("count"));
        var logImpact = await k.Ok(k.Get($"/api/tenant/modules/{Logistics}/impact"), "Logistics impact");
        Count(logImpact, "Open deliveries").Should().BeGreaterThan(0, J.Short(logImpact));
        var demImpact = await k.Ok(k.Get($"/api/tenant/modules/{Demand}/impact"), "Demand impact");
        Count(demImpact, "Open sale orders").Should().BeGreaterThan(0, J.Short(demImpact));
        (await k.Ok(k.Get($"/api/tenant/modules/{Warehouse}/impact"), "Warehouse impact")).A("dependents").Select(d => d.S("code")).Should().Contain(Logistics);

        // back on → the variant's MANUFACTURE route applies again
        await k.EnableOkAsync(Manufacturing);
        var so2 = await k.CreateOrderAsync(w.Customer, w.Pkr, "SHIP", w.Address, RLine(w.Fg, 1m));
        var line2 = (await k.GetSaleOrderAsync(so2)).LineOf(w.Fg);
        (line2.NG("effectiveRouteUuid"), line2.S("routeWarning")).Should().Be(((Guid?)w.Route, (string?)null));
    }

    // ── TS-20..23, CUST-01..04 ───────────────────────────────────────────────────

    [Fact]
    public async Task The_customer_master_protects_the_walk_in_numbers_and_syncs_customers()
    {
        var o = await _root.OrgAsync("CUS");
        var k = o.K;

        // TS-20 / CUST-01: the walk-in
        var first = (await k.Ok(k.Get("/api/customers/search"), "search")).Items();
        var walkIn = first.First();
        (walkIn.S("code"), walkIn.S("customerType"), walkIn.B("isSystem"), walkIn.B("isActive")).Should().Be(("C-WALKIN", "WALK_IN", true, true));
        var wu = walkIn.G("uuid");
        (await k.Patch($"/api/customers/{wu}/status", new { isActive = false })).ShouldFail(HttpStatusCode.BadRequest, "CUST-01", "The walk-in customer cannot be deactivated.");
        (await k.Put($"/api/customers/{wu}", Customer("Walk-in Customer", "COMPANY")))
            .ShouldFail(HttpStatusCode.BadRequest, "CUST-01", "The walk-in customer cannot be changed to another type.");
        (await k.Put($"/api/customers/{wu}", Customer("Walk-in Customer", "WALK_IN", creditLimit: 100m)))
            .ShouldFail(HttpStatusCode.BadRequest, "CUST-04", "Walk-in customers cannot have a credit limit.");
        (await k.Delete($"/api/partners/{wu}")).Status.Should().NotBe(HttpStatusCode.OK, "D-19: no other path deletes it");
        (await k.Ok(k.Get($"/api/customers/{wu}"), "walk-in")).B("isActive").Should().BeTrue();

        // TS-21 / CUST-02: codes
        (await k.Post("/api/customers", Customer(" "))).ShouldFail(HttpStatusCode.BadRequest, "name", "Name is required.");
        (await k.Post("/api/customers", Customer("Counter", "WALK_IN", creditLimit: 10m)))
            .ShouldFail(HttpStatusCode.BadRequest, "CUST-04", "Walk-in customers cannot have a credit limit.");
        var phone = $"0300{Random.Shared.Next(1000000, 9999999)}";
        var c1 = (await k.Ok(k.Post("/api/customers", Customer("Zed Individual", "INDIVIDUAL", phone)), "c1")).GetGuid();
        var c2 = (await k.Ok(k.Post("/api/customers", Customer($"{phone} Traders")), "c2")).GetGuid();
        var c3 = (await k.Ok(k.Post("/api/customers", Customer($"Shop {phone}")), "c3")).GetGuid();
        var d1 = await k.Ok(k.Get($"/api/customers/{c1}"), "c1");
        (d1.S("code"), d1.S("customerType"), d1.B("isVendor"), d1.B("isActive")).Should().Be(("C-00001", "INDIVIDUAL", false, true));
        (await k.Ok(k.Get($"/api/customers/{c2}"), "c2")).S("code").Should().Be("C-00002");
        (await k.Ok(k.Get($"/api/customers/{c3}"), "c3")).S("code").Should().Be("C-00003");

        // TS-22: search by phone — exact phone, then starts-with, then contains
        (await k.Ok(k.Get($"/api/customers/search?q={phone}"), "phone search")).Items().Select(c => c.G("uuid")).Should().Equal(c1, c2, c3);
        var balance = await k.Ok(k.Get($"/api/customers/{c1}/balance"), "balance");
        (balance.D("balance"), balance.S("currencyCode"), balance.D("overdue")).Should().Be((0m, "PKR", 0m));

        // credit management gate
        var c4 = (await k.Ok(k.Post("/api/customers", Customer("Credit Co", creditLimit: 5000m)), "c4")).GetGuid();
        (await k.Ok(k.Get($"/api/customers/{c4}"), "c4")).D("creditLimit").Should().Be(5000m);
        await k.FeatureOkAsync(Customers, "FEATURE_CREDIT_MANAGEMENT", false);
        await k.Ok(k.Put($"/api/customers/{c4}", Customer("Credit Co", creditLimit: 9000m)), "update without credit management");
        (await k.Ok(k.Get($"/api/customers/{c4}"), "c4")).D("creditLimit").Should().Be(5000m, "ignored without FEATURE_CREDIT_MANAGEMENT");
        var c5 = (await k.Ok(k.Post("/api/customers", Customer("No Credit Co", creditLimit: 100m)), "c5")).GetGuid();
        (await k.Ok(k.Get($"/api/customers/{c5}"), "c5")).D("creditLimit").Should().Be(0m);
        await k.FeatureOkAsync(Customers, "FEATURE_CREDIT_MANAGEMENT", true);
        await k.Ok(k.Put($"/api/customers/{c4}", Customer("Credit Co", creditLimit: 9000m)), "update with credit management");
        (await k.Ok(k.Get($"/api/customers/{c4}"), "c4")).D("creditLimit").Should().Be(9000m);

        // TS-23: sync strictly after `since`
        var all = (await k.Ok(k.Get("/api/sync/customers"), "full sync")).A("customers");
        all.Select(c => c.G("uuid")).Should().Contain(new[] { wu, c1, c2, c3, c4, c5 });
        var since = all.Single(c => c.G("uuid") == c2).S("modifiedAt")!;
        var delta = (await k.Ok(k.Get($"/api/sync/customers?since={Uri.EscapeDataString(since)}"), "delta")).A("customers");
        var ids = delta.Select(c => c.G("uuid")).ToList();
        ids.Should().NotContain(new[] { c1, c2 }, "strictly > since");
        ids.Should().Contain(c3);
        await k.Ok(k.Patch($"/api/customers/{c3}/status", new { isActive = false }), "deactivate c3");
        var after = (await k.Ok(k.Get($"/api/sync/customers?since={Uri.EscapeDataString(since)}"), "delta 2")).A("customers");
        after.Single(c => c.G("uuid") == c3).B("isActive").Should().BeFalse("a deactivated customer is sent so offline copies drop it");
        (await k.Ok(k.Get("/api/customers/search?q=Shop"), "search")).Items().Select(c => c.G("uuid")).Should().NotContain(c3, "search lists active only");
    }

    [Fact]
    public async Task Catalog_sync_returns_only_rows_changed_strictly_after_since()
    {
        var o = await _root.OrgAsync("SYN", currency: false);
        var k = o.K;
        var p1 = await k.CreateProductAsync("Sync one", 1m, 2m);
        var all = await k.Ok(k.Get("/api/sync/catalog"), "full catalog");
        var row = all.A("products").Single(p => p.G("uuid") == p1.ProductUuid);
        all.A("variants").Select(v => v.G("uuid")).Should().Contain(p1.VariantUuid);
        var since = row.S("modifiedAt")!;

        var p2 = await k.CreateProductAsync("Sync two", 1m, 2m);
        var delta = await k.Ok(k.Get($"/api/sync/catalog?since={Uri.EscapeDataString(since)}"), "delta");
        delta.A("products").Select(p => p.G("uuid")).Should().Contain(p2.ProductUuid).And.NotContain(p1.ProductUuid, "strictly > since");
        delta.B("hasMore").Should().BeFalse();
        delta.S("nextSince").Should().NotBeNullOrEmpty();
    }

    // ── TS-26/27 ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Permissions_and_workflow_interfaces_carry_their_module_and_whether_it_is_on()
    {
        var o = await _root.OrgAsync("PRM", currency: false);
        var k = o.K;
        var roleCode = $"QA{Guid.NewGuid():N}"[..14].ToUpperInvariant();
        var role = await k.Post("/api/roles", new { Name = $"A37 QA {roleCode}", RoleCode = roleCode, Description = "A37 QA" });
        role.Status.Should().Be(HttpStatusCode.Created, role.ToString());
        var roleId = role.Result.I("roleId");
        var ids = new List<int>();
        foreach (var code in new[] { "SERVICE_ORDER_VIEW", "BOM_VIEW", "PO_CREATE" })
            ids.Add(Convert.ToInt32((await _f.QueryAsync("SELECT PermissionID FROM auth.Permissions WHERE Code = @c", ("@c", code))).Single()["PermissionID"]));
        await k.Ok(k.Put($"/api/roles/{roleId}/permissions", new { AllowedPermissionIds = ids }), "grant");

        await k.DisableOkAsync(Services, 0);
        await k.DisableOkAsync("MODULE_MIR", 0);

        // TS-27
        var items = (await k.Ok(k.Get($"/api/roles/{roleId}"), "role detail")).A("permissionGroups").SelectMany(g => g.A("permissions")).ToList();
        JsonElement P(string code) => items.Single(i => i.S("code") == code);
        (P("SERVICE_ORDER_VIEW").S("moduleCode"), P("SERVICE_ORDER_VIEW").B("moduleEnabled")).Should().Be((Services, false));
        (P("BOM_VIEW").S("moduleCode"), P("BOM_VIEW").B("moduleEnabled")).Should().Be((Inventory, true));
        (P("PO_CREATE").S("moduleCode"), P("PO_CREATE").B("moduleEnabled")).Should().Be((Demand, true));
        P("SERVICE_ORDER_VIEW").B("isAllowed").Should().BeTrue("assignable, just inert");
        var mirCodes = items.Where(i => i.S("moduleCode") == "MODULE_MIR").ToList();
        mirCodes.Should().NotBeEmpty().And.OnlyContain(i => !i.B("moduleEnabled"));

        // TS-26
        var enabled = (await k.EnabledAsync()).Strings("modules").ToHashSet();
        var interfaces = (await k.Ok(k.Get("/api/workflow/config/interfaces"), "workflow interfaces")).Items();
        interfaces.Should().NotBeEmpty();
        interfaces.Should().OnlyContain(r => r.B("moduleEnabled") == (r.IsNull("moduleCode") || enabled.Contains(r.S("moduleCode")!)), J.Short(interfaces[0]));
        var defs = await k.Ok(k.Get("/api/workflow/definitions"), "workflow definitions");
        var defItems = defs.ValueKind == JsonValueKind.Array ? defs.Items() : defs.A("data");
        defItems.Should().OnlyContain(r => r.B("moduleEnabled") == (r.IsNull("moduleCode") || enabled.Contains(r.S("moduleCode")!)));
        var demandRows = interfaces.Where(r => r.S("moduleCode") == Demand).ToList();
        demandRows.Should().NotBeEmpty("PO/PR workflows are seeded").And.OnlyContain(r => r.B("moduleEnabled"));

        await k.DisableOkAsync(Demand, 0);
        (await k.Ok(k.Get("/api/workflow/config/interfaces"), "workflow interfaces")).Items()
            .Where(r => r.S("moduleCode") == Demand).Should().OnlyContain(r => !r.B("moduleEnabled"), "TS-26: greyed");
    }

    // ── TS-12/32 (R-3) ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_gated_job_leaves_an_organization_without_its_module_pending()
    {
        var o = await _root.OrgAsync("JOB");
        var k = o.K;
        var w = await k.MtoWorldAsync("JOB", rawStock: 20m, baseCurrency: o.Pkr);
        var so = await k.CreateOrderAsync(w.Customer, w.Pkr, "SHIP", w.Address, RLine(w.Fg, 1m));
        await k.ConfirmAsync(so);
        (await k.ProductionOrdersOfAsync(so)).Should().ContainSingle("make to order at confirm");

        await k.BackdateProductionPendingAsync(so);
        await k.DisableOkAsync(Manufacturing, 0);
        await k.RunProductionSweepAsync();   // TS-32: no error
        (await k.ProductionPendingSinceAsync(so)).Should().NotBeNull("D-25: a skipped organization's work stays pending");

        await k.EnableOkAsync(Manufacturing);
        await k.RunProductionSweepAsync();
        (await k.ProductionPendingSinceAsync(so)).Should().BeNull("the sweep takes it once the module is back");
        (await k.ProductionOrdersOfAsync(so)).Should().ContainSingle("idempotent per line");
    }

    // ── TS-34/35 ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Edition_flows_licence_modules_and_bom_management_follows()
    {
        var o = await _root.OrgAsync("EDN", plan: "BASIC", currency: false);
        var k = o.K;
        await _root.LicenceAsync(o.OrgId, (Finance, true), (Warehouse, false), ("MODULE_REPORTS", false));

        // TS-34
        var enabled = await k.EnabledAsync();
        enabled.Strings("modules").Should().Contain(new[] { Inventory, Demand, Suppliers, Finance, Customers });
        enabled.Strings("modules").Should().NotContain(new[] { Manufacturing, Services, Warehouse, Logistics, "MODULE_MIR", "MODULE_REPORTS" });
        enabled.Strings("features").Should().NotContain(Bom, "Basic has neither driver");
        enabled.A("grace").Should().BeEmpty();
        var current = await k.Ok(k.Get("/api/tenant/current"), "tenant/current");
        current.Strings("enabledFeatureCodes").Should().Contain(Finance).And.NotContain(Manufacturing).And.NotContain(Bom);
        (await k.CardAsync(Manufacturing)).S("status").Should().Be("NOT_LICENSED");
        (await k.Enable(Manufacturing)).ShouldFail(HttpStatusCode.BadRequest, "D-4", "Manufacturing is not included in your plan — contact your administrator.");
        (await k.Get("/api/boms")).Status.Should().Be(HttpStatusCode.OK, "BOM reads need Inventory only");
        (await k.Post("/api/boms", new { ProductUuid = Guid.NewGuid(), BaseQuantity = 1m, Lines = Array.Empty<object>() }))
            .ShouldBeModuleRefusal(Bom, inGrace: false, "no BOM management in this edition");
        (await k.Get("/api/production-orders")).ShouldBeModuleRefusal(Manufacturing, inGrace: false, "never had it: not even reads");

        // TS-35: Manufacturing + Warehouse licensed → BOM management on by itself
        await _root.LicenceAsync(o.OrgId, (Manufacturing, true), (Warehouse, true));
        enabled = await k.EnabledAsync();
        enabled.Strings("modules").Should().Contain(new[] { Manufacturing, Warehouse });
        enabled.Strings("features").Should().Contain(Bom, "MOD-08 on the licence path");
        var bomCard = await k.FeatureOfAsync(Inventory, Bom);
        (bomCard.B("isEnabled"), bomCard.B("autoManaged")).Should().Be((true, true));
        (await _root.SuperHistoryAsync(o.OrgId)).Where(h => h.S("featureCode") == Bom).Select(h => h.S("action")).Should().Contain("AUTO_ENABLED");
        (await k.Get("/api/production-orders")).Status.Should().Be(HttpStatusCode.OK);
        (await k.Post("/api/boms", new { ProductUuid = Guid.NewGuid(), BaseQuantity = 1m, Lines = Array.Empty<object>() }))
            .ShouldPassTheGate("BOM management is on");
    }

    // ── Backward compatibility: a Standard organization ──────────────────────────

    [Fact]
    public async Task A_standard_plan_organization_still_uses_every_pre_A37_screen()
    {
        var o = await _root.OrgAsync("STD", plan: "STANDARD");
        var k = o.K;

        // products, rate card, PO → GRN, BOM create/submit/approve/activate
        var w = await k.MtoWorldAsync("STD", rawStock: 30m, baseCurrency: o.Pkr);
        // production order → issue → start → output → complete → quality inspection → FGR
        await k.ProduceToStockAsync(w, 2m);

        // GRN + SRO (purchase returns)
        var (_, grn) = await k.StockUpAsync(w.Vendor, w.Wh, (w.Raw, 5m, 5m));
        var grnLine = (await k.Ok(k.Get($"/api/grns/{grn}"), "read GRN")).A("lines").Single();
        await k.Ok(k.Post("/api/sros", new
        {
            SupplierId = w.Vendor.Uuid, SupplierName = w.Vendor.Name, SroType = "POST_RECEIPT_DEFECT", OriginalGrnId = grn,
            WarehouseUuid = w.Wh.Uuid, ReturnReason = "DAMAGED",
            Lines = new[] { new { GrnLineUuid = grnLine.G("uuid"), ProductUuid = w.Raw.ProductUuid, ItemDescription = w.Raw.Name, QtyToReturn = 1m, ReturnReason = "DAMAGED" } }
        }), "create SRO");

        // RFQ, sale quotation
        await k.Ok(k.Post("/api/quotations", new { Title = k.Next("RFQ"), SourceType = "STANDALONE", Lines = new[] { new { ItemDescription = "Bolts", Quantity = 10m } } }), "RFQ");
        var widget = await k.CreateProductAsync("STD Widget", 10m, 40m);
        await k.DraftQuotationAsync(w.Customer, w.Pkr, widget, 1m);

        // sale order confirm → delivery → pick list → goods issue → consignment → proof of delivery
        await k.StockUpAsync(w.Vendor, w.Wh, (widget, 5m, 10m));
        var pickAndShip = await k.RouteUuidAsync(PickAndShip);
        var so = await k.CreateOrderAsync(w.Customer, w.Pkr, "SHIP", w.Address, RLine(widget, 2m, pickAndShip));
        await k.ConfirmAsync(so);
        var delivery = (await k.SoDeliveriesAsync(so)).Single().G("uuid");
        await k.ShipNoPackAsync(delivery, await k.ManualCarrierAsync());

        // self-pickup sale through to an issued invoice
        await k.SellAsync(w.Customer, w.Pkr, SapKit.Line(widget, 1m));

        // the A37 screens are there too
        (await k.Get("/api/customers")).Status.Should().Be(HttpStatusCode.OK);
        (await k.ModulesAsync()).Should().NotBeEmpty();
        var enabled = await k.EnabledAsync();
        enabled.Strings("modules").Should().Contain(new[] { Manufacturing, Services, Logistics, Warehouse, Demand, Finance });
        enabled.Strings("features").Should().Contain(new[] { Bom, "FEATURE_QUALITY_INSPECTION", "FEATURE_RFQ_MANAGEMENT", "FEATURE_PICK_LISTS",
            "FEATURE_SHIPMENT_TRACKING", "FEATURE_PURCHASE_RETURNS", "FEATURE_CREDIT_MANAGEMENT" });
    }
}

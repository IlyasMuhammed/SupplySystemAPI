using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using Xunit;
using static SMS.Integration.Tests.FulfillmentRoutes.Routes;
using static SMS.Integration.Tests.RouteClassification.Rc;
using static SMS.Integration.Tests.SalesPreOrder.PreOrder;

namespace SMS.Integration.Tests.RouteClassification;

/// <summary>
/// A34-PF-04 on the real host (LocalDB): lead time across every stage.
/// <list type="bullet">
/// <item><b>T-C3-06, D-10:</b> a missing defaults row reads 1/3/1/0/0/0; a concurrent first save is an upsert (one row,
/// no 409); validation; a change shows on every variant that uses the org defaults.</item>
/// <item><b>T-C3-01..05, D-11:</b> a variant's 8 components through their tiers (product → rate card → override), the
/// visibility rules by route, reset to defaults.</item>
/// <item><b>T-C4-01..05, D-12..14:</b> the calculator through the real Material BOM reader: stock-aware supplier lead,
/// a 3-level chain short and stocked, the per-level tree; a BOM cycle can't be built.</item>
/// <item><b>T-C4-06/07, D-15, D-16, D-19:</b> inquiry ⏱ → quotation → order (dates copied), manual override set and
/// cleared, the full PUT round-trip, the production order's planned dates at confirm, edits after confirm (not
/// rescheduled) and after cancel (refused).</item>
/// </list>
/// The calculator caches per (org, variant, route, exact quantity, day) for 5 minutes (D-27), so every stock-dependent
/// call after a stock change uses a quantity not asked before.
/// <para>Run alone: <c>dotnet test &lt;out&gt;\SMS.Integration.Tests.dll --filter FullyQualifiedName~LeadTimeE2ETests</c>.</para>
/// </summary>
public sealed class LeadTimeE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _k;

    public LeadTimeE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _k = new SapKit(factory, "LT");
    }

    private static string D(int days) => Day(Today.AddDays(days));

    private Task<Api> PutDefaults(int pick, int ship, int sales, int mfgBuffer, int qc, int transfer, SapKit? kit = null)
    {
        var k = kit ?? _k;
        return k.Put("/api/lead-time/defaults", new
        {
            PickPackDays = pick, ShippingLeadTimeDays = ship, SalesBufferDays = sales,
            ManufacturingBufferDays = mfgBuffer, QualityInspectionDays = qc, InternalTransferDays = transfer
        });
    }

    private async Task DefaultsAsync(int pick, int ship, int sales, int mfgBuffer, int qc, int transfer) =>
        await _k.Ok(PutDefaults(pick, ship, sales, mfgBuffer, qc, transfer), "save the lead-time defaults");

    private Task<JsonElement> VariantLeadTimesAsync(Product p) => _k.Ok(_k.Get($"/api/variants/{p.VariantUuid}/lead-times"), "variant lead times");

    private static JsonElement Comp(JsonElement model, string code) => model.A("components").Single(c => c.S("code") == code);
    private static JsonElement? CompOrNull(JsonElement result, string code) =>
        result.A("components").Where(c => c.S("code") == code).Select(c => (JsonElement?)c).SingleOrDefault();

    private Task<Api> TryCalculate(Product p, decimal qty, Guid? route = null, string? requested = null) =>
        _k.Post("/api/lead-time/calculate", new { VariantUuid = p.VariantUuid, Quantity = qty, RouteUuid = route, RequestedDate = requested });

    private async Task<JsonElement> CalculateAsync(Product p, decimal qty, Guid? route = null, string? requested = null) =>
        await _k.Ok(TryCalculate(p, qty, route, requested), $"calculate {p.Name} × {qty}");

    private async Task SetVariantLeadTimesAsync(Product p, object body) =>
        await _k.Ok(_k.Put($"/api/variants/{p.VariantUuid}/lead-times", body), "save variant lead times");

    private static string Sum(JsonElement result) =>
        string.Join(" + ", result.A("components").Select(c => $"{c.S("code")} {c.I("days")} ({c.S("source")})"));

    // ── T-C3-06 / D-10 ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Defaults_read_as_system_values_until_saved_and_a_change_shows_on_every_variant_using_them()
    {
        // A fresh organization has no row: the system defaults, isSaved false. Two first saves at once are one upsert.
        var (org2, k2, _) = await _k.SecondOrganizationAsync("LTD");
        var fresh = await k2.Ok(k2.Get("/api/lead-time/defaults"), "org 2 defaults");
        (fresh.B("isSaved"), fresh.I("pickPackDays"), fresh.I("shippingLeadTimeDays"), fresh.I("salesBufferDays"),
            fresh.I("manufacturingBufferDays"), fresh.I("qualityInspectionDays"), fresh.I("internalTransferDays"))
            .Should().Be((false, 1, 3, 1, 0, 0, 0), "D-10: a missing row reads as the system defaults");
        var race = await Task.WhenAll(PutDefaults(2, 5, 1, 0, 0, 0, k2), PutDefaults(3, 6, 1, 0, 0, 0, k2));
        race.Should().OnlyContain(r => r.Status == HttpStatusCode.OK, $"a concurrent first save is retried as an update — {string.Join(" / ", race.Select(r => r.ToString()))}");
        var rows = await _f.QueryAsync("SELECT COUNT(*) AS N FROM inventory.LeadTimeDefaults WHERE OrganizationId = @o", ("@o", org2));
        Convert.ToInt32(rows[0]["N"]).Should().Be(1, "one row per organization");
        var saved = await k2.Ok(k2.Get("/api/lead-time/defaults"), "org 2 defaults after the race");
        saved.B("isSaved").Should().BeTrue();
        new[] { (saved.I("pickPackDays"), saved.I("shippingLeadTimeDays")) }.Should().BeSubsetOf(new[] { (2, 5), (3, 6) }, "one of the two saves won whole");

        // BR-C3-01 validation.
        var bad = await PutDefaults(-1, 3, 1, 0, 0, 0);
        bad.ShouldBe(HttpStatusCode.BadRequest, "negative days");
        bad.Message.Should().Contain("Pick & pack days must be between 0 and 365.");
        (await PutDefaults(1, 366, 1, 0, 0, 0)).ShouldBe(HttpStatusCode.BadRequest, "over 365");

        // T-C3-06: a variant on the org defaults follows a change.
        var p = await _k.CreateProductAsync("LT Defaults Widget", 5m, 9m);
        await DefaultsAsync(1, 3, 1, 0, 0, 0);
        var before = await VariantLeadTimesAsync(p);
        (Comp(before, "PICK_PACK").I("resolvedDays"), Comp(before, "PICK_PACK").S("source"), Comp(before, "SHIPPING").I("resolvedDays")).Should().Be((1, "ORG_DEFAULT", 3));
        await DefaultsAsync(2, 4, 2, 1, 1, 1);
        var after = await VariantLeadTimesAsync(p);
        foreach (var (code, days) in new[] { ("PICK_PACK", 2), ("SHIPPING", 4), ("SALES_BUFFER", 2), ("MFG_BUFFER", 1), ("QC", 1), ("TRANSFER", 1) })
            (Comp(after, code).I("resolvedDays"), Comp(after, code).S("source")).Should().Be((days, "ORG_DEFAULT"), $"{code} follows the defaults");
        var mine = await _k.Ok(_k.Get("/api/lead-time/defaults"), "org 1 defaults");
        (mine.B("isSaved"), mine.I("pickPackDays")).Should().Be((true, 2));
        // Org 2 keeps exactly what its own race saved; org 1's (2, 4) never leaks in. (Comparing pickPackDays alone with
        // org 1's 2 was flaky: org 2's race can itself save 2.)
        var again = await k2.Ok(k2.Get("/api/lead-time/defaults"), "org 2 again");
        (again.I("pickPackDays"), again.I("shippingLeadTimeDays"))
            .Should().Be((saved.I("pickPackDays"), saved.I("shippingLeadTimeDays")), "each org has its own row");
    }

    // ── T-C3-01..05 / D-11 ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_variants_components_resolve_through_their_tiers_with_visibility_by_route()
    {
        await DefaultsAsync(1, 3, 1, 0, 0, 0);
        var pkr = await _k.PkrBaseAsync();
        var vendor = await _k.CreateVendorAsync("LT Vendor");
        var p = await _k.CreateClassifiedProductAsync("LT Tier Widget", SMS.Shared.Common.ProductType.StockItem, SMS.Shared.Common.SupplyMethod.Purchase,
            5m, 9m, leadTimeDays: 6);

        var m = await VariantLeadTimesAsync(p);
        m.A("components").Select(c => c.S("code")).Should().Equal("SUPPLIER", "MANUFACTURING", "MFG_BUFFER", "QC", "TRANSFER", "PICK_PACK", "SHIPPING", "SALES_BUFFER");
        (m.S("routeCode"), m.B("routeFromOrgDefault"), m.B("requiresShipping"), m.S("routeCategory")).Should().Be((PickAndShip, true, true, Stock), "the org's SHIP default");
        (Comp(m, "SUPPLIER").I("resolvedDays"), Comp(m, "SUPPLIER").S("source")).Should().Be((6, "PRODUCT"), "D-11: Product.LeadTimeDays");
        (Comp(m, "MANUFACTURING").B("visible"), Comp(m, "MFG_BUFFER").B("visible"), Comp(m, "SHIPPING").B("visible")).Should().Be((false, false, true));

        await _k.PreferredSupplierAsync(p, vendor, 5m, pkr, leadTimeDays: 4);
        m = await VariantLeadTimesAsync(p);
        (Comp(m, "SUPPLIER").I("resolvedDays"), Comp(m, "SUPPLIER").S("source")).Should().Be((4, "SUPPLIER_RATE"), "the preferred rate card");

        await SetVariantLeadTimesAsync(p, new { SupplierLeadTimeDays = 9, PickPackDays = 2 });
        m = await VariantLeadTimesAsync(p);
        (Comp(m, "SUPPLIER").I("resolvedDays"), Comp(m, "SUPPLIER").S("source"), Comp(m, "SUPPLIER").P("storedDays").GetInt32()).Should().Be((9, "VARIANT", 9));
        (Comp(m, "PICK_PACK").I("resolvedDays"), Comp(m, "PICK_PACK").S("source")).Should().Be((2, "VARIANT"));
        (Comp(m, "SHIPPING").I("resolvedDays"), Comp(m, "SHIPPING").S("source")).Should().Be((3, "ORG_DEFAULT"));
        (await _k.VariantAsync(p)).P("supplierLeadTimeDays").GetInt32().Should().Be(9, "D-11: the variant model shows the override");

        // A route without SHIP hides the shipping days (BR-C3-05) and leaves them out of the total.
        await _k.SetVariantRouteAsync(p, await _k.RouteUuidAsync(PickOnly));
        m = await VariantLeadTimesAsync(p);
        (m.B("routeFromOrgDefault"), m.B("requiresShipping"), Comp(m, "SHIPPING").B("visible")).Should().Be((false, false, false));
        m.I("totalDays").Should().Be(9 + 2 + 1, "supplier + pick & pack + sales buffer (QC / transfer 0)");

        // Reset to org defaults = all null.
        await SetVariantLeadTimesAsync(p, new { });
        m = await VariantLeadTimesAsync(p);
        (Comp(m, "SUPPLIER").I("resolvedDays"), Comp(m, "SUPPLIER").S("source"), Comp(m, "PICK_PACK").S("source")).Should().Be((4, "SUPPLIER_RATE", "ORG_DEFAULT"));
        Comp(m, "PICK_PACK").IsNull("storedDays").Should().BeTrue();

        var bad = await _k.Put($"/api/variants/{p.VariantUuid}/lead-times", new { ShippingLeadTimeDays = 4000 });
        bad.ShouldBe(HttpStatusCode.BadRequest, "0–3650");
        bad.Message.Should().Contain("Shipping days must be between 0 and 3650.");

        // A make-to-order variant shows the manufacturing components (BR-C3-04).
        var whId = await _k.WarehouseIdAsync(await _k.CreateWarehouseAsync());
        var fg = await _k.FinishedGoodAsync("LT Tier FG", whId);
        await _k.SetVariantRouteAsync(fg, await _k.RouteUuidAsync(MfgPickShip));
        var f = await VariantLeadTimesAsync(fg);
        (f.S("routeCategory"), Comp(f, "MANUFACTURING").B("visible"), Comp(f, "MFG_BUFFER").B("visible")).Should().Be((Manufacture, true, true));
        (Comp(f, "MANUFACTURING").I("resolvedDays"), Comp(f, "MANUFACTURING").S("source")).Should().Be((1, "SYSTEM_DEFAULT"), "D-12: 1 day per level");
        await SetVariantLeadTimesAsync(fg, new { ManufacturingLeadTimeDays = 3 });
        (Comp(await VariantLeadTimesAsync(fg), "MANUFACTURING").S("source")).Should().Be("VARIANT");
    }

    // ── T-C4-01..05 / D-12..D-14 ────────────────────────────────────────────────

    [Fact]
    public async Task The_calculator_is_stock_aware_and_BOM_aware_over_a_three_level_chain()
    {
        await DefaultsAsync(1, 3, 1, 1, 0, 0);
        var pkr = await _k.PkrBaseAsync();
        await _k.EnsureTaxCodeAsync("PGST17", 17m, "PURCHASE", isDefault: true);
        await _k.CreateApproverPlaceholdersAsync();
        var wh = await _k.CreateWarehouseAsync();
        var whId = await _k.WarehouseIdAsync(wh);
        var vendor = await _k.CreateVendorAsync("LT Calc Vendor");

        // ── T-C4-01 / D-14: a stock variant ──
        var s = await _k.CreateProductAsync("LT Stock", 5m, 9m);
        await _k.PreferredSupplierAsync(s, vendor, 5m, pkr, leadTimeDays: 7);
        var r = await CalculateAsync(s, 5m, requested: D(20));
        (r.S("routeCode"), r.S("routeCategory")).Should().Be((PickAndShip, Stock));
        r.A("components").Select(c => (c.S("code"), c.I("days"))).Should().Equal(new[] { ("SUPPLIER", 7), ("PICK_PACK", 1), ("SHIPPING", 3), ("SALES_BUFFER", 1) }, Sum(r));
        CompOrNull(r, "SUPPLIER")!.Value.S("source").Should().Be("SUPPLIER_RATE");
        (r.I("totalLeadTimeDays"), r.DateOnly("earliestDeliveryDate"), r.DateOnly("latestStartDate"), r.B("meetsRequestedDate")).Should().Be((12, D(12), D(8), true));
        (await CalculateAsync(s, 6m, requested: D(5))).B("meetsRequestedDate").Should().BeFalse();

        await _k.StockUpAsync(vendor, wh, (s, 10m, 5m));
        var inStock = await CalculateAsync(s, 4m);
        (CompOrNull(inStock, "SUPPLIER")!.Value.I("days"), CompOrNull(inStock, "SUPPLIER")!.Value.S("source"), inStock.I("totalLeadTimeDays"))
            .Should().Be((0, "IN_STOCK", 5), "D-14: enough free stock");
        (await CalculateAsync(s, 15m)).I("totalLeadTimeDays").Should().Be(12, "more than the best warehouse holds: the supplier lead counts");

        // ── A 3-level chain: Raw (5 days) → Sub (2 days/level, 2 raw) → FG (4 days/level, 1 sub + 1 pack (3 days)) ──
        var raw = await _k.RawMaterialAsync("LT Raw");
        await _k.PreferredSupplierAsync(raw, vendor, 2m, pkr, leadTimeDays: 5);
        var pack = await _k.RawMaterialAsync("LT Pack");
        await _k.PreferredSupplierAsync(pack, vendor, 1m, pkr, leadTimeDays: 3);
        var sub = await _k.SemiFinishedAsync("LT Sub", whId);
        await _k.CreateActiveBomAsync(sub, 1m, (raw, 2m));
        await SetVariantLeadTimesAsync(sub, new { ManufacturingLeadTimeDays = 2 });
        var fg = await _k.FinishedGoodAsync("LT FG", whId);
        await _k.CreateActiveBomAsync(fg, 1m, (sub, 1m), (pack, 1m));
        await SetVariantLeadTimesAsync(fg, new { ManufacturingLeadTimeDays = 4 });
        var mfg = await _k.RouteUuidAsync(MfgPickShip);
        await _k.SetVariantRouteAsync(fg, mfg);

        // T-C4-03/04: nothing in stock — FG 4 + max(Sub (2 + raw 5), pack 3) = 11.
        var shortR = await CalculateAsync(fg, 2m);
        (shortR.S("routeCategory"), shortR.S("routeCode")).Should().Be((Manufacture, MfgPickShip));
        shortR.A("components").Select(c => (c.S("code"), c.I("days"))).Should().Equal(
            new[] { ("MANUFACTURING", 11), ("MFG_BUFFER", 1), ("PICK_PACK", 1), ("SHIPPING", 3), ("SALES_BUFFER", 1) }, Sum(shortR));
        CompOrNull(shortR, "MANUFACTURING")!.Value.S("source").Should().Be("BOM");
        CompOrNull(shortR, "SUPPLIER").Should().BeNull("a MANUFACTURE route counts materials through the BOM");
        shortR.I("totalLeadTimeDays").Should().Be(17);

        var tree = await _k.Ok(_k.Post("/api/lead-time/calculate-manufacturing", new { VariantUuid = fg.VariantUuid, Quantity = 2m }), "per-level tree");
        (tree.I("levelDays"), tree.S("levelDaysSource"), tree.I("totalDays")).Should().Be((4, "VARIANT", 11));
        var subIn = tree.A("inputs").Single(i => i.G("variantUuid") == sub.VariantUuid);
        (subIn.D("requiredQty"), subIn.D("freeQty"), subIn.D("shortfallQty"), subIn.B("isManufactured"), subIn.I("waitDays"), subIn.S("source"))
            .Should().Be((2m, 0m, 2m, true, 7, "BOM"));
        var subNode = subIn.P("node");
        (subNode.I("levelDays"), subNode.I("totalDays")).Should().Be((2, 7));
        var rawIn = subNode.A("inputs").Single();
        (rawIn.D("requiredQty"), rawIn.I("waitDays"), rawIn.S("source")).Should().Be((4m, 5, "SUPPLIER_RATE"));
        var packIn = tree.A("inputs").Single(i => i.G("variantUuid") == pack.VariantUuid);
        (packIn.I("waitDays"), packIn.B("isManufactured"), packIn.P("node").ValueKind).Should().Be((3, false, JsonValueKind.Null));
        tree.A("warnings").Should().BeEmpty();
        (await _k.Post("/api/lead-time/calculate-manufacturing", new { VariantUuid = s.VariantUuid, Quantity = 1m }))
            .ShouldBe(HttpStatusCode.BadRequest, "not a manufactured product");

        // T-C4-02-ish: purchased inputs in the production warehouse — Sub still has to be made (2), pack waits 0 → 4 + 2 = 6.
        await _k.StockUpAsync(vendor, wh, (raw, 20m, 2m), (pack, 20m, 1m));
        var stocked = await CalculateAsync(fg, 3m);
        CompOrNull(stocked, "MANUFACTURING")!.Value.I("days").Should().Be(6, Sum(stocked));

        // T-C4-02: a one-level BOM whose inputs are all in stock → the level's own days only.
        var simple = await _k.FinishedGoodAsync("LT Simple", whId);
        await _k.CreateActiveBomAsync(simple, 1m, (raw, 1m), (pack, 1m));
        await SetVariantLeadTimesAsync(simple, new { ManufacturingLeadTimeDays = 2 });
        var simpleR = await CalculateAsync(simple, 3m, route: mfg);
        CompOrNull(simpleR, "MANUFACTURING")!.Value.I("days").Should().Be(2, "every component is in stock: component lead 0");

        // Errors (contract §4.6).
        (await TryCalculate(s, 0m)).ShouldBe(HttpStatusCode.BadRequest, "Quantity must be greater than zero.");
        (await _k.Post("/api/lead-time/calculate", new { VariantUuid = Guid.NewGuid(), Quantity = 1m })).ShouldBe(HttpStatusCode.NotFound, "unknown variant");
        var badRoute = await TryCalculate(s, 1m, route: Guid.NewGuid());
        badRoute.ShouldBe(HttpStatusCode.BadRequest, "unknown route");
        badRoute.Message.Should().Contain("Route not found in your organization.");

        // T-C4-05: a cycle can't be built — the BOM rules refuse it before the calculator could meet one.
        var cycle = await _k.TryCreateBom(sub, 1m, (fg, 1m));
        if (cycle.Status == HttpStatusCode.OK)
        {
            var bom = cycle.Result.GetGuid();
            var steps = new[]
            {
                await _k.Post($"/api/boms/{bom}/submit"),
                await _k.Post($"/api/boms/{bom}/approve", null, await _k.InspectorAsync()),
                await _k.Post($"/api/boms/{bom}/activate", null, await _k.InspectorAsync())
            };
            steps.Should().Contain(x => x.Status != HttpStatusCode.OK, "BR-C4-02: a BOM that closes a cycle never becomes active");
        }
        (await CalculateAsync(fg, 7m)).A("components").Should().NotBeEmpty("the chain still calculates");
    }

    // ── T-C4-06/07, D-15, D-16, D-19 ────────────────────────────────────────────

    [Fact]
    public async Task Line_dates_flow_from_inquiry_to_quotation_to_order_and_into_the_production_plan()
    {
        // post = pick 1 + ship 3 + sales 1 + QC 1 + transfer 0 = 6; MANUFACTURING 2 (inputs in stock) + MFG_BUFFER 1.
        await DefaultsAsync(1, 3, 1, 1, 1, 0);
        var w = await _k.MtoWorldAsync("LTL", rawStock: 50m);
        await SetVariantLeadTimesAsync(w.Fg, new { ManufacturingLeadTimeDays = 2 });
        var plain = await _k.CreateProductAsync("LTL Plain", 5m, 9m);
        await _k.StockUpAsync(w.Vendor, w.Wh, (plain, 10m, 5m));
        const int total = 2 + 1 + 1 + 1 + 3 + 1;   // 9

        // ── Inquiry: ⏱ on line 1; line 2 has a typed estimate ──
        var inquiry = (await _k.Ok(_k.Post("/api/sale-inquiries", new
        {
            PartnerId = w.Customer.Uuid, CustomerReference = _k.Next("RFQ"), ReceivedDate = D(0),
            Lines = new object[]
            {
                new { VariantUuid = w.Fg.VariantUuid, ProductDescription = w.Fg.Name, RequestedQuantity = 2m },
                new { VariantUuid = plain.VariantUuid, ProductDescription = plain.Name, RequestedQuantity = 1m }
            }
        }), "create inquiry")).GetGuid();
        var il = (await _k.Ok(_k.Get($"/api/sale-inquiries/{inquiry}"), "read inquiry")).A("lines").OrderBy(l => l.I("lineNumber")).ToList();
        var il1 = il[0].G("uuid");
        var calc = await _k.Ok(_k.Post($"/api/sale-inquiries/{inquiry}/lines/{il1}/lead-time", new { }), "⏱ on the inquiry line");
        calc.P("leadTime").I("totalLeadTimeDays").Should().Be(total, Sum(calc.P("leadTime")));
        var cl = calc.P("line");
        (cl.I("calculatedLeadTimeDays"), cl.DateOnly("calculatedDeliveryDate"), cl.S("deliveryDateSource"), cl.DateOnly("effectiveDeliveryDate"))
            .Should().Be((total, D(total), "CALCULATED", D(total)));
        cl.IsNull("leadTimeCalculatedAt").Should().BeFalse();

        await _k.Ok(_k.Patch($"/api/sale-inquiries/{inquiry}/status", new { Status = "UNDER_REVIEW" }), "start review");
        // D-15: CAN_SUPPLY accepts the calculated date when no estimate is typed.
        await _k.Ok(_k.Put($"/api/sale-inquiries/{inquiry}/lines/{il1}", new
        {
            VariantUuid = w.Fg.VariantUuid, ProductDescription = w.Fg.Name, RequestedQuantity = 2m, LineStatus = "CAN_SUPPLY"
        }), "CAN_SUPPLY without a typed estimate");
        await _k.Ok(_k.Put($"/api/sale-inquiries/{inquiry}/lines/{il[1].G("uuid")}", new
        {
            VariantUuid = plain.VariantUuid, ProductDescription = plain.Name, RequestedQuantity = 1m, LineStatus = "CAN_SUPPLY", EstimatedDeliveryDate = D(12)
        }), "line 2 with an estimate");
        await _k.Ok(_k.Patch($"/api/sale-inquiries/{inquiry}/status", new { Status = "REVIEW_COMPLETE" }), "complete review");

        // ── Quotation: the dates come along (D-15) ──
        var q = (await _k.Ok(_k.Post($"/api/sale-inquiries/{inquiry}/create-quotation", new
        {
            CurrencyId = w.Pkr, ValidFrom = D(0), ValidTo = D(30)
        }), "quotation from the inquiry")).GetGuid();
        (await _k.Post($"/api/sale-inquiries/{inquiry}/lines/{il1}/lead-time", new { })).ShouldBe(HttpStatusCode.BadRequest, "a QUOTED inquiry is closed");
        var ql = (await _k.Ok(_k.Get($"/api/sale-quotations/{q}"), "read quotation")).A("lines").OrderBy(l => l.I("lineNumber")).ToList();
        (ql[0].I("calculatedLeadTimeDays"), ql[0].DateOnly("calculatedDeliveryDate"), ql[0].S("deliveryDateSource")).Should().Be((total, D(total), "CALCULATED"));
        (ql[1].DateOnly("promisedDeliveryDate"), ql[1].S("deliveryDateSource"), ql[1].DateOnly("effectiveDeliveryDate")).Should().Be((D(12), "MANUAL", D(12)));
        var qcalc = await _k.Ok(_k.Post($"/api/sale-quotations/{q}/lines/{ql[0].G("uuid")}/lead-time", new { }), "⏱ on the DRAFT quotation line");
        qcalc.P("line").I("calculatedLeadTimeDays").Should().Be(total);

        await _k.Ok(_k.Post($"/api/sale-quotations/{q}/send"), "send");
        (await _k.Post($"/api/sale-quotations/{q}/lines/{ql[0].G("uuid")}/lead-time", new { })).ShouldBe(HttpStatusCode.BadRequest, "only a DRAFT quotation");
        foreach (var line in ql)
            await _k.Ok(_k.Patch($"/api/sale-quotations/{q}/lines/{line.G("uuid")}/customer-response", new { Response = "ACCEPTED" }), "accept line");
        await _k.Ok(_k.Post($"/api/sale-quotations/{q}/accept"), "accept");
        var so = (await _k.Ok(_k.Post($"/api/sale-quotations/{q}/convert-to-order", new
        {
            DeliveryMode = "SHIP", ShippingAddressId = w.Address, ExpectedDeliveryDate = D(30)
        }), "convert")).GetGuid();

        // ── The order: promised → manual, Calculated* copied ──
        var draft = await _k.GetSaleOrderAsync(so);
        var l1 = draft.LineOf(w.Fg);
        var l2 = draft.LineOf(plain);
        (l1.I("calculatedLeadTimeDays"), l1.DateOnly("calculatedDeliveryDate"), l1.S("deliveryDateSource"), l1.IsNull("manualDeliveryDate"))
            .Should().Be((total, D(total), "CALCULATED", true));
        (l2.DateOnly("manualDeliveryDate"), l2.S("deliveryDateSource")).Should().Be((D(12), "MANUAL"), "D-15: promised → manual");
        var l1Uuid = l1.G("uuid");

        var socalc = await _k.Ok(_k.Post($"/api/sale-orders/{so}/lines/{l1Uuid}/lead-time", new { }), "⏱ on the DRAFT order line");
        (socalc.P("leadTime").DateOnly("latestStartDate"), socalc.P("leadTime").B("meetsRequestedDate")).Should().Be((D(30 - total), true), "the header's expected date is the request");

        // ── T-C4-06 / T-C4-07: manual override set and cleared ──
        async Task<JsonElement> SetDateAsync(string? date, string why) =>
            await _k.Ok(_k.Put($"/api/sale-orders/{so}/lines/{l1Uuid}/delivery-date", new { ManualDeliveryDate = date }), why);
        var set = await SetDateAsync(D(20), "set a manual date");
        (set.P("line").DateOnly("effectiveDeliveryDate"), set.P("line").S("deliveryDateSource"), set.B("productionNotRescheduled"))
            .Should().Be((D(20), "MANUAL", false), "T-C4-06");
        var cleared = await SetDateAsync(null, "clear it");
        (cleared.P("line").DateOnly("effectiveDeliveryDate"), cleared.P("line").S("deliveryDateSource")).Should().Be((D(total), "CALCULATED"), "T-C4-07");
        await SetDateAsync(D(20), "set it again");

        // C-14: the full PUT round-trips the fields (draft lines are rebuilt).
        var current = await _k.GetSaleOrderAsync(so);
        object Back(JsonElement l) => new
        {
            VariantUuid = l.G("variantUuid"), Quantity = l.D("quantity"), DiscountPercent = l.D("discountPercent"), TaxPercent = l.D("taxPercent"),
            TaxCodeUuid = l.NG("taxCodeUuid"), ManualDeliveryDate = l.IsNull("manualDeliveryDate") ? null : l.DateOnly("manualDeliveryDate"),
            CalculatedLeadTimeDays = l.P("calculatedLeadTimeDays").ValueKind == JsonValueKind.Null ? (int?)null : l.I("calculatedLeadTimeDays"),
            CalculatedDeliveryDate = l.IsNull("calculatedDeliveryDate") ? null : l.DateOnly("calculatedDeliveryDate"),
            LeadTimeCalculatedAt = l.IsNull("leadTimeCalculatedAt") ? (DateTime?)null : l.P("leadTimeCalculatedAt").GetDateTime()
        };
        await _k.Ok(_k.Put($"/api/sale-orders/{so}", new
        {
            PartnerId = w.Customer.Uuid, CurrencyId = w.Pkr, DeliveryMode = "SHIP", ShippingAddressId = w.Address, ExpectedDeliveryDate = D(30),
            Lines = current.A("lines").Select(Back).ToArray()
        }), "save the order with the line dates sent back");
        var rt = await _k.GetSaleOrderAsync(so);
        var rt1 = rt.LineOf(w.Fg);
        (rt1.DateOnly("manualDeliveryDate"), rt1.I("calculatedLeadTimeDays"), rt1.DateOnly("calculatedDeliveryDate"), rt1.S("deliveryDateSource"))
            .Should().Be((D(20), total, D(total), "MANUAL"), "C-14: the dates survive the rebuild");
        l1Uuid = rt1.G("uuid");

        // ── D-19: the production order's dates at confirm ──
        var po = (await _k.ConfirmAsync(so)).A("productionOrders").Single().G("productionOrderUuid");
        var p = await _k.ProductionOrderAsync(po);
        (p.DateOnly("requiredDate"), p.DateOnly("plannedStartDate")).Should().Be((D(14), D(11)),
            "RequiredDate = 20 − post 6 = 14; start = 14 − (level 2 + buffer 1) = 11");

        // After confirm: no ⏱; a new manual date is taken but production is not rescheduled.
        var lt = await _k.Post($"/api/sale-orders/{so}/lines/{l1Uuid}/lead-time", new { });
        lt.ShouldBe(HttpStatusCode.BadRequest, "only a draft");
        lt.Message.Should().Contain("is CONFIRMED: line lead times can only be calculated on a draft.");
        var later = await SetDateAsync(D(25), "move the date after confirm");
        (later.B("productionNotRescheduled"), later.P("line").DateOnly("effectiveDeliveryDate")).Should().Be((true, D(25)));
        later.S("warning").Should().Contain(p.S("productionNumber")).And.Contain("is not rescheduled");
        (await _k.ProductionOrderAsync(po)).DateOnly("requiredDate").Should().Be(D(14), "D-16: not rescheduled");

        await _k.Ok(_k.TryCancelOrder(so), "cancel");
        var refused = await _k.Put($"/api/sale-orders/{so}/lines/{l1Uuid}/delivery-date", new { ManualDeliveryDate = D(26) });
        refused.ShouldBe(HttpStatusCode.BadRequest, "a cancelled order's dates are closed");
        refused.Message.Should().Contain("is CANCELLED: its delivery dates can no longer change.");

        // ── D-19 with no date at all: RequiredDate = today + the manufacturing total, start = tomorrow ──
        var bare = await _k.CreateOrderAsync(w.Customer, w.Pkr, "SHIP", w.Address, RLine(w.Fg, 1m));
        var bareLine = (await _k.GetSaleOrderAsync(bare)).LineOf(w.Fg);
        bareLine.S("deliveryDateSource").Should().Be("NONE");
        var barePo = await _k.ProductionOrderAsync((await _k.ConfirmAsync(bare)).A("productionOrders").Single().G("productionOrderUuid"));
        (barePo.DateOnly("requiredDate"), barePo.DateOnly("plannedStartDate")).Should().Be((D(2), D(1)), "manufacturing total 2 (inputs in stock); start tomorrow");
    }
}

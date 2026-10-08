using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Services.LeadTimes;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;
using static SMS.Modules.Inventory.Tests.LeadTimeKit;

namespace SMS.Modules.Inventory.Tests;

/// <summary>
/// A34-PC-02/03/05 (T-C4-01..05, D-11..D-14, API-CONTRACT §4.6): the calculator — the route it judges by, stock
/// awareness, the BOM-aware manufacturing total (recursion, cycles, depth), batching per level, dates and errors.
/// </summary>
public class LeadTimeCalculatorTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();
    private static DateTime Today => DateTime.UtcNow.Date;

    private sealed class World
    {
        public string Name { get; } = Guid.NewGuid().ToString();
        public FakeFulfillmentRouteLookup Routes { get; } = new();
        public FakeBomStructureReader Boms { get; } = new();
        public FakeSupplierLeadTimeLookup Records { get; } = new();

        public LeadTimeCalculator Calculator(Guid org, bool superAdmin = false, IMemoryCache? cache = null, bool withBoms = true)
        {
            var db = Db(Name, org, superAdmin);
            return new LeadTimeCalculator(db, new LeadTimeInputsLoader(db, Records), cache ?? new MemoryCache(new MemoryCacheOptions()),
                Routes, withBoms ? Boms : null);
        }
    }

    private static LeadTimeComponentResult C(LeadTimeResult r, string code) => r.Components.Single(c => c.Code == code);

    // ── STOCK routes (D-11, D-14) ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task T_C4_01_a_stock_variant_with_no_stock_adds_supplier_pick_pack_shipping_and_buffer()
    {
        var w = new World();
        var ship = w.Routes.Add(OrgA, "PICK_AND_SHIP");
        var v = await VariantAsync(Db(w.Name, OrgA), route: ship.Uuid, configure: x => x.LeadTimeDays = 6);

        var r = await w.Calculator(OrgA).CalculateAsync(OrgA, new LeadTimeRequest(v.Uuid, 5));

        r.Components.Select(c => c.Code).Should().Equal(
            LeadTimeComponentCode.Supplier, LeadTimeComponentCode.PickPack, LeadTimeComponentCode.Shipping, LeadTimeComponentCode.SalesBuffer);
        C(r, LeadTimeComponentCode.Supplier).Should().BeEquivalentTo(new { Days = 6, Source = LeadTimeSource.Variant, Name = "Supplier lead time" });
        r.Components.Select(c => c.Days).Should().Equal(6, 1, 3, 1);
        r.TotalLeadTimeDays.Should().Be(11);
        r.EarliestDeliveryDate.Should().Be(Today.AddDays(11));
        r.EarliestDeliveryDate.Kind.Should().Be(DateTimeKind.Unspecified, "date-only travels without a Z");
        r.RouteUuid.Should().Be(ship.Uuid);
        r.RouteCode.Should().Be("PICK_AND_SHIP");
        r.RouteCategory.Should().Be(FulfillmentRouteCategory.Stock);
        r.LatestStartDate.Should().BeNull();
        r.MeetsRequestedDate.Should().BeNull();
    }

    [Fact]
    public async Task D_14_enough_free_stock_in_one_warehouse_makes_the_supplier_lead_0_in_stock()
    {
        var w = new World();
        var ship = w.Routes.Add(OrgA, "PICK_AND_SHIP");
        var v = await VariantAsync(Db(w.Name, OrgA), route: ship.Uuid, configure: x => x.LeadTimeDays = 6);
        var wh = await WarehouseAsync(Db(w.Name, OrgA));
        await StockAsync(Db(w.Name, OrgA), v.Id, wh.Id, onHand: 12, reserved: 4);

        var r = await w.Calculator(OrgA).CalculateAsync(OrgA, new LeadTimeRequest(v.Uuid, 8));

        C(r, LeadTimeComponentCode.Supplier).Should().BeEquivalentTo(new { Days = 0, Source = LeadTimeSource.InStock });
        r.TotalLeadTimeDays.Should().Be(5);
    }

    [Fact]
    public async Task D_14_stock_split_over_two_warehouses_is_not_enough_when_no_single_one_covers_the_quantity()
    {
        var w = new World();
        var v = await VariantAsync(Db(w.Name, OrgA), configure: x => x.LeadTimeDays = 6);
        var wh1 = await WarehouseAsync(Db(w.Name, OrgA), "A");
        var wh2 = await WarehouseAsync(Db(w.Name, OrgA), "B");
        await StockAsync(Db(w.Name, OrgA), v.Id, wh1.Id, onHand: 4);
        await StockAsync(Db(w.Name, OrgA), v.Id, wh2.Id, onHand: 4);

        var r = await w.Calculator(OrgA).CalculateAsync(OrgA, new LeadTimeRequest(v.Uuid, 5));

        C(r, LeadTimeComponentCode.Supplier).Days.Should().Be(6);
    }

    [Fact]
    public async Task Without_any_route_it_is_judged_as_stock_without_SHIP_and_the_SHIP_default_is_used_when_set()
    {
        var w = new World();
        var v = await VariantAsync(Db(w.Name, OrgA));

        var none = await w.Calculator(OrgA).CalculateAsync(OrgA, new LeadTimeRequest(v.Uuid, 1));
        none.RouteUuid.Should().BeNull();
        none.RouteCategory.Should().Be(FulfillmentRouteCategory.Stock);
        none.Components.Select(c => c.Code).Should().NotContain(LeadTimeComponentCode.Shipping);

        var dflt = w.Routes.Add(OrgA, "PICK_AND_SHIP");
        w.Routes.SetShippingDefault(OrgA, dflt);
        var withDefault = await w.Calculator(OrgA).CalculateAsync(OrgA, new LeadTimeRequest(v.Uuid, 1));
        withDefault.RouteUuid.Should().Be(dflt.Uuid);
        C(withDefault, LeadTimeComponentCode.Shipping).Days.Should().Be(3);
    }

    [Fact]
    public async Task Optional_components_with_0_days_are_omitted_and_shown_when_set()
    {
        var w = new World();
        await DefaultsAsync(Db(w.Name, OrgA), pickPack: 0, shipping: 2, sales: 0, mfgBuffer: 0, qc: 2, transfer: 1);
        var ship = w.Routes.Add(OrgA, "PICK_AND_SHIP");
        var v = await VariantAsync(Db(w.Name, OrgA), route: ship.Uuid);

        var r = await w.Calculator(OrgA).CalculateAsync(OrgA, new LeadTimeRequest(v.Uuid, 1));

        r.Components.Select(c => c.Code).Should().Equal(
            LeadTimeComponentCode.Qc, LeadTimeComponentCode.Transfer, LeadTimeComponentCode.PickPack, LeadTimeComponentCode.Shipping);
        C(r, LeadTimeComponentCode.PickPack).Days.Should().Be(0, "PICK_PACK always shows");
        r.TotalLeadTimeDays.Should().Be(5);
    }

    // ── MANUFACTURE routes (D-12, D-13) ────────────────────────────────────────────────────────

    [Fact]
    public async Task T_C4_02_bom_components_all_in_the_production_warehouse_wait_0()
    {
        var w = new World();
        await DefaultsAsync(Db(w.Name, OrgA), pickPack: 1, shipping: 3, sales: 1, mfgBuffer: 2, qc: 1, transfer: 0);
        var mfg = w.Routes.Add(OrgA, "MFG_PICK_PACK_SHIP", category: FulfillmentRouteCategory.Manufacture);
        var plant = await WarehouseAsync(Db(w.Name, OrgA), "P");
        var fg = await VariantAsync(Db(w.Name, OrgA), SupplyMethod.Manufacture, route: mfg.Uuid, productionWarehouseId: plant.Id,
            configure: x => x.ManufacturingLeadTimeDays = 5);
        var parts = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var part = await VariantAsync(Db(w.Name, OrgA), configure: x => x.LeadTimeDays = 30);
            await StockAsync(Db(w.Name, OrgA), part.Id, plant.Id, onHand: 100);
            parts.Add(part.Uuid);
        }
        w.Boms.Add(OrgA, fg.Uuid, 1m, parts.Select(p => (p, 2m, 0m)).ToArray());

        var r = await w.Calculator(OrgA).CalculateAsync(OrgA, new LeadTimeRequest(fg.Uuid, 10));

        r.RouteCategory.Should().Be(FulfillmentRouteCategory.Manufacture);
        r.Components.Select(c => c.Code).Should().Equal(
            LeadTimeComponentCode.Manufacturing, LeadTimeComponentCode.MfgBuffer, LeadTimeComponentCode.Qc,
            LeadTimeComponentCode.PickPack, LeadTimeComponentCode.Shipping, LeadTimeComponentCode.SalesBuffer);
        C(r, LeadTimeComponentCode.Manufacturing).Should().BeEquivalentTo(new { Days = 5, Source = LeadTimeSource.Bom });
        C(r, LeadTimeComponentCode.Manufacturing).Detail.Should().Contain("BOM-0001");
        r.TotalLeadTimeDays.Should().Be(5 + 2 + 1 + 1 + 3 + 1);
        r.Components.Should().NotContain(c => c.Code == LeadTimeComponentCode.Supplier, "materials come through the BOM");
    }

    [Fact]
    public async Task T_C4_03_a_short_purchased_component_adds_its_supplier_lead_and_stock_elsewhere_does_not_count()
    {
        var w = new World();
        var mfg = w.Routes.Add(OrgA, "MFG_PICK_SHIP", category: FulfillmentRouteCategory.Manufacture);
        var plant = await WarehouseAsync(Db(w.Name, OrgA), "P");
        var store = await WarehouseAsync(Db(w.Name, OrgA), "S");
        var fg = await VariantAsync(Db(w.Name, OrgA), SupplyMethod.Manufacture, route: mfg.Uuid, productionWarehouseId: plant.Id,
            configure: x => x.ManufacturingLeadTimeDays = 5);
        var steel = await VariantAsync(Db(w.Name, OrgA), name: "Steel", configure: x => x.LeadTimeDays = 7);
        var bolt = await VariantAsync(Db(w.Name, OrgA), name: "Bolt", configure: x => x.LeadTimeDays = 3);
        await StockAsync(Db(w.Name, OrgA), steel.Id, store.Id, onHand: 1000);   // not in the production warehouse (D-13)
        await StockAsync(Db(w.Name, OrgA), steel.Id, plant.Id, onHand: 5);
        w.Boms.Add(OrgA, fg.Uuid, 1m, (steel.Uuid, 2m, 0m), (bolt.Uuid, 1m, 0m));

        var r = await w.Calculator(OrgA).CalculateAsync(OrgA, new LeadTimeRequest(fg.Uuid, 10));

        C(r, LeadTimeComponentCode.Manufacturing).Days.Should().Be(5 + 7, "level days + the longest wait (parallel inputs)");

        var tree = await w.Calculator(OrgA).CalculateManufacturingAsync(OrgA, fg.Uuid, 10);
        var steelIn = tree.Inputs.Single(i => i.VariantUuid == steel.Uuid);
        steelIn.Should().BeEquivalentTo(new
        {
            RequiredQty = 20m, FreeQty = 5m, ShortfallQty = 15m, IsManufactured = false, WaitDays = 7, Source = LeadTimeSource.Variant
        });
        tree.Inputs.Single(i => i.VariantUuid == bolt.Uuid).WaitDays.Should().Be(3);
        tree.TotalDays.Should().Be(12);
        tree.LevelDays.Should().Be(5);
        tree.LevelDaysSource.Should().Be(LeadTimeSource.Variant);
        tree.BomNumber.Should().Be("BOM-0001");
    }

    [Fact]
    public async Task T_C4_04_a_short_manufactured_sub_assembly_is_recursed_with_its_shortfall_and_scrap_counts()
    {
        var w = new World();
        var mfg = w.Routes.Add(OrgA, "MFG_PICK_SHIP", category: FulfillmentRouteCategory.Manufacture);
        var plant = await WarehouseAsync(Db(w.Name, OrgA), "P");
        var sub = await VariantAsync(Db(w.Name, OrgA), SupplyMethod.Manufacture, productLeadTimeDays: 3, productionWarehouseId: plant.Id, name: "Sub");
        var raw = await VariantAsync(Db(w.Name, OrgA), name: "Raw", configure: x => x.LeadTimeDays = 4);
        var fg = await VariantAsync(Db(w.Name, OrgA), SupplyMethod.Manufacture, route: mfg.Uuid, productionWarehouseId: plant.Id,
            configure: x => x.ManufacturingLeadTimeDays = 5, name: "FG");
        await StockAsync(Db(w.Name, OrgA), sub.Id, plant.Id, onHand: 5);
        w.Boms.Add(OrgA, fg.Uuid, 2m, (sub.Uuid, 4m, 0m));          // 2 sub per FG (base quantity 2)
        w.Boms.Add(OrgA, sub.Uuid, 1m, (raw.Uuid, 1m, 10m));        // 1 raw per sub, 10% scrap

        var tree = await w.Calculator(OrgA).CalculateManufacturingAsync(OrgA, fg.Uuid, 10);

        var subIn = tree.Inputs.Single();
        subIn.Should().BeEquivalentTo(new { RequiredQty = 20m, FreeQty = 5m, ShortfallQty = 15m, IsManufactured = true, Source = LeadTimeSource.Bom });
        subIn.Node!.Quantity.Should().Be(15m);
        subIn.Node.LevelDays.Should().Be(3);
        subIn.Node.LevelDaysSource.Should().Be(LeadTimeSource.Product);
        subIn.Node.Inputs.Single().Should().BeEquivalentTo(new { RequiredQty = 16.5m, ShortfallQty = 16.5m, WaitDays = 4 });
        subIn.Node.TotalDays.Should().Be(7);
        subIn.WaitDays.Should().Be(7);
        tree.TotalDays.Should().Be(12);
        w.Boms.Calls.Should().Be(2, "one BOM read per level: [FG], then [Sub]");

        var r = await w.Calculator(OrgA).CalculateAsync(OrgA, new LeadTimeRequest(fg.Uuid, 10));
        C(r, LeadTimeComponentCode.Manufacturing).Days.Should().Be(12);
    }

    [Fact]
    public async Task T_C4_05_a_cycle_gives_0_for_the_cycle_breaking_node_and_a_warning()
    {
        var w = new World();
        var mfg = w.Routes.Add(OrgA, "MFG_PICK_SHIP", category: FulfillmentRouteCategory.Manufacture);
        var fg = await VariantAsync(Db(w.Name, OrgA), SupplyMethod.Manufacture, route: mfg.Uuid, name: "FG",
            configure: x => x.ManufacturingLeadTimeDays = 5);
        var sub = await VariantAsync(Db(w.Name, OrgA), SupplyMethod.Manufacture, name: "Sub", configure: x => x.ManufacturingLeadTimeDays = 3);
        w.Boms.Add(OrgA, fg.Uuid, 1m, (sub.Uuid, 1m, 0m));
        w.Boms.Add(OrgA, sub.Uuid, 1m, (fg.Uuid, 1m, 0m));

        var tree = await w.Calculator(OrgA).CalculateManufacturingAsync(OrgA, fg.Uuid, 1);

        tree.TotalDays.Should().Be(8);
        var back = tree.Inputs.Single().Node!.Inputs.Single();
        back.WaitDays.Should().Be(0);
        back.Node.Should().BeNull();
        tree.Warnings.Should().ContainSingle(x => x.StartsWith("Cycle:") && x.Contains("FG"));

        var r = await w.Calculator(OrgA).CalculateAsync(OrgA, new LeadTimeRequest(fg.Uuid, 1));
        C(r, LeadTimeComponentCode.Manufacturing).Days.Should().Be(8);
        C(r, LeadTimeComponentCode.Manufacturing).Detail.Should().Contain("Cycle");
    }

    [Fact]
    public async Task Recursion_stops_at_depth_10_with_a_warning()
    {
        var w = new World();
        var chain = new List<Guid>();
        for (var i = 0; i < 12; i++)
            chain.Add((await VariantAsync(Db(w.Name, OrgA), SupplyMethod.Manufacture, name: $"L{i}", configure: x => x.ManufacturingLeadTimeDays = 1)).Uuid);
        for (var i = 0; i < 11; i++)
            w.Boms.Add(OrgA, chain[i], 1m, (chain[i + 1], 1m, 0m));

        var tree = await w.Calculator(OrgA).CalculateManufacturingAsync(OrgA, chain[0], 1);

        tree.TotalDays.Should().Be(10, "levels 0..9");
        tree.Warnings.Should().Contain(x => x.StartsWith("Depth limit 10 reached"));
        w.Boms.Calls.Should().Be(10);
    }

    [Fact]
    public async Task A_manufactured_variant_without_an_active_BOM_counts_its_level_days_with_a_warning()
    {
        var w = new World();
        var mfg = w.Routes.Add(OrgA, "MFG_PICK_SHIP", category: FulfillmentRouteCategory.Manufacture);
        var fg = await VariantAsync(Db(w.Name, OrgA), SupplyMethod.Manufacture, route: mfg.Uuid, name: "FG");

        var tree = await w.Calculator(OrgA).CalculateManufacturingAsync(OrgA, fg.Uuid, 1);
        tree.TotalDays.Should().Be(1);
        tree.LevelDaysSource.Should().Be(LeadTimeSource.SystemDefault);
        tree.BomUuid.Should().BeNull();
        tree.Warnings.Should().Contain(x => x.StartsWith("No active BOM for"));

        var r = await w.Calculator(OrgA).CalculateAsync(OrgA, new LeadTimeRequest(fg.Uuid, 1));
        C(r, LeadTimeComponentCode.Manufacturing).Should().BeEquivalentTo(new { Days = 1, Source = LeadTimeSource.SystemDefault });
        C(r, LeadTimeComponentCode.Manufacturing).Detail.Should().Contain("No active BOM");
    }

    [Fact]
    public async Task Inputs_of_one_level_are_read_in_one_batch()
    {
        var w = new World();
        var a = await VariantAsync(Db(w.Name, OrgA), SupplyMethod.Manufacture, name: "A");
        var b = await VariantAsync(Db(w.Name, OrgA), SupplyMethod.Manufacture, name: "B");
        var root = await VariantAsync(Db(w.Name, OrgA), SupplyMethod.Manufacture, name: "Root");
        var raw = await VariantAsync(Db(w.Name, OrgA), name: "Raw", configure: x => x.DefaultSupplierId = Guid.NewGuid());
        w.Boms.Add(OrgA, root.Uuid, 1m, (a.Uuid, 1m, 0m), (b.Uuid, 1m, 0m));
        w.Boms.Add(OrgA, a.Uuid, 1m, (raw.Uuid, 1m, 0m));
        w.Boms.Add(OrgA, b.Uuid, 1m, (raw.Uuid, 1m, 0m));

        await w.Calculator(OrgA).CalculateManufacturingAsync(OrgA, root.Uuid, 1);

        w.Boms.Requests.Select(r => r.Count).Should().Equal(1, 2);
        w.Records.Calls.Should().Be(1, "one supplier-record read for the whole level");
    }

    // ── Route, dates, errors ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_route_in_the_request_wins_over_the_variants_route()
    {
        var w = new World();
        var mfg = w.Routes.Add(OrgA, "MFG_PICK_SHIP", category: FulfillmentRouteCategory.Manufacture);
        var pickOnly = w.Routes.Add(OrgA, "PICK_ONLY");
        var v = await VariantAsync(Db(w.Name, OrgA), SupplyMethod.Manufacture, route: mfg.Uuid, configure: x => x.LeadTimeDays = 2);

        var r = await w.Calculator(OrgA).CalculateAsync(OrgA, new LeadTimeRequest(v.Uuid, 1, pickOnly.Uuid));

        r.RouteCode.Should().Be("PICK_ONLY");
        r.RouteCategory.Should().Be(FulfillmentRouteCategory.Stock);
        r.Components.Select(c => c.Code).Should().Equal(LeadTimeComponentCode.Supplier, LeadTimeComponentCode.PickPack, LeadTimeComponentCode.SalesBuffer);
    }

    [Fact]
    public async Task A_requested_date_gives_the_latest_start_and_whether_it_is_met()
    {
        var w = new World();
        var ship = w.Routes.Add(OrgA, "PICK_AND_SHIP");
        var v = await VariantAsync(Db(w.Name, OrgA), route: ship.Uuid, configure: x => x.LeadTimeDays = 6);   // total 11

        var late = await w.Calculator(OrgA).CalculateAsync(OrgA, new LeadTimeRequest(v.Uuid, 1, RequestedDate: Today.AddDays(20)));
        late.LatestStartDate.Should().Be(Today.AddDays(9));
        late.MeetsRequestedDate.Should().BeTrue();

        var early = await w.Calculator(OrgA).CalculateAsync(OrgA, new LeadTimeRequest(v.Uuid, 1, RequestedDate: Today.AddDays(5)));
        early.LatestStartDate.Should().Be(Today.AddDays(-6));
        early.MeetsRequestedDate.Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public async Task A_quantity_of_0_or_less_is_a_400(int quantity)
    {
        var w = new World();
        var v = await VariantAsync(Db(w.Name, OrgA));

        (await FluentActions.Awaiting(() => w.Calculator(OrgA).CalculateAsync(OrgA, new LeadTimeRequest(v.Uuid, quantity)))
            .Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Be("Quantity must be greater than zero.");
        await FluentActions.Awaiting(() => w.Calculator(OrgA).CalculateManufacturingAsync(OrgA, v.Uuid, quantity))
            .Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task An_unknown_or_another_orgs_route_is_a_400()
    {
        var w = new World();
        var foreign = w.Routes.Add(OrgB, "PICK_AND_SHIP");
        var v = await VariantAsync(Db(w.Name, OrgA));

        foreach (var route in new[] { foreign.Uuid, Guid.NewGuid() })
            (await FluentActions.Awaiting(() => w.Calculator(OrgA).CalculateAsync(OrgA, new LeadTimeRequest(v.Uuid, 1, route)))
                .Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Be("Route not found in your organization.");
    }

    [Fact]
    public async Task Another_orgs_variant_is_a_404_super_admin_included()
    {
        var w = new World();
        var v = await VariantAsync(Db(w.Name, OrgA), SupplyMethod.Manufacture);

        await FluentActions.Awaiting(() => w.Calculator(OrgB, superAdmin: true).CalculateAsync(OrgB, new LeadTimeRequest(v.Uuid, 1)))
            .Should().ThrowAsync<NotFoundException>();
        await FluentActions.Awaiting(() => w.Calculator(OrgB, superAdmin: true).CalculateManufacturingAsync(OrgB, v.Uuid, 1))
            .Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Calculate_manufacturing_refuses_a_product_that_is_not_manufactured()
    {
        var w = new World();
        var v = await VariantAsync(Db(w.Name, OrgA), SupplyMethod.Purchase, name: "Bought");

        (await FluentActions.Awaiting(() => w.Calculator(OrgA).CalculateManufacturingAsync(OrgA, v.Uuid, 1))
            .Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Contain("MANUFACTURE");
    }

    [Fact]
    public async Task Without_a_BOM_reader_in_the_host_only_the_level_days_count_with_a_warning()
    {
        var w = new World();
        var fg = await VariantAsync(Db(w.Name, OrgA), SupplyMethod.Manufacture, configure: x => x.ManufacturingLeadTimeDays = 4);

        var tree = await w.Calculator(OrgA, withBoms: false).CalculateManufacturingAsync(OrgA, fg.Uuid, 1);

        tree.TotalDays.Should().Be(4);
        tree.Warnings.Should().NotBeEmpty();
    }
}

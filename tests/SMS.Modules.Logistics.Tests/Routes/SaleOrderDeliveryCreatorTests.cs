using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Logistics.Tests.Routes;

/// <summary>
/// A33 PD-03 / T-C4-01..05 — the DRAFT deliveries a confirmed sale order gets, one per route × ship-from warehouse
/// (C-4), each stamped with its route's snapshot (D-10), its mode from the route (D-4), and only for what is still
/// outstanding, so any second call (retry, the D-12 sweep, the recovery button) creates nothing more.
/// </summary>
public class SaleOrderDeliveryCreatorTests
{
    private const int User = 42;

    internal sealed class Harness
    {
        public required Guid OrgId;
        public required string DbName;
        public required LogisticsDbContext Db;
        public required DemandDbContext Demand;
        public required FakeVariantResolver Variants;
        public required FakeStockReservationService Reservations;
        public required SaleOrderDeliveryCreator Creator;
        public required IReadOnlyDictionary<string, Guid> Routes;
    }

    internal static async Task<Harness> NewHarness(Guid? orgId = null, string? dbName = null, bool superAdmin = false)
    {
        var org    = orgId ?? Guid.NewGuid();
        dbName   ??= Guid.NewGuid().ToString();
        var tenant = new StaticTenantContext { OrganizationId = org, IsSuperAdmin = superAdmin };

        var db     = LogisticsTestDb.Open(dbName, tenant);
        var demand = LogisticsTestDb.Demand(dbName, tenant);

        await new FulfillmentRouteSeeder(db).EnsureSeededAsync(org);
        // A custom route with every step, for the full-chain tests.
        db.FulfillmentRoutes.Add(new FulfillmentRoute
        {
            UUID = Guid.NewGuid(), OrganizationId = org, Code = "FULL", Name = "Full chain", IsActive = true,
            RequiresPacking = true, RequiresShipping = true, DisplayOrder = 40, CreatedDate = DateTime.UtcNow,
            Steps =
            {
                new FulfillmentRouteStep { OrganizationId = org, StepCode = "PICK",        StepOrder = 1 },
                new FulfillmentRouteStep { OrganizationId = org, StepCode = "PACK",        StepOrder = 2 },
                new FulfillmentRouteStep { OrganizationId = org, StepCode = "STAGE",       StepOrder = 3 },
                new FulfillmentRouteStep { OrganizationId = org, StepCode = "APPROVAL",    StepOrder = 4 },
                new FulfillmentRouteStep { OrganizationId = org, StepCode = "GOODS_ISSUE", StepOrder = 5 },
                new FulfillmentRouteStep { OrganizationId = org, StepCode = "SHIP",        StepOrder = 6 },
            }
        });
        await db.SaveChangesAsync();
        var routes = await db.FulfillmentRoutes.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.OrganizationId == org).ToDictionaryAsync(r => r.Code, r => r.UUID);
        db.ChangeTracker.Clear();

        var variants     = new FakeVariantResolver();
        var reservations = new FakeStockReservationService();
        var creator = new SaleOrderDeliveryCreator(
            db, demand, new DocumentNumberGenerator(db, tenant), variants, reservations, new FulfillmentRouteLookup(db));

        return new Harness
        {
            OrgId = org, DbName = dbName, Db = db, Demand = demand, Variants = variants, Reservations = reservations,
            Creator = creator, Routes = routes
        };
    }

    internal sealed record L(
        string Route, decimal Qty = 10m, decimal Fulfilled = 0m, string Status = "RESERVED", string Mode = "IN_STOCK",
        Guid? Variant = null);

    internal static async Task<(SaleOrder Order, List<SaleOrderLine> Lines)> SeedOrder(
        Harness h, string status = "CONFIRMED", bool withAddress = true, params L[] lines)
    {
        Guid? addressUuid = null;
        if (withAddress)
        {
            var address = new Address
            {
                UUID = Guid.NewGuid(), OrganizationId = h.OrgId, Line1 = "Plot 12, Korangi", CityName = "Karachi",
                CountryName = "Pakistan", CreatedBy = 1, CreatedDate = DateTime.UtcNow
            };
            h.Db.Addresses.Add(address);
            await h.Db.SaveChangesAsync();
            h.Db.ChangeTracker.Clear();
            addressUuid = address.UUID;
        }

        var order = new SaleOrder
        {
            UUID = Guid.NewGuid(), OrganizationId = h.OrgId, TraceId = Guid.NewGuid(), SoNumber = "SO-2026-01085",
            PartnerId = Guid.NewGuid(), OrderDate = new DateTime(2026, 10, 1), ExpectedDeliveryDate = new DateTime(2026, 10, 9),
            CurrencyId = Guid.NewGuid(), Status = status, DeliveryMode = "SHIP", ShippingAddressId = addressUuid, CreatedBy = 1
        };
        var n = 0;
        foreach (var l in lines)
            order.Lines.Add(new SaleOrderLine
            {
                OrganizationId = h.OrgId,
                VariantUuid = l.Variant ?? h.Variants.AddVariant($"SKU-{++n}", $"Item {n}"),
                Quantity = l.Qty, FulfilledQty = l.Fulfilled, UnitPrice = 40m, LineTotal = l.Qty * 40m,
                Status = l.Status, FulfillmentMode = l.Mode,
                FulfillmentRouteUuid = h.Routes[l.Route], FulfillmentRouteCode = l.Route, RouteSource = "VARIANT"
            });
        h.Demand.SaleOrders.Add(order);
        await h.Demand.SaveChangesAsync();
        h.Demand.ChangeTracker.Clear();
        return (order, order.Lines.OrderBy(l => l.Id).ToList());
    }

    internal static IReadOnlyList<SaleOrderLineRoute> RoutesOf(IEnumerable<SaleOrderLine> lines) =>
        [.. lines.Select(l => new SaleOrderLineRoute(l.UUID, l.FulfillmentRouteUuid!.Value))];

    internal static Task<List<DeliveryOrder>> Deliveries(Harness h, Guid soUuid) =>
        h.Db.DeliveryOrders.IgnoreQueryFilters().AsNoTracking().Include(d => d.Lines)
            .Where(d => d.SaleOrderUuid == soUuid).OrderBy(d => d.Id).ToListAsync();

    private static async Task Hold(Harness h, SaleOrder so, SaleOrderLine line, Guid warehouse, decimal qty, string name = "Karachi Main")
    {
        h.Reservations.SetLayout(line.VariantUuid, (new FakeStockReservationService.StockLocation(warehouse, name), qty));
        (await h.Reservations.ReserveAsync(ReservationSourceType.SalesOrder, so.UUID,
            [new ReservationRequest(line.VariantUuid, warehouse, qty, line.UUID)], User)).Succeeded.Should().BeTrue();
    }

    // ── Grouping (T-C4-01..03) ───────────────────────────────────────────────

    [Fact]
    public async Task T_C4_01_three_lines_on_one_route_make_one_delivery_with_three_lines()
    {
        var h = await NewHarness();
        var (so, lines) = await SeedOrder(h, lines: [new L("PICK_AND_SHIP"), new L("PICK_AND_SHIP"), new L("PICK_AND_SHIP")]);

        var result = await h.Creator.CreateForConfirmedOrderAsync(h.OrgId, so.UUID, RoutesOf(lines), User);

        result.Created.Should().ContainSingle().Which.LineCount.Should().Be(3);
        result.Skipped.Should().BeEmpty();
        (await Deliveries(h, so.UUID)).Single().Lines.Should().HaveCount(3);
    }

    [Fact]
    public async Task T_C4_02_three_routes_make_three_deliveries_of_one_line_each()
    {
        var h = await NewHarness();
        var (so, lines) = await SeedOrder(h, lines: [new L("PICK_ONLY"), new L("PICK_PACK_SHIP"), new L("PICK_AND_SHIP")]);

        var result = await h.Creator.CreateForConfirmedOrderAsync(h.OrgId, so.UUID, RoutesOf(lines), User);

        result.Created.Select(c => c.RouteCode).Should().BeEquivalentTo(["PICK_ONLY", "PICK_PACK_SHIP", "PICK_AND_SHIP"]);
        result.Created.Should().OnlyContain(c => c.LineCount == 1);
        (await Deliveries(h, so.UUID)).Should().HaveCount(3);
    }

    [Fact]
    public async Task T_C4_03_two_on_one_route_and_one_on_another_make_two_deliveries()
    {
        var h = await NewHarness();
        var (so, lines) = await SeedOrder(h, lines: [new L("PICK_AND_SHIP"), new L("PICK_PACK_SHIP"), new L("PICK_AND_SHIP")]);

        var result = await h.Creator.CreateForConfirmedOrderAsync(h.OrgId, so.UUID, RoutesOf(lines), User);

        result.Created.Should().HaveCount(2);
        result.Created.Single(c => c.RouteCode == "PICK_AND_SHIP").LineCount.Should().Be(2);
        result.Created.Single(c => c.RouteCode == "PICK_PACK_SHIP").LineCount.Should().Be(1);
    }

    // ── What each delivery carries (T-C4-04/05, D-4, D-10, BR-C4-03) ─────────

    [Fact]
    public async Task T_C4_04_05_each_delivery_is_a_sale_order_draft_whose_lines_link_back()
    {
        var h = await NewHarness();
        var (so, lines) = await SeedOrder(h, lines: [new L("PICK_PACK_SHIP", Qty: 600m), new L("PICK_PACK_SHIP", Qty: 100m)]);

        await h.Creator.CreateForConfirmedOrderAsync(h.OrgId, so.UUID, RoutesOf(lines), User);

        var d = (await Deliveries(h, so.UUID)).Single();
        d.SourceType.Should().Be("SALE_ORDER");
        d.SourceUuid.Should().Be(so.UUID);
        d.SourceNumber.Should().Be("SO-2026-01085");
        d.SaleOrderUuid.Should().Be(so.UUID);
        d.TraceId.Should().Be(so.TraceId);
        d.Direction.Should().Be("OUTBOUND");
        d.Status.Should().Be("DRAFT", "BR-C4-03: a person releases it for picking");
        d.OrganizationId.Should().Be(h.OrgId);
        d.RequestedDate.Should().Be(new DateTime(2026, 10, 9));
        d.DeliveryNumber.Should().MatchRegex(@"^DLV-\d{4}-\d{5}$");
        d.Lines.OrderBy(l => l.LineNo).Select(l => l.SoLineUuid).Should().Equal(lines[0].UUID, lines[1].UUID);
        d.Lines.Select(l => l.SourceLineUuid).Should().BeEquivalentTo(lines.Select(l => (Guid?)l.UUID));
        d.Lines.Select(l => l.QtyOrdered).Should().BeEquivalentTo([600m, 100m]);
        d.Lines.Should().OnlyContain(l => l.OrganizationId == h.OrgId && l.UnitValue == 40m);
    }

    [Fact]
    public async Task The_route_is_snapshotted_and_decides_the_mode()
    {
        var h = await NewHarness();
        var (so, lines) = await SeedOrder(h, lines: [new L("PICK_ONLY"), new L("PICK_PACK_SHIP")]);

        var result = await h.Creator.CreateForConfirmedOrderAsync(h.OrgId, so.UUID, RoutesOf(lines), User);

        var deliveries = await Deliveries(h, so.UUID);
        var collect = deliveries.Single(d => d.FulfillmentRouteCode == "PICK_ONLY");
        collect.FulfillmentRouteUuid.Should().Be(h.Routes["PICK_ONLY"]);
        collect.RouteSteps.Should().Be("PICK,GOODS_ISSUE");
        collect.DeliveryMode.Should().Be("SELF_PICKUP", "D-4: a route without SHIP is collected, whatever the header says");
        collect.ShipToAddressId.Should().BeNull();

        var shipped = deliveries.Single(d => d.FulfillmentRouteCode == "PICK_PACK_SHIP");
        shipped.RouteSteps.Should().Be("PICK,PACK,GOODS_ISSUE,SHIP");
        shipped.DeliveryMode.Should().Be("SHIP");
        shipped.ShipToAddressId.Should().NotBeNull("a shipped delivery goes to the order's own address row");

        result.Created.Single(c => c.RouteCode == "PICK_ONLY").DeliveryMode.Should().Be("SELF_PICKUP");
    }

    // ── Idempotent, outstanding only (D-12, recovery) ────────────────────────

    [Fact]
    public async Task A_second_call_creates_nothing_and_says_why()
    {
        var h = await NewHarness();
        var (so, lines) = await SeedOrder(h, lines: [new L("PICK_AND_SHIP"), new L("PICK_ONLY")]);
        await h.Creator.CreateForConfirmedOrderAsync(h.OrgId, so.UUID, RoutesOf(lines), User);

        var again = await h.Creator.CreateForConfirmedOrderAsync(h.OrgId, so.UUID, RoutesOf(lines), User);

        again.Created.Should().BeEmpty();
        again.Skipped.Select(s => s.SoLineUuid).Should().BeEquivalentTo(lines.Select(l => l.UUID));
        again.Skipped.Should().OnlyContain(s => s.Reason.Contains("already on a delivery"));
        (await Deliveries(h, so.UUID)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Only_what_is_outstanding_is_put_on_a_delivery()
    {
        var h = await NewHarness();
        var (so, lines) = await SeedOrder(h, status: "PARTIALLY_FULFILLED",
            lines: [new L("PICK_AND_SHIP", Qty: 100m, Fulfilled: 60m, Status: "PARTIALLY_FULFILLED"),
                    new L("PICK_AND_SHIP", Qty: 5m, Fulfilled: 5m, Status: "FULFILLED")]);

        var result = await h.Creator.CreateForConfirmedOrderAsync(h.OrgId, so.UUID, RoutesOf(lines), User);

        (await Deliveries(h, so.UUID)).Single().Lines.Single().QtyOrdered.Should().Be(40m);
        result.Skipped.Should().ContainSingle(s => s.SoLineUuid == lines[1].UUID);
    }

    [Fact]
    public async Task A_cancelled_delivery_gives_its_quantity_back_to_a_later_call()
    {
        var h = await NewHarness();
        var (so, lines) = await SeedOrder(h, lines: [new L("PICK_AND_SHIP")]);
        await h.Creator.CreateForConfirmedOrderAsync(h.OrgId, so.UUID, RoutesOf(lines), User);
        var first = await h.Db.DeliveryOrders.SingleAsync();
        first.Status = "CANCELLED";
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var result = await h.Creator.CreateForConfirmedOrderAsync(h.OrgId, so.UUID, RoutesOf(lines), User);

        result.Created.Should().ContainSingle();
    }

    // ── Which lines (D-5) ────────────────────────────────────────────────────

    [Fact]
    public async Task Drop_ship_cancelled_unknown_and_unlisted_lines_get_no_delivery()
    {
        var h = await NewHarness();
        var (so, lines) = await SeedOrder(h,
            lines: [new L("PICK_AND_SHIP"), new L("PICK_AND_SHIP", Mode: "DROP_SHIP", Status: "OPEN"),
                    new L("PICK_AND_SHIP", Status: "CANCELLED"), new L("PICK_AND_SHIP")]);
        var stranger = Guid.NewGuid();
        IReadOnlyList<SaleOrderLineRoute> asked =
            [.. RoutesOf(lines.Take(3)), new SaleOrderLineRoute(stranger, h.Routes["PICK_AND_SHIP"])];

        var result = await h.Creator.CreateForConfirmedOrderAsync(h.OrgId, so.UUID, asked, User);

        result.Created.Single().LineCount.Should().Be(1, "line 4 was not listed; lines 2 and 3 cannot have one");
        result.Skipped.Select(s => s.SoLineUuid).Should().BeEquivalentTo([lines[1].UUID, lines[2].UUID, stranger]);
    }

    [Fact]
    public async Task A_route_the_organization_does_not_have_is_skipped_by_name()
    {
        var h = await NewHarness();
        var (so, lines) = await SeedOrder(h, lines: [new L("PICK_AND_SHIP")]);

        var result = await h.Creator.CreateForConfirmedOrderAsync(
            h.OrgId, so.UUID, [new SaleOrderLineRoute(lines[0].UUID, Guid.NewGuid())], User);

        result.Created.Should().BeEmpty();
        result.Skipped.Single().Reason.Should().Contain("route");
    }

    [Fact]
    public async Task A_shipping_route_with_no_shipping_address_skips_those_lines_only()
    {
        var h = await NewHarness();
        var (so, lines) = await SeedOrder(h, withAddress: false, lines: [new L("PICK_AND_SHIP"), new L("PICK_ONLY")]);

        var result = await h.Creator.CreateForConfirmedOrderAsync(h.OrgId, so.UUID, RoutesOf(lines), User);

        result.Created.Single().RouteCode.Should().Be("PICK_ONLY");
        result.Skipped.Single().Reason.Should().Contain("shipping address");
    }

    // ── Which orders ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("CANCELLED")]
    [InlineData("FULFILLED")]
    public async Task An_order_that_is_not_confirmed_gets_nothing(string status)
    {
        var h = await NewHarness();
        var (so, lines) = await SeedOrder(h, status: status, lines: [new L("PICK_AND_SHIP")]);

        var act = () => h.Creator.CreateForConfirmedOrderAsync(h.OrgId, so.UUID, RoutesOf(lines), User);

        await act.Should().ThrowAsync<BadRequestException>();
        (await Deliveries(h, so.UUID)).Should().BeEmpty();
    }

    [Fact]
    public async Task Another_organizations_order_is_not_found_whoever_asks()
    {
        var a = await NewHarness();
        var (so, lines) = await SeedOrder(a, lines: [new L("PICK_AND_SHIP")]);

        // Org B's super admin, same database: the EF filter is off, the explicit organization is not.
        var b = await NewHarness(dbName: a.DbName, superAdmin: true);
        var act = () => b.Creator.CreateForConfirmedOrderAsync(b.OrgId, so.UUID, RoutesOf(lines), User);

        await act.Should().ThrowAsync<NotFoundException>();
        (await Deliveries(a, so.UUID)).Should().BeEmpty();
    }

    [Fact]
    public async Task Run_with_no_user_tenant_it_stamps_and_numbers_under_the_given_organization()
    {
        // The D-12 sweep runs from Hangfire: the ambient tenant is empty.
        var h = await NewHarness();
        var (so, lines) = await SeedOrder(h, lines: [new L("PICK_AND_SHIP")]);
        var nobody = new StaticTenantContext { OrganizationId = Guid.Empty };
        var db     = LogisticsTestDb.Open(h.DbName, nobody);
        var creator = new SaleOrderDeliveryCreator(
            db, LogisticsTestDb.Demand(h.DbName, nobody), new DocumentNumberGenerator(db, nobody), h.Variants,
            h.Reservations, new FulfillmentRouteLookup(db));

        await creator.CreateForConfirmedOrderAsync(h.OrgId, so.UUID, RoutesOf(lines), User);

        var d = (await Deliveries(h, so.UUID)).Single();
        d.OrganizationId.Should().Be(h.OrgId);
        d.Lines.Should().OnlyContain(l => l.OrganizationId == h.OrgId);
        (await db.DocumentNumberSequences.IgnoreQueryFilters().SingleAsync(s => s.Prefix == "DLV"))
            .OrganizationId.Should().Be(h.OrgId);
    }

    // ── Partial fulfilment off (D-8) and warehouses (C-4) ────────────────────

    [Fact]
    public async Task Route_splitting_is_not_partial_fulfilment()
    {
        var h = await NewHarness();
        h.Demand.SaleOrderConfigs.Add(new SaleOrderConfig { OrganizationId = h.OrgId, PartialFulfillmentAllowed = false });
        await h.Demand.SaveChangesAsync();
        var (so, lines) = await SeedOrder(h, lines: [new L("PICK_ONLY"), new L("PICK_PACK_SHIP")]);

        var result = await h.Creator.CreateForConfirmedOrderAsync(h.OrgId, so.UUID, RoutesOf(lines), User);

        result.Created.Should().HaveCount(2);
    }

    [Fact]
    public async Task Lines_held_in_different_warehouses_go_on_separate_deliveries_of_the_same_route()
    {
        var h = await NewHarness();
        var (so, lines) = await SeedOrder(h, lines: [new L("PICK_AND_SHIP"), new L("PICK_AND_SHIP"), new L("PICK_AND_SHIP")]);
        Guid khi = Guid.NewGuid(), lhr = Guid.NewGuid();
        await Hold(h, so, lines[0], khi, 10m);
        await Hold(h, so, lines[1], lhr, 10m, "Lahore Depot");
        await Hold(h, so, lines[2], khi, 10m);

        var result = await h.Creator.CreateForConfirmedOrderAsync(h.OrgId, so.UUID, RoutesOf(lines), User);

        result.Created.Should().HaveCount(2);
        result.Created.Single(c => c.ShipFromWarehouseUuid == khi).LineCount.Should().Be(2);
        result.Created.Single(c => c.ShipFromWarehouseUuid == lhr).LineCount.Should().Be(1);
    }

    [Fact]
    public async Task A_line_with_nothing_held_yet_leaves_the_warehouse_to_release()
    {
        var h = await NewHarness();
        var (so, lines) = await SeedOrder(h, lines: [new L("PICK_AND_SHIP", Mode: "BACK_TO_BACK", Status: "OPEN")]);

        var result = await h.Creator.CreateForConfirmedOrderAsync(h.OrgId, so.UUID, RoutesOf(lines), User);

        result.Created.Single().ShipFromWarehouseUuid.Should().BeNull();
    }

    [Fact]
    public async Task A_line_held_partly_in_two_warehouses_is_split_between_them()
    {
        var h = await NewHarness();
        var (so, lines) = await SeedOrder(h, lines: [new L("PICK_AND_SHIP", Qty: 10m)]);
        Guid khi = Guid.NewGuid(), lhr = Guid.NewGuid();
        h.Reservations.SetLayout(lines[0].VariantUuid,
            (new FakeStockReservationService.StockLocation(khi, "Karachi Main"), 6m),
            (new FakeStockReservationService.StockLocation(lhr, "Lahore Depot"), 3m));
        (await h.Reservations.ReserveAsync(ReservationSourceType.SalesOrder, so.UUID,
            [new ReservationRequest(lines[0].VariantUuid, null, 9m, lines[0].UUID)], User)).Succeeded.Should().BeTrue();

        await h.Creator.CreateForConfirmedOrderAsync(h.OrgId, so.UUID, RoutesOf(lines), User);

        var deliveries = await Deliveries(h, so.UUID);
        deliveries.Single(d => d.ShipFromWarehouseUuid == khi).Lines.Single().QtyOrdered
            .Should().Be(7m, "its own 6 plus the 1 nothing holds yet, with the larger hold");
        deliveries.Single(d => d.ShipFromWarehouseUuid == lhr).Lines.Single().QtyOrdered.Should().Be(3m);
    }
}

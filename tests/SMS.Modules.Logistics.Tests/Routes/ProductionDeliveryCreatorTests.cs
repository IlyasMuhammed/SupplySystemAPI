using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Common;
using Xunit;
using static SMS.Modules.Logistics.Tests.Routes.SaleOrderDeliveryCreatorTests;

namespace SMS.Modules.Logistics.Tests.Routes;

/// <summary>
/// A34 PE-04 (D-20, D-21, D-29) — the DRAFT delivery a completed make-to-order production order gets: the D-20 quantity
/// formula (idempotent through <c>delivery_orders.ProductionOrderUuid</c>), the PO's route snapshot even when deactivated,
/// ship-from per D-29, own organization only, and every business outcome as a result rather than an exception (REV-05).
/// T-C6-01, T-C6-05..07. The lock itself is shown on SQL Server in <see cref="ProductionDeliveryCreatorSqlServerTests"/>.
/// </summary>
public class ProductionDeliveryCreatorTests
{
    private const int User = 42;
    private static readonly Guid ProductionWarehouse = Guid.NewGuid();

    private static ProductionDeliveryCreator Creator(Harness h) => new(
        h.Db, h.Demand, new DocumentNumberGenerator(h.Db, new StaticTenantContext { OrganizationId = h.OrgId }),
        h.Variants, h.Reservations, new FulfillmentRouteLookup(h.Db));

    private static ProductionDeliveryRequest Request(
        Harness h, SaleOrder so, SaleOrderLine line, decimal accepted, Guid? po = null, string route = "MFG_PICK_SHIP") =>
        new(po ?? PoUuid, "PROD-2026-00007", so.UUID, line.UUID, h.Routes[route], accepted, ProductionWarehouse);

    private static readonly Guid PoUuid = Guid.NewGuid();

    private static L Mto(decimal qty = 10m, decimal fulfilled = 0m, string route = "MFG_PICK_SHIP") =>
        new(route, Qty: qty, Fulfilled: fulfilled, Status: "OPEN", Mode: "MAKE_TO_ORDER");

    private static async Task SetLine(Harness h, Guid lineUuid, Action<SaleOrderLine> change)
    {
        var line = await h.Demand.SaleOrderLines.IgnoreQueryFilters().SingleAsync(l => l.UUID == lineUuid);
        change(line);
        await h.Demand.SaveChangesAsync();
        h.Demand.ChangeTracker.Clear();
    }

    // ── T-C6-01 / T-C6-05..07 ────────────────────────────────────────────────

    [Fact]
    public async Task T_C6_01_a_completed_po_gets_one_draft_delivery_for_the_accepted_quantity_on_its_route()
    {
        var h = await NewHarness();
        var (so, lines) = await SeedOrder(h, lines: [new L("PICK_AND_SHIP"), Mto(qty: 100m)]);
        await SetLine(h, lines[1].UUID, l => l.ManualDeliveryDate = new DateTime(2026, 11, 20));

        var result = await Creator(h).CreateForProductionOrderAsync(h.OrgId, Request(h, so, lines[1], 100m), User);

        result.QuantityCreated.Should().Be(100m);
        result.SkippedReason.Should().BeNull();
        result.Delivery!.RouteCode.Should().Be("MFG_PICK_SHIP");
        result.Delivery.DeliveryMode.Should().Be("SHIP");
        result.Delivery.LineCount.Should().Be(1);
        result.Deliveries.Should().ContainSingle();
        result.LatestDelivery!.DeliveryUuid.Should().Be(result.Delivery.DeliveryUuid);

        var d = (await Deliveries(h, so.UUID)).Single();
        d.UUID.Should().Be(result.Delivery.DeliveryUuid);
        d.Status.Should().Be("DRAFT", "BR-C6-07");
        d.SourceType.Should().Be("SALE_ORDER", "T-C6-06");
        d.SourceUuid.Should().Be(so.UUID);
        d.SaleOrderUuid.Should().Be(so.UUID);
        d.TraceId.Should().Be(so.TraceId);
        d.OrganizationId.Should().Be(h.OrgId);
        d.ProductionOrderUuid.Should().Be(PoUuid, "the idempotence key and the PO card's filter");
        d.FulfillmentRouteUuid.Should().Be(h.Routes["MFG_PICK_SHIP"]);
        d.RouteSteps.Should().Be("PICK,GOODS_ISSUE,SHIP", "T-C6-05: the route's steps are snapshotted");
        d.ShipFromWarehouseUuid.Should().Be(ProductionWarehouse, "nothing holds the line yet: the PO's warehouse");
        d.ShipToAddressId.Should().NotBeNull();
        d.RequestedDate.Should().Be(new DateTime(2026, 11, 20), "the line's effective delivery date");
        d.Notes.Should().Contain("PROD-2026-00007");
        d.CreatedBy.Should().Be(User);
        d.Lines.Single().SoLineUuid.Should().Be(lines[1].UUID);
        d.Lines.Single().QtyOrdered.Should().Be(100m);
        d.Lines.Single().UnitValue.Should().Be(40m);
    }

    [Fact]
    public async Task The_requested_date_falls_back_to_the_calculated_date_then_the_header()
    {
        var h = await NewHarness();
        var (so, lines) = await SeedOrder(h, lines: [Mto(), Mto()]);
        await SetLine(h, lines[0].UUID, l => l.CalculatedDeliveryDate = new DateTime(2026, 12, 1));

        await Creator(h).CreateForProductionOrderAsync(h.OrgId, Request(h, so, lines[0], 10m, po: Guid.NewGuid()), User);
        await Creator(h).CreateForProductionOrderAsync(h.OrgId, Request(h, so, lines[1], 10m, po: Guid.NewGuid()), User);

        var deliveries = await Deliveries(h, so.UUID);
        deliveries.Single(d => d.Lines.Single().SoLineUuid == lines[0].UUID).RequestedDate.Should().Be(new DateTime(2026, 12, 1));
        deliveries.Single(d => d.Lines.Single().SoLineUuid == lines[1].UUID).RequestedDate.Should().Be(new DateTime(2026, 10, 9));
    }

    // ── D-20 formula: replay, partial then completion, outstanding cap ──────

    [Fact]
    public async Task A_replay_creates_nothing_and_still_names_the_delivery_already_made()
    {
        var h = await NewHarness();
        var (so, lines) = await SeedOrder(h, lines: [Mto()]);
        var first = await Creator(h).CreateForProductionOrderAsync(h.OrgId, Request(h, so, lines[0], 10m), User);

        var replay = await Creator(h).CreateForProductionOrderAsync(h.OrgId, Request(h, so, lines[0], 10m), User);

        replay.QuantityCreated.Should().Be(0m);
        replay.Delivery.Should().BeNull();
        replay.Deliveries.Should().BeNullOrEmpty();
        replay.SkippedReason.Should().Contain("Nothing left to deliver from PROD-2026-00007");
        replay.LatestDelivery!.DeliveryUuid.Should().Be(first.Delivery!.DeliveryUuid, "so the PO can be stamped after a lost reply");
        replay.LatestDelivery.DeliveryNumber.Should().Be(first.Delivery.DeliveryNumber);
        (await Deliveries(h, so.UUID)).Should().ContainSingle();
    }

    [Fact]
    public async Task Create_delivery_now_for_part_then_completion_delivers_the_rest_and_never_more()
    {
        var h = await NewHarness();
        var (so, lines) = await SeedOrder(h, lines: [Mto(qty: 10m)]);

        (await Creator(h).CreateForProductionOrderAsync(h.OrgId, Request(h, so, lines[0], 4m), User)).QuantityCreated.Should().Be(4m);
        (await Creator(h).CreateForProductionOrderAsync(h.OrgId, Request(h, so, lines[0], 10m), User)).QuantityCreated.Should().Be(6m);
        (await Creator(h).CreateForProductionOrderAsync(h.OrgId, Request(h, so, lines[0], 10m), User)).QuantityCreated.Should().Be(0m);

        (await Deliveries(h, so.UUID)).SelectMany(d => d.Lines).Sum(l => l.QtyOrdered).Should().Be(10m);
    }

    [Fact]
    public async Task A_cancelled_delivery_from_the_po_no_longer_counts()
    {
        var h = await NewHarness();
        var (so, lines) = await SeedOrder(h, lines: [Mto(qty: 10m)]);
        var first = await Creator(h).CreateForProductionOrderAsync(h.OrgId, Request(h, so, lines[0], 10m), User);

        var d = await h.Db.DeliveryOrders.IgnoreQueryFilters().SingleAsync(x => x.UUID == first.Delivery!.DeliveryUuid);
        d.Status = "CANCELLED";
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var again = await Creator(h).CreateForProductionOrderAsync(h.OrgId, Request(h, so, lines[0], 10m), User);
        again.QuantityCreated.Should().Be(10m);
        again.LatestDelivery!.DeliveryUuid.Should().Be(again.Delivery!.DeliveryUuid, "the cancelled one is not 'the latest'");
    }

    [Fact]
    public async Task D_21_a_yield_shortfall_is_delivered_as_it_is_whatever_the_partial_fulfilment_setting()
    {
        var h = await NewHarness();
        var (so, lines) = await SeedOrder(h, lines: [Mto(qty: 100m)]);

        var result = await Creator(h).CreateForProductionOrderAsync(h.OrgId, Request(h, so, lines[0], 95m), User);

        result.QuantityCreated.Should().Be(95m);
        result.SkippedReason.Should().BeNull("everything accepted went on the delivery");
    }

    [Fact]
    public async Task The_line_outstanding_caps_the_quantity_and_says_so()
    {
        var h = await NewHarness();
        // 3 already delivered from stock (a D-28 manual reserve), 2 on a stock delivery in flight.
        var (so, lines) = await SeedOrder(h, lines: [Mto(qty: 10m, fulfilled: 3m)]);
        h.Db.DeliveryOrders.Add(new DeliveryOrder
        {
            UUID = Guid.NewGuid(), OrganizationId = h.OrgId, DeliveryNumber = "DLV-2026-99999", Direction = "OUTBOUND",
            SourceType = "SALE_ORDER", SourceUuid = so.UUID, SaleOrderUuid = so.UUID, Priority = "NORMAL", Status = "RELEASED",
            CreatedDate = DateTime.UtcNow,
            Lines = { new DeliveryOrderLine { UUID = Guid.NewGuid(), OrganizationId = h.OrgId, LineNo = 1,
                VariantUuid = lines[0].VariantUuid, QtyOrdered = 2m, SoLineUuid = lines[0].UUID, CreatedDate = DateTime.UtcNow } }
        });
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var result = await Creator(h).CreateForProductionOrderAsync(h.OrgId, Request(h, so, lines[0], 10m), User);

        result.QuantityCreated.Should().Be(5m, "min(10 accepted − 0 from this PO, 10 − 3 fulfilled − 2 in flight)");
        result.SkippedReason.Should().Contain("5 outstanding");
    }

    // ── D-29 ship-from: the SALES_ORDER holds, split as A33 ──────────────────

    [Fact]
    public async Task Ship_from_follows_the_lines_holds_and_splits_between_their_warehouses()
    {
        var h = await NewHarness();
        var (so, lines) = await SeedOrder(h, lines: [Mto(qty: 10m)]);
        Guid khi = Guid.NewGuid(), lhr = Guid.NewGuid();
        h.Reservations.SetLayout(lines[0].VariantUuid,
            (new FakeStockReservationService.StockLocation(khi, "Karachi Main"), 6m),
            (new FakeStockReservationService.StockLocation(lhr, "Lahore Depot"), 4m));
        (await h.Reservations.ReserveAsync(ReservationSourceType.SalesOrder, so.UUID,
            [new ReservationRequest(lines[0].VariantUuid, null, 10m, lines[0].UUID)], User)).Succeeded.Should().BeTrue();

        var result = await Creator(h).CreateForProductionOrderAsync(h.OrgId, Request(h, so, lines[0], 10m), User);

        result.QuantityCreated.Should().Be(10m);
        result.Deliveries.Should().HaveCount(2);
        var deliveries = await Deliveries(h, so.UUID);
        deliveries.Single(d => d.ShipFromWarehouseUuid == khi).Lines.Single().QtyOrdered.Should().Be(6m);
        deliveries.Single(d => d.ShipFromWarehouseUuid == lhr).Lines.Single().QtyOrdered.Should().Be(4m);
        deliveries.Should().OnlyContain(d => d.ProductionOrderUuid == PoUuid);
    }

    // ── Route snapshot (A33 D-10) ───────────────────────────────────────────

    [Fact]
    public async Task A_route_deactivated_since_the_po_was_made_is_still_the_one_snapshotted()
    {
        var h = await NewHarness();
        var (so, lines) = await SeedOrder(h, lines: [Mto(route: "MFG_PICK_PACK_SHIP")]);
        var route = await h.Db.FulfillmentRoutes.IgnoreQueryFilters().SingleAsync(r => r.UUID == h.Routes["MFG_PICK_PACK_SHIP"]);
        route.IsActive = false;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        await Creator(h).CreateForProductionOrderAsync(h.OrgId, Request(h, so, lines[0], 10m, route: "MFG_PICK_PACK_SHIP"), User);

        var d = (await Deliveries(h, so.UUID)).Single();
        d.FulfillmentRouteCode.Should().Be("MFG_PICK_PACK_SHIP");
        d.RouteSteps.Should().Be("PICK,PACK,GOODS_ISSUE,SHIP");
    }

    [Fact]
    public async Task A_route_that_no_longer_exists_gives_the_legacy_full_path_with_the_orders_mode()
    {
        var h = await NewHarness();
        var (so, lines) = await SeedOrder(h, lines: [Mto()]);
        var request = Request(h, so, lines[0], 10m) with { RouteUuid = Guid.NewGuid() };

        var result = await Creator(h).CreateForProductionOrderAsync(h.OrgId, request, User);

        result.QuantityCreated.Should().Be(10m);
        var d = (await Deliveries(h, so.UUID)).Single();
        d.FulfillmentRouteUuid.Should().BeNull("A33 R-1: no route = today's full path");
        d.RouteSteps.Should().BeNull();
        d.DeliveryMode.Should().Be("SHIP", "the order's header mode");
    }

    // ── Results, not exceptions (REV-05) — and own organization only ────────

    [Theory]
    [InlineData("CANCELLED")]
    [InlineData("DRAFT")]
    [InlineData("FULFILLED")]
    public async Task An_order_that_is_not_confirmed_any_more_gets_nothing_and_says_why(string status)
    {
        var h = await NewHarness();
        var (so, lines) = await SeedOrder(h, status: status, lines: [Mto()]);

        var result = await Creator(h).CreateForProductionOrderAsync(h.OrgId, Request(h, so, lines[0], 10m), User);

        result.QuantityCreated.Should().Be(0m);
        result.SkippedReason.Should().Contain(status);
        (await Deliveries(h, so.UUID)).Should().BeEmpty();
    }

    [Fact]
    public async Task Another_organizations_order_reads_as_absent()
    {
        var h = await NewHarness();
        var (so, lines) = await SeedOrder(h, lines: [Mto()]);

        var result = await Creator(h).CreateForProductionOrderAsync(Guid.NewGuid(), Request(h, so, lines[0], 10m), User);

        result.QuantityCreated.Should().Be(0m);
        result.SkippedReason.Should().Contain("not found");
        (await Deliveries(h, so.UUID)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_cancelled_or_unknown_line_gets_nothing()
    {
        var h = await NewHarness();
        var (so, lines) = await SeedOrder(h, lines: [Mto(), Mto()]);
        await SetLine(h, lines[0].UUID, l => l.Status = "CANCELLED");

        (await Creator(h).CreateForProductionOrderAsync(h.OrgId, Request(h, so, lines[0], 10m), User))
            .SkippedReason.Should().Contain("Line 1").And.Contain("cancelled");

        var unknown = Request(h, so, lines[1], 10m) with { SoLineUuid = Guid.NewGuid() };
        (await Creator(h).CreateForProductionOrderAsync(h.OrgId, unknown, User))
            .SkippedReason.Should().Contain("not a line of");

        (await Deliveries(h, so.UUID)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_shipping_route_without_a_shipping_address_waits_for_one()
    {
        var h = await NewHarness();
        var (so, lines) = await SeedOrder(h, withAddress: false, lines: [Mto()]);

        var result = await Creator(h).CreateForProductionOrderAsync(h.OrgId, Request(h, so, lines[0], 10m), User);

        result.QuantityCreated.Should().Be(0m);
        result.SkippedReason.Should().Contain("no shipping address");
    }

    [Fact]
    public async Task Nothing_accepted_yet_creates_nothing()
    {
        var h = await NewHarness();
        var (so, lines) = await SeedOrder(h, lines: [Mto()]);

        var result = await Creator(h).CreateForProductionOrderAsync(h.OrgId, Request(h, so, lines[0], 0m), User);

        result.QuantityCreated.Should().Be(0m);
        result.SkippedReason.Should().Contain("no accepted quantity");
    }
}

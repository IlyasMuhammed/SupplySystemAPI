using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;
using SMS.Modules.Logistics.Services;
using SMS.Modules.Material.Data;
using SMS.Modules.Warehouse.Data;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;
using static SMS.Modules.Logistics.Tests.Routes.SaleOrderDeliveryCreatorTests;

namespace SMS.Modules.Logistics.Tests.Routes;

/// <summary>
/// A33 PD-02 / PD-05 — what the API serves around route deliveries: the recovery "Create deliveries" (D-12 button),
/// the list's new filters and columns, and the detail's route and customer.
/// </summary>
public class SaleOrderDeliveryApiTests
{
    private const int User = 42;

    private static (SaleOrderDeliveryService Service, DeliveryRepository Deliveries) Wire(
        Harness h, string customer = "Punjab Group", SMS.Modules.Demand.Services.ISaleOrderService? orders = null)
    {
        var tenant = new StaticTenantContext { OrganizationId = h.OrgId };
        DbContextOptions<T> Options<T>() where T : Microsoft.EntityFrameworkCore.DbContext =>
            new DbContextOptionsBuilder<T>().UseInMemoryDatabase(h.DbName).Options;

        var names = new Mock<ISupplierNameLookupService>();
        names.Setup(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()))
             .ReturnsAsync((IReadOnlyList<Guid> ids) => ids.ToDictionary(id => id, _ => customer));

        var numbers    = new DocumentNumberGenerator(h.Db, tenant);
        var addresses  = new AddressNormalizer(new FakeCityLookup());
        var deliveries = new DeliveryRepository(h.Db, numbers, addresses, h.Demand, names.Object);
        var fromSource = new DeliveryFromSourceRepository(
            h.Db, h.Demand, new WarehouseDbContext(Options<WarehouseDbContext>(), tenant),
            new MaterialDbContext(Options<MaterialDbContext>(), tenant), numbers, addresses, h.Variants, h.Reservations);

        return (new SaleOrderDeliveryService(h.Demand, deliveries, fromSource, h.Creator, orders), deliveries);
    }

    // ── Recovery: POST api/sale-orders/{uuid}/create-deliveries ──────────────

    [Fact]
    public async Task Recovery_creates_the_route_deliveries_from_the_confirmed_snapshots_and_is_idempotent()
    {
        var h = await NewHarness();
        var (so, _) = await SeedOrder(h, lines: [new L("PICK_ONLY"), new L("PICK_PACK_SHIP"), new L("PICK_ONLY")]);
        var orders = new Mock<SMS.Modules.Demand.Services.ISaleOrderService>();
        var (service, _) = Wire(h, orders: orders.Object);

        var first = (await service.CreateRouteDeliveriesAsync(so.UUID, User))!;
        orders.Verify(o => o.MarkDeliveriesCreatedAsync(so.UUID), Times.Once,
            "REV-01: the D-12 sweep must not retry an order the recovery button has just served");
        first.Created.Select(c => c.RouteCode).Should().BeEquivalentTo(["PICK_ONLY", "PICK_PACK_SHIP"]);

        var second = (await service.CreateRouteDeliveriesAsync(so.UUID, User))!;
        second.Created.Should().BeEmpty();
        (await Deliveries(h, so.UUID)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Recovery_skips_lines_confirmed_before_routes_and_says_to_use_create_delivery()
    {
        var h = await NewHarness();
        var (so, lines) = await SeedOrder(h, lines: [new L("PICK_ONLY"), new L("PICK_AND_SHIP")]);
        var legacy = await h.Demand.SaleOrderLines.SingleAsync(l => l.UUID == lines[1].UUID);
        legacy.FulfillmentRouteUuid = null;
        legacy.FulfillmentRouteCode = null;
        legacy.RouteSource          = null;
        await h.Demand.SaveChangesAsync();
        h.Demand.ChangeTracker.Clear();

        var result = (await Wire(h).Service.CreateRouteDeliveriesAsync(so.UUID, User))!;

        result.Created.Single().RouteCode.Should().Be("PICK_ONLY");
        result.Skipped.Single().SoLineUuid.Should().Be(lines[1].UUID);
        result.Skipped.Single().Reason.Should().Contain("Create delivery");
    }

    [Fact]
    public async Task Recovery_on_an_order_that_is_not_confirmed_is_a_400()
    {
        var h = await NewHarness();
        var (so, _) = await SeedOrder(h, status: "DRAFT", lines: [new L("PICK_ONLY")]);

        var act = () => Wire(h).Service.CreateRouteDeliveriesAsync(so.UUID, User);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Recovery_on_another_organizations_order_is_a_404_for_a_super_admin_too()
    {
        var a = await NewHarness();
        var (so, _) = await SeedOrder(a, lines: [new L("PICK_ONLY")]);
        var b = await NewHarness(dbName: a.DbName, superAdmin: true);

        (await Wire(b).Service.CreateRouteDeliveriesAsync(so.UUID, User)).Should().BeNull();
        (await Deliveries(a, so.UUID)).Should().BeEmpty();
    }

    // ── List and detail (contract §6) ────────────────────────────────────────

    [Fact]
    public async Task The_list_carries_route_customer_and_a_line_summary_and_filters_by_order_and_route()
    {
        var h = await NewHarness();
        var (so, _) = await SeedOrder(h, lines: [new L("PICK_ONLY", Qty: 500m), new L("PICK_ONLY", Qty: 3m), new L("PICK_PACK_SHIP")]);
        var (other, _) = await SeedOrder(h, lines: [new L("PICK_ONLY")]);
        var (service, deliveries) = Wire(h);
        await service.CreateRouteDeliveriesAsync(so.UUID, User);
        await service.CreateRouteDeliveriesAsync(other.UUID, User);

        var bySo = await deliveries.GetListAsync(new DeliveryFilter { SaleOrderUuid = so.UUID });
        bySo.Data.Should().HaveCount(2);

        var pickOnly = (await deliveries.GetListAsync(new DeliveryFilter
            { SaleOrderUuid = so.UUID, FulfillmentRouteUuid = h.Routes["PICK_ONLY"] })).Data.Single();
        pickOnly.SaleOrderUuid.Should().Be(so.UUID);
        pickOnly.FulfillmentRouteUuid.Should().Be(h.Routes["PICK_ONLY"]);
        pickOnly.FulfillmentRouteCode.Should().Be("PICK_ONLY");
        pickOnly.FulfillmentRouteName.Should().Be("Pick Only");
        pickOnly.CustomerName.Should().Be("Punjab Group");
        pickOnly.LineSummary.Should().Be("Item 1 (SKU-1) × 500, +1 more");

        (await deliveries.GetListAsync(new DeliveryFilter { FulfillmentRouteUuid = h.Routes["PICK_ONLY"] }))
            .Data.Should().HaveCount(2, "one per order on that route");

        var forOrder = await deliveries.GetForSaleOrderAsync(so.UUID);
        forOrder.Should().OnlyContain(d => d.CustomerName == "Punjab Group" && d.FulfillmentRouteName != null);
    }

    [Fact]
    public async Task A34_the_list_filters_by_production_order_and_list_and_detail_carry_it()
    {
        var h = await NewHarness();
        var (so, _) = await SeedOrder(h, lines: [new L("PICK_ONLY"), new L("PICK_PACK_SHIP")]);
        var (service, deliveries) = Wire(h);
        var created = (await service.CreateRouteDeliveriesAsync(so.UUID, User))!.Created;

        var po = Guid.NewGuid();
        var fromPo = await h.Db.DeliveryOrders.IgnoreQueryFilters().SingleAsync(d => d.UUID == created[0].DeliveryUuid);
        fromPo.ProductionOrderUuid = po;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var list = await deliveries.GetListAsync(new DeliveryFilter { ProductionOrderUuid = po });
        list.Data.Should().ContainSingle().Which.UUID.Should().Be(created[0].DeliveryUuid);
        list.Data.Single().ProductionOrderUuid.Should().Be(po);

        (await deliveries.GetListAsync(new DeliveryFilter { ProductionOrderUuid = Guid.NewGuid() })).Data.Should().BeEmpty();
        (await deliveries.GetListAsync(new DeliveryFilter { SaleOrderUuid = so.UUID })).Data
            .Single(d => d.UUID == created[1].DeliveryUuid).ProductionOrderUuid.Should().BeNull();

        (await deliveries.GetByUuidAsync(created[0].DeliveryUuid))!.ProductionOrderUuid.Should().Be(po);
    }

    [Fact]
    public async Task The_detail_names_the_route_and_customer_and_a_non_sale_order_delivery_has_neither()
    {
        var h = await NewHarness();
        var (so, _) = await SeedOrder(h, lines: [new L("PICK_PACK_SHIP")]);
        var (service, deliveries) = Wire(h);
        var created = (await service.CreateRouteDeliveriesAsync(so.UUID, User))!.Created.Single();

        var detail = (await deliveries.GetByUuidAsync(created.DeliveryUuid))!;
        detail.FulfillmentRouteName.Should().Be("Pick, Pack & Ship");
        detail.CustomerName.Should().Be("Punjab Group");
        detail.RouteSteps.Select(s => s.Label).Should().Equal("Pick", "Pack", "Goods Issue", "Ship", "Complete");
        detail.NextStep.Should().Be("PICK");
        detail.NextActions.First().Should().Be("RELEASE");

        var manual = await deliveries.CreateAsync(new CreateDeliveryRequest
        {
            Direction = "OUTBOUND", Lines = [new CreateDeliveryLineRequest { ItemDescription = "Spare", QtyOrdered = 1m }]
        }, User);
        var plain = (await deliveries.GetByUuidAsync(manual))!;
        plain.CustomerName.Should().BeNull();
        plain.FulfillmentRouteName.Should().BeNull();
        plain.RouteSteps.Should().BeEmpty();
    }
}

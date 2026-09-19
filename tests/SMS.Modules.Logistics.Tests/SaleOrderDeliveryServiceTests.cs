using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;
using SMS.Modules.Logistics.Services;
using SMS.Modules.Material.Data;
using SMS.Modules.Warehouse.Data;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Logistics.Tests;

// A29-P6-05 §7.8 — the sale-order-facing fulfilment endpoints:
// POST /api/sale-orders/{id}/create-delivery and GET /api/sale-orders/{id}/deliveries.
public class SaleOrderDeliveryServiceTests
{
    private const int User = 42;

    private sealed record Harness(
        LogisticsDbContext Db,
        DemandDbContext Demand,
        FakeVariantResolver Variants,
        SaleOrderDeliveryService Service,
        DeliveryRepository Deliveries,
        string DbName);

    private static Harness NewHarness(Guid? organizationId = null, string? dbName = null)
    {
        dbName ??= Guid.NewGuid().ToString();
        var tenant = new StaticTenantContext { OrganizationId = organizationId ?? Guid.NewGuid() };

        DbContextOptions<T> Options<T>() where T : DbContext =>
            new DbContextOptionsBuilder<T>().UseInMemoryDatabase(dbName).Options;

        var demand    = new DemandDbContext(Options<DemandDbContext>(), tenant);
        var warehouse = new WarehouseDbContext(Options<WarehouseDbContext>(), tenant);
        var material  = new MaterialDbContext(Options<MaterialDbContext>(), tenant);
        var db        = LogisticsTestDb.Open(dbName, tenant);

        var numbers    = new DocumentNumberGenerator(db, tenant);
        var addresses  = new AddressNormalizer(new FakeCityLookup());
        var variants   = new FakeVariantResolver();
        var deliveries = new DeliveryRepository(db, numbers, addresses);
        var fromSource = new DeliveryFromSourceRepository(
            db, demand, warehouse, material, numbers, addresses, variants, new FakeStockReservationService());

        return new Harness(db, demand, variants,
            new SaleOrderDeliveryService(demand, deliveries, fromSource), deliveries, dbName);
    }

    private static async Task<SaleOrder> SeedOrder(Harness h, decimal qty = 100m, string mode = "SHIP")
    {
        var address = new Address
        {
            UUID = Guid.NewGuid(), Line1 = "Plot 12", CityName = "Karachi", CountryName = "Pakistan",
            CreatedBy = 1, CreatedDate = DateTime.UtcNow
        };
        h.Db.Addresses.Add(address);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var order = new SaleOrder
        {
            SoNumber = "SO-2026-00042", PartnerId = Guid.NewGuid(), OrderDate = DateTime.UtcNow.Date,
            CurrencyId = Guid.NewGuid(), Status = "CONFIRMED", DeliveryMode = mode,
            ShippingAddressId = address.UUID, CreatedBy = 1,
            Lines =
            {
                new SaleOrderLine
                {
                    VariantUuid = h.Variants.AddVariant("CAB-4MM", "4mm cable", uom: "M"),
                    Quantity = qty, UnitPrice = 40m, LineTotal = qty * 40m, FulfillmentMode = "IN_STOCK", Status = "RESERVED"
                }
            }
        };
        h.Demand.SaleOrders.Add(order);
        await h.Demand.SaveChangesAsync();
        h.Demand.ChangeTracker.Clear();
        return order;
    }

    // ── POST /api/sale-orders/{id}/create-delivery ────────────────────────────

    [Fact]
    public async Task An_empty_body_delivers_everything_outstanding_in_the_orders_own_mode()
    {
        var h  = NewHarness();
        var so = await SeedOrder(h, qty: 100m, mode: "SHIP");

        var uuid   = await h.Service.CreateAsync(so.UUID, null, User);
        var detail = await h.Deliveries.GetByUuidAsync(uuid);

        detail!.SourceType.Should().Be("SALE_ORDER");
        detail.SaleOrderUuid.Should().Be(so.UUID);
        detail.SourceNumber.Should().Be("SO-2026-00042");
        detail.DeliveryMode.Should().Be("SHIP");
        detail.Status.Should().Be("DRAFT");
        detail.ShipToAddress.Should().NotBeNull("a shipped order goes to its own address");
        detail.Lines.Single().QtyOrdered.Should().Be(100m);
        detail.Lines.Single().SoLineUuid.Should().Be(so.Lines.Single().UUID);
    }

    [Fact]
    public async Task The_bodys_options_reach_the_delivery()
    {
        var h  = NewHarness();
        var so = await SeedOrder(h, qty: 100m, mode: "SHIP");

        var uuid = await h.Service.CreateAsync(so.UUID, new CreateSaleOrderDeliveryRequest
        {
            DeliveryMode = "SELF_PICKUP",
            Notes        = "Customer collects Friday",
            Priority     = "HIGH",
            Lines        = [new SourceLineSelection { SourceLineUuid = so.Lines.Single().UUID, Qty = 30m }]
        }, User);
        var detail = await h.Deliveries.GetByUuidAsync(uuid);

        detail!.DeliveryMode.Should().Be("SELF_PICKUP");
        detail.ShipToAddress.Should().BeNull();
        detail.Notes.Should().Be("Customer collects Friday");
        detail.Priority.Should().Be("HIGH");
        detail.Lines.Single().QtyOrdered.Should().Be(30m);
    }

    [Fact]
    public async Task The_same_rules_as_the_generic_create_apply()
    {
        // It is the from-source create under another route — nothing outstanding means nothing raised.
        var h  = NewHarness();
        var so = await SeedOrder(h, qty: 100m);
        await h.Service.CreateAsync(so.UUID, null, User);

        var act = async () => await h.Service.CreateAsync(so.UUID, null, User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*nothing left to deliver*");
    }

    [Fact]
    public async Task An_unknown_order_cannot_have_a_delivery_raised()
    {
        var h = NewHarness();

        var act = async () => await h.Service.CreateAsync(Guid.NewGuid(), null, User);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    // ── GET /api/sale-orders/{id}/deliveries ──────────────────────────────────

    [Fact]
    public async Task Lists_the_orders_deliveries_oldest_first_with_mode_and_status()
    {
        var h    = NewHarness();
        var so   = await SeedOrder(h, qty: 100m, mode: "SHIP");
        var line = so.Lines.Single();

        var first = await h.Service.CreateAsync(so.UUID, new CreateSaleOrderDeliveryRequest
        {
            Lines = [new SourceLineSelection { SourceLineUuid = line.UUID, Qty = 40m }]
        }, User);
        var second = await h.Service.CreateAsync(so.UUID, new CreateSaleOrderDeliveryRequest
        {
            DeliveryMode = "SELF_PICKUP",
            Lines = [new SourceLineSelection { SourceLineUuid = line.UUID, Qty = 30m }]
        }, User);

        var issued = await h.Db.DeliveryOrders.SingleAsync(d => d.UUID == first);
        issued.Status = "GOODS_ISSUED";
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var list = await h.Service.GetDeliveriesAsync(so.UUID);

        list.Should().NotBeNull();
        list!.Select(d => d.UUID).Should().Equal(new List<Guid> { first, second }, "oldest first — the order's history");
        list[0].DeliveryMode.Should().Be("SHIP");
        list[0].Status.Should().Be("GOODS_ISSUED");
        list[0].SourceNumber.Should().Be("SO-2026-00042");
        list[0].LineCount.Should().Be(1);
        list[1].DeliveryMode.Should().Be("SELF_PICKUP");
        list[1].Status.Should().Be("DRAFT");
        list[1].ShipToCity.Should().BeNull();
    }

    [Fact]
    public async Task Deleted_deliveries_and_other_orders_deliveries_are_left_out()
    {
        var h     = NewHarness();
        var so    = await SeedOrder(h, qty: 100m);
        var other = await SeedOrder(h, qty: 10m);

        var kept    = await h.Service.CreateAsync(so.UUID, new CreateSaleOrderDeliveryRequest
        {
            Lines = [new SourceLineSelection { SourceLineUuid = so.Lines.Single().UUID, Qty = 40m }]
        }, User);
        var deleted = await h.Service.CreateAsync(so.UUID, null, User);
        await h.Service.CreateAsync(other.UUID, null, User);

        await h.Deliveries.DeleteAsync(deleted);

        var list = await h.Service.GetDeliveriesAsync(so.UUID);

        list!.Select(d => d.UUID).Should().Equal(new List<Guid> { kept });
    }

    [Fact]
    public async Task An_order_with_no_deliveries_lists_none_but_an_unknown_order_is_not_found()
    {
        var h  = NewHarness();
        var so = await SeedOrder(h);

        (await h.Service.GetDeliveriesAsync(so.UUID)).Should().BeEmpty();
        (await h.Service.GetDeliveriesAsync(Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task Another_organizations_order_is_invisible_both_ways()
    {
        var orgA = NewHarness();
        var so   = await SeedOrder(orgA);
        await orgA.Service.CreateAsync(so.UUID, null, User);

        var orgB = NewHarness(Guid.NewGuid(), orgA.DbName);

        (await orgB.Service.GetDeliveriesAsync(so.UUID)).Should().BeNull();
        var act = async () => await orgB.Service.CreateAsync(so.UUID, null, User);
        await act.Should().ThrowAsync<NotFoundException>();
    }
}

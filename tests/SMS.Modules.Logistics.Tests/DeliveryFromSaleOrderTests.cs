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

// A29-P6-02 §7.3 — POST /deliveries/from-source for a sale order: the outbound customer delivery
// (TC-08 self-pickup, TC-09 one order across several deliveries in mixed modes).
public class DeliveryFromSaleOrderTests
{
    private const int User = 42;

    private sealed record Harness(
        LogisticsDbContext Db,
        DemandDbContext Demand,
        FakeVariantResolver Variants,
        FakeStockReservationService Reservations,
        DeliveryFromSourceRepository Repo,
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

        var numbers      = new DocumentNumberGenerator(db, tenant);
        var addresses    = new AddressNormalizer(new FakeCityLookup());
        var variants     = new FakeVariantResolver();
        var reservations = new FakeStockReservationService();

        return new Harness(db, demand, variants, reservations,
            new DeliveryFromSourceRepository(db, demand, warehouse, material, numbers, addresses, variants, reservations),
            new DeliveryRepository(db, numbers, addresses),
            dbName);
    }

    /// <summary>One sale order line: what, how much, and where it stands.</summary>
    private sealed record L(
        Guid Variant, decimal Qty, decimal Fulfilled = 0m, string Status = "RESERVED", string? Mode = "IN_STOCK",
        decimal Price = 40m);

    private static async Task<Address> SeedAddress(Harness h, string line1 = "Plot 12, Korangi Industrial Area")
    {
        var address = new Address
        {
            UUID        = Guid.NewGuid(),
            Line1       = line1,
            CityName    = "Karachi",
            CountryName = "Pakistan",
            ContactName = "Ahmed Raza",
            CreatedBy   = 1,
            CreatedDate = DateTime.UtcNow
        };
        h.Db.Addresses.Add(address);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
        return address;
    }

    private static async Task<SaleOrder> SeedOrder(
        Harness h, string status = "CONFIRMED", string mode = "SHIP", Guid? addressUuid = null,
        params L[] lines)
    {
        var order = new SaleOrder
        {
            UUID                 = Guid.NewGuid(),
            TraceId              = Guid.NewGuid(),
            SoNumber             = "SO-2026-00042",
            PartnerId            = Guid.NewGuid(),
            OrderDate            = new DateTime(2026, 9, 1),
            ExpectedDeliveryDate = new DateTime(2026, 9, 20),
            CurrencyId           = Guid.NewGuid(),
            Status               = status,
            DeliveryMode         = mode,
            ShippingAddressId    = addressUuid,
            CreatedBy            = 1
        };

        foreach (var l in lines.Length > 0 ? lines : [new L(h.Variants.AddVariant("CAB-4MM", "4mm cable", uom: "M"), 100m)])
            order.Lines.Add(new SaleOrderLine
            {
                VariantUuid     = l.Variant,
                Quantity        = l.Qty,
                UnitPrice       = l.Price,
                LineTotal       = l.Qty * l.Price,
                FulfilledQty    = l.Fulfilled,
                FulfillmentMode = l.Mode,
                Status          = l.Status
            });

        h.Demand.SaleOrders.Add(order);
        await h.Demand.SaveChangesAsync();
        h.Demand.ChangeTracker.Clear();
        return order;
    }

    private static CreateDeliveryFromSourceRequest Request(Guid soUuid) =>
        new() { SourceType = "SALE_ORDER", SourceUuid = soUuid };

    private static SourceLineSelection Pick(SaleOrderLine line, decimal? qty = null) =>
        new() { SourceLineUuid = line.UUID, Qty = qty };

    private static Task<DeliveryOrder> Stored(Harness h, Guid uuid) =>
        h.Db.DeliveryOrders.Include(d => d.Lines).AsNoTracking().SingleAsync(d => d.UUID == uuid);

    /// <summary>Holds a line's stock for the order in one warehouse, as confirming it would have.</summary>
    private static async Task ReserveIn(Harness h, SaleOrder so, SaleOrderLine line, Guid warehouseUuid, string name)
    {
        h.Reservations.SetLayout(line.VariantUuid,
            (new FakeStockReservationService.StockLocation(warehouseUuid, name), line.Quantity));

        var result = await h.Reservations.ReserveAsync(ReservationSourceType.SalesOrder, so.UUID,
            [new ReservationRequest(line.VariantUuid, warehouseUuid, line.Quantity, line.UUID)], User);
        result.Succeeded.Should().BeTrue();
    }

    // ── The document ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_confirmed_order_becomes_an_outbound_draft_delivery_that_posts_its_own_goods_issue()
    {
        var h  = NewHarness();
        var so = await SeedOrder(h, addressUuid: (await SeedAddress(h)).UUID);

        var uuid   = await h.Repo.CreateFromSourceAsync(Request(so.UUID), User);
        var detail = await h.Deliveries.GetByUuidAsync(uuid);

        detail!.Direction.Should().Be("OUTBOUND");
        detail.Status.Should().Be("DRAFT");
        detail.SourceType.Should().Be("SALE_ORDER");
        detail.PostsGoodsIssue.Should().BeTrue("confirming the order only reserved the stock — this is the movement");
        detail.SaleOrderUuid.Should().Be(so.UUID);
        detail.SourceUuid.Should().Be(so.UUID);
        detail.SourceNumber.Should().Be("SO-2026-00042");
        detail.TraceId.Should().Be(so.TraceId, "one trace id spans SO → PO → GRN → delivery → invoice");
        detail.DeliveryNumber.Should().MatchRegex(@"^DLV-\d{4}-\d{5}$", "numbered from the shared sequence");
        detail.RequestedDate.Should().Be(new DateTime(2026, 9, 20), "the order's expected date is the default ask");
    }

    [Fact]
    public async Task Each_delivery_takes_its_own_number_from_the_shared_sequence()
    {
        var h  = NewHarness();
        var so = await SeedOrder(h, addressUuid: (await SeedAddress(h)).UUID);
        var line = so.Lines.Single();

        var first  = await h.Repo.CreateFromSourceAsync(new() { SourceType = "SALE_ORDER", SourceUuid = so.UUID, Lines = [Pick(line, 30m)] }, User);
        var second = await h.Repo.CreateFromSourceAsync(new() { SourceType = "SALE_ORDER", SourceUuid = so.UUID, Lines = [Pick(line, 30m)] }, User);

        var numbers = new[] { (await Stored(h, first)).DeliveryNumber, (await Stored(h, second)).DeliveryNumber };
        numbers.Should().OnlyHaveUniqueItems();
        numbers.Select(n => int.Parse(n[^5..])).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task A_shipped_order_takes_its_mode_and_the_orders_own_address_row()
    {
        var h       = NewHarness();
        var address = await SeedAddress(h);
        var so      = await SeedOrder(h, mode: "SHIP", addressUuid: address.UUID);

        var uuid   = await h.Repo.CreateFromSourceAsync(Request(so.UUID), User);
        var detail = await h.Deliveries.GetByUuidAsync(uuid);

        detail!.DeliveryMode.Should().Be("SHIP");
        detail.ShipToAddress!.UUID.Should().Be(address.UUID);
        detail.ShipToAddress.Line1.Should().Be("Plot 12, Korangi Industrial Area");

        (await Stored(h, uuid)).ShipToAddressId.Should().Be(address.Id,
            "the delivery references the order's address row rather than copying it — rows are never edited in place");
        (await h.Db.Addresses.CountAsync()).Should().Be(1, "no second address row was written");
    }

    [Fact]
    public async Task Each_line_names_the_order_line_it_fulfils_and_describes_the_variant()
    {
        var h = NewHarness();
        var laptop = h.Variants.AddVariant("DELL-5450-I7", "Dell Latitude 5450", "i7 / 16GB", isDefault: false, uom: "Piece");
        var cable  = h.Variants.AddVariant("CAB-4MM", "4mm cable", uom: "M");
        var so = await SeedOrder(h, addressUuid: (await SeedAddress(h)).UUID,
            lines: [new L(laptop, 3m, Price: 155000m), new L(cable, 250m, Price: 40m)]);
        var soLines = so.Lines.OrderBy(l => l.Id).ToList();

        var detail = await h.Deliveries.GetByUuidAsync(await h.Repo.CreateFromSourceAsync(Request(so.UUID), User));

        detail!.Lines.Should().HaveCount(2);
        detail.Lines.Select(l => l.LineNo).Should().Equal(1, 2);

        var first = detail.Lines[0];
        first.SoLineUuid.Should().Be(soLines[0].UUID);
        first.SourceLineUuid.Should().Be(soLines[0].UUID, "the generic source-line link is kept as well");
        first.VariantUuid.Should().Be(laptop);
        first.ItemDescription.Should().Be("Dell Latitude 5450 - i7 / 16GB (DELL-5450-I7)");
        first.UnitOfMeasure.Should().Be("Piece");
        first.QtyOrdered.Should().Be(3m);
        first.UnitValue.Should().Be(155000m, "the selling price, so the delivery is valued as the customer sees it");

        detail.Lines[1].ItemDescription.Should().Be("4mm cable (CAB-4MM)", "a default variant reads as its product");
        detail.Lines[1].UnitOfMeasure.Should().Be("M");
        detail.Lines[1].QtyOrdered.Should().Be(250m);
    }

    [Fact]
    public async Task A_variant_the_catalogue_cannot_describe_still_gets_a_readable_line()
    {
        var h       = NewHarness();
        var unknown = Guid.NewGuid();
        var so      = await SeedOrder(h, addressUuid: (await SeedAddress(h)).UUID, lines: [new L(unknown, 5m)]);

        var detail = await h.Deliveries.GetByUuidAsync(await h.Repo.CreateFromSourceAsync(Request(so.UUID), User));

        detail!.Lines.Single().ItemDescription.Should().Be($"Variant {unknown}");
        detail.Lines.Single().VariantUuid.Should().Be(unknown, "the id is kept so the pick can still resolve stock");
    }

    // ── Delivery mode (TC-08 / TC-09) ─────────────────────────────────────────

    [Fact]
    public async Task A_self_pickup_order_needs_no_address()
    {
        var h  = NewHarness();
        var so = await SeedOrder(h, mode: "SELF_PICKUP", addressUuid: null);

        var detail = await h.Deliveries.GetByUuidAsync(await h.Repo.CreateFromSourceAsync(Request(so.UUID), User));

        detail!.DeliveryMode.Should().Be("SELF_PICKUP");
        detail.ShipToAddress.Should().BeNull("the customer collects from the warehouse");
        detail.PostsGoodsIssue.Should().BeTrue("stock still leaves the books when it is handed over");
    }

    [Fact]
    public async Task A_shipped_order_with_no_address_on_file_is_refused()
    {
        var h  = NewHarness();
        var so = await SeedOrder(h, mode: "SHIP", addressUuid: null);

        var act = async () => await h.Repo.CreateFromSourceAsync(Request(so.UUID), User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*no shipping address*");
    }

    [Fact]
    public async Task An_address_the_order_points_at_but_that_no_longer_exists_is_refused_too()
    {
        var h  = NewHarness();
        var so = await SeedOrder(h, mode: "SHIP", addressUuid: Guid.NewGuid());

        var act = async () => await h.Repo.CreateFromSourceAsync(Request(so.UUID), User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*no shipping address*");
    }

    [Fact]
    public async Task A_ship_to_address_given_on_the_request_overrides_the_orders()
    {
        var h  = NewHarness();
        var so = await SeedOrder(h, mode: "SHIP", addressUuid: (await SeedAddress(h)).UUID);

        var req = Request(so.UUID);
        req.ShipToAddress = new AddressRequest
        {
            Line1 = "Site office, Gate 3", CityName = "Lahore", CountryName = "Pakistan", CountryIsoCode = "PK"
        };

        var detail = await h.Deliveries.GetByUuidAsync(await h.Repo.CreateFromSourceAsync(req, User));

        detail!.ShipToAddress!.Line1.Should().Be("Site office, Gate 3");
        (await h.Db.Addresses.CountAsync()).Should().Be(2, "a fresh snapshot, leaving the order's address untouched");
    }

    [Fact]
    public async Task Part_of_a_shipped_order_can_be_collected_instead()
    {
        // TC-09: 40 shipped, 30 shipped, 30 self-pickup — each delivery chooses its own mode.
        var h    = NewHarness();
        var so   = await SeedOrder(h, mode: "SHIP", addressUuid: (await SeedAddress(h)).UUID);
        var line = so.Lines.Single();

        var req = Request(so.UUID);
        req.DeliveryMode = "SELF_PICKUP";
        req.Lines        = [Pick(line, 30m)];

        var detail = await h.Deliveries.GetByUuidAsync(await h.Repo.CreateFromSourceAsync(req, User));

        detail!.DeliveryMode.Should().Be("SELF_PICKUP");
        detail.ShipToAddress.Should().BeNull();
        detail.Lines.Single().QtyOrdered.Should().Be(30m);
    }

    [Fact]
    public async Task An_unknown_delivery_mode_lists_the_valid_ones()
    {
        var h  = NewHarness();
        var so = await SeedOrder(h, addressUuid: (await SeedAddress(h)).UUID);

        var req = Request(so.UUID);
        req.DeliveryMode = "COURIER";

        var act = async () => await h.Repo.CreateFromSourceAsync(req, User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*COURIER*")
            .WithMessage("*SHIP, SELF_PICKUP*");
    }

    // ── Which orders may be delivered ─────────────────────────────────────────

    [Theory]
    [InlineData("CONFIRMED")]
    [InlineData("PARTIALLY_FULFILLED")]
    public async Task An_order_with_goods_still_to_go_can_be_delivered(string status)
    {
        var h  = NewHarness();
        var so = await SeedOrder(h, status, addressUuid: (await SeedAddress(h)).UUID);

        var act = async () => await h.Repo.CreateFromSourceAsync(Request(so.UUID), User);

        await act.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("FULFILLED")]
    [InlineData("INVOICED")]
    [InlineData("CLOSED")]
    [InlineData("CANCELLED")]
    public async Task An_order_with_nothing_to_send_is_rejected(string status)
    {
        var h  = NewHarness();
        var so = await SeedOrder(h, status, addressUuid: (await SeedAddress(h)).UUID);

        var act = async () => await h.Repo.CreateFromSourceAsync(Request(so.UUID), User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage($"*{status}*")
            .WithMessage("*CONFIRMED, PARTIALLY_FULFILLED*");
    }

    [Fact]
    public async Task An_unknown_sale_order_is_not_found()
    {
        var h = NewHarness();

        var act = async () => await h.Repo.CreateFromSourceAsync(Request(Guid.NewGuid()), User);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Another_organizations_order_is_not_found()
    {
        var orgA = NewHarness();
        var so   = await SeedOrder(orgA, addressUuid: (await SeedAddress(orgA)).UUID);

        var orgB = NewHarness(Guid.NewGuid(), orgA.DbName);
        var act  = async () => await orgB.Repo.CreateFromSourceAsync(Request(so.UUID), User);

        await act.Should().ThrowAsync<NotFoundException>("the tenant filter makes it simply not exist here");
    }

    // ── One order, several deliveries (§7.6 / TC-09) ──────────────────────────

    [Fact]
    public async Task One_order_can_go_out_in_several_deliveries_until_nothing_is_left()
    {
        var h    = NewHarness();
        var so   = await SeedOrder(h, addressUuid: (await SeedAddress(h)).UUID);
        var line = so.Lines.Single();

        var first = Request(so.UUID); first.Lines = [Pick(line, 40m)];
        (await h.Deliveries.GetByUuidAsync(await h.Repo.CreateFromSourceAsync(first, User)))!
            .Lines.Single().QtyOrdered.Should().Be(40m);

        var second = Request(so.UUID); second.Lines = [Pick(line, 30m)];
        (await h.Deliveries.GetByUuidAsync(await h.Repo.CreateFromSourceAsync(second, User)))!
            .Lines.Single().QtyOrdered.Should().Be(30m);

        (await h.Deliveries.GetByUuidAsync(await h.Repo.CreateFromSourceAsync(Request(so.UUID), User)))!
            .Lines.Single().QtyOrdered.Should().Be(30m, "an unqualified request takes whatever is left");

        var act = async () => await h.Repo.CreateFromSourceAsync(Request(so.UUID), User);
        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*nothing left to deliver*");
    }

    [Fact]
    public async Task Quantity_already_fulfilled_is_not_delivered_again()
    {
        var h  = NewHarness();
        var so = await SeedOrder(h, "PARTIALLY_FULFILLED", addressUuid: (await SeedAddress(h)).UUID,
            lines: [new L(h.Variants.AddVariant("CAB-4MM", "4mm cable"), 100m, Fulfilled: 60m, Status: "PARTIALLY_FULFILLED")]);

        var detail = await h.Deliveries.GetByUuidAsync(await h.Repo.CreateFromSourceAsync(Request(so.UUID), User));

        detail!.Lines.Single().QtyOrdered.Should().Be(40m);
    }

    [Fact]
    public async Task A_fully_fulfilled_line_is_left_out_entirely()
    {
        var h  = NewHarness();
        var so = await SeedOrder(h, "PARTIALLY_FULFILLED", addressUuid: (await SeedAddress(h)).UUID,
            lines:
            [
                new L(h.Variants.AddVariant("CAB-4MM", "4mm cable"), 100m, Fulfilled: 100m, Status: "FULFILLED"),
                new L(h.Variants.AddVariant("JB-01", "Junction box"), 40m)
            ]);

        var detail = await h.Deliveries.GetByUuidAsync(await h.Repo.CreateFromSourceAsync(Request(so.UUID), User));

        detail!.Lines.Should().HaveCount(1);
        detail.Lines.Single().ItemDescription.Should().Be("Junction box (JB-01)");
    }

    [Fact]
    public async Task Goods_already_issued_are_counted_once_through_fulfilled_qty_not_twice()
    {
        // FulfilledQty grows at goods issue (§7.5). An issued delivery's units live there, so they
        // must stop counting as "on a delivery" or the balance would be blocked.
        var h    = NewHarness();
        var so   = await SeedOrder(h, addressUuid: (await SeedAddress(h)).UUID);
        var line = so.Lines.Single();

        var first = Request(so.UUID); first.Lines = [Pick(line, 60m)];
        var firstUuid = await h.Repo.CreateFromSourceAsync(first, User);

        var issued = await h.Db.DeliveryOrders.SingleAsync(d => d.UUID == firstUuid);
        issued.Status = "GOODS_ISSUED";
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var tracked = await h.Demand.SaleOrderLines.SingleAsync(l => l.UUID == line.UUID);
        tracked.FulfilledQty = 60m;
        await h.Demand.SaveChangesAsync();
        h.Demand.ChangeTracker.Clear();

        var detail = await h.Deliveries.GetByUuidAsync(await h.Repo.CreateFromSourceAsync(Request(so.UUID), User));

        detail!.Lines.Single().QtyOrdered.Should().Be(40m, "the 60 that went out are accounted for once");
    }

    [Theory]
    [InlineData("CANCELLED")]
    [InlineData("SHORT_CLOSED")]
    public async Task A_delivery_that_is_not_going_returns_its_quantity_to_the_order(string status)
    {
        var h  = NewHarness();
        var so = await SeedOrder(h, addressUuid: (await SeedAddress(h)).UUID);

        var firstUuid = await h.Repo.CreateFromSourceAsync(Request(so.UUID), User);

        var first = await h.Db.DeliveryOrders.SingleAsync(d => d.UUID == firstUuid);
        first.Status = status;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var detail = await h.Deliveries.GetByUuidAsync(await h.Repo.CreateFromSourceAsync(Request(so.UUID), User));

        detail!.Lines.Single().QtyOrdered.Should().Be(100m);
    }

    [Fact]
    public async Task A_delivery_still_being_picked_or_held_keeps_its_claim_on_the_order()
    {
        var h    = NewHarness();
        var so   = await SeedOrder(h, addressUuid: (await SeedAddress(h)).UUID);
        var line = so.Lines.Single();

        var first = Request(so.UUID); first.Lines = [Pick(line, 70m)];
        var firstUuid = await h.Repo.CreateFromSourceAsync(first, User);

        var picking = await h.Db.DeliveryOrders.SingleAsync(d => d.UUID == firstUuid);
        picking.Status = "ON_HOLD";
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var detail = await h.Deliveries.GetByUuidAsync(await h.Repo.CreateFromSourceAsync(Request(so.UUID), User));

        detail!.Lines.Single().QtyOrdered.Should().Be(30m);
    }

    [Fact]
    public async Task Asking_for_more_than_is_left_is_refused_with_the_real_figure()
    {
        var h  = NewHarness();
        var so = await SeedOrder(h, "PARTIALLY_FULFILLED", addressUuid: (await SeedAddress(h)).UUID,
            lines: [new L(h.Variants.AddVariant("CAB-4MM", "4mm cable"), 100m, Fulfilled: 40m, Status: "PARTIALLY_FULFILLED")]);

        var req = Request(so.UUID);
        req.Lines = [Pick(so.Lines.Single(), 75m)];

        var act = async () => await h.Repo.CreateFromSourceAsync(req, User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*only 60*")
            .WithMessage("*75*");
    }

    [Fact]
    public async Task A_zero_quantity_is_refused()
    {
        var h  = NewHarness();
        var so = await SeedOrder(h, addressUuid: (await SeedAddress(h)).UUID);

        var req = Request(so.UUID);
        req.Lines = [Pick(so.Lines.Single(), 0m)];

        var act = async () => await h.Repo.CreateFromSourceAsync(req, User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*greater than zero*");
    }

    [Fact]
    public async Task A_line_from_a_different_order_is_rejected()
    {
        var h  = NewHarness();
        var so = await SeedOrder(h, addressUuid: (await SeedAddress(h)).UUID);

        var req = Request(so.UUID);
        req.Lines = [new SourceLineSelection { SourceLineUuid = Guid.NewGuid() }];

        var act = async () => await h.Repo.CreateFromSourceAsync(req, User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*does not belong to this sale order*");
    }

    [Fact]
    public async Task Cancelled_and_drop_ship_lines_are_left_out()
    {
        var h = NewHarness();
        var stocked   = h.Variants.AddVariant("CAB-4MM", "4mm cable");
        var cancelled = h.Variants.AddVariant("JB-01", "Junction box");
        var dropShip  = h.Variants.AddVariant("GEN-50", "Generator 50kVA");
        var so = await SeedOrder(h, addressUuid: (await SeedAddress(h)).UUID,
            lines:
            [
                new L(stocked, 100m),
                new L(cancelled, 10m, Status: "CANCELLED"),
                new L(dropShip, 1m, Status: "OPEN", Mode: "DROP_SHIP")
            ]);

        var detail = await h.Deliveries.GetByUuidAsync(await h.Repo.CreateFromSourceAsync(Request(so.UUID), User));

        detail!.Lines.Should().ContainSingle().Which.VariantUuid.Should().Be(stocked,
            "a cancelled line has nothing to send and a drop-ship line goes vendor → customer, never through here");

        var req = Request(so.UUID);
        req.Lines = [Pick(so.Lines.Single(l => l.VariantUuid == dropShip))];
        var act = async () => await h.Repo.CreateFromSourceAsync(req, User);
        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*shipped by the vendor directly*");
    }

    // ── Which warehouse ───────────────────────────────────────────────────────

    [Fact]
    public async Task The_delivery_ships_from_the_warehouse_the_order_is_reserved_in()
    {
        var h   = NewHarness();
        var so  = await SeedOrder(h, addressUuid: (await SeedAddress(h)).UUID);
        var whA = Guid.NewGuid();
        await ReserveIn(h, so, so.Lines.Single(), whA, "Karachi Main");

        var uuid = await h.Repo.CreateFromSourceAsync(Request(so.UUID), User);

        (await Stored(h, uuid)).ShipFromWarehouseUuid.Should().Be(whA,
            "the pick has to happen where the reservation is holding the stock");
    }

    [Fact]
    public async Task A_warehouse_named_on_the_request_wins()
    {
        var h   = NewHarness();
        var so  = await SeedOrder(h, addressUuid: (await SeedAddress(h)).UUID);
        await ReserveIn(h, so, so.Lines.Single(), Guid.NewGuid(), "Karachi Main");
        var whB = Guid.NewGuid();

        var req = Request(so.UUID);
        req.ShipFromWarehouseUuid = whB;

        (await Stored(h, await h.Repo.CreateFromSourceAsync(req, User))).ShipFromWarehouseUuid.Should().Be(whB);
    }

    [Fact]
    public async Task Lines_reserved_in_different_warehouses_need_the_caller_to_choose()
    {
        var h = NewHarness();
        var so = await SeedOrder(h, addressUuid: (await SeedAddress(h)).UUID,
            lines:
            [
                new L(h.Variants.AddVariant("CAB-4MM", "4mm cable"), 100m),
                new L(h.Variants.AddVariant("JB-01", "Junction box"), 40m)
            ]);
        var lines = so.Lines.OrderBy(l => l.Id).ToList();
        var whA = Guid.NewGuid();
        await ReserveIn(h, so, lines[0], whA, "Karachi Main");
        await ReserveIn(h, so, lines[1], Guid.NewGuid(), "Lahore Depot");

        var act = async () => await h.Repo.CreateFromSourceAsync(Request(so.UUID), User);
        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*2 different warehouses*");

        // Picking only the lines held in one place resolves it.
        var req = Request(so.UUID);
        req.Lines = [Pick(lines[0])];
        (await Stored(h, await h.Repo.CreateFromSourceAsync(req, User))).ShipFromWarehouseUuid.Should().Be(whA);
    }

    [Fact]
    public async Task An_order_with_nothing_reserved_yet_leaves_the_warehouse_for_release_to_choose()
    {
        // A back-to-back line still waiting on its PO has no hold anywhere yet.
        var h  = NewHarness();
        var so = await SeedOrder(h, addressUuid: (await SeedAddress(h)).UUID,
            lines: [new L(h.Variants.AddVariant("GEN-50", "Generator 50kVA"), 2m, Status: "OPEN", Mode: "BACK_TO_BACK")]);

        var uuid = await h.Repo.CreateFromSourceAsync(Request(so.UUID), User);

        (await Stored(h, uuid)).ShipFromWarehouseUuid.Should().BeNull();
    }

    [Fact]
    public async Task A_released_or_consumed_hold_no_longer_decides_the_warehouse()
    {
        var h  = NewHarness();
        var so = await SeedOrder(h, addressUuid: (await SeedAddress(h)).UUID);
        await ReserveIn(h, so, so.Lines.Single(), Guid.NewGuid(), "Karachi Main");
        await h.Reservations.ReleaseBySourceAsync(ReservationSourceType.SalesOrder, so.UUID, "expired", User);

        var uuid = await h.Repo.CreateFromSourceAsync(Request(so.UUID), User);

        (await Stored(h, uuid)).ShipFromWarehouseUuid.Should().BeNull();
    }
}

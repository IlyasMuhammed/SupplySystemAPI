using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Services;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;
using SMS.Modules.Logistics.Services;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Modules.Material.Data;
using SMS.Modules.Warehouse.Data;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Models;
using Xunit;
using Inv = SMS.Modules.Inventory.Domain;
using Log = SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Tests;

/// <summary>
/// A29-P6-07 §7/§8 — fulfilment end to end with nothing faked underneath: Inventory's real
/// reservation ledger and goods-issue poster, Demand's real order, availability check and
/// fulfilment service, Logistics' real repositories and PDF generation. Only what leaves the
/// process is stubbed — Hangfire (jobs captured), email recipients, the letterhead.
/// <para>
/// TC-08: self-pickup, confirm → DO → pick, pack → collect → DELIVERED, stock out as SALES_HANDOVER,
/// gate pass. TC-09: one order of 100 as 40 shipped + 30 shipped + 30 collected, mixed modes, ends
/// FULFILLED. TC-12: an organization sees none of another's.
/// </para>
/// </summary>
public class FulfillmentIntegrationTests
{
    private const int Sales  = 11;
    private const int Store  = 42;
    private const int Picker = 99;

    private sealed class World
    {
        public required Guid OrgId;
        public required string DbName;
        public required LogisticsDbContext Log;
        public required DemandDbContext Demand;
        public required InventoryDbContext Inv;
        public required StockReservationService Stock;
        public required AvailabilityCheckService Availability;
        public required SaleOrderFulfillmentService Fulfillment;
        public required DeliveryRepository Deliveries;
        public required DeliveryReleaseRepository Release;
        public required DeliveryStatusRepository Status;
        public required PickListRepository PickLists;
        public required PackageRepository Packages;
        public required GoodsIssueRepository GoodsIssue;
        public required SaleOrderDeliveryService SaleOrderDeliveries;
        public required DeliveryDocumentService Documents;
        public required List<Job> Jobs;
        public required Guid WarehouseUuid;
        public required int WarehouseId;

        public void Fresh()
        {
            Log.ChangeTracker.Clear();
            Demand.ChangeTracker.Clear();
            Inv.ChangeTracker.Clear();
        }
    }

    private static World NewWorld(Guid? orgId = null, string? dbName = null)
    {
        orgId  ??= Guid.NewGuid();
        dbName ??= Guid.NewGuid().ToString();
        var tenant = new StaticTenantContext { OrganizationId = orgId.Value };

        DbContextOptions<T> Options<T>() where T : DbContext =>
            new DbContextOptionsBuilder<T>().UseInMemoryDatabase(dbName).Options;

        var log    = LogisticsTestDb.Open(dbName, tenant);
        var demand = new DemandDbContext(Options<DemandDbContext>(), tenant);
        var inv    = new InventoryDbContext(Options<InventoryDbContext>(), tenant);
        var wh     = new WarehouseDbContext(Options<WarehouseDbContext>(), tenant);
        var mat    = new MaterialDbContext(Options<MaterialDbContext>(), tenant);

        // Inventory — the real ledger.
        var stock  = new StockReservationService(inv);
        var poster = new GoodsIssuePoster(inv, new InventoryLedgerService(inv, NullLogger<InventoryLedgerService>.Instance));

        var warehouse = new Inv.Warehouse { Uuid = Guid.NewGuid(), Name = "Karachi Main", Code = "KHI", IsActive = true, CreatedBy = 1 };
        inv.Warehouses.Add(warehouse);
        inv.SaveChanges();
        inv.ChangeTracker.Clear();

        // Demand — real services; what leaves the process is stubbed.
        var jobs = new List<Job>();
        var jobClient = new Mock<IBackgroundJobClient>();
        jobClient.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()))
            .Callback<Job, IState>((job, _) => jobs.Add(job))
            .Returns("fake-job-id");

        var config = new Mock<ISaleOrderConfigService>();
        config.Setup(c => c.GetConfigAsync()).ReturnsAsync(new SaleOrderConfigModel
        {
            ReservationTtlHours = 72, SelfPickupEnabled = true, DropShipEnabled = false, AutoPoEnabled = false,
            EmailIntimationEnabled = true
        });
        var users = new Mock<IUserQueryService>();
        users.Setup(u => u.GetUserEmailAsync(Sales)).ReturnsAsync("sales@example.com");
        var orgChart = new Mock<IOrgChartService>();
        var partnerNames = new Mock<ISupplierNameLookupService>();
        partnerNames.Setup(p => p.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()))
            .ReturnsAsync((IReadOnlyList<Guid> ids) => ids.ToDictionary(id => id, _ => "Acme Ltd"));

        var email = new SaleOrderEmailService(
            demand, stock, config.Object, orgChart.Object, users.Object, partnerNames.Object,
            jobClient.Object, NullLogger<SaleOrderEmailService>.Instance);
        var availability = new AvailabilityCheckService(demand, stock, config.Object);
        var fulfillment  = new SaleOrderFulfillmentService(
            demand, email, jobClient.Object, NullLogger<SaleOrderFulfillmentService>.Instance);

        // Logistics — the real repositories.
        var numbers    = new DocumentNumberGenerator(log, tenant);
        var addresses  = new AddressNormalizer(new FakeCityLookup());
        var deliveries = new DeliveryRepository(log, numbers, addresses);
        var fromSource = new DeliveryFromSourceRepository(
            log, demand, wh, mat, numbers, addresses, new ProductVariantResolver(inv), stock);
        var release    = new DeliveryReleaseRepository(log, stock, numbers);
        var status     = new DeliveryStatusRepository(log, stock);
        var pickLists  = new PickListRepository(log, stock, numbers);
        var packages   = new PackageRepository(log, numbers);
        var goodsIssue = new GoodsIssueRepository(
            log, demand, stock, poster, fulfillment, NullLogger<GoodsIssueRepository>.Instance);

        var templates = new Mock<IPoDocumentTemplateService>();
        templates.Setup(t => t.GetActiveAsync()).ReturnsAsync((PoDocumentTemplateModel?)null);
        var deliveryService = new DeliveryService(deliveries, fromSource, status, release, goodsIssue);

        return new World
        {
            OrgId = orgId.Value, DbName = dbName, Log = log, Demand = demand, Inv = inv,
            Stock = stock, Availability = availability, Fulfillment = fulfillment,
            Deliveries = deliveries, Release = release, Status = status, PickLists = pickLists,
            Packages = packages, GoodsIssue = goodsIssue,
            SaleOrderDeliveries = new SaleOrderDeliveryService(demand, deliveries, fromSource),
            Documents = new DeliveryDocumentService(deliveryService, packages, templates.Object, new StubEnvironment()),
            Jobs = jobs, WarehouseUuid = warehouse.Uuid, WarehouseId = warehouse.Id
        };
    }

    // ── Seeding ───────────────────────────────────────────────────────────────

    /// <summary>A product with one default variant and its stock, in one or more batches.</summary>
    private static async Task<Guid> SeedVariant(
        World w, string sku, string name, params (string? Batch, DateTime? Expiry, decimal OnHand)[] rows)
    {
        var product = new Inv.Product
        {
            Uuid = Guid.NewGuid(), Sku = sku, Name = name, UomCode = "EA", IsActive = true, CreatedBy = 1,
            Variants = { new Inv.ProductVariant { Uuid = Guid.NewGuid(), Sku = sku, VariantName = "Default", IsDefault = true, IsActive = true, CreatedBy = 1 } }
        };
        w.Inv.Products.Add(product);
        await w.Inv.SaveChangesAsync();

        var variant = product.Variants.Single();
        foreach (var (batch, expiry, onHand) in rows.Length > 0 ? rows : [(null, null, 100m)])
            w.Inv.InventoryItems.Add(new Inv.InventoryItem
            {
                Uuid = Guid.NewGuid(), VariantId = variant.Id, WarehouseId = w.WarehouseId,
                BatchNumber = batch, ExpiryDate = expiry, QtyOnHand = onHand, UnitCost = 25m
            });
        await w.Inv.SaveChangesAsync();
        w.Inv.ChangeTracker.Clear();

        return variant.Uuid;
    }

    private sealed record Order(Guid Uuid, Guid TraceId, IReadOnlyList<Guid> LineUuids);

    /// <summary>
    /// A sale order confirmed the way SaleOrderService.ConfirmAsync confirms it: the real
    /// availability check reserves its stock under SALES_ORDER, then the status moves.
    /// </summary>
    private static async Task<Order> ConfirmOrder(World w, string mode, params (Guid Variant, decimal Qty)[] lines)
    {
        Guid? addressUuid = null;
        if (mode == "SHIP")
        {
            var address = new Log.Address
            {
                UUID = Guid.NewGuid(), Line1 = "Plot 12, Korangi Industrial Area", CityName = "Karachi",
                CountryName = "Pakistan", ContactName = "Ahmed Raza", CreatedBy = 1, CreatedDate = DateTime.UtcNow
            };
            w.Log.Addresses.Add(address);
            await w.Log.SaveChangesAsync();
            addressUuid = address.UUID;
        }

        var order = new SaleOrder
        {
            SoNumber = $"SO-2026-{Random.Shared.Next(10000, 99999)}", PartnerId = Guid.NewGuid(),
            OrderDate = DateTime.UtcNow.Date, ExpectedDeliveryDate = DateTime.UtcNow.Date.AddDays(7),
            CurrencyId = Guid.NewGuid(), Status = "DRAFT", DeliveryMode = mode, ShippingAddressId = addressUuid,
            CreatedBy = Sales, TraceId = Guid.NewGuid()
        };
        foreach (var (variant, qty) in lines)
            order.Lines.Add(new SaleOrderLine { VariantUuid = variant, Quantity = qty, UnitPrice = 40m, LineTotal = qty * 40m });
        w.Demand.SaleOrders.Add(order);
        await w.Demand.SaveChangesAsync();

        await w.Availability.CheckAndReserveAsync(order.UUID, Sales);
        order.Status = "CONFIRMED";
        await w.Demand.SaveChangesAsync();
        w.Fresh();

        return new Order(order.UUID, order.TraceId, order.Lines.OrderBy(l => l.Id).Select(l => l.UUID).ToList());
    }

    // ── Driving a delivery ────────────────────────────────────────────────────

    private static async Task<Guid> RaiseDelivery(World w, Order order, decimal? qty = null, string? mode = null)
    {
        var req = new CreateSaleOrderDeliveryRequest { DeliveryMode = mode };
        if (qty is { } q)
            req.Lines = [new SourceLineSelection { SourceLineUuid = order.LineUuids[0], Qty = q }];

        var uuid = await w.SaleOrderDeliveries.CreateAsync(order.Uuid, req, Sales);
        w.Fresh();
        return uuid;
    }

    /// <summary>Release → pick list from the real allocations → confirm pick → pack → stage.</summary>
    private static async Task PickPackStage(World w, Guid deliveryUuid)
    {
        (await w.Release.ReleaseAsync(deliveryUuid, null, Store)).Should().BeTrue();
        w.Fresh();

        var pickListUuid = await w.PickLists.GenerateAsync(deliveryUuid, null, Store);
        var pickList     = (await w.PickLists.GetByUuidAsync(pickListUuid))!;
        await w.PickLists.ConfirmAsync(pickListUuid, new ConfirmPickRequest
        {
            Lines = pickList.Lines.Select(l => new ConfirmPickLineRequest { LineUuid = l.UUID, QtyPicked = l.QtyToPick }).ToList()
        }, Picker);
        w.Fresh();

        var packing = (await w.Packages.GetForDeliveryAsync(deliveryUuid))!;
        await w.Packages.PackAsync(deliveryUuid, new PackRequest
        {
            GrossWeightKg = 12m,
            Contents = packing.Lines.Select(l => new PackContentRequest { DeliveryLineUuid = l.DeliveryLineUuid, Qty = l.QtyPicked }).ToList()
        }, Store);
        w.Fresh();

        (await w.GoodsIssue.StageAsync(deliveryUuid, Store)).Should().BeTrue();
        w.Fresh();
    }

    private static async Task<GoodsIssueResultModel> Issue(World w, Guid deliveryUuid)
    {
        var result = (await w.GoodsIssue.IssueAsync(deliveryUuid, Store))!;
        w.Fresh();
        return result;
    }

    private static async Task<PickupResultModel> Collect(World w, Guid deliveryUuid)
    {
        var result = (await w.GoodsIssue.RecordPickupAsync(deliveryUuid, new RecordPickupRequest
        {
            PickupPersonName = "Ahmed Raza", PickupPersonIdType = "CNIC", PickupPersonIdNumber = "35202-1234567-1"
        }, Store))!;
        w.Fresh();
        return result;
    }

    // ── Reading the world back ────────────────────────────────────────────────

    private static Task<List<Inv.InventoryItem>> StockRows(World w, Guid variant) =>
        w.Inv.InventoryItems.AsNoTracking().Where(i => i.Variant.Uuid == variant).OrderBy(i => i.Id).ToListAsync();

    private static async Task<(decimal OnHand, decimal Reserved)> Counters(World w, Guid variant)
    {
        var rows = await StockRows(w, variant);
        return (rows.Sum(r => r.QtyOnHand), rows.Sum(r => r.QtyReserved));
    }

    // Ordered by the delivery number: the numbers are issued in sequence, and the in-memory
    // provider gives ledger rows no other deterministic order.
    private static Task<List<Inv.InventoryLedgerEntry>> Ledger(World w) =>
        w.Inv.InventoryLedgerEntries.AsNoTracking().OrderBy(e => e.ReferenceNumber).ThenBy(e => e.QuantityOut).ToListAsync();

    private static Task<List<Inv.StockReservation>> Holds(World w, string sourceType, Guid sourceUuid) =>
        w.Inv.StockReservations.AsNoTracking()
            .Where(r => r.SourceType == sourceType && r.SourceUuid == sourceUuid).OrderBy(r => r.Id).ToListAsync();

    private static Task<SaleOrder> ReadOrder(World w, Order order) =>
        w.Demand.SaleOrders.AsNoTracking().Include(o => o.Lines).SingleAsync(o => o.UUID == order.Uuid);

    private static List<TimelineEvent> Timeline(World w) =>
        w.Jobs.Where(j => j.Method.Name == "AppendAsync").Select(j => (TimelineEvent)j.Args[1]).ToList();

    // ── TC-08 — self-pickup ───────────────────────────────────────────────────

    [Fact]
    public async Task TC08_A_self_pickup_order_is_collected_and_its_stock_leaves_as_a_handover()
    {
        var w       = NewWorld();
        var laptop  = await SeedVariant(w, "DELL-5450", "Dell Latitude 5450", (null, null, 50m));
        var order   = await ConfirmOrder(w, "SELF_PICKUP", (laptop, 30m));

        // Confirming held the 30 for the order in the real ledger.
        (await Counters(w, laptop)).Should().Be((50m, 30m));
        (await Holds(w, ReservationSourceType.SalesOrder, order.Uuid)).Should().ContainSingle()
            .Which.ExpiresAt.Should().NotBeNull("a sale order's hold carries the TTL");

        // DO from the SO: mode, warehouse and description all come from the real data.
        var deliveryUuid = await RaiseDelivery(w, order);
        var detail = (await w.Deliveries.GetByUuidAsync(deliveryUuid))!;
        detail.SourceType.Should().Be("SALE_ORDER");
        detail.DeliveryMode.Should().Be("SELF_PICKUP");
        detail.TraceId.Should().Be(order.TraceId);
        detail.ShipToAddress.Should().BeNull();
        detail.Lines.Single().ItemDescription.Should().Be("Dell Latitude 5450 (DELL-5450)");
        detail.Lines.Single().UnitOfMeasure.Should().Be("EA");
        detail.Lines.Single().QtyOrdered.Should().Be(30m);
        (await w.Log.DeliveryOrders.AsNoTracking().SingleAsync(d => d.UUID == deliveryUuid))
            .ShipFromWarehouseUuid.Should().Be(w.WarehouseUuid, "derived from where the order's hold sits");

        // Pick, pack, stage — the order's hold became the delivery's; nothing was reserved twice.
        await PickPackStage(w, deliveryUuid);
        (await Counters(w, laptop)).Should().Be((50m, 30m), "the units are exactly as reserved as before");
        (await Holds(w, ReservationSourceType.SalesOrder, order.Uuid)).Should().BeEmpty();
        var held = (await Holds(w, ReservationSourceType.Delivery, deliveryUuid)).Should().ContainSingle().Subject;
        held.ReservedQty.Should().Be(30m);
        held.ExpiresAt.Should().BeNull("a hold that reached a delivery no longer expires");
        (await w.PickLists.GetForDeliveryAsync(deliveryUuid))!.Lines.Single().QtyToPick.Should().Be(30m);

        // A collection pass can be printed before the customer arrives.
        var (passBefore, passName) = await w.Documents.GenerateGatePassAsync(deliveryUuid);
        passName.Should().StartWith("GatePass-");
        passBefore.Length.Should().BeGreaterThan(1000);

        // The customer collects.
        var pickup = await Collect(w, deliveryUuid);
        pickup.Status.Should().Be("DELIVERED");
        pickup.GoodsIssue!.PostedStock.Should().BeTrue();
        pickup.GoodsIssue.QtyOut.Should().Be(30m);
        pickup.SaleOrderStatus.Should().Be("FULFILLED");

        // Stock: out via SALES_HANDOVER, hold consumed, nothing left reserved.
        var entry = (await Ledger(w)).Should().ContainSingle().Subject;
        entry.TransactionType.Should().Be("SALES_HANDOVER");
        entry.ReferenceType.Should().Be("DELIVERY");
        entry.ReferenceNumber.Should().Be(detail.DeliveryNumber);
        entry.QuantityOut.Should().Be(30m);
        (await Counters(w, laptop)).Should().Be((20m, 0m));
        (await Holds(w, ReservationSourceType.Delivery, deliveryUuid)).Should().OnlyContain(h => h.Status == "CONSUMED");

        // The order: line credited, order fulfilled, recorded and announced.
        var so = await ReadOrder(w, order);
        so.Status.Should().Be("FULFILLED");
        so.Lines.Single().FulfilledQty.Should().Be(30m);
        so.Lines.Single().Status.Should().Be("FULFILLED");

        var fulfilled = Timeline(w).Should().ContainSingle(e => e.EventType == "SO_FULFILLED").Subject;
        fulfilled.DocumentId.Should().Be(order.Uuid);
        fulfilled.Notes.Should().Contain(detail.DeliveryNumber);
        w.Jobs.Single(j => j.Method.Name == "AppendAsync").Args[4].Should().Be(w.OrgId, "org passed explicitly");

        var notice = (await w.Demand.SaleOrderIntimations.AsNoTracking().ToListAsync()).Should().ContainSingle().Subject;
        notice.EventType.Should().Be("SO_FULFILLED");
        notice.Recipients.Should().Contain("sales@example.com");
        notice.BodyHtml.Should().Contain(detail.DeliveryNumber).And.Contain("30 of 30 delivered");

        // The delivery itself, and its pass now naming the collector.
        var delivered = await w.Log.DeliveryOrders.AsNoTracking().Include(d => d.Lines).SingleAsync(d => d.UUID == deliveryUuid);
        delivered.PickupPersonName.Should().Be("Ahmed Raza");
        delivered.PickupPersonIdType.Should().Be("CNIC");
        delivered.PickedUpAt.Should().NotBeNull();
        delivered.Lines.Single().QtyDelivered.Should().Be(30m);
        var (passAfter, _) = await w.Documents.GenerateGatePassAsync(deliveryUuid);
        passAfter.Should().NotEqual(passBefore);
    }

    // ── TC-09 — one order, three deliveries, mixed modes ─────────────────────

    [Fact]
    public async Task TC09_One_order_goes_out_as_two_shipments_and_a_collection_and_ends_fulfilled()
    {
        var w     = NewWorld();
        var cable = await SeedVariant(w, "CAB-4MM", "4mm cable",
            ("B-SOON", new DateTime(2026, 12, 1), 60m),
            ("B-LATE", new DateTime(2028, 12, 1), 40m));
        var order = await ConfirmOrder(w, "SHIP", (cable, 100m));
        (await Counters(w, cable)).Should().Be((100m, 100m));

        // 40 shipped.
        var first = await RaiseDelivery(w, order, qty: 40m);
        await PickPackStage(w, first);
        (await Issue(w, first)).QtyOut.Should().Be(40m);

        var rows = await StockRows(w, cable);
        rows.Single(r => r.BatchNumber == "B-SOON").QtyOnHand.Should().Be(20m, "FEFO: the soonest batch ships first");
        rows.Single(r => r.BatchNumber == "B-LATE").QtyOnHand.Should().Be(40m);
        (await Counters(w, cable)).Should().Be((60m, 60m), "the order still holds the other 60");
        (await ReadOrder(w, order)).Lines.Single().FulfilledQty.Should().Be(40m);

        // The carrier reports the first shipment delivered. Nothing in Logistics moves a shipped
        // delivery to DELIVERED yet (proof of delivery is consignment-level), so this is the call
        // that path will make; it is what turns CONFIRMED into PARTIALLY_FULFILLED.
        var afterFirst = await w.Fulfillment.RecordDeliveryCompletedAsync(new DeliveryCompletion(
            order.Uuid, first, "DLV-first", [new DeliveredLine(order.LineUuids[0], 40m)], Store));
        afterFirst.Status.Should().Be("PARTIALLY_FULFILLED");
        (await ReadOrder(w, order)).Status.Should().Be("PARTIALLY_FULFILLED");
        Timeline(w).Should().NotContain(e => e.EventType == "SO_FULFILLED");

        // 30 shipped.
        var second = await RaiseDelivery(w, order, qty: 30m);
        await PickPackStage(w, second);
        (await Issue(w, second)).QtyOut.Should().Be(30m);
        (await Counters(w, cable)).Should().Be((30m, 30m));
        (await ReadOrder(w, order)).Lines.Single().FulfilledQty.Should().Be(70m);

        // The last 30 collected by the customer — a different mode on the same order.
        var third = await RaiseDelivery(w, order, mode: "SELF_PICKUP");
        (await w.Deliveries.GetByUuidAsync(third))!.Lines.Single().QtyOrdered.Should().Be(30m, "whatever is left");
        await PickPackStage(w, third);
        var pickup = await Collect(w, third);
        pickup.SaleOrderStatus.Should().Be("FULFILLED");

        // Stock: all gone, nothing held, three deliveries' movements of the right kinds in order.
        (await Counters(w, cable)).Should().Be((0m, 0m));
        var ledger = await Ledger(w);
        ledger.GroupBy(e => (e.ReferenceNumber, e.TransactionType))
              .OrderBy(g => g.Key.ReferenceNumber)
              .Select(g => (g.Key.TransactionType, g.Sum(e => e.QuantityOut ?? 0m)))
              .Should().Equal(new List<(string, decimal)> { ("SALES_SHIP", 40m), ("SALES_SHIP", 30m), ("SALES_HANDOVER", 30m) });
        var secondNumber = (await w.Deliveries.GetByUuidAsync(second))!.DeliveryNumber;
        ledger.Where(e => e.ReferenceNumber == secondNumber).Select(e => e.QuantityOut).Should().BeEquivalentTo(
            new decimal?[] { 20m, 10m },
            "the second shipment took the last 20 of B-SOON and 10 of B-LATE — one movement per batch, as reserved");
        (await w.Inv.StockReservations.AsNoTracking().ToListAsync()).Should().OnlyContain(r => r.Status == "CONSUMED");

        // The order: fulfilled, once, by the third delivery.
        var so = await ReadOrder(w, order);
        so.Status.Should().Be("FULFILLED");
        so.Lines.Single().FulfilledQty.Should().Be(100m);
        so.Lines.Single().Status.Should().Be("FULFILLED");
        var thirdNumber = (await w.Deliveries.GetByUuidAsync(third))!.DeliveryNumber;
        Timeline(w).Where(e => e.EventType == "SO_FULFILLED").Should().ContainSingle()
            .Which.Notes.Should().Contain(thirdNumber).And.Contain("100 of 100");
        (await w.Demand.SaleOrderIntimations.AsNoTracking().CountAsync(i => i.EventType == "SO_FULFILLED")).Should().Be(1);

        // The order's deliveries, as the sale order screen sees them.
        var list = (await w.SaleOrderDeliveries.GetDeliveriesAsync(order.Uuid))!;
        list.Select(d => d.UUID).Should().Equal(new List<Guid> { first, second, third });
        list.Select(d => d.DeliveryMode).Should().Equal(new List<string?> { "SHIP", "SHIP", "SELF_PICKUP" });
        list.Select(d => d.Status).Should().Equal(new List<string> { "GOODS_ISSUED", "GOODS_ISSUED", "DELIVERED" });

        // And a fulfilled order takes no further delivery.
        var act = async () => await RaiseDelivery(w, order);
        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*FULFILLED*")
            .WithMessage("*nothing can be delivered*");
    }

    // ── Release refusals and reversals against the real ledger ───────────────

    [Fact]
    public async Task Cancelling_a_released_delivery_hands_the_stock_back_to_the_order_in_the_real_ledger()
    {
        var w     = NewWorld();
        var cable = await SeedVariant(w, "CAB-4MM", "4mm cable", (null, null, 100m));
        var order = await ConfirmOrder(w, "SHIP", (cable, 100m));

        var delivery = await RaiseDelivery(w, order, qty: 40m);
        await w.Release.ReleaseAsync(delivery, null, Store);
        w.Fresh();
        (await Holds(w, ReservationSourceType.SalesOrder, order.Uuid)).Sum(h => h.ReservedQty).Should().Be(60m);
        (await Holds(w, ReservationSourceType.Delivery, delivery)).Sum(h => h.ReservedQty).Should().Be(40m);

        await w.Status.CancelAsync(delivery, new DeliveryReasonRequest { Reason = "Truck broke down" }, Store);
        w.Fresh();

        (await Holds(w, ReservationSourceType.SalesOrder, order.Uuid)).Where(h => h.Status == "ACTIVE").Sum(h => h.ReservedQty)
            .Should().Be(100m, "back with the order");
        (await Holds(w, ReservationSourceType.Delivery, delivery)).Where(h => h.Status == "ACTIVE")
            .Should().BeEmpty("the rows changed owner again — a whole row moves, it is not copied");
        (await Counters(w, cable)).Should().Be((100m, 100m), "never became free stock");

        // The order can raise the delivery again.
        var again = await RaiseDelivery(w, order, qty: 40m);
        await PickPackStage(w, again);
        (await Holds(w, ReservationSourceType.Delivery, again)).Sum(h => h.ReservedQty).Should().Be(40m);
    }

    [Fact]
    public async Task A_delivery_for_more_than_the_order_holds_takes_the_balance_from_free_stock_or_refuses()
    {
        var w     = NewWorld();
        var cable = await SeedVariant(w, "CAB-4MM", "4mm cable", (null, null, 60m));
        var order = await ConfirmOrder(w, "SHIP", (cable, 100m));

        // 60 on hand: the availability check reserved 60 and left 40 as a deficit (SPLIT).
        var so = await ReadOrder(w, order);
        so.Lines.Single().FulfillmentMode.Should().Be("SPLIT");
        so.Lines.Single().DeficitQty.Should().Be(40m);

        var delivery = await RaiseDelivery(w, order);
        (await w.Deliveries.GetByUuidAsync(delivery))!.Lines.Single().QtyOrdered.Should().Be(100m);

        // Nothing free for the other 40: refused, and the order's 60 untouched.
        var act = async () => await w.Release.ReleaseAsync(delivery, null, Store);
        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*not enough stock*");
        w.Fresh();
        (await Holds(w, ReservationSourceType.SalesOrder, order.Uuid)).Where(h => h.Status == "ACTIVE").Sum(h => h.ReservedQty).Should().Be(60m);

        // The balance arrives (a GRN, say); now the release takes 60 from the order and 40 fresh.
        var row = await w.Inv.InventoryItems.SingleAsync(i => i.Variant.Uuid == cable);
        row.QtyOnHand += 40m;
        await w.Inv.SaveChangesAsync();
        w.Fresh();

        (await w.Release.ReleaseAsync(delivery, null, Store)).Should().BeTrue();
        w.Fresh();
        (await Holds(w, ReservationSourceType.Delivery, delivery)).Where(h => h.Status == "ACTIVE").Sum(h => h.ReservedQty).Should().Be(100m);
        (await Counters(w, cable)).Should().Be((100m, 100m));
    }

    // ── TC-12 — tenant isolation ──────────────────────────────────────────────

    [Fact]
    public async Task TC12_Another_organization_sees_none_of_an_orders_fulfilment()
    {
        var a      = NewWorld();
        var laptop = await SeedVariant(a, "DELL-5450", "Dell Latitude 5450", (null, null, 50m));
        var order  = await ConfirmOrder(a, "SELF_PICKUP", (laptop, 30m));
        var delivery = await RaiseDelivery(a, order);
        await PickPackStage(a, delivery);

        var b = NewWorld(Guid.NewGuid(), a.DbName);

        (await b.SaleOrderDeliveries.GetDeliveriesAsync(order.Uuid)).Should().BeNull();
        var create = async () => await b.SaleOrderDeliveries.CreateAsync(order.Uuid, null, Sales);
        await create.Should().ThrowAsync<NotFoundException>();

        (await b.Deliveries.GetByUuidAsync(delivery)).Should().BeNull();
        (await b.GoodsIssue.RecordPickupAsync(delivery, new RecordPickupRequest
        {
            PickupPersonName = "Somebody", PickupPersonIdType = "CNIC", PickupPersonIdNumber = "1"
        }, Store)).Should().BeNull();
        var pass = async () => await b.Documents.GenerateGatePassAsync(delivery);
        await pass.Should().ThrowAsync<NotFoundException>();

        (await b.Stock.GetBySourceAsync(ReservationSourceType.Delivery, delivery)).Should().BeEmpty("the ledger is tenant-scoped too");
        (await b.Stock.GetAvailableAsync([laptop], null)).Should().BeEmpty();

        // And A's world is exactly as it was.
        (await Holds(a, ReservationSourceType.Delivery, delivery)).Where(h => h.Status == "ACTIVE").Sum(h => h.ReservedQty).Should().Be(30m);
        (await a.Deliveries.GetByUuidAsync(delivery))!.Status.Should().Be("STAGED");
    }
}

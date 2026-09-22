using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Modules.Reports.Services;
using SMS.Shared.Common;
using InventoryWarehouse = SMS.Modules.Inventory.Domain.Warehouse;

namespace SMS.Modules.Reports.Tests;

/// <summary>
/// A tenant's sale orders, deliveries and warehouses across the three modules that hold them — Demand,
/// Logistics and Inventory, each with its own context over one shared store, as production has them — and
/// the fulfilment report service that reads them. In-memory, so it shows what the service decides.
/// </summary>
internal sealed class FulfilmentWorld
{
    internal static readonly Guid Org      = ReceivablesReportWorld.Org;
    internal static readonly Guid OtherOrg = ReceivablesReportWorld.OtherOrg;

    internal static readonly Guid AcmeId   = ReceivablesReportWorld.AcmeId;
    internal static readonly Guid GlobexId = ReceivablesReportWorld.GlobexId;

    internal static readonly Guid Lahore  = Guid.Parse("c0000000-0000-0000-0000-00000000000a");
    internal static readonly Guid Karachi = Guid.Parse("c0000000-0000-0000-0000-00000000000b");

    internal readonly string DatabaseName = Guid.NewGuid().ToString();

    internal readonly Dictionary<Guid, string> Names = new() { [AcmeId] = "Acme Ltd", [GlobexId] = "Globex Corp" };

    internal string? CompanyName { get; set; } = "Northwind Trading";

    /// <summary>What "now" is for the service: when the report says it was made and what "days open" counts to.</summary>
    internal DateTime Now { get; set; } = new DateTime(2026, 9, 20, 10, 30, 0, DateTimeKind.Utc);

    private int _sequence;

    /// <param name="seedWarehouses">Whether to give the world its two warehouses now. A world pointed at SQL Server sets its factories first and seeds them after.</param>
    internal FulfilmentWorld(bool seedWarehouses = true)
    {
        if (seedWarehouses) SeedWarehouses();
    }

    internal void SeedWarehouses()
    {
        Warehouse(Lahore, "Lahore Main");
        Warehouse(Karachi, "Karachi Depot");
    }

    private static StaticTenantContext Tenant(Guid? org) => new() { OrganizationId = org ?? Org };

    /// <summary>Set to point the world at a real SQL Server database instead of the in-memory store.</summary>
    internal Func<Guid, LogisticsDbContext>? LogisticsFactory { get; set; }
    internal Func<Guid, DemandDbContext>?    DemandFactory    { get; set; }
    internal Func<Guid, InventoryDbContext>? InventoryFactory { get; set; }

    internal LogisticsDbContext Logistics(Guid? org = null) =>
        LogisticsFactory?.Invoke(org ?? Org)
        ?? new LogisticsDbContext(new DbContextOptionsBuilder<LogisticsDbContext>().UseInMemoryDatabase(DatabaseName).Options, Tenant(org));

    internal DemandDbContext Demand(Guid? org = null) =>
        DemandFactory?.Invoke(org ?? Org)
        ?? new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(DatabaseName).Options, Tenant(org));

    internal InventoryDbContext Inventory(Guid? org = null) =>
        InventoryFactory?.Invoke(org ?? Org)
        ?? new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(DatabaseName).Options, Tenant(org));

    internal FulfilmentReportService Service(Guid? org = null, Mock<ISupplierNameLookupService>? names = null) =>
        Service(Logistics(org), Demand(org), Inventory(org), names);

    internal FulfilmentReportService Service(
        LogisticsDbContext logistics, DemandDbContext demand, InventoryDbContext inventory, Mock<ISupplierNameLookupService>? names = null)
    {
        names ??= new Mock<ISupplierNameLookupService>();
        names.Setup(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()))
             .ReturnsAsync((IReadOnlyList<Guid> ids) => ids.Where(Names.ContainsKey).ToDictionary(id => id, id => Names[id]));

        var templates = new Mock<IPoDocumentTemplateService>();
        templates.Setup(t => t.GetActiveAsync()).ReturnsAsync(() =>
            CompanyName is null ? null : new PoDocumentTemplateModel { CompanyName = CompanyName });

        return new FulfilmentReportService(logistics, demand, inventory, names.Object, templates.Object, new MovableClock(this));
    }

    private sealed class MovableClock(FulfilmentWorld world) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(world.Now, DateTimeKind.Utc));
    }

    internal void Warehouse(Guid uuid, string name, Guid? org = null)
    {
        using var db = Inventory(org);
        db.Warehouses.Add(new InventoryWarehouse { Uuid = uuid, OrganizationId = org ?? Org, Code = name[..3].ToUpperInvariant(), Name = name });
        db.SaveChanges();
    }

    internal SaleOrder Order(string number, Guid? partner = null, Guid? org = null)
    {
        var order = new SaleOrder
        {
            UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), OrganizationId = org ?? Org, SoNumber = number, PartnerId = partner ?? AcmeId,
            OrderDate = new DateTime(2026, 9, 1), Status = "CONFIRMED", DeliveryMode = "SHIP", CreatedBy = 1
        };

        using var db = Demand(org);
        db.SaleOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    /// <summary>A delivery for a sale order — open unless the status says otherwise — with lines of (ordered, delivered) quantities.</summary>
    internal DeliveryOrder Delivery(
        string number, string status = "RELEASED", string? mode = "SHIP", Guid? warehouse = null, SaleOrder? order = null,
        DateTime? created = null, DateTime? promised = null, DateTime? requested = null, (decimal Ordered, decimal Delivered)[]? lines = null,
        string sourceType = "SALE_ORDER", bool deleted = false, Guid? org = null, string direction = "OUTBOUND", bool noWarehouse = false)
    {
        var n = Interlocked.Increment(ref _sequence);
        var delivery = new DeliveryOrder
        {
            UUID = Guid.NewGuid(), OrganizationId = org ?? Org, TraceId = Guid.NewGuid(), DeliveryNumber = number,
            Direction = direction, SourceType = sourceType, SourceUuid = order?.UUID, SourceNumber = order?.SoNumber ?? $"SRC-{n}",
            SaleOrderUuid = sourceType == "SALE_ORDER" ? order?.UUID : null, DeliveryMode = mode,
            ShipFromWarehouseUuid = noWarehouse ? null : warehouse ?? Lahore,
            RequestedDate = requested, PromisedDate = promised, Status = status,
            IsDelete = deleted, CreatedBy = 1, CreatedDate = created ?? new DateTime(2026, 9, 10, 9, 0, 0)
        };

        var lineNo = 0;
        foreach (var (ordered, delivered) in lines ?? [])
            delivery.Lines.Add(new DeliveryOrderLine
            {
                UUID = Guid.NewGuid(), OrganizationId = org ?? Org, LineNo = ++lineNo, ItemDescription = "Item", QtyOrdered = ordered, QtyDelivered = delivered
            });

        using var db = Logistics(org);
        db.DeliveryOrders.Add(delivery);
        db.SaveChanges();
        return delivery;
    }


    internal static readonly Guid Multan = Guid.Parse("c0000000-0000-0000-0000-00000000000c");

    /// <summary>
    /// Eighty deliveries from a fixed pseudo-random hand: every status, both delivery modes, three
    /// warehouses and none, six sources of which one is a sale order, some deleted, some another
    /// organization's, some with lines and some without, over twelve sale orders of three customers.
    /// Identical in every world that seeds it, so two worlds can be compared row by row.
    /// </summary>
    internal void SeedScatter()
    {
        var random   = new Random(20260922);
        var statuses = Enum.GetValues<DeliveryStatus>().Select(LogisticsCode.Of).ToArray();
        string[] others = ["PO", "MIV", "TRANSFER", "SRO", "MANUAL"];
        Guid[] partners = [AcmeId, GlobexId, Guid.Parse("33333333-3333-3333-3333-333333333333")];

        Warehouse(Multan, "Multan Yard");
        var orders   = Enumerable.Range(0, 12).Select(i => Order($"SO-2026-{i + 1:00000}", partners[i % 3])).ToList();
        var theirs   = Order("SO-THEIRS", org: OtherOrg);
        var warehouses = new Guid?[] { Karachi, Lahore, Multan, null };

        for (var i = 0; i < 80; i++)
        {
            var theirOrg = i % 23 == 22;
            var lines    = i % 3 == 0 ? null : Enumerable.Range(0, random.Next(1, 4)).Select(_ => (Ordered: random.Next(1, 40) / 2m, Delivered: random.Next(0, 10) / 2m)).ToArray();

            Delivery($"DLV-2026-{i + 1:00000}",
                statuses[random.Next(statuses.Length)],
                i % 3 == 0 ? "SELF_PICKUP" : "SHIP",
                warehouses[i % 4],
                theirOrg ? theirs : orders[i % 12],
                created: new DateTime(2026, 8, 15).AddDays(random.Next(0, 35)).AddHours(random.Next(0, 24)),
                promised: i % 5 == 0 ? null : new DateTime(2026, 9, 1).AddDays(random.Next(0, 40)),
                lines: lines,
                sourceType: i % 5 == 4 ? others[(i / 5) % 5] : "SALE_ORDER",
                deleted: i % 19 == 18,
                org: theirOrg ? OtherOrg : Org,
                noWarehouse: i % 4 == 3);
        }
    }
    /// <summary>Adds many bare open deliveries in one save, for the volume tests.</summary>
    internal void BulkDeliveries(int count, string status = "RELEASED")
    {
        using var db = Logistics();
        for (var i = 0; i < count; i++)
            db.DeliveryOrders.Add(new DeliveryOrder
            {
                UUID = Guid.NewGuid(), OrganizationId = Org, TraceId = Guid.NewGuid(), DeliveryNumber = $"DLV-BULK-{Interlocked.Increment(ref _sequence):000000}",
                Direction = "OUTBOUND", SourceType = "SALE_ORDER", SourceNumber = "SO-1", SaleOrderUuid = Guid.NewGuid(), DeliveryMode = "SHIP",
                ShipFromWarehouseUuid = Lahore, Status = status, CreatedBy = 1, CreatedDate = new DateTime(2026, 9, 10).AddMinutes(i)
            });
        db.SaveChanges();
    }
}

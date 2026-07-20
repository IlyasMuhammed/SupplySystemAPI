using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Finance.Data;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Material.Data;
using SMS.Modules.Reports.Data;
using SMS.Modules.Reports.Models;
using SMS.Modules.Reports.Repositories;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Warehouse.Domain;
using SMS.Shared.Common;
using SMS.WorkflowEngine.Services;
using Xunit;
using WarehouseDbContext = SMS.Modules.Warehouse.Data.WarehouseDbContext;

namespace SMS.Modules.Reports.Tests;

file static class Build
{
    internal static (ReportsRepository Repo, DemandDbContext Demand, WarehouseDbContext Warehouse) NewRepo()
    {
        var name = Guid.NewGuid().ToString();

        var demand    = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(name).Options);
        var warehouse = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseInMemoryDatabase(name).Options);

        var repo = new ReportsRepository(
            db:        new ReportsDbContext(new DbContextOptionsBuilder<ReportsDbContext>().UseInMemoryDatabase(name).Options),
            demand:    demand,
            warehouse: warehouse,
            inventory: new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(name).Options),
            finance:   new FinanceDbContext(new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(name).Options),
            logistics: new LogisticsDbContext(new DbContextOptionsBuilder<LogisticsDbContext>().UseInMemoryDatabase(name).Options),
            suppliers: new SuppliersDbContext(new DbContextOptionsBuilder<SuppliersDbContext>().UseInMemoryDatabase(name).Options),
            material:  new MaterialDbContext(new DbContextOptionsBuilder<MaterialDbContext>().UseInMemoryDatabase(name).Options),
            userQuery: new Mock<IUserQueryService>().Object,
            timeline:  new Mock<ITimelineService>().Object);

        return (repo, demand, warehouse);
    }

    internal static PurchaseOrder SeedPo(
        DemandDbContext db, Guid supplierId, string supplierName, string poNumber, string status,
        decimal totalAmount, DateTime createdDate, DateTime? deliveryDate, decimal qtyOrdered = 10m, decimal qtyReceived = 0m)
    {
        var po = new PurchaseOrder
        {
            UUID         = Guid.NewGuid(),
            TraceId      = Guid.NewGuid(),
            PoNumber     = poNumber,
            SupplierId   = supplierId,
            SupplierName = supplierName,
            Status       = status,
            TotalAmount  = totalAmount,
            DeliveryDate = deliveryDate,
            CreatedBy    = 1,
            CreatedDate  = createdDate,
            Lines =
            [
                new PurchaseOrderLine
                {
                    UUID            = Guid.NewGuid(),
                    LineNo          = 1,
                    ItemDescription = "Item",
                    Quantity        = qtyOrdered,
                    UnitPrice       = totalAmount / Math.Max(qtyOrdered, 1),
                    LineTotal       = totalAmount,
                    QtyReceived     = qtyReceived
                }
            ]
        };
        db.PurchaseOrders.Add(po);
        db.SaveChanges();
        return po;
    }

    internal static void SeedGrn(WarehouseDbContext db, Guid poUuid, Guid supplierId, string supplierName, DateTime receivedAt)
    {
        db.Grns.Add(new Grn
        {
            UUID         = Guid.NewGuid(),
            TraceId      = Guid.NewGuid(),
            GrnNumber    = $"GRN-{Guid.NewGuid():N}"[..12],
            PoUuid       = poUuid,
            PoNumber     = "PO",
            SupplierId   = supplierId,
            SupplierName = supplierName,
            ReceivedAt   = receivedAt,
            Status       = "APPROVED",
            ReceivedBy   = 1,
            CreatedBy    = 1,
            CreatedDate  = receivedAt
        });
        db.SaveChanges();
    }
}

public class GetSupplierOrdersAsync_Tests
{
    [Fact]
    public async Task Supplier_With_Five_POs_Shows_Correct_Counts_Per_Status()
    {
        var (repo, demand, _) = Build.NewRepo();
        var supplierId = Guid.NewGuid();

        Build.SeedPo(demand, supplierId, "Acme", "PO-1", "RECEIVED",           10_000m, DateTime.UtcNow, null);
        Build.SeedPo(demand, supplierId, "Acme", "PO-2", "PARTIALLY_INVOICED", 10_000m, DateTime.UtcNow, null);
        Build.SeedPo(demand, supplierId, "Acme", "PO-3", "PARTIALLY_RECEIVED", 10_000m, DateTime.UtcNow, null);
        Build.SeedPo(demand, supplierId, "Acme", "PO-4", "SENT",               10_000m, DateTime.UtcNow, null);
        Build.SeedPo(demand, supplierId, "Acme", "PO-5", "CANCELLED",          10_000m, DateTime.UtcNow, null);

        var result = await repo.GetSupplierOrdersAsync(new SupplierOrdersFilter());

        var row = result.Suppliers.Single();
        row.TotalPoCount.Should().Be(5);
        row.FullyReceivedCount.Should().Be(2);     // RECEIVED + PARTIALLY_INVOICED
        row.PartiallyReceivedCount.Should().Be(1);
        row.PendingCount.Should().Be(1);           // SENT
        row.CancelledCount.Should().Be(1);
    }

    [Fact]
    public async Task Late_Delivery_Shows_Correct_Variance_And_Amber_Colour()
    {
        var (repo, demand, warehouse) = Build.NewRepo();
        var supplierId = Guid.NewGuid();
        var po = Build.SeedPo(demand, supplierId, "Acme", "PO-1", "RECEIVED", 10_000m,
            new DateTime(2026, 6, 1), new DateTime(2026, 6, 10));
        Build.SeedGrn(warehouse, po.UUID, supplierId, "Acme", new DateTime(2026, 6, 15));

        var result = await repo.GetSupplierOrdersAsync(new SupplierOrdersFilter());

        var detail = result.Suppliers.Single().PurchaseOrders.Single();
        detail.DeliveryVarianceDays.Should().Be(5);
        detail.DeliveryColor.Should().Be("amber");
    }

    [Fact]
    public async Task Po_With_No_Expected_Delivery_Date_Shows_Neutral_Variance()
    {
        var (repo, demand, warehouse) = Build.NewRepo();
        var supplierId = Guid.NewGuid();
        var po = Build.SeedPo(demand, supplierId, "Acme", "PO-1", "RECEIVED", 10_000m,
            new DateTime(2026, 6, 1), deliveryDate: null);
        Build.SeedGrn(warehouse, po.UUID, supplierId, "Acme", new DateTime(2026, 6, 15));

        var result = await repo.GetSupplierOrdersAsync(new SupplierOrdersFilter());

        var detail = result.Suppliers.Single().PurchaseOrders.Single();
        detail.DeliveryVarianceDays.Should().BeNull();
        detail.DeliveryColor.Should().BeNull();
    }

    [Theory]
    [InlineData(-3, "green")]  // early
    [InlineData(0, "green")]   // exactly on time
    [InlineData(1, "amber")]   // amber lower boundary
    [InlineData(7, "amber")]   // amber upper boundary
    [InlineData(8, "red")]     // red lower boundary
    [InlineData(20, "red")]
    public async Task Colour_Coding_Is_Correct_At_Threshold_Boundaries(int varianceDays, string expectedColor)
    {
        var (repo, demand, warehouse) = Build.NewRepo();
        var supplierId = Guid.NewGuid();
        var expected = new DateTime(2026, 6, 10);
        var po = Build.SeedPo(demand, supplierId, "Acme", "PO-1", "RECEIVED", 10_000m, new DateTime(2026, 6, 1), expected);
        Build.SeedGrn(warehouse, po.UUID, supplierId, "Acme", expected.AddDays(varianceDays));

        var result = await repo.GetSupplierOrdersAsync(new SupplierOrdersFilter());

        var detail = result.Suppliers.Single().PurchaseOrders.Single();
        detail.DeliveryVarianceDays.Should().Be(varianceDays);
        detail.DeliveryColor.Should().Be(expectedColor);
    }

    [Fact]
    public async Task Filtering_By_Delivery_Performance_Late_Returns_Only_POs_With_Positive_Variance()
    {
        var (repo, demand, warehouse) = Build.NewRepo();
        var supplierId = Guid.NewGuid();
        var onTimePo = Build.SeedPo(demand, supplierId, "Acme", "PO-ONTIME", "RECEIVED", 10_000m,
            new DateTime(2026, 6, 1), new DateTime(2026, 6, 10));
        Build.SeedGrn(warehouse, onTimePo.UUID, supplierId, "Acme", new DateTime(2026, 6, 8)); // early

        var latePo = Build.SeedPo(demand, supplierId, "Acme", "PO-LATE", "RECEIVED", 10_000m,
            new DateTime(2026, 6, 1), new DateTime(2026, 6, 10));
        Build.SeedGrn(warehouse, latePo.UUID, supplierId, "Acme", new DateTime(2026, 6, 14)); // late

        var result = await repo.GetSupplierOrdersAsync(new SupplierOrdersFilter { DeliveryPerformance = "late" });

        var details = result.Suppliers.Single().PurchaseOrders;
        details.Should().ContainSingle();
        details[0].PoNumber.Should().Be("PO-LATE");
        details.Should().OnlyContain(d => d.DeliveryVarianceDays > 0);
    }

    [Fact]
    public async Task Grand_Total_Correctly_Sums_Across_All_Filtered_Suppliers()
    {
        var (repo, demand, _) = Build.NewRepo();
        var supplierA = Guid.NewGuid();
        var supplierB = Guid.NewGuid();

        Build.SeedPo(demand, supplierA, "Supplier A", "PO-A1", "RECEIVED", 30_000m, DateTime.UtcNow, null);
        Build.SeedPo(demand, supplierA, "Supplier A", "PO-A2", "SENT",     20_000m, DateTime.UtcNow, null);
        Build.SeedPo(demand, supplierB, "Supplier B", "PO-B1", "RECEIVED", 15_000m, DateTime.UtcNow, null);

        var result = await repo.GetSupplierOrdersAsync(new SupplierOrdersFilter());

        result.GrandTotalPoCount.Should().Be(3);
        result.GrandTotalPoValue.Should().Be(65_000m);
        result.Suppliers.Sum(s => s.TotalPoCount).Should().Be(result.GrandTotalPoCount);
        result.Suppliers.Sum(s => s.TotalPoValue).Should().Be(result.GrandTotalPoValue);
    }
}

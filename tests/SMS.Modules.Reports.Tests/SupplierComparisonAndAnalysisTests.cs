using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Material.Data;
using SMS.Modules.Reports.Data;
using SMS.Modules.Reports.Models;
using SMS.Modules.Reports.Repositories;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Domain;
using SMS.Modules.Warehouse.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Services;
using Xunit;
using WarehouseDbContext = SMS.Modules.Warehouse.Data.WarehouseDbContext;

namespace SMS.Modules.Reports.Tests;

file static class Build
{
    internal static (ReportsRepository Repo, DemandDbContext Demand, WarehouseDbContext Warehouse,
                      FinanceDbContext Finance, SuppliersDbContext Suppliers, InventoryDbContext Inventory)
        NewRepo()
    {
        var name = Guid.NewGuid().ToString();

        var demand    = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(name).Options);
        var warehouse = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseInMemoryDatabase(name).Options);
        var finance   = new FinanceDbContext(new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(name).Options);
        var suppliers = new SuppliersDbContext(new DbContextOptionsBuilder<SuppliersDbContext>().UseInMemoryDatabase(name).Options);
        var inventory = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(name).Options);

        var repo = new ReportsRepository(
            db:        new ReportsDbContext(new DbContextOptionsBuilder<ReportsDbContext>().UseInMemoryDatabase(name).Options),
            demand:    demand,
            warehouse: warehouse,
            inventory: inventory,
            finance:   finance,
            logistics: new LogisticsDbContext(new DbContextOptionsBuilder<LogisticsDbContext>().UseInMemoryDatabase(name).Options),
            suppliers: suppliers,
            material:  new MaterialDbContext(new DbContextOptionsBuilder<MaterialDbContext>().UseInMemoryDatabase(name).Options),
            userQuery: new Mock<IUserQueryService>().Object,
            timeline:  new Mock<ITimelineService>().Object);

        return (repo, demand, warehouse, finance, suppliers, inventory);
    }

    internal static Supplier SeedSupplier(SuppliersDbContext db, string name)
    {
        var s = new Supplier
        {
            UUID = Guid.NewGuid(), SupplierName = name, SupplierCode = $"SUP-{Guid.NewGuid():N}"[..10],
            Status = "APPROVED", IsActive = true, CreatedBy = 1, CreatedDate = DateTime.UtcNow
        };
        db.Suppliers.Add(s);
        db.SaveChanges();
        return s;
    }

    internal static void SeedSnapshot(SuppliersDbContext db, Guid supplierId, decimal totalScore, string grade)
    {
        db.SupplierScoreSnapshots.Add(new SupplierScoreSnapshot
        {
            UUID = Guid.NewGuid(), SupplierId = supplierId,
            PeriodStart = new DateTime(2026, 6, 1), PeriodEnd = new DateTime(2026, 7, 1),
            TotalScore = totalScore, Grade = grade, GrnCount = 1, CreatedBy = 1, CreatedDate = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    internal static void SeedLedgerEntry(FinanceDbContext db, Guid supplierId, decimal debit, decimal credit)
    {
        db.SupplierLedgerEntries.Add(new SupplierLedgerEntry
        {
            UUID = Guid.NewGuid(), SupplierId = supplierId, SequenceNo = 1, TransactionType = debit > 0 ? "INVOICE" : "PAYMENT",
            ReferenceType = "Invoice", ReferenceId = Guid.NewGuid(), ReferenceNo = "REF-1",
            EntryDate = DateTime.UtcNow, DebitAmount = debit, CreditAmount = credit, BalanceAfter = 0,
            CreatedBy = 1, CreatedDate = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    internal static PurchaseOrder SeedPo(DemandDbContext db, Guid supplierId, string supplierName, decimal totalAmount, DateTime? deliveryDate)
    {
        var po = new PurchaseOrder
        {
            UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), PoNumber = "PO-1", SupplierId = supplierId,
            SupplierName = supplierName, Status = "RECEIVED", TotalAmount = totalAmount, DeliveryDate = deliveryDate,
            CreatedBy = 1, CreatedDate = DateTime.UtcNow
        };
        db.PurchaseOrders.Add(po);
        db.SaveChanges();
        return po;
    }

    internal static Grn SeedGrn(WarehouseDbContext db, Guid poUuid, Guid supplierId, string supplierName, DateTime receivedAt, params (string InspectionResult, string? RejectionReason)[] lines)
    {
        var grn = new Grn
        {
            UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), GrnNumber = $"GRN-{Guid.NewGuid():N}"[..12], PoUuid = poUuid,
            PoNumber = "PO-1", SupplierId = supplierId, SupplierName = supplierName, ReceivedAt = receivedAt, Status = "APPROVED",
            ReceivedBy = 1, CreatedBy = 1, CreatedDate = receivedAt,
            Lines = lines.Select((l, i) => new GrnLine
            {
                UUID = Guid.NewGuid(), LineNo = i + 1, PoLineUuid = Guid.NewGuid(), ItemDescription = "Item",
                QtyOrdered = 10m, QtyReceived = 10m, QtyAccepted = 10m,
                InspectionResult = l.InspectionResult, RejectionReason = l.RejectionReason, InspectedAt = receivedAt
            }).ToList()
        };
        db.Grns.Add(grn);
        db.SaveChanges();
        return grn;
    }
}

public class GetSupplierComparisonAsync_Tests
{
    [Fact]
    public async Task Three_Suppliers_Returns_Correct_Columns_With_No_Cross_Contamination()
    {
        var (repo, demand, _, finance, suppliers, _) = Build.NewRepo();
        var a = Build.SeedSupplier(suppliers, "Supplier A");
        var b = Build.SeedSupplier(suppliers, "Supplier B");
        var c = Build.SeedSupplier(suppliers, "Supplier C");

        Build.SeedSnapshot(suppliers, a.UUID, 90m, "A");
        Build.SeedSnapshot(suppliers, b.UUID, 55m, "F");
        Build.SeedSnapshot(suppliers, c.UUID, 75m, "C");

        Build.SeedLedgerEntry(finance, a.UUID, 100_000m, 60_000m);
        Build.SeedLedgerEntry(finance, b.UUID, 50_000m, 50_000m);

        Build.SeedPo(demand, a.UUID, "Supplier A", 10_000m, null);
        Build.SeedPo(demand, b.UUID, "Supplier B", 20_000m, null);
        Build.SeedPo(demand, b.UUID, "Supplier B", 5_000m, null);

        var result = await repo.GetSupplierComparisonAsync([a.UUID, b.UUID, c.UUID]);

        result.Suppliers.Should().HaveCount(3);

        var colA = result.Suppliers.Single(s => s.SupplierId == a.UUID);
        colA.Grade.Should().Be("A");
        colA.CompositeScore.Should().Be(90m);
        colA.PoCount.Should().Be(1);
        colA.TotalPoValue.Should().Be(10_000m);
        colA.TotalInvoiced.Should().Be(100_000m);
        colA.OutstandingBalance.Should().Be(40_000m);

        var colB = result.Suppliers.Single(s => s.SupplierId == b.UUID);
        colB.Grade.Should().Be("F");
        colB.PoCount.Should().Be(2);
        colB.TotalPoValue.Should().Be(25_000m);
        colB.OutstandingBalance.Should().Be(0m);

        var colC = result.Suppliers.Single(s => s.SupplierId == c.UUID);
        colC.Grade.Should().Be("C");
        colC.PoCount.Should().Be(0);
        colC.TotalInvoiced.Should().Be(0m); // no ledger entries seeded for C -> must not pick up A's or B's
    }

    [Fact]
    public async Task More_Than_Five_Ids_Throws_BadRequest()
    {
        var (repo, _, _, _, _, _) = Build.NewRepo();
        var ids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToList();

        var act = () => repo.GetSupplierComparisonAsync(ids);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Fewer_Than_Two_Ids_Throws_BadRequest()
    {
        var (repo, _, _, _, _, _) = Build.NewRepo();

        var act = () => repo.GetSupplierComparisonAsync([Guid.NewGuid()]);

        await act.Should().ThrowAsync<BadRequestException>();
    }
}

public class GetGrnQualityAnalysisAsync_Tests
{
    [Fact]
    public async Task Correctly_Calculates_Monthly_Pass_Rates_From_Inspection_Results()
    {
        var (repo, demand, warehouse, _, suppliers, _) = Build.NewRepo();
        var supplier = Build.SeedSupplier(suppliers, "Acme");
        var po = Build.SeedPo(demand, supplier.UUID, "Acme", 10_000m, null);

        // June: 3 Pass, 1 Fail -> 75% pass
        Build.SeedGrn(warehouse, po.UUID, supplier.UUID, "Acme", new DateTime(2026, 6, 5), ("Pass", null));
        Build.SeedGrn(warehouse, po.UUID, supplier.UUID, "Acme", new DateTime(2026, 6, 10), ("Pass", null));
        Build.SeedGrn(warehouse, po.UUID, supplier.UUID, "Acme", new DateTime(2026, 6, 15), ("Pass", null));
        Build.SeedGrn(warehouse, po.UUID, supplier.UUID, "Acme", new DateTime(2026, 6, 20), ("Fail", "Damaged packaging"));

        // July: 1 Pass, 1 PartialPass -> 50% pass, 50% partial
        Build.SeedGrn(warehouse, po.UUID, supplier.UUID, "Acme", new DateTime(2026, 7, 5), ("Pass", null));
        Build.SeedGrn(warehouse, po.UUID, supplier.UUID, "Acme", new DateTime(2026, 7, 10), ("PartialPass", "Short quantity"));

        var result = await repo.GetGrnQualityAnalysisAsync(new GrnQualityAnalysisFilter());

        var supplierResult = result.Suppliers.Single();
        supplierResult.MonthlyTrend.Should().HaveCount(2);

        var june = supplierResult.MonthlyTrend.Single(m => m.Month == "2026-06");
        june.PassRate.Should().Be(75m);
        june.FailRate.Should().Be(25m);
        june.TotalLines.Should().Be(4);

        var july = supplierResult.MonthlyTrend.Single(m => m.Month == "2026-07");
        july.PassRate.Should().Be(50m);
        july.PartialRate.Should().Be(50m);

        result.FailedLines.Should().HaveCount(2);
        result.FailedLines.Should().Contain(l => l.RejectionReason == "Damaged packaging");
        result.FailedLines.Should().Contain(l => l.RejectionReason == "Short quantity");
    }
}

public class GetSupplierSpendAnalysisAsync_Tests
{
    private static void SeedInvoiceWithLine(
        FinanceDbContext finance, DemandDbContext demand, InventoryDbContext inventory,
        Guid supplierId, string supplierName, decimal amount, int categoryId, string categoryName)
    {
        var poLineUuid = Guid.NewGuid();
        var productUuid = Guid.NewGuid();

        if (!inventory.ProductCategories.Any(c => c.Id == categoryId))
            inventory.ProductCategories.Add(new ProductCategory { Id = categoryId, Name = categoryName, Code = categoryName.ToUpper(), IsActive = true, CreatedDate = DateTime.UtcNow });
        inventory.Products.Add(new Product { Uuid = productUuid, Sku = $"SKU-{Guid.NewGuid():N}"[..8], Name = "Item", CategoryId = categoryId, Status = "ACTIVE", IsActive = true, CreatedBy = 1, CreatedDate = DateTime.UtcNow });
        inventory.SaveChanges();

        var po = new PurchaseOrder
        {
            UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), PoNumber = "PO-X", SupplierId = supplierId, SupplierName = supplierName,
            Status = "RECEIVED", TotalAmount = amount, CreatedBy = 1, CreatedDate = DateTime.UtcNow,
            Lines = [new PurchaseOrderLine { UUID = poLineUuid, LineNo = 1, ItemDescription = "Item", Quantity = 1, UnitPrice = amount, LineTotal = amount, ProductUuid = productUuid }]
        };
        demand.PurchaseOrders.Add(po);
        demand.SaveChanges();

        var invoice = new Invoice
        {
            UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), InvoiceNumber = $"INV-{Guid.NewGuid():N}"[..10], SupplierId = supplierId,
            SupplierName = supplierName, PoUuid = po.UUID, PoNumber = "PO-X", InvoiceDate = DateTime.UtcNow,
            ReceivedDate = DateTime.UtcNow, DueDate = DateTime.UtcNow.AddDays(30), Subtotal = amount, TotalAmount = amount,
            CreatedBy = 1, CreatedDate = DateTime.UtcNow,
            Lines = [new InvoiceLine { UUID = Guid.NewGuid(), LineNo = 1, PoLineUuid = poLineUuid, ItemDescription = "Item", QtyInvoiced = 1, UnitPrice = amount, LineTotal = amount }]
        };
        finance.Invoices.Add(invoice);
        finance.SaveChanges();
    }

    [Fact]
    public async Task Top_Ten_Suppliers_Sorted_By_Invoiced_Amount_Descending()
    {
        var (repo, demand, _, finance, _, inventory) = Build.NewRepo();

        for (var i = 1; i <= 12; i++)
        {
            var supplierId = Guid.NewGuid();
            SeedInvoiceWithLine(finance, demand, inventory, supplierId, $"Supplier {i}", i * 1000m, 1, "General");
        }

        var result = await repo.GetSupplierSpendAnalysisAsync(new SupplierSpendFilter());

        result.TopSuppliers.Should().HaveCount(10);
        result.TopSuppliers.Should().BeInDescendingOrder(s => s.TotalInvoiced);
        result.TopSuppliers[0].TotalInvoiced.Should().Be(12_000m); // Supplier 12, highest
    }

    [Fact]
    public async Task Top_Five_Concentration_Percent_Correctly_Calculated()
    {
        var (repo, demand, _, finance, _, inventory) = Build.NewRepo();

        // 5 suppliers at 100 each (500 total) + 5 suppliers at 10 each (50 total) = grand total 550
        for (var i = 0; i < 5; i++)
            SeedInvoiceWithLine(finance, demand, inventory, Guid.NewGuid(), $"Big {i}", 100m, 1, "General");
        for (var i = 0; i < 5; i++)
            SeedInvoiceWithLine(finance, demand, inventory, Guid.NewGuid(), $"Small {i}", 10m, 1, "General");

        var result = await repo.GetSupplierSpendAnalysisAsync(new SupplierSpendFilter());

        result.GrandTotalSpend.Should().Be(550m);
        result.Top5ConcentrationPercent.Should().Be(90.9m); // 500 / 550 * 100, rounded to 1dp
    }

    [Fact]
    public async Task Spend_Is_Broken_Down_By_Category()
    {
        var (repo, demand, _, finance, _, inventory) = Build.NewRepo();
        SeedInvoiceWithLine(finance, demand, inventory, Guid.NewGuid(), "S1", 300m, 1, "Electronics");
        SeedInvoiceWithLine(finance, demand, inventory, Guid.NewGuid(), "S2", 200m, 2, "Furniture");

        var result = await repo.GetSupplierSpendAnalysisAsync(new SupplierSpendFilter());

        result.SpendByCategory.Should().Contain(c => c.Category == "Electronics" && c.TotalInvoiced == 300m);
        result.SpendByCategory.Should().Contain(c => c.Category == "Furniture" && c.TotalInvoiced == 200m);
    }
}

public class GetDeliveryPerformanceHeatmapAsync_Tests
{
    [Fact]
    public async Task Month_With_Zero_Grns_Is_All_Grey()
    {
        var (repo, demand, warehouse, _, suppliers, _) = Build.NewRepo();
        var supplier = Build.SeedSupplier(suppliers, "Acme");
        var po = Build.SeedPo(demand, supplier.UUID, "Acme", 10_000m, new DateTime(2026, 6, 1));
        // Only a March GRN — February must be all grey.
        Build.SeedGrn(warehouse, po.UUID, supplier.UUID, "Acme", new DateTime(2026, 3, 10), ("Pass", null));

        var result = await repo.GetDeliveryPerformanceHeatmapAsync(supplier.UUID, 2026);

        result.Should().NotBeNull();
        var februaryDays = result!.Days.Where(d => d.Date.Month == 2).ToList();
        februaryDays.Should().HaveCount(28);
        februaryDays.Should().OnlyContain(d => d.Color == "grey" && !d.HasGrn);
    }

    [Fact]
    public async Task Grn_Ten_Days_Late_Is_Coloured_Red()
    {
        var (repo, demand, warehouse, _, suppliers, _) = Build.NewRepo();
        var supplier = Build.SeedSupplier(suppliers, "Acme");
        var deliveryDate = new DateTime(2026, 6, 1);
        var po = Build.SeedPo(demand, supplier.UUID, "Acme", 10_000m, deliveryDate);
        Build.SeedGrn(warehouse, po.UUID, supplier.UUID, "Acme", deliveryDate.AddDays(10), ("Pass", null));

        var result = await repo.GetDeliveryPerformanceHeatmapAsync(supplier.UUID, 2026);

        var day = result!.Days.Single(d => d.Date == deliveryDate.AddDays(10));
        day.HasGrn.Should().BeTrue();
        day.VarianceDays.Should().Be(10);
        day.Color.Should().Be("red");
    }

    [Fact]
    public async Task Unknown_Supplier_Returns_Null()
    {
        var (repo, _, _, _, _, _) = Build.NewRepo();

        var result = await repo.GetDeliveryPerformanceHeatmapAsync(Guid.NewGuid(), 2026);

        result.Should().BeNull();
    }
}

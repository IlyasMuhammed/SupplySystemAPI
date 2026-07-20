using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Material.Data;
using SMS.Modules.Reports.Data;
using SMS.Modules.Reports.Models;
using SMS.Modules.Reports.Repositories;
using SMS.Modules.Reports.Services.Exports;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Domain;
using SMS.Shared.Common;
using SMS.WorkflowEngine.Services;
using Xunit;
using WarehouseDbContext = SMS.Modules.Warehouse.Data.WarehouseDbContext;

namespace SMS.Modules.Reports.Tests;

file static class Build
{
    internal static (ReportsRepository Repo, FinanceDbContext Finance, SuppliersDbContext Suppliers) NewRepo()
    {
        var name = Guid.NewGuid().ToString();

        var finance   = new FinanceDbContext(new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(name).Options);
        var suppliers = new SuppliersDbContext(new DbContextOptionsBuilder<SuppliersDbContext>().UseInMemoryDatabase(name).Options);

        var repo = new ReportsRepository(
            db:        new ReportsDbContext(new DbContextOptionsBuilder<ReportsDbContext>().UseInMemoryDatabase(name).Options),
            demand:    new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(name).Options),
            warehouse: new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseInMemoryDatabase(name).Options),
            inventory: new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(name).Options),
            finance:   finance,
            logistics: new LogisticsDbContext(new DbContextOptionsBuilder<LogisticsDbContext>().UseInMemoryDatabase(name).Options),
            suppliers: suppliers,
            material:  new MaterialDbContext(new DbContextOptionsBuilder<MaterialDbContext>().UseInMemoryDatabase(name).Options),
            userQuery: new Mock<IUserQueryService>().Object,
            timeline:  new Mock<ITimelineService>().Object);

        return (repo, finance, suppliers);
    }

    internal static Supplier SeedSupplier(SuppliersDbContext db, string name, string code, string status = "APPROVED")
    {
        var s = new Supplier
        {
            UUID         = Guid.NewGuid(),
            SupplierName = name,
            SupplierCode = code,
            Status       = status,
            IsActive     = true,
            CreatedBy    = 1,
            CreatedDate  = DateTime.UtcNow
        };
        db.Suppliers.Add(s);
        db.SaveChanges();
        return s;
    }

    internal static void SeedLedgerEntry(
        FinanceDbContext db, Guid supplierId, int sequenceNo, string transactionType,
        DateTime entryDate, decimal debit, decimal credit)
    {
        db.SupplierLedgerEntries.Add(new SupplierLedgerEntry
        {
            UUID            = Guid.NewGuid(),
            SupplierId      = supplierId,
            SequenceNo      = sequenceNo,
            TransactionType = transactionType,
            ReferenceType   = transactionType,
            ReferenceId     = Guid.NewGuid(),
            ReferenceNo     = $"{transactionType}-{sequenceNo}",
            EntryDate       = entryDate,
            DebitAmount     = debit,
            CreditAmount    = credit,
            BalanceAfter    = 0m,
            CreatedBy       = 1,
            CreatedDate     = entryDate
        });
        db.SaveChanges();
    }
}

public class GetSupplierLedgerSummaryAsync_Tests
{
    [Fact]
    public async Task Outstanding_Balance_Is_Total_Invoiced_Minus_Total_Paid()
    {
        var (repo, finance, suppliers) = Build.NewRepo();
        var supplier = Build.SeedSupplier(suppliers, "Prime Enterprises", "SUP-001");

        // 3 invoices totalling 150,000
        Build.SeedLedgerEntry(finance, supplier.UUID, 1, "INVOICE", DateTime.UtcNow.AddDays(-10), 50_000m, 0m);
        Build.SeedLedgerEntry(finance, supplier.UUID, 2, "INVOICE", DateTime.UtcNow.AddDays(-9),  60_000m, 0m);
        Build.SeedLedgerEntry(finance, supplier.UUID, 3, "INVOICE", DateTime.UtcNow.AddDays(-8),  40_000m, 0m);
        // 2 payments totalling 80,000
        Build.SeedLedgerEntry(finance, supplier.UUID, 4, "PAYMENT", DateTime.UtcNow.AddDays(-5),  0m, 50_000m);
        Build.SeedLedgerEntry(finance, supplier.UUID, 5, "PAYMENT", DateTime.UtcNow.AddDays(-3),  0m, 30_000m);

        var result = await repo.GetSupplierLedgerSummaryAsync(new SupplierLedgerSummaryFilter());

        var row = result.Items.Single();
        row.TotalInvoiced.Should().Be(150_000m);
        row.TotalPaid.Should().Be(80_000m);
        row.OutstandingBalance.Should().Be(70_000m);
        row.AdvanceBalance.Should().Be(0m);
    }

    [Fact]
    public async Task Date_Range_Filter_Excludes_Ledger_Entries_Outside_The_Range()
    {
        var (repo, finance, suppliers) = Build.NewRepo();
        var supplier = Build.SeedSupplier(suppliers, "Acme Traders", "SUP-002");

        Build.SeedLedgerEntry(finance, supplier.UUID, 1, "INVOICE", new DateTime(2026, 1, 15), 10_000m, 0m);  // outside
        Build.SeedLedgerEntry(finance, supplier.UUID, 2, "INVOICE", new DateTime(2026, 3, 15), 20_000m, 0m);  // inside
        Build.SeedLedgerEntry(finance, supplier.UUID, 3, "INVOICE", new DateTime(2026, 6, 15), 30_000m, 0m);  // outside

        var result = await repo.GetSupplierLedgerSummaryAsync(new SupplierLedgerSummaryFilter
        {
            DateFrom = "2026-03-01",
            DateTo   = "2026-03-31"
        });

        var row = result.Items.Single();
        row.TotalInvoiced.Should().Be(20_000m);
    }

    [Fact]
    public async Task Minimum_Outstanding_Threshold_Filter_Excludes_Suppliers_Below_Threshold()
    {
        var (repo, finance, suppliers) = Build.NewRepo();
        var lowSupplier  = Build.SeedSupplier(suppliers, "Low Outstanding Co", "SUP-LOW");
        var highSupplier = Build.SeedSupplier(suppliers, "High Outstanding Co", "SUP-HIGH");

        Build.SeedLedgerEntry(finance, lowSupplier.UUID,  1, "INVOICE", DateTime.UtcNow, 5_000m, 0m);
        Build.SeedLedgerEntry(finance, highSupplier.UUID, 1, "INVOICE", DateTime.UtcNow, 100_000m, 0m);

        var result = await repo.GetSupplierLedgerSummaryAsync(new SupplierLedgerSummaryFilter { MinOutstanding = 10_000m });

        result.Items.Should().ContainSingle();
        result.Items[0].SupplierCode.Should().Be("SUP-HIGH");
    }

    [Fact]
    public async Task DrillDownUrl_Passes_The_Report_Date_Range_To_The_Per_Supplier_Ledger_Endpoint()
    {
        var (repo, finance, suppliers) = Build.NewRepo();
        var supplier = Build.SeedSupplier(suppliers, "Drilldown Supplier", "SUP-003");
        Build.SeedLedgerEntry(finance, supplier.UUID, 1, "INVOICE", new DateTime(2026, 3, 15), 1_000m, 0m);

        var result = await repo.GetSupplierLedgerSummaryAsync(new SupplierLedgerSummaryFilter
        {
            DateFrom = "2026-03-01",
            DateTo   = "2026-03-31"
        });

        var url = result.Items.Single().DrillDownUrl;
        url.Should().Be($"/api/suppliers/{supplier.UUID}/ledger?dateFrom=2026-03-01&dateTo=2026-03-31");
    }

    [Fact]
    public async Task Supplier_Status_Filter_Excludes_Non_Matching_Suppliers()
    {
        var (repo, finance, suppliers) = Build.NewRepo();
        var approved  = Build.SeedSupplier(suppliers, "Approved Co", "SUP-A", status: "APPROVED");
        var suspended = Build.SeedSupplier(suppliers, "Suspended Co", "SUP-S", status: "SUSPENDED");

        Build.SeedLedgerEntry(finance, approved.UUID,  1, "INVOICE", DateTime.UtcNow, 1_000m, 0m);
        Build.SeedLedgerEntry(finance, suspended.UUID, 1, "INVOICE", DateTime.UtcNow, 1_000m, 0m);

        var result = await repo.GetSupplierLedgerSummaryAsync(new SupplierLedgerSummaryFilter { SupplierStatus = "APPROVED" });

        result.Items.Should().ContainSingle();
        result.Items[0].SupplierCode.Should().Be("SUP-A");
    }

    [Fact]
    public async Task Sort_By_Outstanding_Balance_Descending_Is_The_Default()
    {
        var (repo, finance, suppliers) = Build.NewRepo();
        var small = Build.SeedSupplier(suppliers, "Small Balance", "SUP-SMALL");
        var big   = Build.SeedSupplier(suppliers, "Big Balance", "SUP-BIG");

        Build.SeedLedgerEntry(finance, small.UUID, 1, "INVOICE", DateTime.UtcNow, 1_000m, 0m);
        Build.SeedLedgerEntry(finance, big.UUID,   1, "INVOICE", DateTime.UtcNow, 50_000m, 0m);

        var result = await repo.GetSupplierLedgerSummaryAsync(new SupplierLedgerSummaryFilter());

        result.Items.Select(i => i.SupplierCode).Should().Equal("SUP-BIG", "SUP-SMALL");
    }
}

public class SupplierLedgerSummaryExportTests
{
    private static SupplierLedgerSummaryReport SampleReport() => new()
    {
        Items =
        [
            new SupplierLedgerSummaryItem
            {
                SupplierId = Guid.NewGuid(), SupplierName = "Prime Enterprises", SupplierCode = "SUP-001",
                TotalInvoiced = 150_000m, TotalPaid = 80_000m, OutstandingBalance = 70_000m,
                LastTransactionDate = DateTime.UtcNow, DrillDownUrl = "/api/suppliers/x/ledger"
            },
            new SupplierLedgerSummaryItem
            {
                SupplierId = Guid.NewGuid(), SupplierName = "Acme Traders", SupplierCode = "SUP-002",
                TotalInvoiced = 20_000m, TotalPaid = 20_000m, OutstandingBalance = 0m,
                LastTransactionDate = DateTime.UtcNow, DrillDownUrl = "/api/suppliers/y/ledger"
            }
        ],
        GrandTotalInvoiced = 170_000m,
        GrandTotalPaid = 100_000m,
        GrandTotalOutstanding = 70_000m
    };

    [Fact]
    public void Pdf_Export_Produces_A_Valid_File()
    {
        var bytes = SupplierLedgerSummaryPdfExporter.Export(SampleReport());

        bytes.Should().NotBeEmpty();
        System.Text.Encoding.ASCII.GetString(bytes, 0, 5).Should().Be("%PDF-");

        using var ms = new MemoryStream(bytes);
        var doc = PdfSharpCore.Pdf.IO.PdfReader.Open(ms, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.InformationOnly);
        doc.PageCount.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void Excel_Export_Contains_All_Visible_Rows_Plus_Header_And_Grand_Total()
    {
        var report = SampleReport();
        var bytes  = SupplierLedgerSummaryExcelExporter.Export(report);

        bytes.Should().NotBeEmpty();

        using var ms = new MemoryStream(bytes);
        using var wb = new ClosedXML.Excel.XLWorkbook(ms);
        var ws = wb.Worksheet("Supplier Ledger Summary");

        ws.Cell(1, 1).GetString().Should().Be("Supplier");
        ws.Cell(2, 1).GetString().Should().Be("Prime Enterprises");
        ws.Cell(3, 1).GetString().Should().Be("Acme Traders");
        ws.Cell(4, 1).GetString().Should().Be("Grand Total");
        ws.Cell(4, 5).GetValue<decimal>().Should().Be(70_000m);
    }
}

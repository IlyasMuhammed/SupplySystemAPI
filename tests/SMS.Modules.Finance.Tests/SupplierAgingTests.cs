using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Repositories;
using SMS.Modules.Finance.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Finance.Tests;

file static class Build
{
    internal static FinanceDbContext NewFinanceDb(string? dbName = null) =>
        new(new DbContextOptionsBuilder<FinanceDbContext>()
            .UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString())
            .Options);

    internal static SupplierPaymentRepository NewRepo(FinanceDbContext db) =>
        new(db, new SupplierLedgerService(db), new Mock<INotificationService>().Object);

    internal static Invoice SeedInvoice(
        FinanceDbContext db, Guid supplierId, decimal totalAmount, decimal paidAmount, int daysOverdue,
        string invoiceNumber = "INV-2026-00001", string? supplierName = null, string? paymentStatus = null)
    {
        var status = paymentStatus ?? (paidAmount <= 0 ? "UNPAID" : paidAmount < totalAmount ? "PARTIALLY_PAID" : "FULLY_PAID");
        var inv = new Invoice
        {
            UUID              = Guid.NewGuid(),
            TraceId           = Guid.NewGuid(),
            InvoiceNumber     = invoiceNumber,
            SupplierId        = supplierId,
            SupplierName      = supplierName ?? "Test Supplier",
            PoUuid            = Guid.NewGuid(),
            PoNumber          = "PO-2026-00001",
            InvoiceDate       = DateTime.UtcNow.AddDays(-daysOverdue - 30),
            ReceivedDate      = DateTime.UtcNow.AddDays(-daysOverdue - 30),
            DueDate           = DateTime.UtcNow.Date.AddDays(-daysOverdue),
            Currency          = "PKR",
            Subtotal          = totalAmount,
            TaxAmount         = 0m,
            TotalAmount       = totalAmount,
            MatchedPoValue    = totalAmount,
            MatchedGrnValue   = 0m,
            VarianceAmount    = 0m,
            MatchStatus       = "Matched",
            PaymentStatus     = status,
            PaidAmount        = paidAmount,
            IsActive          = true,
            CreatedBy         = 1,
            CreatedDate       = DateTime.UtcNow
        };
        db.Invoices.Add(inv);
        db.SaveChanges();
        return inv;
    }
}

public class GetSupplierAgingAsync_Tests
{
    [Fact]
    public async Task Invoice_Due_15_Days_Ago_With_Outstanding_10000_Appears_In_Current_Bucket()
    {
        var db = Build.NewFinanceDb();
        var supplierId = Guid.NewGuid();
        Build.SeedInvoice(db, supplierId, totalAmount: 10000m, paidAmount: 0m, daysOverdue: 15, invoiceNumber: "INV-15D");

        var aging = await Build.NewRepo(db).GetSupplierAgingAsync(supplierId);

        var current = aging.Buckets.Single(b => b.BucketName == "Current");
        current.Total.Should().Be(10000m);
        current.Invoices.Should().ContainSingle(i => i.InvoiceNumber == "INV-15D");
        aging.Buckets.Where(b => b.BucketName != "Current").Should().OnlyContain(b => b.Total == 0m);
    }

    [Fact]
    public async Task Invoice_Due_45_Days_Ago_With_Outstanding_20000_Appears_In_31_60_Bucket()
    {
        var db = Build.NewFinanceDb();
        var supplierId = Guid.NewGuid();
        Build.SeedInvoice(db, supplierId, totalAmount: 20000m, paidAmount: 0m, daysOverdue: 45, invoiceNumber: "INV-45D");

        var aging = await Build.NewRepo(db).GetSupplierAgingAsync(supplierId);

        var bucket = aging.Buckets.Single(b => b.BucketName == "31-60");
        bucket.Total.Should().Be(20000m);
        bucket.Invoices.Should().ContainSingle(i => i.InvoiceNumber == "INV-45D");
    }

    [Fact]
    public async Task PartiallyPaid_Invoice_Due_75_Days_Ago_Ages_Only_On_Outstanding_Portion()
    {
        var db = Build.NewFinanceDb();
        var supplierId = Guid.NewGuid();
        // amount 50,000, paid 30,000 -> outstanding 20,000, due 75 days ago -> 61-90 bucket
        Build.SeedInvoice(db, supplierId, totalAmount: 50000m, paidAmount: 30000m, daysOverdue: 75, invoiceNumber: "INV-75D");

        var aging = await Build.NewRepo(db).GetSupplierAgingAsync(supplierId);

        var bucket = aging.Buckets.Single(b => b.BucketName == "61-90");
        bucket.Total.Should().Be(20000m);
        bucket.Invoices.Single().OutstandingAmount.Should().Be(20000m);

        // The full 50,000 must not appear anywhere.
        aging.Buckets.Sum(b => b.Total).Should().Be(20000m);
        aging.GrandTotal.Should().Be(20000m);
    }

    [Fact]
    public async Task FullyPaid_Invoice_Does_Not_Appear_In_Any_Bucket()
    {
        var db = Build.NewFinanceDb();
        var supplierId = Guid.NewGuid();
        Build.SeedInvoice(db, supplierId, totalAmount: 10000m, paidAmount: 10000m, daysOverdue: 50, invoiceNumber: "INV-PAID");
        Build.SeedInvoice(db, supplierId, totalAmount: 5000m, paidAmount: 0m, daysOverdue: 10, invoiceNumber: "INV-UNPAID");

        var aging = await Build.NewRepo(db).GetSupplierAgingAsync(supplierId);

        aging.Buckets.SelectMany(b => b.Invoices).Should().NotContain(i => i.InvoiceNumber == "INV-PAID");
        aging.GrandTotal.Should().Be(5000m);
    }

    [Fact]
    public async Task Invoice_91_To_120_Days_Overdue_Appears_In_91_120_Bucket()
    {
        var db = Build.NewFinanceDb();
        var supplierId = Guid.NewGuid();
        Build.SeedInvoice(db, supplierId, totalAmount: 7000m, paidAmount: 0m, daysOverdue: 100, invoiceNumber: "INV-100D");

        var aging = await Build.NewRepo(db).GetSupplierAgingAsync(supplierId);

        aging.Buckets.Single(b => b.BucketName == "91-120").Total.Should().Be(7000m);
    }

    [Fact]
    public async Task Invoice_Over_120_Days_Overdue_Appears_In_120_Plus_Bucket()
    {
        var db = Build.NewFinanceDb();
        var supplierId = Guid.NewGuid();
        Build.SeedInvoice(db, supplierId, totalAmount: 9000m, paidAmount: 0m, daysOverdue: 200, invoiceNumber: "INV-200D");

        var aging = await Build.NewRepo(db).GetSupplierAgingAsync(supplierId);

        aging.Buckets.Single(b => b.BucketName == "120+").Total.Should().Be(9000m);
    }

    [Fact]
    public async Task NotYetDue_Invoice_Appears_In_Current_Bucket()
    {
        var db = Build.NewFinanceDb();
        var supplierId = Guid.NewGuid();
        Build.SeedInvoice(db, supplierId, totalAmount: 4000m, paidAmount: 0m, daysOverdue: -10, invoiceNumber: "INV-FUTURE");

        var aging = await Build.NewRepo(db).GetSupplierAgingAsync(supplierId);

        aging.Buckets.Single(b => b.BucketName == "Current").Total.Should().Be(4000m);
        aging.Buckets.SelectMany(b => b.Invoices).Single().DaysOverdue.Should().Be(0);
    }

    [Fact]
    public async Task GrandTotal_Sums_All_Buckets()
    {
        var db = Build.NewFinanceDb();
        var supplierId = Guid.NewGuid();
        Build.SeedInvoice(db, supplierId, 1000m, 0m, 10,  "INV-A");
        Build.SeedInvoice(db, supplierId, 2000m, 0m, 45,  "INV-B");
        Build.SeedInvoice(db, supplierId, 3000m, 0m, 75,  "INV-C");
        Build.SeedInvoice(db, supplierId, 4000m, 0m, 100, "INV-D");
        Build.SeedInvoice(db, supplierId, 5000m, 0m, 150, "INV-E");

        var aging = await Build.NewRepo(db).GetSupplierAgingAsync(supplierId);

        aging.GrandTotal.Should().Be(15000m);
        aging.Buckets.Should().HaveCount(5);
    }

    [Fact]
    public async Task Returns_Empty_Buckets_For_Supplier_With_No_Outstanding_Invoices()
    {
        var db = Build.NewFinanceDb();

        var aging = await Build.NewRepo(db).GetSupplierAgingAsync(Guid.NewGuid());

        aging.GrandTotal.Should().Be(0m);
        aging.Buckets.Should().HaveCount(5);
        aging.Buckets.Should().OnlyContain(b => b.Total == 0m && b.Invoices.Count == 0);
    }
}

public class GetCrossSupplierAgingAsync_Tests
{
    [Fact]
    public async Task Aggregates_All_Suppliers_With_Outstanding_Payables()
    {
        var db = Build.NewFinanceDb();
        var supplierA = Guid.NewGuid();
        var supplierB = Guid.NewGuid();

        Build.SeedInvoice(db, supplierA, 10000m, 0m, 15, "INV-A1", supplierName: "Supplier A");
        Build.SeedInvoice(db, supplierA, 5000m,  0m, 75, "INV-A2", supplierName: "Supplier A");
        Build.SeedInvoice(db, supplierB, 8000m,  0m, 45, "INV-B1", supplierName: "Supplier B");
        Build.SeedInvoice(db, supplierB, 2000m,  2000m, 10, "INV-B2-PAID", supplierName: "Supplier B"); // fully paid, excluded

        var report = await Build.NewRepo(db).GetCrossSupplierAgingAsync();

        report.Suppliers.Should().HaveCount(2);

        var rowA = report.Suppliers.Single(s => s.SupplierId == supplierA);
        rowA.Current.Should().Be(10000m);
        rowA.Bucket61To90.Should().Be(5000m);
        rowA.GrandTotal.Should().Be(15000m);

        var rowB = report.Suppliers.Single(s => s.SupplierId == supplierB);
        rowB.Bucket31To60.Should().Be(8000m);
        rowB.GrandTotal.Should().Be(8000m);

        report.GrandTotalRow.GrandTotal.Should().Be(23000m);
        report.GrandTotalRow.Current.Should().Be(10000m);
        report.GrandTotalRow.Bucket31To60.Should().Be(8000m);
        report.GrandTotalRow.Bucket61To90.Should().Be(5000m);
        report.GrandTotalRow.SupplierName.Should().Be("Grand Total");
    }

    [Fact]
    public async Task Returns_Empty_Report_When_No_Outstanding_Invoices_Exist()
    {
        var db = Build.NewFinanceDb();

        var report = await Build.NewRepo(db).GetCrossSupplierAgingAsync();

        report.Suppliers.Should().BeEmpty();
        report.GrandTotalRow.GrandTotal.Should().Be(0m);
    }

    [Fact]
    public async Task Excludes_Suppliers_Whose_Only_Invoices_Are_Fully_Paid()
    {
        var db = Build.NewFinanceDb();
        var supplierId = Guid.NewGuid();
        Build.SeedInvoice(db, supplierId, 1000m, 1000m, 20, "INV-PAID");

        var report = await Build.NewRepo(db).GetCrossSupplierAgingAsync();

        report.Suppliers.Should().BeEmpty();
    }
}

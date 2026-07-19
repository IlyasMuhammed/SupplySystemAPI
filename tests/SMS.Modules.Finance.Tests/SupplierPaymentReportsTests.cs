using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
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
        string invoiceNumber = "INV-2026-00001", string? supplierName = null)
    {
        var status = paidAmount <= 0 ? "UNPAID" : paidAmount < totalAmount ? "PARTIALLY_PAID" : "FULLY_PAID";
        var inv = new Invoice
        {
            UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), InvoiceNumber = invoiceNumber,
            SupplierId = supplierId, SupplierName = supplierName ?? "Test Supplier",
            PoUuid = Guid.NewGuid(), PoNumber = "PO-2026-00001",
            InvoiceDate = DateTime.UtcNow.AddDays(-daysOverdue - 30), ReceivedDate = DateTime.UtcNow.AddDays(-daysOverdue - 30),
            DueDate = DateTime.UtcNow.Date.AddDays(-daysOverdue), Currency = "PKR",
            Subtotal = totalAmount, TaxAmount = 0m, TotalAmount = totalAmount,
            MatchedPoValue = totalAmount, MatchedGrnValue = 0m, VarianceAmount = 0m,
            MatchStatus = "Matched", PaymentStatus = status, PaidAmount = paidAmount,
            IsActive = true, CreatedBy = 1, CreatedDate = DateTime.UtcNow
        };
        db.Invoices.Add(inv);
        db.SaveChanges();
        return inv;
    }

    // Creates, approves, and posts a payment allocated against the given invoices.
    internal static async Task<Guid> PostedPayment(
        FinanceDbContext db, Guid supplierId, decimal totalAmount, string method,
        (Invoice invoice, decimal amount)[] allocations, DateTime? paymentDate = null,
        string? bankAccount = null, string? chequeNo = null)
    {
        var repo = NewRepo(db);
        var req = new CreateSupplierPaymentRequest
        {
            SupplierId = supplierId, SupplierName = "Test Supplier",
            PaymentDate = paymentDate ?? DateTime.UtcNow, PaymentMethod = method, TotalAmount = totalAmount,
            BankAccount = bankAccount, ChequeNo = chequeNo, ChequeDate = chequeNo is not null ? DateTime.UtcNow : null,
            Lines = allocations.Select(a => new CreateSupplierPaymentLineRequest { InvoiceUuid = a.invoice.UUID, AllocatedAmount = a.amount }).ToList()
        };
        var uuid = await repo.CreateAsync(req, createdBy: 1);
        await repo.ApproveAsync(uuid, approvedBy: 1);
        await repo.PostAsync(uuid, postedBy: 1);
        return uuid;
    }
}

public class GetPaymentRegisterAsync_Tests
{
    [Fact]
    public async Task Filters_By_Supplier_Status_Method_And_DateRange_Simultaneously()
    {
        var db = Build.NewFinanceDb();
        var supplierA = Guid.NewGuid();
        var supplierB = Guid.NewGuid();
        var invA = Build.SeedInvoice(db, supplierA, 1000m, 0m, 0, "INV-A");
        var invB = Build.SeedInvoice(db, supplierB, 1000m, 0m, 0, "INV-B");

        var target = await Build.PostedPayment(db, supplierA, 1000m, "CASH", [(invA, 1000m)], paymentDate: new DateTime(2026, 6, 15));
        await Build.PostedPayment(db, supplierA, 500m, "CHEQUE", [], paymentDate: new DateTime(2026, 6, 15), chequeNo: "CHQ-1"); // wrong method
        await Build.PostedPayment(db, supplierB, 1000m, "CASH", [(invB, 1000m)], paymentDate: new DateTime(2026, 6, 15)); // wrong supplier
        await Build.PostedPayment(db, supplierA, 300m, "CASH", [], paymentDate: new DateTime(2026, 1, 1)); // out of date range

        var repo = Build.NewRepo(db);
        var result = await repo.GetPaymentRegisterAsync(new PaymentRegisterFilter
        {
            SupplierId = supplierA, Status = "POSTED", Method = "CASH",
            DateFrom = new DateTime(2026, 6, 1), DateTo = new DateTime(2026, 6, 30)
        });

        result.Data.Should().ContainSingle();
        result.Data[0].Uuid.Should().Be(target);
    }

    [Fact]
    public async Task Filters_By_BankReference_Matching_BankAccount_Or_ChequeNo()
    {
        var db = Build.NewFinanceDb();
        var supplierId = Guid.NewGuid();

        await Build.PostedPayment(db, supplierId, 100m, "BANK_TRANSFER", [], bankAccount: "ACC-998877");
        await Build.PostedPayment(db, supplierId, 200m, "CHEQUE", [], chequeNo: "CHQ-998877");
        await Build.PostedPayment(db, supplierId, 300m, "CASH", []);

        var repo = Build.NewRepo(db);
        var result = await repo.GetPaymentRegisterAsync(new PaymentRegisterFilter { BankReference = "998877" });

        result.Data.Should().HaveCount(2);
        result.TotalRecords.Should().Be(2);
    }

    [Fact]
    public async Task Paginates_Results()
    {
        var db = Build.NewFinanceDb();
        var supplierId = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
            await Build.PostedPayment(db, supplierId, 100m, "CASH", []);

        var repo = Build.NewRepo(db);
        var page1 = await repo.GetPaymentRegisterAsync(new PaymentRegisterFilter { Page = 1, PageSize = 2 });

        page1.Data.Should().HaveCount(2);
        page1.TotalRecords.Should().Be(5);
        page1.TotalPages.Should().Be(3);
    }
}

public class SupplierLedgerReport_Tests
{
    [Fact]
    public async Task Ledger_Report_Running_Balance_Matches_Live_Balance()
    {
        var db = Build.NewFinanceDb();
        var supplierId = Guid.NewGuid();
        var inv1 = Build.SeedInvoice(db, supplierId, 10000m, 0m, 0, "INV-1");
        var inv2 = Build.SeedInvoice(db, supplierId, 5000m, 0m, 0, "INV-2");

        await Build.PostedPayment(db, supplierId, 10000m, "CASH", [(inv1, 10000m)]);
        await Build.PostedPayment(db, supplierId, 3000m, "CASH", [(inv2, 3000m)]);

        var ledgerSvc = new SupplierLedgerService(db);
        var ledgerReport = await ledgerSvc.GetLedgerAsync(supplierId, new SupplierLedgerFilter { PageSize = 50 });
        var liveBalance  = await ledgerSvc.GetBalanceAsync(supplierId);

        // Most recent entry (index 0, since GetLedgerAsync orders newest-first) carries the
        // current running balance — it must equal the live balance summary exactly.
        var mostRecentEntry = ledgerReport.Data.OrderByDescending(e => e.SequenceNo).First();
        mostRecentEntry.BalanceAfter.Should().Be(liveBalance.NetBalance);
    }
}

public class GetOutstandingPayablesAsync_Tests
{
    [Fact]
    public async Task Excludes_FULLY_PAID_Invoices()
    {
        var db = Build.NewFinanceDb();
        var supplierId = Guid.NewGuid();
        Build.SeedInvoice(db, supplierId, 1000m, 1000m, 10, "INV-PAID");
        Build.SeedInvoice(db, supplierId, 2000m, 0m, 10, "INV-UNPAID");

        var repo = Build.NewRepo(db);
        var result = await repo.GetOutstandingPayablesAsync(new OutstandingPayablesFilter());

        var group = result.Data.Single(g => g.SupplierId == supplierId);
        group.Invoices.Should().NotContain(i => i.InvoiceNumber == "INV-PAID");
        group.Invoices.Should().ContainSingle(i => i.InvoiceNumber == "INV-UNPAID");
        group.TotalOutstanding.Should().Be(2000m);
    }

    [Fact]
    public async Task Groups_By_Supplier_With_Correct_Per_Supplier_Totals()
    {
        var db = Build.NewFinanceDb();
        var supplierA = Guid.NewGuid();
        var supplierB = Guid.NewGuid();
        Build.SeedInvoice(db, supplierA, 1000m, 0m, 10, "INV-A1", supplierName: "Supplier A");
        Build.SeedInvoice(db, supplierA, 2000m, 500m, 20, "INV-A2", supplierName: "Supplier A");
        Build.SeedInvoice(db, supplierB, 5000m, 0m, 10, "INV-B1", supplierName: "Supplier B");

        var repo = Build.NewRepo(db);
        var result = await repo.GetOutstandingPayablesAsync(new OutstandingPayablesFilter());

        result.Data.Should().HaveCount(2);
        var groupA = result.Data.Single(g => g.SupplierId == supplierA);
        groupA.Invoices.Should().HaveCount(2);
        groupA.TotalOutstanding.Should().Be(2500m); // 1000 + (2000-500)

        var groupB = result.Data.Single(g => g.SupplierId == supplierB);
        groupB.TotalOutstanding.Should().Be(5000m);
    }
}

public class GetPaymentMethodBreakdownAsync_Tests
{
    [Fact]
    public async Task Totals_By_Method_Reconcile_With_Sum_Of_All_Posted_Payments_In_Range()
    {
        var db = Build.NewFinanceDb();
        var supplierId = Guid.NewGuid();

        await Build.PostedPayment(db, supplierId, 1000m, "CASH", [], paymentDate: new DateTime(2026, 6, 5));
        await Build.PostedPayment(db, supplierId, 2000m, "CASH", [], paymentDate: new DateTime(2026, 6, 10));
        await Build.PostedPayment(db, supplierId, 3000m, "BANK_TRANSFER", [], paymentDate: new DateTime(2026, 6, 15), bankAccount: "ACC-1");
        await Build.PostedPayment(db, supplierId, 500m, "CASH", [], paymentDate: new DateTime(2026, 1, 1)); // out of range

        var repo = Build.NewRepo(db);
        var report = await repo.GetPaymentMethodBreakdownAsync(new PaymentMethodBreakdownFilter
        {
            DateFrom = new DateTime(2026, 6, 1), DateTo = new DateTime(2026, 6, 30)
        });

        var cash = report.Methods.Single(m => m.Method == "CASH");
        cash.TotalAmount.Should().Be(3000m);
        cash.Count.Should().Be(2);

        var bank = report.Methods.Single(m => m.Method == "BANK_TRANSFER");
        bank.TotalAmount.Should().Be(3000m);

        report.GrandTotal.Should().Be(report.Methods.Sum(m => m.TotalAmount));
        report.GrandTotal.Should().Be(6000m);
        report.TotalCount.Should().Be(3);
    }

    [Fact]
    public async Task Excludes_NonPosted_Payments()
    {
        var db = Build.NewFinanceDb();
        var supplierId = Guid.NewGuid();
        var repo = Build.NewRepo(db);

        // DRAFT only — never approved/posted.
        await repo.CreateAsync(new CreateSupplierPaymentRequest
        {
            SupplierId = supplierId, SupplierName = "Test Supplier", PaymentDate = DateTime.UtcNow,
            PaymentMethod = "CASH", TotalAmount = 9999m
        }, createdBy: 1);

        var report = await repo.GetPaymentMethodBreakdownAsync(new PaymentMethodBreakdownFilter());

        report.Methods.Should().BeEmpty();
        report.GrandTotal.Should().Be(0m);
    }
}

// ── Read-only verification ──────────────────────────────────────────────────
// Every report query below projects straight into DTOs via AsNoTracking() — none of them ever
// stage a change. This is the honest, testable proxy for "read-only" available without adding a
// separate read-only database connection/interceptor: if nothing was ever tracked, a subsequent
// SaveChangesAsync is guaranteed to be a no-op, i.e. any accidental write attempt is inert.
public class ReadOnly_Verification_Tests
{
    [Fact]
    public async Task Report_Queries_Never_Track_Entities()
    {
        var db = Build.NewFinanceDb();
        var supplierId = Guid.NewGuid();
        var inv = Build.SeedInvoice(db, supplierId, 1000m, 0m, 10);
        await Build.PostedPayment(db, supplierId, 500m, "CASH", []);
        db.ChangeTracker.Clear();

        var repo = Build.NewRepo(db);

        await repo.GetPaymentRegisterAsync(new PaymentRegisterFilter());
        db.ChangeTracker.Entries().Should().BeEmpty("the payment register query must not track any entity");

        await repo.GetSupplierAgingAsync(supplierId);
        db.ChangeTracker.Entries().Should().BeEmpty("the aging query must not track any entity");

        await repo.GetCrossSupplierAgingAsync();
        db.ChangeTracker.Entries().Should().BeEmpty("the cross-supplier aging query must not track any entity");

        await repo.GetOutstandingPayablesAsync(new OutstandingPayablesFilter());
        db.ChangeTracker.Entries().Should().BeEmpty("the outstanding payables query must not track any entity");

        await repo.GetPaymentMethodBreakdownAsync(new PaymentMethodBreakdownFilter());
        db.ChangeTracker.Entries().Should().BeEmpty("the payment method breakdown query must not track any entity");

        var ledgerSvc = new SupplierLedgerService(db);
        await ledgerSvc.GetLedgerAsync(supplierId, new SupplierLedgerFilter());
        db.ChangeTracker.Entries().Should().BeEmpty("the ledger report query must not track any entity");

        // Since nothing is tracked, calling SaveChangesAsync after any of these report queries
        // is provably a no-op — there is nothing pending for it to write.
        var writesAttempted = await db.SaveChangesAsync();
        writesAttempted.Should().Be(0);
    }
}

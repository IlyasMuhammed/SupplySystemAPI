using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using Xunit;

namespace SMS.Modules.Finance.Tests;

file static class Build
{
    internal static FinanceDbContext NewDb(string? dbName = null) =>
        new(new DbContextOptionsBuilder<FinanceDbContext>()
            .UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString())
            .Options);

    internal static SupplierLedgerService NewService(FinanceDbContext db) => new(db);
}

// ── PostEntryAsync ───────────────────────────────────────────────────────────

public class PostEntryAsync_Tests
{
    [Fact]
    public async Task Debit_10000_On_Zero_Balance_Supplier_Creates_Entry_With_BalanceAfter_10000()
    {
        var svc        = Build.NewService(Build.NewDb());
        var supplierId = Guid.NewGuid();

        var entry = await svc.PostEntryAsync(
            supplierId, "INVOICE", "Invoice", Guid.NewGuid(), "INV-2026-00001",
            debitAmount: 10000m, creditAmount: 0m, narration: null, createdBy: 1);

        entry.BalanceAfter.Should().Be(10000m);
        entry.SequenceNo.Should().Be(1);
    }

    [Fact]
    public async Task Credit_5000_After_Debit_10000_Creates_Entry_With_BalanceAfter_5000()
    {
        var db  = Build.NewDb();
        var svc = Build.NewService(db);
        var supplierId = Guid.NewGuid();

        await svc.PostEntryAsync(supplierId, "INVOICE", "Invoice", Guid.NewGuid(), "INV-2026-00001",
            debitAmount: 10000m, creditAmount: 0m, narration: null, createdBy: 1);

        var second = await svc.PostEntryAsync(supplierId, "PAYMENT", "Payment", Guid.NewGuid(), "PAY-2026-00001",
            debitAmount: 0m, creditAmount: 5000m, narration: null, createdBy: 1);

        second.BalanceAfter.Should().Be(5000m);
        second.SequenceNo.Should().Be(2);
    }

    [Fact]
    public async Task Entries_For_Different_Suppliers_Track_Independent_Balances()
    {
        var db  = Build.NewDb();
        var svc = Build.NewService(db);
        var supplierA = Guid.NewGuid();
        var supplierB = Guid.NewGuid();

        var a = await svc.PostEntryAsync(supplierA, "INVOICE", "Invoice", Guid.NewGuid(), "INV-A", 1000m, 0m, null, 1);
        var b = await svc.PostEntryAsync(supplierB, "INVOICE", "Invoice", Guid.NewGuid(), "INV-B", 500m, 0m, null, 1);

        a.BalanceAfter.Should().Be(1000m);
        a.SequenceNo.Should().Be(1);
        b.BalanceAfter.Should().Be(500m);
        b.SequenceNo.Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_Posts_For_Same_Supplier_Both_Succeed_With_Sequential_Balances()
    {
        var dbName     = Guid.NewGuid().ToString();
        var supplierId = Guid.NewGuid();

        var svc1 = Build.NewService(Build.NewDb(dbName));
        var svc2 = Build.NewService(Build.NewDb(dbName));

        var results = await Task.WhenAll(
            svc1.PostEntryAsync(supplierId, "INVOICE", "Invoice", Guid.NewGuid(), "INV-A", 1000m, 0m, null, 1),
            svc2.PostEntryAsync(supplierId, "INVOICE", "Invoice", Guid.NewGuid(), "INV-B", 2000m, 0m, null, 1));

        // No lost update: sequence numbers are distinct and consecutive, one entry's balance
        // reflects the other having already been posted.
        results.Select(r => r.SequenceNo).Should().BeEquivalentTo([1, 2]);

        var verifyDb  = Build.NewDb(dbName);
        var verifySvc = Build.NewService(verifyDb);
        var ledger = await verifySvc.GetLedgerAsync(supplierId, new SupplierLedgerFilter { PageSize = 10 });

        ledger.Data.Should().HaveCount(2);
        var bySeq = ledger.Data.OrderBy(e => e.SequenceNo).ToList();
        bySeq[0].BalanceAfter.Should().Be(bySeq[0].DebitAmount);
        bySeq[1].BalanceAfter.Should().Be(bySeq[0].BalanceAfter + bySeq[1].DebitAmount);
    }

    [Fact]
    public async Task Many_Concurrent_Posts_For_Same_Supplier_Produce_No_Lost_Updates()
    {
        var dbName     = Guid.NewGuid().ToString();
        var supplierId = Guid.NewGuid();
        const int concurrency = 8;

        var tasks = Enumerable.Range(0, concurrency).Select(i =>
        {
            var svc = Build.NewService(Build.NewDb(dbName));
            return svc.PostEntryAsync(supplierId, "INVOICE", "Invoice", Guid.NewGuid(), $"INV-{i}", 100m, 0m, null, 1);
        });

        var results = await Task.WhenAll(tasks);

        results.Select(r => r.SequenceNo).Distinct().Should().HaveCount(concurrency);
        results.Select(r => r.SequenceNo).Should().BeEquivalentTo(Enumerable.Range(1, concurrency));

        var verifySvc = Build.NewService(Build.NewDb(dbName));
        var balance = await verifySvc.GetBalanceAsync(supplierId);
        balance.TotalDebits.Should().Be(100m * concurrency);
        balance.NetBalance.Should().Be(100m * concurrency);
    }
}

// ── GetLedgerAsync ───────────────────────────────────────────────────────────

public class GetLedgerAsync_Tests
{
    [Fact]
    public async Task Returns_Entries_In_Reverse_Chronological_Order_With_Running_Balance()
    {
        var db  = Build.NewDb();
        var svc = Build.NewService(db);
        var supplierId = Guid.NewGuid();

        await svc.PostEntryAsync(supplierId, "INVOICE", "Invoice", Guid.NewGuid(), "INV-1", 1000m, 0m, null, 1);
        await svc.PostEntryAsync(supplierId, "INVOICE", "Invoice", Guid.NewGuid(), "INV-2", 500m, 0m, null, 1);
        await svc.PostEntryAsync(supplierId, "PAYMENT", "Payment", Guid.NewGuid(), "PAY-1", 0m, 300m, null, 1);

        var ledger = await svc.GetLedgerAsync(supplierId, new SupplierLedgerFilter());

        ledger.Data.Select(e => e.ReferenceNo).Should().Equal("PAY-1", "INV-2", "INV-1");
        ledger.Data.Select(e => e.BalanceAfter).Should().Equal(1200m, 1500m, 1000m);
        ledger.TotalRecords.Should().Be(3);
    }

    [Fact]
    public async Task Filters_By_Date_Range()
    {
        var db  = Build.NewDb();
        var svc = Build.NewService(db);
        var supplierId = Guid.NewGuid();

        await svc.PostEntryAsync(supplierId, "INVOICE", "Invoice", Guid.NewGuid(), "INV-1", 1000m, 0m, null, 1);

        var futureFilter = new SupplierLedgerFilter { DateFrom = DateTime.UtcNow.AddDays(1) };
        var futureLedger = await svc.GetLedgerAsync(supplierId, futureFilter);
        futureLedger.Data.Should().BeEmpty();

        var pastFilter = new SupplierLedgerFilter { DateFrom = DateTime.UtcNow.AddDays(-1) };
        var pastLedger = await svc.GetLedgerAsync(supplierId, pastFilter);
        pastLedger.Data.Should().ContainSingle();
    }

    [Fact]
    public async Task Paginates_Correctly()
    {
        var db  = Build.NewDb();
        var svc = Build.NewService(db);
        var supplierId = Guid.NewGuid();

        for (var i = 0; i < 5; i++)
            await svc.PostEntryAsync(supplierId, "INVOICE", "Invoice", Guid.NewGuid(), $"INV-{i}", 100m, 0m, null, 1);

        var page1 = await svc.GetLedgerAsync(supplierId, new SupplierLedgerFilter { Page = 1, PageSize = 2 });
        var page2 = await svc.GetLedgerAsync(supplierId, new SupplierLedgerFilter { Page = 2, PageSize = 2 });

        page1.Data.Should().HaveCount(2);
        page2.Data.Should().HaveCount(2);
        page1.TotalRecords.Should().Be(5);
        page1.TotalPages.Should().Be(3);
        page1.Data.Select(e => e.Uuid).Should().NotIntersectWith(page2.Data.Select(e => e.Uuid));
    }

    [Fact]
    public async Task Returns_Empty_For_Supplier_With_No_Entries()
    {
        var svc = Build.NewService(Build.NewDb());

        var ledger = await svc.GetLedgerAsync(Guid.NewGuid(), new SupplierLedgerFilter());

        ledger.Data.Should().BeEmpty();
        ledger.TotalRecords.Should().Be(0);
    }
}

// ── GetBalanceAsync ──────────────────────────────────────────────────────────

public class GetBalanceAsync_Tests
{
    [Fact]
    public async Task Correctly_Sums_Debits_Credits_And_Net_Balance()
    {
        var db  = Build.NewDb();
        var svc = Build.NewService(db);
        var supplierId = Guid.NewGuid();

        await svc.PostEntryAsync(supplierId, "INVOICE", "Invoice", Guid.NewGuid(), "INV-1", 10000m, 0m, null, 1);
        await svc.PostEntryAsync(supplierId, "INVOICE", "Invoice", Guid.NewGuid(), "INV-2", 2000m, 0m, null, 1);
        await svc.PostEntryAsync(supplierId, "PAYMENT", "Payment", Guid.NewGuid(), "PAY-1", 0m, 5000m, null, 1);

        var balance = await svc.GetBalanceAsync(supplierId);

        balance.TotalDebits.Should().Be(12000m);
        balance.TotalCredits.Should().Be(5000m);
        balance.NetBalance.Should().Be(7000m);
        balance.AvailableAdvanceCredit.Should().Be(0m);
    }

    [Fact]
    public async Task Overpayment_Produces_Available_Advance_Credit()
    {
        var db  = Build.NewDb();
        var svc = Build.NewService(db);
        var supplierId = Guid.NewGuid();

        await svc.PostEntryAsync(supplierId, "INVOICE", "Invoice", Guid.NewGuid(), "INV-1", 1000m, 0m, null, 1);
        await svc.PostEntryAsync(supplierId, "PAYMENT", "Payment", Guid.NewGuid(), "PAY-1", 0m, 1500m, null, 1);

        var balance = await svc.GetBalanceAsync(supplierId);

        balance.NetBalance.Should().Be(-500m);
        balance.AvailableAdvanceCredit.Should().Be(500m);
    }

    [Fact]
    public async Task Returns_Zeros_For_Supplier_With_No_Entries()
    {
        var svc = Build.NewService(Build.NewDb());

        var balance = await svc.GetBalanceAsync(Guid.NewGuid());

        balance.TotalDebits.Should().Be(0m);
        balance.TotalCredits.Should().Be(0m);
        balance.NetBalance.Should().Be(0m);
        balance.AvailableAdvanceCredit.Should().Be(0m);
    }
}

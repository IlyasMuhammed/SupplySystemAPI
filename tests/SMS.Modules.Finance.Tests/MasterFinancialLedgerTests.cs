using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Repositories;
using SMS.Modules.Finance.Services;
using SMS.Modules.Warehouse.Data;
using Xunit;

namespace SMS.Modules.Finance.Tests;

file static class Build
{
    internal static FinanceDbContext NewFinanceDb(string? dbName = null) =>
        new(new DbContextOptionsBuilder<FinanceDbContext>()
            .UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString())
            .Options);

    internal static DemandDbContext NewDemandDb(string? dbName = null) =>
        new(new DbContextOptionsBuilder<DemandDbContext>()
            .UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString())
            .Options);

    internal static WarehouseDbContext NewWarehouseDb(string? dbName = null) =>
        new(new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString())
            .Options);

    internal static Invoice SeedInvoice(FinanceDbContext db, Guid supplierId, string supplierName, decimal totalAmount, string invoiceNumber)
    {
        var inv = new Invoice
        {
            UUID              = Guid.NewGuid(),
            TraceId           = Guid.NewGuid(),
            InvoiceNumber     = invoiceNumber,
            SupplierId        = supplierId,
            SupplierName      = supplierName,
            PoUuid            = Guid.NewGuid(),
            PoNumber          = "PO-2026-00001",
            InvoiceDate       = DateTime.UtcNow,
            ReceivedDate      = DateTime.UtcNow,
            DueDate           = DateTime.UtcNow.AddDays(30),
            Currency          = "PKR",
            Subtotal          = totalAmount,
            TaxAmount         = 0m,
            TotalAmount       = totalAmount,
            MatchedPoValue    = totalAmount,
            MatchedGrnValue   = 0m,
            VarianceAmount    = 0m,
            MatchStatus       = "Matched",
            PaymentStatus     = "Unpaid",
            IsActive          = true,
            CreatedBy         = 1,
            CreatedDate       = DateTime.UtcNow
        };
        db.Invoices.Add(inv);
        db.SaveChanges();
        return inv;
    }
}

// ── ML-001: MasterFinancialLedger write integration ─────────────────────────

public class MasterFinancialLedger_PostEntryAsync_Tests
{
    [Fact]
    public async Task Posting_An_Entry_Writes_Both_A_SupplierLedgerEntry_And_A_MasterFinancialLedger_Entry()
    {
        var db         = Build.NewFinanceDb();
        var ledger     = new SupplierLedgerService(db);
        var supplierId = Guid.NewGuid();

        await ledger.PostEntryAsync(
            supplierId, "INVOICE_APPROVED", "Invoice", Guid.NewGuid(), "INV-A",
            debitAmount: 400000m, creditAmount: 0m, narration: null, createdBy: 1,
            supplierName: "Acme Supplies");

        (await db.SupplierLedgerEntries.CountAsync()).Should().Be(1);

        var master = await db.MasterFinancialLedgers.SingleAsync();
        master.SupplierId.Should().Be(supplierId);
        master.SupplierName.Should().Be("Acme Supplies");
        master.TransactionType.Should().Be("INVOICE_APPROVED");
        master.DebitAmount.Should().Be(400000m);
        master.BalanceAfter.Should().Be(400000m);
        master.SequenceNo.Should().Be(1);
    }

    [Fact]
    public async Task Master_Balance_Accumulates_Across_Multiple_Suppliers()
    {
        var db     = Build.NewFinanceDb();
        var ledger = new SupplierLedgerService(db);

        // Invoice A (supplier 1, 400,000) + Invoice B (supplier 2, 150,000) = master balance 550,000,
        // even though each supplier's OWN per-supplier balance only reflects their own invoice.
        await ledger.PostEntryAsync(Guid.NewGuid(), "INVOICE_APPROVED", "Invoice", Guid.NewGuid(), "INV-A",
            debitAmount: 400000m, creditAmount: 0m, narration: null, createdBy: 1, supplierName: "Supplier 1");
        await ledger.PostEntryAsync(Guid.NewGuid(), "INVOICE_APPROVED", "Invoice", Guid.NewGuid(), "INV-B",
            debitAmount: 150000m, creditAmount: 0m, narration: null, createdBy: 1, supplierName: "Supplier 2");

        var master = await db.MasterFinancialLedgers.OrderBy(e => e.SequenceNo).ToListAsync();
        master.Should().HaveCount(2);
        master[0].BalanceAfter.Should().Be(400000m);
        master[1].BalanceAfter.Should().Be(550000m);
    }

    [Fact]
    public async Task Payment_Reduces_Master_Balance_From_550000_To_250000()
    {
        var db          = Build.NewFinanceDb();
        var ledger      = new SupplierLedgerService(db);
        var supplier1Id = Guid.NewGuid();

        await ledger.PostEntryAsync(supplier1Id, "INVOICE_APPROVED", "Invoice", Guid.NewGuid(), "INV-A",
            debitAmount: 400000m, creditAmount: 0m, narration: null, createdBy: 1, supplierName: "Supplier 1");
        await ledger.PostEntryAsync(Guid.NewGuid(), "INVOICE_APPROVED", "Invoice", Guid.NewGuid(), "INV-B",
            debitAmount: 150000m, creditAmount: 0m, narration: null, createdBy: 1, supplierName: "Supplier 2");

        await ledger.PostEntryAsync(supplier1Id, "PAYMENT", "SupplierPayment", Guid.NewGuid(), "SPAY-A",
            debitAmount: 0m, creditAmount: 300000m, narration: null, createdBy: 1, supplierName: "Supplier 1");

        var last = await db.MasterFinancialLedgers.OrderByDescending(e => e.SequenceNo).FirstAsync();
        last.TransactionType.Should().Be("PAYMENT");
        last.CreditAmount.Should().Be(300000m);
        last.BalanceAfter.Should().Be(250000m);
    }

    [Fact]
    public async Task Credit_Note_Creates_A_Master_Credit_Entry_With_Correct_Reference()
    {
        var db          = Build.NewFinanceDb();
        var ledger      = new SupplierLedgerService(db);
        var supplierId  = Guid.NewGuid();
        var creditNoteId = Guid.NewGuid();

        await ledger.PostEntryAsync(supplierId, "INVOICE_APPROVED", "Invoice", Guid.NewGuid(), "INV-A",
            debitAmount: 10000m, creditAmount: 0m, narration: null, createdBy: 1, supplierName: "Supplier 1");
        await ledger.PostEntryAsync(supplierId, "CREDIT_NOTE", "CreditNote", creditNoteId, "CN-2026-00001",
            debitAmount: 0m, creditAmount: 4000m, narration: null, createdBy: 1, supplierName: "Supplier 1");

        var master = await db.MasterFinancialLedgers.OrderByDescending(e => e.SequenceNo).FirstAsync();
        master.TransactionType.Should().Be("CREDIT_NOTE");
        master.ReferenceType.Should().Be("CreditNote");
        master.ReferenceId.Should().Be(creditNoteId);
        master.CreditAmount.Should().Be(4000m);
        master.BalanceAfter.Should().Be(6000m);
    }

    [Fact]
    public async Task Simulated_Master_Ledger_Failure_Rolls_Back_The_SupplierLedgerEntry_And_The_Invoice_Approval()
    {
        var dbName   = Guid.NewGuid().ToString();
        var finDb    = Build.NewFinanceDb(dbName);
        var demandDb = Build.NewDemandDb();
        var whDb     = Build.NewWarehouseDb();

        var inv = Build.SeedInvoice(finDb, Guid.NewGuid(), "Test Supplier", 50000m, "INV-2026-00001");

        var failingMaster = new Mock<IMasterFinancialLedgerService>();
        failingMaster
            .Setup(m => m.BuildAndTrackEntryAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<decimal>(),
                It.IsAny<string?>(), It.IsAny<int>()))
            .ThrowsAsync(new InvalidOperationException("Simulated master ledger failure"));

        var ledger = new SupplierLedgerService(finDb, failingMaster.Object);
        var repo   = new InvoiceRepository(finDb, demandDb, whDb, ledger);

        var act = async () => await repo.ApproveAsync(inv.UUID, "notes", approvedBy: 1);
        await act.Should().ThrowAsync<InvalidOperationException>();

        // Fresh context against the same in-memory database — proves nothing was actually
        // persisted for any of the three writes (invoice, supplier ledger, master ledger).
        var verifyDb = Build.NewFinanceDb(dbName);
        var reloaded = await verifyDb.Invoices.AsNoTracking().FirstAsync(x => x.UUID == inv.UUID);
        reloaded.MatchStatus.Should().NotBe("Approved");
        reloaded.ApprovedBy.Should().BeNull();

        (await verifyDb.SupplierLedgerEntries.AnyAsync()).Should().BeFalse();
        (await verifyDb.MasterFinancialLedgers.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Two_Concurrent_Invoice_Approvals_For_Different_Suppliers_Both_Succeed_With_Sequential_Master_Balances()
    {
        var dbName = Guid.NewGuid().ToString();

        var finDb1 = Build.NewFinanceDb(dbName);
        var finDb2 = Build.NewFinanceDb(dbName);
        var demandDb1 = Build.NewDemandDb();
        var demandDb2 = Build.NewDemandDb();
        var whDb1 = Build.NewWarehouseDb();
        var whDb2 = Build.NewWarehouseDb();

        var inv1 = Build.SeedInvoice(finDb1, Guid.NewGuid(), "Supplier 1", 400000m, "INV-A");
        var inv2 = Build.SeedInvoice(finDb2, Guid.NewGuid(), "Supplier 2", 150000m, "INV-B");

        var ledger1 = new SupplierLedgerService(finDb1);
        var ledger2 = new SupplierLedgerService(finDb2);
        var repo1   = new InvoiceRepository(finDb1, demandDb1, whDb1, ledger1);
        var repo2   = new InvoiceRepository(finDb2, demandDb2, whDb2, ledger2);

        var results = await Task.WhenAll(
            repo1.ApproveAsync(inv1.UUID, null, approvedBy: 1),
            repo2.ApproveAsync(inv2.UUID, null, approvedBy: 1));

        results.Should().AllBeEquivalentTo(true);

        var verifyDb = Build.NewFinanceDb(dbName);
        var master   = await verifyDb.MasterFinancialLedgers.OrderBy(e => e.SequenceNo).ToListAsync();

        master.Should().HaveCount(2);
        master.Select(m => m.SequenceNo).Should().BeEquivalentTo([1, 2]);
        // Whichever order they landed in, the running balance must be strictly sequential —
        // no lost update, no two entries sharing a SequenceNo or an incorrect BalanceAfter.
        master[0].BalanceAfter.Should().Be(master[0].DebitAmount - master[0].CreditAmount);
        master[1].BalanceAfter.Should().Be(master[0].BalanceAfter + master[1].DebitAmount - master[1].CreditAmount);
        (master[0].DebitAmount + master[1].DebitAmount).Should().Be(550000m);
    }

    [Theory]
    [InlineData("INVOICE_APPROVED", 100, 0, 100)]
    [InlineData("PAYMENT", 0, 100, -100)]
    [InlineData("CREDIT_NOTE", 0, 100, -100)]
    [InlineData("DEBIT_NOTE", 0, 100, -100)]
    [InlineData("ADVANCE_PAYMENT", 0, 100, -100)]
    [InlineData("ADVANCE_ADJUSTMENT", 100, 0, 100)]
    [InlineData("RETENTION_HOLD", 0, 100, -100)]
    [InlineData("RETENTION_RELEASE", 100, 0, 100)]
    [InlineData("CHEQUE_BOUNCE_REVERSAL", 100, 0, 100)]
    [InlineData("BAD_DEBT_WRITEOFF", 0, 100, -100)]
    [InlineData("OPENING_BALANCE", 100, 0, 100)]
    public async Task All_11_Transaction_Types_Create_A_Master_Entry_With_Correct_Debit_Or_Credit_Direction(
        string transactionType, int debitInt, int creditInt, int expectedBalanceAfterInt)
    {
        decimal debit = debitInt, credit = creditInt, expectedBalanceAfter = expectedBalanceAfterInt;

        var db     = Build.NewFinanceDb();
        var ledger = new SupplierLedgerService(db);

        await ledger.PostEntryAsync(
            Guid.NewGuid(), transactionType, "Reference", Guid.NewGuid(), "REF-0001",
            debitAmount: debit, creditAmount: credit, narration: null, createdBy: 1, supplierName: "Test Supplier");

        var master = await db.MasterFinancialLedgers.SingleAsync();
        master.TransactionType.Should().Be(transactionType);
        master.DebitAmount.Should().Be(debit);
        master.CreditAmount.Should().Be(credit);
        master.BalanceAfter.Should().Be(expectedBalanceAfter);
    }
}

// ── ML-002: read-side (GetLedgerAsync / GetSummaryAsync / GetCurrentBalanceAsync) ──

public class MasterLedgerQueryService_Tests
{
    [Fact]
    public async Task Unfiltered_View_Shows_All_Entries_Across_All_Suppliers_With_Correct_Running_Balance()
    {
        var db     = Build.NewFinanceDb();
        var ledger = new SupplierLedgerService(db);
        var query  = new MasterFinancialLedgerService(db);

        await ledger.PostEntryAsync(Guid.NewGuid(), "INVOICE_APPROVED", "Invoice", Guid.NewGuid(), "INV-A",
            400000m, 0m, null, 1, supplierName: "Supplier 1");
        await ledger.PostEntryAsync(Guid.NewGuid(), "INVOICE_APPROVED", "Invoice", Guid.NewGuid(), "INV-B",
            150000m, 0m, null, 1, supplierName: "Supplier 2");

        var result = await query.GetLedgerAsync(new MasterLedgerFilter { PageSize = 50 });

        result.TotalRecords.Should().Be(2);
        result.Data.Should().HaveCount(2);
        // Most recent first.
        result.Data[0].SupplierName.Should().Be("Supplier 2");
        result.Data[0].BalanceAfter.Should().Be(550000m);
        result.Data[1].SupplierName.Should().Be("Supplier 1");
        result.Data[1].BalanceAfter.Should().Be(400000m);
    }

    [Fact]
    public async Task Filtering_By_Supplier_Shows_Only_That_Suppliers_Entries_But_BalanceAfter_Is_The_OrgLevel_Total()
    {
        var db          = Build.NewFinanceDb();
        var ledger      = new SupplierLedgerService(db);
        var query       = new MasterFinancialLedgerService(db);
        var supplier1Id = Guid.NewGuid();

        await ledger.PostEntryAsync(supplier1Id, "INVOICE_APPROVED", "Invoice", Guid.NewGuid(), "INV-A",
            400000m, 0m, null, 1, supplierName: "Supplier 1");
        await ledger.PostEntryAsync(Guid.NewGuid(), "INVOICE_APPROVED", "Invoice", Guid.NewGuid(), "INV-B",
            150000m, 0m, null, 1, supplierName: "Supplier 2");

        var result = await query.GetLedgerAsync(new MasterLedgerFilter { SupplierId = supplier1Id, PageSize = 50 });

        result.Data.Should().ContainSingle();
        // Balance shown is the ORG-level running total at the time of that entry (400,000), not
        // some recomputed per-supplier-only total — it's simply the value already stored on the row.
        result.Data[0].BalanceAfter.Should().Be(400000m);
    }

    [Fact]
    public async Task Filtering_By_TransactionType_Payment_Shows_Only_Payment_Entries()
    {
        var db          = Build.NewFinanceDb();
        var ledger      = new SupplierLedgerService(db);
        var query       = new MasterFinancialLedgerService(db);
        var supplierId  = Guid.NewGuid();

        await ledger.PostEntryAsync(supplierId, "INVOICE_APPROVED", "Invoice", Guid.NewGuid(), "INV-A",
            10000m, 0m, null, 1, supplierName: "Supplier 1");
        await ledger.PostEntryAsync(supplierId, "PAYMENT", "SupplierPayment", Guid.NewGuid(), "SPAY-A",
            0m, 3000m, null, 1, supplierName: "Supplier 1");

        var result = await query.GetLedgerAsync(new MasterLedgerFilter { TransactionTypes = ["PAYMENT"], PageSize = 50 });

        result.Data.Should().ContainSingle();
        result.Data[0].TransactionType.Should().Be("PAYMENT");
    }

    [Fact]
    public async Task Summary_Reflects_Filtered_Period_Totals_While_TotalPayables_Stays_OrgLevel_And_Unfiltered()
    {
        var db          = Build.NewFinanceDb();
        var ledger      = new SupplierLedgerService(db);
        var query       = new MasterFinancialLedgerService(db);
        var supplier1Id = Guid.NewGuid();

        await ledger.PostEntryAsync(supplier1Id, "INVOICE_APPROVED", "Invoice", Guid.NewGuid(), "INV-A",
            400000m, 0m, null, 1, supplierName: "Supplier 1");
        await ledger.PostEntryAsync(Guid.NewGuid(), "INVOICE_APPROVED", "Invoice", Guid.NewGuid(), "INV-B",
            150000m, 0m, null, 1, supplierName: "Supplier 2");
        await ledger.PostEntryAsync(supplier1Id, "PAYMENT", "SupplierPayment", Guid.NewGuid(), "SPAY-A",
            0m, 300000m, null, 1, supplierName: "Supplier 1");

        // Filtered to supplier 1 only: debits 400,000, credits 300,000 for THAT supplier's rows —
        // but TotalPayables must still be the org-wide current balance (250,000), not scoped to
        // supplier 1's own balance.
        var summary = await query.GetSummaryAsync(new MasterLedgerFilter { SupplierId = supplier1Id });

        summary.TotalDebits.Should().Be(400000m);
        summary.TotalCredits.Should().Be(300000m);
        summary.NetMovement.Should().Be(100000m);
        summary.TotalPayables.Should().Be(250000m);
    }

    [Fact]
    public async Task GetCurrentBalanceAsync_Returns_The_Latest_Entrys_BalanceAfter()
    {
        var db     = Build.NewFinanceDb();
        var ledger = new SupplierLedgerService(db);
        var query  = new MasterFinancialLedgerService(db);

        await ledger.PostEntryAsync(Guid.NewGuid(), "INVOICE_APPROVED", "Invoice", Guid.NewGuid(), "INV-A",
            400000m, 0m, null, 1, supplierName: "Supplier 1");
        await ledger.PostEntryAsync(Guid.NewGuid(), "PAYMENT", "SupplierPayment", Guid.NewGuid(), "SPAY-A",
            0m, 150000m, null, 1, supplierName: "Supplier 1");

        var balance = await query.GetCurrentBalanceAsync();
        balance.Balance.Should().Be(250000m);
    }
}
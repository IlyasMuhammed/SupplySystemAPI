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
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Finance.Tests;

// FSD Addendum 24 (ML-005) — edge cases: opening balance import, bad debt write-off, cheque
// bounce reversal, negative master balance, and concurrent multi-supplier activity.

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
            PaymentStatus     = "UNPAID",
            IsActive          = true,
            CreatedBy         = 1,
            CreatedDate       = DateTime.UtcNow
        };
        db.Invoices.Add(inv);
        db.SaveChanges();
        return inv;
    }

    internal static SupplierPaymentRepository NewPaymentRepo(FinanceDbContext db, ISupplierLedgerService ledger) =>
        new(db, ledger, new Mock<INotificationService>().Object);

    internal static async Task<Guid> PostedChequePayment(
        FinanceDbContext db, ISupplierLedgerService ledger, Guid supplierId, string supplierName,
        decimal totalAmount, (Invoice invoice, decimal amount)[] allocations)
    {
        var repo = NewPaymentRepo(db, ledger);
        var req = new CreateSupplierPaymentRequest
        {
            SupplierId    = supplierId,
            SupplierName  = supplierName,
            PaymentDate   = DateTime.UtcNow,
            PaymentMethod = "CHEQUE",
            ChequeNo      = "CHQ-0001",
            ChequeDate    = DateTime.UtcNow,
            TotalAmount   = totalAmount,
            Lines = allocations
                .Select(a => new CreateSupplierPaymentLineRequest { InvoiceUuid = a.invoice.UUID, AllocatedAmount = a.amount })
                .ToList()
        };
        var uuid = await repo.CreateAsync(req, createdBy: 1);
        await repo.ApproveAsync(uuid, approvedBy: 1);
        await repo.PostAsync(uuid, postedBy: 1);
        return uuid;
    }
}

// ── Opening Balance Import ───────────────────────────────────────────────────

public class OpeningBalanceImport_Tests
{
    [Fact]
    public async Task Importing_3_Suppliers_Creates_3_Master_Entries_With_Correct_Individual_Debits_And_Cumulative_Balance()
    {
        var db  = Build.NewFinanceDb();
        var svc = new OpeningBalanceService(db, new SupplierLedgerService(db));

        var req = new OpeningBalanceImportRequest
        {
            Suppliers =
            [
                new OpeningBalanceLineRequest { SupplierId = Guid.NewGuid(), SupplierName = "Supplier A", Amount = 100000m },
                new OpeningBalanceLineRequest { SupplierId = Guid.NewGuid(), SupplierName = "Supplier B", Amount = 250000m },
                new OpeningBalanceLineRequest { SupplierId = Guid.NewGuid(), SupplierName = "Supplier C", Amount = 75000m }
            ]
        };

        var result = await svc.ImportAsync(req, importedBy: 1);

        result.EntriesCreated.Should().Be(3);
        result.TotalImported.Should().Be(425000m);
        result.MasterBalanceAfter.Should().Be(425000m);

        var entries = await db.MasterFinancialLedgers.OrderBy(e => e.SequenceNo).ToListAsync();
        entries.Should().HaveCount(3);
        entries.All(e => e.TransactionType == "OPENING_BALANCE").Should().BeTrue();
        entries[0].DebitAmount.Should().Be(100000m);
        entries[0].BalanceAfter.Should().Be(100000m);
        entries[1].DebitAmount.Should().Be(250000m);
        entries[1].BalanceAfter.Should().Be(350000m);
        entries[2].DebitAmount.Should().Be(75000m);
        entries[2].BalanceAfter.Should().Be(425000m);
    }

    [Fact]
    public async Task Running_The_Import_A_Second_Time_Is_Rejected()
    {
        var db  = Build.NewFinanceDb();
        var svc = new OpeningBalanceService(db, new SupplierLedgerService(db));

        await svc.ImportAsync(new OpeningBalanceImportRequest
        {
            Suppliers = [new OpeningBalanceLineRequest { SupplierId = Guid.NewGuid(), SupplierName = "Supplier A", Amount = 100000m }]
        }, importedBy: 1);

        var act = async () => await svc.ImportAsync(new OpeningBalanceImportRequest
        {
            Suppliers = [new OpeningBalanceLineRequest { SupplierId = Guid.NewGuid(), SupplierName = "Supplier D", Amount = 50000m }]
        }, importedBy: 1);

        await act.Should().ThrowAsync<UnprocessableEntityException>();

        // Only the first import's entry exists — the second attempt wrote nothing.
        (await db.MasterFinancialLedgers.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Import_With_Zero_Or_Negative_Amount_Is_Rejected()
    {
        var db  = Build.NewFinanceDb();
        var svc = new OpeningBalanceService(db, new SupplierLedgerService(db));

        var act = async () => await svc.ImportAsync(new OpeningBalanceImportRequest
        {
            Suppliers = [new OpeningBalanceLineRequest { SupplierId = Guid.NewGuid(), SupplierName = "Supplier A", Amount = 0m }]
        }, importedBy: 1);

        await act.Should().ThrowAsync<BadRequestException>();
    }
}

// ── Bad Debt Write-off ───────────────────────────────────────────────────────

public class DebtWriteOff_Tests
{
    [Fact]
    public async Task Approving_A_15000_WriteOff_Creates_A_Credit_Entry_Reducing_Balance_By_15000()
    {
        var db         = Build.NewFinanceDb();
        var ledger     = new SupplierLedgerService(db);
        var writeOffSvc = new DebtWriteOffService(db, ledger);
        var supplierId = Guid.NewGuid();

        await ledger.PostEntryAsync(supplierId, "INVOICE_APPROVED", "Invoice", Guid.NewGuid(), "INV-A",
            debitAmount: 50000m, creditAmount: 0m, narration: null, createdBy: 1, supplierName: "Supplier A");

        var uuid = await writeOffSvc.CreateAsync(new CreateWriteOffRequest
        {
            SupplierId = supplierId, SupplierName = "Supplier A", Amount = 15000m, Reason = "Uncollectable — supplier insolvent"
        }, createdBy: 1);

        // Not posted yet — no ledger effect until a Finance Manager approves.
        var pending = await writeOffSvc.GetByIdAsync(uuid);
        pending!.Status.Should().Be("PENDING_APPROVAL");
        (await ledger.GetBalanceAsync(supplierId)).NetBalance.Should().Be(50000m);

        var approved = await writeOffSvc.ApproveAsync(uuid, approvedBy: 7);
        approved.Should().BeTrue();

        var writeOff = await writeOffSvc.GetByIdAsync(uuid);
        writeOff!.Status.Should().Be("APPROVED");
        writeOff.ApprovedBy.Should().Be(7);
        writeOff.ApprovedAt.Should().NotBeNull();

        var balance = await ledger.GetBalanceAsync(supplierId);
        balance.NetBalance.Should().Be(35000m); // 50000 - 15000

        var master = await db.MasterFinancialLedgers.OrderByDescending(e => e.SequenceNo).FirstAsync();
        master.TransactionType.Should().Be("BAD_DEBT_WRITEOFF");
        master.CreditAmount.Should().Be(15000m);
        master.BalanceAfter.Should().Be(35000m);
    }

    [Fact]
    public async Task Rejecting_A_WriteOff_Has_No_Ledger_Effect()
    {
        var db          = Build.NewFinanceDb();
        var ledger      = new SupplierLedgerService(db);
        var writeOffSvc = new DebtWriteOffService(db, ledger);
        var supplierId  = Guid.NewGuid();

        await ledger.PostEntryAsync(supplierId, "INVOICE_APPROVED", "Invoice", Guid.NewGuid(), "INV-A",
            debitAmount: 50000m, creditAmount: 0m, narration: null, createdBy: 1, supplierName: "Supplier A");

        var uuid = await writeOffSvc.CreateAsync(new CreateWriteOffRequest
        {
            SupplierId = supplierId, SupplierName = "Supplier A", Amount = 15000m, Reason = "Disputed"
        }, createdBy: 1);

        await writeOffSvc.RejectAsync(uuid, "Insufficient evidence of default", rejectedBy: 7);

        var writeOff = await writeOffSvc.GetByIdAsync(uuid);
        writeOff!.Status.Should().Be("REJECTED");
        writeOff.RejectionReason.Should().Be("Insufficient evidence of default");

        (await ledger.GetBalanceAsync(supplierId)).NetBalance.Should().Be(50000m); // unchanged
        (await db.MasterFinancialLedgers.CountAsync(e => e.TransactionType == "BAD_DEBT_WRITEOFF")).Should().Be(0);
    }

    [Fact]
    public async Task Approving_An_Already_Approved_WriteOff_Throws()
    {
        var db          = Build.NewFinanceDb();
        var ledger      = new SupplierLedgerService(db);
        var writeOffSvc = new DebtWriteOffService(db, ledger);
        var supplierId  = Guid.NewGuid();

        var uuid = await writeOffSvc.CreateAsync(new CreateWriteOffRequest
        {
            SupplierId = supplierId, SupplierName = "Supplier A", Amount = 1000m, Reason = "Test"
        }, createdBy: 1);
        await writeOffSvc.ApproveAsync(uuid, approvedBy: 7);

        var act = async () => await writeOffSvc.ApproveAsync(uuid, approvedBy: 7);
        await act.Should().ThrowAsync<UnprocessableEntityException>();
    }
}

// ── Cheque Bounce Reversal (master ledger) ──────────────────────────────────

public class MasterLedger_ChequeBounce_Tests
{
    [Fact]
    public async Task Bounce_After_A_300000_Payment_Creates_A_300000_Debit_Restoring_The_PrePayment_Balance()
    {
        var dbName   = Guid.NewGuid().ToString();
        var finDb    = Build.NewFinanceDb(dbName);
        var demandDb = Build.NewDemandDb();
        var whDb     = Build.NewWarehouseDb();
        var ledger   = new SupplierLedgerService(finDb);
        var supplierId = Guid.NewGuid();

        var inv = Build.SeedInvoice(finDb, supplierId, "Supplier A", 300000m, "INV-2026-00001");
        var invRepo = new InvoiceRepository(finDb, demandDb, whDb, ledger);
        await invRepo.ApproveAsync(inv.UUID, null, approvedBy: 1);

        // Pre-payment balance: 300,000 owed.
        var preBalance = await finDb.MasterFinancialLedgers.OrderByDescending(e => e.SequenceNo).FirstAsync();
        preBalance.BalanceAfter.Should().Be(300000m);

        var paymentUuid = await Build.PostedChequePayment(finDb, ledger, supplierId, "Supplier A", 300000m, [(inv, 300000m)]);

        // Post-payment: fully settled.
        var postPayment = await finDb.MasterFinancialLedgers.OrderByDescending(e => e.SequenceNo).FirstAsync();
        postPayment.BalanceAfter.Should().Be(0m);

        var paymentRepo = Build.NewPaymentRepo(finDb, ledger);
        await paymentRepo.BounceAsync(paymentUuid, bouncedBy: 1);

        var afterBounce = await finDb.MasterFinancialLedgers.OrderByDescending(e => e.SequenceNo).FirstAsync();
        afterBounce.TransactionType.Should().Be("PAYMENT_BOUNCED");
        afterBounce.DebitAmount.Should().Be(300000m);
        afterBounce.BalanceAfter.Should().Be(300000m); // restored to pre-payment
    }
}

// ── Negative Master Balance (advance exceeds invoices) ──────────────────────

public class MasterLedger_NegativeBalance_Tests
{
    [Fact]
    public async Task Advance_Payment_Exceeding_Invoices_Produces_A_Correctly_Negative_Master_Balance()
    {
        var db     = Build.NewFinanceDb();
        var ledger = new SupplierLedgerService(db);
        var supplierId = Guid.NewGuid();

        // Invoice for 50,000, advance payment of 80,000 — the org ends up in credit with the supplier.
        await ledger.PostEntryAsync(supplierId, "INVOICE_APPROVED", "Invoice", Guid.NewGuid(), "INV-A",
            debitAmount: 50000m, creditAmount: 0m, narration: null, createdBy: 1, supplierName: "Supplier A");
        await ledger.PostEntryAsync(supplierId, "ADVANCE_PAYMENT", "SupplierPayment", Guid.NewGuid(), "SPAY-A",
            debitAmount: 0m, creditAmount: 80000m, narration: null, createdBy: 1, supplierName: "Supplier A");

        var master = await db.MasterFinancialLedgers.OrderByDescending(e => e.SequenceNo).FirstAsync();
        master.BalanceAfter.Should().Be(-30000m); // 50000 - 80000, genuinely negative, not clamped to 0

        var supplierBalance = await ledger.GetBalanceAsync(supplierId);
        supplierBalance.NetBalance.Should().Be(-30000m);
        supplierBalance.AvailableAdvanceCredit.Should().Be(30000m);
    }
}

// ── Concurrent Multi-Supplier Activity ───────────────────────────────────────

public class MasterLedger_Concurrency_Tests
{
    [Fact]
    public async Task Two_Simultaneous_Approvals_For_Different_Suppliers_Produce_Sequential_Balances_With_No_Gap_Or_Collision()
    {
        var dbName = Guid.NewGuid().ToString();

        var finDb1 = Build.NewFinanceDb(dbName);
        var finDb2 = Build.NewFinanceDb(dbName);

        var ledger1 = new SupplierLedgerService(finDb1);
        var ledger2 = new SupplierLedgerService(finDb2);

        var results = await Task.WhenAll(
            ledger1.PostEntryAsync(Guid.NewGuid(), "INVOICE_APPROVED", "Invoice", Guid.NewGuid(), "INV-A",
                debitAmount: 400000m, creditAmount: 0m, narration: null, createdBy: 1, supplierName: "Supplier 1"),
            ledger2.PostEntryAsync(Guid.NewGuid(), "INVOICE_APPROVED", "Invoice", Guid.NewGuid(), "INV-B",
                debitAmount: 150000m, creditAmount: 0m, narration: null, createdBy: 1, supplierName: "Supplier 2"));

        results.Should().HaveCount(2);

        var verifyDb = Build.NewFinanceDb(dbName);
        var master   = await verifyDb.MasterFinancialLedgers.OrderBy(e => e.SequenceNo).ToListAsync();

        master.Should().HaveCount(2);
        master.Select(m => m.SequenceNo).Should().BeEquivalentTo([1, 2]); // no gap, no collision
        master[1].BalanceAfter.Should().Be(master[0].BalanceAfter + master[1].DebitAmount);
        (master[0].DebitAmount + master[1].DebitAmount).Should().Be(550000m);
    }
}

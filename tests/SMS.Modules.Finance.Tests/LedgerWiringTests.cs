using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Repositories;
using SMS.Modules.Finance.Services;
using SMS.Modules.Warehouse.Data;
using SMS.Modules.Warehouse.Domain;
using SMS.Shared.Common;
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

    internal static Invoice SeedInvoice(FinanceDbContext db, Guid supplierId, decimal totalAmount, string invoiceNumber = "INV-2026-00001")
    {
        var inv = new Invoice
        {
            UUID              = Guid.NewGuid(),
            TraceId           = Guid.NewGuid(),
            InvoiceNumber     = invoiceNumber,
            SupplierId        = supplierId,
            SupplierName      = "Test Supplier",
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

    internal static SupplierReturnOrder SeedSro(WarehouseDbContext db, Guid supplierId, string status, decimal qtyToReturn, decimal unitCost)
    {
        var sro = new SupplierReturnOrder
        {
            UUID         = Guid.NewGuid(),
            ReturnNumber = "SRO-2026-00001",
            SroType      = "POST_RECEIPT_DEFECT",
            SupplierId   = supplierId,
            SupplierName = "Test Supplier",
            ReturnReason = "DAMAGED",
            Status       = status,
            IsActive     = true,
            CreatedBy    = 1,
            CreatedDate  = DateTime.UtcNow,
            Lines =
            [
                new SupplierReturnOrderLine
                {
                    UUID = Guid.NewGuid(), LineNo = 1, ItemDescription = "Returned Item",
                    QtyToReturn = qtyToReturn, UnitCost = unitCost
                }
            ]
        };
        db.SupplierReturnOrders.Add(sro);
        db.SaveChanges();
        return sro;
    }

    internal static Mock<IBackgroundJobClient> LooseJobsMock() => new();
    internal static Mock<INotificationService> LooseNotifMock() => new();
    internal static Mock<IAuditService> LooseAuditMock() => new();
}

// ── Invoice approval → ledger debit ─────────────────────────────────────────

public class InvoiceApproval_LedgerWiring_Tests
{
    [Fact]
    public async Task Approving_Invoice_For_50000_Creates_Ledger_Debit_With_Correct_BalanceAfter()
    {
        var dbName     = Guid.NewGuid().ToString();
        var finDb      = Build.NewFinanceDb(dbName);
        var demandDb   = Build.NewDemandDb();
        var whDb       = Build.NewWarehouseDb();
        var ledger     = new SupplierLedgerService(finDb);
        var supplierId = Guid.NewGuid();

        var inv  = Build.SeedInvoice(finDb, supplierId, 50000m);
        var repo = new InvoiceRepository(finDb, demandDb, whDb, ledger);

        var ok = await repo.ApproveAsync(inv.UUID, null, approvedBy: 1);

        ok.Should().BeTrue();

        var entries = await finDb.SupplierLedgerEntries.Where(e => e.SupplierId == supplierId).ToListAsync();
        entries.Should().ContainSingle();
        entries[0].DebitAmount.Should().Be(50000m);
        entries[0].BalanceAfter.Should().Be(50000m);
        entries[0].TransactionType.Should().Be("INVOICE_APPROVED");
        entries[0].ReferenceId.Should().Be(inv.UUID);
    }

    [Fact]
    public async Task Approval_For_Supplier_With_No_Prior_Entries_Creates_First_Entry_With_BalanceAfter_Equal_To_Amount()
    {
        var finDb      = Build.NewFinanceDb();
        var demandDb   = Build.NewDemandDb();
        var whDb       = Build.NewWarehouseDb();
        var ledger     = new SupplierLedgerService(finDb);
        var supplierId = Guid.NewGuid();

        var inv  = Build.SeedInvoice(finDb, supplierId, 12345.67m);
        var repo = new InvoiceRepository(finDb, demandDb, whDb, ledger);

        await repo.ApproveAsync(inv.UUID, null, approvedBy: 1);

        var entry = await finDb.SupplierLedgerEntries.FirstAsync(e => e.SupplierId == supplierId);
        entry.SequenceNo.Should().Be(1);
        entry.BalanceAfter.Should().Be(12345.67m);
    }

    [Fact]
    public async Task Approved_Invoice_Sets_Approval_Fields_And_Persists_Them()
    {
        var finDb      = Build.NewFinanceDb();
        var demandDb   = Build.NewDemandDb();
        var whDb       = Build.NewWarehouseDb();
        var ledger     = new SupplierLedgerService(finDb);
        var inv        = Build.SeedInvoice(finDb, Guid.NewGuid(), 1000m);
        var repo       = new InvoiceRepository(finDb, demandDb, whDb, ledger);

        await repo.ApproveAsync(inv.UUID, "Looks good", approvedBy: 7);

        var reloaded = await finDb.Invoices.AsNoTracking().FirstAsync(x => x.UUID == inv.UUID);
        reloaded.MatchStatus.Should().Be("Approved");
        reloaded.ApprovedBy.Should().Be(7);
        reloaded.ApprovedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Simulated_Ledger_Write_Failure_Rolls_Back_The_Entire_Approval()
    {
        var dbName   = Guid.NewGuid().ToString();
        var finDb    = Build.NewFinanceDb(dbName);
        var demandDb = Build.NewDemandDb();
        var whDb     = Build.NewWarehouseDb();

        var inv = Build.SeedInvoice(finDb, Guid.NewGuid(), 50000m);

        var failingLedger = new Mock<ISupplierLedgerService>();
        failingLedger
            .Setup(l => l.PostEntryAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<int>()))
            .ThrowsAsync(new InvalidOperationException("Simulated ledger failure"));

        var repo = new InvoiceRepository(finDb, demandDb, whDb, failingLedger.Object);

        var act = async () => await repo.ApproveAsync(inv.UUID, "notes", approvedBy: 1);
        await act.Should().ThrowAsync<InvalidOperationException>();

        // Verify against a FRESH context against the same in-memory database — proves nothing
        // was actually persisted, not just that the in-flight tracked entity looks unchanged.
        var verifyDb = Build.NewFinanceDb(dbName);
        var reloaded = await verifyDb.Invoices.AsNoTracking().FirstAsync(x => x.UUID == inv.UUID);
        reloaded.MatchStatus.Should().NotBe("Approved");
        reloaded.ApprovedBy.Should().BeNull();
        reloaded.ApprovedAt.Should().BeNull();

        (await verifyDb.SupplierLedgerEntries.AnyAsync()).Should().BeFalse();
    }
}

// ── CreditNote / DebitNote (Addendum 8A) → ledger entries ──────────────────

public class CreditDebitNote_LedgerWiring_Tests
{
    [Fact]
    public async Task Creating_A_5000_CreditNote_Creates_A_Credit_Entry_Reducing_Balance()
    {
        var finDb      = Build.NewFinanceDb();
        var whDb       = Build.NewWarehouseDb();
        var ledger     = new SupplierLedgerService(finDb);
        var supplierId = Guid.NewGuid();

        // Baseline: supplier already owes 10,000 from a prior invoice approval.
        await ledger.PostEntryAsync(supplierId, "INVOICE_APPROVED", "Invoice", Guid.NewGuid(), "INV-2026-00001",
            debitAmount: 10000m, creditAmount: 0m, narration: null, createdBy: 1);

        var sro  = Build.SeedSro(whDb, supplierId, "SUPPLIER_RECEIVED", qtyToReturn: 50m, unitCost: 200m);
        var repo = new CreditNoteRepository(finDb, whDb, ledger);

        var cnUuid = await repo.CreateAsync(new CreateCreditNoteRequest
        {
            SroId                = sro.UUID,
            SupplierCreditNoteNo = "SUP-CN-0001",
            CreditDate           = DateTime.UtcNow,
            CreditAmount         = 5000m
        }, createdBy: 1);

        cnUuid.Should().NotBeEmpty();

        var entries = await finDb.SupplierLedgerEntries
            .Where(e => e.SupplierId == supplierId)
            .OrderBy(e => e.SequenceNo)
            .ToListAsync();

        entries.Should().HaveCount(2);
        var creditEntry = entries[1];
        creditEntry.CreditAmount.Should().Be(5000m);
        creditEntry.DebitAmount.Should().Be(0m);
        creditEntry.BalanceAfter.Should().Be(5000m); // 10000 debit - 5000 credit
        creditEntry.TransactionType.Should().Be("CREDIT_NOTE_APPROVED");
        creditEntry.ReferenceId.Should().Be(cnUuid);
    }

    [Fact]
    public async Task Creating_A_DebitNote_Creates_A_Debit_Entry()
    {
        var finDb      = Build.NewFinanceDb();
        var whDb       = Build.NewWarehouseDb();
        var ledger     = new SupplierLedgerService(finDb);
        var supplierId = Guid.NewGuid();

        var sro  = Build.SeedSro(whDb, supplierId, "SUPPLIER_RECEIVED", qtyToReturn: 20m, unitCost: 500m);
        var repo = new DebitNoteRepository(
            finDb, whDb, Build.LooseJobsMock().Object, Build.LooseNotifMock().Object, Build.LooseAuditMock().Object, ledger);

        var dnUuid = await repo.CreateAsync(new CreateDebitNoteRequest
        {
            SroId       = sro.UUID,
            DebitReason = "DAMAGED_GOODS",
            DebitAmount = 3000m
        }, createdBy: 1);

        dnUuid.Should().NotBeEmpty();

        var entry = await finDb.SupplierLedgerEntries.SingleAsync(e => e.SupplierId == supplierId);
        entry.DebitAmount.Should().Be(3000m);
        entry.CreditAmount.Should().Be(0m);
        entry.BalanceAfter.Should().Be(3000m);
        entry.TransactionType.Should().Be("DEBIT_NOTE_APPROVED");
        entry.ReferenceId.Should().Be(dnUuid);
    }

    [Fact]
    public async Task CreditNote_And_DebitNote_Together_Produce_Correct_Net_Balance()
    {
        var finDb      = Build.NewFinanceDb();
        var whDb       = Build.NewWarehouseDb();
        var ledger     = new SupplierLedgerService(finDb);
        var supplierId = Guid.NewGuid();

        var creditSro = Build.SeedSro(whDb, supplierId, "SUPPLIER_RECEIVED", qtyToReturn: 50m, unitCost: 200m);
        var creditRepo = new CreditNoteRepository(finDb, whDb, ledger);
        await creditRepo.CreateAsync(new CreateCreditNoteRequest
        {
            SroId = creditSro.UUID, SupplierCreditNoteNo = "SUP-CN-1", CreditDate = DateTime.UtcNow, CreditAmount = 4000m
        }, createdBy: 1);

        var debitSro = Build.SeedSro(whDb, supplierId, "ESCALATED", qtyToReturn: 20m, unitCost: 500m);
        var debitRepo = new DebitNoteRepository(
            finDb, whDb, Build.LooseJobsMock().Object, Build.LooseNotifMock().Object, Build.LooseAuditMock().Object, ledger);
        await debitRepo.CreateAsync(new CreateDebitNoteRequest
        {
            SroId = debitSro.UUID, DebitReason = "SHORT_SHIPMENT", DebitAmount = 1500m
        }, createdBy: 1);

        var balance = await ledger.GetBalanceAsync(supplierId);
        balance.TotalCredits.Should().Be(4000m);
        balance.TotalDebits.Should().Be(1500m);
        balance.NetBalance.Should().Be(-2500m);
        balance.AvailableAdvanceCredit.Should().Be(2500m);
    }
}

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Repositories;
using SMS.Modules.Finance.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Finance.Tests;

file static class Build
{
    internal static FinanceDbContext NewFinanceDb(string? dbName = null) =>
        new(new DbContextOptionsBuilder<FinanceDbContext>()
            .UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString())
            .Options);

    internal static SupplierPaymentRepository NewRepo(FinanceDbContext db, ISupplierLedgerService? ledger = null) =>
        new(db, ledger ?? new SupplierLedgerService(db), new Mock<INotificationService>().Object);

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
            PaymentStatus     = "UNPAID",
            IsActive          = true,
            CreatedBy         = 1,
            CreatedDate       = DateTime.UtcNow
        };
        db.Invoices.Add(inv);
        db.SaveChanges();
        return inv;
    }

    // Creates + approves a payment allocated against the given invoices, ready to post.
    internal static async Task<Guid> ApprovedPayment(
        FinanceDbContext db, Guid supplierId, decimal totalAmount,
        (Invoice invoice, decimal amount)[] allocations, string paymentType = "STANDARD", Guid? creditNoteUuid = null)
    {
        var repo = NewRepo(db);
        var req = new CreateSupplierPaymentRequest
        {
            SupplierId    = supplierId,
            SupplierName  = "Test Supplier",
            PaymentDate   = DateTime.UtcNow,
            PaymentMethod = "CASH",
            TotalAmount   = totalAmount,
            PaymentType   = paymentType,
            CreditNoteUuid = creditNoteUuid,
            Lines = allocations
                .Select(a => new CreateSupplierPaymentLineRequest { InvoiceUuid = a.invoice.UUID, AllocatedAmount = a.amount })
                .ToList()
        };
        var uuid = await repo.CreateAsync(req, createdBy: 1);
        await repo.ApproveAsync(uuid, approvedBy: 1);
        return uuid;
    }
}

public class PostAsync_Tests
{
    [Fact]
    public async Task Posting_50000_Against_One_50000_Invoice_Fully_Pays_It_And_Reduces_Supplier_Balance()
    {
        var db = Build.NewFinanceDb();
        var supplierId = Guid.NewGuid();
        var inv = Build.SeedInvoice(db, supplierId, 50000m);
        var uuid = await Build.ApprovedPayment(db, supplierId, 50000m, [(inv, 50000m)]);

        var repo = Build.NewRepo(db);
        var ok = await repo.PostAsync(uuid, postedBy: 1);

        ok.Should().BeTrue();

        var reloadedInvoice = await db.Invoices.AsNoTracking().FirstAsync(i => i.UUID == inv.UUID);
        reloadedInvoice.PaidAmount.Should().Be(50000m);
        reloadedInvoice.PaymentStatus.Should().Be("FULLY_PAID");

        var reloadedPayment = await db.SupplierPayments.AsNoTracking().FirstAsync(p => p.UUID == uuid);
        reloadedPayment.Status.Should().Be("POSTED");
        reloadedPayment.PostedAt.Should().NotBeNull();

        var ledgerSvc = new SupplierLedgerService(db);
        var balance = await ledgerSvc.GetBalanceAsync(supplierId);
        balance.TotalCredits.Should().Be(50000m);
        balance.NetBalance.Should().Be(-50000m);
    }

    [Fact]
    public async Task Posting_30000_Against_A_50000_Invoice_Is_Partially_Paid()
    {
        var db = Build.NewFinanceDb();
        var supplierId = Guid.NewGuid();
        var inv = Build.SeedInvoice(db, supplierId, 50000m);
        var uuid = await Build.ApprovedPayment(db, supplierId, 30000m, [(inv, 30000m)]);

        var repo = Build.NewRepo(db);
        await repo.PostAsync(uuid, postedBy: 1);

        var reloaded = await db.Invoices.AsNoTracking().FirstAsync(i => i.UUID == inv.UUID);
        reloaded.PaidAmount.Should().Be(30000m);
        reloaded.PaymentStatus.Should().Be("PARTIALLY_PAID");
    }

    [Fact]
    public async Task Posting_Against_Two_Invoices_Updates_Both_And_Writes_One_Ledger_Entry_For_The_Total()
    {
        var db = Build.NewFinanceDb();
        var supplierId = Guid.NewGuid();
        var inv1 = Build.SeedInvoice(db, supplierId, 20000m, "INV-1");
        var inv2 = Build.SeedInvoice(db, supplierId, 30000m, "INV-2");
        var uuid = await Build.ApprovedPayment(db, supplierId, 50000m, [(inv1, 20000m), (inv2, 30000m)]);

        var repo = Build.NewRepo(db);
        await repo.PostAsync(uuid, postedBy: 1);

        var r1 = await db.Invoices.AsNoTracking().FirstAsync(i => i.UUID == inv1.UUID);
        var r2 = await db.Invoices.AsNoTracking().FirstAsync(i => i.UUID == inv2.UUID);
        r1.PaidAmount.Should().Be(20000m);
        r1.PaymentStatus.Should().Be("FULLY_PAID");
        r2.PaidAmount.Should().Be(30000m);
        r2.PaymentStatus.Should().Be("FULLY_PAID");

        var ledgerEntries = await db.SupplierLedgerEntries.Where(e => e.SupplierId == supplierId).ToListAsync();
        ledgerEntries.Should().ContainSingle();
        ledgerEntries[0].CreditAmount.Should().Be(50000m);
    }

    [Fact]
    public async Task Posting_Only_APPROVED_Payments_Rejects_DRAFT()
    {
        var db = Build.NewFinanceDb();
        var supplierId = Guid.NewGuid();
        var inv = Build.SeedInvoice(db, supplierId, 1000m);

        var repo = Build.NewRepo(db);
        var uuid = await repo.CreateAsync(new CreateSupplierPaymentRequest
        {
            SupplierId = supplierId, SupplierName = "Test Supplier", PaymentDate = DateTime.UtcNow,
            PaymentMethod = "CASH", TotalAmount = 1000m,
            Lines = [new CreateSupplierPaymentLineRequest { InvoiceUuid = inv.UUID, AllocatedAmount = 1000m }]
        }, createdBy: 1);
        // Not approved — still DRAFT.

        var act = async () => await repo.PostAsync(uuid, postedBy: 1);
        await act.Should().ThrowAsync<UnprocessableEntityException>();
    }

    [Fact]
    public async Task Overpaying_An_Invoice_Sets_OVERPAID_Status()
    {
        var db = Build.NewFinanceDb();
        var supplierId = Guid.NewGuid();
        var inv = Build.SeedInvoice(db, supplierId, 1000m);

        // CreateAsync's outstanding-amount guard prevents over-allocating a fresh payment, so to
        // exercise PostAsync's OVERPAID derivation branch directly we construct an
        // already-APPROVED over-allocated payment, simulating e.g. a correction that reduced the
        // invoice total after this payment was approved against the original higher amount.
        var overPayment = new SupplierPayment
        {
            UUID = Guid.NewGuid(), PaymentNumber = "SPAY-TEST-OVER", SupplierId = supplierId,
            SupplierName = "Test Supplier", PaymentDate = DateTime.UtcNow, PaymentMethod = "CASH",
            TotalAmount = 1500m, Status = "APPROVED", PaymentType = "STANDARD",
            CreatedBy = 1, CreatedDate = DateTime.UtcNow,
            Lines = [ new SupplierPaymentLine { UUID = Guid.NewGuid(), InvoiceUuid = inv.UUID, InvoiceNumber = inv.InvoiceNumber, AllocatedAmount = 1500m, OutstandingBeforeAllocation = 1000m } ]
        };
        db.SupplierPayments.Add(overPayment);
        await db.SaveChangesAsync();

        var repo = Build.NewRepo(db);
        await repo.PostAsync(overPayment.UUID, postedBy: 1);

        var reloaded = await db.Invoices.AsNoTracking().FirstAsync(i => i.UUID == inv.UUID);
        reloaded.PaidAmount.Should().Be(1500m);
        reloaded.PaymentStatus.Should().Be("OVERPAID");
    }

    [Fact]
    public async Task Overpaid_Posting_Sends_A_System_Alert_Notification()
    {
        var db = Build.NewFinanceDb();
        var supplierId = Guid.NewGuid();
        var inv = Build.SeedInvoice(db, supplierId, 1000m);

        var overPayment = new SupplierPayment
        {
            UUID = Guid.NewGuid(), PaymentNumber = "SPAY-TEST-OVER2", SupplierId = supplierId,
            SupplierName = "Test Supplier", PaymentDate = DateTime.UtcNow, PaymentMethod = "CASH",
            TotalAmount = 1500m, Status = "APPROVED", PaymentType = "STANDARD",
            CreatedBy = 1, CreatedDate = DateTime.UtcNow,
            Lines = [ new SupplierPaymentLine { UUID = Guid.NewGuid(), InvoiceUuid = inv.UUID, InvoiceNumber = inv.InvoiceNumber, AllocatedAmount = 1500m, OutstandingBeforeAllocation = 1000m } ]
        };
        db.SupplierPayments.Add(overPayment);
        await db.SaveChangesAsync();

        var notif = new Mock<INotificationService>();
        var repo = new SupplierPaymentRepository(db, new SupplierLedgerService(db), notif.Object);

        await repo.PostAsync(overPayment.UUID, postedBy: 1);

        notif.Verify(n => n.TryCreateAsync(It.Is<NotificationRequest>(r => r.Type == "INVOICE_OVERPAID")), Times.Once);
    }

    [Fact]
    public async Task ADVANCE_PAYMENT_Creates_SupplierAdvancePayment_With_Correct_Available_Balance()
    {
        var db = Build.NewFinanceDb();
        var supplierId = Guid.NewGuid();

        var repo = Build.NewRepo(db);
        var req = new CreateSupplierPaymentRequest
        {
            SupplierId = supplierId, SupplierName = "Test Supplier", PaymentDate = DateTime.UtcNow,
            PaymentMethod = "CASH", TotalAmount = 25000m, PaymentType = "ADVANCE_PAYMENT"
        };
        var uuid = await repo.CreateAsync(req, createdBy: 1);
        await repo.ApproveAsync(uuid, approvedBy: 1);

        await repo.PostAsync(uuid, postedBy: 1);

        var advance = await db.SupplierAdvancePayments.SingleAsync(a => a.SupplierPaymentUuid == uuid);
        advance.SupplierId.Should().Be(supplierId);
        advance.OriginalAmount.Should().Be(25000m);
        advance.AvailableBalance.Should().Be(25000m);
    }

    [Fact]
    public async Task Simulated_Failure_During_Post_Rolls_Back_Everything()
    {
        var dbName = Guid.NewGuid().ToString();
        var db = Build.NewFinanceDb(dbName);
        var supplierId = Guid.NewGuid();
        var inv = Build.SeedInvoice(db, supplierId, 50000m);
        var uuid = await Build.ApprovedPayment(db, supplierId, 50000m, [(inv, 50000m)]);

        var failingLedger = new Mock<ISupplierLedgerService>();
        failingLedger
            .Setup(l => l.PostEntryAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<int>()))
            .ThrowsAsync(new InvalidOperationException("Simulated failure after invoice updates were tracked"));

        var repo = new SupplierPaymentRepository(db, failingLedger.Object, new Mock<INotificationService>().Object);

        var act = async () => await repo.PostAsync(uuid, postedBy: 1);
        await act.Should().ThrowAsync<InvalidOperationException>();

        // Fresh context against the same in-memory database — proves nothing was persisted.
        var verifyDb = Build.NewFinanceDb(dbName);
        var reloadedInvoice = await verifyDb.Invoices.AsNoTracking().FirstAsync(i => i.UUID == inv.UUID);
        reloadedInvoice.PaidAmount.Should().Be(0m);
        reloadedInvoice.PaymentStatus.Should().Be("UNPAID");

        var reloadedPayment = await verifyDb.SupplierPayments.AsNoTracking().FirstAsync(p => p.UUID == uuid);
        reloadedPayment.Status.Should().Be("APPROVED");
        reloadedPayment.PostedAt.Should().BeNull();

        (await verifyDb.SupplierLedgerEntries.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task PURCHASE_RETURN_SETTLEMENT_Reduces_The_Source_CreditNotes_Remaining_Credit()
    {
        var db = Build.NewFinanceDb();
        var supplierId = Guid.NewGuid();

        var creditNote = new CreditNote
        {
            UUID = Guid.NewGuid(), CreditNoteNumber = "CN-2026-00001", SupplierCreditNoteNo = "SUP-CN-1",
            SroUuid = Guid.NewGuid(), SroNumber = "SRO-2026-00001", SupplierId = supplierId, SupplierName = "Test Supplier",
            CreditDate = DateTime.UtcNow, CreditAmount = 10000m, ApplicationStatus = "CARRIED_FORWARD",
            CarriedForwardAmount = 10000m, IsActive = true, CreatedBy = 1, CreatedDate = DateTime.UtcNow
        };
        db.CreditNotes.Add(creditNote);
        await db.SaveChangesAsync();

        var repo = Build.NewRepo(db);
        var req = new CreateSupplierPaymentRequest
        {
            SupplierId = supplierId, SupplierName = "Test Supplier", PaymentDate = DateTime.UtcNow,
            PaymentMethod = "CASH", TotalAmount = 4000m,
            PaymentType = "PURCHASE_RETURN_SETTLEMENT", CreditNoteUuid = creditNote.UUID
        };
        var uuid = await repo.CreateAsync(req, createdBy: 1);
        await repo.ApproveAsync(uuid, approvedBy: 1);

        await repo.PostAsync(uuid, postedBy: 1);

        var reloadedCn = await db.CreditNotes.AsNoTracking().FirstAsync(c => c.UUID == creditNote.UUID);
        reloadedCn.CarriedForwardAmount.Should().Be(6000m);
    }

    [Fact]
    public async Task PURCHASE_RETURN_SETTLEMENT_Without_CreditNoteUuid_Is_Rejected_At_Creation()
    {
        var db = Build.NewFinanceDb();
        var repo = Build.NewRepo(db);

        var req = new CreateSupplierPaymentRequest
        {
            SupplierId = Guid.NewGuid(), SupplierName = "Test Supplier", PaymentDate = DateTime.UtcNow,
            PaymentMethod = "CASH", TotalAmount = 1000m, PaymentType = "PURCHASE_RETURN_SETTLEMENT"
        };

        var act = async () => await repo.CreateAsync(req, createdBy: 1);
        await act.Should().ThrowAsync<BadRequestException>();
    }
}

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

    // Creates, approves, and posts a CHEQUE payment allocated against the given invoices.
    internal static async Task<Guid> PostedChequePayment(
        FinanceDbContext db, Guid supplierId, decimal totalAmount, (Invoice invoice, decimal amount)[] allocations)
    {
        var repo = NewRepo(db);
        var req = new CreateSupplierPaymentRequest
        {
            SupplierId    = supplierId,
            SupplierName  = "Test Supplier",
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

    // Creates, approves, and posts a non-CHEQUE payment (for the "only CHEQUE" guard tests).
    internal static async Task<Guid> PostedCashPayment(
        FinanceDbContext db, Guid supplierId, decimal totalAmount, (Invoice invoice, decimal amount)[] allocations)
    {
        var repo = NewRepo(db);
        var req = new CreateSupplierPaymentRequest
        {
            SupplierId = supplierId, SupplierName = "Test Supplier", PaymentDate = DateTime.UtcNow,
            PaymentMethod = "CASH", TotalAmount = totalAmount,
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

public class BounceAsync_Tests
{
    [Fact]
    public async Task Bouncing_A_50000_Cheque_Creates_Debit_Ledger_Entry_Restoring_Supplier_Balance()
    {
        var db = Build.NewFinanceDb();
        var supplierId = Guid.NewGuid();
        var inv = Build.SeedInvoice(db, supplierId, 50000m);
        var uuid = await Build.PostedChequePayment(db, supplierId, 50000m, [(inv, 50000m)]);

        var repo = Build.NewRepo(db);
        var ok = await repo.BounceAsync(uuid, bouncedBy: 1);

        ok.Should().BeTrue();

        var ledger = new SupplierLedgerService(db);
        var entries = await ledger.GetLedgerAsync(supplierId, new SupplierLedgerFilter { PageSize = 10 });
        entries.Data.Should().HaveCount(2); // original credit (post) + reversing debit (bounce)

        var bounceEntry = entries.Data.OrderBy(e => e.SequenceNo).Last();
        bounceEntry.DebitAmount.Should().Be(50000m);
        bounceEntry.CreditAmount.Should().Be(0m);
        bounceEntry.Narration.Should().Be("Cheque bounced");

        var balance = await ledger.GetBalanceAsync(supplierId);
        balance.NetBalance.Should().Be(0m); // fully restored: credit 50000 then debit 50000
    }

    [Fact]
    public async Task Fully_Paid_Invoice_Reverts_To_Unpaid_After_Bounce()
    {
        var db = Build.NewFinanceDb();
        var supplierId = Guid.NewGuid();
        var inv = Build.SeedInvoice(db, supplierId, 50000m);
        var uuid = await Build.PostedChequePayment(db, supplierId, 50000m, [(inv, 50000m)]);

        var reloadedBefore = await db.Invoices.AsNoTracking().FirstAsync(i => i.UUID == inv.UUID);
        reloadedBefore.PaymentStatus.Should().Be("FULLY_PAID");

        await Build.NewRepo(db).BounceAsync(uuid, bouncedBy: 1);

        var reloadedAfter = await db.Invoices.AsNoTracking().FirstAsync(i => i.UUID == inv.UUID);
        reloadedAfter.PaidAmount.Should().Be(0m);
        reloadedAfter.PaymentStatus.Should().Be("UNPAID");
    }

    [Fact]
    public async Task Partially_Paid_Invoice_By_Cheque_And_Prior_Payment_Reverts_To_Pre_Cheque_Amount()
    {
        var db = Build.NewFinanceDb();
        var supplierId = Guid.NewGuid();
        var inv = Build.SeedInvoice(db, supplierId, 10000m);

        // Prior CASH payment of 3000, then a CHEQUE payment of 4000 on top of it.
        await Build.PostedCashPayment(db, supplierId, 3000m, [(inv, 3000m)]);
        var afterCash = await db.Invoices.AsNoTracking().FirstAsync(i => i.UUID == inv.UUID);
        afterCash.PaidAmount.Should().Be(3000m);
        afterCash.PaymentStatus.Should().Be("PARTIALLY_PAID");

        var chequeUuid = await Build.PostedChequePayment(db, supplierId, 4000m, [(inv, 4000m)]);
        var afterCheque = await db.Invoices.AsNoTracking().FirstAsync(i => i.UUID == inv.UUID);
        afterCheque.PaidAmount.Should().Be(7000m);
        afterCheque.PaymentStatus.Should().Be("PARTIALLY_PAID");

        await Build.NewRepo(db).BounceAsync(chequeUuid, bouncedBy: 1);

        var afterBounce = await db.Invoices.AsNoTracking().FirstAsync(i => i.UUID == inv.UUID);
        afterBounce.PaidAmount.Should().Be(3000m); // back to the pre-cheque (cash-only) amount
        afterBounce.PaymentStatus.Should().Be("PARTIALLY_PAID");
    }

    [Fact]
    public async Task Bouncing_A_NonCheque_Payment_Returns_422()
    {
        var db = Build.NewFinanceDb();
        var supplierId = Guid.NewGuid();
        var inv = Build.SeedInvoice(db, supplierId, 1000m);
        var uuid = await Build.PostedCashPayment(db, supplierId, 1000m, [(inv, 1000m)]);

        var repo = Build.NewRepo(db);
        var act = async () => await repo.BounceAsync(uuid, bouncedBy: 1);

        await act.Should().ThrowAsync<UnprocessableEntityException>();
    }

    [Fact]
    public async Task Bouncing_A_NonPosted_Cheque_Payment_Returns_422()
    {
        var db = Build.NewFinanceDb();
        var supplierId = Guid.NewGuid();
        var inv = Build.SeedInvoice(db, supplierId, 1000m);

        var repo = Build.NewRepo(db);
        var uuid = await repo.CreateAsync(new CreateSupplierPaymentRequest
        {
            SupplierId = supplierId, SupplierName = "Test Supplier", PaymentDate = DateTime.UtcNow,
            PaymentMethod = "CHEQUE", ChequeNo = "CHQ-0002", ChequeDate = DateTime.UtcNow, TotalAmount = 1000m,
            Lines = [new CreateSupplierPaymentLineRequest { InvoiceUuid = inv.UUID, AllocatedAmount = 1000m }]
        }, createdBy: 1);
        await repo.ApproveAsync(uuid, approvedBy: 1);
        // Approved but not yet posted.

        var act = async () => await repo.BounceAsync(uuid, bouncedBy: 1);
        await act.Should().ThrowAsync<UnprocessableEntityException>();
    }

    [Fact]
    public async Task Bounced_Payment_Status_Is_Set_And_BouncedAt_Recorded()
    {
        var db = Build.NewFinanceDb();
        var supplierId = Guid.NewGuid();
        var inv = Build.SeedInvoice(db, supplierId, 1000m);
        var uuid = await Build.PostedChequePayment(db, supplierId, 1000m, [(inv, 1000m)]);

        var repo = Build.NewRepo(db);
        await repo.BounceAsync(uuid, bouncedBy: 9);

        var detail = await repo.GetByUuidAsync(uuid);
        detail!.Status.Should().Be("BOUNCED");
        detail.BouncedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Simulated_Failure_During_Bounce_Rolls_Back_Everything()
    {
        var dbName = Guid.NewGuid().ToString();
        var db = Build.NewFinanceDb(dbName);
        var supplierId = Guid.NewGuid();
        var inv = Build.SeedInvoice(db, supplierId, 50000m);
        var uuid = await Build.PostedChequePayment(db, supplierId, 50000m, [(inv, 50000m)]);

        var failingLedger = new Mock<ISupplierLedgerService>();
        failingLedger
            .Setup(l => l.PostEntryAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string?>(), It.IsAny<int>()))
            .ThrowsAsync(new InvalidOperationException("Simulated bounce failure"));

        var repo = new SupplierPaymentRepository(db, failingLedger.Object, new Mock<INotificationService>().Object);

        var act = async () => await repo.BounceAsync(uuid, bouncedBy: 1);
        await act.Should().ThrowAsync<InvalidOperationException>();

        var verifyDb = Build.NewFinanceDb(dbName);
        var reloadedInvoice = await verifyDb.Invoices.AsNoTracking().FirstAsync(i => i.UUID == inv.UUID);
        reloadedInvoice.PaidAmount.Should().Be(50000m);
        reloadedInvoice.PaymentStatus.Should().Be("FULLY_PAID");

        var reloadedPayment = await verifyDb.SupplierPayments.AsNoTracking().FirstAsync(p => p.UUID == uuid);
        reloadedPayment.Status.Should().Be("POSTED");
        reloadedPayment.BouncedAt.Should().BeNull();

        var ledgerEntries = await verifyDb.SupplierLedgerEntries.Where(e => e.SupplierId == supplierId).ToListAsync();
        ledgerEntries.Should().ContainSingle(); // only the original PAYMENT_POSTED credit — no reversal
    }
}

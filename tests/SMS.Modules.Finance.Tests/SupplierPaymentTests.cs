using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Finance.Controllers;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Repositories;
using SMS.Modules.Finance.Services;
using SMS.Shared.Authorization;
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

    internal static Invoice SeedInvoice(
        FinanceDbContext db, Guid supplierId, decimal totalAmount,
        string invoiceNumber = "INV-2026-00001", string paymentStatus = "Unpaid")
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
            PaymentStatus     = paymentStatus,
            IsActive          = true,
            CreatedBy         = 1,
            CreatedDate       = DateTime.UtcNow
        };
        db.Invoices.Add(inv);
        db.SaveChanges();
        return inv;
    }

    internal static CreateSupplierPaymentRequest BaseRequest(Guid supplierId, decimal totalAmount) => new()
    {
        SupplierId    = supplierId,
        SupplierName  = "Test Supplier",
        PaymentDate   = DateTime.UtcNow,
        PaymentMethod = "CASH",
        TotalAmount   = totalAmount,
        Lines         = []
    };
}

// ── Create — multi-invoice allocation ───────────────────────────────────────

public class SupplierPayment_CreateAsync_Tests
{
    [Fact]
    public async Task Payment_With_3_Invoice_Lines_Summing_To_TotalAmount_Succeeds()
    {
        var db = Build.NewFinanceDb();
        var repo = Build.NewRepo(db);
        var supplierId = Guid.NewGuid();

        var inv1 = Build.SeedInvoice(db, supplierId, 1000m, "INV-1");
        var inv2 = Build.SeedInvoice(db, supplierId, 2000m, "INV-2");
        var inv3 = Build.SeedInvoice(db, supplierId, 3000m, "INV-3");

        var req = Build.BaseRequest(supplierId, 6000m);
        req.Lines =
        [
            new CreateSupplierPaymentLineRequest { InvoiceUuid = inv1.UUID, AllocatedAmount = 1000m },
            new CreateSupplierPaymentLineRequest { InvoiceUuid = inv2.UUID, AllocatedAmount = 2000m },
            new CreateSupplierPaymentLineRequest { InvoiceUuid = inv3.UUID, AllocatedAmount = 3000m }
        ];

        var uuid = await repo.CreateAsync(req, createdBy: 1);

        uuid.Should().NotBeEmpty();
        var detail = await repo.GetByUuidAsync(uuid);
        detail!.Lines.Should().HaveCount(3);
        detail.Status.Should().Be("DRAFT");
        detail.Lines.Select(l => l.AllocatedAmount).Sum().Should().Be(6000m);
    }

    [Fact]
    public async Task Lines_Summing_To_Less_Than_TotalAmount_Returns_BadRequest()
    {
        var db = Build.NewFinanceDb();
        var repo = Build.NewRepo(db);
        var supplierId = Guid.NewGuid();
        var inv1 = Build.SeedInvoice(db, supplierId, 5000m);

        var req = Build.BaseRequest(supplierId, 5000m);
        req.Lines = [new CreateSupplierPaymentLineRequest { InvoiceUuid = inv1.UUID, AllocatedAmount = 3000m }];

        var act = async () => await repo.CreateAsync(req, createdBy: 1);
        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Method_CHEQUE_Missing_ChequeNo_Returns_BadRequest()
    {
        var db = Build.NewFinanceDb();
        var repo = Build.NewRepo(db);

        var req = Build.BaseRequest(Guid.NewGuid(), 1000m);
        req.PaymentMethod = "CHEQUE";
        req.ChequeDate = DateTime.UtcNow;
        // ChequeNo intentionally omitted

        var act = async () => await repo.CreateAsync(req, createdBy: 1);
        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Method_BANK_TRANSFER_Missing_BankAccount_Returns_BadRequest()
    {
        var db = Build.NewFinanceDb();
        var repo = Build.NewRepo(db);

        var req = Build.BaseRequest(Guid.NewGuid(), 1000m);
        req.PaymentMethod = "BANK_TRANSFER";
        // BankAccount intentionally omitted

        var act = async () => await repo.CreateAsync(req, createdBy: 1);
        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Method_ONLINE_WIRE_Missing_BankAccount_Returns_BadRequest()
    {
        var db = Build.NewFinanceDb();
        var repo = Build.NewRepo(db);

        var req = Build.BaseRequest(Guid.NewGuid(), 1000m);
        req.PaymentMethod = "ONLINE_WIRE";

        var act = async () => await repo.CreateAsync(req, createdBy: 1);
        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Method_CHEQUE_With_ChequeNo_And_Date_Succeeds()
    {
        var db = Build.NewFinanceDb();
        var repo = Build.NewRepo(db);

        var req = Build.BaseRequest(Guid.NewGuid(), 1000m);
        req.PaymentMethod = "CHEQUE";
        req.ChequeNo = "CHQ-001";
        req.ChequeDate = DateTime.UtcNow;

        var uuid = await repo.CreateAsync(req, createdBy: 1);
        uuid.Should().NotBeEmpty();
    }

    [Fact]
    public async Task AllocatedAmount_Exceeding_Invoice_Outstanding_Returns_BadRequest()
    {
        var db = Build.NewFinanceDb();
        var repo = Build.NewRepo(db);
        var supplierId = Guid.NewGuid();
        var inv = Build.SeedInvoice(db, supplierId, 1000m);

        var req = Build.BaseRequest(supplierId, 1500m);
        req.Lines = [new CreateSupplierPaymentLineRequest { InvoiceUuid = inv.UUID, AllocatedAmount = 1500m }];

        var act = async () => await repo.CreateAsync(req, createdBy: 1);
        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Payment_With_No_Lines_Is_Allowed_As_An_Unallocated_Advance()
    {
        var db = Build.NewFinanceDb();
        var repo = Build.NewRepo(db);

        var req = Build.BaseRequest(Guid.NewGuid(), 500m);

        var uuid = await repo.CreateAsync(req, createdBy: 1);

        var detail = await repo.GetByUuidAsync(uuid);
        detail!.Lines.Should().BeEmpty();
        detail.TotalAmount.Should().Be(500m);
    }
}

// ── Approve / Cancel lifecycle ──────────────────────────────────────────────

public class SupplierPayment_Lifecycle_Tests
{
    [Fact]
    public async Task Approve_Transitions_Draft_To_Approved()
    {
        var db = Build.NewFinanceDb();
        var repo = Build.NewRepo(db);
        var uuid = await repo.CreateAsync(Build.BaseRequest(Guid.NewGuid(), 100m), createdBy: 1);

        var ok = await repo.ApproveAsync(uuid, approvedBy: 5);

        ok.Should().BeTrue();
        var detail = await repo.GetByUuidAsync(uuid);
        detail!.Status.Should().Be("APPROVED");
        detail.ApprovedBy.Should().Be(5);
        detail.ApprovedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Cancel_A_POSTED_Payment_Returns_422()
    {
        var db = Build.NewFinanceDb();
        var repo = Build.NewRepo(db);
        var uuid = await repo.CreateAsync(Build.BaseRequest(Guid.NewGuid(), 100m), createdBy: 1);

        // No "post" action exists yet — simulate a posted payment directly for this guard test.
        var payment = await db.SupplierPayments.FirstAsync(x => x.UUID == uuid);
        payment.Status = "POSTED";
        await db.SaveChangesAsync();

        var act = async () => await repo.CancelAsync(uuid, cancelledBy: 1);
        await act.Should().ThrowAsync<UnprocessableEntityException>();
    }

    [Fact]
    public async Task Cancel_A_Draft_Payment_Succeeds()
    {
        var db = Build.NewFinanceDb();
        var repo = Build.NewRepo(db);
        var uuid = await repo.CreateAsync(Build.BaseRequest(Guid.NewGuid(), 100m), createdBy: 1);

        var ok = await repo.CancelAsync(uuid, cancelledBy: 1);

        ok.Should().BeTrue();
        var detail = await repo.GetByUuidAsync(uuid);
        detail!.Status.Should().Be("CANCELLED");
    }

    [Fact]
    public async Task Cancel_An_Approved_Payment_Succeeds()
    {
        var db = Build.NewFinanceDb();
        var repo = Build.NewRepo(db);
        var uuid = await repo.CreateAsync(Build.BaseRequest(Guid.NewGuid(), 100m), createdBy: 1);
        await repo.ApproveAsync(uuid, approvedBy: 1);

        var ok = await repo.CancelAsync(uuid, cancelledBy: 1);

        ok.Should().BeTrue();
    }

    [Fact]
    public async Task Cancelled_Allocations_No_Longer_Count_Against_Invoice_Outstanding()
    {
        var db = Build.NewFinanceDb();
        var repo = Build.NewRepo(db);
        var supplierId = Guid.NewGuid();
        var inv = Build.SeedInvoice(db, supplierId, 1000m);

        var req = Build.BaseRequest(supplierId, 1000m);
        req.Lines = [new CreateSupplierPaymentLineRequest { InvoiceUuid = inv.UUID, AllocatedAmount = 1000m }];
        var uuid = await repo.CreateAsync(req, createdBy: 1);

        await repo.CancelAsync(uuid, cancelledBy: 1);

        // Outstanding should be fully available again since the allocating payment was cancelled.
        var outstanding = await repo.GetOutstandingInvoicesAsync(supplierId);
        outstanding.Should().ContainSingle(o => o.InvoiceUuid == inv.UUID && o.OutstandingAmount == 1000m);
    }
}

// ── GET list / detail ────────────────────────────────────────────────────────

public class SupplierPayment_Query_Tests
{
    [Fact]
    public async Task GetByUuid_Returns_Lines_With_Linked_Invoice_Numbers()
    {
        var db = Build.NewFinanceDb();
        var repo = Build.NewRepo(db);
        var supplierId = Guid.NewGuid();
        var inv = Build.SeedInvoice(db, supplierId, 500m, "INV-2026-00099");

        var req = Build.BaseRequest(supplierId, 500m);
        req.Lines = [new CreateSupplierPaymentLineRequest { InvoiceUuid = inv.UUID, AllocatedAmount = 500m }];
        var uuid = await repo.CreateAsync(req, createdBy: 1);

        var detail = await repo.GetByUuidAsync(uuid);

        detail!.Lines.Should().ContainSingle();
        detail.Lines[0].InvoiceNumber.Should().Be("INV-2026-00099");
        detail.Lines[0].InvoiceUuid.Should().Be(inv.UUID);
    }

    [Fact]
    public async Task GetList_Filters_By_Supplier_And_Status()
    {
        var db = Build.NewFinanceDb();
        var repo = Build.NewRepo(db);
        var supplierA = Guid.NewGuid();
        var supplierB = Guid.NewGuid();

        var uuidA = await repo.CreateAsync(Build.BaseRequest(supplierA, 100m), createdBy: 1);
        await repo.CreateAsync(Build.BaseRequest(supplierB, 200m), createdBy: 1);
        await repo.ApproveAsync(uuidA, approvedBy: 1);

        var resultA = await repo.GetListAsync(new SupplierPaymentFilter { SupplierId = supplierA });
        resultA.Data.Should().ContainSingle();
        resultA.Data[0].SupplierId.Should().Be(supplierA);

        var resultDraft = await repo.GetListAsync(new SupplierPaymentFilter { Status = "DRAFT" });
        resultDraft.Data.Should().ContainSingle(p => p.SupplierId == supplierB);
    }

    [Fact]
    public async Task GetByUuid_Returns_Null_For_Unknown_Payment()
    {
        var db = Build.NewFinanceDb();
        var repo = Build.NewRepo(db);

        var detail = await repo.GetByUuidAsync(Guid.NewGuid());

        detail.Should().BeNull();
    }
}

// ── Outstanding invoices ─────────────────────────────────────────────────────

public class GetOutstandingInvoicesAsync_Tests
{
    [Fact]
    public async Task Returns_Only_Invoices_Not_Fully_Paid_For_The_Given_Supplier()
    {
        var db = Build.NewFinanceDb();
        var repo = Build.NewRepo(db);
        var supplierId = Guid.NewGuid();
        var otherSupplier = Guid.NewGuid();

        var unpaid  = Build.SeedInvoice(db, supplierId, 1000m, "INV-UNPAID", paymentStatus: "Unpaid");
        var partial = Build.SeedInvoice(db, supplierId, 2000m, "INV-PARTIAL", paymentStatus: "Partial");
        Build.SeedInvoice(db, supplierId, 3000m, "INV-PAID", paymentStatus: "Paid");
        Build.SeedInvoice(db, otherSupplier, 4000m, "INV-OTHER-SUPPLIER", paymentStatus: "Unpaid");

        var result = await repo.GetOutstandingInvoicesAsync(supplierId);

        result.Select(r => r.InvoiceNumber).Should().BeEquivalentTo(["INV-UNPAID", "INV-PARTIAL"]);
        result.Should().NotContain(r => r.InvoiceNumber == "INV-PAID");
        result.Should().NotContain(r => r.InvoiceNumber == "INV-OTHER-SUPPLIER");
    }

    [Fact]
    public async Task OutstandingAmount_Reflects_Existing_Allocations()
    {
        var db = Build.NewFinanceDb();
        var repo = Build.NewRepo(db);
        var supplierId = Guid.NewGuid();
        var inv = Build.SeedInvoice(db, supplierId, 1000m);

        var req = Build.BaseRequest(supplierId, 400m);
        req.Lines = [new CreateSupplierPaymentLineRequest { InvoiceUuid = inv.UUID, AllocatedAmount = 400m }];
        await repo.CreateAsync(req, createdBy: 1);

        var result = await repo.GetOutstandingInvoicesAsync(supplierId);

        result.Should().ContainSingle();
        result[0].OutstandingAmount.Should().Be(600m);
    }

    [Fact]
    public async Task Returns_Empty_For_Supplier_With_No_Invoices()
    {
        var db = Build.NewFinanceDb();
        var repo = Build.NewRepo(db);

        var result = await repo.GetOutstandingInvoicesAsync(Guid.NewGuid());

        result.Should().BeEmpty();
    }
}

// ── Authorization ─────────────────────────────────────────────────────────────

public class SupplierPaymentsController_Authorization_Tests
{
    [Fact]
    public void Approve_Requires_PAYMENT_APPROVE_Permission()
    {
        var method = typeof(SupplierPaymentsController).GetMethod(nameof(SupplierPaymentsController.Approve));

        method.Should().NotBeNull();

        var attr = method!.GetCustomAttributes(typeof(RequirePermissionAttribute), inherit: false)
            .Cast<RequirePermissionAttribute>()
            .FirstOrDefault();

        attr.Should().NotBeNull("payment approval must be guarded by RequirePermissionAttribute");
        attr!.Policy.Should().Be($"Permission:{PermissionCodes.PAYMENT_APPROVE}",
            "only users with PAYMENT_APPROVE (Finance Manager) may approve — Finance Officer gets 403");
    }

    [Fact]
    public void Cancel_Has_No_Special_Permission_Gate_Beyond_Authentication()
    {
        var method = typeof(SupplierPaymentsController).GetMethod(nameof(SupplierPaymentsController.Cancel));

        method.Should().NotBeNull();
        var attr = method!.GetCustomAttributes(typeof(RequirePermissionAttribute), inherit: false);
        attr.Should().BeEmpty();
    }
}

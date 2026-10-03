using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Repositories;
using SMS.Modules.Finance.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Finance.Tests.SupplierInvoices;

/// <summary>
/// The legacy single-invoice payment flow (api/finance/payments): item E — a supplier invoice is a payable only
/// once it is Approved — and what the security review found in it: another organization's invoice and payment were
/// reachable by a super admin, and a payment's status could be patched to anything, including back from Reversed.
/// </summary>
public class LegacyPaymentTests
{
    private const int User = PurchaseRig.User;

    private static CreatePaymentRequest Paying(Guid invoice, decimal amount = 100m) => new()
    {
        InvoiceUuid = invoice, PaymentDate = new DateTime(2026, 9, 20), AmountPaid = amount, PaymentMethod = "Cash"
    };

    private static async Task<Guid> InvoiceInAsync(PurchaseRig rig, string status)
    {
        var uuid = await rig.CreateAsync(rig.Request(subtotal: 500m));
        if (status != InvoiceMatchStatus.Pending) await rig.TamperAsync(uuid, i => i.MatchStatus = status);
        return uuid;
    }

    [Theory]
    [InlineData(InvoiceMatchStatus.Pending)]
    [InlineData(InvoiceMatchStatus.Matched)]
    [InlineData(InvoiceMatchStatus.Variance)]
    public async Task An_invoice_not_yet_approved_cannot_be_paid(string status)
    {
        var rig = PurchaseRig.New();
        var uuid = await InvoiceInAsync(rig, status);

        await new PaymentRepository(rig.Finance).Invoking(p => p.CreateAsync(Paying(uuid), User))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*is not approved yet; approve it before paying*");
        (await rig.Finance.Payments.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_approved_invoice_is_paid_and_its_payment_status_follows()
    {
        var rig = PurchaseRig.New();
        var uuid = await InvoiceInAsync(rig, InvoiceMatchStatus.Approved);

        await new PaymentRepository(rig.Finance).CreateAsync(Paying(uuid, 200m), User);

        rig.Forget();
        (await rig.LoadAsync(uuid)).PaymentStatus.Should().Be("Partial");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Another_organizations_invoice_and_payment_are_not_found_even_for_a_super_admin(bool superAdmin)
    {
        var rig = PurchaseRig.New();
        var uuid = await InvoiceInAsync(rig, InvoiceMatchStatus.Approved);
        var payment = await new PaymentRepository(rig.Finance).CreateAsync(Paying(uuid), User);
        rig.Forget();

        var stranger = new PaymentRepository(new FinanceDbContext(
            new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(rig.DbName).Options,
            new StaticTenantContext { OrganizationId = Guid.NewGuid(), IsSuperAdmin = superAdmin }));

        await stranger.Invoking(p => p.CreateAsync(Paying(uuid), User)).Should().ThrowAsync<NotFoundException>();
        (await stranger.PatchAsync(payment, new PatchPaymentRequest { Status = "Reversed" }, User)).Should().BeFalse();

        (await rig.Finance.Payments.AsNoTracking().SingleAsync()).Status.Should().Be("Pending");
        (await rig.LoadAsync(uuid)).PaymentStatus.Should().Be("Partial");
    }

    [Theory]
    [InlineData("Approved")]
    [InlineData("Paid")]
    [InlineData("whatever")]
    public async Task A_payment_status_that_is_not_one_of_the_flows_is_refused(string target)
    {
        var rig = PurchaseRig.New();
        var uuid = await InvoiceInAsync(rig, InvoiceMatchStatus.Approved);
        var payment = await new PaymentRepository(rig.Finance).CreateAsync(Paying(uuid), User);

        await new PaymentRepository(rig.Finance).Invoking(p => p.PatchAsync(payment, new PatchPaymentRequest { Status = target }, User))
            .Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task A_reversed_payment_cannot_be_brought_back_and_reversing_one_updates_the_invoice()
    {
        var rig = PurchaseRig.New();
        var uuid = await InvoiceInAsync(rig, InvoiceMatchStatus.Approved);
        var repo = new PaymentRepository(rig.Finance);
        var payment = await repo.CreateAsync(Paying(uuid), User);

        (await repo.PatchAsync(payment, new PatchPaymentRequest { Status = "Reversed" }, User)).Should().BeTrue();
        rig.Forget();
        (await rig.LoadAsync(uuid)).PaymentStatus.Should().Be("Unpaid");

        await repo.Invoking(p => p.PatchAsync(payment, new PatchPaymentRequest { Status = "Pending" }, User))
            .Should().ThrowAsync<ConflictException>().WithMessage("*reversed*");
    }

    [Fact]
    public async Task A_payment_already_made_against_an_invoice_that_is_not_approved_can_still_be_reversed()
    {
        var rig = PurchaseRig.New();
        var uuid = await InvoiceInAsync(rig, InvoiceMatchStatus.Pending);
        var old = await rig.LegacyPaymentAsync(uuid, "Pending");      // made before item E

        (await new PaymentRepository(rig.Finance).PatchAsync(old.UUID, new PatchPaymentRequest { Status = "Reversed" }, User)).Should().BeTrue();

        (await rig.Finance.Payments.AsNoTracking().SingleAsync()).Status.Should().Be("Reversed");
    }

    // ── Never more than is owed (found by the note E2E tester) ───────────────

    [Fact]
    public async Task A_payment_of_more_than_is_still_owed_is_refused()
    {
        var rig = PurchaseRig.New();
        var uuid = await InvoiceInAsync(rig, InvoiceMatchStatus.Approved);      // 500
        var repo = new PaymentRepository(rig.Finance);
        await repo.CreateAsync(Paying(uuid, 300m), User);

        await repo.Invoking(p => p.CreateAsync(Paying(uuid, 200.01m), User))
            .Should().ThrowAsync<UnprocessableEntityException>().WithMessage("*more than is still owed*");
        (await repo.CreateAsync(Paying(uuid, 200m), User)).Should().NotBeEmpty();
        rig.Forget();
        (await rig.LoadAsync(uuid)).PaymentStatus.Should().Be("Paid");
    }

    [Fact]
    public async Task An_invoice_settled_by_a_supplier_payment_cannot_be_paid_again_through_the_legacy_flow()
    {
        var rig = PurchaseRig.New();
        var uuid = await InvoiceInAsync(rig, InvoiceMatchStatus.Approved);      // 500
        await rig.TamperAsync(uuid, i => { i.PaidAmount = 500m; i.PaymentStatus = InvoicePaymentStatus.FullyPaid; });

        await new PaymentRepository(rig.Finance).Invoking(p => p.CreateAsync(Paying(uuid, 500m), User))
            .Should().ThrowAsync<UnprocessableEntityException>();
        (await rig.Finance.Payments.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_note_that_settles_an_invoice_paid_through_the_legacy_flow_marks_it_paid()
    {
        var rig = PurchaseRig.New();
        var uuid = await InvoiceInAsync(rig, InvoiceMatchStatus.Approved);      // 500
        await new PaymentRepository(rig.Finance).CreateAsync(Paying(uuid, 350m), User);
        var note = new SMS.Modules.Finance.Domain.CreditNote
        {
            UUID = Guid.NewGuid(), CreditNoteNumber = "CN-2026-00001", SupplierCreditNoteNo = "S-CN-1", SroUuid = Guid.NewGuid(),
            SroNumber = "SRO-1", SupplierId = rig.Supplier, SupplierName = "Karachi Steel", CreditDate = new DateTime(2026, 9, 18),
            CreditAmount = 150m, CarriedForwardAmount = 150m, ApplicationStatus = "CARRIED_FORWARD", IsActive = true,
            CreatedBy = 1, CreatedDate = DateTime.UtcNow
        };
        rig.Finance.CreditNotes.Add(note);
        await rig.Finance.SaveChangesAsync();
        rig.Forget();

        await new CreditNoteRepository(rig.Finance, rig.Warehouse, new SupplierLedgerService(rig.Finance))
            .ApplyCarriedForwardAsync(note.UUID, new ApplyCreditNoteRequest { InvoiceUuid = uuid }, User);

        rig.Forget();
        var invoice = await rig.LoadAsync(uuid);
        (invoice.TotalAmount, invoice.PaymentStatus).Should().Be((350m, "Paid"), "350 paid against what is now 350 owed");
    }

    [Fact]
    public async Task The_ordinary_flow_still_moves_forward_and_resending_the_same_status_is_not_a_change()
    {
        var rig = PurchaseRig.New();
        var uuid = await InvoiceInAsync(rig, InvoiceMatchStatus.Approved);
        var repo = new PaymentRepository(rig.Finance);
        var payment = await repo.CreateAsync(Paying(uuid), User);

        (await repo.PatchAsync(payment, new PatchPaymentRequest { Status = "Pending", Notes = "Called the bank" }, User)).Should().BeTrue();
        (await repo.PatchAsync(payment, new PatchPaymentRequest { Status = "Processed" }, User)).Should().BeTrue();
        (await repo.PatchAsync(payment, new PatchPaymentRequest { Status = "Cleared" }, User)).Should().BeTrue();

        (await rig.Finance.Payments.AsNoTracking().SingleAsync()).Status.Should().Be("Cleared");
    }
}

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Repositories;
using SMS.Modules.Finance.Services;
using SMS.Modules.Warehouse.Data;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Services;
using Xunit;

namespace SMS.Modules.Finance.Tests.SupplierInvoices;

/// <summary>
/// What the independent review of work package C found beyond the builder's own tests — each was red before its
/// fix. The concurrency of the transitions, which needs a real SQL Server, is in InvoiceTransitionConcurrencyTests.
/// </summary>
public class PurchaseReviewFindingsTests
{
    private const int User = PurchaseRig.User;

    // ── Rejection ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_rejected_invoice_cannot_be_rejected_again_and_keeps_its_first_reason()
    {
        var rig = PurchaseRig.New();
        var uuid = await rig.CreateAsync(rig.Request(subtotal: 500m));
        (await rig.Service.RejectAsync(uuid, "Wrong prices", User)).Should().BeTrue();
        rig.Forget();

        await rig.Invoking(r => r.Service.RejectAsync(uuid, "Something else", User))
            .Should().ThrowAsync<ConflictException>().WithMessage("*already been rejected*");
        (await rig.LoadAsync(uuid)).Notes.Should().Be("Wrong prices");
    }

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("APPROVED")]
    [InlineData("POSTED")]
    public async Task An_invoice_on_a_live_supplier_payment_cannot_be_rejected(string status)
    {
        var rig = PurchaseRig.New();
        var uuid = await rig.CreateAsync(rig.Request(subtotal: 500m));
        var payment = await rig.SupplierPaymentAsync(uuid, status);

        await rig.Invoking(r => r.Service.RejectAsync(uuid, "Wrong prices", User))
            .Should().ThrowAsync<ConflictException>().WithMessage($"*{payment.PaymentNumber}*");
        (await rig.LoadAsync(uuid)).MatchStatus.Should().Be(InvoiceMatchStatus.Pending);
    }

    [Fact]
    public async Task A_legacy_payment_or_a_paid_amount_also_stops_a_rejection_but_a_cancelled_payment_does_not()
    {
        var rig = PurchaseRig.New();
        var legacy = await rig.CreateAsync(rig.Request(subtotal: 500m));
        await rig.LegacyPaymentAsync(legacy, "Pending");
        await rig.Invoking(r => r.Service.RejectAsync(legacy, "No", User)).Should().ThrowAsync<ConflictException>();

        var paid = await rig.CreateAsync(rig.Request(subtotal: 500m));
        await rig.TamperAsync(paid, i => i.PaidAmount = 100m);
        await rig.Invoking(r => r.Service.RejectAsync(paid, "No", User)).Should().ThrowAsync<ConflictException>();

        var cancelled = await rig.CreateAsync(rig.Request(subtotal: 500m));
        await rig.SupplierPaymentAsync(cancelled, "CANCELLED");
        (await rig.Service.RejectAsync(cancelled, "No", User)).Should().BeTrue();
    }

    // ── Lengths: a 400, not the database's 500 ────────────────────────────────

    [Fact]
    public async Task Approval_notes_longer_than_the_column_are_a_bad_request_and_nothing_is_booked()
    {
        var rig = PurchaseRig.New();
        var (po, lines) = await rig.PoAsync(true, (10m, 100m));
        var uuid = await rig.CreateAsync(rig.Request(po, lines));

        await rig.Invoking(r => r.Service.ApproveAsync(uuid, new string('x', 301), User))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*300*");
        (await rig.LedgerAsync(uuid)).Should().BeEmpty();
        (await rig.Service.ApproveAsync(uuid, new string('x', 300), User)).Should().BeTrue();
    }

    [Fact]
    public async Task Blank_approval_notes_leave_the_invoices_notes_as_they_were()
    {
        var rig = PurchaseRig.New();
        var request = rig.Request(subtotal: 500m);
        request.Notes = "Checked against the delivery note";
        var uuid = await rig.CreateAsync(request);

        (await rig.Service.ApproveAsync(uuid, "   ", User)).Should().BeTrue();

        (await rig.LoadAsync(uuid)).Notes.Should().Be("Checked against the delivery note");
    }

    [Fact]
    public async Task Too_long_notes_supplier_number_or_payment_method_are_refused_on_create_and_edit()
    {
        var rig = PurchaseRig.New();

        var create = rig.Request(subtotal: 500m);
        create.Notes = new string('n', 301);
        await rig.Invoking(r => r.Service.CreateAsync(create, User)).Should().ThrowAsync<BadRequestException>();
        create.Notes = null;
        create.SupplierInvoiceNo = new string('s', 51);
        await rig.Invoking(r => r.Service.CreateAsync(create, User)).Should().ThrowAsync<BadRequestException>();
        (await rig.Finance.Invoices.CountAsync()).Should().Be(0);

        var uuid = await rig.CreateAsync(rig.Request(subtotal: 500m));
        await rig.Invoking(r => r.Service.PatchAsync(uuid, new PatchInvoiceRequest { Notes = new string('n', 301) }, User))
            .Should().ThrowAsync<BadRequestException>();
        await rig.Invoking(r => r.Service.PatchAsync(uuid, new PatchInvoiceRequest { PaymentMethod = new string('m', 51) }, User))
            .Should().ThrowAsync<BadRequestException>();
        (await rig.Service.PatchAsync(uuid, new PatchInvoiceRequest { Notes = new string('n', 300) }, User)).Should().BeTrue();
    }

    // ── The supplier billed is the purchase order's ───────────────────────────

    [Fact]
    public async Task An_invoice_against_a_PO_cannot_name_another_supplier()
    {
        var rig = PurchaseRig.New();
        var (po, lines) = await rig.PoAsync(true, (10m, 100m));
        var request = rig.Request(po, lines);
        request.SupplierId = Guid.NewGuid();

        await rig.Invoking(r => r.Service.CreateAsync(request, User))
            .Should().ThrowAsync<BadRequestException>().WithMessage($"*{po.PoNumber}*");
        (await rig.Finance.Invoices.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_invoice_against_a_PO_that_names_no_supplier_takes_the_POs()
    {
        var rig = PurchaseRig.New();
        var (po, lines) = await rig.PoAsync(true, (10m, 100m));
        var request = rig.Request(po, lines);
        request.SupplierId = Guid.Empty;

        var invoice = await rig.LoadAsync(await rig.CreateAsync(request));

        invoice.SupplierId.Should().Be(po.SupplierId);
        invoice.SupplierName.Should().Be(po.SupplierName);
    }

    // ── Amounts are stored to the cent, and the tax is worked out from what is stored ──

    [Fact]
    public async Task Line_totals_and_the_subtotal_are_rounded_to_the_cent_before_the_tax_is_worked_out_from_them()
    {
        var rig = PurchaseRig.New();
        var half = rig.TaxCodes.Add("HALF", 50m);
        var (po, lines) = await rig.PoAsync(true, (1.0005m, 10m));

        var invoice = await rig.LoadAsync(await rig.CreateAsync(rig.Request(po, lines, taxCode: half.Uuid)));

        invoice.Lines.Single().LineTotal.Should().Be(10.01m, "1.0005 × 10.00 = 10.005, half away from zero");
        invoice.Subtotal.Should().Be(10.01m, "the sum of the stored line totals");
        invoice.TaxAmount.Should().Be(5.01m, "50% of the stored 10.01, not of 10.005");
        invoice.TotalAmount.Should().Be(15.02m);
        invoice.TaxAmount.Should().Be(InvoiceRepository.TaxFor(invoice.Subtotal, 50m), "re-picking the code later gives the same tax");
    }

    [Fact]
    public async Task A_hand_entered_tax_or_subtotal_is_kept_to_the_cent()
    {
        var rig = PurchaseRig.New();

        var invoice = await rig.LoadAsync(await rig.CreateAsync(rig.Request(subtotal: 100.005m, tax: 12.345m)));

        invoice.Subtotal.Should().Be(100.01m);
        invoice.TaxAmount.Should().Be(12.35m);
        invoice.TotalAmount.Should().Be(112.36m);

        (await rig.Service.PatchAsync(invoice.UUID, new PatchInvoiceRequest { TaxAmount = 12.354m }, User)).Should().BeTrue();
        rig.Forget();
        (await rig.LoadAsync(invoice.UUID)).TaxAmount.Should().Be(12.35m, "12.354 is 12.35 — not a change at all");
    }

    // ── Another organization's invoice ────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Another_organizations_invoice_is_not_found_by_any_change_even_for_a_super_admin(bool superAdmin)
    {
        var rig = PurchaseRig.New();
        var (po, lines) = await rig.PoAsync(true, (10m, 100m));
        var pending  = await rig.CreateAsync(rig.Request(po, lines));
        var (approved, _, _) = await rig.ApprovedAsync();

        var stranger = Stranger(rig, superAdmin);

        (await stranger.ApproveAsync(pending, null, User)).Should().BeFalse();
        (await stranger.RejectAsync(pending, "No", User)).Should().BeFalse();
        (await stranger.PatchAsync(pending, new PatchInvoiceRequest { Notes = "x" }, User)).Should().BeFalse();
        (await stranger.UploadAttachmentAsync(pending, "/uploads/x.pdf", User)).Should().BeFalse();
        (await stranger.ReverseAsync(approved, "No", User)).Should().BeFalse();

        (await rig.LoadAsync(pending)).MatchStatus.Should().Be(InvoiceMatchStatus.Matched);
        (await rig.LoadAsync(approved)).MatchStatus.Should().Be(InvoiceMatchStatus.Approved);
        (await rig.Finance.SupplierLedgerEntries.AsNoTracking().IgnoreQueryFilters().CountAsync()).Should().Be(1, "only the one approval");
    }

    private static InvoiceRepository Stranger(PurchaseRig rig, bool superAdmin)
    {
        var tenant    = new StaticTenantContext { OrganizationId = Guid.NewGuid(), IsSuperAdmin = superAdmin };
        var finance   = new FinanceDbContext(new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(rig.DbName).Options, tenant);
        var demand    = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(rig.DbName).Options, tenant);
        var warehouse = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseInMemoryDatabase(rig.DbName).Options, tenant);
        return new InvoiceRepository(finance, demand, warehouse, new SupplierLedgerService(finance), new FakeSupplierNameLookup());
    }

    // ── Payments against an invoice that owes nothing ─────────────────────────

    private static SupplierPaymentRepository Payments(PurchaseRig rig) =>
        new(rig.Finance, new SupplierLedgerService(rig.Finance), Mock.Of<INotificationService>());

    [Fact]
    public async Task A_rejected_invoice_cannot_be_paid_by_either_payment_flow()
    {
        var rig = PurchaseRig.New();
        var uuid = await rig.CreateAsync(rig.Request(subtotal: 500m));
        await rig.Service.RejectAsync(uuid, "Not ours", User);
        rig.Forget();

        await Payments(rig).Invoking(p => p.CreateAsync(new CreateSupplierPaymentRequest
        {
            SupplierId = rig.Supplier, SupplierName = "Karachi Steel", PaymentDate = new DateTime(2026, 9, 20), PaymentMethod = "CASH",
            TotalAmount = 100m, Lines = [new CreateSupplierPaymentLineRequest { InvoiceUuid = uuid, AllocatedAmount = 100m }]
        }, User)).Should().ThrowAsync<BadRequestException>().WithMessage("*rejected*");

        await new PaymentRepository(rig.Finance).Invoking(p => p.CreateAsync(new CreatePaymentRequest
        {
            InvoiceUuid = uuid, PaymentDate = new DateTime(2026, 9, 20), AmountPaid = 100m, PaymentMethod = "Cash"
        }, User)).Should().ThrowAsync<UnprocessableEntityException>().WithMessage("*rejected*");
    }

    [Theory]
    [InlineData(InvoiceMatchStatus.Reversed)]
    [InlineData(InvoiceMatchStatus.Rejected)]
    public async Task Posting_a_payment_drafted_before_its_invoice_was_reversed_or_rejected_moves_no_money(string status)
    {
        var rig = PurchaseRig.New();
        var (uuid, _, _) = await rig.ApprovedAsync();
        var payment = await rig.SupplierPaymentAsync(uuid, "APPROVED", amount: 100m);
        // What a reversal racing the payment's creation leaves behind (the draft passed its check first).
        await rig.TamperAsync(uuid, i => i.MatchStatus = status);
        var ledgerBefore = await rig.Finance.SupplierLedgerEntries.CountAsync();

        // 409 since item E: posting refuses any invoice that is not Approved.
        await Payments(rig).Invoking(p => p.PostAsync(payment.UUID, User))
            .Should().ThrowAsync<ConflictException>().WithMessage($"*{status.ToLowerInvariant()}*");

        rig.Forget();
        (await rig.LoadAsync(uuid)).PaidAmount.Should().Be(0m);
        (await rig.Finance.SupplierLedgerEntries.CountAsync()).Should().Be(ledgerBefore);
        (await rig.Finance.SupplierPayments.AsNoTracking().SingleAsync(p => p.UUID == payment.UUID)).Status.Should().Be("APPROVED");
    }

    [Fact]
    public async Task Only_an_approved_invoice_is_outstanding_aged_or_payable_not_a_rejected_or_pending_one()
    {
        var rig = PurchaseRig.New();
        var rejected = await rig.CreateAsync(rig.Request(subtotal: 500m));
        await rig.Service.RejectAsync(rejected, "Not ours", User);
        await rig.CreateAsync(rig.Request(subtotal: 300m));                    // Pending: not yet a payable (item E)
        var approved = await rig.CreateAsync(rig.Request(subtotal: 200m));
        await rig.Service.ApproveAsync(approved, null, User);
        rig.Forget();
        var payments = Payments(rig);

        (await payments.GetOutstandingInvoicesAsync(rig.Supplier)).Select(i => i.InvoiceUuid).Should().Equal(approved);
        (await payments.GetSupplierAgingAsync(rig.Supplier)).GrandTotal.Should().Be(200m);
        (await payments.GetCrossSupplierAgingAsync()).GrandTotalRow!.GrandTotal.Should().Be(200m);
        (await payments.GetOutstandingPayablesAsync(new OutstandingPayablesFilter())).Data.SelectMany(g => g.Invoices)
            .Select(i => i.InvoiceUuid).Should().Equal(approved);
    }

    // ── The PDF of a reversed invoice ─────────────────────────────────────────

    [Theory]
    [InlineData("Approved", true)]
    [InlineData("Reversed", false)]
    public async Task A_reversed_invoices_PDF_never_says_its_total_is_due(string status, bool due)
    {
        var uuid = Guid.NewGuid();
        var detail = new InvoiceDetailModel
        {
            UUID = uuid, InvoiceNumber = "INV-2026-00042", SupplierName = "Karachi Steel", SupplierId = Guid.NewGuid(),
            InvoiceDate = new DateTime(2026, 9, 15), DueDate = new DateTime(2026, 10, 15), ReceivedDate = new DateTime(2026, 9, 16),
            Currency = "PKR", Subtotal = 1000m, TotalAmount = 1000m, MatchStatus = status, PaymentStatus = "Unpaid",
            ReversedAt = status == "Reversed" ? new DateTime(2026, 9, 20) : null, ReversalReason = status == "Reversed" ? "Entered twice" : null
        };
        var invoices  = new Mock<IInvoiceService>();
        invoices.Setup(i => i.GetByUuidAsync(uuid)).ReturnsAsync(detail);
        var templates = new Mock<SMS.Modules.Lookups.Services.IPoDocumentTemplateService>();
        var contacts  = new Mock<ISupplierContactLookupService>();

        var bytes = await new InvoiceDocumentService(invoices.Object, templates.Object, contacts.Object,
            Mock.Of<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>()).GeneratePdfAsync(uuid);

        using var pdf = UglyToad.PdfPig.PdfDocument.Open(bytes);
        var text = string.Join(" ", pdf.GetPages().SelectMany(p => p.GetWords()).Select(w => w.Text));
        text.Contains("Amount Due").Should().Be(due);
        if (!due) text.Should().Contain("REVERSED").And.Contain("nothing is due");
    }

    // ── The list's payment-status filter speaks both vocabularies ─────────────

    [Fact]
    public async Task Filtering_the_list_by_Paid_or_Partial_finds_invoices_settled_by_supplier_payments_too()
    {
        var rig = PurchaseRig.New();
        var legacyPaid = await rig.CreateAsync(rig.Request(subtotal: 100m));
        var fullyPaid  = await rig.CreateAsync(rig.Request(subtotal: 200m));
        var overpaid   = await rig.CreateAsync(rig.Request(subtotal: 300m));
        var partly     = await rig.CreateAsync(rig.Request(subtotal: 400m));
        await rig.TamperAsync(legacyPaid, i => i.PaymentStatus = "Paid");
        await rig.TamperAsync(fullyPaid,  i => i.PaymentStatus = InvoicePaymentStatus.FullyPaid);
        await rig.TamperAsync(overpaid,   i => i.PaymentStatus = InvoicePaymentStatus.Overpaid);
        await rig.TamperAsync(partly,     i => i.PaymentStatus = InvoicePaymentStatus.PartiallyPaid);

        (await rig.Service.GetListAsync(new InvoiceFilter { PaymentStatus = "Paid" })).Data.Select(i => i.UUID)
            .Should().BeEquivalentTo([legacyPaid, fullyPaid, overpaid]);
        (await rig.Service.GetListAsync(new InvoiceFilter { PaymentStatus = "Partial" })).Data.Select(i => i.UUID)
            .Should().BeEquivalentTo([partly]);
        (await rig.Service.GetListAsync(new InvoiceFilter { PaymentStatus = InvoicePaymentStatus.FullyPaid })).Data
            .Should().HaveCount(3);
    }
}

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Models;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Finance.Tests.SupplierInvoices;

/// <summary>S-8 — the three-way match on the net Subtotal.</summary>
public class ThreeWayMatchOnSubtotalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tax_no_longer_makes_a_matching_invoice_a_variance(bool byCode)
    {
        var rig = PurchaseRig.New();
        var gst = rig.TaxCodes.Add("GST17", 17m);
        var (po, lines) = await rig.PoAsync(true, (10m, 100m));
        var grn = await rig.GrnAsync(po, lines, 10m);

        var uuid = await rig.CreateAsync(rig.Request(po, lines, tax: byCode ? 0m : 170m, taxCode: byCode ? gst.Uuid : null, grn: grn.UUID));

        var invoice = await rig.LoadAsync(uuid);
        invoice.TotalAmount.Should().Be(1170m);
        invoice.MatchStatus.Should().Be("Matched", "1000 net against a 1000 PO and a 1000 GRN — the 17% tax used to make this a Variance");
        (invoice.MatchedPoValue, invoice.MatchedGrnValue, invoice.VarianceAmount).Should().Be((1000m, 1000m, 0m));
    }

    [Theory]
    [InlineData("1050.00", "Matched")]
    [InlineData("1050.01", "Variance")]
    [InlineData("950.00",  "Matched")]
    [InlineData("949.99",  "Variance")]
    public async Task The_match_is_within_five_percent_of_the_net_subtotal_whatever_the_tax(string subtotal, string expected)
    {
        var rig = PurchaseRig.New();
        var (po, _) = await rig.PoAsync(true, (10m, 100m));
        var net = decimal.Parse(subtotal, System.Globalization.CultureInfo.InvariantCulture);

        var uuid = await rig.CreateAsync(rig.Request(po, subtotal: net, tax: 500m));

        var invoice = await rig.LoadAsync(uuid);
        invoice.MatchStatus.Should().Be(expected);
        invoice.VarianceAmount.Should().Be(net - 1000m, "the variance is net against net too");
    }

    [Fact]
    public async Task With_a_GRN_the_net_subtotal_must_be_within_five_percent_of_the_GRN_value_as_well()
    {
        var rig = PurchaseRig.New();
        var (po, lines) = await rig.PoAsync(true, (10m, 100m));
        var grn = await rig.GrnAsync(po, lines, 8m);

        var uuid = await rig.CreateAsync(rig.Request(po, lines, grn: grn.UUID));

        var invoice = await rig.LoadAsync(uuid);
        (invoice.MatchStatus, invoice.MatchedGrnValue, invoice.VarianceAmount).Should().Be(("Variance", 800m, 0m));
    }

    [Fact]
    public async Task An_invoice_with_no_purchase_order_stays_Pending_on_create_and_on_a_tax_edit()
    {
        var rig = PurchaseRig.New();
        var gst = rig.TaxCodes.Add("GST17", 17m);
        var uuid = await rig.CreateAsync(rig.Request(subtotal: 12500m, tax: 100m));
        (await rig.LoadAsync(uuid)).MatchStatus.Should().Be("Pending");

        await rig.Service.PatchAsync(uuid, new PatchInvoiceRequest { TaxAmount = 150m }, PurchaseRig.User);
        (await rig.LoadAsync(uuid)).MatchStatus.Should().Be("Pending", "a tax edit used to turn a PO-less invoice into a Variance");

        await rig.Service.PatchAsync(uuid, new PatchInvoiceRequest { TaxCodeUuid = gst.Uuid }, PurchaseRig.User);
        var invoice = await rig.LoadAsync(uuid);
        (invoice.MatchStatus, invoice.VarianceAmount, invoice.TotalAmount).Should().Be(("Pending", 0m, 14625m));
    }
}

/// <summary>S-7 — what an edit may no longer do, and approval and rejection only from the right states.</summary>
public class SupplierInvoiceGuardTests
{
    private static async Task<Guid> InvoiceInAsync(PurchaseRig rig, string status)
    {
        if (status == "Pending")
            return await rig.CreateAsync(rig.Request(subtotal: 1000m));

        var (po, lines) = await rig.PoAsync(true, (10m, 100m));
        var uuid = await rig.CreateAsync(rig.Request(po, lines));
        switch (status)
        {
            case "Variance": await rig.Service.PatchAsync(uuid, new PatchInvoiceRequest { MatchStatus = "Variance" }, PurchaseRig.User); break;
            case "Approved": await rig.Service.ApproveAsync(uuid, null, PurchaseRig.User); break;
            case "Rejected": await rig.Service.RejectAsync(uuid, "Wrong prices", PurchaseRig.User); break;
            case "Reversed":
                await rig.Service.ApproveAsync(uuid, null, PurchaseRig.User);
                await rig.Service.ReverseAsync(uuid, "Entered twice", PurchaseRig.User);
                break;
        }
        rig.Forget();
        (await rig.LoadAsync(uuid)).MatchStatus.Should().Be(status);
        return uuid;
    }

    // ── Match status ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Pending")]
    [InlineData("Matched")]
    [InlineData("Variance")]
    [InlineData("Approved")]
    [InlineData("Rejected")]
    [InlineData("Reversed")]
    public async Task Patching_the_match_status_to_Approved_is_refused_whatever_the_invoice_is(string status)
    {
        var rig = PurchaseRig.New();
        var uuid = await InvoiceInAsync(rig, status);
        var entries = (await rig.LedgerAsync(uuid)).Count;

        await rig.Service.Invoking(s => s.PatchAsync(uuid, new PatchInvoiceRequest { MatchStatus = "approved" }, PurchaseRig.User))
            .Should().ThrowAsync<ConflictException>().WithMessage("*Approve action*");

        (await rig.LoadAsync(uuid)).MatchStatus.Should().Be(status);
        (await rig.LedgerAsync(uuid)).Should().HaveCount(entries, "an edit never books anything to the ledger");
    }

    [Theory]
    [InlineData("Rejected", "*Reject action*")]
    [InlineData("REVERSED", "*Reverse action*")]
    public async Task Rejecting_and_reversing_by_editing_the_match_status_are_refused(string target, string message)
    {
        var rig = PurchaseRig.New();
        var uuid = await InvoiceInAsync(rig, "Matched");

        await rig.Service.Invoking(s => s.PatchAsync(uuid, new PatchInvoiceRequest { MatchStatus = target }, PurchaseRig.User))
            .Should().ThrowAsync<ConflictException>().WithMessage(message);

        (await rig.LoadAsync(uuid)).MatchStatus.Should().Be("Matched");
    }

    [Fact]
    public async Task An_unknown_match_status_is_a_bad_request()
    {
        var rig = PurchaseRig.New();
        var uuid = await InvoiceInAsync(rig, "Matched");

        await rig.Service.Invoking(s => s.PatchAsync(uuid, new PatchInvoiceRequest { MatchStatus = "Closed" }, PurchaseRig.User))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*'Closed' is not a match status*");
    }

    [Fact]
    public async Task Before_approval_the_match_can_still_be_set_by_hand_to_Pending_Matched_or_Variance()
    {
        var rig = PurchaseRig.New();
        var uuid = await InvoiceInAsync(rig, "Matched");

        await rig.Service.PatchAsync(uuid, new PatchInvoiceRequest { MatchStatus = "variance" }, PurchaseRig.User);
        (await rig.LoadAsync(uuid)).MatchStatus.Should().Be("Variance", "stored in its canonical spelling");

        await rig.Service.PatchAsync(uuid, new PatchInvoiceRequest { MatchStatus = "Pending" }, PurchaseRig.User);
        (await rig.LoadAsync(uuid)).MatchStatus.Should().Be("Pending");
    }

    [Fact]
    public async Task A_rejected_invoices_match_status_can_no_longer_change()
    {
        var rig = PurchaseRig.New();
        var uuid = await InvoiceInAsync(rig, "Rejected");

        await rig.Service.Invoking(s => s.PatchAsync(uuid, new PatchInvoiceRequest { MatchStatus = "Matched" }, PurchaseRig.User))
            .Should().ThrowAsync<ConflictException>().WithMessage("*has been rejected*");
    }

    // ── An approved invoice ──────────────────────────────────────────────────

    [Theory]
    [InlineData("tax amount")]
    [InlineData("tax code")]
    [InlineData("removing the code")]
    [InlineData("match status")]
    public async Task An_approved_invoice_refuses_every_edit_to_its_tax_and_match_and_stays_as_booked(string what)
    {
        var rig = PurchaseRig.New();
        var gst = rig.TaxCodes.Add("GST17", 17m);
        var other = rig.TaxCodes.Add("GST18", 18m);
        var (uuid, _, _) = await rig.ApprovedAsync(gst.Uuid);
        var edit = what switch
        {
            "tax amount"        => new PatchInvoiceRequest { TaxAmount = 171m },
            "tax code"          => new PatchInvoiceRequest { TaxCodeUuid = other.Uuid },
            "removing the code" => new PatchInvoiceRequest { TaxCodeUuid = Guid.Empty, TaxAmount = 1m },
            _                   => new PatchInvoiceRequest { MatchStatus = "Matched" }
        };

        await rig.Service.Invoking(s => s.PatchAsync(uuid, edit, PurchaseRig.User))
            .Should().ThrowAsync<ConflictException>().WithMessage("*Reverse the invoice instead*");

        var invoice = await rig.LoadAsync(uuid);
        (invoice.MatchStatus, invoice.TaxCode, invoice.TaxAmount, invoice.TotalAmount).Should().Be(("Approved", "GST17", 170m, 1170m));
        (await rig.LedgerAsync(uuid)).Should().ContainSingle().Which.DebitAmount.Should().Be(1170m);
    }

    [Fact]
    public async Task An_approved_invoice_still_takes_its_due_date_payment_method_notes_attachment_and_supplier_number()
    {
        var rig = PurchaseRig.New();
        var (uuid, _, _) = await rig.ApprovedAsync();

        await rig.Service.PatchAsync(uuid, new PatchInvoiceRequest
        {
            SupplierInvoiceNo = "KSW/881-R", DueDate = new DateTime(2026, 11, 1), PaymentMethod = "Cheque",
            Notes = "Supplier re-issued the paper", AttachmentUrl = "/uploads/invoices/x.pdf",
            TaxAmount = 0m, MatchStatus = null    // the same tax again is not a change, so not refused
        }, PurchaseRig.User);

        var invoice = await rig.LoadAsync(uuid);
        (invoice.SupplierInvoiceNo, invoice.DueDate, invoice.PaymentMethod, invoice.Notes, invoice.AttachmentUrl)
            .Should().Be(("KSW/881-R", new DateTime(2026, 11, 1), "Cheque", "Supplier re-issued the paper", "/uploads/invoices/x.pdf"));
        (invoice.MatchStatus, invoice.TotalAmount).Should().Be(("Approved", 1000m));
    }

    // ── A reversed invoice ───────────────────────────────────────────────────

    public static IEnumerable<object[]> NonNoteEdits() =>
    [
        [new PatchInvoiceRequest { SupplierInvoiceNo = "NEW" }],
        [new PatchInvoiceRequest { DueDate = new DateTime(2027, 1, 1) }],
        [new PatchInvoiceRequest { PaymentMethod = "Cash" }],
        [new PatchInvoiceRequest { TaxAmount = 5m }],
        [new PatchInvoiceRequest { PaymentStatus = "Scheduled" }],
        [new PatchInvoiceRequest { MatchStatus = "Matched" }],
    ];

    [Theory]
    [MemberData(nameof(NonNoteEdits))]
    public async Task A_reversed_invoice_refuses_everything_but_its_notes_and_attachment(PatchInvoiceRequest edit)
    {
        var rig = PurchaseRig.New();
        var uuid = await InvoiceInAsync(rig, "Reversed");

        await rig.Service.Invoking(s => s.PatchAsync(uuid, edit, PurchaseRig.User))
            .Should().ThrowAsync<ConflictException>().WithMessage("*has been reversed*");
    }

    [Fact]
    public async Task A_reversed_invoice_still_takes_notes_and_an_attachment()
    {
        var rig = PurchaseRig.New();
        var uuid = await InvoiceInAsync(rig, "Reversed");

        await rig.Service.PatchAsync(uuid, new PatchInvoiceRequest { Notes = "See INV-2026-00099", AttachmentUrl = "/y.pdf", SupplierInvoiceNo = "KSW/881" }, PurchaseRig.User);

        var invoice = await rig.LoadAsync(uuid);
        (invoice.Notes, invoice.AttachmentUrl, invoice.MatchStatus).Should().Be(("See INV-2026-00099", "/y.pdf", "Reversed"));
    }

    // ── Payment status ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("Paid")]
    [InlineData("Partial")]
    [InlineData("FULLY_PAID")]
    [InlineData("Overdue")]
    [InlineData("whatever")]
    public async Task Only_Unpaid_or_Scheduled_can_be_set_by_hand_the_rest_follows_the_payments(string status)
    {
        var rig = PurchaseRig.New();
        var (uuid, _, _) = await rig.ApprovedAsync();

        await rig.Service.Invoking(s => s.PatchAsync(uuid, new PatchInvoiceRequest { PaymentStatus = status }, PurchaseRig.User))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*Only Unpaid or Scheduled*");

        (await rig.LoadAsync(uuid)).PaymentStatus.Should().Be("Unpaid");
    }

    [Fact]
    public async Task An_approved_unpaid_invoice_can_be_scheduled_and_unscheduled()
    {
        var rig = PurchaseRig.New();
        var (uuid, _, _) = await rig.ApprovedAsync();

        await rig.Service.PatchAsync(uuid, new PatchInvoiceRequest { PaymentStatus = "scheduled" }, PurchaseRig.User);
        (await rig.LoadAsync(uuid)).PaymentStatus.Should().Be("Scheduled");

        await rig.Service.PatchAsync(uuid, new PatchInvoiceRequest { PaymentStatus = "UNPAID" }, PurchaseRig.User);
        (await rig.LoadAsync(uuid)).PaymentStatus.Should().Be("Unpaid");
    }

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("APPROVED")]
    [InlineData("POSTED")]
    public async Task Once_a_supplier_payment_is_on_the_invoice_its_payment_status_and_supplier_number_are_refused(string paymentStatus)
    {
        var rig = PurchaseRig.New();
        var (uuid, _, _) = await rig.ApprovedAsync();
        await rig.SupplierPaymentAsync(uuid, paymentStatus);

        await rig.Service.Invoking(s => s.PatchAsync(uuid, new PatchInvoiceRequest { PaymentStatus = "Scheduled" }, PurchaseRig.User))
            .Should().ThrowAsync<ConflictException>().WithMessage("*payment status now follows them*");
        await rig.Service.Invoking(s => s.PatchAsync(uuid, new PatchInvoiceRequest { SupplierInvoiceNo = "OTHER" }, PurchaseRig.User))
            .Should().ThrowAsync<ConflictException>().WithMessage("*supplier's invoice number*");

        // Neither is a financial field, and the same number again is not a change.
        await rig.Service.PatchAsync(uuid, new PatchInvoiceRequest { DueDate = new DateTime(2026, 12, 1), SupplierInvoiceNo = "KSW/881" }, PurchaseRig.User);
        (await rig.LoadAsync(uuid)).DueDate.Should().Be(new DateTime(2026, 12, 1));
    }

    [Fact]
    public async Task A_legacy_payment_freezes_the_tax_of_an_invoice_not_yet_approved()
    {
        var rig = PurchaseRig.New();
        var (po, lines) = await rig.PoAsync(true, (10m, 100m));
        var uuid = await rig.CreateAsync(rig.Request(po, lines));
        await rig.LegacyPaymentAsync(uuid, "Pending");

        await rig.Service.Invoking(s => s.PatchAsync(uuid, new PatchInvoiceRequest { TaxAmount = 170m }, PurchaseRig.User))
            .Should().ThrowAsync<ConflictException>().WithMessage("*its tax can no longer change*");
    }

    [Fact]
    public async Task A_paid_amount_alone_counts_as_a_payment()
    {
        var rig = PurchaseRig.New();
        var (uuid, _, _) = await rig.ApprovedAsync();
        await rig.TamperAsync(uuid, i => i.PaidAmount = 1m);

        await rig.Service.Invoking(s => s.PatchAsync(uuid, new PatchInvoiceRequest { PaymentStatus = "Scheduled" }, PurchaseRig.User))
            .Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Cancelled_and_bounced_supplier_payments_and_reversed_legacy_ones_do_not_count()
    {
        var rig = PurchaseRig.New();
        var (uuid, _, _) = await rig.ApprovedAsync();
        await rig.SupplierPaymentAsync(uuid, "CANCELLED");
        await rig.SupplierPaymentAsync(uuid, "BOUNCED");
        await rig.LegacyPaymentAsync(uuid, "Reversed");

        await rig.Service.PatchAsync(uuid, new PatchInvoiceRequest { SupplierInvoiceNo = "KSW/881-R", PaymentStatus = "Scheduled" }, PurchaseRig.User);

        var invoice = await rig.LoadAsync(uuid);
        (invoice.SupplierInvoiceNo, invoice.PaymentStatus).Should().Be(("KSW/881-R", "Scheduled"));
    }

    // ── Approve ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Approving_twice_is_refused_and_books_nothing_more()
    {
        var rig = PurchaseRig.New();
        var (uuid, po, _) = await rig.ApprovedAsync();
        (await rig.PoNowAsync(po.UUID)).Lines.Single().QtyInvoiced.Should().Be(10m);

        await rig.Service.Invoking(s => s.ApproveAsync(uuid, "again", PurchaseRig.User))
            .Should().ThrowAsync<ConflictException>().WithMessage("*already approved*twice*");

        (await rig.LedgerAsync(uuid)).Should().ContainSingle("a second INVOICE_APPROVED debit is what the old re-approval posted");
        (await rig.MasterAsync(uuid)).Should().ContainSingle();
        var poNow = await rig.PoNowAsync(po.UUID);
        poNow.Lines.Single().QtyInvoiced.Should().Be(10m, "not 20 — the old re-approval doubled it");
        poNow.Status.Should().Be("CLOSED");
        (await rig.LoadAsync(uuid)).Notes.Should().BeNull("the refused approval's notes were not kept");
    }

    [Theory]
    [InlineData("Rejected")]
    [InlineData("Reversed")]
    public async Task A_rejected_or_reversed_invoice_cannot_be_approved(string status)
    {
        var rig = PurchaseRig.New();
        var uuid = await InvoiceInAsync(rig, status);
        var entries = (await rig.LedgerAsync(uuid)).Count;

        await rig.Service.Invoking(s => s.ApproveAsync(uuid, null, PurchaseRig.User))
            .Should().ThrowAsync<ConflictException>().WithMessage($"*is {status}, so it cannot be approved*");

        (await rig.LedgerAsync(uuid)).Should().HaveCount(entries);
        (await rig.LoadAsync(uuid)).MatchStatus.Should().Be(status);
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Matched")]
    [InlineData("Variance")]
    public async Task Pending_Matched_and_Variance_invoices_can_be_approved_once(string status)
    {
        var rig = PurchaseRig.New();
        var uuid = await InvoiceInAsync(rig, status);

        (await rig.Service.ApproveAsync(uuid, null, PurchaseRig.User)).Should().BeTrue();

        (await rig.LoadAsync(uuid)).MatchStatus.Should().Be("Approved");
        (await rig.LedgerAsync(uuid)).Should().ContainSingle().Which.TransactionType.Should().Be("INVOICE_APPROVED");
    }

    // ── Reject ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Approved", "*Reverse it instead*")]
    [InlineData("Reversed", "*nothing left to reject*")]
    public async Task An_approved_or_reversed_invoice_cannot_be_rejected(string status, string message)
    {
        var rig = PurchaseRig.New();
        var uuid = await InvoiceInAsync(rig, status);

        await rig.Service.Invoking(s => s.RejectAsync(uuid, "Wrong prices", PurchaseRig.User))
            .Should().ThrowAsync<ConflictException>().WithMessage(message);

        (await rig.LoadAsync(uuid)).MatchStatus.Should().Be(status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_rejection_needs_a_reason(string reason)
    {
        var rig = PurchaseRig.New();
        var uuid = await InvoiceInAsync(rig, "Matched");

        await rig.Service.Invoking(s => s.RejectAsync(uuid, reason, PurchaseRig.User)).Should().ThrowAsync<BadRequestException>();
        await rig.Service.Invoking(s => s.RejectAsync(uuid, new string('x', 301), PurchaseRig.User))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*at most 300*");

        (await rig.LoadAsync(uuid)).MatchStatus.Should().Be("Matched");
    }

    [Fact]
    public async Task A_matched_invoice_is_rejected_with_its_reason()
    {
        var rig = PurchaseRig.New();
        var uuid = await InvoiceInAsync(rig, "Matched");

        (await rig.Service.RejectAsync(uuid, "  Wrong prices  ", PurchaseRig.User)).Should().BeTrue();

        var invoice = await rig.LoadAsync(uuid);
        (invoice.MatchStatus, invoice.Notes).Should().Be(("Rejected", "Wrong prices"));
        (await rig.Service.RejectAsync(Guid.NewGuid(), "x", PurchaseRig.User)).Should().BeFalse();
    }
}

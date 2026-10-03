using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Repositories;
using SMS.Modules.Finance.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Finance.Tests.SupplierInvoices;

/// <summary>S-5 — the exchange rate is fixed when the invoice is approved; a missing one never blocks it.</summary>
public class ExchangeRateSnapshotAtApprovalTests
{
    [Fact]
    public async Task An_invoice_in_the_base_currency_is_snapshotted_at_rate_one_without_asking_for_a_rate()
    {
        var rig = PurchaseRig.New("PKR");

        var (uuid, _, _) = await rig.ApprovedAsync();

        var invoice = await rig.LoadAsync(uuid);
        (invoice.ExchangeRate, invoice.BaseCurrencyCode, invoice.BaseTotalAmount).Should().Be((1m, "PKR", 1000m));
        rig.Rates.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task A_foreign_invoice_takes_the_rate_on_its_invoice_date_and_converts_the_total()
    {
        var rig = PurchaseRig.New("pkr");
        rig.Rates.Rates[("USD", "PKR")] = 278.4567m;
        var (po, lines) = await rig.PoAsync(true, (3m, 411.52m));
        var uuid = await rig.CreateAsync(rig.Request(po, lines, currency: "usd"));

        (await rig.Service.ApproveAsync(uuid, null, PurchaseRig.User)).Should().BeTrue();

        var invoice = await rig.LoadAsync(uuid);
        invoice.TotalAmount.Should().Be(1234.56m);
        (invoice.ExchangeRate, invoice.BaseCurrencyCode, invoice.BaseTotalAmount).Should().Be((278.4567m, "PKR", 343771.50m));
        invoice.BaseTotalAmount.Should().Be(ExchangeRateMath.Convert(1234.56m, 278.4567m));
        rig.Rates.Asked.Should().ContainSingle().Which.Should().Be(("USD", "PKR", new DateTime(2026, 9, 15)));

        var detail = await rig.Service.GetByUuidAsync(uuid);
        (detail!.ExchangeRate, detail.BaseCurrencyCode, detail.BaseTotalAmount).Should().Be((278.4567m, "PKR", 343771.50m));
    }

    [Fact]
    public async Task A_missing_rate_leaves_the_rate_and_base_total_null_and_the_invoice_is_still_approved()
    {
        var rig = PurchaseRig.New("PKR");
        var (po, lines) = await rig.PoAsync(true, (10m, 100m));
        var uuid = await rig.CreateAsync(rig.Request(po, lines, currency: "EUR"));

        (await rig.Service.ApproveAsync(uuid, null, PurchaseRig.User)).Should().BeTrue();

        var invoice = await rig.LoadAsync(uuid);
        (invoice.MatchStatus, invoice.ExchangeRate, invoice.BaseCurrencyCode, invoice.BaseTotalAmount)
            .Should().Be(("Approved", (decimal?)null, "PKR", (decimal?)null));
        (await rig.LedgerAsync(uuid)).Should().ContainSingle().Which.DebitAmount.Should().Be(1000m);
    }

    [Fact]
    public async Task A_rate_provider_that_fails_never_blocks_the_approval()
    {
        var rig = PurchaseRig.New("PKR");
        rig.Rates.Throw = new InvalidOperationException("rates table unreachable");
        var (po, lines) = await rig.PoAsync(true, (10m, 100m));
        var uuid = await rig.CreateAsync(rig.Request(po, lines, currency: "USD"));

        (await rig.Service.ApproveAsync(uuid, null, PurchaseRig.User)).Should().BeTrue();

        var invoice = await rig.LoadAsync(uuid);
        (invoice.MatchStatus, invoice.ExchangeRate, invoice.BaseTotalAmount).Should().Be(("Approved", (decimal?)null, (decimal?)null));
    }

    [Fact]
    public async Task An_organization_with_no_base_currency_gets_no_snapshot()
    {
        var rig = PurchaseRig.New(baseCurrency: null);

        var (uuid, _, _) = await rig.ApprovedAsync();

        var invoice = await rig.LoadAsync(uuid);
        (invoice.ExchangeRate, invoice.BaseCurrencyCode, invoice.BaseTotalAmount).Should().Be(((decimal?)null, (string?)null, (decimal?)null));
        rig.Rates.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task There_is_no_snapshot_before_approval_nor_in_a_harness_without_the_lookups()
    {
        var rig = PurchaseRig.New("PKR");
        var created = await rig.CreateAsync(rig.Request(subtotal: 1000m));
        (await rig.LoadAsync(created)).ExchangeRate.Should().BeNull();

        var bare = PurchaseRig.New("PKR", withLookups: false);
        var (approved, _, _) = await bare.ApprovedAsync();
        var invoice = await bare.LoadAsync(approved);
        (invoice.MatchStatus, invoice.ExchangeRate, invoice.BaseCurrencyCode).Should().Be(("Approved", (decimal?)null, (string?)null));
    }
}

/// <summary>S-7 — reversing an approved, unpaid supplier invoice.</summary>
public class SupplierInvoiceReversalTests
{
    private const string Reason = "Duplicate of INV-2026-00007";

    [Fact]
    public async Task Reversing_credits_what_the_approval_debited_mirrors_it_to_the_master_ledger_and_rolls_the_PO_back()
    {
        var rig = PurchaseRig.New();
        var gst = rig.TaxCodes.Add("GST17", 17m);
        var (uuid, po, _) = await rig.ApprovedAsync(gst.Uuid, 0m, (10m, 100m), (5m, 200m));
        var approved = await rig.LoadAsync(uuid);
        approved.TotalAmount.Should().Be(2340m);
        (await rig.PoNowAsync(po.UUID)).Status.Should().Be("CLOSED");

        (await rig.Service.ReverseAsync(uuid, $"  {Reason}  ", PurchaseRig.User)).Should().BeTrue();

        var ledger = await rig.LedgerAsync(uuid);
        ledger.Should().HaveCount(2);
        ledger[0].Should().BeEquivalentTo(new { TransactionType = "INVOICE_APPROVED", DebitAmount = 2340m, CreditAmount = 0m, BalanceAfter = 2340m });
        ledger[1].Should().BeEquivalentTo(new
        {
            TransactionType = "INVOICE_REVERSED", ReferenceType = "Invoice", ReferenceNo = approved.InvoiceNumber,
            DebitAmount = 0m, CreditAmount = 2340m, BalanceAfter = 0m, CreatedBy = PurchaseRig.User
        });
        ledger[1].Narration.Should().Contain(Reason);

        var master = await rig.MasterAsync(uuid);
        master.Select(m => (m.TransactionType, m.DebitAmount, m.CreditAmount, m.BalanceAfter))
            .Should().Equal(("INVOICE_APPROVED", 2340m, 0m, 2340m), ("INVOICE_REVERSED", 0m, 2340m, 0m));
        master[1].SupplierName.Should().Be("Karachi Steel");

        var invoice = await rig.LoadAsync(uuid);
        invoice.MatchStatus.Should().Be("Reversed");
        invoice.ReversedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        (invoice.ReversedBy, invoice.ReversalReason).Should().Be((PurchaseRig.User, Reason));
        (invoice.Subtotal, invoice.TaxAmount, invoice.TotalAmount, invoice.TaxCode).Should().Be((2000m, 340m, 2340m, "GST17"), "nothing about the invoice was edited");

        var poNow = await rig.PoNowAsync(po.UUID);
        poNow.Lines.Select(l => l.QtyInvoiced).Should().Equal(0m, 0m);
        poNow.Status.Should().Be("RECEIVED", "everything is received and nothing is invoiced any more");

        var detail = await rig.Service.GetByUuidAsync(uuid);
        (detail!.MatchStatus, detail.ReversedBy, detail.ReversalReason).Should().Be(("Reversed", PurchaseRig.User, Reason));
        detail.ReversedAt.Should().NotBeNull();
        (await rig.Service.GetListAsync(new InvoiceFilter { MatchStatus = "Reversed" })).Data.Should().ContainSingle().Which.UUID.Should().Be(uuid);
    }

    [Fact]
    public async Task Reversing_one_of_two_invoices_leaves_the_PO_partly_invoiced()
    {
        var rig = PurchaseRig.New();
        var (po, lines) = await rig.PoAsync(true, (10m, 100m));
        var halves = new List<Guid>();
        for (var i = 0; i < 2; i++)
        {
            var request = rig.Request(po, lines);
            request.Lines![0].QtyInvoiced = 5m;
            var uuid = await rig.CreateAsync(request);
            await rig.Service.ApproveAsync(uuid, null, PurchaseRig.User);
            rig.Forget();
            halves.Add(uuid);
        }
        (await rig.PoNowAsync(po.UUID)).Status.Should().Be("CLOSED");

        await rig.Service.ReverseAsync(halves[0], Reason, PurchaseRig.User);

        var poNow = await rig.PoNowAsync(po.UUID);
        (poNow.Lines.Single().QtyInvoiced, poNow.Status).Should().Be((5m, "PARTIALLY_INVOICED"));
    }

    [Fact]
    public async Task An_invoice_with_no_lines_rolls_the_PO_back_through_its_GRN()
    {
        var rig = PurchaseRig.New();
        var (po, lines) = await rig.PoAsync(true, (10m, 100m));
        var grn = await rig.GrnAsync(po, lines, 10m);
        var uuid = await rig.CreateAsync(rig.Request(po, subtotal: 1000m, grn: grn.UUID));
        await rig.Service.ApproveAsync(uuid, null, PurchaseRig.User);
        rig.Forget();
        (await rig.PoNowAsync(po.UUID)).Lines.Single().QtyInvoiced.Should().Be(10m);

        await rig.Service.ReverseAsync(uuid, Reason, PurchaseRig.User);

        var poNow = await rig.PoNowAsync(po.UUID);
        (poNow.Lines.Single().QtyInvoiced, poNow.Status).Should().Be((0m, "RECEIVED"));
    }

    [Fact]
    public async Task A_PO_with_nothing_received_goes_back_to_SENT_not_to_a_receipt_it_never_had()
    {
        var rig = PurchaseRig.New();
        var (po, lines) = await rig.PoAsync(false, (10m, 100m));
        var uuid = await rig.CreateAsync(rig.Request(po, lines));
        await rig.Service.ApproveAsync(uuid, null, PurchaseRig.User);
        rig.Forget();

        await rig.Service.ReverseAsync(uuid, Reason, PurchaseRig.User);

        (await rig.PoNowAsync(po.UUID)).Status.Should().Be("SENT");
    }

    [Fact]
    public async Task A_PO_whose_status_approval_did_not_set_keeps_it()
    {
        var rig = PurchaseRig.New();
        var (uuid, po, _) = await rig.ApprovedAsync();
        var tracked = await rig.Demand.PurchaseOrders.SingleAsync(p => p.UUID == po.UUID);
        tracked.Status = "CANCELLED";
        await rig.Demand.SaveChangesAsync();
        rig.Forget();

        await rig.Service.ReverseAsync(uuid, Reason, PurchaseRig.User);

        var poNow = await rig.PoNowAsync(po.UUID);
        (poNow.Lines.Single().QtyInvoiced, poNow.Status).Should().Be((0m, "CANCELLED"));
    }

    [Fact]
    public async Task A_reversed_invoice_can_be_entered_again_and_approved()
    {
        var rig = PurchaseRig.New();
        var (uuid, po, lines) = await rig.ApprovedAsync();
        await rig.Service.ReverseAsync(uuid, Reason, PurchaseRig.User);
        rig.Forget();

        var again = await rig.CreateAsync(rig.Request(po, lines));
        (await rig.Service.ApproveAsync(again, null, PurchaseRig.User)).Should().BeTrue();

        var poNow = await rig.PoNowAsync(po.UUID);
        (poNow.Lines.Single().QtyInvoiced, poNow.Status).Should().Be((10m, "CLOSED"));
        var balance = await rig.Finance.SupplierLedgerEntries.AsNoTracking()
            .Where(e => e.SupplierId == rig.Supplier).OrderByDescending(e => e.SequenceNo).Select(e => e.BalanceAfter).FirstAsync();
        balance.Should().Be(1000m, "approve 1000, reverse −1000, approve 1000");
    }

    // ── Refusals ─────────────────────────────────────────────────────────────

    private static async Task ShouldBeRefusedAsync(PurchaseRig rig, Guid uuid, string message, string expectedStatus = "Approved")
    {
        var before = await rig.LedgerAsync(uuid);

        await rig.Service.Invoking(s => s.ReverseAsync(uuid, Reason, PurchaseRig.User))
            .Should().ThrowAsync<ConflictException>().WithMessage(message);

        (await rig.LoadAsync(uuid)).MatchStatus.Should().Be(expectedStatus);
        var after = await rig.LedgerAsync(uuid);
        after.Should().HaveCount(before.Count, "a refused reversal posts nothing");
        after.Count(e => e.TransactionType == "INVOICE_REVERSED").Should().Be(before.Count(e => e.TransactionType == "INVOICE_REVERSED"));
    }

    [Theory]
    [InlineData("Matched")]
    [InlineData("Variance")]
    [InlineData("Pending")]
    [InlineData("Rejected")]
    public async Task Only_an_approved_invoice_can_be_reversed(string status)
    {
        var rig = PurchaseRig.New();
        Guid uuid;
        if (status == "Pending")
            uuid = await rig.CreateAsync(rig.Request(subtotal: 1000m));
        else
        {
            var (po, lines) = await rig.PoAsync(true, (10m, 100m));
            uuid = await rig.CreateAsync(rig.Request(po, lines));
            if (status == "Variance") await rig.Service.PatchAsync(uuid, new PatchInvoiceRequest { MatchStatus = "Variance" }, PurchaseRig.User);
            if (status == "Rejected") await rig.Service.RejectAsync(uuid, "Wrong prices", PurchaseRig.User);
            rig.Forget();
        }

        await ShouldBeRefusedAsync(rig, uuid, $"*Only an approved invoice can be reversed*is {status}*", status);
    }

    [Fact]
    public async Task An_invoice_cannot_be_reversed_twice()
    {
        var rig = PurchaseRig.New();
        var (uuid, po, _) = await rig.ApprovedAsync();
        await rig.Service.ReverseAsync(uuid, Reason, PurchaseRig.User);
        rig.Forget();

        await ShouldBeRefusedAsync(rig, uuid, "*already been reversed*", "Reversed");
        (await rig.LedgerAsync(uuid)).Should().HaveCount(2);
        (await rig.PoNowAsync(po.UUID)).Lines.Single().QtyInvoiced.Should().Be(0m, "not rolled back a second time");
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(100)]
    public async Task A_paid_or_partly_paid_invoice_cannot_be_reversed(int paid)
    {
        var rig = PurchaseRig.New();
        var (uuid, po, _) = await rig.ApprovedAsync();
        await rig.TamperAsync(uuid, i =>
        {
            i.PaidAmount    = paid;
            i.PaymentStatus = InvoicePaymentStatus.Derive(paid, i.TotalAmount);
        });

        await ShouldBeRefusedAsync(rig, uuid, $"*has {paid:N2} PKR paid against it*");
        (await rig.PoNowAsync(po.UUID)).Lines.Single().QtyInvoiced.Should().Be(10m);
    }

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("APPROVED")]
    [InlineData("POSTED")]
    public async Task An_invoice_on_a_supplier_payment_cannot_be_reversed(string paymentStatus)
    {
        var rig = PurchaseRig.New();
        var (uuid, _, _) = await rig.ApprovedAsync();
        var payment = await rig.SupplierPaymentAsync(uuid, paymentStatus);

        await ShouldBeRefusedAsync(rig, uuid, $"*is on payment {payment.PaymentNumber}*cancel that payment*");
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Processed")]
    [InlineData("Cleared")]
    public async Task An_invoice_with_a_legacy_payment_cannot_be_reversed(string paymentStatus)
    {
        var rig = PurchaseRig.New();
        var (uuid, _, _) = await rig.ApprovedAsync();
        var payment = await rig.LegacyPaymentAsync(uuid, paymentStatus);

        await ShouldBeRefusedAsync(rig, uuid, $"*is on payment {payment.PaymentNumber}*");
    }

    [Fact]
    public async Task An_invoice_a_credit_note_was_deducted_from_cannot_be_reversed()
    {
        var rig = PurchaseRig.New();
        var (uuid, _, _) = await rig.ApprovedAsync();
        var note = await rig.CreditNoteAppliedAsync(uuid, 100m);

        await ShouldBeRefusedAsync(rig, uuid, $"*{note.CreditNoteNumber} has been deducted from*");
    }

    [Fact]
    public async Task Cancelled_or_bounced_supplier_payments_and_reversed_legacy_ones_do_not_block_a_reversal()
    {
        var rig = PurchaseRig.New();
        var (uuid, _, _) = await rig.ApprovedAsync();
        await rig.SupplierPaymentAsync(uuid, "CANCELLED");
        await rig.SupplierPaymentAsync(uuid, "BOUNCED");
        await rig.LegacyPaymentAsync(uuid, "Reversed");

        (await rig.Service.ReverseAsync(uuid, Reason, PurchaseRig.User)).Should().BeTrue();

        (await rig.LoadAsync(uuid)).MatchStatus.Should().Be("Reversed");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task A_reversal_needs_a_reason(string? reason)
    {
        var rig = PurchaseRig.New();
        var (uuid, _, _) = await rig.ApprovedAsync();

        await rig.Service.Invoking(s => s.ReverseAsync(uuid, reason!, PurchaseRig.User))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*why*");

        (await rig.LoadAsync(uuid)).MatchStatus.Should().Be("Approved");
    }

    [Fact]
    public async Task The_reason_can_be_500_characters_but_no_more()
    {
        var rig = PurchaseRig.New();
        var (tooLong, _, _) = await rig.ApprovedAsync();
        var (justRight, _, _) = await rig.ApprovedAsync();

        await rig.Service.Invoking(s => s.ReverseAsync(tooLong, new string('r', 501), PurchaseRig.User))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*at most 500*");
        (await rig.Service.ReverseAsync(justRight, new string('r', 500), PurchaseRig.User)).Should().BeTrue();

        (await rig.LedgerAsync(justRight)).Last().Narration!.Length.Should().BeLessThanOrEqualTo(500, "the narration column is 500 long");
    }

    [Fact]
    public async Task Reversing_an_invoice_that_does_not_exist_finds_nothing()
    {
        var rig = PurchaseRig.New();

        (await rig.Service.ReverseAsync(Guid.NewGuid(), Reason, PurchaseRig.User)).Should().BeFalse();
    }

    // ── Old data ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_invoice_the_old_re_approval_bug_booked_twice_is_undone_in_full()
    {
        var rig = PurchaseRig.New();
        var (uuid, po, _) = await rig.ApprovedAsync();
        // What the old code allowed: the status dropped back (a tax edit did that) and it was approved again.
        await rig.TamperAsync(uuid, i => i.MatchStatus = "Matched");
        await rig.Service.ApproveAsync(uuid, null, PurchaseRig.User);
        rig.Forget();
        (await rig.LedgerAsync(uuid)).Should().HaveCount(2);
        (await rig.PoNowAsync(po.UUID)).Lines.Single().QtyInvoiced.Should().Be(20m);

        await rig.Service.ReverseAsync(uuid, Reason, PurchaseRig.User);

        var reversal = (await rig.LedgerAsync(uuid)).Last();
        (reversal.TransactionType, reversal.CreditAmount, reversal.BalanceAfter).Should().Be(("INVOICE_REVERSED", 2000m, 0m));
        (await rig.PoNowAsync(po.UUID)).Lines.Single().QtyInvoiced.Should().Be(0m);
    }

    [Fact]
    public async Task An_invoice_approved_before_the_ledger_existed_is_reversed_without_a_ledger_entry()
    {
        var rig = PurchaseRig.New();
        var uuid = await rig.CreateAsync(rig.Request(subtotal: 1000m));
        await rig.TamperAsync(uuid, i => { i.MatchStatus = "Approved"; i.ApprovedAt = DateTime.UtcNow; });

        (await rig.Service.ReverseAsync(uuid, Reason, PurchaseRig.User)).Should().BeTrue();

        (await rig.LoadAsync(uuid)).MatchStatus.Should().Be("Reversed");
        (await rig.LedgerAsync(uuid)).Should().BeEmpty("there was no approval debit to undo");
    }

    // ── What a reversed invoice is no longer part of ─────────────────────────

    [Fact]
    public async Task A_reversed_invoice_is_not_outstanding_aged_or_payable()
    {
        var rig = PurchaseRig.New();
        var (reversed, _, _) = await rig.ApprovedAsync();
        var (live, _, _)     = await rig.ApprovedAsync();
        await rig.Service.ReverseAsync(reversed, Reason, PurchaseRig.User);
        rig.Forget();

        var payments = new SupplierPaymentRepository(rig.Finance, new SupplierLedgerService(rig.Finance), new Mock<INotificationService>().Object);

        (await payments.GetOutstandingInvoicesAsync(rig.Supplier)).Select(i => i.InvoiceUuid).Should().Equal(live);
        (await payments.GetSupplierAgingAsync(rig.Supplier)).GrandTotal.Should().Be(1000m);
        (await payments.GetCrossSupplierAgingAsync()).GrandTotalRow!.GrandTotal.Should().Be(1000m);
        (await payments.GetOutstandingPayablesAsync(new OutstandingPayablesFilter())).Data.SelectMany(g => g.Invoices)
            .Select(i => i.InvoiceUuid).Should().Equal(live);

        await payments.Invoking(p => p.CreateAsync(new CreateSupplierPaymentRequest
        {
            SupplierId = rig.Supplier, SupplierName = "Karachi Steel", PaymentDate = new DateTime(2026, 9, 30),
            PaymentMethod = "CASH", TotalAmount = 100m,
            Lines = [new CreateSupplierPaymentLineRequest { InvoiceUuid = reversed, AllocatedAmount = 100m }]
        }, PurchaseRig.User)).Should().ThrowAsync<BadRequestException>().WithMessage("*has been reversed*");

        await new PaymentRepository(rig.Finance).Invoking(p => p.CreateAsync(new CreatePaymentRequest
        {
            InvoiceUuid = reversed, PaymentDate = new DateTime(2026, 9, 30), AmountPaid = 100m, PaymentMethod = "Cash"
        }, PurchaseRig.User)).Should().ThrowAsync<UnprocessableEntityException>().WithMessage("*has been reversed*");
    }

    [Fact]
    public async Task A_carried_forward_credit_or_debit_note_cannot_be_deducted_from_a_reversed_invoice()
    {
        var rig = PurchaseRig.New();
        var (uuid, _, _) = await rig.ApprovedAsync();
        await rig.Service.ReverseAsync(uuid, Reason, PurchaseRig.User);
        rig.Forget();

        var credit = new CreditNote
        {
            UUID = Guid.NewGuid(), CreditNoteNumber = "CN-2026-00001", SupplierCreditNoteNo = "S-1", SroUuid = Guid.NewGuid(), SroNumber = "SRO-1",
            SupplierId = rig.Supplier, SupplierName = "Karachi Steel", CreditDate = new DateTime(2026, 9, 20), CreditAmount = 50m,
            ApplicationStatus = "CARRIED_FORWARD", CarriedForwardAmount = 50m, CreatedBy = 1, CreatedDate = DateTime.UtcNow
        };
        var debit = new DebitNote
        {
            UUID = Guid.NewGuid(), DebitNoteNumber = "DN-2026-00001", SroUuid = Guid.NewGuid(), SroNumber = "SRO-2",
            SupplierId = rig.Supplier, SupplierName = "Karachi Steel", DebitReason = "SHORT_SUPPLY", DebitAmount = 50m,
            ApplicationStatus = "CARRIED_FORWARD", CarriedForwardAmount = 50m, CreatedBy = 1, CreatedDate = DateTime.UtcNow
        };
        rig.Finance.CreditNotes.Add(credit);
        rig.Finance.DebitNotes.Add(debit);
        await rig.Finance.SaveChangesAsync();
        rig.Forget();

        var ledger = new SupplierLedgerService(rig.Finance);
        await new CreditNoteRepository(rig.Finance, rig.Warehouse, ledger)
            .Invoking(r => r.ApplyCarriedForwardAsync(credit.UUID, new ApplyCreditNoteRequest { InvoiceUuid = uuid }, PurchaseRig.User))
            .Should().ThrowAsync<UnprocessableEntityException>().WithMessage("*reversed invoice*");
        await new DebitNoteRepository(rig.Finance, rig.Warehouse, new Mock<IBackgroundJobClient>().Object,
                new Mock<INotificationService>().Object, new Mock<IAuditService>().Object, ledger)
            .Invoking(r => r.ApplyCarriedForwardAsync(debit.UUID, new ApplyDebitNoteRequest { InvoiceUuid = uuid }, PurchaseRig.User))
            .Should().ThrowAsync<UnprocessableEntityException>().WithMessage("*reversed invoice*");

        (await rig.LoadAsync(uuid)).TotalAmount.Should().Be(1000m);
    }
}

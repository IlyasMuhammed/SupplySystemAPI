using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.ArAp.Payables;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Integration.Posting;
using SMS.Modules.Finance.Services;
using SMS.Shared.Exceptions;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;
using Xunit;
using static SMS.Modules.Finance.Tests.Posting.PostingAssert;

namespace SMS.Modules.Finance.Tests.ArApPayables;

/// <summary>A48 C11 — TS-PR-001..011, TS-EP-003, BR-PR-001..010 through the real services (in-memory).</summary>
public class PaymentRunServiceTests
{
    private static readonly ApActor Actor = ApRig.Actor;

    /// <summary>5 vendors × 3 invoices of 100, 200, 300 each, due within the month.</summary>
    private static async Task<(ApRig Rig, List<Guid> Vendors, List<Guid> Invoices)> FifteenAsync()
    {
        var rig = ApRig.New();
        var vendors = Enumerable.Range(1, 5).Select(_ => Guid.NewGuid()).ToList();
        var invoices = new List<Guid>();
        for (var v = 0; v < vendors.Count; v++)
        {
            rig.Partners.Vendor(vendors[v], $"Vendor {v + 1}", $"V-{v + 1:D3}", "HBL", $"0001{v}234567");
            for (var i = 1; i <= 3; i++)
                invoices.Add(await rig.ApprovedInvoiceAsync(vendors[v], 100m * i, rig.Today.AddDays(5 * i)));
        }
        return (rig, vendors, invoices);
    }

    [Fact]
    public async Task TS_PR_001_five_vendors_fifteen_items_give_the_right_totals()
    {
        var (rig, _, _) = await FifteenAsync();

        var run = await rig.Runs.ProposeAsync(rig.Proposal(), Actor);

        run.Run.Status.Should().Be("Proposed");
        run.Run.RunNumber.Should().StartWith("PR-");
        (run.Run.TotalVendors, run.Run.TotalItems, run.Run.TotalAmount, run.Run.TotalDiscount, run.Run.NetPaymentAmount)
            .Should().Be((5, 15, 3000m, 0m, 3000m));
        run.VendorGroups.Should().HaveCount(5).And.OnlyContain(g => g.ItemCount == 3 && g.NetAmount == 600m && g.PaymentMethod == "BANK_TRANSFER");
        run.VendorGroups[0].BankDetailsMasked.Should().Be("HBL ••••4567");
        run.Run.ProposedByName.Should().Be("Tess Tester");
        (await rig.Fin.Set<PaymentSchedule>().CountAsync(s => s.Status == "Scheduled" && s.ReservedByRunId != null)).Should().Be(15);
    }

    [Fact]
    public async Task BR_PR_001_only_items_due_by_the_cutoff_are_proposed()
    {
        var (rig, _, _) = await FifteenAsync();
        var run = await rig.Runs.ProposeAsync(rig.Proposal(cutoff: rig.Today.AddDays(10)), Actor);
        run.Run.TotalItems.Should().Be(10, "the third invoice of each vendor is due on day 15");

        var nothing = rig.Proposal(cutoff: rig.Today.AddDays(-1));
        await FluentActions.Awaiting(() => rig.Runs.ProposeAsync(nothing, Actor)).Should().ThrowAsync<BadRequestException>()
            .WithMessage($"Nothing is due by {rig.Today.AddDays(-1):yyyy-MM-dd}.");
    }

    [Fact]
    public async Task TS_PR_002_blocked_items_are_excluded_and_listed_separately()
    {
        var (rig, _, invoices) = await FifteenAsync();
        await rig.Schedules.BlockAsync((await rig.ScheduleAsync(invoices[0])).Uuid, "Quality dispute", Actor);
        rig.Forget();

        var run = await rig.Runs.ProposeAsync(rig.Proposal(), Actor);

        run.Run.TotalItems.Should().Be(14);
        run.VendorGroups.SelectMany(g => g.Items).Should().NotContain(i => i.InvoiceUuid == invoices[0]);
        run.BlockedItems.Should().ContainSingle(b => b.InvoiceUuid == invoices[0] && b.BlockReason == "Quality dispute");
    }

    [Fact]
    public async Task Filters_vendor_currency_method_and_priority()
    {
        var (rig, vendors, _) = await FifteenAsync();
        await rig.VendorProfileAsync(vendors[1], "High", "CHEQUE");
        var other = await rig.ApprovedInvoiceAsync(vendors[1], 50m, rig.Today.AddDays(2));
        var schedule = await rig.Fin.Set<PaymentSchedule>().SingleAsync(s => s.InvoiceUuid == other);
        schedule.PaymentPriority.Should().Be("High", "taken from the vendor profile when the schedule was created");
        rig.Forget();

        var byVendor = rig.Proposal(); byVendor.VendorUuid = vendors[0];
        (await rig.Runs.ProposeAsync(byVendor, Actor)).Run.TotalItems.Should().Be(3);

        // A method filter leaves out what the schedule or the vendor's profile says is paid otherwise; the rest is paid by it.
        var transfers = rig.Proposal(); transfers.PaymentMethod = "BANK_TRANSFER";
        var bank = await rig.Runs.ProposeAsync(transfers, Actor);
        bank.Run.TotalItems.Should().Be(9);
        bank.VendorGroups.Should().NotContain(g => g.VendorUuid == vendors[1]);

        var cheques = rig.Proposal(); cheques.PaymentMethod = "CHEQUE";
        var run = await rig.Runs.ProposeAsync(cheques, Actor);
        // The vendor's earlier schedules have no method of their own: the profile's preferred CHEQUE applies to them too.
        run.VendorGroups.Should().ContainSingle().Which.Items.Should().HaveCount(4).And.Contain(i => i.InvoiceUuid == other);

        var high = rig.Proposal(); high.MinPriority = "High";
        await FluentActions.Awaiting(() => rig.Runs.ProposeAsync(high, Actor)).Should().ThrowAsync<BadRequestException>("the high one is already in a run");
    }

    [Fact]
    public async Task TS_PR_003_removing_an_item_recalculates_and_releases_its_schedule_and_it_can_be_restored()
    {
        var (rig, _, invoices) = await FifteenAsync();
        var run = await rig.Runs.ProposeAsync(rig.Proposal(), Actor);
        var item = run.VendorGroups.SelectMany(g => g.Items).Single(i => i.InvoiceUuid == invoices[2]); // 300

        await FluentActions.Awaiting(() => rig.Runs.RemoveItemAsync(run.Run.Uuid, item.ItemUuid, "", Actor))
            .Should().ThrowAsync<BadRequestException>().WithMessage("A removal needs a reason.");

        var after = await rig.Runs.RemoveItemAsync(run.Run.Uuid, item.ItemUuid, "Pay next week", Actor);
        (after.Run.TotalItems, after.Run.TotalAmount).Should().Be((14, 2700m));
        after.RemovedItems.Should().ContainSingle(i => i.ItemUuid == item.ItemUuid && i.RemovedReason == "Pay next week");
        after.VendorGroups.Single(g => g.Items.Count == 2).NetAmount.Should().Be(300m);
        (await rig.ScheduleAsync(invoices[2])).Should().Match<PaymentSchedule>(s => s.Status == "Open" && s.ReservedByRunId == null);

        var restored = await rig.Runs.RestoreItemAsync(run.Run.Uuid, item.ItemUuid, Actor);
        (restored.Run.TotalItems, restored.Run.TotalAmount).Should().Be((15, 3000m));
        (await rig.ScheduleAsync(invoices[2])).Status.Should().Be("Scheduled");
    }

    [Fact]
    public async Task Changing_an_items_method_moves_it_to_its_own_batch()
    {
        var (rig, _, invoices) = await FifteenAsync();
        var run = await rig.Runs.ProposeAsync(rig.Proposal(), Actor);
        var item = run.VendorGroups.SelectMany(g => g.Items).Single(i => i.InvoiceUuid == invoices[0]);

        var after = await rig.Runs.UpdateItemMethodAsync(run.Run.Uuid, item.ItemUuid, "cheque", Actor);

        after.VendorGroups.Should().HaveCount(6);
        after.VendorGroups.Single(g => g.PaymentMethod == "CHEQUE").Items.Should().ContainSingle(i => i.ItemUuid == item.ItemUuid);
        after.Run.TotalAmount.Should().Be(3000m);
        await FluentActions.Awaiting(() => rig.Runs.UpdateItemMethodAsync(run.Run.Uuid, item.ItemUuid, "BITCOIN", Actor))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Unknown paymentMethod 'BITCOIN'.*");
    }

    [Fact]
    public async Task TS_PR_004_approve_sets_the_run_and_its_items_Approved_and_it_can_no_longer_be_edited()
    {
        var (rig, _, _) = await FifteenAsync();
        var run = await rig.Runs.ProposeAsync(rig.Proposal(), Actor);
        (await rig.Runs.ReviewAsync(run.Run.Uuid, Actor)).Run.Status.Should().Be("Reviewing");

        var approved = await rig.Runs.ApproveAsync(run.Run.Uuid, Actor);

        approved.Run.Status.Should().Be("Approved");
        approved.Run.ApprovedByName.Should().Be("Tess Tester");
        approved.VendorGroups.SelectMany(g => g.Items).Should().OnlyContain(i => i.Status == "Approved");
        var any = approved.VendorGroups[0].Items[0].ItemUuid;
        await FluentActions.Awaiting(() => rig.Runs.RemoveItemAsync(run.Run.Uuid, any, "late", Actor)).Should().ThrowAsync<ConflictException>()
            .WithMessage($"Payment run {run.Run.RunNumber} is Approved; it can no longer change.");
    }

    [Fact]
    public async Task A_bank_transfer_run_needs_the_bank_account_to_pay_from()
    {
        var (rig, _, _) = await FifteenAsync();
        var request = rig.Proposal(); request.BankAccount = null;
        var run = await rig.Runs.ProposeAsync(request, Actor);
        await FluentActions.Awaiting(() => rig.Runs.ApproveAsync(run.Run.Uuid, Actor)).Should().ThrowAsync<BadRequestException>()
            .WithMessage("BankAccount is required for BANK_TRANSFER payments*");
    }

    [Fact]
    public async Task TS_PR_005_006_007_execute_makes_one_posted_payment_per_vendor_and_settles_every_invoice()
    {
        var (rig, vendors, invoices) = await FifteenAsync();
        var run = await rig.Runs.ProposeAsync(rig.Proposal(), Actor);
        await rig.Runs.ApproveAsync(run.Run.Uuid, Actor);

        var done = await rig.Runs.ExecuteAsync(run.Run.Uuid, Actor);

        done.Run.Status.Should().Be("Completed");
        done.Run.FailedBatchCount.Should().Be(0);
        done.VendorGroups.Should().OnlyContain(g => g.Status == "Paid" && g.SupplierPaymentNumber != null);

        // TS-PR-005: one SupplierPayment per vendor, posted, through the existing payment path.
        var payments = await rig.Fin.SupplierPayments.AsNoTracking().Include(p => p.Lines).ToListAsync();
        payments.Should().HaveCount(5).And.OnlyContain(p => p.Status == "POSTED" && p.Lines.Count == 3 && p.TotalAmount == 600m);
        rig.Gateway.Posts.Where(t => t.DocumentType == FinDocType.BillPayment).Should().HaveCount(5);

        // TS-PR-006: schedules Paid in this run.
        var runId = await rig.Fin.Set<PaymentRun>().Where(r => r.Uuid == run.Run.Uuid).Select(r => r.Id).SingleAsync();
        (await rig.Fin.Set<PaymentSchedule>().AsNoTracking().ToListAsync())
            .Should().OnlyContain(s => s.Status == "Paid" && s.PaidInRunId == runId && s.ReservedByRunId == null && s.AmountRemaining == 0m);

        // TS-PR-007: every invoice fully paid, every vendor's ledger back to zero.
        foreach (var invoice in invoices)
            (await rig.P.LoadAsync(invoice)).PaymentStatus.Should().Be(InvoicePaymentStatus.FullyPaid);
        foreach (var vendor in vendors)
            (await rig.LedgerOfVendorAsync(vendor)).Last().BalanceAfter.Should().Be(0m);
    }

    [Fact]
    public async Task TS_PR_008_TS_EP_003_an_early_discount_is_taken_settled_in_the_ledger_and_posted_Dr_AP_Cr_Bank_Cr_Discount()
    {
        var rig = ApRig.New(new DateOnly(2026, 10, 8));
        var vendor = Guid.NewGuid();
        rig.Partners.Vendor(vendor, "Lahore Paper", "V-100", "MCB", "99887766");
        var term = await rig.TermAsync();
        await rig.VendorProfileAsync(vendor, termId: term.Id);
        var invoice = await rig.ApprovedInvoiceAsync(vendor, 1000m, new DateOnly(2026, 10, 31), invoiceDate: new DateOnly(2026, 10, 1)); // deadline 10-11

        var run = await rig.Runs.ProposeAsync(rig.Proposal(cutoff: new DateOnly(2026, 10, 15)), Actor);
        var item = run.VendorGroups.Single().Items.Single();
        (item.HasDiscount, item.DiscountAmount, item.NetPaymentAmount, item.DiscountDate).Should().Be((true, 20m, 980m, new DateOnly(2026, 10, 11)));
        (run.Run.TotalAmount, run.Run.TotalDiscount, run.Run.NetPaymentAmount).Should().Be((1000m, 20m, 980m));

        await rig.Runs.ApproveAsync(run.Run.Uuid, Actor);
        await rig.Runs.ExecuteAsync(run.Run.Uuid, Actor);

        var payment = await rig.Fin.SupplierPayments.AsNoTracking().Include(p => p.Lines).SingleAsync();
        payment.TotalAmount.Should().Be(980m, "the cash");
        (payment.Lines.Single().AllocatedAmount, payment.Lines.Single().DiscountAmount).Should().Be((1000m, 20m));
        var stored = await rig.P.LoadAsync(invoice);
        (stored.PaidAmount, stored.PaymentStatus).Should().Be((1000m, InvoicePaymentStatus.FullyPaid));

        var ledger = await rig.LedgerOfVendorAsync(vendor);
        ledger.Should().Contain(e => e.TransactionType == "PAYMENT_POSTED" && e.CreditAmount == 980m);
        ledger.Should().Contain(e => e.TransactionType == "PAYMENT_DISCOUNT" && e.CreditAmount == 20m);
        ledger.Last().BalanceAfter.Should().Be(0m);

        var tx = rig.Gateway.Posts.Single(t => t.DocumentType == FinDocType.BillPayment);
        Balanced(tx);
        Dr(tx, PostingRoles.Payable).Should().Be(1000m);
        Cr(tx, PostingRoles.Bank).Should().Be(980m);
        Cr(tx, PostingRoles.DiscountReceived).Should().Be(20m);
        var doc = (BillPaymentPayload)tx.Metadata!.Document!;
        (doc.Amount, doc.Applications.Single().Amount).Should().Be((980m, 980m));
        doc.PrivateNote.Should().Contain("Early-payment discount 20.00");
    }

    [Fact]
    public async Task Without_discounts_the_full_amount_is_paid_even_inside_the_window()
    {
        var rig = ApRig.New(new DateOnly(2026, 10, 8));
        var vendor = Guid.NewGuid();
        var term = await rig.TermAsync();
        await rig.VendorProfileAsync(vendor, termId: term.Id);
        await rig.ApprovedInvoiceAsync(vendor, 1000m, new DateOnly(2026, 10, 9), invoiceDate: new DateOnly(2026, 10, 1));

        var request = rig.Proposal(); request.IncludeDiscountItems = false;
        var run = await rig.Runs.ProposeAsync(request, Actor);

        run.Run.TotalDiscount.Should().Be(0m);
    }

    [Fact]
    public async Task Discount_items_not_yet_due_are_included_while_the_discount_lasts()
    {
        var rig = ApRig.New(new DateOnly(2026, 10, 8));
        var vendor = Guid.NewGuid();
        var term = await rig.TermAsync();
        await rig.VendorProfileAsync(vendor, termId: term.Id);
        await rig.ApprovedInvoiceAsync(vendor, 1000m, new DateOnly(2026, 10, 31), invoiceDate: new DateOnly(2026, 10, 1));

        var run = await rig.Runs.ProposeAsync(rig.Proposal(cutoff: new DateOnly(2026, 10, 9)), Actor);

        run.Run.TotalItems.Should().Be(1, "due on 10-31, but its discount (until 10-11) is still there to take");
    }

    [Fact]
    public async Task TS_PR_009_the_payment_file_is_a_CSV_with_the_vendors_bank_details()
    {
        var (rig, _, _) = await FifteenAsync();
        var run = await rig.Runs.ProposeAsync(rig.Proposal(), Actor);
        await FluentActions.Awaiting(() => rig.Runs.ExportAsync(run.Run.Uuid, "csv", Actor)).Should().ThrowAsync<ConflictException>();
        await rig.Runs.ApproveAsync(run.Run.Uuid, Actor);
        await rig.Runs.ExecuteAsync(run.Run.Uuid, Actor);

        var file = await rig.Runs.ExportAsync(run.Run.Uuid, "CSV", Actor);

        file.FileName.Should().Be($"{run.Run.RunNumber}.csv");
        file.ContentType.Should().Be("text/csv");
        var lines = Encoding.UTF8.GetString(file.Content).TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().Be("RunNumber,PaymentDate,VendorCode,VendorName,BankName,AccountNumber,IBAN,SWIFT,Amount,Currency,PaymentMethod,Reference,Invoices");
        lines.Should().HaveCount(6);
        lines[1].Should().StartWith($"{run.Run.RunNumber},{rig.Today:yyyy-MM-dd},V-001,Vendor 1,HBL,00010234567,PK0000010234567,HABBPKKA,600.00,PKR,BANK_TRANSFER,SPAY-");
        (await rig.Runs.GetAsync(run.Run.Uuid)).PaymentFileName.Should().Be(file.FileName);
        await FluentActions.Awaiting(() => rig.Runs.ExportAsync(run.Run.Uuid, "sepa", Actor)).Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public void CSV_fields_are_quoted_and_never_run_as_formulas()
    {
        PaymentRunService.Csv("Acme, Ltd").Should().Be("\"Acme, Ltd\"");
        PaymentRunService.Csv("Say \"hi\"").Should().Be("\"Say \"\"hi\"\"\"");
        PaymentRunService.Csv("=SUM(A1)").Should().Be("'=SUM(A1)");
        PaymentRunService.Csv(null).Should().BeEmpty();
    }

    [Fact]
    public async Task TS_PR_010_cancelling_an_approved_run_pays_nothing_and_releases_its_schedules()
    {
        var (rig, _, _) = await FifteenAsync();
        var run = await rig.Runs.ProposeAsync(rig.Proposal(), Actor);
        await rig.Runs.ApproveAsync(run.Run.Uuid, Actor);

        await FluentActions.Awaiting(() => rig.Runs.CancelAsync(run.Run.Uuid, null, Actor)).Should().ThrowAsync<BadRequestException>();
        var cancelled = await rig.Runs.CancelAsync(run.Run.Uuid, "Cash position", Actor);

        cancelled.Run.Status.Should().Be("Cancelled");
        cancelled.CancelReason.Should().Be("Cash position");
        (await rig.Fin.SupplierPayments.CountAsync()).Should().Be(0);
        (await rig.Fin.Set<PaymentSchedule>().AsNoTracking().ToListAsync()).Should().OnlyContain(s => s.Status == "Open" && s.ReservedByRunId == null);

        await FluentActions.Awaiting(() => rig.Runs.CancelAsync(run.Run.Uuid, "again", Actor)).Should().ThrowAsync<ConflictException>()
            .WithMessage("Only a draft, proposed, reviewing or approved payment run can be cancelled.");
        (await rig.Runs.ProposeAsync(rig.Proposal(), Actor)).Run.TotalItems.Should().Be(15, "the released schedules can be proposed again");
    }

    [Fact]
    public async Task TS_PR_011_BR_PR_009_a_completed_run_cannot_be_executed_again_or_changed()
    {
        var (rig, _, _) = await FifteenAsync();
        var run = await rig.Runs.ProposeAsync(rig.Proposal(), Actor);
        await FluentActions.Awaiting(() => rig.Runs.ExecuteAsync(run.Run.Uuid, Actor)).Should().ThrowAsync<ConflictException>()
            .WithMessage("Only an approved payment run can be executed.", "BR-PR-006: approval first");
        await rig.Runs.ApproveAsync(run.Run.Uuid, Actor);
        await rig.Runs.ExecuteAsync(run.Run.Uuid, Actor);

        await FluentActions.Awaiting(() => rig.Runs.ExecuteAsync(run.Run.Uuid, Actor)).Should().ThrowAsync<ConflictException>()
            .WithMessage("Only an approved payment run can be executed.");
        await FluentActions.Awaiting(() => rig.Runs.CancelAsync(run.Run.Uuid, "late", Actor)).Should().ThrowAsync<ConflictException>();
        (await rig.Fin.SupplierPayments.CountAsync()).Should().Be(5, "nothing paid twice");
    }

    [Fact]
    public async Task TS_PR_012_a_second_proposal_never_takes_what_the_first_holds()
    {
        var (rig, _, _) = await FifteenAsync();
        await rig.Runs.ProposeAsync(rig.Proposal(), Actor);
        await FluentActions.Awaiting(() => rig.Runs.ProposeAsync(rig.Proposal(), Actor)).Should().ThrowAsync<BadRequestException>();
        (await rig.Fin.Set<PaymentRun>().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_failed_batch_is_Failed_with_its_reason_and_the_others_are_paid()
    {
        var (rig, vendors, invoices) = await FifteenAsync();
        var run = await rig.Runs.ProposeAsync(rig.Proposal(), Actor);
        await rig.Runs.ApproveAsync(run.Run.Uuid, Actor);
        // Between approval and execution one invoice stops being a payable (as a reversal the hooks missed would).
        await rig.P.TamperAsync(invoices[0], i => i.MatchStatus = InvoiceMatchStatus.Rejected);

        var done = await rig.Runs.ExecuteAsync(run.Run.Uuid, Actor);

        done.Run.Status.Should().Be("Completed");
        done.Run.FailedBatchCount.Should().Be(1);
        var failed = done.VendorGroups.Single(g => g.Status == "Failed");
        failed.VendorUuid.Should().Be(vendors[0]);
        failed.ErrorMessage.Should().Contain("rejected");
        failed.Items.Should().OnlyContain(i => i.Status == "Failed");
        done.VendorGroups.Count(g => g.Status == "Paid").Should().Be(4);
        (done.Run.TotalItems, done.Run.TotalAmount).Should().Be((12, 2400m), "the totals of a completed run are what was paid");
        (await rig.Fin.SupplierPayments.CountAsync()).Should().Be(4);
        (await rig.ScheduleAsync(invoices[1])).Should().Match<PaymentSchedule>(s => s.ReservedByRunId == null && s.Status == "Open");
    }

    [Fact]
    public async Task Runs_are_listed_newest_first_and_filtered_by_status()
    {
        var (rig, _, _) = await FifteenAsync();
        var run = await rig.Runs.ProposeAsync(rig.Proposal(), Actor);
        (await rig.Runs.ListAsync(null, 1, 25)).Data.Should().ContainSingle(r => r.Uuid == run.Run.Uuid);
        (await rig.Runs.ListAsync("Completed", 1, 25)).TotalRecords.Should().Be(0);
        await FluentActions.Awaiting(() => rig.Runs.ListAsync("Done", 1, 25)).Should().ThrowAsync<BadRequestException>();
        await FluentActions.Awaiting(() => rig.Runs.GetAsync(Guid.NewGuid())).Should().ThrowAsync<NotFoundException>();
    }
}

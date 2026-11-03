using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.ArAp.Payables;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Integration.Posting;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Shared.Exceptions;
using SMS.Shared.Finance.Posting;
using Xunit;
using static SMS.Modules.Finance.Tests.Posting.PostingAssert;

namespace SMS.Modules.Finance.Tests.ArApPayables;

/// <summary>A48 C14 (TS-CM-004/005) — AP debit memos; AP-1 — the supplier payment's early-payment discount.</summary>
public class ApDebitMemoAndDiscountTests
{
    private static readonly ApActor Actor = ApRig.Actor;

    [Fact]
    public async Task TS_CM_004_a_debit_memo_against_a_vendor_invoice_reduces_it_and_posts_Dr_Payable_Cr_Purchase()
    {
        var rig = ApRig.New();
        var vendor = Guid.NewGuid();
        var invoice = await rig.ApprovedInvoiceAsync(vendor, 1000m, new DateOnly(2026, 10, 31));

        var memo = await rig.Memos.CreateAsync(new CreateApDebitMemoRequest
        {
            VendorUuid = vendor, OriginalInvoiceUuid = invoice, Reason = "price_difference", ReasonDetail = "Billed 10 above the PO price", Amount = 100m
        }, Actor);

        (memo.Origin, memo.ApplicationStatus, memo.RemainingAmount, memo.DebitReason).Should().Be(("MEMO", "APPLIED_TO_INVOICE", 0m, "PRICE_DIFFERENCE"));
        memo.Applications.Should().ContainSingle(a => a.InvoiceUuid == invoice && a.Amount == 100m);
        (await rig.P.LoadAsync(invoice)).TotalAmount.Should().Be(900m);
        (await rig.ScheduleAsync(invoice)).AmountRemaining.Should().Be(900m);
        (await rig.LedgerOfVendorAsync(vendor)).Should().Contain(e => e.TransactionType == "DEBIT_NOTE_APPROVED" && e.CreditAmount == 100m);

        var note = await rig.Fin.DebitNotes.AsNoTracking().SingleAsync();
        (note.SroUuid, note.SroNumber, note.DebitNoteNumber).Should().Be((Guid.Empty, "", memo.DebitNoteNumber));
        memo.DebitNoteNumber.Should().StartWith("DN-");

        var tx = rig.Gateway.Credits.Single(t => t.DocumentType == FinDocType.VendorCredit);
        Balanced(tx);
        Dr(tx, PostingRoles.Payable).Should().Be(100m);
        Cr(tx, PostingRoles.Purchase).Should().Be(100m);
        tx.Description.Should().Be($"Debit memo {memo.DebitNoteNumber}: PRICE_DIFFERENCE");
    }

    [Fact]
    public async Task TS_CM_005_a_carried_forward_memo_is_applied_in_parts_to_several_invoices()
    {
        var rig = ApRig.New();
        var vendor = Guid.NewGuid();
        var a = await rig.ApprovedInvoiceAsync(vendor, 500m, new DateOnly(2026, 10, 20));
        var b = await rig.ApprovedInvoiceAsync(vendor, 500m, new DateOnly(2026, 10, 25));
        var memo = await rig.Memos.CreateAsync(new CreateApDebitMemoRequest { VendorUuid = vendor, Reason = "REBATE", Amount = 300m }, Actor);
        (memo.ApplicationStatus, memo.RemainingAmount).Should().Be(("CARRIED_FORWARD", 300m));
        rig.Forget();

        await FluentActions.Awaiting(() => rig.Memos.ApplyAsync(memo.Uuid, new ApplyApDebitMemoRequest
            { Applications = [new() { InvoiceUuid = a, Amount = 200m }, new() { InvoiceUuid = b, Amount = 200m }] }, Actor))
            .Should().ThrowAsync<BadRequestException>().WithMessage($"{memo.DebitNoteNumber} has only 300.00 left to apply.");

        var partly = await rig.Memos.ApplyAsync(memo.Uuid, new ApplyApDebitMemoRequest
            { Applications = [new() { InvoiceUuid = a, Amount = 120m }, new() { InvoiceUuid = b, Amount = 80m }] }, Actor);
        (partly.ApplicationStatus, partly.RemainingAmount, partly.Applications.Count).Should().Be(("CARRIED_FORWARD", 100m, 2));
        (await rig.P.LoadAsync(a)).TotalAmount.Should().Be(380m);
        (await rig.P.LoadAsync(b)).TotalAmount.Should().Be(420m);
        (await rig.ScheduleAsync(a)).AmountRemaining.Should().Be(380m);
        rig.Forget();

        var rest = await rig.Memos.ApplyAsync(memo.Uuid, new ApplyApDebitMemoRequest { Applications = [new() { InvoiceUuid = b, Amount = 100m }] }, Actor);
        (rest.ApplicationStatus, rest.RemainingAmount).Should().Be(("APPLIED", 0m));
        (await rig.Memos.ApplicationsAsync(memo.Uuid)).Sum(x => x.Amount).Should().Be(300m);

        // A partly applied memo still stops the invoice being reversed (D-6).
        await FluentActions.Awaiting(() => rig.Invoices.ReverseAsync(a, "mistake", 7)).Should().ThrowAsync<ConflictException>()
            .WithMessage($"{memo.DebitNoteNumber} has been deducted from invoice*");
    }

    [Fact]
    public async Task A_memo_cannot_be_applied_to_another_suppliers_invoice_or_above_what_it_owes()
    {
        var rig = ApRig.New();
        var vendor = Guid.NewGuid();
        var mine = await rig.ApprovedInvoiceAsync(vendor, 50m, new DateOnly(2026, 10, 20));
        var theirs = await rig.ApprovedInvoiceAsync(Guid.NewGuid(), 500m, new DateOnly(2026, 10, 20));
        var memo = await rig.Memos.CreateAsync(new CreateApDebitMemoRequest { VendorUuid = vendor, Reason = "REBATE", Amount = 100m }, Actor);
        rig.Forget();

        await FluentActions.Awaiting(() => rig.Memos.ApplyAsync(memo.Uuid, new ApplyApDebitMemoRequest { Applications = [new() { InvoiceUuid = theirs, Amount = 10m }] }, Actor))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*belongs to another supplier.");
        await FluentActions.Awaiting(() => rig.Memos.ApplyAsync(memo.Uuid, new ApplyApDebitMemoRequest { Applications = [new() { InvoiceUuid = mine, Amount = 60m }] }, Actor))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*has only 50.00 open.");
        await FluentActions.Awaiting(() => rig.Memos.ApplyAsync(Guid.NewGuid(), new ApplyApDebitMemoRequest { Applications = [new() { InvoiceUuid = mine, Amount = 1m }] }, Actor))
            .Should().ThrowAsync<NotFoundException>();
        await FluentActions.Awaiting(() => rig.Memos.CreateAsync(new CreateApDebitMemoRequest { VendorUuid = vendor, Reason = "", Amount = 1m }, Actor))
            .Should().ThrowAsync<BadRequestException>();
        await FluentActions.Awaiting(() => rig.Memos.CreateAsync(new CreateApDebitMemoRequest { VendorUuid = vendor, OriginalInvoiceUuid = theirs, Reason = "X", Amount = 1m }, Actor))
            .Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task FE_8_a_debit_memo_reads_by_uuid_and_a_vendors_list_filters_open_ones()
    {
        var rig = ApRig.New();
        var vendor = Guid.NewGuid();
        var invoice = await rig.ApprovedInvoiceAsync(vendor, 500m, new DateOnly(2026, 10, 20));
        var applied = await rig.Memos.CreateAsync(new CreateApDebitMemoRequest { VendorUuid = vendor, OriginalInvoiceUuid = invoice, Reason = "REBATE", Amount = 50m }, Actor);
        var open = await rig.Memos.CreateAsync(new CreateApDebitMemoRequest { VendorUuid = vendor, Reason = "REBATE", Amount = 80m }, Actor);
        rig.Forget();
        await rig.Memos.ApplyAsync(open.Uuid, new ApplyApDebitMemoRequest { Applications = [new() { InvoiceUuid = invoice, Amount = 30m }] }, Actor);
        await rig.Memos.CreateAsync(new CreateApDebitMemoRequest { VendorUuid = Guid.NewGuid(), Reason = "REBATE", Amount = 10m }, Actor);
        rig.Forget();

        var detail = await rig.Memos.GetAsync(open.Uuid);
        (detail.RemainingAmount, detail.Applications.Count, detail.ApplicationStatus).Should().Be((50m, 1, "CARRIED_FORWARD"));
        await FluentActions.Awaiting(() => rig.Memos.GetAsync(Guid.NewGuid())).Should().ThrowAsync<NotFoundException>();

        var all = await rig.Memos.ListByVendorAsync(vendor, openOnly: false, 1, 25);
        all.TotalRecords.Should().Be(2);
        all.Data.Select(d => d.Uuid).Should().Equal(open.Uuid, applied.Uuid);
        all.Data.Single(d => d.Uuid == applied.Uuid).Applications.Should().ContainSingle(a => a.Amount == 50m);
        // FE-10: the memo's currency — its original invoice's; without one, the purchase base (unknown in this rig → null).
        all.Data.Single(d => d.Uuid == applied.Uuid).CurrencyCode.Should().Be("PKR");
        applied.CurrencyCode.Should().Be("PKR", "the create response carries it too");
        detail.CurrencyCode.Should().BeNull();
        (await rig.Memos.ListByVendorAsync(vendor, openOnly: true, 1, 25)).Data.Should().ContainSingle(d => d.Uuid == open.Uuid && d.RemainingAmount == 50m);
    }

    [Fact]
    public async Task A_memo_bigger_than_the_invoice_owes_is_carried_forward()
    {
        var rig = ApRig.New();
        var vendor = Guid.NewGuid();
        var invoice = await rig.ApprovedInvoiceAsync(vendor, 50m, new DateOnly(2026, 10, 20));
        var memo = await rig.Memos.CreateAsync(new CreateApDebitMemoRequest { VendorUuid = vendor, OriginalInvoiceUuid = invoice, Reason = "REBATE", Amount = 80m }, Actor);
        (memo.ApplicationStatus, memo.RemainingAmount).Should().Be(("CARRIED_FORWARD", 80m));
        (await rig.P.LoadAsync(invoice)).TotalAmount.Should().Be(50m);
    }

    // ── AP-1: the supplier payment's discount ────────────────────────────────

    /// <summary>An invoice of 1000 dated 10-01 under 2/10 Net 30: its discount (20) lasts until 10-11.</summary>
    private static async Task<(ApRig Rig, Guid Vendor, Guid Invoice)> TermInvoiceAsync(SMS.Shared.Finance.Posting.IPostingRoleReadiness? roles = null)
    {
        var rig = ApRig.New(roles: roles);
        var vendor = Guid.NewGuid();
        var term = await rig.TermAsync();
        await rig.VendorProfileAsync(vendor, termId: term.Id);
        var invoice = await rig.ApprovedInvoiceAsync(vendor, 1000m, new DateOnly(2026, 10, 31), invoiceDate: new DateOnly(2026, 10, 1));
        return (rig, vendor, invoice);
    }

    private sealed class MissingDiscountAccount : SMS.Shared.Finance.Posting.IPostingRoleReadiness
    {
        public Task<IReadOnlyList<string>> MissingRolesAsync(IReadOnlyCollection<string> roles, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(roles.ToList());
    }

    [Fact]
    public async Task REV_5_a_manual_discount_needs_the_invoices_term_its_date_its_cap_and_the_discount_account()
    {
        var (rig, vendor, invoice) = await TermInvoiceAsync();
        CreateSupplierPaymentRequest Pay(DateTime date, decimal discount) => new()
        {
            SupplierId = vendor, SupplierName = "Test Supplier", PaymentDate = date, PaymentMethod = "CASH", TotalAmount = 1000m - discount,
            Lines = [new CreateSupplierPaymentLineRequest { InvoiceUuid = invoice, AllocatedAmount = 1000m, DiscountAmount = discount }]
        };

        await FluentActions.Awaiting(() => rig.PaymentsRepo.CreateAsync(Pay(new DateTime(2026, 10, 5), 999m), 7)).Should().ThrowAsync<BadRequestException>()
            .WithMessage("The discount on invoice * can be at most 20.00 (2% of 1,000.00).");
        await FluentActions.Awaiting(() => rig.PaymentsRepo.CreateAsync(Pay(new DateTime(2026, 10, 12), 20m), 7)).Should().ThrowAsync<BadRequestException>()
            .WithMessage("The early-payment discount on invoice * was available until 2026-10-11.");

        var other = await rig.ApprovedInvoiceAsync(Guid.NewGuid(), 500m, new DateOnly(2026, 10, 31));
        var otherVendor = (await rig.P.LoadAsync(other)).SupplierId;
        await FluentActions.Awaiting(() => rig.PaymentsRepo.CreateAsync(new CreateSupplierPaymentRequest
        {
            SupplierId = otherVendor, SupplierName = "x", PaymentDate = new DateTime(2026, 10, 5), PaymentMethod = "CASH", TotalAmount = 490m,
            Lines = [new CreateSupplierPaymentLineRequest { InvoiceUuid = other, AllocatedAmount = 500m, DiscountAmount = 10m }]
        }, 7)).Should().ThrowAsync<BadRequestException>().WithMessage("Invoice * has no early-payment term, so no discount can be taken on it.");

        var (noAccount, v2, i2) = await TermInvoiceAsync(new MissingDiscountAccount());
        await FluentActions.Awaiting(() => noAccount.PaymentsRepo.CreateAsync(new CreateSupplierPaymentRequest
        {
            SupplierId = v2, SupplierName = "x", PaymentDate = new DateTime(2026, 10, 5), PaymentMethod = "CASH", TotalAmount = 980m,
            Lines = [new CreateSupplierPaymentLineRequest { InvoiceUuid = i2, AllocatedAmount = 1000m, DiscountAmount = 20m }]
        }, 7)).Should().ThrowAsync<ConflictException>().WithMessage("*DEFAULT_DISCOUNT_RECEIVED*");
    }

    [Fact]
    public async Task REV_6_a_debit_memo_is_applied_only_to_invoices_in_its_currency()
    {
        var rig = ApRig.New();
        var vendor = Guid.NewGuid();
        var pkr = await rig.ApprovedInvoiceAsync(vendor, 50m, new DateOnly(2026, 10, 20));
        var usd = await rig.ApprovedInvoiceAsync(vendor, 500m, new DateOnly(2026, 10, 20), currency: "USD");
        // Against the PKR invoice but bigger than it owes: carried forward, in PKR.
        var memo = await rig.Memos.CreateAsync(new CreateApDebitMemoRequest { VendorUuid = vendor, OriginalInvoiceUuid = pkr, Reason = "REBATE", Amount = 80m }, Actor);
        rig.Forget();

        await FluentActions.Awaiting(() => rig.Memos.ApplyAsync(memo.Uuid, new ApplyApDebitMemoRequest { Applications = [new() { InvoiceUuid = usd, Amount = 10m }] }, Actor))
            .Should().ThrowAsync<BadRequestException>().WithMessage($"{memo.DebitNoteNumber} is in PKR; invoice * is in USD.");
        (await rig.P.LoadAsync(usd)).TotalAmount.Should().Be(500m);

        // No original invoice and no known base currency: one application cannot mix currencies.
        var loose = await rig.Memos.CreateAsync(new CreateApDebitMemoRequest { VendorUuid = vendor, Reason = "REBATE", Amount = 30m }, Actor);
        rig.Forget();
        await FluentActions.Awaiting(() => rig.Memos.ApplyAsync(loose.Uuid, new ApplyApDebitMemoRequest
            { Applications = [new() { InvoiceUuid = pkr, Amount = 10m }, new() { InvoiceUuid = usd, Amount = 10m }] }, Actor))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*is in PKR; invoice * is in USD.");
    }

    [Fact]
    public async Task A_payment_line_discount_must_be_between_0_and_the_line_and_the_total_is_the_cash()
    {
        var (rig, vendor, invoice) = await TermInvoiceAsync();
        CreateSupplierPaymentRequest Pay(decimal total, decimal discount) => new()
        {
            SupplierId = vendor, SupplierName = "Test Supplier", PaymentDate = new DateTime(2026, 10, 5), PaymentMethod = "CASH", TotalAmount = total,
            Lines = [new CreateSupplierPaymentLineRequest { InvoiceUuid = invoice, AllocatedAmount = 1000m, DiscountAmount = discount }]
        };

        await FluentActions.Awaiting(() => rig.PaymentsRepo.CreateAsync(Pay(1000m, 20m), 7)).Should().ThrowAsync<BadRequestException>()
            .WithMessage("Sum of payment line allocations less their discounts (980.00) must equal TotalAmount (1,000.00).");
        await FluentActions.Awaiting(() => rig.PaymentsRepo.CreateAsync(Pay(0m, 1001m), 7)).Should().ThrowAsync<BadRequestException>();
        await FluentActions.Awaiting(() => rig.PaymentsRepo.CreateAsync(Pay(1020m, -20m), 7)).Should().ThrowAsync<BadRequestException>()
            .WithMessage("A line's DiscountAmount must be between 0 and its AllocatedAmount.");

        var ok = await rig.PaymentsRepo.CreateAsync(Pay(980m, 20m), 7);
        rig.Forget();
        (await rig.PaymentsRepo.GetByUuidAsync(ok))!.Lines.Single().DiscountAmount.Should().Be(20m);
    }

    [Fact]
    public async Task A_bounced_cheque_withdraws_the_discount_too()
    {
        var (rig, vendor, invoice) = await TermInvoiceAsync();
        var payment = await rig.Payments.CreateAsync(new CreateSupplierPaymentRequest
        {
            SupplierId = vendor, SupplierName = "Test Supplier", PaymentDate = new DateTime(2026, 10, 5), PaymentMethod = "CHEQUE", ChequeNo = "001",
            ChequeDate = new DateTime(2026, 10, 5), TotalAmount = 980m,
            Lines = [new CreateSupplierPaymentLineRequest { InvoiceUuid = invoice, AllocatedAmount = 1000m, DiscountAmount = 20m }]
        }, 7);
        rig.Forget();
        await rig.Payments.ApproveAsync(payment, 7); rig.Forget();
        await rig.Payments.PostAsync(payment, 7); rig.Forget();
        (await rig.LedgerOfVendorAsync(vendor)).Last().BalanceAfter.Should().Be(0m);
        (await rig.ScheduleAsync(invoice)).Status.Should().Be("Paid");

        await rig.Payments.BounceAsync(payment, 7);
        rig.Forget();

        var ledger = await rig.LedgerOfVendorAsync(vendor);
        ledger.Should().Contain(e => e.TransactionType == "PAYMENT_DISCOUNT_REVERSED" && e.DebitAmount == 20m);
        ledger.Last().BalanceAfter.Should().Be(1000m, "owed in full again");
        (await rig.P.LoadAsync(invoice)).PaidAmount.Should().Be(0m);
        (await rig.ScheduleAsync(invoice)).Should().Match<PaymentSchedule>(s => s.Status == "Open" && s.AmountRemaining == 1000m);
    }

    [Fact]
    public async Task Tolerance_rules_one_default_unique_names_one_active_rule_per_vendor()
    {
        var rig = ApRig.New();
        var first = await rig.Rules.CreateAsync(new ToleranceRuleRequest { Name = "DEFAULT", PriceTolerancePercent = 2, IsDefault = true }, Actor);
        await rig.Rules.CreateAsync(new ToleranceRuleRequest { Name = "x" }, Actor);
        await FluentActions.Awaiting(() => rig.Rules.CreateAsync(new ToleranceRuleRequest { Name = "DEFAULT" }, Actor)).Should().ThrowAsync<ConflictException>()
            .WithMessage("A tolerance rule named 'DEFAULT' already exists.");
        await FluentActions.Awaiting(() => rig.Rules.CreateAsync(new ToleranceRuleRequest { Name = "neg", PriceTolerancePercent = -1 }, Actor)).Should().ThrowAsync<BadRequestException>()
            .WithMessage("Tolerances cannot be negative.");
        await FluentActions.Awaiting(() => rig.Rules.CreateAsync(new ToleranceRuleRequest { Name = "big", QuantityTolerancePercent = 101 }, Actor)).Should().ThrowAsync<BadRequestException>()
            .WithMessage("A percentage cannot exceed 100.");

        var vendor = Guid.NewGuid();
        rig.Partners.Vendor(vendor, "Lahore Paper", "V-9");
        await rig.Rules.CreateAsync(new ToleranceRuleRequest { Name = "LP", VendorUuid = vendor, PriceTolerancePercent = 5 }, Actor);
        await FluentActions.Awaiting(() => rig.Rules.CreateAsync(new ToleranceRuleRequest { Name = "LP2", VendorUuid = vendor }, Actor)).Should().ThrowAsync<ConflictException>()
            .WithMessage("Lahore Paper already has an active tolerance rule (LP).");

        rig.Forget();
        await FluentActions.Awaiting(() => rig.Rules.UpdateAsync(first.Uuid, new ToleranceRuleRequest { IsActive = false }, Actor)).Should().ThrowAsync<ConflictException>()
            .WithMessage("The default tolerance rule cannot be deactivated; make another rule the default first.");
        rig.Forget();
        var other = (await rig.Rules.ListAsync()).Single(r => r.Name == "x");
        await rig.Rules.UpdateAsync(other.Uuid, new ToleranceRuleRequest { IsDefault = true }, Actor);
        rig.Forget();
        (await rig.Rules.ListAsync()).Where(r => r.IsDefault).Should().ContainSingle().Which.Name.Should().Be("x");
    }
}

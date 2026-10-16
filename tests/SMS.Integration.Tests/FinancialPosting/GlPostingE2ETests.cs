using System.Net;
using FluentAssertions;
using SMS.Integration.Tests.GeneralLedger;
using SMS.Integration.Tests.SapAlignment;
using Xunit;
using static SMS.Integration.Tests.FinancialPosting.Fp;

namespace SMS.Integration.Tests.FinancialPosting;

/// <summary>
/// A44 (QA) — the internal GL target fed by SCM's real screens (API-CONTRACT §3), on the real host (LocalDB):
/// <list type="bullet">
/// <item>T-09..T-12 (GL), T-55 — <see cref="Sales_invoice_payment_and_cancel_post_by_the_rules_and_AR_nets_to_zero"/>.</item>
/// <item>T-13..T-16 (GL), T-56..T-58, BR-26 — <see cref="Bills_supplier_payments_and_supplier_notes_post_by_the_rules"/>.</item>
/// <item>T-21 (rewritten, D-9), BR-03/04 — <see cref="A_locked_period_never_blocks_the_document_and_the_posting_is_retried"/>.</item>
/// <item>D-8 AutoPostToGL=false, GL off — <see cref="Draft_journals_when_auto_post_is_off_and_nothing_when_the_GL_is_off"/>.</item>
/// </list>
/// COGS is not posted on sales in A44 (D-28). <para>Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~GlPostingE2ETests</c>.</para>
/// </summary>
public sealed class GlPostingE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _root;

    public GlPostingE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _f.ShouldBeOnLocalDb();
        _root = new SapKit(factory, "A44");
    }

    private static string Show(IEnumerable<Journal> js) => string.Join("\n", js);

    [Fact]
    public async Task Sales_invoice_payment_and_cancel_post_by_the_rules_and_AR_nets_to_zero()
    {
        var o = await _root.NewOrgAsync("GLS");
        var k = o.K;
        await k.EnableGlAsync();
        var c = await k.DefaultCodesAsync();
        var gst = await k.EnsureTaxCodeAsync("GST17", 17m, "SALES", isDefault: true);
        var (_, _, customer, item) = await k.StockedWorldAsync("GLS", price: 100m);

        // SalesInvoice issue: Dr AR 234 ; Cr Revenue 200 ; Cr Tax payable 34
        var inv = await k.IssuedInvoiceAsync(customer, item, 2m, o.O.Pkr, gst);
        var invUuid = inv.G("uuid");
        var total = inv.D("grandTotal");
        total.Should().Be(234m, Gl.Opt(inv, "grandTotal"));
        var ij = (await _f.JournalsAsync(o.OrgId, invUuid)).Should().ContainSingle().Subject;
        (ij.Type, ij.Status, ij.Reference).Should().Be(("SALES_INVOICE", "Posted", inv.S("invoiceNumber")), ij.ToString());
        ij.Balanced.Should().BeTrue(ij.ToString());
        ij.Net(c["DEFAULT_RECEIVABLE"]).Should().Be(234m, ij.ToString());
        ij.Net(c["DEFAULT_REVENUE"]).Should().Be(-200m, ij.ToString());
        ij.Net(c["DEFAULT_TAX_PAYABLE"]).Should().Be(-34m, ij.ToString());
        ij.Lines.Should().NotContain(l => l.Code == c["DEFAULT_COGS"], "D-28: no COGS on sales in A44");

        // Payment (customer, CASH): Dr Cash ; Cr AR — and the invoice's AR nets to zero
        var pay = await k.RecordPaymentAsync(customer, 234m, "CASH", (invUuid, 234m));
        var pj = (await _f.JournalsAsync(o.OrgId, pay.G("paymentUuid"))).Should().ContainSingle().Subject;
        (pj.Type, pj.Balanced).Should().Be(("PAYMENT", true), pj.ToString());
        pj.Net(c["DEFAULT_CASH"]).Should().Be(234m, pj.ToString());
        pj.Net(c["DEFAULT_RECEIVABLE"]).Should().Be(-234m, pj.ToString());
        (await _f.NetAsync(o.OrgId, c["DEFAULT_RECEIVABLE"])).Should().Be(0m, "issued then fully paid");

        // a bank transfer on account (no allocation): Dr Bank ; Cr AR
        var bank = await k.RecordPaymentAsync(customer, 50m, "BANK_TRANSFER");
        var bj = (await _f.JournalsAsync(o.OrgId, bank.G("paymentUuid"))).Should().ContainSingle().Subject;
        bj.Net(c["DEFAULT_BANK"]).Should().Be(50m, bj.ToString());
        bj.Net(c["DEFAULT_RECEIVABLE"]).Should().Be(-50m, bj.ToString());

        // SalesInvoice cancel (T-55): a reversing journal of the issue; the source shows both, the movement nets to zero
        var inv2 = await k.IssuedInvoiceAsync(customer, item, 1m, o.O.Pkr, gst);
        await k.Ok(k.Post($"/api/sales-invoices/{inv2.G("uuid")}/cancel", new { reason = "A44 QA wrong customer" }), "cancel invoice");
        var cj = await _f.JournalsAsync(o.OrgId, inv2.G("uuid"));
        cj.Should().HaveCount(2, $"issue + reversal — {Show(cj)}");
        cj.Should().OnlyContain(j => j.Balanced && j.Status == "Posted", Show(cj));
        cj.Sum(j => j.Net(c["DEFAULT_RECEIVABLE"])).Should().Be(0m, Show(cj));
        cj.Sum(j => j.Net(c["DEFAULT_REVENUE"])).Should().Be(0m, Show(cj));
        cj[1].Key.Should().EndWith(":void", "D-13 void key");

        // replaying the cancel's posting (reconciliation / a retry) does not reverse twice
        await _f.RunJobsAsync(2);
        (await _f.JournalsAsync(o.OrgId, inv2.G("uuid"))).Should().HaveCount(2);
        // every journal of the org is balanced
        (await _f.AllJournalsAsync(o.OrgId)).Should().OnlyContain(j => j.Balanced);
    }

    [Fact]
    public async Task Bills_supplier_payments_and_supplier_notes_post_by_the_rules()
    {
        var o = await _root.NewOrgAsync("GLP");
        var k = o.K;
        await k.EnableGlAsync();
        var c = await k.DefaultCodesAsync();
        var vendor = await k.CreateVendorAsync("GLP Vendor");

        // PurchaseBill approve: Dr Purchase/Inventory 1000 ; Dr Tax receivable 170 ; Cr AP 1170
        var bill = await k.ApprovedBillAsync(vendor, 1000m, 170m);
        var bj = (await _f.JournalsAsync(o.OrgId, bill)).Should().ContainSingle().Subject;
        (bj.Type, bj.Balanced).Should().Be(("PURCHASE_BILL", true), bj.ToString());
        bj.Net(c["DEFAULT_PAYABLE"]).Should().Be(-1170m, bj.ToString());
        bj.Net(c["DEFAULT_TAX_RECEIVABLE"]).Should().Be(170m, bj.ToString());
        (bj.Net(c["DEFAULT_PURCHASE"]) + bj.Net(c["DEFAULT_INVENTORY"])).Should().Be(1000m, bj.ToString());

        // BillPayment (posted): Dr AP ; Cr Bank
        var sp = await k.PostedSupplierPaymentAsync(vendor, bill, 1170m, "BANK_TRANSFER");
        var spj = (await _f.JournalsAsync(o.OrgId, sp)).Should().ContainSingle().Subject;
        (spj.Type, spj.Balanced).Should().Be(("BILL_PAYMENT", true), spj.ToString());
        spj.Net(c["DEFAULT_PAYABLE"]).Should().Be(1170m);
        spj.Net(c["DEFAULT_BANK"]).Should().Be(-1170m);

        // a cash supplier payment credits Cash
        var billCash = await k.ApprovedBillAsync(vendor, 50m);
        var cashPay = await k.PostedSupplierPaymentAsync(vendor, billCash, 50m, "CASH");
        (await _f.JournalsAsync(o.OrgId, cashPay)).Single().Net(c["DEFAULT_CASH"]).Should().Be(-50m);

        // a cheque supplier payment, bounced after posting: the GL reverses it (T-56)
        var bill2 = await k.ApprovedBillAsync(vendor, 300m);
        var cheque = await k.PostedSupplierPaymentAsync(vendor, bill2, 300m, "CHEQUE");
        (await _f.JournalsAsync(o.OrgId, cheque)).Single().Net(c["DEFAULT_BANK"]).Should().Be(-300m);
        await k.Ok(k.Post($"/api/supplier-payments/{cheque}/bounce"), "bounce supplier payment");
        var bounced = await _f.JournalsAsync(o.OrgId, cheque);
        bounced.Should().HaveCount(2, Show(bounced));
        bounced.Sum(j => j.Net(c["DEFAULT_BANK"])).Should().Be(0m, Show(bounced));

        // PurchaseBill reverse (BR-26): GL reversal (no QuickBooks connected here → external no-op, BR-25)
        var bill3 = await k.ApprovedBillAsync(vendor, 400m);
        await k.Ok(k.Post($"/api/finance/invoices/{bill3}/reverse", new { Reason = "A44 QA duplicate" }), "reverse bill");
        var rj = await _f.JournalsAsync(o.OrgId, bill3);
        rj.Should().HaveCount(2, Show(rj));
        rj.Sum(j => j.Net(c["DEFAULT_PAYABLE"])).Should().Be(0m, Show(rj));

        // VendorCredit — supplier credit note (CN) on create and debit note (DN) issued: Dr AP ; Cr Purchase
        var sro = await k.ReadySroAsync(vendor, 2m, 25m);
        var cn = await k.SupplierCreditNoteAsync(sro, 50m);
        var cnj = (await _f.JournalsAsync(o.OrgId, cn)).Should().ContainSingle().Subject;
        (cnj.Type, cnj.Balanced).Should().Be(("VENDOR_CREDIT", true), cnj.ToString());
        cnj.Net(c["DEFAULT_PAYABLE"]).Should().Be(50m);
        cnj.Net(c["DEFAULT_PURCHASE"]).Should().Be(-50m);

        var sro2 = await k.ReadySroAsync(vendor, 1m, 30m);
        var dn = await k.DebitNoteAsync(sro2, 30m);
        var dnj = (await _f.JournalsAsync(o.OrgId, dn)).Should().ContainSingle("a debit note is ISSUED on create").Subject;
        dnj.Net(c["DEFAULT_PAYABLE"]).Should().Be(30m, dnj.ToString());

        (await _f.AllJournalsAsync(o.OrgId)).Should().OnlyContain(j => j.Balanced);
        (await _f.NetAsync(o.OrgId, c["DEFAULT_PAYABLE"])).Should().Be(-300m + 50m + 30m,
            "AP: bill 1170 paid; bill2 300 owed again after the bounce; bill3 reversed; CN 50 and DN 30 reduce it");
    }

    [Fact]
    public async Task A_locked_period_never_blocks_the_document_and_the_posting_is_retried()
    {
        var o = await _root.NewOrgAsync("GLL");
        var k = o.K;
        await k.EnableGlAsync();
        var c = await k.DefaultCodesAsync();
        var (_, _, customer, item) = await k.StockedWorldAsync("GLL", price: 100m);

        var period = await k.PeriodOfAsync(Today);
        await k.Ok(k.SetPeriodStatus(period, "Locked"), "lock this month");

        // T-21 (D-9): the invoice issues; its posting waits as PERIOD_CLOSED, nothing in the GL
        var inv = await k.IssuedInvoiceAsync(customer, item, 1m, o.O.Pkr);
        (await k.GetInvoiceAsync(inv.G("uuid"))).S("status").Should().Be("ISSUED", "the source commit never depends on the GL");
        (await _f.JournalsAsync(o.OrgId, inv.G("uuid"))).Should().BeEmpty();
        var row = (await k.PostingRowsAsync(inv.G("uuid"))).Should().ContainSingle().Subject;
        row.TargetError("internal_gl").Should().Contain("PERIOD_CLOSED", J.Short(row));

        // still locked: the retry job leaves it waiting
        await _f.RunGlRetryJobAsync();
        (await _f.JournalsAsync(o.OrgId, inv.G("uuid"))).Should().BeEmpty();

        // reopened: the retry (job, or POST log/{id}/retry) posts it once — due time permitting, the manual retry is immediate
        await k.Ok(k.SetPeriodStatus(period, "Open"), "reopen this month");
        row.TargetStatus("internal_gl").Should().BeOneOf(new[] { "Pending", "Failed" }, J.Short(row));
        await k.Ok(k.RetryPosting(row, "internal_gl"), "retry the posting");
        await _f.RunGlRetryJobAsync();
        var j = (await _f.JournalsAsync(o.OrgId, inv.G("uuid"))).Should().ContainSingle().Subject;
        j.Balanced.Should().BeTrue();
        j.Net(c["DEFAULT_RECEIVABLE"]).Should().Be(inv.D("grandTotal"));
        (await k.PostingRowsAsync(inv.G("uuid"))).Single().TargetStatus("internal_gl").Should().Be("Posted");

        // skip (dismiss) of a failed posting: an unassigned default the accountant will not assign; skip closes it
        await k.Ok(k.SetDefault("DEFAULT_DISCOUNT_RECEIVED", null), "unassign an optional default");
        var tx = Synthetic(SMS.Shared.Finance.Posting.FinDocType.SalesInvoice, 0m, lines: [("DEFAULT_RECEIVABLE", 5m, 0m), ("DEFAULT_DISCOUNT_RECEIVED", 0m, 5m)]);
        await _f.PostDirectAsync(o.OrgId, tx);
        var failed = (await k.PostingRowsAsync(tx.SourceDocumentId)).Single();
        failed.TargetStatus("internal_gl").Should().BeOneOf("Failed", "Pending", "DeadLetter");
        await k.Ok(k.SkipPosting(failed, "internal_gl", "role will never exist"), "skip the failed posting");
        for (var i = 0; i < 3; i++) await _f.RunGlRetryJobAsync();
        (await _f.JournalsAsync(o.OrgId, tx.SourceDocumentId)).Should().BeEmpty();
        (await k.PostingRowsAsync(tx.SourceDocumentId)).Single().TargetStatus("internal_gl").Should().Be("Skipped");
    }

    [Fact]
    public async Task Draft_journals_when_auto_post_is_off_and_nothing_when_the_GL_is_off()
    {
        var o = await _root.NewOrgAsync("GLD");
        var k = o.K;
        await k.EnableGlAsync(autoPost: false);
        var (_, _, customer, item) = await k.StockedWorldAsync("GLD");

        var inv = await k.IssuedInvoiceAsync(customer, item, 1m, o.O.Pkr);
        var j = (await _f.JournalsAsync(o.OrgId, inv.G("uuid"))).Should().ContainSingle().Subject;
        j.Status.Should().Be("Draft", "D-8: AutoPostToGL=false creates Draft journals for review");
        j.Balanced.Should().BeTrue();

        // internal accounting switched off: later documents post nothing to the GL
        await k.Ok(k.PutConfig(false), "turn the GL off");
        var inv2 = await k.IssuedInvoiceAsync(customer, item, 1m, o.O.Pkr);
        (await _f.JournalsAsync(o.OrgId, inv2.G("uuid"))).Should().BeEmpty();

        // an organization that never enabled it: no journals at all after a full sale
        var plain = await _root.NewOrgAsync("GLN");
        var (_, _, pc, pi) = await plain.K.StockedWorldAsync("GLN");
        var pinv = await plain.K.IssuedInvoiceAsync(pc, pi, 1m, plain.O.Pkr);
        await plain.K.RecordPaymentAsync(pc, pinv.D("grandTotal"), "CASH", (pinv.G("uuid"), pinv.D("grandTotal")));
        (await _f.AllJournalsAsync(plain.OrgId)).Should().BeEmpty("D-8: InternalAccountingEnabled defaults to false");
    }
}

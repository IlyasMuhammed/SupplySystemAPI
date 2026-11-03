using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.FinancialPosting;
using SMS.Integration.Tests.GeneralLedger;
using SMS.Integration.Tests.SapAlignment;
using Xunit;
using static SMS.Integration.Tests.ArAp.Ar;

namespace SMS.Integration.Tests.ArAp;

/// <summary>
/// A48 (QA) — AR credit memos and AP debit memos (C7, C14, D-6; TS-CM-001..005, tasks 588–593). Both are the existing
/// documents (a CREDIT_NOTE sales invoice; a supplier DebitNote with origin MEMO) applied to open invoices; the GL effect is
/// the note's own A44 posting (checked with the internal GL on).
/// Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~ArAp.ArApMemoE2ETests</c>.
/// </summary>
public sealed class ArApMemoE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _root;

    public ArApMemoE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _f.ShouldBeOnLocalDb();
        _root = new SapKit(factory, "A48CM");
    }

    private static async Task<Guid> CreditMemoOkAsync(ArOrg o, Partner c, decimal amount, Guid? original = null) =>
        (await o.K.Ok(o.CreditMemo(c.Uuid, amount, original), $"credit memo {amount}")).UuidOf();

    [Fact]
    public async Task TS_CM_001_ar_credit_memo_posts_dr_revenue_cr_receivable()
    {
        var o = await _root.NewArOrgAsync("CM1", gl: true, stock: 20m);
        var inv = await o.IssuedInvoiceAsync(o.Customer, 10m);
        var note = await CreditMemoOkAsync(o, o.Customer, 200m, inv);

        var doc = await o.K.GetInvoiceAsync(note);
        doc.S("status").Should().Be("CREDIT_NOTE");
        Math.Abs(doc.D("grandTotal")).Should().Be(200m);
        doc.G("partnerId").Should().Be(o.Customer.Uuid);

        await _f.RunJobsAsync();
        var codes = await o.K.DefaultCodesAsync();
        var journals = await SapKit.WaitForAsync(() => _f.JournalsAsync(o.OrgId, note), j => j.Count > 0, "the credit note's A44 journal");
        var je = journals.Last();
        je.Balanced.Should().BeTrue(je.ToString());
        je.Net(codes["DEFAULT_RECEIVABLE"]).Should().Be(-200m, $"TS-CM-001: Cr receivable — {je}");
        je.Net(codes["DEFAULT_REVENUE"]).Should().Be(200m, $"Dr revenue — {je}");
    }

    [Fact]
    public async Task TS_CM_002_apply_in_full_clears_the_invoice()
    {
        var o = await _root.NewArOrgAsync("CM2", stock: 20m);
        var inv = await o.IssuedInvoiceAsync(o.Customer, 10m); // 1 000
        var note = await CreditMemoOkAsync(o, o.Customer, 1_000m);
        Rows(await o.K.Ok(o.K.Get($"/api/finance/ar/customers/{o.Customer.Uuid}/open-credits"), "open credits"))
            .Should().Contain(c => c.UuidOf() == note || J.Short(c).Contains(note.ToString()));

        await o.K.Ok(o.ApplyCreditMemo(note, [(inv, 1_000m)]), "apply in full");
        (await o.SalesBalanceAsync(inv)).Should().Be(0m, "TS-CM-002: residual 0");
        (await o.K.GetInvoiceAsync(inv)).S("status").Should().Be("PAID");
        var apps = Rows(await o.K.Ok(o.K.Get($"/api/finance/ar/credit-memo/{note}/applications"), "applications"));
        apps.Should().ContainSingle().Which.D("amount").Should().Be(1_000m);
        (await o.K.Ok(o.CustomerBalance(o.Customer.Uuid), "balance")).D("netBalance").Should().Be(0m);
        (await o.ApplyCreditMemo(note, [(inv, 1m)])).Status.Should().Be(HttpStatusCode.BadRequest, "nothing left on the note");
    }

    [Fact]
    public async Task TS_CM_003_partial_application_reduces_the_residual_by_the_amount_applied()
    {
        var o = await _root.NewArOrgAsync("CM3", stock: 30m);
        var inv = await o.IssuedInvoiceAsync(o.Customer, 10m);   // 1 000
        var note = await CreditMemoOkAsync(o, o.Customer, 300m);
        var noteNo = (await o.K.GetInvoiceAsync(note)).S("invoiceNumber");

        (await o.ApplyCreditMemo(note, [(inv, 400m)])).ShouldRefuse(HttpStatusCode.BadRequest, $"{noteNo} has only 300", "over-apply");
        var other = await o.CustomerAsync("Someone else");
        var otherInv = await o.IssuedInvoiceAsync(other, 1m);
        (await o.ApplyCreditMemo(note, [(otherInv, 50m)])).ShouldRefuse(HttpStatusCode.BadRequest, "belongs to another customer.", "other customer");

        await o.K.Ok(o.ApplyCreditMemo(note, [(inv, 120m)]), "apply 120");
        (await o.SalesBalanceAsync(inv)).Should().Be(880m, "TS-CM-003");
        (await o.K.GetInvoiceAsync(inv)).S("status").Should().Be("PARTIALLY_PAID");
        await o.K.Ok(o.ApplyCreditMemo(note, [(inv, 180m)]), "apply the rest");
        (await o.SalesBalanceAsync(inv)).Should().Be(700m);
        var small = await o.IssuedInvoiceAsync(o.Customer, 1m);  // 100
        var big = await CreditMemoOkAsync(o, o.Customer, 500m);
        var smallNo = (await o.K.GetInvoiceAsync(small)).S("invoiceNumber");
        (await o.ApplyCreditMemo(big, [(small, 150m)])).ShouldRefuse(HttpStatusCode.BadRequest, $"Invoice {smallNo} has only 100", "invoice over-credited");
    }

    [Fact]
    public async Task TS_CM_004_ap_debit_memo_posts_dr_payable_cr_expense()
    {
        var o = await _root.NewArOrgAsync("CM4", gl: true, stock: 1m);
        var memo = await o.K.Ok(o.DebitMemo(o.Vendor.Uuid, 150m), "debit memo");
        var uuid = memo.UuidOf();
        var row = (await _f.QueryAsync("SELECT Origin, DebitAmount FROM finance.debit_notes WHERE UUID = @u", ("@u", uuid))).Single();
        row["Origin"].Should().Be("MEMO");
        Convert.ToDecimal(row["DebitAmount"]).Should().Be(150m);

        await _f.RunJobsAsync();
        var codes = await o.K.DefaultCodesAsync();
        var journals = await SapKit.WaitForAsync(() => _f.JournalsAsync(o.OrgId, uuid), j => j.Count > 0, "the debit memo's A44 journal");
        var je = journals.Last();
        je.Balanced.Should().BeTrue(je.ToString());
        je.Net(codes["DEFAULT_PAYABLE"]).Should().Be(150m, $"TS-CM-004: Dr payable — {je}");
        je.Lines.Where(l => l.Code != codes["DEFAULT_PAYABLE"]).Sum(l => l.Credit - l.Debit).Should().Be(150m, "Cr expense / purchase");
    }

    [Fact]
    public async Task TS_CM_005_apply_debit_memo_reduces_the_vendor_invoice()
    {
        var o = await _root.NewArOrgAsync("CM5", stock: 1m);
        var bill = await o.ApprovedBillAsync(o.Vendor, 1_000m);
        var memo = (await o.K.Ok(o.DebitMemo(o.Vendor.Uuid, 150m), "debit memo")).UuidOf();
        await o.K.Ok(o.ApplyDebitMemo(memo, [(bill, 150m)]), "apply debit memo");

        (await o.K.Ok(o.VendorBalance(o.Vendor.Uuid), "vendor balance")).D("totalOpen").Should().Be(850m, "TS-CM-005");
        (await o.ScheduleOfAsync(bill)).D("amountRemaining").Should().Be(850m, "the schedule follows the invoice");
        Rows(await o.K.Ok(o.K.Get($"/api/finance/ap/debit-memo/{memo}/applications"), "applications"))
            .Should().ContainSingle(a => a.G("invoiceUuid") == bill && a.D("amount") == 150m);

        var other = await o.VendorAsync("Other vendor");
        var otherBill = await o.ApprovedBillAsync(other, 500m);
        var memo2 = (await o.K.Ok(o.DebitMemo(o.Vendor.Uuid, 50m), "second memo")).UuidOf();
        (await o.ApplyDebitMemo(memo2, [(otherBill, 50m)])).Status.Should().Be(HttpStatusCode.BadRequest, "another vendor's invoice");
    }

    [Fact]
    public async Task Debit_memo_applies_partly_across_invoices_per_contract()
    {
        // API-CONTRACT §9: applications [{ invoiceUuid, amount }], texts mirror §4 — a note can be split.
        var o = await _root.NewArOrgAsync("CM6", stock: 1m);
        var b1 = await o.ApprovedBillAsync(o.Vendor, 400m);
        var b2 = await o.ApprovedBillAsync(o.Vendor, 400m);
        var memo = (await o.K.Ok(o.DebitMemo(o.Vendor.Uuid, 300m), "debit memo")).UuidOf();
        var r = await o.ApplyDebitMemo(memo, [(b1, 100m), (b2, 200m)]);
        r.Status.Should().Be(HttpStatusCode.OK, $"contract §9 — {r}");
        (await o.K.Ok(o.VendorBalance(o.Vendor.Uuid), "vendor balance")).D("totalOpen").Should().Be(500m);
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.GeneralLedger;
using SMS.Integration.Tests.MultiCurrency;
using SMS.Integration.Tests.SapAlignment;
using Xunit;
using static SMS.Integration.Tests.ArAp.Ar;

namespace SMS.Integration.Tests.ArAp;

/// <summary>
/// A48 (QA) — customer statements and vendor reconciliation (C4, C13, D-11, D-13; TS-ST-001..007, BR-ST-001..005).
/// Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~ArAp.ArStatementE2ETests</c>.
/// </summary>
public sealed class ArStatementE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _root;

    public ArStatementE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _f.ShouldBeOnLocalDb();
        _f.Mail.Accept = true;
        _root = new SapKit(factory, "A48ST");
    }

    private static JsonElement Section(JsonElement st, string ccy = "PKR") => st.A("sections").Single(s => s.S("currencyCode") == ccy);

    [Fact]
    public async Task TS_ST_001_activity_statement_ten_transactions_with_running_balance()
    {
        var o = await _root.NewArOrgAsync("ST1", stock: 20m);
        for (var i = 0; i < 5; i++)
        {
            var inv = await o.IssuedInvoiceAsync(o.Customer, 1m);
            await o.PayCustomerAsync(o.Customer, 20m, (inv, 20m));
        }
        var st = await o.K.Ok(o.Statement(o.Customer.Uuid, Today.AddDays(-30), Today), "activity statement");
        var s = Section(st);
        var lines = s.A("lines");
        lines.Should().HaveCount(10, "TS-ST-001: 5 invoices + 5 payments");
        var running = s.D("openingBalance");
        foreach (var l in lines)
        {
            running += (l.ND("debit") ?? 0m) - (l.ND("credit") ?? 0m);
            l.D("runningBalance").Should().Be(running, "BR-ST-001: running balance");
        }
        s.D("closingBalance").Should().Be(400m, "BR-ST-004: 0 + 500 − 100");
        s.D("totalDebits").Should().Be(500m);
        s.D("totalCredits").Should().Be(100m);
    }

    [Fact]
    public async Task TS_ST_002_003_open_items_and_opening_balance()
    {
        var o = await _root.NewArOrgAsync("ST2", stock: 20m);
        var old = await o.IssuedInvoiceAsync(o.Customer, 1m);   // 100, moved to 60 days ago
        await o.BackdateSalesInvoiceAsync(old, 30);
        var paid = await o.IssuedInvoiceAsync(o.Customer, 3m);  // 300, paid
        await o.PayCustomerAsync(o.Customer, 300m, (paid, 300m));
        var open = await o.IssuedInvoiceAsync(o.Customer, 2m);  // 200

        var act = Section(await o.K.Ok(o.Statement(o.Customer.Uuid, Today.AddDays(-30), Today), "activity"));
        act.D("openingBalance").Should().Be(100m, "TS-ST-003 / BR-ST-003: everything before the from date");
        act.D("closingBalance").Should().Be(300m, "BR-ST-004: 100 + 500 − 300");

        var oi = await o.K.Ok(o.Statement(o.Customer.Uuid, Today.AddDays(-30), Today, "OpenItems"), "open items statement");
        var refs = Section(oi).A("lines").Select(l => l.S("entryNumber")).ToList();
        var paidNo = (await o.K.GetInvoiceAsync(paid)).S("invoiceNumber");
        refs.Should().NotContain(paidNo, "TS-ST-002 / BR-ST-002: paid invoices are not open");
        refs.Should().Contain((await o.K.GetInvoiceAsync(open)).S("invoiceNumber"));
        refs.Should().Contain((await o.K.GetInvoiceAsync(old)).S("invoiceNumber"), "open items ignore the from date");
        Section(oi).D("closingBalance").Should().Be(300m);
    }

    [Fact]
    public async Task TS_ST_004_pdf_is_generated_on_request()
    {
        var o = await _root.NewArOrgAsync("ST4", stock: 5m);
        await o.IssuedInvoiceAsync(o.Customer, 1m);
        using var resp = await o.K.Admin.GetAsync($"/api/finance/ar/customers/{o.Customer.Uuid}/statement?from={Iso(Today.AddDays(-30))}&to={Iso(Today)}&type=Activity&format=pdf");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("%PDF", "TS-ST-004: a valid PDF");
        Rows(await o.K.Ok(o.K.Get($"/api/finance/statements/log?role=Customer&partnerUuid={o.Customer.Uuid}"), "statement log"))
            .Should().Contain(l => l.S("format") == "pdf" || l.S("format") == "PDF", "D-11: a log row per generation");
    }

    [Fact]
    public async Task TS_ST_005_batch_only_for_balances_over_the_minimum()
    {
        var o = await _root.NewArOrgAsync("ST5", stock: 30m);
        var big = await o.CustomerAsync("Big balance");
        var small = await o.CustomerAsync("Small balance");
        await o.IssuedInvoiceAsync(big, 15m);   // 1 500
        await o.IssuedInvoiceAsync(small, 3m);  // 300
        var r = await o.K.Ok(o.BatchStatements(Today.AddDays(-30), Today, 1_000m), "batch statements");
        var items = r.A("items");
        items.Should().Contain(i => i.G("partnerId") == big.Uuid, "TS-ST-005");
        items.Should().NotContain(i => i.G("partnerId") == small.Uuid && (i.S("outcome") == "GENERATED" || i.S("outcome") == "EMAILED"),
            "BR-ST-005: below the minimum");
        r.I("generated").Should().Be(1);
    }

    [Fact]
    public async Task TS_ST_006_email_statement_is_sent_and_logged()
    {
        var o = await _root.NewArOrgAsync("ST6", stock: 5m);
        await o.IssuedInvoiceAsync(o.Customer, 2m);
        var email = await o.SetEmailAsync(o.Customer);
        var sent = await o.K.Ok(o.EmailStatement(o.Customer.Uuid, Today.AddDays(-30), Today), "email statement");
        sent.S("emailedTo").Should().Be(email);
        var mail = _f.Mail.To(email);
        mail.Should().ContainSingle("TS-ST-006: one e-mail");
        mail[0].Attachments.Should().ContainSingle(a => a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase), "the statement PDF is attached");
        Rows(await o.K.Ok(o.K.Get($"/api/finance/statements/log?role=Customer&partnerUuid={o.Customer.Uuid}"), "statement log"))
            .Should().Contain(l => l.S("emailStatus") == "Sent" && l.S("emailedTo") == email);

        var noMail = await o.CustomerAsync("No address");
        await o.IssuedInvoiceAsync(noMail, 1m);
        (await o.EmailStatement(noMail.Uuid, Today.AddDays(-30), Today)).Status.Should().Be(HttpStatusCode.BadRequest, "nowhere to send it");
    }

    [Fact]
    public async Task TS_ST_007_vendor_reconciliation_lists_the_differences()
    {
        var o = await _root.NewArOrgAsync("ST7", stock: 1m);
        var b1 = await o.ApprovedBillAsync(o.Vendor, 1_000m);
        var b2 = await o.ApprovedBillAsync(o.Vendor, 500m);
        var n1 = (await o.SupplierInvoiceDetailAsync(b1)).S("supplierInvoiceNo")!;
        var n2 = (await o.SupplierInvoiceDetailAsync(b2)).S("supplierInvoiceNo")!;
        var claimed = new object[]
        {
            new { reference = n1, date = Iso(Today), amount = 1_000m },
            new { reference = n2, date = Iso(Today), amount = 450m },
            new { reference = "VEND-ONLY-1", date = Iso(Today), amount = 200m }
        };
        var r = await o.K.Ok(o.VendorReconciliation(o.Vendor.Uuid, 1_650m, claimed), "vendor reconciliation");
        r.D("smsBalance").Should().Be(1_500m);
        r.D("difference").Should().Be(-150m);
        r.B("isReconciled").Should().BeFalse();
        var diffs = r.A("differences");
        diffs.Should().Contain(d => d.S("type") == "AmountDifference" && d.S("reference") == n2, "TS-ST-007: 500 vs 450");
        diffs.Should().Contain(d => d.S("type") == "InVendorNotSMS" && d.S("reference") == "VEND-ONLY-1");
        diffs.Should().NotContain(d => d.S("reference") == n1, "the matching item is no difference");
    }

    [Fact]
    public async Task Statement_has_one_section_per_currency()
    {
        var o = await _root.NewArOrgAsync("STFX", stock: 20m);
        await o.IssuedInvoiceAsync(o.Customer, 1m);            // 100 PKR, before any USD price exists
        var usd = await o.K.UsdAsync();
        await o.K.CreateRateAsync("USD", "PKR", 280m, Today.AddDays(-400));
        await o.K.SellingPriceAsync(o.Item, 10m, usd);
        var so = await o.K.CreateSaleOrderAsync(o.Customer, usd, SapKit.Line(o.Item, 2m));
        await o.K.ConfirmSaleOrderAsync(so);
        await o.K.InvoiceAndIssueAsync(so);

        var st = await o.K.Ok(o.Statement(o.Customer.Uuid, Today.AddDays(-30), Today), "statement");
        st.A("sections").Select(s => s.S("currencyCode")).Should().BeEquivalentTo(["PKR", "USD"], "D-13");
        Section(st, "USD").D("closingBalance").Should().Be(20m);
        Section(st, "PKR").D("closingBalance").Should().Be(100m);
    }
}

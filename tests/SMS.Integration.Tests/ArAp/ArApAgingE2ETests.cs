using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.FinancialPosting;
using SMS.Integration.Tests.GeneralLedger;
using SMS.Integration.Tests.MultiCurrency;
using SMS.Integration.Tests.SapAlignment;
using Xunit;
using static SMS.Integration.Tests.ArAp.Ar;

namespace SMS.Integration.Tests.ArAp;

/// <summary>
/// A48 (QA) — aging, DSO/DPO and AR/AP inquiry against hand-computed totals (C3, C6, C8, C12, D-1, D-12, D-13;
/// TS-AG-001..008, BR-AG-001..006). Invoices are made through the real flows and moved in time in SQL; every expected figure
/// is worked out in the test from the amounts it created (item 100, no tax).
/// Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~ArAp.ArApAgingE2ETests</c>.
/// </summary>
public sealed class ArApAgingE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _root;

    public ArApAgingE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _f.ShouldBeOnLocalDb();
        _root = new SapKit(factory, "A48AG");
    }

    private static readonly string[] DefaultLabels = ["Current", "1-30", "31-60", "61-90", "91-120", "120+"];

    private static async Task<Guid> SoldAsync(ArOrg o, Partner c, decimal units, int dueDaysAgo)
    {
        var inv = await o.IssuedInvoiceAsync(c, units);
        await o.BackdateSalesInvoiceAsync(inv, dueDaysAgo);
        return inv;
    }

    [Fact]
    public async Task TS_AG_001_005_006_007_008_receivables_default_buckets_against_hand_totals()
    {
        var o = await _root.NewArOrgAsync("AG1", stock: 60m);
        var a = o.Customer;
        var notDue = await SoldAsync(o, a, 1m, -5);    // Current (D-12: not yet due)
        await SoldAsync(o, a, 1m, 10);                 // 1-30
        await SoldAsync(o, a, 1m, 30);                 // 1-30 (boundary)
        var b31 = await SoldAsync(o, a, 1m, 45);       // 31-60
        await SoldAsync(o, a, 1m, 75);                 // 61-90
        await SoldAsync(o, a, 1m, 100);                // 91-120
        await SoldAsync(o, a, 1m, 150);                // 120+
        var partial = await SoldAsync(o, a, 2m, 12);   // 200, 80 paid → 120 in 1-30 (TS-AG-006)
        await o.PayCustomerAsync(a, 80m, (partial, 80m));
        var cancelled = await SoldAsync(o, a, 3m, 20); // cancelled → excluded (TS-AG-007)
        await o.K.Ok(o.K.Post($"/api/sales-invoices/{cancelled}/cancel", new { Reason = "A48 QA" }), "cancel invoice");
        var so = await o.DraftSoAsync(a, 4m);          // a draft invoice → excluded (TS-AG-008)
        await o.K.ConfirmSaleOrderAsync(so);
        var draft = (await o.K.RaiseInvoiceAsync(await o.K.DeliverAsync(so))).G("invoiceUuid");

        decimal[] expected = [100m, 320m, 100m, 100m, 100m, 100m];
        var aging = await o.K.Ok(o.ArAging($"customerUuid={a.Uuid}&includeDetails=true"), "AR aging");
        aging.S("basis").Should().Be("DueDate", "D-12 default");
        aging.A("bucketLabels").Select(x => x.GetString()).Should().Equal(DefaultLabels, "BR-AG-005");
        var line = aging.LineOf(a.Uuid);
        line.ValueKind.Should().Be(JsonValueKind.Object, J.Short(aging));
        line.Buckets().Should().Equal(expected, $"TS-AG-001 / BR-AG-002 — {J.Short(line)}");
        line.D("totalOutstanding").Should().Be(820m, "7 × 100 + the 120 residual");
        line.D("totalOverdue").Should().Be(720m, "everything but the not-yet-due 100");
        line.I("openItemCount").Should().Be(8);

        var details = line.A("details");
        details.Select(d => d.G("documentUuid")).Should().NotContain([cancelled, draft], "TS-AG-007/008, BR-AG-001");
        details.Single(d => d.G("documentUuid") == partial).D("openAmount").Should().Be(120m, "TS-AG-006: the residual");
        details.Single(d => d.G("documentUuid") == notDue).S("bucketLabel").Should().Be("Current");
        details.Single(d => d.G("documentUuid") == b31).I("daysOverdue").Should().Be(45);

        // TS-AG-005 — drill down to one bucket (31-60).
        var drill = await o.K.Ok(o.K.Get($"/api/finance/ar/aging/details?customerUuid={a.Uuid}&bucketIndex=2&page=1&pageSize=50"), "aging drill-down");
        Rows(drill).Select(d => d.G("documentUuid")).Should().Equal([b31], "only the 31-60 invoice");

        // The summary agrees with the aging.
        var sum = await o.K.Ok(o.ArSummary(), "AR summary");
        sum.D("totalOutstanding").Should().Be(820m);
        sum.D("totalOverdue").Should().Be(720m);
        sum.D("totalCurrent").Should().Be(100m);
        sum.A("agingBuckets").Select(x => x.GetDecimal()).Should().Equal(expected);

        // C6 inquiry.
        var bal = await o.K.Ok(o.CustomerBalance(a.Uuid), "customer balance");
        bal.D("totalOpen").Should().Be(820m);
        bal.D("netBalance").Should().Be(820m);
        bal.I("openItemCount").Should().Be(8);
        Rows(await o.K.Ok(o.CustomerOpenItems(a.Uuid), "open items")).Where(i => !i.B("isCredit")).Sum(i => i.D("openAmount")).Should().Be(820m);
        var tx = Rows(await o.K.Ok(o.CustomerTransactions(a.Uuid), "transactions"));
        tx.Sum(t => t.D("debit") - t.D("credit")).Should().Be(820m, "ledger debits − credits = the balance (cancel reverses its invoice)");

        // Document-date basis moves items by invoice date (due − 30): the not-due one is 25 days old → 1-30.
        var byDoc = await o.K.Ok(o.ArAging($"customerUuid={a.Uuid}&basis=DocumentDate"), "AR aging by document date");
        byDoc.LineOf(a.Uuid).Buckets()[0].Should().Be(0m, "nothing is 'current' by document age");
        byDoc.LineOf(a.Uuid).D("totalOutstanding").Should().Be(820m);
    }

    [Fact]
    public async Task TS_AG_002_payables_custom_buckets_and_validation()
    {
        var o = await _root.NewArOrgAsync("AG2", stock: 1m);
        var due = new (int DaysAgo, decimal Amount, int Bucket)[] { (-3, 100m, 0), (10, 200m, 1), (20, 300m, 2), (40, 400m, 3), (50, 500m, 4), (90, 600m, 5) };
        foreach (var (daysAgo, amount, _) in due)
        {
            var bill = await o.ApprovedBillAsync(o.Vendor, amount);
            await o.BackdateSupplierInvoiceAsync(bill, daysAgo);
        }
        var aging = await o.K.Ok(o.ApAging($"vendorUuid={o.Vendor.Uuid}&buckets=15,30,45,60"), "AP aging custom");
        aging.A("bucketLabels").Select(x => x.GetString()).Should().Equal("Current", "1-15", "16-30", "31-45", "46-60", "60+");
        aging.LineOf(o.Vendor.Uuid).Buckets().Should().Equal(due.Select(d => d.Amount), "TS-AG-002 / BR-AG-006");
        aging.LineOf(o.Vendor.Uuid).D("totalOutstanding").Should().Be(2_100m);

        (await o.ApAging("buckets=30")).ShouldRefuse(HttpStatusCode.BadRequest, "At least 3 buckets are needed (2 boundaries).", "one boundary");
        (await o.ApAging("buckets=60,30")).ShouldRefuse(HttpStatusCode.BadRequest, "Bucket boundaries must be positive and ascending.", "descending");
        (await o.ApAging("buckets=-5,15")).ShouldRefuse(HttpStatusCode.BadRequest, "Bucket boundaries must be positive and ascending.", "negative");
        var fsdSpelling = await o.K.Ok(o.ApAging($"vendorUuid={o.Vendor.Uuid}&buckets=0,15,30,45,60"), "FSD spelling [0,15,30,45,60]");
        fsdSpelling.LineOf(o.Vendor.Uuid).Buckets().Should().Equal(due.Select(d => d.Amount), "a leading 0 is the implicit Current boundary");

        var vb = await o.K.Ok(o.VendorBalance(o.Vendor.Uuid), "vendor balance");
        vb.D("totalOpen").Should().Be(2_100m);
        vb.D("totalOverdue").Should().Be(2_000m);
        Rows(await o.K.Ok(o.VendorOpenItems(o.Vendor.Uuid), "vendor open items")).Count(i => !i.B("isCredit")).Should().Be(6);
        Rows(await o.K.Ok(o.VendorTransactions(o.Vendor.Uuid), "vendor transactions")).Should().HaveCountGreaterThanOrEqualTo(6);
        var ap = await o.K.Ok(o.ApSummary(), "AP summary");
        ap.D("totalOutstanding").Should().Be(2_100m);
        ap.D("totalCurrent").Should().Be(100m);
    }

    [Fact]
    public async Task TS_AG_003_004_dso_and_dpo_over_90_days()
    {
        var o = await _root.NewArOrgAsync("AG3", stock: 30m);
        // AR: 1 000 invoiced 10 days ago (300 paid), 500 invoiced 200 days ago, still open.
        var recent = await SoldAsync(o, o.Customer, 10m, -20); // invoice date = due − 30 = 10 days ago
        await o.PayCustomerAsync(o.Customer, 300m, (recent, 300m));
        await SoldAsync(o, o.Customer, 5m, 170);              // invoice date 200 days ago
        var dso = await o.K.Ok(o.Dso(90), "DSO");
        dso.D("balance").Should().Be(1_200m);
        dso.D("periodAmount").Should().Be(1_000m, "credit sales in the last 90 days");
        dso.D("days").Should().Be(108.0m, "TS-AG-003 / BR-AG-003: 1 200 / 1 000 × 90");

        // AP: 2 000 billed 10 days ago (500 paid), 1 000 billed 200 days ago.
        var b1 = await o.ApprovedBillAsync(o.Vendor, 2_000m, invoiceDate: Today.AddDays(-10));
        await o.K.PostedSupplierPaymentAsync(o.Vendor, b1, 500m);
        var b2 = await o.ApprovedBillAsync(o.Vendor, 1_000m);
        await o.BackdateSupplierInvoiceAsync(b2, 170);
        // The stock-up GRN raised an automatic bill for the org's vendor; DPO counts every approved bill, so read it from the aging.
        var apTotal = (await o.K.Ok(o.ApAging(), "AP aging")).P("totals").D("grandTotal");
        var dpo = await o.K.Ok(o.Dpo(90), "DPO");
        dpo.D("balance").Should().Be(apTotal);
        dpo.D("periodAmount").Should().BeGreaterThanOrEqualTo(2_000m);
        dpo.D("days").Should().Be(Math.Round(dpo.D("balance") / dpo.D("periodAmount") * 90m, 1), "TS-AG-004 / BR-AG-004, D-12");
    }

    [Fact]
    public async Task Multi_currency_totals_are_in_base_and_details_in_document_currency()
    {
        var o = await _root.NewArOrgAsync("AGFX", stock: 30m);
        var c = await o.CustomerAsync("Dollar customer");
        var pkrInv = await SoldAsync(o, c, 2m, 45);        // 200 PKR, before any USD price exists
        var usd = await o.K.UsdAsync();
        await o.K.CreateRateAsync("USD", "PKR", 280m, Today.AddDays(-400));
        await o.K.SellingPriceAsync(o.Item, 10m, usd);
        var so = await o.K.CreateSaleOrderAsync(c, usd, SapKit.Line(o.Item, 3m)); // 30 USD
        await o.K.ConfirmSaleOrderAsync(so);
        var invUsd = (await o.K.InvoiceAndIssueAsync(so)).UuidOf();
        await o.BackdateSalesInvoiceAsync(invUsd, 45);
        (await o.K.GetInvoiceAsync(invUsd)).D("grandTotal").Should().Be(30m, "3 × 10 USD");
        (await o.K.GetInvoiceAsync(pkrInv)).D("grandTotal").Should().Be(200m);

        var aging = await o.K.Ok(o.ArAging($"customerUuid={c.Uuid}&includeDetails=true"), "AR aging");
        aging.S("baseCurrencyCode").Should().Be("PKR");
        aging.LineOf(c.Uuid).Buckets()[2].Should().Be(30m * 280m + 200m, "D-13: 30 USD × 280 + 200 PKR");
        var usdRow = aging.LineOf(c.Uuid).A("details").Single(d => d.G("documentUuid") == invUsd);
        usdRow.S("currencyCode").Should().Be("USD");
        usdRow.D("openAmount").Should().Be(30m);
        usdRow.D("openAmountBase").Should().Be(8_400m);
        aging.A("byCurrency").Single(x => x.S("currencyCode") == "USD").D("total").Should().Be(30m);

        (await o.ExposureAsync(c.Uuid)).D("openInvoicesAmount").Should().Be(8_600m, "exposure is in base too");
    }

    [Fact]
    public async Task Existing_aging_endpoints_keep_working()
    {
        var o = await _root.NewArOrgAsync("AGOLD", stock: 10m);
        var inv = await SoldAsync(o, o.Customer, 1m, 10);
        await o.ApprovedBillAsync(o.Vendor, 100m);
        (await o.K.Get("/api/reports/sales/aging-receivables")).Status.Should().Be(HttpStatusCode.OK, "A-6: unchanged");
        (await o.K.Get($"/api/suppliers/{o.Vendor.Uuid}/aging")).Status.Should().Be(HttpStatusCode.OK);
        (await o.K.Get("/api/reports/supplier-aging")).Status.Should().Be(HttpStatusCode.OK);
        (await o.K.Ok(o.ArAging(), "new AR aging")).P("totals").D("grandTotal").Should().Be(100m);
        _ = inv;
    }
}

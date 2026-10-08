using FluentAssertions;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using Xunit;
using static SMS.Integration.Tests.MultiCurrency.A35;

namespace SMS.Integration.Tests.MultiCurrency;

/// <summary>
/// A35-P4-05 (QA) — FSD Appendix A end to end on the real host (LocalDB), every step through the HTTP API:
/// <list type="bullet">
/// <item><b>A</b> — sale base PKR, purchase base USD: customer "Al Rashid Trading" (default AED) → inquiry (AED auto-filled) →
/// quotation from the inquiry (AED inherited) → send (76.30 locked, PKR base) → accept → SO (AED inherited, not locked) →
/// confirm (re-locked 76.30, 457,800 PKR) → delivery → sales invoice issued (76.30, base 457,800) → customer payment
/// 7 days later at 76.45 → realized gain row (+900 on AED 6,000) on the gain account; the margin cross-conversion
/// $42,500 → PKR 11,817,125.</item>
/// <item><b>B</b> — a brand-new single-currency org (everything PKR, never configured): domestic SO / PO / invoice / payment
/// at rate 1 with no rate rows in the org beyond the SYSTEM one and no exchange-difference rows.</item>
/// <item><b>C</b> — a PO in USD with purchase base USD: rate 1, base = amounts (it also stocks scenario A).</item>
/// </list>
/// <para>Run alone: <c>dotnet test &lt;out&gt;\SMS.Integration.Tests.dll --filter FullyQualifiedName~MultiCurrencyAppendixAE2ETests</c>.</para>
/// </summary>
public sealed class MultiCurrencyAppendixAE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapKit _root;

    public MultiCurrencyAppendixAE2ETests(SapWebApplicationFactory factory) => _root = new SapKit(factory, "MCA");

    [Fact]
    public async Task Scenario_A_and_C_buy_in_USD_sell_in_AED_from_inquiry_to_realized_gain()
    {
        var o = await _root.NewOrgAsync("APXA");
        var k = o.K;
        var pkr = await k.PkrAsync();
        var usd = await k.UsdAsync();
        var aed = await k.AedAsync();
        var settings = await k.SetBasesAsync(pkr, usd, pkr);
        settings.S("rateCurrencyCode").Should().Be("PKR", "D-2: the rate currency stays PKR");
        await k.AddRateAsync(aed, 76.30m, Today.AddDays(-30));
        await k.AddRateAsync(usd, 278.05m, Today.AddDays(-30));

        // ── C: PO in USD, purchase base USD → rate 1, no conversion (this PO also stocks the sale) ──
        await k.CreateApproverPlaceholdersAsync();
        var wh = await k.CreateWarehouseAsync();
        var supplier = await k.VendorAsync("US Supplier", usd);
        var item = await k.CreateProductAsync("Al Rashid Widget", 85m, null);
        await k.SellingPriceAsync(item, 120m, aed);

        var po = await k.CreatePoAsync(supplier, wh, null, (item, 500m, 85m));
        (await k.PoAsync(po)).NG("currencyId").Should().Be(usd, "the supplier's default purchase currency");
        await k.SubmitApproveAndSendPoAsync(po);
        var approvedPo = await k.PoAsync(po);
        approvedPo.ND("exchangeRate").Should().Be(1m, "USD→USD: purchase base = USD");
        approvedPo.NG("baseCurrencyId").Should().Be(usd);
        approvedPo.D("totalAmount").Should().Be(42_500m);
        approvedPo.ND("totalAmountBase").Should().Be(42_500m);
        var poLine = approvedPo.A("lines").Single();
        poLine.ND("unitPriceBase").Should().Be(85m);
        poLine.ND("lineTotalBase").Should().Be(42_500m);
        await k.ReceiveAllAsync(po, wh);

        // ── A: customer (default AED) → inquiry ─────────────────────────────────────
        var customer = await k.CustomerAsync("Al Rashid Trading", aed);
        var inquiry = await k.ReviewedInquiryAsync(customer, item, 50m);
        var inq = await k.Ok(k.Get($"/api/sale-inquiries/{inquiry}"), "read inquiry");
        inq.NG("currencyId").Should().Be(aed, "T-C4-01: the customer's default sale currency");
        inq.S("currencyCode").Should().Be("AED");

        // ── quotation from the inquiry: AED inherited; send locks 76.30 ─────────────
        var q = (await k.Ok(k.Post($"/api/sale-inquiries/{inquiry}/create-quotation", new
        {
            ValidFrom = Day(Today), ValidTo = Day(Today.AddDays(30)), PaymentTerms = "30 days"
        }), "create quotation from the inquiry")).GetGuid();
        var draftQ = await k.QuotationReadAsync(q);
        draftQ.G("currencyId").Should().Be(aed, "D-14: inquiry → quotation");
        draftQ.ND("exchangeRate").Should().BeNull();
        var ql = draftQ.A("lines").Single();
        ql.D("unitPrice").Should().Be(120m);
        ql.D("lineTotal").Should().Be(6_000m);

        await k.Ok(k.TrySendQuotationAsync(q), "send quotation");
        var sentQ = await k.QuotationReadAsync(q);
        sentQ.ND("exchangeRate").Should().Be(76.30m);
        sentQ.NG("baseCurrencyId").Should().Be(pkr, "the SALE base, not the purchase base");
        sentQ.A("lines").Single().ND("unitPriceBase").Should().Be(9_156m);
        sentQ.A("lines").Single().ND("lineTotalBase").Should().Be(457_800m);

        // ── SO from the quotation: AED inherited, re-locked at confirm ──────────────
        var so = await k.AcceptAndConvertAsync(q);
        var draftSo = await k.GetSaleOrderAsync(so);
        draftSo.G("currencyId").Should().Be(aed);
        draftSo.ND("exchangeRate").Should().BeNull();
        var confirmed = await k.ConfirmAsync(so);
        confirmed.ND("exchangeRate").Should().Be(76.30m);
        confirmed.NG("baseCurrencyId").Should().Be(pkr);
        confirmed.S("baseCurrencyCode").Should().Be("PKR");
        var sol = confirmed.A("lines").Single();
        sol.D("lineTotal").Should().Be(6_000m);
        sol.ND("unitPriceBase").Should().Be(9_156m, "120 × 76.30");
        sol.ND("lineTotalBase").Should().Be(457_800m);
        confirmed.ND("grandTotalBase").Should().Be(457_800m);

        // ── sales invoice: AED from the SO, locked at ISSUE ─────────────────────────
        var inv = await k.InvoiceAndIssueAsync(so);
        inv.S("currencyCode").Should().Be("AED");
        inv.NG("currencyId").Should().Be(aed);
        inv.D("grandTotal").Should().Be(6_000m);
        inv.ND("exchangeRate").Should().Be(76.30m);
        inv.S("baseCurrencyCode").Should().Be("PKR");
        inv.ND("baseGrandTotal").Should().Be(457_800m);

        // ── payment 7 days later at 76.45 → realized gain ───────────────────────────
        await k.AddRateAsync(aed, 76.45m, Today.AddDays(7));
        var pay = await k.PayCustomerAsync(customer, 6_000m, "AED", Today.AddDays(7), (inv.G("uuid"), 6_000m));
        pay.ND("exchangeRate").Should().Be(76.45m);
        pay.ND("amountBase").Should().Be(458_700m);
        pay.ND("exchangeDifference").Should().Be(900m, "6,000 × (76.45 − 76.30)");

        var rows = await k.DifferencesOfAsync(inv.G("uuid"), "REALIZED");
        rows.Should().ContainSingle();
        rows[0].D("differenceBase").Should().Be(900m);
        rows[0].S("accountCode").Should().Be("7110");
        rows[0].D("bookedRate").Should().Be(76.30m);
        rows[0].D("settlementRate").Should().Be(76.45m);
        (await k.GetInvoiceAsync(inv.G("uuid"))).S("status").Should().Be("PAID");
        (await k.GetInvoiceAsync(inv.G("uuid"))).ND("exchangeRate").Should().Be(76.30m, "the invoice keeps its lock");

        // ── margin cross-conversion (Appendix A): cost in USD → PKR ─────────────────
        var cost = await k.Ok(k.TryConvertAsync(42_500m, usd, pkr, Today), "cost to PKR");
        cost.D("convertedAmount").Should().Be(11_817_125m);
        cost.D("rateUsed").Should().Be(278.05m);
    }

    [Fact]
    public async Task Scenario_B_single_currency_org_runs_at_rate_1_with_no_rates_and_no_differences()
    {
        var o = await _root.NewOrgAsync("APXB");
        var k = o.K;

        // A brand-new org: nothing configured by hand.
        var settings = await k.SettingsAsync();
        settings.S("saleBaseCurrencyCode").Should().Be("PKR");
        settings.S("purchaseBaseCurrencyCode").Should().Be("PKR");
        settings.S("serviceBaseCurrencyCode").Should().Be("PKR");
        settings.S("rateCurrencyCode").Should().Be("PKR");
        var pkr = settings.G("saleBaseCurrencyId");
        (await k.OrgCurrenciesAsync()).Should().Contain(c => c.S("code") == "PKR" && c.B("isActive"), "P1-14: provisioned with PKR");
        var rates = (await k.Ok(k.Get("/api/currency-rates"), "list rates")).Items();
        rates.Should().OnlyContain(r => r.S("source") == "SYSTEM", "no manual rate exists anywhere in this org");

        await k.CreateApproverPlaceholdersAsync();
        var wh = await k.CreateWarehouseAsync();
        var vendor = await k.VendorAsync("Local Vendor", null);
        var item = await k.CreateProductAsync("Domestic Widget", 6_000m, 9_200m);
        var po = await k.CreatePoAsync(vendor, wh, null, (item, 30m, 1_000m)); // below the GM-approval threshold (role 1 = System Admin)
        await k.SubmitApproveAndSendPoAsync(po);
        var approvedPo = await k.PoAsync(po);
        approvedPo.NG("currencyId").Should().Be(pkr);
        approvedPo.ND("exchangeRate").Should().Be(1m);
        approvedPo.ND("totalAmountBase").Should().Be(30_000m);
        await k.ReceiveAllAsync(po, wh);

        var customer = await k.CustomerAsync("Lahore Customer", null);
        var so = await k.SaleOrderAsync(customer, null, Line(item, 25m));
        (await k.GetSaleOrderAsync(so)).G("currencyId").Should().Be(pkr, "the sale base when the customer has no default");
        var confirmed = await k.ConfirmAsync(so);
        confirmed.ND("exchangeRate").Should().Be(1m);
        var line = confirmed.A("lines").Single();
        line.ND("unitPriceBase").Should().Be(9_200m);
        line.ND("lineTotalBase").Should().Be(230_000m);

        var inv = await k.InvoiceAndIssueAsync(so);
        inv.ND("exchangeRate").Should().Be(1m);
        inv.ND("baseGrandTotal").Should().Be(inv.D("grandTotal"));
        var pay = await k.PayCustomerAsync(customer, inv.D("grandTotal"), "PKR", Today, (inv.G("uuid"), inv.D("grandTotal")));
        pay.ND("exchangeRate").Should().Be(1m);
        (pay.ND("exchangeDifference") ?? 0m).Should().Be(0m);

        // The supplier side too, still with no currency setup: a bill in PKR approves at rate 1 and is paid.
        var bill = await k.SupplierInvoiceAsync(vendor, "PKR", null, 5_000m);
        await k.Ok(k.TryApproveSupplierInvoiceAsync(bill), "approve the PKR bill in a never-configured org");
        var billRead = await k.SupplierInvoiceReadAsync(bill);
        billRead.ND("exchangeRate").Should().Be(1m);
        billRead.NG("baseCurrencyId").Should().Be(pkr);
        billRead.ND("baseTotalAmount").Should().Be(5_000m);
        var supPay = await k.PaySupplierAsync(vendor, bill, 5_000m, "PKR", Today);
        supPay.ND("exchangeRate").Should().Be(1m);
        (supPay.ND("exchangeDifference") ?? 0m).Should().Be(0m);

        (await k.DifferencesAsync("")).Should().BeEmpty("a single-currency org never has an exchange difference");
        var reval = await k.Ok(k.TryRevalueAsync(Today), "revaluation in a single-currency org");
        reval.I("rowsWritten").Should().Be(0);
        (await k.DifferencesAsync("")).Should().BeEmpty();
    }
}

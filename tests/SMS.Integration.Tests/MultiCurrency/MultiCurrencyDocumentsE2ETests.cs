using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using SMS.Shared.Common;
using Xunit;
using static SMS.Integration.Tests.MultiCurrency.A35;

namespace SMS.Integration.Tests.MultiCurrency;

/// <summary>
/// A35-P3-16 (QA) — FSD §13.5 / §13.6 on the real host (LocalDB): dual-amount storage and rate locking on sale documents
/// (T-C5-01..10) and the conversion service (T-C6-01..05), through the HTTP API of API-CONTRACT v1.2. Every test runs in its
/// own brand-new organization (sale base PKR = rate currency unless the test says otherwise), so rates and settings never leak.
/// Spec arithmetic that D-13 rounds differently is noted at the assert (T-C6-02: 241.0896 → 241.09, not the spec's 241.08).
/// <para>Run alone: <c>dotnet test &lt;out&gt;\SMS.Integration.Tests.dll --filter FullyQualifiedName~MultiCurrencyDocumentsE2ETests</c>.</para>
/// </summary>
public sealed class MultiCurrencyDocumentsE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _root;

    public MultiCurrencyDocumentsE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _root = new SapKit(factory, "MCD");
    }

    /// <summary>A fresh org: PKR sale/purchase/service base and rate currency; AED 76.30 and EUR 316.48 since 30 days ago.</summary>
    private async Task<(SapKit K, Guid Org, Guid Pkr, Guid Aed, Guid Eur)> PkrOrgAsync(string prefix)
    {
        var o = await _root.NewOrgAsync(prefix);
        var k = o.K;
        var pkr = await k.PkrAsync();
        var aed = await k.AedAsync();
        var eur = await k.EurAsync();
        await k.SetBasesAsync(pkr, pkr, pkr);
        await k.AddRateAsync(aed, 76.30m, Today.AddDays(-30));
        await k.AddRateAsync(eur, 316.48m, Today.AddDays(-30));
        return (k, o.OrgId, pkr, aed, eur);
    }

    // ── T-C5 ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task T_C5_01_and_04_SO_confirmed_in_AED_locks_the_confirm_date_rate_and_stores_PKR_base_amounts()
    {
        var (k, _, pkr, aed, _) = await PkrOrgAsync("C501");
        var item = await k.CreateProductAsync("AED Widget", 1m, 100m);
        await k.SellingPriceAsync(item, 120m, aed);
        await k.StockAsync(item);
        var customer = await k.CustomerAsync("Dubai Customer", null);

        var so = await k.SaleOrderAsync(customer, aed, Line(item, 50m));
        var draft = await k.GetSaleOrderAsync(so);
        draft.G("currencyId").Should().Be(aed);
        draft.S("currencyCode").Should().Be("AED");
        draft.ND("exchangeRate").Should().BeNull("D-11: not locked before CONFIRMED");
        draft.ND("grandTotalBase").Should().BeNull();
        draft.A("lines").Single().ND("lineTotalBase").Should().BeNull();

        var confirmed = await k.ConfirmAsync(so);
        confirmed.ND("exchangeRate").Should().Be(76.30m, "T-C5-04: the rate of the confirm date");
        confirmed.NG("baseCurrencyId").Should().Be(pkr);
        confirmed.S("baseCurrencyCode").Should().Be("PKR");
        confirmed.S("rateLockedAt").Should().NotBeNull();
        var line = confirmed.A("lines").Single();
        line.D("lineTotal").Should().Be(6000m, "50 × AED 120");
        line.ND("unitPriceBase").Should().Be(9156m, "120 × 76.30");
        line.ND("lineTotalBase").Should().Be(457_800m, "T-C5-01");
        confirmed.ND("grandTotalBase").Should().Be(457_800m, "v1.2: Σ line base");
        confirmed.ND("subtotalBase").Should().Be(457_800m, "no tax, no discount");
    }

    [Fact]
    public async Task T_C5_02_SO_in_the_sale_base_locks_rate_1_with_identical_amounts_and_no_rate_lookup()
    {
        // The sale base is EUR, which has NO rate row at all (the rate currency is PKR): if confirm looked a rate up it
        // would 400. BR-C5-04 says it must not.
        var o = await _root.NewOrgAsync("C502");
        var k = o.K;
        var pkr = await k.PkrAsync();
        var eur = await k.EurAsync();
        await k.SetBasesAsync(eur, pkr, pkr);
        var item = await k.CreateProductAsync("Domestic Widget", 1m, 100m);
        await k.SellingPriceAsync(item, 9200m, eur);
        await k.StockAsync(item);
        var customer = await k.CustomerAsync("Domestic Customer", null);

        var so = await k.SaleOrderAsync(customer, eur, Line(item, 25m));
        var confirmed = await k.ConfirmAsync(so);
        confirmed.ND("exchangeRate").Should().Be(1m);
        confirmed.NG("baseCurrencyId").Should().Be(eur);
        var line = confirmed.A("lines").Single();
        line.ND("unitPriceBase").Should().Be(9200m);
        line.ND("lineTotalBase").Should().Be(230_000m);
        confirmed.ND("grandTotalBase").Should().Be(confirmed.D("grandTotal"));
    }

    [Fact]
    public async Task T_C5_03_05_quotation_locks_at_SENT_SO_at_CONFIRMED_and_a_later_rate_correction_changes_neither()
    {
        var o = await _root.NewOrgAsync("C503");
        var k = o.K;
        var pkr = await k.PkrAsync();
        var aed = await k.AedAsync();
        await k.SetBasesAsync(pkr, pkr, pkr);
        await k.AddRateAsync(aed, 75.00m, Today.AddDays(-30));
        var current = await k.AddRateAsync(aed, 76.30m, Today); // closes the 75.00 row to yesterday
        var item = await k.CreateProductAsync("Quoted Widget", 1m, 100m);
        await k.StockAsync(item);
        var customer = await k.CustomerAsync("Quote Customer", aed);

        var q = await k.QuotationAsync(customer, aed, item, 10m, 120m);
        (await k.QuotationReadAsync(q)).ND("exchangeRate").Should().BeNull("a draft is not locked");
        await k.Ok(k.TrySendQuotationAsync(q), "send quotation");
        var sent = await k.QuotationReadAsync(q);
        sent.ND("exchangeRate").Should().Be(76.30m, "T-C5-03: the sent date's rate, not yesterday's 75.00");
        sent.NG("baseCurrencyId").Should().Be(pkr);
        sent.A("lines").Single().ND("lineTotalBase").Should().Be(91_560m, "1,200 × 76.30");

        var so = await k.AcceptAndConvertAsync(q);
        var confirmed = await k.ConfirmAsync(so);
        confirmed.ND("exchangeRate").Should().Be(76.30m, "T-C5-04");

        // T-C5-05: correct today's row afterwards — the locked documents keep their snapshot.
        await k.Ok(k.TryCorrectRateAsync(current, 76.45m), "historical correction of today's AED rate");
        (await k.RateOnAsync(aed, Today)).D("rate").Should().Be(76.45m);
        var after = await k.GetSaleOrderAsync(so);
        after.ND("exchangeRate").Should().Be(76.30m, "BR-C5-06: immutable after lock");
        after.A("lines").Single().ND("lineTotalBase").Should().Be(91_560m);
        (await k.QuotationReadAsync(q)).ND("exchangeRate").Should().Be(76.30m);

        // And the currency of a locked SO cannot change.
        (await k.Ok(k.Get($"/api/sale-orders/{so}"), "read")).S("currencyCode").Should().Be("AED");
    }

    [Fact]
    public async Task T_C5_06_SO_from_a_EUR_quotation_inherits_EUR_and_relocks_at_the_confirm_date()
    {
        var (k, _, pkr, _, eur) = await PkrOrgAsync("C506");
        var row = (await k.RateOnAsync(eur, Today));
        var item = await k.CreateProductAsync("Euro Widget", 1m, 100m);
        await k.StockAsync(item);
        var customer = await k.CustomerAsync("Euro Customer", null);

        var q = await k.QuotationAsync(customer, eur, item, 2m, 50m);
        await k.Ok(k.TrySendQuotationAsync(q), "send");
        (await k.QuotationReadAsync(q)).ND("exchangeRate").Should().Be(316.48m);

        await k.Ok(k.TryCorrectRateAsync(row, 317.00m), "EUR rate corrected after the quotation was sent");
        var so = await k.AcceptAndConvertAsync(q);
        var draft = await k.GetSaleOrderAsync(so);
        draft.G("currencyId").Should().Be(eur, "D-14: the SO inherits the quotation's currency");
        draft.ND("exchangeRate").Should().BeNull("D-12: the SO does not inherit the quotation's rate");

        var confirmed = await k.ConfirmAsync(so);
        confirmed.ND("exchangeRate").Should().Be(317.00m, "spec §6.3.5 / D-12: re-locked at confirmation");
        confirmed.NG("baseCurrencyId").Should().Be(pkr);
        (await k.QuotationReadAsync(q)).ND("exchangeRate").Should().Be(316.48m, "the quotation keeps its own lock");
    }

    [Fact]
    public async Task T_C5_07_and_08_JPY_and_BHD_round_at_their_own_decimals_and_base_at_PKR_decimals()
    {
        var o = await _root.NewOrgAsync("C507");
        var k = o.K;
        var pkr = await k.PkrAsync();
        var jpy = await k.JpyAsync();
        var bhd = await k.BhdAsync();
        await k.SetBasesAsync(pkr, pkr, pkr);
        await k.AddRateAsync(jpy, 1.86m, Today.AddDays(-30));
        await k.AddRateAsync(bhd, 737.47m, Today.AddDays(-30));
        var yenItem = await k.CreateProductAsync("Yen Widget", 1m, 100m);
        var dinarItem = await k.CreateProductAsync("Dinar Widget", 1m, 100m);
        await k.SellingPriceAsync(yenItem, 1234m, jpy);
        await k.SellingPriceAsync(dinarItem, 99.750m, bhd);
        await k.StockAsync(yenItem, dinarItem);
        var customer = await k.CustomerAsync("Rounding Customer", null);

        var yen = await k.ConfirmAsync(await k.SaleOrderAsync(customer, jpy, Line(yenItem, 10m)));
        var yl = yen.A("lines").Single();
        yl.D("lineTotal").Should().Be(12_340m, "¥, 0 dp");
        yl.ND("unitPriceBase").Should().Be(2_295.24m, "1,234 × 1.86");
        yl.ND("lineTotalBase").Should().Be(22_952.40m, "12,340 × 1.86 at PKR's 2 dp");

        var dinar = await k.ConfirmAsync(await k.SaleOrderAsync(customer, bhd, Line(dinarItem, 5m)));
        var dl = dinar.A("lines").Single();
        dl.D("unitPrice").Should().Be(99.750m);
        dl.D("lineTotal").Should().Be(498.750m, "BHD, 3 dp");
        dl.ND("unitPriceBase").Should().Be(73_562.63m, "99.750 × 737.47 = 73,562.6325");
        dl.ND("lineTotalBase").Should().Be(367_813.16m, "498.750 × 737.47 = 367,813.1625");
    }

    [Fact]
    public async Task T_C5_09_grand_total_base_is_the_sum_of_line_bases_on_a_five_line_AED_order()
    {
        var o = await _root.NewOrgAsync("C509");
        var k = o.K;
        var pkr = await k.PkrAsync();
        var aed = await k.AedAsync();
        await k.SetBasesAsync(pkr, pkr, pkr);
        await k.AddRateAsync(aed, 76.3333333333m, Today.AddDays(-30)); // a rate that makes per-line rounding visible
        var items = new List<Product>();
        foreach (var price in new[] { 1.11m, 2.27m, 3.33m, 4.49m, 5.55m })
        {
            var p = await k.CreateProductAsync($"Line {price}", 1m, 100m);
            await k.SellingPriceAsync(p, price, aed);
            items.Add(p);
        }
        await k.StockAsync(items.ToArray());
        var customer = await k.CustomerAsync("Five Line Customer", null);

        var so = await k.ConfirmAsync(await k.SaleOrderAsync(customer, aed, items.Select(i => Line(i, 3m)).ToArray()));
        var lines = so.A("lines");
        lines.Should().HaveCount(5);
        foreach (var l in lines)
            l.ND("lineTotalBase").Should().Be(Math.Round(l.D("lineTotal") * 76.3333333333m, 2, MidpointRounding.AwayFromZero));
        so.ND("grandTotalBase").Should().Be(lines.Sum(l => l.ND("lineTotalBase")), "T-C5-09 / v1.2");
        so.ND("subtotalBase").Should().Be(so.ND("grandTotalBase") - so.ND("taxAmountBase") + so.ND("discountAmountBase"), "v1.2");
    }

    [Fact]
    public async Task T_C5_10_confirming_an_MXN_order_with_no_MXN_rate_is_a_400_and_leaves_it_DRAFT()
    {
        var (k, _, _, _, _) = await PkrOrgAsync("C510");
        var mxn = await k.MxnAsync();
        var item = await k.CreateProductAsync("Peso Widget", 1m, 100m);
        await k.SellingPriceAsync(item, 10m, mxn);
        await k.StockAsync(item);
        var customer = await k.CustomerAsync("Mexico Customer", null);

        var so = await k.SaleOrderAsync(customer, mxn, Line(item, 1m));
        (await k.TryConfirmAsync(so)).ShouldBeMissingRate("MXN", Today, "BR-C2-05 / D-5");
        var after = await k.GetSaleOrderAsync(so);
        after.S("status").Should().Be("DRAFT", "the confirm rolled back");
        after.ND("exchangeRate").Should().BeNull();

        // The quotation send is the same gate.
        var q = await k.QuotationAsync(customer, mxn, item, 1m, 10m);
        (await k.TrySendQuotationAsync(q)).ShouldBeMissingRate("MXN", Today, "quotation send needs the rate too");
        (await k.QuotationReadAsync(q)).S("status").Should().Be("DRAFT");

        // With a rate, the same order confirms.
        await k.AddRateAsync(mxn, 15.10m, Today.AddDays(-1));
        (await k.ConfirmAsync(so)).ND("exchangeRate").Should().Be(15.10m);
    }

    // ── T-C6 (api/currency/convert, ICurrencyService) ───────────────────────────

    [Fact]
    public async Task T_C6_01_to_04_convert_direct_cross_same_currency_and_historical()
    {
        var (k, _, pkr, aed, eur) = await PkrOrgAsync("C601");
        var usd = await k.UsdAsync();
        await k.AddRateAsync(usd, 277.00m, Today.AddDays(-20));
        await k.AddRateAsync(usd, 277.50m, Today.AddDays(-6));
        await k.AddRateAsync(usd, 278.05m, Today.AddDays(-2)); // 277.50 now covers [-6, -3]

        var direct = await k.Ok(k.TryConvertAsync(1000m, aed, null, Today, "SALE"), "T-C6-01");
        direct.D("convertedAmount").Should().Be(76_300.00m);
        direct.P("toCurrency").G("id").Should().Be(pkr, "toCurrencyId null = the domain base");
        direct.D("rateUsed").Should().Be(76.30m);

        var cross = await k.Ok(k.TryConvertAsync(1000m, aed, eur, Today), "T-C6-02");
        cross.D("convertedAmount").Should().Be(241.09m, "1000 × 76.30 / 316.48 = 241.0896 (spec's 241.08 truncates; D-13 rounds)");
        cross.D("rateUsed").Should().Be(Math.Round(76.30m / 316.48m, 10, MidpointRounding.AwayFromZero));

        var same = await k.Ok(k.TryConvertAsync(5000m, pkr, pkr, Today), "T-C6-03");
        same.D("convertedAmount").Should().Be(5000m);
        same.D("rateUsed").Should().Be(1m);
        same.S("effectiveFrom")!.Should().StartWith("2000-01-01", "no lookup: the whole range");

        var historical = await k.Ok(k.TryConvertAsync(100m, usd, pkr, Today.AddDays(-4)), "T-C6-04");
        historical.D("rateUsed").Should().Be(277.50m);
        historical.D("convertedAmount").Should().Be(27_750m);
        historical.S("rateDate")!.Should().StartWith(Day(-4));

        var missing = await k.TryConvertAsync(1m, usd, pkr, Today.AddDays(-25));
        missing.ShouldBeMissingRate("USD", Today.AddDays(-25), "before the first USD row");
    }

    [Fact]
    public async Task T_C6_05_base_currency_per_domain_through_the_api_and_the_service()
    {
        var o = await _root.NewOrgAsync("C605");
        var k = o.K;
        var pkr = await k.PkrAsync();
        var usd = await k.UsdAsync();
        await k.AddRateAsync(usd, 278.05m, Today.AddDays(-30));
        var saved = await k.SetBasesAsync(pkr, usd, pkr);
        saved.G("saleBaseCurrencyId").Should().Be(pkr);
        saved.G("purchaseBaseCurrencyId").Should().Be(usd);
        (await k.SettingsAsync()).S("purchaseBaseCurrencyCode").Should().Be("USD");

        await using (var scope = _f.Services.CreateAsyncScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<ICurrencyService>();
            (await svc.GetBaseCurrencyIdAsync(o.OrgId, TransactionDomain.Sale)).Should().Be(pkr);
            (await svc.GetBaseCurrencyIdAsync(o.OrgId, TransactionDomain.Purchase)).Should().Be(usd);
            (await svc.GetBaseCurrencyIdAsync(o.OrgId, TransactionDomain.Service)).Should().Be(pkr);

            var ocs = scope.ServiceProvider.GetRequiredService<IOrganizationCurrencyService>();
            (await ocs.GetBaseCurrencyIdAsync(o.OrgId)).Should().Be(pkr, "D-7: the old overload = Sale");
            (await ocs.GetBaseCurrencyIdAsync(o.OrgId, TransactionDomain.Purchase)).Should().Be(usd);
        }

        // The convert endpoint resolves the domain base: 278.05 PKR → 1 USD in the PURCHASE domain.
        var toPurchaseBase = await k.Ok(k.TryConvertAsync(27_805m, pkr, null, Today, "PURCHASE"), "convert to the purchase base");
        toPurchaseBase.P("toCurrency").G("id").Should().Be(usd);
        toPurchaseBase.D("convertedAmount").Should().Be(100m);
    }

    [Fact]
    public async Task PO_in_a_foreign_currency_locks_at_APPROVED_against_the_purchase_base_and_missing_rate_blocks_approval()
    {
        var (k, _, pkr, _, _) = await PkrOrgAsync("CPO");
        var usd = await k.UsdAsync();
        var mxn = await k.MxnAsync();
        await k.AddRateAsync(usd, 278.05m, Today.AddDays(-30));
        await k.CreateApproverPlaceholdersAsync();
        var wh = await k.CreateWarehouseAsync();
        var vendor = await k.VendorAsync("US Supplier", usd);
        var item = await k.CreateProductAsync("Imported Part", 85m, null);

        var po = await k.CreatePoAsync(vendor, wh, null, (item, 500m, 85m));
        var draft = await k.PoAsync(po);
        draft.NG("currencyId").Should().Be(usd, "T-C4-04 / D-14: the supplier's default purchase currency");
        draft.ND("exchangeRate").Should().BeNull();

        await k.SubmitApproveAndSendPoAsync(po);
        var approved = await k.PoAsync(po);
        approved.ND("exchangeRate").Should().Be(278.05m);
        approved.NG("baseCurrencyId").Should().Be(pkr);
        approved.ND("totalAmountBase").Should().Be(11_817_125m, "$42,500 × 278.05");
        approved.A("lines").Single().ND("unitPriceBase").Should().Be(23_634.25m, "Appendix A single-base: $85 × 278.05");

        var poMxn = await k.CreatePoAsync(vendor, wh, mxn, (item, 1m, 10m));
        await k.Ok(k.Post($"/api/purchase-orders/{poMxn}/submit"), "submit MXN PO");
        var approve = await k.Post($"/api/purchase-orders/{poMxn}/approve");
        approve.ShouldBeMissingRate("MXN", Today, "PO approve needs the MXN rate (D-5)");
        (await k.PoAsync(poMxn)).S("status").Should().NotBe("APPROVED");
    }
}

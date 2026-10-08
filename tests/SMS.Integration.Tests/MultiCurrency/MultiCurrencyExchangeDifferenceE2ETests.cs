using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.SapAlignment;
using Xunit;
using static SMS.Integration.Tests.MultiCurrency.A35;

namespace SMS.Integration.Tests.MultiCurrency;

/// <summary>
/// A35-P4-04 host cross-check (QA) — FSD §13.7 T-C8-01..08 re-proven on the real host over FIN's unit tests: issued sales
/// invoices / approved supplier invoices in a foreign currency, customer and supplier payments posted on a later date at a
/// different rate, the realized difference per allocation (D-15 register row with the org's gain/loss account code), and the
/// revaluation run (UNREALIZED rows, idempotent per date, same-currency documents skipped). Each test runs in its own org:
/// all bases PKR (= rate currency), account codes 7110/7120/7130/7140.
/// <para>Run alone: <c>dotnet test &lt;out&gt;\SMS.Integration.Tests.dll --filter FullyQualifiedName~MultiCurrencyExchangeDifferenceE2ETests</c>.</para>
/// </summary>
public sealed class MultiCurrencyExchangeDifferenceE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapKit _root;

    public MultiCurrencyExchangeDifferenceE2ETests(SapWebApplicationFactory factory) => _root = new SapKit(factory, "MCX");

    private sealed record World(SapKit K, Guid Pkr, Guid Aed, Guid Eur, Guid Usd, Partner Customer);

    /// <summary>AED 76.30 / EUR 316.48 / USD 278.05 from 30 days ago; the later rates are each test's own.</summary>
    private async Task<World> WorldAsync(string prefix)
    {
        var o = await _root.NewOrgAsync(prefix);
        var k = o.K;
        var pkr = await k.PkrAsync();
        var aed = await k.AedAsync();
        var eur = await k.EurAsync();
        var usd = await k.UsdAsync();
        await k.SetBasesAsync(pkr, pkr, pkr);
        await k.AddRateAsync(aed, 76.30m, Today.AddDays(-30));
        await k.AddRateAsync(eur, 316.48m, Today.AddDays(-30));
        await k.AddRateAsync(usd, 278.05m, Today.AddDays(-30));
        var customer = await k.CustomerAsync("FX Customer", null);
        return new World(k, pkr, aed, eur, usd, customer);
    }

    /// <summary>An issued sales invoice of exactly <paramref name="amount"/> in <paramref name="currency"/> (one untaxed line).</summary>
    private static async Task<JsonElement> IssuedInvoiceAsync(World w, Guid currency, decimal amount)
    {
        var k = w.K;
        var item = await k.CreateProductAsync($"Item {amount}", 1m, 100m);
        await k.SellingPriceAsync(item, amount, currency);
        await k.StockAsync(item);
        var so = await k.SaleOrderAsync(w.Customer, currency, Line(item, 1m));
        await k.ConfirmAsync(so);
        return await k.InvoiceAndIssueAsync(so);
    }

    private static JsonElement Single(List<JsonElement> rows, string because)
    {
        rows.Should().ContainSingle(because);
        return rows[0];
    }

    [Fact]
    public async Task T_C8_01_realized_gain_on_a_full_AED_payment_at_a_higher_rate()
    {
        var w = await WorldAsync("X801");
        var inv = await IssuedInvoiceAsync(w, w.Aed, 10_550m);
        inv.ND("exchangeRate").Should().Be(76.30m, "locked at ISSUE");
        inv.ND("baseGrandTotal").Should().Be(804_965m);
        inv.NG("currencyId").Should().Be(w.Aed);
        inv.NG("baseCurrencyId").Should().Be(w.Pkr);
        inv.S("exchangeRateLockedAt").Should().NotBeNull();
        await w.K.AddRateAsync(w.Aed, 76.45m, Today.AddDays(7));

        var pay = await w.K.PayCustomerAsync(w.Customer, 10_550m, "AED", Today.AddDays(7), (inv.G("uuid"), 10_550m));
        pay.ND("exchangeRate").Should().Be(76.45m, "locked at the payment date");
        pay.ND("amountBase").Should().Be(806_547.50m);
        pay.ND("exchangeDifference").Should().Be(1_582.50m);
        pay.A("allocations").Single().ND("exchangeDifference").Should().Be(1_582.50m);

        var row = Single(await w.K.DifferencesOfAsync(inv.G("uuid"), "REALIZED"), "one realized row per allocation");
        row.S("side").Should().Be("RECEIVABLE");
        row.D("amountCurrency").Should().Be(10_550m);
        row.D("bookedRate").Should().Be(76.30m);
        row.D("settlementRate").Should().Be(76.45m);
        row.D("bookedAmountBase").Should().Be(804_965m);
        row.D("settledAmountBase").Should().Be(806_547.50m);
        row.D("differenceBase").Should().Be(1_582.50m, "T-C8-01");
        row.S("accountCode").Should().Be("7110", "exchange gain account");
        row.S("baseCurrencyCode").Should().Be("PKR");
        row.S("paymentType").Should().Be("CUSTOMER_PAYMENT");
    }

    [Fact]
    public async Task T_C8_02_realized_loss_on_a_EUR_payment_at_a_lower_rate()
    {
        var w = await WorldAsync("X802");
        var inv = await IssuedInvoiceAsync(w, w.Eur, 5_000m);
        await w.K.AddRateAsync(w.Eur, 315.90m, Today.AddDays(7));

        await w.K.PayCustomerAsync(w.Customer, 5_000m, "EUR", Today.AddDays(7), (inv.G("uuid"), 5_000m));
        var row = Single(await w.K.DifferencesOfAsync(inv.G("uuid"), "REALIZED"), "one row");
        row.D("differenceBase").Should().Be(-2_900m, "T-C8-02: 5,000 × (315.90 − 316.48)");
        row.S("accountCode").Should().Be("7120", "exchange loss account");
    }

    [Fact]
    public async Task T_C8_03_and_04_same_rate_gives_zero_and_a_base_currency_invoice_gives_nothing()
    {
        var w = await WorldAsync("X803");
        var aedInv = await IssuedInvoiceAsync(w, w.Aed, 1_000m);
        var pay = await w.K.PayCustomerAsync(w.Customer, 1_000m, "AED", Today.AddDays(3), (aedInv.G("uuid"), 1_000m));
        pay.ND("exchangeDifference").Should().Be(0m, "T-C8-03: same rate (76.30 still current)");
        var rows = await w.K.DifferencesOfAsync(aedInv.G("uuid"), "REALIZED");
        rows.Should().NotContain(r => r.D("differenceBase") != 0m, "a zero difference is never a gain or a loss (a zero row or no row)");

        var pkrInv = await IssuedInvoiceAsync(w, w.Pkr, 50_000m);
        pkrInv.ND("exchangeRate").Should().Be(1m);
        var pkrPay = await w.K.PayCustomerAsync(w.Customer, 50_000m, "PKR", Today.AddDays(3), (pkrInv.G("uuid"), 50_000m));
        (pkrPay.ND("exchangeDifference") ?? 0m).Should().Be(0m);
        (await w.K.DifferencesOfAsync(pkrInv.G("uuid")))
            .Should().BeEmpty("T-C8-04 / BR-C7-05: no calculation for a base-currency invoice");
    }

    [Fact]
    public async Task T_C8_05_partial_payment_realizes_only_the_allocated_portion()
    {
        var w = await WorldAsync("X805");
        var inv = await IssuedInvoiceAsync(w, w.Aed, 10_000m);
        await w.K.AddRateAsync(w.Aed, 76.50m, Today.AddDays(5));

        await w.K.PayCustomerAsync(w.Customer, 4_000m, "AED", Today.AddDays(5), (inv.G("uuid"), 4_000m));
        var row = Single(await w.K.DifferencesOfAsync(inv.G("uuid"), "REALIZED"), "one row");
        row.D("amountCurrency").Should().Be(4_000m);
        row.D("differenceBase").Should().Be(800m, "T-C8-05: 4,000 × 0.20");
        var after = await w.K.GetInvoiceAsync(inv.G("uuid"));
        after.D("balanceDue").Should().Be(6_000m, "the remaining AED 6,000 is untouched");
        after.ND("exchangeRate").Should().Be(76.30m, "the invoice keeps its booked rate");
    }

    [Fact]
    public async Task Allocating_a_payment_to_an_invoice_in_another_currency_is_a_400()
    {
        var w = await WorldAsync("XMIX");
        var inv = await IssuedInvoiceAsync(w, w.Aed, 100m);
        var api = await w.K.TryPayCustomerAsync(w.Customer, 100m, "EUR", Today, (inv.G("uuid"), 100m));
        api.Status.Should().Be(HttpStatusCode.BadRequest, $"D-14 — {api}");
        api.Message.Should().Be($"Payment is in EUR; invoice {inv.S("invoiceNumber")} is in AED. Allocate it to invoices in the same currency.");
    }

    [Fact]
    public async Task T_C8_06_to_08_revaluation_books_unrealized_gain_on_AR_loss_on_AP_skips_base_currency_and_is_idempotent()
    {
        var w = await WorldAsync("X806");
        var k = w.K;
        var eurInv = await IssuedInvoiceAsync(w, w.Eur, 5_000m);
        var pkrInv = await IssuedInvoiceAsync(w, w.Pkr, 50_000m);

        var vendor = await k.VendorAsync("US Vendor", w.Usd);
        var bill = await k.SupplierInvoiceAsync(vendor, "USD", w.Usd, 10_000m);
        await k.Ok(k.TryApproveSupplierInvoiceAsync(bill), "approve the USD bill");
        var billRead = await k.SupplierInvoiceReadAsync(bill);
        billRead.ND("exchangeRate").Should().Be(278.05m, "locked at APPROVAL against the purchase base");
        billRead.ND("baseTotalAmount").Should().Be(2_780_500m);

        var revalDate = Today.AddDays(10);
        await k.AddRateAsync(w.Eur, 317.00m, revalDate);
        await k.AddRateAsync(w.Usd, 277.50m, revalDate);

        var run = await k.Ok(k.TryRevalueAsync(revalDate), "run revaluation");
        run.I("receivablesRevalued").Should().BeGreaterThanOrEqualTo(1);
        run.I("payablesRevalued").Should().BeGreaterThanOrEqualTo(1);

        var ar = Single(await k.DifferencesOfAsync(eurInv.G("uuid"), "UNREALIZED"), "one row per open document per date");
        ar.D("differenceBase").Should().Be(2_600m, "T-C8-06: 5,000 × (317.00 − 316.48)");
        ar.S("accountCode").Should().Be("7130");
        ar.S("revaluationDate")!.Should().StartWith(Day(revalDate));
        ar.IsNull("paymentType").Should().BeTrue();

        var ap = Single(await k.DifferencesOfAsync(bill, "UNREALIZED"), "one AP row");
        ap.S("side").Should().Be("PAYABLE");
        // T-C8-07: the spec calls this a PKR 5,500 loss; FIN's recorded deviation (economically right): owing USD 10,000 that
        // now costs 5,500 PKR less is an unrealized GAIN on a payable (booked − settled).
        ap.D("differenceBase").Should().Be(5_500m, "T-C8-07 magnitude 10,000 × 0.55, sign per FIN's payable deviation");
        ap.S("accountCode").Should().Be("7130", "unrealized gain account");

        (await k.DifferencesOfAsync(pkrInv.G("uuid"))).Should().BeEmpty("T-C8-08: PKR AR is skipped");

        // Idempotent: the same date again replaces, never duplicates.
        var again = await k.Ok(k.TryRevalueAsync(revalDate), "re-run revaluation");
        again.I("rowsReplaced").Should().BeGreaterThanOrEqualTo(2);
        (await k.DifferencesOfAsync(eurInv.G("uuid"), "UNREALIZED")).Should().ContainSingle();
    }

    [Fact]
    public async Task Supplier_payment_in_USD_realizes_the_difference_on_the_payable_side()
    {
        var w = await WorldAsync("XSUP");
        var k = w.K;
        var vendor = await k.VendorAsync("US Vendor", w.Usd);
        var bill = await k.SupplierInvoiceAsync(vendor, "USD", w.Usd, 1_000m);
        await k.Ok(k.TryApproveSupplierInvoiceAsync(bill), "approve");
        await k.AddRateAsync(w.Usd, 280.00m, Today.AddDays(4));

        var pay = await k.PaySupplierAsync(vendor, bill, 1_000m, "USD", Today.AddDays(4));
        pay.ND("exchangeRate").Should().Be(280.00m);
        pay.ND("amountBase").Should().Be(280_000m);
        pay.ND("exchangeDifference").Should().Be(-1_950m, "paying more PKR for the same USD 1,000 is a loss: 1,000 × (278.05 − 280.00)");
        var row = Single(await k.DifferencesOfAsync(bill, "REALIZED"), "one row");
        row.S("side").Should().Be("PAYABLE");
        row.D("differenceBase").Should().Be(-1_950m);
        row.S("accountCode").Should().Be("7120");
    }
}

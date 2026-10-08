using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace SMS.Integration.Tests.SapAlignment;

/// <summary>
/// SAP alignment scenario 2 — the sales side through the real host, from stock received on a PO/GRN to a
/// sale order priced with tax codes, a self-pickup delivery, a sales invoice raised from it and issued:
/// the code travels as a snapshot (S-3), the currency is snapshotted at issue (S-5), an issued invoice is
/// cancelled by opposite entries rather than edited (S-7), and a price rule in another currency is converted
/// into the order's currency.
/// <para>Run alone: <c>dotnet test &lt;out&gt;\SMS.Integration.Tests.dll --filter FullyQualifiedName~SalesFlowE2ETests</c>.</para>
/// </summary>
public sealed class SalesFlowE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapKit _k;
    private readonly SapWebApplicationFactory _f;

    public SalesFlowE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _k = new SapKit(factory, "SF");
    }

    [Fact]
    public async Task An_issued_invoice_keeps_its_codes_and_base_snapshot_and_is_cancelled_by_opposite_entries_not_edits()
    {
        // ── Setup: PKR base, codes, stock on hand (PO → GRN) ─────────────────────
        var pkr = await _k.EnsureCurrencyAsync("PKR", "Pakistani Rupee", "Rs");
        await _k.SetBaseCurrencyAsync(pkr);
        var gst17 = await _k.EnsureTaxCodeAsync("GST17", 17m, "SALES", isDefault: true);
        await _k.EnsureTaxCodeAsync("PGST17", 17m, "PURCHASE", isDefault: true);
        await _k.CreateApproverPlaceholdersAsync();

        var wh       = await _k.CreateWarehouseAsync();
        var vendor   = await _k.CreateVendorAsync("Stock Vendor");
        var customer = await _k.CreateCustomerAsync("Walk-in Customer");
        var a = await _k.CreateProductAsync("Coded Widget", purchasePrice: 60m, sellingPrice: 100m);
        var b = await _k.CreateProductAsync("Legacy Gadget", purchasePrice: 30m, sellingPrice: 50m);
        await _k.StockUpAsync(vendor, wh, (a, 50m, 60m), (b, 50m, 30m));

        (await ProductLedgerAsync("VariantUuid", a.VariantUuid)).Should().Contain(r => (string)r["EntryType"]! == "PURCHASE",
            "the GRN put the stock on the product ledger, which the invoice's cost of sales draws on");

        // ── Sale order: a coded line (no client percent) and a legacy percent line ──
        var soUuid = await _k.CreateSaleOrderAsync(customer, pkr,
            SapKit.Line(a, 2m, taxCode: gst17),
            SapKit.Line(b, 2m, taxPercent: 5m));
        var so = await _k.GetSaleOrderAsync(soUuid);
        var soA = so.A("lines").Single(l => l.G("variantUuid") == a.VariantUuid);
        var soB = so.A("lines").Single(l => l.G("variantUuid") == b.VariantUuid);
        (soA.S("taxCode"), soA.D("taxPercent"), soA.D("lineTotal")).Should().Be(("GST17", 17m, 234m));
        (soB.S("taxCode"), soB.D("taxPercent"), soB.D("lineTotal")).Should().Be(((string?)null, 5m, 105m));
        (so.D("subtotal"), so.D("taxAmount"), so.D("grandTotal")).Should().Be((300m, 39m, 339m));

        await _k.ConfirmSaleOrderAsync(soUuid);
        var delivery = await _k.DeliverAsync(soUuid);

        // ── Raise: the lines carry the code snapshot ───────────────────────────────
        var raised = await _k.RaiseInvoiceAsync(delivery);
        raised.B("alreadyExisted").Should().BeFalse();
        raised.D("grandTotal").Should().Be(339m);
        var inv1 = raised.G("invoiceUuid");
        (await _k.RaiseInvoiceAsync(delivery)).G("invoiceUuid").Should().Be(inv1, "asking again returns the invoice already raised");

        var draft = await _k.GetInvoiceAsync(inv1);
        draft.S("status").Should().Be("DRAFT");
        var lineA = draft.A("lines").Single(l => l.G("variantUuid") == a.VariantUuid);
        var lineB = draft.A("lines").Single(l => l.G("variantUuid") == b.VariantUuid);
        (lineA.S("taxCode"), lineA.NG("taxCodeUuid"), lineA.D("taxPercent"), lineA.D("lineTotal")).Should().Be(("GST17", (Guid?)gst17, 17m, 234m));
        (lineB.S("taxCode"), lineB.NG("taxCodeUuid"), lineB.D("taxPercent"), lineB.D("lineTotal")).Should().Be(((string?)null, (Guid?)null, 5m, 105m));
        (draft.D("subtotal"), draft.D("taxAmount"), draft.D("grandTotal")).Should().Be((300m, 39m, 339m));
        draft.ND("exchangeRate").Should().BeNull("the rate is fixed at issue, not before");

        var draftCancel = await Cancel(inv1, "too early");
        draftCancel.Status.Should().Be(HttpStatusCode.Conflict, $"a draft booked nothing; it is deleted, not cancelled — {draftCancel}");

        // ── Issue: same-currency snapshot, receivable booked ───────────────────────
        await _k.IssueAsync(inv1);
        var issued = await _k.GetInvoiceAsync(inv1);
        issued.S("status").Should().Be("ISSUED");
        issued.ND("exchangeRate").Should().Be(1m, "the invoice is in the base currency");
        issued.S("baseCurrencyCode").Should().Be("PKR");
        issued.ND("baseGrandTotal").Should().Be(339m);

        var ledger = await CustomerLedgerAsync(customer.Uuid);
        ledger.Should().ContainSingle();
        (ledger[0].Type, ledger[0].Debit, ledger[0].Credit, ledger[0].Balance).Should().Be(("INVOICE", 339m, 0m, 339m));
        (await _k.Ok(_k.Get($"/api/partners/{customer.Uuid}/ledger"), "customer ledger API")).A("data").Should().ContainSingle();

        (await InvoicedQtyAsync(soUuid)).Should().BeEquivalentTo(new[] { 2m, 2m });
        (await ProductLedgerAsync("ReferenceId", inv1)).Should().HaveCount(2).And
            .OnlyContain(r => (string)r["EntryType"]! == "SALE" && (string)r["Direction"]! == "OUT");

        var pdf = await _k.Admin.GetAsync($"/api/sales-invoices/{inv1}/pdf");
        pdf.StatusCode.Should().Be(HttpStatusCode.OK, "a coded invoice prints");
        System.Text.Encoding.ASCII.GetString((await pdf.Content.ReadAsByteArrayAsync())[..4]).Should().Be("%PDF");

        // ── The order cannot be cancelled out from under a live invoice ───────────
        var soCancel = await _k.Post($"/api/sale-orders/{soUuid}/cancel", new { Reason = "changed mind" });
        soCancel.Status.Should().Be(HttpStatusCode.Conflict, soCancel.ToString());
        soCancel.Raw.Should().Contain(issued.S("invoiceNumber")!, "the refusal names the invoice that stands");

        // ── Cancel the issued invoice: a reason is required ────────────────────────
        (await _k.Post($"/api/sales-invoices/{inv1}/cancel", null)).Status.Should().Be(HttpStatusCode.BadRequest, "no reason");
        (await Cancel(inv1, "   ")).Status.Should().Be(HttpStatusCode.BadRequest, "a blank reason");
        (await Cancel(inv1, new string('x', 501))).Status.Should().Be(HttpStatusCode.BadRequest, "a reason over 500 characters");

        var cancelled = await _k.Ok(Cancel(inv1, "Customer returned the goods"), "cancel issued invoice");
        cancelled.S("status").Should().Be("CANCELLED");
        cancelled.S("cancellationReason").Should().Be("Customer returned the goods");
        cancelled.IsNull("cancelledAt").Should().BeFalse();
        cancelled.IsNull("cancelledBy").Should().BeFalse();
        cancelled.D("balanceDue").Should().Be(0m);
        cancelled.D("grandTotal").Should().Be(339m, "nothing on the invoice is edited");
        cancelled.ND("baseGrandTotal").Should().Be(339m, "the snapshot stays");

        ledger = await CustomerLedgerAsync(customer.Uuid);
        ledger.Should().HaveCount(2, "the ledger is append-only: the debit stays and an opposite credit offsets it");
        (ledger[1].Type, ledger[1].Debit, ledger[1].Credit, ledger[1].Balance, ledger[1].Reference).Should().Be(("CREDIT_NOTE", 0m, 339m, 0m, inv1));
        ledger[1].Narration.Should().Contain("Customer returned the goods");

        var stock = await ProductLedgerAsync("ReferenceId", inv1);
        stock.Should().HaveCount(4);
        var sales   = stock.Where(r => (string)r["EntryType"]! == "SALE").ToList();
        var returns = stock.Where(r => (string)r["EntryType"]! == "RETURN_IN").ToList();
        returns.Should().HaveCount(2).And.OnlyContain(r => (string)r["Direction"]! == "IN");
        foreach (var sale in sales)
        {
            var back = returns.Single(r => (Guid)r["VariantUuid"]! == (Guid)sale["VariantUuid"]!);
            ((decimal)back["Quantity"]!).Should().Be((decimal)sale["Quantity"]!);
            ((decimal)back["UnitCost"]!).Should().Be((decimal)sale["UnitCost"]!, "the goods come back at the cost they went out at");
        }

        (await InvoicedQtyAsync(soUuid)).Should().BeEquivalentTo(new[] { 0m, 0m }, "the order's invoiced quantities are taken back");

        // The reports built on these books agree: nothing sold, nothing owed.
        var summaryA = await _k.Ok(_k.Get($"/api/product-ledger/{a.VariantUuid}/summary"), "product ledger summary");
        (summaryA.D("soldQuantity"), summaryA.D("costOfGoodsSold"), summaryA.D("currentQuantity"))
            .Should().Be((0m, 0m, 50m), $"a sale reversed by a cancellation is not a sale — {J.Short(summaryA)}");
        var aging = await _k.Ok(_k.Get($"/api/reports/sales/aging-receivables?asOf={Today}&partnerId={customer.Uuid}"), "aging receivables");
        aging.A("invoices").Should().BeEmpty($"a cancelled invoice is owed by nobody — {J.Short(aging)}");
        var byProduct = await _k.Ok(_k.Get($"/api/reports/sales/sales-by-product?dateFrom={Today}&dateTo={Today}&partnerId={customer.Uuid}"), "sales by product");
        byProduct.A("items").Should().BeEmpty($"no live invoice, no sale — {J.Short(byProduct)}");
        var statement = await _k.Ok(_k.Get($"/api/partners/{customer.Uuid}/ledger"), "customer ledger API");
        statement.A("data").Select(e => e.S("entryType")).Should().BeEquivalentTo(new[] { "INVOICE", "CREDIT_NOTE" });

        (await Cancel(inv1, "again")).Status.Should().Be(HttpStatusCode.Conflict, "already cancelled");
        (await _k.Post($"/api/sales-invoices/{inv1}/issue")).Status.Should().Be(HttpStatusCode.Conflict, "a cancelled invoice is not re-issued");
        (await _k.Ok(_k.Get($"/api/sales-invoices?status=CANCELLED&saleOrderUuid={soUuid}"), "list cancelled")).A("data")
            .Should().ContainSingle(i => i.G("uuid") == inv1);

        // ── The delivery can be invoiced again ─────────────────────────────────────
        var again = await _k.RaiseInvoiceAsync(delivery);
        again.B("alreadyExisted").Should().BeFalse("the cancelled invoice no longer holds the delivery");
        var inv2 = again.G("invoiceUuid");
        inv2.Should().NotBe(inv1);
        await _k.IssueAsync(inv2);
        (await InvoicedQtyAsync(soUuid)).Should().BeEquivalentTo(new[] { 2m, 2m });
        ledger = await CustomerLedgerAsync(customer.Uuid);
        (ledger[^1].Type, ledger[^1].Debit, ledger[^1].Balance).Should().Be(("INVOICE", 339m, 339m));
        (await _k.Ok(_k.Get($"/api/product-ledger/{a.VariantUuid}/summary"), "product ledger summary")).D("soldQuantity")
            .Should().Be(2m, "only the live invoice's sale counts");
        (await _k.Ok(_k.Get($"/api/reports/sales/aging-receivables?asOf={Today}&partnerId={customer.Uuid}"), "aging receivables"))
            .A("invoices").Should().ContainSingle(i => i.G("invoiceUuid") == inv2);
        (await _k.Ok(_k.Get($"/api/reports/sales/sales-by-product?dateFrom={Today}&dateTo={Today}&partnerId={customer.Uuid}"), "sales by product"))
            .A("items").Sum(i => i.D("quantitySold")).Should().Be(4m, "the re-issued invoice's 2 + 2 units, once");

        // ── Paid or part-paid: refused ─────────────────────────────────────────────
        await Pay(customer, 100m);
        (await _k.GetInvoiceAsync(inv2)).S("status").Should().Be("PARTIALLY_PAID");
        var partPaid = await Cancel(inv2, "wrong customer");
        partPaid.Status.Should().Be(HttpStatusCode.Conflict, $"something has been paid against it — {partPaid}");

        await Pay(customer, 239m);
        (await _k.GetInvoiceAsync(inv2)).S("status").Should().Be("PAID");
        (await Cancel(inv2, "wrong customer")).Status.Should().Be(HttpStatusCode.Conflict, "a paid invoice cannot be cancelled");

        // The FIFO payments went to the live invoice, never to the cancelled one.
        (await _f.QueryAsync(
            "SELECT COUNT(*) AS N FROM finance.payment_allocations a JOIN finance.sales_invoices i ON i.Id = a.SalesInvoiceId WHERE i.UUID = @i",
            ("@i", inv1))).Single()["N"].Should().Be(0);
        (await _k.GetInvoiceAsync(inv1)).D("amountPaid").Should().Be(0m);

        (await _k.Post($"/api/sale-orders/{soUuid}/cancel", new { Reason = "still not" })).Status
            .Should().Be(HttpStatusCode.Conflict, "the order still has a live (paid) invoice");
    }

    [Fact]
    public async Task Foreign_currency_invoices_snapshot_the_rate_at_issue_and_price_rules_are_converted_into_the_order_currency()
    {
        var today = DateTime.UtcNow.Date;
        var pkr = await _k.EnsureCurrencyAsync("PKR", "Pakistani Rupee", "Rs");
        var usd = await _k.EnsureCurrencyAsync("USD", "US Dollar", "$");
        var eur = await _k.EnsureCurrencyAsync("EUR", "Euro", "€");
        var gbp = await _k.EnsureCurrencyAsync("GBP", "Pound Sterling", "£");
        await _k.SetBaseCurrencyAsync(pkr);
        var gst17 = await _k.EnsureTaxCodeAsync("GST17", 17m, "SALES", isDefault: true);
        await _k.CreateApproverPlaceholdersAsync();
        await _k.CreateRateAsync("USD", "PKR", 280m, today.AddDays(-30));
        // No EUR or GBP rate anywhere.

        var wh       = await _k.CreateWarehouseAsync();
        var vendor   = await _k.CreateVendorAsync("FX Vendor");
        var customer = await _k.CreateCustomerAsync("Export Customer");
        var ruled    = await _k.CreateProductAsync("USD Priced Widget", purchasePrice: 1000m, sellingPrice: 999m);
        var plain    = await _k.CreateProductAsync("Plain Widget", purchasePrice: 30m, sellingPrice: 50m);
        var gbpRuled = await _k.CreateProductAsync("GBP Priced Widget", purchasePrice: 30m, sellingPrice: 50m);
        var eurRuled = await _k.CreateProductAsync("EUR Priced Widget", purchasePrice: 30m, sellingPrice: 50m);
        await _k.StockUpAsync(vendor, wh, (ruled, 20m, 1000m), (plain, 20m, 30m), (eurRuled, 20m, 30m));

        await _k.Ok(_k.Post("/api/pricing-rules", new
        {
            VariantUuid = ruled.VariantUuid, PriceType = "SELLING", UnitPrice = 10m, CurrencyId = usd, EffectiveFrom = today.AddDays(-60)
        }), "USD selling price rule");
        await _k.Ok(_k.Post("/api/pricing-rules", new
        {
            VariantUuid = gbpRuled.VariantUuid, PriceType = "SELLING", UnitPrice = 5m, CurrencyId = gbp, EffectiveFrom = today.AddDays(-60)
        }), "GBP selling price rule");
        await _k.Ok(_k.Post("/api/pricing-rules", new
        {
            VariantUuid = eurRuled.VariantUuid, PriceType = "SELLING", UnitPrice = 40m, CurrencyId = eur, EffectiveFrom = today.AddDays(-60)
        }), "EUR selling price rule");

        // ── A USD price rule on a PKR order is converted at the rate for the order date ──
        var pkrOrder = await _k.CreateSaleOrderAsync(customer, pkr, SapKit.Line(ruled, 1m, taxCode: gst17));
        var pkrLine = (await _k.GetSaleOrderAsync(pkrOrder)).A("lines").Single();
        pkrLine.D("unitPrice").Should().Be(2800m, "10 USD × 280");
        pkrLine.D("lineTotal").Should().Be(3276m);

        var noRate = await _k.TryCreateSaleOrderAsync(customer, pkr, SapKit.Line(gbpRuled, 1m, taxCode: gst17));
        noRate.Status.Should().Be(HttpStatusCode.BadRequest, $"no GBP→PKR rate to convert at — {noRate}");
        noRate.Raw.Should().Contain("GBP").And.Contain("exchange rate");

        // ── The variant's own list price is in the base currency: converted onto a USD order ──
        var listPriced = await _k.CreateSaleOrderAsync(customer, usd, SapKit.Line(plain, 1m, taxCode: gst17));
        var reciprocal = Math.Round(1m / 280m, 8, MidpointRounding.AwayFromZero);
        (await _k.GetSaleOrderAsync(listPriced)).A("lines").Single().D("unitPrice")
            .Should().Be(Math.Round(50m * reciprocal, 2, MidpointRounding.AwayFromZero), "50 PKR at the reciprocal of USD→PKR 280");
        var listNoRate = await _k.TryCreateSaleOrderAsync(customer, eur, SapKit.Line(plain, 1m, taxCode: gst17));
        listNoRate.Status.Should().Be(HttpStatusCode.BadRequest, $"a PKR list price on a EUR order needs a PKR↔EUR rate — {listNoRate}");

        // ── A USD invoice: the rate into the base currency is fixed at issue ─────────
        var (usdSo, _, usdInvoice) = await _k.SellAsync(customer, usd, SapKit.Line(ruled, 1m, taxCode: gst17));
        var usdLine = (await _k.GetSaleOrderAsync(usdSo)).A("lines").Single();
        usdLine.D("unitPrice").Should().Be(10m, "the rule is already in the order's currency");
        var usdInv = await _k.GetInvoiceAsync(usdInvoice);
        usdInv.S("currencyCode").Should().Be("USD");
        usdInv.D("grandTotal").Should().Be(11.70m);
        usdInv.ND("exchangeRate").Should().Be(280m);
        usdInv.S("baseCurrencyCode").Should().Be("PKR");
        usdInv.ND("baseGrandTotal").Should().Be(3276m, "11.70 USD × 280");
        usdInv.A("lines").Single().S("taxCode").Should().Be("GST17");

        // A later rate never changes an issued invoice.
        await _k.CreateRateAsync("USD", "PKR", 300m, today);
        (await _k.GetInvoiceAsync(usdInvoice)).ND("exchangeRate").Should().Be(280m, "the snapshot is the document's own");

        // ── A35 D-5 (supersedes S-5): no EUR rate on file refuses the lock — the order cannot be confirmed ─────
        // (A EUR price rule, so the order itself needs no conversion.)
        var eurSo = await _k.CreateSaleOrderAsync(customer, eur, SapKit.Line(eurRuled, 1m, taxCode: gst17));
        var noRateAtLock = await _k.Post($"/api/sale-orders/{eurSo}/confirm");
        noRateAtLock.Status.Should().Be(HttpStatusCode.BadRequest, $"a EUR order in a PKR org needs a EUR rate to lock — {noRateAtLock}");
        noRateAtLock.Message.Should().Be($"No exchange rate for EUR on {Today}. Add one under Settings → Exchange Rates.");
        (await _k.GetSaleOrderAsync(eurSo)).S("status").Should().Be("DRAFT", "nothing was locked");

        // With a rate the same order confirms, and its invoice carries the rate from issue.
        await _k.CreateRateAsync("EUR", "PKR", 316.48m, today.AddDays(-1));
        await _k.ConfirmSaleOrderAsync(eurSo);
        var eurInv = await _k.GetInvoiceAsync(await IssueAsync(eurSo));
        eurInv.S("status").Should().Be("ISSUED");
        eurInv.S("currencyCode").Should().Be("EUR");
        eurInv.D("grandTotal").Should().Be(46.80m, "40 EUR + 17%");
        eurInv.ND("exchangeRate").Should().Be(316.48m);
        eurInv.ND("baseGrandTotal").Should().Be(14_811.26m, "46.80 × 316.48 = 14,811.264");
        eurInv.S("baseCurrencyCode").Should().Be("PKR");
    }

    private async Task<Guid> IssueAsync(Guid confirmedSo)
    {
        var delivery = await _k.DeliverAsync(confirmedSo);
        var invoice = (await _k.RaiseInvoiceAsync(delivery)).G("invoiceUuid");
        await _k.IssueAsync(invoice);
        return invoice;
    }
    // ── helpers ──────────────────────────────────────────────────────────────────

    private static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd");

    private Task<Api> Cancel(Guid invoice, string reason) => _k.Post($"/api/sales-invoices/{invoice}/cancel", new { reason });

    private async Task Pay(Partner customer, decimal amount) =>
        await _k.Ok(_k.Post("/api/customer-payments", new
        {
            PartnerId = customer.Uuid, Amount = amount, Method = "CASH", CurrencyCode = "PKR", PaymentDate = DateTime.UtcNow.Date
        }), $"customer payment {amount}");

    private sealed record LedgerRow(string Type, decimal Debit, decimal Credit, decimal Balance, Guid Reference, string? Narration);

    private async Task<List<LedgerRow>> CustomerLedgerAsync(Guid partner) =>
        (await _f.QueryAsync(
            "SELECT EntryType, DebitAmount, CreditAmount, RunningBalance, ReferenceId, Narration FROM finance.customer_ledger " +
            "WHERE PartnerId = @p ORDER BY SequenceNo", ("@p", partner)))
        .Select(r => new LedgerRow((string)r["EntryType"]!, (decimal)r["DebitAmount"]!, (decimal)r["CreditAmount"]!,
            (decimal)r["RunningBalance"]!, (Guid)r["ReferenceId"]!, r["Narration"] as string))
        .ToList();

    private Task<List<Dictionary<string, object?>>> ProductLedgerAsync(string column, Guid value) =>
        _f.QueryAsync(
            $"SELECT EntryType, Direction, Quantity, UnitCost, VariantUuid, ReferenceId FROM finance.product_ledger WHERE {column} = @v ORDER BY Id",
            ("@v", value));

    private async Task<List<decimal>> InvoicedQtyAsync(Guid soUuid) =>
        (await _k.GetSaleOrderAsync(soUuid)).A("lines").Select(l => l.D("invoicedQty")).ToList();
}

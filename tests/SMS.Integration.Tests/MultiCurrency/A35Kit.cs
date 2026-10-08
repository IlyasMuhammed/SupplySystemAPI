using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;

namespace SMS.Integration.Tests.MultiCurrency;

/// <summary>One fresh organization for an A35 host test, acting as its own (non-super) admin.</summary>
internal sealed record CurrencyOrg(Guid OrgId, SapKit K, string AdminEmail);

/// <summary>
/// A35 (QA) — what the multi-currency host classes share on top of <see cref="SapKit"/> / <see cref="PreOrder"/>: a fresh
/// organization per test (so settings, rates and documents never leak between tests), its currencies through
/// <c>api/currencies</c>, its bases through <c>api/organization/currency-settings</c>, rates through <c>api/currency-rates</c>,
/// conversions through <c>api/currency/convert</c>, and the document flows (quotation → SO → delivery → sales invoice →
/// customer payment; PO → GRN; supplier invoice → supplier payment) through the endpoints a person would use.
/// Everything is written against docs/multi-currency/API-CONTRACT.md (v1.2): bodies are anonymous objects and answers are
/// read as JSON, so these tests compile whatever the owners' C# models look like.
/// </summary>
internal static class A35
{
    public static DateTime Today => DateTime.UtcNow.Date;
    public static string Day(DateTime d) => d.ToString("yyyy-MM-dd");
    public static string Day(int offset) => Day(Today.AddDays(offset));

    public static string MissingRate(string code, DateTime date) =>
        $"No exchange rate for {code} on {Day(date)}. Add one under Settings → Exchange Rates.";

    // ── Organization ─────────────────────────────────────────────────────────────

    /// <summary>A brand-new organization (provisioned through the platform API) and a kit acting as its admin.</summary>
    public static async Task<CurrencyOrg> NewOrgAsync(this SapKit root, string prefix)
    {
        var (orgId, k, email) = await root.SecondOrganizationAsync(prefix);
        return new CurrencyOrg(orgId, k, email);
    }

    // ── Currencies (api/currencies, D-1) ─────────────────────────────────────────

    /// <summary>The organization's currency list (inactive included).</summary>
    public static async Task<List<JsonElement>> OrgCurrenciesAsync(this SapKit k) =>
        (await k.Ok(k.Get("/api/currencies?includeInactive=true"), "list org currencies")).Items();

    /// <summary>
    /// The global currency id of <paramref name="code"/>, configured and active for the kit's organization with the given
    /// formatting: linked/created through POST api/currencies when missing, otherwise updated through PUT.
    /// </summary>
    public static async Task<Guid> CurrencyAsync(this SapKit k, string code, string name, string symbol, int decimals = 2,
        string symbolPosition = "before")
    {
        var existing = (await k.OrgCurrenciesAsync()).FirstOrDefault(c => c.S("code") == code);
        if (existing.ValueKind == JsonValueKind.Object)
        {
            var id = existing.G("currencyId");
            if (!existing.B("isActive") || existing.I("decimalPlaces") != decimals || existing.S("symbolPosition") != symbolPosition)
                await k.Ok(k.Put($"/api/currencies/{id}", new
                {
                    code, name, symbol, decimalPlaces = decimals, rounding = Rounding(decimals), symbolPosition, isActive = true
                }), $"update org currency {code}");
            return id;
        }

        var created = await k.Ok(k.Post("/api/currencies", new
        {
            code, name, symbol, decimalPlaces = decimals, rounding = Rounding(decimals), symbolPosition, isActive = true
        }), $"add org currency {code}");
        return created.G("currencyId");
    }

    private static decimal Rounding(int decimals) => decimals switch { 0 => 1m, 1 => 0.1m, 2 => 0.01m, _ => 0.001m };

    public static Task<Guid> PkrAsync(this SapKit k) => k.CurrencyAsync("PKR", "Pakistani Rupee", "Rs");
    public static Task<Guid> UsdAsync(this SapKit k) => k.CurrencyAsync("USD", "US Dollar", "$");
    public static Task<Guid> AedAsync(this SapKit k) => k.CurrencyAsync("AED", "UAE Dirham", "AED");
    public static Task<Guid> EurAsync(this SapKit k) => k.CurrencyAsync("EUR", "Euro", "€");
    public static Task<Guid> JpyAsync(this SapKit k) => k.CurrencyAsync("JPY", "Japanese Yen", "¥", 0);
    public static Task<Guid> BhdAsync(this SapKit k) => k.CurrencyAsync("BHD", "Bahraini Dinar", "BD", 3);
    public static Task<Guid> MxnAsync(this SapKit k) => k.CurrencyAsync("MXN", "Mexican Peso", "MX$");

    // ── Settings (api/organization/currency-settings, D-7) ──────────────────────

    public static Task<JsonElement> SettingsAsync(this SapKit k) =>
        k.Ok(k.Get("/api/organization/currency-settings"), "read currency settings");

    public static Task<Api> TrySetBasesAsync(this SapKit k, Guid sale, Guid purchase, Guid service, Guid? rateCurrency = null) =>
        k.Put("/api/organization/currency-settings", new
        {
            saleBaseCurrencyId = sale, purchaseBaseCurrencyId = purchase, serviceBaseCurrencyId = service, rateCurrencyId = rateCurrency,
            exchangeGainAccountCode = "7110", exchangeLossAccountCode = "7120",
            unrealizedGainAccountCode = "7130", unrealizedLossAccountCode = "7140"
        });

    public static async Task<JsonElement> SetBasesAsync(this SapKit k, Guid sale, Guid purchase, Guid service, Guid? rateCurrency = null) =>
        await k.Ok(k.TrySetBasesAsync(sale, purchase, service, rateCurrency), "save currency settings");

    // ── Rates (api/currency-rates, D-3) ──────────────────────────────────────────

    public static Task<Api> TryAddRateAsync(this SapKit k, Guid currency, decimal rate, DateTime from, DateTime? to = null) =>
        k.Post("/api/currency-rates", new
        {
            currencyId = currency, rate, effectiveFrom = Day(from), effectiveTo = to is { } t ? Day(t) : null, notes = "a35 qa"
        });

    /// <summary>Adds a rate; returns the new row (CurrencyRateModel).</summary>
    public static async Task<JsonElement> AddRateAsync(this SapKit k, Guid currency, decimal rate, DateTime from, DateTime? to = null) =>
        (await k.Ok(k.TryAddRateAsync(currency, rate, from, to), $"add rate {rate} from {Day(from)}")).P("rate");

    /// <summary>A historical correction of one row (PUT api/currency-rates/{id}).</summary>
    public static Task<Api> TryCorrectRateAsync(this SapKit k, JsonElement row, decimal rate) =>
        k.Put($"/api/currency-rates/{row.G("id")}", new
        {
            rate,
            effectiveFrom = row.S("effectiveFrom")![..10],
            effectiveTo = row.S("effectiveTo")![..10] == "9999-12-31" ? null : row.S("effectiveTo")![..10],
            notes = "a35 qa correction"
        });

    /// <summary>The row covering <paramref name="date"/> (GET api/currency-rates/{currencyId}?date=).</summary>
    public static Task<JsonElement> RateOnAsync(this SapKit k, Guid currency, DateTime date) =>
        k.Ok(k.Get($"/api/currency-rates/{currency}?date={Day(date)}"), $"rate on {Day(date)}");

    public static Task<Api> TryConvertAsync(this SapKit k, decimal amount, Guid from, Guid? to, DateTime? date = null, string? domain = null) =>
        k.Post("/api/currency/convert", new
        {
            amount, fromCurrencyId = from, toCurrencyId = to, date = date is { } d ? Day(d) : null, domain
        });

    // ── Partners ─────────────────────────────────────────────────────────────────

    public static async Task<Partner> CustomerAsync(this SapKit k, string label, Guid? defaultSaleCurrency)
    {
        var name = k.Next(label);
        var uuid = await k.Ok(k.Post("/api/partners", new
        {
            PartnerCode = $"C{Guid.NewGuid():N}"[..10], CompanyName = name, PartnerType = "CUSTOMER", IsCustomer = true, IsActive = true,
            DefaultSaleCurrencyId = defaultSaleCurrency
        }), "create customer");
        return new Partner(uuid.GetGuid(), name);
    }

    public static async Task<Partner> VendorAsync(this SapKit k, string label, Guid? defaultPurchaseCurrency)
    {
        var name = k.Next(label);
        var uuid = await k.Ok(k.Post("/api/suppliers", new
        {
            SupplierName = name, SupplierCode = $"V{Guid.NewGuid():N}"[..10], DefaultPurchaseCurrencyId = defaultPurchaseCurrency
        }), "create vendor");
        return new Partner(uuid.GetGuid(), name);
    }

    // ── Prices ───────────────────────────────────────────────────────────────────

    /// <summary>A selling price rule in <paramref name="currency"/> — a sale order in that currency takes it as is.</summary>
    public static async Task SellingPriceAsync(this SapKit k, Product p, decimal unitPrice, Guid currency) =>
        await k.Ok(k.Post("/api/pricing-rules", new
        {
            VariantUuid = p.VariantUuid, PriceType = "SELLING", UnitPrice = unitPrice, CurrencyId = currency, EffectiveFrom = Today.AddDays(-60)
        }), $"selling price {unitPrice}");

    /// <summary>A plain line: no tax code, no tax, no discount (amounts stay exactly qty × price).</summary>
    public static object Line(Product p, decimal qty) => new { VariantUuid = p.VariantUuid, Quantity = qty, DiscountPercent = 0m, TaxPercent = 0m };

    // ── Stock (PO → approve → send → GRN), the PO in the org's purchase base unless told ──

    public static Task<Api> TryCreatePoAsync(this SapKit k, Partner vendor, Warehouse wh, Guid? currency, params (Product Product, decimal Qty, decimal Price)[] lines) =>
        k.Post("/api/purchase-orders", new
        {
            SupplierId = vendor.Uuid, SupplierName = vendor.Name, CurrencyId = currency,
            Lines = lines.Select(l => new
            {
                VariantUuid = l.Product.VariantUuid, ItemDescription = l.Product.Name, Quantity = l.Qty, UnitPrice = l.Price,
                WarehouseId = wh.Uuid, WarehouseName = wh.Name, RequiresInspection = false
            }).ToArray()
        });

    public static async Task<Guid> CreatePoAsync(this SapKit k, Partner vendor, Warehouse wh, Guid? currency, params (Product Product, decimal Qty, decimal Price)[] lines) =>
        (await k.Ok(k.TryCreatePoAsync(vendor, wh, currency, lines), "create purchase order")).GetGuid();

    public static Task<JsonElement> PoAsync(this SapKit k, Guid po) => k.Ok(k.Get($"/api/purchase-orders/{po}"), "read PO");

    /// <summary>A world to sell from: approver placeholders, a warehouse, a vendor (no default currency) and stock of each product.</summary>
    public static async Task<(Warehouse Wh, Partner Vendor)> StockAsync(this SapKit k, params Product[] products)
    {
        await k.CreateApproverPlaceholdersAsync();
        var wh = await k.CreateWarehouseAsync();
        var vendor = await k.VendorAsync("Stock Vendor", null);
        if (products.Length > 0)
            await k.StockUpAsync(vendor, wh, products.Select(p => (p, 1000m, 1m)).ToArray());
        return (wh, vendor);
    }

    // ── Sale orders ──────────────────────────────────────────────────────────────

    public static async Task<Guid> SaleOrderAsync(this SapKit k, Partner customer, Guid? currency, params object[] lines) =>
        (await k.Ok(k.Post("/api/sale-orders", new { PartnerId = customer.Uuid, CurrencyId = currency, DeliveryMode = "SELF_PICKUP", Lines = lines }),
            "create sale order")).GetGuid();

    public static Task<Api> TryConfirmAsync(this SapKit k, Guid so) => k.Post($"/api/sale-orders/{so}/confirm");

    public static async Task<JsonElement> ConfirmAsync(this SapKit k, Guid so)
    {
        await k.Ok(k.TryConfirmAsync(so), "confirm sale order");
        var read = await k.GetSaleOrderAsync(so);
        read.S("status").Should().Be("CONFIRMED");
        return read;
    }

    // ── Quotations ───────────────────────────────────────────────────────────────

    /// <summary>A DRAFT quotation with one NORMAL line at an explicit price, in <paramref name="currency"/> (null = default).</summary>
    public static async Task<Guid> QuotationAsync(this SapKit k, Partner customer, Guid? currency, Product item, decimal qty, decimal price) =>
        (await k.Ok(k.Post("/api/sale-quotations", new
        {
            PartnerId = customer.Uuid, CurrencyId = currency, ValidFrom = Day(Today), ValidTo = Day(Today.AddDays(30)),
            Lines = new object[] { new { LineType = "NORMAL", VariantUuid = item.VariantUuid, Quantity = qty, UnitPrice = price, TaxPercent = 0m } }
        }), "create quotation")).GetGuid();

    public static Task<JsonElement> QuotationReadAsync(this SapKit k, Guid q) => k.Ok(k.Get($"/api/sale-quotations/{q}"), "read quotation");

    public static Task<Api> TrySendQuotationAsync(this SapKit k, Guid q) => k.Post($"/api/sale-quotations/{q}/send");

    /// <summary>Every offered line ACCEPTED, the quotation accepted and converted (SELF_PICKUP). Returns the sale order.</summary>
    public static async Task<Guid> AcceptAndConvertAsync(this SapKit k, Guid q)
    {
        foreach (var line in (await k.QuotationReadAsync(q)).A("lines"))
            await k.Ok(k.Patch($"/api/sale-quotations/{q}/lines/{line.G("uuid")}/customer-response", new { Response = "ACCEPTED" }), "accept line");
        await k.Ok(k.Post($"/api/sale-quotations/{q}/accept"), "accept quotation");
        return (await k.Ok(k.Post($"/api/sale-quotations/{q}/convert-to-order", new { DeliveryMode = "SELF_PICKUP" }), "convert to order")).GetGuid();
    }

    // ── Sales invoices + customer payments ───────────────────────────────────────

    /// <summary>Confirmed SO → delivered → invoice raised → issued. Returns the issued invoice.</summary>
    public static async Task<JsonElement> InvoiceAndIssueAsync(this SapKit k, Guid confirmedSo)
    {
        var delivery = await k.DeliverAsync(confirmedSo);
        var invoice = (await k.RaiseInvoiceAsync(delivery)).G("invoiceUuid");
        await k.IssueAsync(invoice);
        return await k.GetInvoiceAsync(invoice);
    }

    public static Task<Api> TryPayCustomerAsync(this SapKit k, Partner customer, decimal amount, string currencyCode, DateTime date,
        params (Guid Invoice, decimal Amount)[] allocations) =>
        k.Post("/api/customer-payments", new
        {
            PartnerId = customer.Uuid, Amount = amount, Method = "CASH", CurrencyCode = currencyCode, PaymentDate = date,
            Allocations = allocations.Length == 0 ? null : allocations.Select(a => new { InvoiceUuid = a.Invoice, a.Amount }).ToArray()
        });

    /// <summary>Records the payment; returns its detail (GET api/customer-payments/{uuid}).</summary>
    public static async Task<JsonElement> PayCustomerAsync(this SapKit k, Partner customer, decimal amount, string currencyCode, DateTime date,
        params (Guid Invoice, decimal Amount)[] allocations)
    {
        var recorded = await k.Ok(k.TryPayCustomerAsync(customer, amount, currencyCode, date, allocations), $"customer payment {amount} {currencyCode}");
        return await k.Ok(k.Get($"/api/customer-payments/{recorded.G("paymentUuid")}"), "read customer payment");
    }

    // ── Supplier invoices + payments ─────────────────────────────────────────────

    public static async Task<Guid> SupplierInvoiceAsync(this SapKit k, Partner vendor, string currencyCode, Guid? currencyId, decimal amount)
    {
        var uuid = (await k.Ok(k.Post("/api/finance/invoices", new
        {
            SupplierInvoiceNo = $"A35-{Guid.NewGuid():N}"[..16], SupplierId = vendor.Uuid, InvoiceDate = Today, ReceivedDate = Today,
            DueDate = Today.AddDays(30), Currency = currencyCode, CurrencyId = currencyId, Subtotal = amount, TaxAmount = 0m
        }), $"supplier invoice {amount} {currencyCode}")).GetGuid();
        return uuid;
    }

    public static Task<Api> TryApproveSupplierInvoiceAsync(this SapKit k, Guid invoice) =>
        k.Post($"/api/finance/invoices/{invoice}/approve", new { Notes = "a35 qa" });

    public static Task<JsonElement> SupplierInvoiceReadAsync(this SapKit k, Guid invoice) =>
        k.Ok(k.Get($"/api/finance/invoices/{invoice}"), "read supplier invoice");

    /// <summary>Draft → approve → post. Returns the posted payment's detail.</summary>
    public static async Task<JsonElement> PaySupplierAsync(this SapKit k, Partner vendor, Guid invoice, decimal amount, string currencyCode, DateTime date)
    {
        var p = (await k.Ok(k.Post("/api/supplier-payments", new
        {
            SupplierId = vendor.Uuid, SupplierName = vendor.Name, PaymentDate = date, PaymentMethod = "CASH", TotalAmount = amount,
            CurrencyCode = currencyCode, Lines = new[] { new { InvoiceUuid = invoice, AllocatedAmount = amount } }
        }), "draft supplier payment")).GetGuid();
        await k.Ok(k.Post($"/api/supplier-payments/{p}/approve"), "approve supplier payment");
        await k.Ok(k.Post($"/api/supplier-payments/{p}/post"), "post supplier payment");
        return await k.Ok(k.Get($"/api/supplier-payments/{p}"), "read supplier payment");
    }

    // ── Exchange differences (FIN, D-15) ─────────────────────────────────────────

    public static async Task<List<JsonElement>> DifferencesAsync(this SapKit k, string query) =>
        (await k.Ok(k.Get($"/api/finance/exchange-differences?pageSize=200&{query}"), $"exchange differences {query}")).A("data");

    /// <summary>The register rows of one document (by its uuid), optionally of one kind (REALIZED | UNREALIZED).</summary>
    public static async Task<List<JsonElement>> DifferencesOfAsync(this SapKit k, Guid documentUuid, string? kind = null) =>
        (await k.DifferencesAsync(kind is null ? "" : $"kind={kind}"))
            .Where(r => r.NG("documentUuid") == documentUuid).ToList();

    public static Task<Api> TryRevalueAsync(this SapKit k, DateTime date) =>
        k.Post("/api/finance/exchange-revaluation/run", new { revaluationDate = Day(date) });

    // ── asserts ──────────────────────────────────────────────────────────────────

    public static void ShouldBeMissingRate(this Api api, string code, DateTime date, string because)
    {
        api.Status.Should().Be(HttpStatusCode.BadRequest, $"{because} — {api}");
        api.Message.Should().Be(MissingRate(code, date), because);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using SMS.Shared.Common;

namespace SMS.Integration.Tests.SapAlignment;

/// <summary>One HTTP answer: the status, the parsed body (an empty object when there was none) and the raw text.</summary>
internal sealed record Api(HttpStatusCode Status, JsonElement Body, string Raw)
{
    public JsonElement Result => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("result", out var r) ? r : default;

    public string Message =>
        Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()!
            : string.Empty;

    public override string ToString() => $"HTTP {(int)Status}: {(Raw.Length > 1500 ? Raw[..1500] + "…" : Raw)}";
}

/// <summary>Case-insensitive, null-aware reads of the API's camelCase JSON — no dependency on the builders' DTO types.</summary>
internal static class J
{
    public static JsonElement P(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Expected an object holding '{name}', got {e.ValueKind}: {Short(e)}");
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        throw new KeyNotFoundException($"No property '{name}' in {Short(e)}");
    }

    public static bool Has(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.EnumerateObject().Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public static bool IsNull(this JsonElement e, string name) => e.P(name).ValueKind == JsonValueKind.Null;

    public static string? S(this JsonElement e, string name)
    {
        var v = e.P(name);
        return v.ValueKind == JsonValueKind.Null ? null : v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
    }

    public static decimal D(this JsonElement e, string name) => e.P(name).GetDecimal();

    public static decimal? ND(this JsonElement e, string name)
    {
        var v = e.P(name);
        return v.ValueKind == JsonValueKind.Null ? null : v.GetDecimal();
    }

    public static Guid G(this JsonElement e, string name) => e.P(name).GetGuid();

    public static Guid? NG(this JsonElement e, string name)
    {
        var v = e.P(name);
        return v.ValueKind == JsonValueKind.Null ? null : v.GetGuid();
    }

    public static bool B(this JsonElement e, string name) => e.P(name).GetBoolean();
    public static int I(this JsonElement e, string name) => e.P(name).GetInt32();

    public static List<JsonElement> A(this JsonElement e, string name) => e.P(name).EnumerateArray().ToList();
    public static List<JsonElement> Items(this JsonElement e) => e.EnumerateArray().ToList();

    public static string Short(JsonElement e)
    {
        var raw = e.ValueKind == JsonValueKind.Undefined ? "(undefined)" : e.GetRawText();
        return raw.Length > 800 ? raw[..800] + "…" : raw;
    }
}

internal sealed record Warehouse(Guid Uuid, string Name);
internal sealed record Product(int ProductId, Guid ProductUuid, Guid VariantUuid, string Name);
internal sealed record Partner(Guid Uuid, string Name);

/// <summary>
/// Everything the SAP-alignment journeys need to set up, done through the same endpoints a person would use:
/// currencies, the organization's base currency, partners, products, stock (PO → GRN with its receiving
/// check), sale orders, self-pickup deliveries, sales invoices. Bodies are anonymous objects and answers are
/// read as JSON, so these tests compile whatever the builders' C# models look like.
/// </summary>
internal sealed class SapKit
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public SapWebApplicationFactory F { get; }
    public HttpClient Admin { get; }
    public string Marker { get; }

    private int _seq;

    /// <param name="client">Whose session the kit acts in; the seeded (super) admin when omitted.</param>
    public SapKit(SapWebApplicationFactory factory, string markerPrefix, HttpClient? client = null)
    {
        F      = factory;
        Admin  = client ?? factory.CreateAdminClient();
        Marker = $"{markerPrefix}{Guid.NewGuid():N}"[..(markerPrefix.Length + 6)].ToUpperInvariant();
    }

    public string Next(string label) => $"{Marker} {label} {Interlocked.Increment(ref _seq)}";

    // ── HTTP ─────────────────────────────────────────────────────────────────────

    public Task<Api> Get(string url, HttpClient? client = null) => Send(HttpMethod.Get, url, null, client);
    public Task<Api> Post(string url, object? body = null, HttpClient? client = null) => Send(HttpMethod.Post, url, body, client);
    public Task<Api> Put(string url, object? body, HttpClient? client = null) => Send(HttpMethod.Put, url, body, client);
    public Task<Api> Patch(string url, object? body, HttpClient? client = null) => Send(HttpMethod.Patch, url, body, client);
    public Task<Api> Delete(string url, HttpClient? client = null) => Send(HttpMethod.Delete, url, null, client);

    public async Task<Api> Send(HttpMethod method, string url, object? body, HttpClient? client = null)
    {
        using var req = new HttpRequestMessage(method, url);
        if (body is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        else if (method == HttpMethod.Post || method == HttpMethod.Put || method == HttpMethod.Patch)
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var resp = await (client ?? Admin).SendAsync(req);
        var raw = await resp.Content.ReadAsStringAsync();
        JsonElement parsed;
        try
        {
            parsed = string.IsNullOrWhiteSpace(raw) ? JsonDocument.Parse("{}").RootElement.Clone() : JsonDocument.Parse(raw).RootElement.Clone();
        }
        catch (JsonException)
        {
            parsed = JsonDocument.Parse("{}").RootElement.Clone();
        }
        return new Api(resp.StatusCode, parsed, raw);
    }

    /// <summary>The call must succeed (200, success=true); returns its <c>result</c>.</summary>
    public async Task<JsonElement> Ok(Task<Api> call, string what)
    {
        var api = await call;
        api.Status.Should().Be(HttpStatusCode.OK, $"{what} should succeed — {api}");
        if (api.Body.ValueKind == JsonValueKind.Object && api.Body.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.False)
            throw new InvalidOperationException($"{what}: success=false — {api}");
        return api.Result;
    }

    // ── Reference data ───────────────────────────────────────────────────────────

    /// <summary>The currency with this ISO code from the global Lookups catalog, created when missing.</summary>
    public async Task<Guid> EnsureCurrencyAsync(string code, string name, string symbol)
    {
        Guid? id = null;
        var list = await Ok(Get("/api/lookups/currencies"), "list currencies");
        foreach (var c in list.Items())
            if (string.Equals(c.S("code")?.Trim(), code, StringComparison.OrdinalIgnoreCase))
                id = c.G("id");

        id ??= (await Ok(Post("/api/lookups/currencies", new { name, code, symbol }), $"create currency {code}")).GetGuid();
        await EnsureOrgCurrencyAsync(code, name, symbol);
        return id.Value;
    }

    /// <summary>
    /// A35 D-1: documents and rates use the organization's own currencies (finance.org_currencies) — the code is configured
    /// and active for the kit's organization through api/currencies (linked to the global catalog row).
    /// </summary>
    public async Task EnsureOrgCurrencyAsync(string code, string name, string symbol)
    {
        var mine = (await Ok(Get("/api/currencies?includeInactive=true"), "list org currencies")).Items()
            .FirstOrDefault(c => c.S("code") == code);
        if (mine.ValueKind == JsonValueKind.Object)
        {
            if (!mine.B("isActive"))
                await Ok(Put($"/api/currencies/{mine.G("currencyId")}", new { code, isActive = true }), $"activate org currency {code}");
            return;
        }
        await Ok(Post("/api/currencies", new { code, name, symbol }), $"add org currency {code}");
    }

    /// <summary>The organization's base currency, through the platform admin's organization edit (the seeded admin is super admin).</summary>
    public async Task SetBaseCurrencyAsync(Guid currencyId)
    {
        var org = await Ok(Get($"/api/system/organizations/{F.OrganizationId}"), "read the organization");
        var name = org.S("orgName") ?? throw new InvalidOperationException($"Organization has no orgName: {J.Short(org)}");

        await Ok(Put($"/api/system/organizations/{F.OrganizationId}", new
        {
            orgName      = name,
            contactEmail = org.S("contactEmail"),
            contactPhone = org.S("contactPhone"),
            address      = org.S("address"),
            country      = org.S("country"),
            timeZone     = org.S("timeZone"),
            baseCurrency = currencyId
        }), "set the organization's base currency");

        var rows = await F.QueryAsync("SELECT BaseCurrency FROM tenant.Organizations WHERE Id = @id", ("@id", F.OrganizationId));
        rows.Should().ContainSingle().Which["BaseCurrency"].Should().Be(currencyId, "the organization edit stores the base currency");
    }

    /// <summary>
    /// ROLE-type approval steps resolve their candidates at submit time and refuse when nobody holds the role,
    /// even though the seeded admin approves everything (VendorBackwardCompatibilityTests / ManufacturingCycleTests).
    /// </summary>
    public async Task CreateApproverPlaceholdersAsync()
    {
        foreach (var (role, tag) in new[]
                 {
                     ((int)EnumRole.ProcurementManager, "proc"), ((int)EnumRole.InventoryManager, "inv"),
                     ((int)EnumRole.FinanceOfficer, "fin"), ((int)EnumRole.WarehouseOperator, "wh")
                 })
            await CreateUserAsync(role, $"{tag}-{Guid.NewGuid():N}@sap-e2e.test");
    }

    public async Task CreateUserAsync(int roleId, string email)
    {
        var api = await Post("/api/users", new
        {
            FirstName = "SapE2E", LastName = "User", Email = email, RoleID = roleId, SupplierType = "INTERNAL"
        });
        api.Status.Should().Be(HttpStatusCode.Created, $"user create for role {roleId} — {api}");
    }

    /// <summary>A real login for a user in <paramref name="roleId"/>, for permission checks.</summary>
    public async Task<HttpClient> LoginAsNewUserAsync(int roleId, string tag)
    {
        var email    = $"{tag}-{Guid.NewGuid():N}@sap-e2e.test";
        const string password = "SapE2E@12345!";
        await CreateUserAsync(roleId, email);
        await F.SetPasswordAsync(email, password);
        return F.CreateBearerClient(await F.LoginAsync(email, password));
    }

    // ── Masters ──────────────────────────────────────────────────────────────────

    public async Task<Warehouse> CreateWarehouseAsync()
    {
        var code = $"W{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        var name = Next("Warehouse");
        await Ok(Post("/api/warehouses", new { Code = code, Name = name }), "create warehouse");
        var all = await Ok(Get("/api/warehouses"), "list warehouses");
        var row = all.Items().Single(w => w.S("code") == code);
        return new Warehouse(row.G("uuid"), name);
    }

    public async Task<Partner> CreateVendorAsync(string label)
    {
        var name = Next(label);
        var uuid = await Ok(Post("/api/suppliers", new { SupplierName = name, SupplierCode = $"V{Guid.NewGuid():N}"[..10] }), "create vendor");
        return new Partner(uuid.GetGuid(), name);
    }

    public async Task<Partner> CreateCustomerAsync(string label)
    {
        var name = Next(label);
        var uuid = await Ok(Post("/api/partners", new
        {
            PartnerCode = $"C{Guid.NewGuid():N}"[..10], CompanyName = name, PartnerType = "CUSTOMER", IsCustomer = true, IsActive = true
        }), "create customer");
        return new Partner(uuid.GetGuid(), name);
    }

    public async Task<Product> CreateProductAsync(string label, decimal purchasePrice, decimal? sellingPrice)
    {
        var name = Next(label);
        var created = await Ok(Post("/api/products", new
        {
            Name = name, UomCode = "PCS",
            Variants = new[]
            {
                new
                {
                    VariantName = "Default", PurchasePrice = purchasePrice, SellingPrice = sellingPrice, IsDefault = true,
                    IsAvailableForRetail = true, IsAvailableForMirMiv = true
                }
            }
        }), $"create product {name}");
        var id = created.I("id");
        var detail = await Ok(Get($"/api/products/{id}"), "read product");
        return new Product(id, detail.G("uuid"), detail.A("variants").Single().G("uuid"), name);
    }

    // ── Purchasing: PO → approve → send → GRN (receiving check) → approve ───────

    public async Task<Guid> CreatePurchaseOrderAsync(Partner vendor, Warehouse wh, params (Product Product, decimal Qty, decimal Price)[] lines)
    {
        var uuid = await Ok(Post("/api/purchase-orders", new
        {
            SupplierId = vendor.Uuid, SupplierName = vendor.Name,
            Lines = lines.Select(l => new
            {
                VariantUuid = l.Product.VariantUuid, ItemDescription = l.Product.Name, Quantity = l.Qty, UnitPrice = l.Price,
                WarehouseId = wh.Uuid, WarehouseName = wh.Name, RequiresInspection = false
            }).ToArray()
        }), "create purchase order");
        return uuid.GetGuid();
    }

    public async Task SubmitApproveAndSendPoAsync(Guid poUuid)
    {
        await Ok(Post($"/api/purchase-orders/{poUuid}/submit"), "PO submit");
        for (var i = 0; i < 6; i++)
        {
            await Ok(Post($"/api/purchase-orders/{poUuid}/approve"), "PO approve");
            if ((await Ok(Get($"/api/purchase-orders/{poUuid}"), "read PO")).S("status") == "APPROVED") break;
        }
        (await Ok(Get($"/api/purchase-orders/{poUuid}"), "read PO")).S("status").Should().Be("APPROVED");
        await Ok(Post($"/api/purchase-orders/{poUuid}/send", new { SupplierContactMobile = (string?)null }), "PO send");
    }

    /// <summary>
    /// Receives every PO line — in full, or <paramref name="qty"/> of each — records the receiving check (PASS) on
    /// each GRN line, submits and approves.
    /// </summary>
    public async Task<Guid> ReceiveAllAsync(Guid poUuid, Warehouse wh, decimal? qty = null)
    {
        var po = await Ok(Get($"/api/purchase-orders/{poUuid}"), "read PO");
        var poLines = po.A("lines");

        var grnUuid = (await Ok(Post("/api/grns", new
        {
            PoUuid = poUuid, WarehouseUuid = wh.Uuid, ReceivedAt = DateTime.UtcNow,
            Lines = poLines.Select(l => new
            {
                PoLineUuid = l.G("uuid"), QtyReceived = qty ?? l.D("quantity"), QtyAccepted = qty ?? l.D("quantity"), QtyRejected = 0m
            }).ToArray()
        }), "create GRN")).GetGuid();

        // The Warehouse "receiving check": every line needs a PASS/FAIL/PARTIAL before submit. The line PATCH
        // rewrites the whole line, so everything it holds is sent back as it is.
        var grn = await Ok(Get($"/api/grns/{grnUuid}"), "read GRN");
        foreach (var line in grn.A("lines"))
        {
            await Ok(Patch($"/api/grns/{grnUuid}/lines/{line.G("uuid")}", new
            {
                VariantUuid     = line.NG("variantUuid"),
                QtyReceived     = line.D("qtyReceived"),
                QtyAccepted     = line.D("qtyAccepted"),
                QtyRejected     = line.D("qtyRejected"),
                RejectionReason = line.S("rejectionReason"),
                BinUuid         = line.NG("binUuid"),
                BatchNumber     = line.S("batchNumber"),
                ExpiryDate      = line.IsNull("expiryDate") ? (DateTime?)null : line.P("expiryDate").GetDateTime(),
                UnitCost        = line.ND("unitCost"),
                QcResult        = "PASS"
            }), "record the receiving check");
        }

        await Ok(Post($"/api/grns/{grnUuid}/submit"), "GRN submit");
        await Ok(Post($"/api/grns/{grnUuid}/approve", new { Remarks = (string?)null }), "GRN approve");
        (await Ok(Get($"/api/grns/{grnUuid}"), "read GRN")).S("status").Should().Be("APPROVED");
        return grnUuid;
    }

    public async Task<(Guid Po, Guid Grn)> StockUpAsync(Partner vendor, Warehouse wh, params (Product Product, decimal Qty, decimal Price)[] lines)
    {
        var po = await CreatePurchaseOrderAsync(vendor, wh, lines);
        await SubmitApproveAndSendPoAsync(po);
        var grn = await ReceiveAllAsync(po, wh);
        return (po, grn);
    }

    // ── Finance setup ────────────────────────────────────────────────────────────

    public async Task<JsonElement> CreateTaxCodeAsync(string code, decimal rate, string usage, bool isDefault, string? name = null) =>
        await Ok(Post("/api/finance/tax-codes", new
        {
            code, name = name ?? $"{code} {rate}%", description = (string?)null, ratePercent = rate, usage, isDefault, isActive = true
        }), $"create tax code {code}");

    /// <summary>The code if this organization already has it (any state), else created as asked.</summary>
    public async Task<Guid> EnsureTaxCodeAsync(string code, decimal rate, string usage, bool isDefault)
    {
        var all = await Ok(Get("/api/finance/tax-codes?includeInactive=true"), "list tax codes");
        foreach (var c in all.Items())
            if (c.S("code") == code) return c.G("uuid");
        return (await CreateTaxCodeAsync(code, rate, usage, isDefault)).G("uuid");
    }

    /// <summary>
    /// A35 (D-2/D-3/D-17): a rate is "units of the organization's rate currency per 1 <paramref name="from"/>", entered through
    /// api/currency-rates (the legacy api/finance/exchange-rates table is frozen). <paramref name="to"/> must be the rate
    /// currency. Open-ended from <paramref name="effective"/> (the current row is closed the day before). Returns the new row
    /// (CurrencyRateModel: id, rate, effectiveFrom, effectiveTo, …).
    /// </summary>
    public async Task<JsonElement> CreateRateAsync(string from, string to, decimal rate, DateTime effective)
    {
        var settings = await Ok(Get("/api/organization/currency-settings"), "read currency settings");
        settings.S("rateCurrencyCode").Should().Be(to, $"rates are entered against the rate currency (A35 D-2), not {from}->{to}");
        var currency = (await Ok(Get("/api/currencies?includeInactive=true"), "list org currencies")).Items()
            .Single(c => c.S("code") == from).G("currencyId");
        return (await Ok(Post("/api/currency-rates", new
        {
            currencyId = currency, rate, effectiveFrom = effective.ToString("yyyy-MM-dd"), notes = "sap e2e"
        }), $"create rate {from}->{to}")).P("rate");
    }

    /// <summary>A35: a historical correction of one rate row (PUT api/currency-rates/{id}); its range is kept.</summary>
    public async Task<JsonElement> CorrectRateAsync(JsonElement row, decimal rate) =>
        await Ok(Put($"/api/currency-rates/{row.G("id")}", new
        {
            rate,
            effectiveFrom = row.S("effectiveFrom")![..10],
            effectiveTo   = row.S("effectiveTo")![..10] == "9999-12-31" ? null : row.S("effectiveTo")![..10],
            notes         = "corrected later"
        }), "correct a rate");

    // ── Selling: SO → confirm → self-pickup delivery → invoice → issue ──────────

    public static object Line(Product p, decimal qty, Guid? taxCode = null, decimal taxPercent = 0m, decimal discount = 0m) => new
    {
        VariantUuid = p.VariantUuid, Quantity = qty, DiscountPercent = discount, TaxPercent = taxPercent, TaxCodeUuid = taxCode
    };

    public Task<Api> TryCreateSaleOrderAsync(Partner customer, Guid currencyId, params object[] lines) =>
        Post("/api/sale-orders", new { PartnerId = customer.Uuid, CurrencyId = currencyId, DeliveryMode = "SELF_PICKUP", Lines = lines });

    public async Task<Guid> CreateSaleOrderAsync(Partner customer, Guid currencyId, params object[] lines) =>
        (await Ok(TryCreateSaleOrderAsync(customer, currencyId, lines), "create sale order")).GetGuid();

    public Task<JsonElement> GetSaleOrderAsync(Guid uuid) => Ok(Get($"/api/sale-orders/{uuid}"), "read sale order");

    public async Task ConfirmSaleOrderAsync(Guid uuid)
    {
        await Ok(Post($"/api/sale-orders/{uuid}/confirm"), "confirm sale order");
        var so = await GetSaleOrderAsync(uuid);
        so.S("status").Should().Be("CONFIRMED");
        so.A("lines").Should().OnlyContain(l => (l.ND("deficitQty") ?? 0m) == 0m, "the order was confirmed against stock on hand");
    }

    /// <summary>
    /// Delivers every outstanding line: the DRAFT delivery the confirm created (A33 D-1, a SELF_PICKUP order's
    /// PICK_ONLY route), or else one raised by hand, then release → pick → pack (unless the route packed it at
    /// confirm-pick, D-3) → customer pickup (DELIVERED).
    /// </summary>
    public async Task<Guid> DeliverAsync(Guid soUuid)
    {
        var drafts = (await Ok(Get($"/api/sale-orders/{soUuid}/deliveries"), "list the order's deliveries")).Items()
            .Where(d => d.S("status") == "DRAFT").ToList();
        drafts.Should().HaveCountLessThan(2, $"DeliverAsync handles one delivery per order — {string.Join(", ", drafts.Select(J.Short))}");
        var delivery = drafts.Count == 1
            ? drafts[0].G("uuid")
            : (await Ok(Post($"/api/sale-orders/{soUuid}/create-delivery", new { }), "create delivery")).GetGuid();

        await Ok(Post($"/api/logistics/deliveries/{delivery}/release", new { OnShortage = "BLOCK" }), "release delivery");

        var pickList = (await Ok(Post($"/api/logistics/deliveries/{delivery}/pick-list", new { Notes = "sap e2e" }), "generate pick list")).GetGuid();
        var pl = await Ok(Get($"/api/logistics/pick-lists/{pickList}"), "read pick list");
        var confirm = await Ok(Post($"/api/logistics/pick-lists/{pickList}/confirm", new
        {
            Lines = pl.A("lines").Select(l => new { LineUuid = l.G("uuid"), QtyPicked = l.D("qtyToPick") }).ToArray()
        }), "confirm picks");
        confirm.B("completed").Should().BeTrue($"every pick was answered — {J.Short(confirm)}");

        var detail = await Ok(Get($"/api/logistics/deliveries/{delivery}"), "read delivery");
        if (detail.S("status") == "PICKED")
            await Ok(Post($"/api/logistics/deliveries/{delivery}/packages", new
            {
                PackageType = "BOX",
                Contents = detail.A("lines").Where(l => l.D("qtyPicked") > 0).Select(l => new { DeliveryLineUuid = l.G("uuid"), Qty = l.D("qtyPicked") }).ToArray()
            }), "pack");

        await Ok(Post($"/api/logistics/deliveries/{delivery}/pickup", new
        {
            PickupPersonName = "E2E Collector", PickupPersonIdType = "CNIC", PickupPersonIdNumber = "35202-1234567-1"
        }), "record customer pickup");

        (await Ok(Get($"/api/logistics/deliveries/{delivery}"), "read delivery")).S("status").Should().Be("DELIVERED");
        return delivery;
    }

    public Task<Api> TryRaiseInvoiceAsync(Guid deliveryUuid) => Post("/api/sales-invoices", new { DeliveryUuid = deliveryUuid });

    public async Task<JsonElement> RaiseInvoiceAsync(Guid deliveryUuid) =>
        await Ok(TryRaiseInvoiceAsync(deliveryUuid), "raise sales invoice from delivery");

    public Task<JsonElement> GetInvoiceAsync(Guid uuid) => Ok(Get($"/api/sales-invoices/{uuid}"), "read sales invoice");

    public async Task<JsonElement> IssueAsync(Guid invoiceUuid) =>
        await Ok(Post($"/api/sales-invoices/{invoiceUuid}/issue"), "issue sales invoice");

    /// <summary>SO (confirmed) → delivered → invoice raised → issued. Returns the sale order, delivery and invoice ids.</summary>
    public async Task<(Guid So, Guid Delivery, Guid Invoice)> SellAsync(Partner customer, Guid currencyId, params object[] lines)
    {
        var so = await CreateSaleOrderAsync(customer, currencyId, lines);
        await ConfirmSaleOrderAsync(so);
        var delivery = await DeliverAsync(so);
        var invoice = (await RaiseInvoiceAsync(delivery)).G("invoiceUuid");
        await IssueAsync(invoice);
        return (so, delivery, invoice);
    }

    // ── Waiting for background (Hangfire) work ───────────────────────────────────

    public static async Task<T> WaitForAsync<T>(Func<Task<T>> fetch, Func<T, bool> ready, string what, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var value = await fetch();
            if (ready(value)) return value;
            if (DateTime.UtcNow >= deadline) throw new TimeoutException($"Timed out after {seconds}s waiting for: {what}");
            await Task.Delay(300);
        }
    }
}

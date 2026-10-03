using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;

namespace SMS.Integration.Tests.FulfillmentRoutes;

/// <summary>
/// A33 Phase F (QA): what the fulfillment-route E2E classes share on top of <see cref="SapKit"/> and
/// <c>PreOrder</c>. It covers routes, variant routes, SHIP / SELF_PICKUP sale orders with line routes, the
/// confirm result, an order's deliveries, and every warehouse step a delivery can take (release, pick, pack,
/// stage, approve, goods issue, record collection, and ship through a manual carrier's consignment plus a
/// proof of delivery).
/// Everything goes through the endpoints a person would use. Bodies are anonymous objects and answers are
/// read as JSON (docs/fulfillment-routes/API-CONTRACT.md), so the kit compiles whatever the builders' C#
/// models look like.
/// </summary>
internal static class Routes
{
    public const string PickOnly     = "PICK_ONLY";
    public const string PickAndShip  = "PICK_AND_SHIP";
    public const string PickPackShip = "PICK_PACK_SHIP";

    // ── Routes ───────────────────────────────────────────────────────────────────

    public static async Task<List<JsonElement>> RoutesAsync(this SapKit k, bool includeInactive = false, HttpClient? client = null) =>
        (await k.Ok(k.Get($"/api/fulfillment-routes?includeInactive={includeInactive.ToString().ToLowerInvariant()}", client), "list routes")).Items();

    /// <summary>The caller organization's route with this code, active or not.</summary>
    public static async Task<JsonElement> RouteAsync(this SapKit k, string code) =>
        (await k.RoutesAsync(includeInactive: true)).Single(r => r.S("code") == code);

    public static async Task<Guid> RouteUuidAsync(this SapKit k, string code) => (await k.RouteAsync(code)).G("uuid");

    /// <summary>Step requests in the given order, numbered 1..n, all mandatory.</summary>
    public static object[] Steps(params string[] codes) =>
        codes.Select((c, i) => (object)new { StepCode = c, StepOrder = i + 1, IsMandatory = true }).ToArray();

    public static Task<Api> TryCreateRouteAsync(this SapKit k, string code, string name, string[] steps, HttpClient? client = null) =>
        k.Post("/api/fulfillment-routes", new { Code = code, Name = name, Steps = Steps(steps) }, client);

    /// <summary>A custom route with a unique code (prefix + random suffix); returns the model.</summary>
    public static async Task<JsonElement> CreateRouteAsync(this SapKit k, string prefix, params string[] steps)
    {
        var code = $"{prefix}_{Guid.NewGuid():N}"[..Math.Min(30, prefix.Length + 7)].ToUpperInvariant();
        return await k.Ok(k.TryCreateRouteAsync(code, k.Next(prefix), steps), $"create route {code}");
    }

    public static Task<Api> RoutePatch(this SapKit k, Guid route, string action, HttpClient? client = null) =>
        k.Patch($"/api/fulfillment-routes/{route}/{action}", new { }, client);

    public static Task<Api> AssignVariantRoute(this SapKit k, Guid variant, Guid? route, HttpClient? client = null) =>
        k.Put($"/api/variants/{variant}/fulfillment-route", new { FulfillmentRouteUuid = route }, client);

    public static async Task SetVariantRouteAsync(this SapKit k, Product p, Guid? route) =>
        await k.Ok(k.AssignVariantRoute(p.VariantUuid, route), $"set the route of {p.Name}");

    // ── Sale order settings ──────────────────────────────────────────────────────

    private static readonly ConditionalWeakTable<SapKit, HttpClient> ConfigWriters = new();

    /// <summary>
    /// Reads the org's sale order settings, applies <paramref name="change"/> (camelCase keys) and PUTs them back
    /// whole. Saving takes SALE_ORDER_CONFIG_WRITE, which a system administrator deliberately lacks (only the Supply
    /// Dept Admin holds it), so it is done as a user of the kit's org holding exactly the two config codes.
    /// </summary>
    public static Task SetSaleOrderConfigAsync(this SapKit k, params (string Key, object? Value)[] change) =>
        k.SaveSaleOrderConfigAsync(change, omit: null);

    /// <summary>Saves the settings unchanged except that <paramref name="omit"/> is left out of the body, as a client older than that field would.</summary>
    public static Task SaveSaleOrderConfigOmittingAsync(this SapKit k, string omit) => k.SaveSaleOrderConfigAsync([], omit);

    public static async Task<JsonElement> SaleOrderConfigAsync(this SapKit k) =>
        await k.Ok(k.Get("/api/sale-order-config", await k.ConfigWriterAsync()), "read sale order settings");

    private static async Task<HttpClient> ConfigWriterAsync(this SapKit k)
    {
        if (!ConfigWriters.TryGetValue(k, out var writer))
        {
            writer = await k.LoginWithPermissionsAsync("cfg", "SALE_ORDER_CONFIG_READ", "SALE_ORDER_CONFIG_WRITE");
            ConfigWriters.AddOrUpdate(k, writer);
        }
        return writer;
    }

    private static async Task SaveSaleOrderConfigAsync(this SapKit k, (string Key, object? Value)[] change, string? omit)
    {
        var writer = await k.ConfigWriterAsync();
        var current = await k.Ok(k.Get("/api/sale-order-config", writer), "read sale order settings");
        var body = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in current.EnumerateObject()) body[p.Name] = p.Value.Clone();
        foreach (var (key, value) in change) body[key] = value;
        if (omit is not null) body.Remove(omit);
        await k.Ok(k.Put("/api/sale-order-config", body, writer), "save sale order settings");
    }

    // ── Sale orders ──────────────────────────────────────────────────────────────

    /// <summary>A shipping address for the customer, in the address book the order form uses.</summary>
    public static async Task<Guid> ShippingAddressAsync(this SapKit k, Partner customer, string city = "Lahore")
    {
        var address = await k.Ok(k.Post("/api/addresses", new
        {
            Line1 = $"{k.Next("Plot")} Industrial Estate", CityName = city, State = "Punjab", CountryName = "Pakistan",
            CountryIsoCode = "PK", ContactName = "Receiving Bay", ContactPhone = "+92-300-0000000", ConsigneeUuid = customer.Uuid
        }), "save a shipping address");
        return address.G("uuid");
    }

    /// <summary>A sale order line with an optional route override (null = inherit).</summary>
    public static object RLine(Product p, decimal qty, Guid? route = null, Guid? taxCode = null) => new
    {
        VariantUuid = p.VariantUuid, Quantity = qty, DiscountPercent = 0m, TaxPercent = 0m, TaxCodeUuid = taxCode,
        FulfillmentRouteUuid = route
    };

    public static Task<Api> TryCreateOrderAsync(
        this SapKit k, Partner customer, Guid currency, string mode, Guid? shippingAddress, params object[] lines) =>
        k.Post("/api/sale-orders", new
        {
            PartnerId = customer.Uuid, CurrencyId = currency, DeliveryMode = mode, ShippingAddressId = shippingAddress, Lines = lines
        });

    public static async Task<Guid> CreateOrderAsync(
        this SapKit k, Partner customer, Guid currency, string mode, Guid? shippingAddress, params object[] lines) =>
        (await k.Ok(k.TryCreateOrderAsync(customer, currency, mode, shippingAddress, lines), "create sale order")).GetGuid();

    public static Task<Api> TryConfirm(this SapKit k, Guid so, HttpClient? client = null) => k.Post($"/api/sale-orders/{so}/confirm", null, client);

    /// <summary>Confirms and returns the SaleOrderConfirmResultModel (status, deliveries, skippedLines, deliveryCreationFailed).</summary>
    public static async Task<JsonElement> ConfirmAsync(this SapKit k, Guid so)
    {
        var result = await k.Ok(k.TryConfirm(so), "confirm sale order");
        result.S("status").Should().Be("CONFIRMED", $"the confirm result names the new status — {J.Short(result)}");
        return result;
    }

    /// <summary>The order's deliveries, oldest first (GET api/sale-orders/{uuid}/deliveries).</summary>
    public static async Task<List<JsonElement>> SoDeliveriesAsync(this SapKit k, Guid so, HttpClient? client = null) =>
        (await k.Ok(k.Get($"/api/sale-orders/{so}/deliveries", client), "list the order's deliveries")).Items();

    /// <summary>The order's delivery carrying this route code (exactly one).</summary>
    public static async Task<Guid> SoDeliveryOnRouteAsync(this SapKit k, Guid so, string routeCode) =>
        (await k.SoDeliveriesAsync(so)).Single(d => d.S("fulfillmentRouteCode") == routeCode).G("uuid");

    // ── Deliveries ───────────────────────────────────────────────────────────────

    public static Task<JsonElement> DeliveryAsync(this SapKit k, Guid d) => k.Ok(k.Get($"/api/logistics/deliveries/{d}"), "read delivery");

    public static async Task<string> DeliveryStatusAsync(this SapKit k, Guid d) => (await k.DeliveryAsync(d)).S("status")!;

    /// <summary>The route tracker as "PICK:DONE,GOODS_ISSUE:CURRENT,COMPLETE:PENDING".</summary>
    public static string Tracker(this JsonElement delivery) =>
        string.Join(",", delivery.A("routeSteps").Select(s => $"{s.S("stepCode")}:{s.S("state")}"));

    public static List<string> NextActions(this JsonElement delivery) =>
        delivery.A("nextActions").Select(a => a.ValueKind == JsonValueKind.String ? a.GetString()! : a.GetRawText()).ToList();

    /// <summary>The actions that move a delivery forward along its route (HOLD / RESUME / CANCEL / SHORT_CLOSE are not).</summary>
    public static readonly HashSet<string> ForwardActions =
    [
        "RELEASE", "GENERATE_PICK_LIST", "CONFIRM_PICK", "PACK", "STAGE", "APPROVE", "GOODS_ISSUE", "CREATE_CONSIGNMENT", "RECORD_COLLECTION"
    ];

    /// <summary>
    /// The delivery screen's primary button is the first forward action in <c>nextActions</c>. This records a mismatch
    /// in <paramref name="log"/> unless the delivery is at <paramref name="status"/> and its forward actions are
    /// exactly <paramref name="forward"/>, in order (none when empty or null). Agreed with FLOW and REV: one forward
    /// action per status, except that a collected delivery (route without SHIP) also lists RECORD_COLLECTION
    /// second at PACKED / STAGED when no approval is pending. Mismatches are collected, not thrown, so one run lists
    /// every mismatch along a walk.
    /// </summary>
    public static async Task CheckNextAsync(this SapKit k, List<string> log, Guid d, string route, string status, params string[]? forward)
    {
        forward ??= [];
        var detail = await k.DeliveryAsync(d);
        var all = detail.NextActions();
        var fwd = all.Where(ForwardActions.Contains).ToList();
        if (detail.S("status") != status || !fwd.SequenceEqual(forward))
            log.Add($"{route} at {detail.S("status")} (expected {status}): nextActions [{string.Join(", ", all)}], expected forward actions [{string.Join(", ", forward)}]");
    }

    public static Task<Api> Release(this SapKit k, Guid d, HttpClient? client = null) =>
        k.Post($"/api/logistics/deliveries/{d}/release", new { OnShortage = "BLOCK" }, client);

    public static async Task ReleaseAsync(this SapKit k, Guid d) => await k.Ok(k.Release(d), "release delivery");

    /// <summary>Generates the pick list and confirms every line in full.</summary>
    public static async Task PickAllAsync(this SapKit k, Guid d, HttpClient? client = null) =>
        await k.ConfirmPickAsync(await k.GeneratePickListAsync(d, client), client);

    /// <summary>RELEASED → PICKING. Returns the pick list.</summary>
    public static async Task<Guid> GeneratePickListAsync(this SapKit k, Guid d, HttpClient? client = null) =>
        (await k.Ok(k.Post($"/api/logistics/deliveries/{d}/pick-list", new { Notes = "a33 qa" }, client), "generate pick list")).GetGuid();

    /// <summary>Confirms every pick-list line in full.</summary>
    public static async Task ConfirmPickAsync(this SapKit k, Guid pickList, HttpClient? client = null)
    {
        var pl = await k.Ok(k.Get($"/api/logistics/pick-lists/{pickList}", client), "read pick list");
        var confirm = await k.Ok(k.Post($"/api/logistics/pick-lists/{pickList}/confirm", new
        {
            Lines = pl.A("lines").Select(l => new { LineUuid = l.G("uuid"), QtyPicked = l.D("qtyToPick") }).ToArray()
        }, client), "confirm picks");
        confirm.B("completed").Should().BeTrue($"every pick was answered — {J.Short(confirm)}");
    }

    /// <summary>Packs everything picked into one handling unit of <paramref name="packageType"/>.</summary>
    public static async Task<Guid> PackAllAsync(this SapKit k, Guid d, string packageType = "BOX", HttpClient? client = null)
    {
        var detail = await k.Ok(k.Get($"/api/logistics/deliveries/{d}", client), "read delivery");
        return (await k.Ok(k.Post($"/api/logistics/deliveries/{d}/packages", new
        {
            PackageType = packageType,
            Contents = detail.A("lines").Where(l => l.D("qtyPicked") > l.D("qtyPacked"))
                .Select(l => new { DeliveryLineUuid = l.G("uuid"), Qty = l.D("qtyPicked") - l.D("qtyPacked") }).ToArray()
        }, client), "pack")).GetGuid();
    }

    /// <summary>The delivery's handling units (GET …/packages → packages[]).</summary>
    public static async Task<List<JsonElement>> PackagesAsync(this SapKit k, Guid d)
    {
        var packing = await k.Ok(k.Get($"/api/logistics/deliveries/{d}/packages"), "read packages");
        return packing.A("packages");
    }

    public static Task<Api> Stage(this SapKit k, Guid d, HttpClient? client = null) => k.Post($"/api/logistics/deliveries/{d}/stage", null, client);
    public static Task<Api> GoodsIssue(this SapKit k, Guid d, HttpClient? client = null) => k.Post($"/api/logistics/deliveries/{d}/goods-issue", null, client);
    public static Task<Api> Approve(this SapKit k, Guid d, HttpClient? client = null) => k.Post($"/api/logistics/deliveries/{d}/approve", null, client);
    public static Task<Api> Advance(this SapKit k, Guid d, string? expectedStatus = null, HttpClient? client = null) =>
        k.Post($"/api/logistics/deliveries/{d}/advance", new { ExpectedStatus = expectedStatus }, client);

    public static Task<Api> RecordCollection(this SapKit k, Guid d, HttpClient? client = null) =>
        k.Post($"/api/logistics/deliveries/{d}/pickup", new
        {
            PickupPersonName = "A33 Collector", PickupPersonIdType = "CNIC", PickupPersonIdNumber = "35202-7654321-1"
        }, client);

    /// <summary>A carrier with no API integration (manual), for consignments booked by hand.</summary>
    public static async Task<Guid> ManualCarrierAsync(this SapKit k) =>
        (await k.Ok(k.Post("/api/logistics/carriers", new
        {
            Name = k.Next("Carrier"), Code = $"C{Guid.NewGuid():N}"[..8].ToUpperInvariant(), ServiceType = "COURIER"
        }), "create carrier")).GetGuid();

    public static Task<Api> TryCreateConsignment(this SapKit k, Guid carrier, Guid delivery, HttpClient? client = null) =>
        k.Post("/api/logistics/consignments", new { CarrierUuid = carrier, Mode = "COURIER", DeliveryUuids = new[] { delivery } }, client);

    /// <summary>
    /// The SHIP step: a consignment for the delivery on a manual carrier → booked by hand (AWB) → proof of delivery.
    /// The proof marks the consignment DELIVERED and carries the delivery to DELIVERED. Returns the consignment.
    /// </summary>
    public static async Task<Guid> ShipAndProveAsync(this SapKit k, Guid delivery, Guid carrier)
    {
        var consignment = (await k.Ok(k.TryCreateConsignment(carrier, delivery), "create consignment")).GetGuid();
        await k.Ok(k.Post($"/api/logistics/consignments/{consignment}/book-manual", new { Awb = $"AWB{Guid.NewGuid():N}"[..14].ToUpperInvariant() }), "book manually");
        await k.Ok(k.Post($"/api/logistics/delivery-proofs/consignment/{consignment}", new { ReceivedBy = "Customer Receiving", Relationship = "Storekeeper" }), "record proof of delivery");
        return consignment;
    }

    /// <summary>A delivered delivery invoices: raise from the delivery (DRAFT invoice) and issue it.</summary>
    public static async Task<Guid> InvoiceAsync(this SapKit k, Guid delivery)
    {
        var invoice = (await k.RaiseInvoiceAsync(delivery)).G("invoiceUuid");
        await k.IssueAsync(invoice);
        return invoice;
    }

    public static async Task<decimal> SoLedgerHeldAsync(this SapKit k, Guid so)
    {
        var rows = await k.F.QueryAsync(
            "SELECT ISNULL(SUM(ReservedQty), 0) AS Q FROM inventory.StockReservations WHERE SourceType = 'SALES_ORDER' AND SourceUuid = @so AND Status = 'ACTIVE'",
            ("@so", so));
        return Convert.ToDecimal(rows[0]["Q"]);
    }

    public static async Task<decimal> DeliveryLedgerHeldAsync(this SapKit k, Guid delivery)
    {
        var rows = await k.F.QueryAsync(
            "SELECT ISNULL(SUM(ReservedQty), 0) AS Q FROM inventory.StockReservations WHERE SourceType = 'DELIVERY' AND SourceUuid = @d AND Status = 'ACTIVE'",
            ("@d", delivery));
        return Convert.ToDecimal(rows[0]["Q"]);
    }

    public static void ShouldBe(this Api api, HttpStatusCode status, string because) =>
        api.Status.Should().Be(status, $"{because} — {api}");
}

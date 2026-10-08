using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SMS.Integration.Tests.FulfillmentRoutes;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using SMS.Shared.Common;

namespace SMS.Integration.Tests.RouteClassification;

/// <summary>
/// One make-to-order world: a warehouse that is also the production warehouse, a vendor that is the raw material's
/// preferred supplier, a raw material (PURCHASE) and a finished good (MANUFACTURE, BOM = <see cref="PerUnit"/> raw per
/// unit, default production warehouse = <see cref="Wh"/>) whose variant carries a MANUFACTURE route.
/// </summary>
internal sealed record MtoWorld(
    Guid Pkr, Warehouse Wh, int WhId, Partner Vendor, Partner Customer, Guid Address,
    Product Raw, Product Fg, Guid Bom, decimal PerUnit, Guid Route, string RouteCode);

/// <summary>
/// A34 Phase F (QA): what the route-classification E2E classes share on top of <see cref="SapKit"/>,
/// <c>PreOrder</c> and A33's <see cref="Routes"/>. It covers route categories, manufactured products and their BOMs,
/// make-to-order sale orders, production orders walked through the floor (issue → start → output → complete → QI →
/// FGR), the deliveries production hands over, lead-time endpoints, the two A34 sweeps, and the rows only SQL can
/// show (pending flags, allocation demands, notifications).
/// Bodies are anonymous objects and answers are read as JSON (docs/route-classification/API-CONTRACT.md v1.0), so the
/// kit compiles whatever the builders' C# models look like.
/// </summary>
internal static class Rc
{
    public const string MfgPickShip     = "MFG_PICK_SHIP";
    public const string MfgPickPackShip = "MFG_PICK_PACK_SHIP";
    public const string Stock           = "STOCK";
    public const string Manufacture     = "MANUFACTURE";

    /// <summary>The A33 confirm skip reason for a make-to-order line (contract §6.4 / §8.2).</summary>
    public static string MtoSkipReason(int lineNumber) =>
        $"Line {lineNumber} is made to order: its delivery is created when its production order completes.";

    // ── Routes ───────────────────────────────────────────────────────────────────

    public static Task<Api> TryCreateCategoryRoute(this SapKit k, string code, string name, string? category, string[] steps, HttpClient? client = null) =>
        k.Post("/api/fulfillment-routes", new { Code = code, Name = name, RouteCategory = category, Steps = Routes.Steps(steps) }, client);

    /// <summary>A custom route with a unique code and the given category (null = omitted → STOCK).</summary>
    public static async Task<JsonElement> CreateCategoryRouteAsync(this SapKit k, string prefix, string? category, params string[] steps)
    {
        var code = $"{prefix}_{Guid.NewGuid():N}"[..Math.Min(30, prefix.Length + 7)].ToUpperInvariant();
        return await k.Ok(k.TryCreateCategoryRoute(code, k.Next(prefix), category, steps), $"create route {code}");
    }

    /// <summary>
    /// PUT the route back as it is (name, description, order; steps omitted = untouched), with
    /// <paramref name="category"/> (null = omitted = unchanged).
    /// </summary>
    public static Task<Api> TryUpdateRouteCategory(this SapKit k, JsonElement route, string? category, HttpClient? client = null) =>
        k.Put($"/api/fulfillment-routes/{route.G("uuid")}", new
        {
            Name = route.S("name"), Description = route.Has("description") ? route.S("description") : null,
            DisplayOrder = route.I("displayOrder"), RouteCategory = category
        }, client);

    public static async Task<JsonElement> RouteByUuidAsync(this SapKit k, Guid route) =>
        await k.Ok(k.Get($"/api/fulfillment-routes/{route}"), "read route");

    // ── Warehouses, categories ───────────────────────────────────────────────────

    /// <summary>The int id of a warehouse (products name their default production warehouse by id).</summary>
    public static async Task<int> WarehouseIdAsync(this SapKit k, Warehouse wh) =>
        (await k.Ok(k.Get("/api/warehouses"), "list warehouses")).Items().Single(w => w.G("uuid") == wh.Uuid).I("id");

    public static async Task<int> CreateProductCategoryAsync(this SapKit k, string label) =>
        (await k.Ok(k.Post("/api/product-categories", new { Name = k.Next(label), Code = $"C{Guid.NewGuid():N}"[..8].ToUpperInvariant() }), "create product category")).GetInt32();

    // ── Products ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A product with an A30 classification. A raw material is purchased and not sold; a finished good is made
    /// (MANUFACTURE) and sold. Its one variant is open to retail, production and MIR/MIV.
    /// </summary>
    public static async Task<Product> CreateClassifiedProductAsync(
        this SapKit k, string label, string productType, string supplyMethod, decimal purchasePrice, decimal? sellingPrice,
        int? productionWarehouseId = null, int? leadTimeDays = null, int? categoryId = null)
    {
        var name = k.Next(label);
        var created = await k.Ok(k.Post("/api/products", new
        {
            Name = name, UomCode = "PCS", ProductType = productType, SupplyMethod = supplyMethod,
            DefaultProductionWarehouseId = productionWarehouseId, LeadTimeDays = leadTimeDays, CategoryId = categoryId,
            Variants = new[]
            {
                new
                {
                    VariantName = "Default", PurchasePrice = purchasePrice, SellingPrice = sellingPrice, IsDefault = true,
                    IsAvailableForRetail = true, IsAvailableForMirMiv = true,
                    // A stock item or an asset can't be a production input (A30 §6.1), so it can't be offered to production.
                    IsAvailableForProduction = ProductTypeRules.For(productType).CanBeBomInput
                }
            }
        }), $"create product {name}");
        var id = created.I("id");
        var detail = await k.Ok(k.Get($"/api/products/{id}"), "read product");
        return new Product(id, detail.G("uuid"), detail.A("variants").Single().G("uuid"), name);
    }

    public static Task<Product> RawMaterialAsync(this SapKit k, string label, decimal price = 5m, int? leadTimeDays = null) =>
        k.CreateClassifiedProductAsync(label, ProductType.RawMaterial, SupplyMethod.Purchase, price, null, leadTimeDays: leadTimeDays);

    public static Task<Product> FinishedGoodAsync(this SapKit k, string label, int? productionWarehouseId, decimal selling = 100m, int? leadTimeDays = null, int? categoryId = null) =>
        k.CreateClassifiedProductAsync(label, ProductType.FinishedGood, SupplyMethod.Manufacture, 0m, selling, productionWarehouseId, leadTimeDays, categoryId);

    public static Task<Product> SemiFinishedAsync(this SapKit k, string label, int? productionWarehouseId, int? leadTimeDays = null) =>
        k.CreateClassifiedProductAsync(label, ProductType.SemiFinished, SupplyMethod.Manufacture, 0m, null, productionWarehouseId, leadTimeDays);

    public static Task<JsonElement> ProductDetailAsync(this SapKit k, Product p, HttpClient? client = null) =>
        k.Ok(k.Get($"/api/products/{p.ProductId}", client), "read product");

    public static async Task<JsonElement> VariantAsync(this SapKit k, Product p) =>
        (await k.ProductDetailAsync(p)).A("variants").Single(v => v.G("uuid") == p.VariantUuid);

    /// <summary>A rate card for the vendor marked preferred (the only way a variant's default supplier is set).</summary>
    public static async Task<Guid> PreferredSupplierAsync(this SapKit k, Product p, Partner vendor, decimal cost, Guid currency, int? leadTimeDays = null)
    {
        var card = (await k.Ok(k.Post("/api/rate-cards", new
        {
            VariantUuid = p.VariantUuid, SupplierUuid = vendor.Uuid, VendorUnitCost = cost, LeadTimeDays = leadTimeDays,
            EffectiveFrom = PreOrder.Day(PreOrder.Today), CurrencyId = currency
        }), "rate card")).GetGuid();
        await k.Ok(k.Patch($"/api/rate-cards/{card}/preferred", null), "preferred supplier");
        return card;
    }

    // ── People ───────────────────────────────────────────────────────────────────

    private static readonly ConditionalWeakTable<SapKit, HttpClient> Inspectors = new();

    /// <summary>
    /// A second real login (Inventory Manager) in the kit's organization: BOM approval is four-eyes and QI must not be
    /// done by the production order's creator (A30), and the confirming admin creates the make-to-order POs.
    /// </summary>
    public static async Task<HttpClient> InspectorAsync(this SapKit k)
    {
        if (!Inspectors.TryGetValue(k, out var c))
        {
            c = await k.LoginAsNewUserAsync((int)EnumRole.InventoryManager, "a34insp");
            Inspectors.AddOrUpdate(k, c);
        }
        return c;
    }

    // ── BOMs ─────────────────────────────────────────────────────────────────────

    public static async Task<Guid> CreateActiveBomAsync(this SapKit k, Product output, decimal baseQty, params (Product Input, decimal Qty)[] lines)
    {
        var bom = await k.CreateDraftBomAsync(output, baseQty, lines);
        await k.Ok(k.Post($"/api/boms/{bom}/submit"), "BOM submit");
        var approver = await k.InspectorAsync();
        await k.Ok(k.Post($"/api/boms/{bom}/approve", null, approver), "BOM approve");
        await k.Ok(k.Post($"/api/boms/{bom}/activate", null, approver), "BOM activate");
        return bom;
    }

    public static async Task<Guid> CreateDraftBomAsync(this SapKit k, Product output, decimal baseQty, params (Product Input, decimal Qty)[] lines) =>
        (await k.Ok(k.TryCreateBom(output, baseQty, lines), "create BOM")).GetGuid();

    public static Task<Api> TryCreateBom(this SapKit k, Product output, decimal baseQty, params (Product Input, decimal Qty)[] lines) =>
        k.Post("/api/boms", new
        {
            ProductUuid = output.ProductUuid, BaseQuantity = baseQty,
            Lines = lines.Select(l => new { MaterialVariantUuid = l.Input.VariantUuid, Quantity = l.Qty, IsCritical = true }).ToArray()
        });

    // ── A make-to-order world ────────────────────────────────────────────────────

    /// <summary>
    /// Base currency, tax codes, approver placeholders, a warehouse (also the production warehouse), a vendor, a
    /// customer with a shipping address, a raw material (preferred supplier = the vendor) with <paramref name="rawStock"/>
    /// in stock, and a finished good whose BOM takes <paramref name="perUnit"/> raw per unit and whose variant carries the
    /// MANUFACTURE seed <paramref name="routeCode"/>.
    /// </summary>
    /// <param name="baseCurrency">
    /// The organization's base currency, already set (a second organization's kit can't edit the organization; the root
    /// kit sets it with <c>SetOrgBaseCurrencyAsync</c>). Null = PKR set as the main organization's base here.
    /// </param>
    public static async Task<MtoWorld> MtoWorldAsync(this SapKit k, string label, decimal rawStock, decimal perUnit = 2m, string routeCode = MfgPickShip,
        Guid? baseCurrency = null)
    {
        var pkr = baseCurrency ?? await k.PkrBaseAsync();
        await k.EnsureTaxCodeAsync("GST17", 17m, "SALES", isDefault: true);
        await k.EnsureTaxCodeAsync("PGST17", 17m, "PURCHASE", isDefault: true);
        await k.CreateApproverPlaceholdersAsync();
        var wh = await k.CreateWarehouseAsync();
        var whId = await k.WarehouseIdAsync(wh);
        var vendor = await k.CreateVendorAsync($"{label} Vendor");
        var customer = await k.CreateCustomerAsync($"{label} Customer");
        var address = await k.ShippingAddressAsync(customer);

        var raw = await k.RawMaterialAsync($"{label} Raw");
        await k.PreferredSupplierAsync(raw, vendor, 5m, pkr);
        if (rawStock > 0) await k.StockUpAsync(vendor, wh, (raw, rawStock, 5m));

        var fg = await k.FinishedGoodAsync($"{label} FG", whId);
        var bom = await k.CreateActiveBomAsync(fg, 1m, (raw, perUnit));
        var route = await k.RouteUuidAsync(routeCode);
        await k.SetVariantRouteAsync(fg, route);
        return new MtoWorld(pkr, wh, whId, vendor, customer, address, raw, fg, bom, perUnit, route, routeCode);
    }

    // ── Sale orders ──────────────────────────────────────────────────────────────

    public static JsonElement LineOf(this JsonElement so, Product p) => so.A("lines").Single(l => l.G("variantUuid") == p.VariantUuid);

    public static List<(int? Line, string? Code)> BlockerCodes(this JsonElement doc, string name = "confirmBlockers") =>
        doc.A(name).Select(b => (b.P("lineNumber").ValueKind == JsonValueKind.Null ? (int?)null : b.I("lineNumber"), b.S("code"))).ToList();

    public static Task<Api> TryCreateProductionOrders(this SapKit k, Guid so, HttpClient? client = null) =>
        k.Post($"/api/sale-orders/{so}/create-production-orders", null, client);

    public static Task<Api> TryCancelOrder(this SapKit k, Guid so, string reason = "a34 qa", HttpClient? client = null) =>
        k.Post($"/api/sale-orders/{so}/cancel", new { Reason = reason }, client);

    public static async Task<List<string>> SoTimelineEventsAsync(this SapKit k, Guid so) =>
        (await k.Ok(k.Get($"/api/sale-orders/{so}/timeline"), "read SO timeline")).A("events").Select(e => e.S("eventType")!).ToList();

    public static async Task<DateTime?> ProductionPendingSinceAsync(this SapKit k, Guid so) =>
        (await k.F.QueryAsync("SELECT ProductionCreationPendingSince AS P FROM demand.sale_orders WHERE UUID = @so", ("@so", so))).Single()["P"] as DateTime?;

    public static Task BackdateProductionPendingAsync(this SapKit k, Guid so) =>
        k.F.ExecuteAsync("UPDATE demand.sale_orders SET ProductionCreationPendingSince = DATEADD(HOUR, -1, SYSUTCDATETIME()) WHERE UUID = @so", ("@so", so));

    /// <summary>Open (or all) SALES_ORDER allocation demands of the order.</summary>
    public static async Task<List<Dictionary<string, object?>>> SoDemandsAsync(this SapKit k, Guid so) =>
        await k.F.QueryAsync(
            "SELECT DemandLineUuid, RequiredQty, Status, Priority, RequiredDate FROM inventory.AllocationDemands WHERE DemandType = 'SALES_ORDER' AND DemandUuid = @so",
            ("@so", so));

    // ── Production orders ────────────────────────────────────────────────────────

    public static Task<JsonElement> ProductionOrderAsync(this SapKit k, Guid po, HttpClient? client = null) =>
        k.Ok(k.Get($"/api/production-orders/{po}", client), "read production order");

    public static async Task<string> PoStatusAsync(this SapKit k, Guid po) => (await k.ProductionOrderAsync(po)).S("status")!;

    /// <summary>Every production order whose source is this sale order (A31 reverse navigation), oldest first.</summary>
    public static async Task<List<JsonElement>> ProductionOrdersOfAsync(this SapKit k, Guid so) =>
        (await k.Ok(k.Get($"/api/production-orders?sourceUuid={so}&pageSize=100"), "list the order's production orders"))
        .A("data").OrderBy(p => p.P("createdAt").GetDateTime()).ToList();

    public static async Task<List<JsonElement>> ProductionOrdersOfProductAsync(this SapKit k, Product p) =>
        (await k.Ok(k.Get($"/api/production-orders?productUuid={p.ProductUuid}&pageSize=100"), "list the product's production orders")).A("data");

    public static async Task<List<JsonElement>> MaterialsAsync(this SapKit k, Guid po) =>
        (await k.Ok(k.Get($"/api/production-orders/{po}/materials"), "read PO materials")).Items();

    /// <summary>Planning ran: PLANNED or later, with its material requirements (BR-C5-04 / T-C5-05).</summary>
    public static readonly string[] PlannedOrLater = ["PLANNED", "MATERIAL_PENDING", "READY", "IN_PROGRESS", "QUALITY_INSPECTION", "COMPLETED", "CLOSED"];

    /// <summary>Runs the order-scoped allocation (A31 C10) until the order is READY.</summary>
    public static async Task ReadyAsync(this SapKit k, Guid po)
    {
        if (await k.PoStatusAsync(po) == "READY") return;
        await k.Ok(k.Post($"/api/production-orders/{po}/run-allocation"), "run allocation for the production order");
        await SapKit.WaitForAsync(() => k.PoStatusAsync(po), s => s == "READY", $"production order {po} READY", 20);
    }

    /// <summary>Issues every outstanding material, then starts the order (→ IN_PROGRESS).</summary>
    public static async Task IssueAndStartAsync(this SapKit k, Guid po)
    {
        await k.ReadyAsync(po);
        var lines = (await k.MaterialsAsync(po)).Where(m => m.D("outstanding") > 0)
            .Select(m => new { RequirementUuid = m.G("uuid"), Quantity = m.D("outstanding") }).ToArray();
        if (lines.Length > 0)
            await k.Ok(k.Post($"/api/production-orders/{po}/issues", new { IssueType = "STANDARD", Lines = lines, Confirm = true }), "issue materials");
        await k.Ok(k.Post($"/api/production-orders/{po}/start"), "start production");
    }

    /// <summary>Reports the output and completes (→ QUALITY_INSPECTION).</summary>
    public static async Task ReportAndCompleteAsync(this SapKit k, Guid po, decimal produced)
    {
        await k.Ok(k.Post($"/api/production-orders/{po}/report-output", new { Quantity = produced }), "report output");
        await k.Ok(k.Post($"/api/production-orders/{po}/complete"), "complete production");
    }

    /// <summary>QI by the inspector (not the PO's creator): <paramref name="accepted"/> PASS, <paramref name="rejected"/> FAIL.</summary>
    public static Task<Api> TryInspect(this SapKit k, Guid po, HttpClient inspector, decimal accepted, decimal rejected)
    {
        var lines = new List<object>();
        if (accepted > 0) lines.Add(new { CheckName = "Visual", Result = "PASS", QuantityChecked = accepted });
        if (rejected > 0) lines.Add(new { CheckName = "Visual", Result = "FAIL", QuantityChecked = rejected });
        return k.Post($"/api/production-orders/{po}/quality-inspections", new { Lines = lines }, inspector);
    }

    public static Task<Api> TryFgr(this SapKit k, Guid po, decimal qty, HttpClient? client = null) =>
        k.Post($"/api/production-orders/{po}/fgr", new { Quantity = qty, Confirm = true }, client);

    /// <summary>issue → start → output → complete → QI → FGR of everything accepted (none when 0 accepted).</summary>
    public static async Task FloorToFgrAsync(this SapKit k, Guid po, decimal produced, decimal accepted, decimal rejected)
    {
        await k.IssueAndStartAsync(po);
        await k.ReportAndCompleteAsync(po, produced);
        await k.Ok(k.TryInspect(po, await k.InspectorAsync(), accepted, rejected), "quality inspection");
        if (accepted > 0) await k.Ok(k.TryFgr(po, accepted), "finished goods receipt");
    }

    /// <summary>
    /// Free finished goods: a standalone production order (no source) for <paramref name="qty"/> of the world's finished
    /// good, planned, walked through the floor and received in full. Returns the production order.
    /// </summary>
    public static async Task<Guid> ProduceToStockAsync(this SapKit k, MtoWorld w, decimal qty)
    {
        var po = (await k.Ok(k.Post("/api/production-orders", new
        {
            ProductUuid = w.Fg.ProductUuid, PlannedQuantity = qty, WarehouseUuid = w.Wh.Uuid,
            RequiredDate = PreOrder.Day(PreOrder.Today.AddDays(7)), Plan = true
        }), "standalone production order")).GetGuid();
        await k.FloorToFgrAsync(po, qty, qty, 0);
        (await k.PoStatusAsync(po)).Should().Be("COMPLETED");
        return po;
    }

    public static Task<Api> TryCreateDeliveryNow(this SapKit k, Guid po, HttpClient? client = null) =>
        k.Post($"/api/production-orders/{po}/create-delivery", null, client);

    public static async Task<DateTime?> DeliveryPendingSinceAsync(this SapKit k, Guid po) =>
        (await k.F.QueryAsync("SELECT DeliveryCreationPendingSince AS P FROM material.production_orders WHERE UUID = @po", ("@po", po))).Single()["P"] as DateTime?;

    /// <summary>Marks the hand-over pending an hour ago (whether or not it was), so the D-20 sweep picks it up.</summary>
    public static Task BackdateDeliveryPendingAsync(this SapKit k, Guid po) =>
        k.F.ExecuteAsync("UPDATE material.production_orders SET DeliveryCreationPendingSince = DATEADD(HOUR, -1, SYSUTCDATETIME()) WHERE UUID = @po", ("@po", po));

    /// <summary>Moves a pending hand-over's flag an hour back so the sweep (≥ 10 min) takes it now; leaves a clear flag clear.</summary>
    public static Task BackdateDeliveryPendingIfSetAsync(this SapKit k, Guid po) =>
        k.F.ExecuteAsync("UPDATE material.production_orders SET DeliveryCreationPendingSince = DATEADD(HOUR, -1, SYSUTCDATETIME()) " +
                         "WHERE UUID = @po AND DeliveryCreationPendingSince IS NOT NULL", ("@po", po));

    // ── Purchasing the shortages a plan raised ───────────────────────────────────

    /// <summary>
    /// The purchase orders the plan raised for the order's PURCHASE shortages (Supply Requirement Engine): each is
    /// submitted, approved, sent and received in full into its line warehouse (GRN with the receiving check), then the
    /// production order's own allocation runs (A31 C10). Returns the purchase order uuids.
    /// </summary>
    public static async Task<List<Guid>> BuyShortagesAsync(this SapKit k, Guid po, Warehouse wh)
    {
        var srs = await SapKit.WaitForAsync(
            async () => (await k.Ok(k.Get($"/api/production-orders/{po}/supply-requirements"), "supply requirements")).Items(),
            list => list.Count > 0 && list.All(s => s.S("supplyMethod") != "PURCHASE" || !s.IsNull("supplySourceUuid")),
            "the plan's purchase supply requirements, each acted on", 30);
        var purchases = srs.Where(s => s.S("supplyMethod") == "PURCHASE").Select(s => s.G("supplySourceUuid")).Distinct().ToList();
        foreach (var purchase in purchases)
        {
            await k.SubmitApproveAndSendPoAsync(purchase);
            await k.ReceiveAllAsync(purchase, wh);
        }
        await k.ReadyAsync(po);
        return purchases;
    }

    // ── Deliveries made from production ──────────────────────────────────────────

    public static async Task<List<JsonElement>> DeliveriesFromPoAsync(this SapKit k, Guid po, HttpClient? client = null) =>
        (await k.Ok(k.Get($"/api/logistics/deliveries?productionOrderUuid={po}&pageSize=100", client), "list deliveries from the production order")).A("data");

    /// <summary>The delivery's ship-from warehouse (stored, not on the detail model).</summary>
    public static async Task<Guid?> ShipFromWarehouseAsync(this SapKit k, Guid delivery) =>
        (await k.F.QueryAsync("SELECT ShipFromWarehouseUuid AS W FROM logistics.delivery_orders WHERE UUID = @d", ("@d", delivery))).Single()["W"] as Guid?;

    public static async Task<decimal> QtyOnLiveDeliveriesFromPoAsync(this SapKit k, Guid po)
    {
        var rows = await k.F.QueryAsync(
            "SELECT ISNULL(SUM(l.QtyOrdered), 0) AS Q FROM logistics.delivery_order_lines l JOIN logistics.delivery_orders d ON d.Id = l.DeliveryOrderId " +
            "WHERE d.ProductionOrderUuid = @po AND d.IsDelete = 0 AND d.Status <> 'CANCELLED'", ("@po", po));
        return Convert.ToDecimal(rows[0]["Q"]);
    }

    /// <summary>Walks a SHIP delivery whose route has no PACK and no STAGE: release → pick (auto LOOSE + stage) → goods issue → consignment → proof.</summary>
    public static async Task ShipNoPackAsync(this SapKit k, Guid d, Guid carrier, List<string>? next = null, string route = "")
    {
        if (next is not null) await k.CheckNextAsync(next, d, route, "DRAFT", "RELEASE");
        await k.ReleaseAsync(d);
        if (next is not null) await k.CheckNextAsync(next, d, route, "RELEASED", "GENERATE_PICK_LIST");
        await k.PickAllAsync(d);
        if (next is not null) await k.CheckNextAsync(next, d, route, "STAGED", "GOODS_ISSUE");
        await k.Ok(k.GoodsIssue(d), "goods issue");
        if (next is not null) await k.CheckNextAsync(next, d, route, "GOODS_ISSUED", "CREATE_CONSIGNMENT");
        await k.ShipAndProveAsync(d, carrier);
        if (next is not null) await k.CheckNextAsync(next, d, route, "DELIVERED", null);
        (await k.DeliveryStatusAsync(d)).Should().Be("DELIVERED");
    }

    // ── Notifications ────────────────────────────────────────────────────────────

    /// <summary>
    /// The host factory never creates the Notifications module's tables (that module doesn't migrate at start), so every
    /// notification is silently dropped (TryCreate) on a test database. This creates them from the module's model, once
    /// per database, before a test that reads notifications.
    /// </summary>
    public static async Task EnsureNotificationsTableAsync(this SapKit k)
    {
        var exists = await k.F.QueryAsync("SELECT OBJECT_ID(N'notifications.notifications', N'U') AS O");
        if (exists[0]["O"] is not null) return;
        await using var scope = k.F.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SMS.Modules.Notifications.Data.NotificationsDbContext>();
        await Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions
            .GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalDatabaseCreator>(db).CreateTablesAsync();
    }

    /// <summary>Notifications of <paramref name="type"/> about any of <paramref name="mentions"/> (entity uuid, or text in the message).</summary>
    public static async Task<int> NotificationCountAsync(this SapKit k, string type, params string[] mentions)
    {
        var rows = await k.F.QueryAsync("SELECT EntityUuid, Message FROM notifications.notifications WHERE Type = @t", ("@t", type));
        return rows.Count(r => mentions.Any(m =>
            string.Equals(r["EntityUuid"] as string, m, StringComparison.OrdinalIgnoreCase) ||
            ((r["Message"] as string) ?? string.Empty).Contains(m, StringComparison.OrdinalIgnoreCase)));
    }

    // ── Jobs ─────────────────────────────────────────────────────────────────────

    /// <summary>DEM's D-17 production sweep (Demand job whose name says Production + Sweep), run in-process from a fresh scope.</summary>
    public static Task RunProductionSweepAsync(this SapKit k) =>
        RunJobAsync(k, typeof(SMS.Modules.Demand.Data.DemandDbContext).Assembly, "Production", "Sweep");

    /// <summary>MFG's D-20 delivery sweep (Material job whose name says Delivery + Sweep), run in-process from a fresh scope.</summary>
    public static Task RunProductionDeliverySweepAsync(this SapKit k) =>
        RunJobAsync(k, typeof(SMS.Modules.Material.Data.MaterialDbContext).Assembly, "Delivery", "Sweep");

    private static async Task RunJobAsync(SapKit k, Assembly asm, params string[] nameParts)
    {
        var type = asm.GetTypes().Single(t => t is { IsClass: true, IsAbstract: false } && t.Name.EndsWith("Job")
                                              && nameParts.All(p => t.Name.Contains(p)));
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .First(m => typeof(Task).IsAssignableFrom(m.ReturnType) && m.DeclaringType == type && m.GetParameters().All(p => p.IsOptional));
        await using var scope = k.F.Services.CreateAsyncScope();
        var job = scope.ServiceProvider.GetRequiredService(type);
        await (Task)method.Invoke(job, method.GetParameters().Select(p => p.DefaultValue).ToArray())!;
    }

    // ── Features ─────────────────────────────────────────────────────────────────

    /// <summary>Switches a tenant feature for an organization (the platform admin's features edit; <paramref name="root"/> = the super admin).</summary>
    public static async Task SetFeatureAsync(this SapKit root, Guid orgId, string feature, bool enabled) =>
        await root.Ok(root.Put($"/api/system/organizations/{orgId}/features",
            new { features = new[] { new { featureCode = feature, isEnabled = enabled } } }), $"{feature} {(enabled ? "on" : "off")} for {orgId}");

    // ── Misc ─────────────────────────────────────────────────────────────────────

    public static string DateOnly(this JsonElement e, string name) => e.S(name)![..10];

    public static void ShouldBeOneOf(this Api api, string because, params HttpStatusCode[] statuses) =>
        api.Status.Should().BeOneOf(statuses, $"{because} — {api}");
}

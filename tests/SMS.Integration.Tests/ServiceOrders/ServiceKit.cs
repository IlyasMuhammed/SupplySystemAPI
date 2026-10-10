using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.RouteClassification;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using SMS.Shared.Common;

namespace SMS.Integration.Tests.ServiceOrders;

/// <summary>
/// One A36 service world in its own ENTERPRISE organization (MODULE_SERVICES + INVENTORY + MANUFACTURING on from the plan),
/// driven by that organization's admin (<see cref="K"/>): a warehouse with stock, a vendor, a customer, stock materials,
/// a subcontracted service (vendor-bought), an hourly labour service (UOM HR) and the service sold (<see cref="Svc"/>).
/// </summary>
internal sealed record SvcWorld(
    Guid OrgId, SapKit K, Guid Pkr, Warehouse Wh, Partner Vendor, Partner Customer, int AdminUserId,
    Product Raw, Product Spare, Product Scarce, Product Subcon, Product Labor, Product Svc);

/// <summary>A36-P5-10 (QA) — what the service-order E2E class needs on top of <see cref="SapKit"/>, PreOrder and <see cref="Rc"/>.</summary>
internal static class ServiceKit
{
    public static string Today => PreOrder.Day(PreOrder.Today);

    /// <param name="root">The seeded super admin's kit (creates the organization, sets its base currency).</param>
    public static async Task<SvcWorld> WorldAsync(this SapKit root, string prefix, decimal rawStock = 10m, decimal spareStock = 5m)
    {
        var pkr = await root.EnsureCurrencyAsync("PKR", "Pakistani Rupee", "Rs");
        var (org, k, _) = await root.SecondOrganizationAsync(prefix);
        await root.SetOrgBaseCurrencyAsync(org, pkr);
        await k.EnsureOrgCurrencyAsync("PKR", "Pakistani Rupee", "Rs");
        await k.EnsureTaxCodeAsync("GST17", 17m, "SALES", isDefault: true);
        await k.EnsureTaxCodeAsync("PGST17", 17m, "PURCHASE", isDefault: true);
        await k.CreateApproverPlaceholdersAsync();

        var wh = await k.CreateWarehouseAsync();
        var vendor = await k.CreateVendorAsync($"{prefix} Vendor");
        var customer = await k.CreateCustomerAsync($"{prefix} Customer");

        var raw = await k.RawMaterialAsync($"{prefix} Raw");
        var spare = await k.RawMaterialAsync($"{prefix} Spare");
        var scarce = await k.RawMaterialAsync($"{prefix} Scarce");
        var lines = new List<(Product, decimal, decimal)>();
        if (rawStock > 0) lines.Add((raw, rawStock, 5m));
        if (spareStock > 0) lines.Add((spare, spareStock, 3m));
        if (lines.Count > 0) await k.StockUpAsync(vendor, wh, lines.ToArray());

        var subcon = await k.CreateClassifiedProductAsync($"{prefix} Subcon", ProductType.Service, SupplyMethod.Service, 50m, 80m);
        var labor = await k.ServiceProductAsync($"{prefix} Labour", "HR", 0m, 30m);
        var svc = await k.CreateClassifiedProductAsync($"{prefix} Svc", ProductType.Service, SupplyMethod.Service, 0m, 500m);

        return new SvcWorld(org, k, pkr, wh, vendor, customer, AdminUserId(k.Admin), raw, spare, scarce, subcon, labor, svc);
    }

    public static int AdminUserId(HttpClient client)
    {
        var token = new JwtSecurityTokenHandler().ReadJwtToken(client.DefaultRequestHeaders.Authorization!.Parameter);
        return int.Parse(token.Claims.First(c => c.Type is "sub" or "nameid" or "userId").Value);
    }

    /// <summary>A SERVICE product in the given unit of measure (labour: HR), its variant open to retail and services.</summary>
    public static async Task<Product> ServiceProductAsync(this SapKit k, string label, string uom, decimal purchase, decimal? selling)
    {
        var name = k.Next(label);
        var created = await k.Ok(k.Post("/api/products", new
        {
            Name = name, UomCode = uom, ProductType = ProductType.Service, SupplyMethod = SupplyMethod.Service,
            Variants = new[]
            {
                new
                {
                    VariantName = "Default", PurchasePrice = purchase, SellingPrice = selling, IsDefault = true,
                    IsAvailableForRetail = true, IsAvailableForMirMiv = false, IsAvailableForProduction = true, IsAvailableForServices = true
                }
            }
        }), $"create service product {name}");
        var id = created.I("id");
        var detail = await k.Ok(k.Get($"/api/products/{id}"), "read product");
        return new Product(id, detail.G("uuid"), detail.A("variants").Single().G("uuid"), name);
    }

    public static Task<Api> PatchService(this SapKit k, Product p, object body) => k.Patch($"/api/products/{p.ProductId}", body);

    /// <summary>TS-01: the service configuration (FIXED_PRICE, INCLUSIVE, 2 h, service BOM on, subcontractable).</summary>
    public static async Task ConfigureServiceAsync(this SapKit k, Product svc, string policy = "FIXED_PRICE", bool hasBom = true) =>
        await k.Ok(k.PatchService(svc, new
        {
            serviceInvoicingPolicy = policy, serviceBillingModel = "INCLUSIVE", estimatedDurationHours = 2m,
            hasServiceBom = hasBom, isSubcontractable = true
        }), "configure the service");

    public static object BomLine(Product material, decimal qty, string source = "STOCK", Guid? supplier = null, bool critical = true, string? uom = null) => new
    {
        MaterialVariantUuid = material.VariantUuid, Quantity = qty, IsCritical = critical, SourceType = source,
        SubcontractSupplierUuid = supplier, Uom = uom
    };

    public static Task<Api> TryCreateServiceBom(this SapKit k, Product output, params object[] lines) =>
        k.Post("/api/boms", new { ProductUuid = output.ProductUuid, BaseQuantity = 1m, Lines = lines });

    /// <summary>Draft → submit → approve + activate by a second login (four-eyes).</summary>
    public static async Task<Guid> ActiveServiceBomAsync(this SapKit k, Product output, params object[] lines)
    {
        var bom = (await k.Ok(k.TryCreateServiceBom(output, lines), "create service BOM")).GetGuid();
        await k.Ok(k.Post($"/api/boms/{bom}/submit"), "BOM submit");
        var approver = await k.InspectorAsync();
        await k.Ok(k.Post($"/api/boms/{bom}/approve", null, approver), "BOM approve");
        await k.Ok(k.Post($"/api/boms/{bom}/activate", null, approver), "BOM activate");
        return bom;
    }

    /// <summary>The world's main service: configured, with a BOM of 2 raw (STOCK) + 1 subcontracted (non-critical) + 1.5 h labour per unit.</summary>
    public static async Task<Guid> StandardServiceBomAsync(this SvcWorld w)
    {
        await w.K.ConfigureServiceAsync(w.Svc);
        return await w.K.ActiveServiceBomAsync(w.Svc,
            BomLine(w.Raw, 2m),
            BomLine(w.Subcon, 1m, "SUBCONTRACT", w.Vendor.Uuid, critical: false),
            BomLine(w.Labor, 1.5m, "INTERNAL_LABOR"));
    }

    // ── Service orders ──────────────────────────────────────────────────────────

    public static Task<Api> TryCreateOrder(this SvcWorld w, Product service, decimal qty, int? assignee, int priority = 1, string? date = null,
        string? time = null, HttpClient? client = null) =>
        w.K.Post("/api/service-orders", new
        {
            serviceProductUuid = service.ProductUuid, customerUuid = w.Customer.Uuid, quantity = qty, warehouseUuid = w.Wh.Uuid,
            assignedUserId = assignee, scheduledDate = date, scheduledTime = time, priority, notes = "a36 qa"
        }, client);

    public static async Task<Guid> CreateOrderAsync(this SvcWorld w, Product service, decimal qty, int priority = 1, string? date = null, string? time = null, bool assign = true) =>
        (await w.K.Ok(w.TryCreateOrder(service, qty, assign ? w.AdminUserId : null, priority, date ?? Today, time), "create service order")).GetGuid();

    public static Task<JsonElement> OrderAsync(this SvcWorld w, Guid so) => w.K.Ok(w.K.Get($"/api/service-orders/{so}"), "read service order");

    public static Task<Api> Act(this SvcWorld w, Guid so, string action, object? body = null, HttpClient? client = null) =>
        w.K.Post($"/api/service-orders/{so}/{action}", body, client);

    public static Task<JsonElement> DoAsync(this SvcWorld w, Guid so, string action, object? body = null) =>
        w.K.Ok(w.Act(so, action, body), $"service order {action}");

    /// <summary>DRAFT/PLANNED update: the whole body read back from the detail, with the given warehouse and assignee.</summary>
    public static Task<Api> TryUpdate(this SvcWorld w, JsonElement detail, string? notes, int? assignee = null, Guid? warehouse = null, string? rowVersion = null) =>
        w.K.Put($"/api/service-orders/{detail.G("uuid")}", new
        {
            customerUuid = detail.G("customerUuid"), quantity = detail.D("quantity"), warehouseUuid = warehouse ?? detail.G("warehouseUuid"),
            assignedUserId = assignee ?? (detail.IsNull("assignedUserId") ? (int?)null : detail.I("assignedUserId")),
            assignedRoleId = (int?)null, scheduledDate = detail.S("scheduledDate"), scheduledTime = detail.S("scheduledTime"),
            estimatedHours = detail.ND("estimatedHours"), priority = detail.I("priority"), notes, rowVersion = rowVersion ?? detail.S("rowVersion")
        });

    public static JsonElement Material(this JsonElement detail, Product p) =>
        detail.A("materials").Single(m => m.G("variantUuid") == p.VariantUuid && m.S("status") != "CANCELLED");

    public static List<string> Actions(this JsonElement detail) => detail.A("allowedActions").Select(a => a.GetString()!).ToList();

    public static object Consume(params (JsonElement Smr, decimal Qty)[] lines) => lines.Select(l => new { smrUuid = l.Smr.G("uuid"), consumedQuantity = l.Qty }).ToArray();

    // ── Rows only SQL shows ─────────────────────────────────────────────────────

    public static async Task<decimal> OnHandAsync(this SvcWorld w, Product p)
    {
        var rows = await w.K.F.QueryAsync(
            "SELECT ISNULL(SUM(i.QtyOnHand), 0) AS Q FROM inventory.InventoryItems i JOIN inventory.ProductVariants v ON v.Id = i.VariantId " +
            "JOIN inventory.Warehouses wh ON wh.Id = i.WarehouseId WHERE v.Uuid = @v AND wh.Uuid = @w", ("@v", p.VariantUuid), ("@w", w.Wh.Uuid));
        return Convert.ToDecimal(rows[0]["Q"]);
    }

    /// <summary>What the stock ledger holds as reserved for the variant in the world's warehouse.</summary>
    public static async Task<decimal> ReservedAsync(this SvcWorld w, Product p)
    {
        var rows = await w.K.F.QueryAsync(
            "SELECT ISNULL(SUM(i.QtyReserved), 0) AS Q FROM inventory.InventoryItems i JOIN inventory.ProductVariants v ON v.Id = i.VariantId " +
            "JOIN inventory.Warehouses wh ON wh.Id = i.WarehouseId WHERE v.Uuid = @v AND wh.Uuid = @w", ("@v", p.VariantUuid), ("@w", w.Wh.Uuid));
        return Convert.ToDecimal(rows[0]["Q"]);
    }

    /// <summary>The SERVICE_ORDER allocation demands of a service order: (variant, status).</summary>
    public static async Task<List<(Guid Variant, string Status)>> DemandsAsync(this SvcWorld w, Guid so) =>
        (await w.K.F.QueryAsync("SELECT VariantUuid, Status FROM inventory.AllocationDemands WHERE DemandType = 'SERVICE_ORDER' AND DemandUuid = @so", ("@so", so)))
        .Select(r => ((Guid)r["VariantUuid"]!, (string)r["Status"]!)).ToList();

    /// <summary>The SERVICE_ORDER supply requirements of a service order's requirements: (variant, status).</summary>
    public static async Task<List<(Guid Variant, string Status)>> SupplyAsync(this SvcWorld w, Guid so) =>
        (await w.K.F.QueryAsync(
            "SELECT s.VariantUuid, s.Status FROM material.supply_requirements s JOIN material.service_material_requirements m ON m.UUID = s.DemandSourceUuid " +
            "JOIN material.service_orders o ON o.Id = m.ServiceOrderId WHERE s.DemandSourceType = 'SERVICE_ORDER' AND o.UUID = @so", ("@so", so)))
        .Select(r => ((Guid)r["VariantUuid"]!, (string)r["Status"]!)).ToList();

    public static void ShouldFail(this Api api, HttpStatusCode status, string because, string? messagePart = null)
    {
        api.Status.Should().Be(status, $"{because} — {api}");
        if (messagePart is not null) api.Message.Should().Contain(messagePart, because);
    }
}

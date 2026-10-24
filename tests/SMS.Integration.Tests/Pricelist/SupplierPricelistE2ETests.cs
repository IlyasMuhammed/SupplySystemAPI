using System.Net;
using FluentAssertions;
using SMS.Integration.Tests.RouteClassification;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using SMS.Integration.Tests.ServiceOrders;
using Xunit;

namespace SMS.Integration.Tests.Pricelist;

/// <summary>
/// A45 (BE) â€” supplier pricelists and PO auto-pricing end to end on the real host (LocalDB), in a fresh organization:
/// the CRUD contract (201 / 400 / 409 / overlap warning / soft delete), resolve-price (found, tiers, not found, other tenant),
/// the purchase order line's verified price source, permissions and the module switch.
/// <para>Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~SupplierPricelistE2ETests</c>.</para>
/// </summary>
public sealed class SupplierPricelistE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private const string Base = "/api/purchase/supplier-pricelists";
    private readonly SapKit _root;

    public SupplierPricelistE2ETests(SapWebApplicationFactory factory) => _root = new SapKit(factory, "A45");

    private async Task<(Guid Org, SapKit K, Guid Pkr)> WorldAsync(string prefix)
    {
        var pkr = await _root.EnsureCurrencyAsync("PKR", "Pakistani Rupee", "Rs");
        var (org, k, _) = await _root.SecondOrganizationAsync(prefix);
        await _root.SetOrgBaseCurrencyAsync(org, pkr);
        await k.EnsureOrgCurrencyAsync("PKR", "Pakistani Rupee", "Rs");
        return (org, k, pkr);
    }

    private static object Body(Product p, Partner v, Guid currency, decimal price, decimal minQty = 1m, string from = "2026-01-01", string to = "2026-12-31",
        int priority = 0, int basis = 0, Guid? variant = null, string? rowVersion = null, bool? isActive = null) =>
        new
        {
            productUuid = p.ProductUuid, variantUuid = variant, supplierUuid = v.Uuid, vendorProductCode = "ACM-1", minQuantity = minQty,
            unitPrice = price, currencyId = currency, pricingDateBasis = basis, validFrom = from, validTo = to, leadTimeDays = 14,
            priority, notes = "annual", rowVersion, isActive
        };

    /// <summary>A create answers 201 (the spec), which the kit\u0027s Ok does not accept.</summary>
    private static async Task<System.Text.Json.JsonElement> Made(SapKit k, object body, string what)
    {
        var api = await k.Post(Base, body);
        api.Status.Should().Be(HttpStatusCode.Created, $"{what} - {api}");
        return api.Result;
    }

    private static object Resolve(Product p, Partner v, decimal qty, string order = "2026-10-17", string? delivery = null) =>
        new { variantUuid = p.VariantUuid, supplierUuid = v.Uuid, quantity = qty, orderDate = order, expectedDeliveryDate = delivery };

    [Fact]
    public async Task Crud_resolve_and_the_po_line_price_source()
    {
        var (_, k, pkr) = await WorldAsync("PL1");
        var vendor = await k.CreateVendorAsync("Acme");
        var product = await k.CreateProductAsync("Widget", 20m, 30m);
        var wh = await k.CreateWarehouseAsync();

        // Create (201) â€” the saved item is the contract's PricelistItem; names and labels are joined in.
        var created = await k.Post(Base, Body(product, vendor, pkr, 15m, 1m));
        created.Status.Should().Be(HttpStatusCode.Created, created.ToString());
        var item = created.Result.P("item");
        var uuid = item.G("uuid");
        item.S("productName").Should().Be(product.Name);
        item.S("supplierName").Should().Be(vendor.Name);
        item.S("currencyCode").Should().Be("PKR");
        item.S("pricingDateBasisLabel").Should().Be("Order Date");
        item.S("validFrom").Should().Be("2026-01-01");
        item.B("isActive").Should().BeTrue();
        item.S("rowVersion").Should().NotBeNullOrEmpty();
        created.Result.Has("overlapWarning").Should().BeTrue();

        // Quantity break, and an overlapping duplicate of the first tier's dates but another priority: warning, not a block.
        await Made(k, Body(product, vendor, pkr, 12.5m, 100m), "tier 100");
        var overlap = await k.Post(Base, Body(product, vendor, pkr, 14m, 1m, from: "2026-06-01", to: "2026-12-31", priority: 2));
        overlap.Status.Should().Be(HttpStatusCode.Created);
        overlap.Result.S("overlapWarning").Should().Contain("Priority 0").And.Contain("2026-01-01");

        // Exact duplicate: 409. Bad values: 400 with a user-facing message.
        (await k.Post(Base, Body(product, vendor, pkr, 15m, 1m))).Status.Should().Be(HttpStatusCode.Conflict);
        var zero = await k.Post(Base, Body(product, vendor, pkr, 0m));
        zero.Status.Should().Be(HttpStatusCode.BadRequest);
        zero.Message.Should().Contain("Unit price");
        (await k.Post(Base, Body(product, vendor, pkr, 5m, from: "2026-12-31", to: "2026-01-01"))).Status.Should().Be(HttpStatusCode.BadRequest);
        (await k.Post(Base, Body(product, vendor, Guid.NewGuid(), 5m))).Status.Should().Be(HttpStatusCode.BadRequest);

        // List (paged shape: data / totalRecords / page / pageSize / totalPages) and get.
        var list = await k.Ok(k.Get($"{Base}?productUuid={product.ProductUuid}"), "list");
        list.P("totalRecords").GetInt32().Should().Be(3);
        list.A("data").Should().HaveCount(3);
        (await k.Ok(k.Get($"{Base}?supplierUuid={Guid.NewGuid()}"), "other vendor")).A("data").Should().BeEmpty();
        (await k.Ok(k.Get($"{Base}/{uuid}"), "get")).G("uuid").Should().Be(uuid);
        (await k.Get($"{Base}/{Guid.NewGuid()}")).Status.Should().Be(HttpStatusCode.NotFound);

        // resolve-price: tiers, priority, not found (200 with found:false and the warning).
        (await k.Ok(k.Post("/api/purchase/resolve-price", Resolve(product, vendor, 50m)), "r50")).D("unitPrice").Should().Be(15m);
        var r200 = await k.Ok(k.Post("/api/purchase/resolve-price", Resolve(product, vendor, 200m)), "r200");
        r200.D("unitPrice").Should().Be(12.5m);
        r200.S("dateBasisUsed").Should().Be("OrderDate");
        r200.S("effectiveDateUsed").Should().Be("2026-10-17");
        r200.S("currencyCode").Should().Be("PKR");
        r200.I("leadTimeDays").Should().Be(14);
        var none = await k.Post("/api/purchase/resolve-price", Resolve(product, vendor, 50m, order: "2027-03-01"));
        none.Status.Should().Be(HttpStatusCode.OK);
        none.Result.B("found").Should().BeFalse();
        none.Result.S("warningMessage").Should().Contain(product.Name).And.Contain(vendor.Name).And.Contain("2027-03-01");
        (await k.Post("/api/purchase/resolve-price", Resolve(product, vendor, 0m))).Status.Should().Be(HttpStatusCode.BadRequest);

        // PUT: a stale row version is a 409; the current one saves.
        var current = await k.Ok(k.Get($"{Base}/{uuid}"), "read for edit");
        await k.Ok(k.Put($"{Base}/{uuid}", Body(product, vendor, pkr, 15.5m, rowVersion: current.S("rowVersion"))), "update");
        var stale = await k.Put($"{Base}/{uuid}", Body(product, vendor, pkr, 16m, rowVersion: current.S("rowVersion")));
        stale.Status.Should().Be(HttpStatusCode.Conflict, stale.ToString());
        (await k.Ok(k.Post("/api/purchase/resolve-price", Resolve(product, vendor, 50m)), "after update")).D("unitPrice").Should().Be(15.5m);

        // A PO line: the price the pricelist gave stays Pricelist; an edited one is stored as Override with the system price kept.
        var po = (await k.Ok(k.Post("/api/purchase-orders", new
        {
            SupplierId = vendor.Uuid, SupplierName = vendor.Name,
            Lines = new object[]
            {
                new { VariantUuid = product.VariantUuid, ItemDescription = product.Name, Quantity = 50m, UnitPrice = 15.5m, PriceSource = 1,
                      SupplierPricelistUuid = uuid, SystemResolvedPrice = 15.5m, WarehouseId = wh.Uuid, WarehouseName = wh.Name },
                new { VariantUuid = product.VariantUuid, ItemDescription = product.Name, Quantity = 50m, UnitPrice = 11m, PriceSource = 1,
                      SupplierPricelistUuid = uuid, SystemResolvedPrice = 15.5m, PriceOverrideReason = "Spot", WarehouseId = wh.Uuid, WarehouseName = wh.Name },
                new { VariantUuid = product.VariantUuid, ItemDescription = product.Name, Quantity = 1m, UnitPrice = 3m, WarehouseId = wh.Uuid, WarehouseName = wh.Name }
            }
        }), "create PO")).GetGuid();
        var lines = (await k.Ok(k.Get($"/api/purchase-orders/{po}"), "read PO")).A("lines");
        lines[0].I("priceSource").Should().Be(1);
        lines[0].D("systemResolvedPrice").Should().Be(15.5m);
        lines[0].G("supplierPricelistUuid").Should().Be(uuid);
        lines[0].S("pricingDateBasis").Should().Be("OrderDate");
        lines[1].I("priceSource").Should().Be(2);
        lines[1].D("systemResolvedPrice").Should().Be(15.5m);
        lines[1].S("priceOverrideReason").Should().Be("Spot");
        lines[2].I("priceSource").Should().Be(0);
        lines[2].IsNull("supplierPricelistUuid").Should().BeTrue();

        // Soft delete: gone from the default list and from resolution, still there for the PO line.
        (await k.Delete($"{Base}/{uuid}")).Status.Should().Be(HttpStatusCode.OK);
        (await k.Delete($"{Base}/{uuid}")).Status.Should().Be(HttpStatusCode.NotFound, "already inactive");
        (await k.Ok(k.Get($"{Base}?productUuid={product.ProductUuid}"), "active only")).P("totalRecords").GetInt32().Should().Be(2);
        (await k.Ok(k.Get($"{Base}?productUuid={product.ProductUuid}&activeOnly=false"), "all")).P("totalRecords").GetInt32().Should().Be(3);
        (await k.Ok(k.Post("/api/purchase/resolve-price", Resolve(product, vendor, 50m, order: "2026-03-01")), "after delete")).B("found")
            .Should().BeFalse("the only tier-1 row is inactive and the priority-2 one starts in June");
        (await k.Ok(k.Get($"/api/purchase-orders/{po}"), "PO after delete")).A("lines")[0].G("supplierPricelistUuid").Should().Be(uuid);

        // Re-activating through PUT.
        var inactive = await k.Ok(k.Get($"{Base}/{uuid}"), "read inactive");
        inactive.B("isActive").Should().BeFalse();
        (await k.Ok(k.Put($"{Base}/{uuid}", Body(product, vendor, pkr, 15.5m, rowVersion: inactive.S("rowVersion"), isActive: true)), "reactivate"))
            .P("item").B("isActive").Should().BeTrue();
    }

    [Fact]
    public async Task Another_organization_sees_nothing_and_the_permissions_and_module_switch_apply()
    {
        var (org, k, pkr) = await WorldAsync("PL2");
        var vendor = await k.CreateVendorAsync("Acme");
        var product = await k.CreateProductAsync("Widget", 20m, 30m);
        var uuid = (await Made(k, Body(product, vendor, pkr, 15m), "create")).P("item").G("uuid");

        var (_, other, _) = await WorldAsync("PL3");
        (await other.Get($"{Base}/{uuid}")).Status.Should().Be(HttpStatusCode.NotFound);
        (await other.Delete($"{Base}/{uuid}")).Status.Should().Be(HttpStatusCode.NotFound);
        (await other.Ok(other.Get(Base), "other list")).A("data").Should().BeEmpty();
        (await other.Ok(other.Post("/api/purchase/resolve-price", Resolve(product, vendor, 5m)), "other resolve")).B("found")
            .Should().BeFalse("Org B cannot resolve Org A's prices");

        var viewer = await k.LoginWithPermissionsAsync("VIEW", "PO_VIEW");
        var nobody = await k.LoginWithPermissionsAsync("NONE", "SALE_ORDER_VIEW");
        (await k.Get(Base, viewer)).Status.Should().Be(HttpStatusCode.OK);
        (await k.Post("/api/purchase/resolve-price", Resolve(product, vendor, 5m), viewer)).Status.Should().Be(HttpStatusCode.OK);
        (await k.Post(Base, Body(product, vendor, pkr, 9m), viewer)).Status.Should().Be(HttpStatusCode.Forbidden, "PO_VIEW cannot change prices");
        (await k.Delete($"{Base}/{uuid}", viewer)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await k.Get(Base, nobody)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await k.Post("/api/purchase/resolve-price", Resolve(product, vendor, 5m), nobody)).Status.Should().Be(HttpStatusCode.Forbidden);
        var anon = _root.F.CreateClient();
        (await anon.GetAsync(Base)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        await _root.SetFeatureAsync(org, "MODULE_DEMAND", false);
        (await k.Post(Base, Body(product, vendor, pkr, 8m))).Status.Should().Be(HttpStatusCode.Forbidden, "MODULE_DEMAND is off");
        await _root.SetFeatureAsync(org, "MODULE_DEMAND", true);
        (await k.Post(Base, Body(product, vendor, pkr, 8m, 7m))).Status.Should().Be(HttpStatusCode.Created);
    }
}




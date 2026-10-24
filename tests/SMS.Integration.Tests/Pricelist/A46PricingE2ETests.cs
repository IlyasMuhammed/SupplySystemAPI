using System.Net;
using FluentAssertions;
using SMS.Integration.Tests.RouteClassification;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using SMS.Integration.Tests.ServiceOrders;
using Xunit;

namespace SMS.Integration.Tests.Pricelist;

/// <summary>
/// A46 (BE-1) end to end on the real host (LocalDB), in a fresh organization with PKR as its base: the PO order date (TS-27..29),
/// 4dp prices (TS-35/36), the server pricing an unpriced line and re-pricing on a quantity change (TS-38/39), a EUR pricelist row on
/// a PKR PO converted through Finance's rates with the audit on the line (TS-50/56) and the no-rate answer (TS-51), and resolve-price
/// backward compatibility (A46-0233/0234).
/// <para>Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~A46PricingE2ETests</c>.</para>
/// </summary>
public sealed class A46PricingE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private const string Base = "/api/purchase/supplier-pricelists";
    private readonly SapKit _root;

    public A46PricingE2ETests(SapWebApplicationFactory factory) => _root = new SapKit(factory, "A46");

    private static object Row(Product p, Partner v, Guid currency, decimal price, decimal minQty = 1m) =>
        new
        {
            productUuid = p.ProductUuid, supplierUuid = v.Uuid, minQuantity = minQty, unitPrice = price, currencyId = currency,
            pricingDateBasis = 0, validFrom = "2026-01-01", validTo = "2026-12-31", leadTimeDays = 14, priority = 0
        };

    [Fact]
    public async Task Order_date_precision_server_pricing_and_currency_conversion()
    {
        var pkr = await _root.EnsureCurrencyAsync("PKR", "Pakistani Rupee", "Rs");
        var (org, k, _) = await _root.SecondOrganizationAsync("A46");
        await _root.SetOrgBaseCurrencyAsync(org, pkr);
        await k.EnsureOrgCurrencyAsync("PKR", "Pakistani Rupee", "Rs");
        var eur = await k.EnsureCurrencyAsync("EUR", "Euro", "E");
        var usd = await k.EnsureCurrencyAsync("USD", "US Dollar", "$");
        await k.CreateRateAsync("EUR", "PKR", 300m, new DateTime(2026, 10, 1));

        var vendor = await k.CreateVendorAsync("Acme");
        var widget = await k.CreateProductAsync("Widget", 20m, 30m);
        var euroItem = await k.CreateProductAsync("Euro item", 20m, 30m);
        var wh = await k.CreateWarehouseAsync();

        (await k.Post(Base, Row(widget, vendor, pkr, 12.3456m))).Status.Should().Be(HttpStatusCode.Created);
        (await k.Post(Base, Row(widget, vendor, pkr, 10.5m, 100m))).Status.Should().Be(HttpStatusCode.Created);
        (await k.Post(Base, Row(euroItem, vendor, eur, 50m))).Status.Should().Be(HttpStatusCode.Created);

        // TS-27 / TS-38 / TS-35: no orderDate, no price -> dated today, priced by the server at 4dp.
        var line = new { VariantUuid = widget.VariantUuid, ItemDescription = widget.Name, Quantity = 10m, WarehouseId = wh.Uuid, WarehouseName = wh.Name };
        var po = (await k.Ok(k.Post("/api/purchase-orders", new { SupplierId = vendor.Uuid, SupplierName = vendor.Name, Lines = new[] { line } }), "create PO")).GetGuid();
        var read = await k.Ok(k.Get($"/api/purchase-orders/{po}"), "read PO");
        read.S("orderDate").Should().Be(DateTime.UtcNow.ToString("yyyy-MM-dd"));
        var l0 = read.A("lines")[0];
        l0.I("priceSource").Should().Be(1);
        l0.D("unitPrice").Should().Be(12.3456m);
        l0.D("systemResolvedPrice").Should().Be(12.3456m);
        l0.D("lineTotal").Should().Be(123.46m);

        // TS-28: the order date of a draft is editable. TS-36: resending the unchanged line keeps it Pricelist.
        await k.Ok(k.Patch($"/api/purchase-orders/{po}", new { orderDate = "2026-10-15" }), "patch order date");
        (await k.Ok(k.Get($"/api/purchase-orders/{po}"), "read")).S("orderDate").Should().Be("2026-10-15");
        var uuid = l0.G("uuid");
        await k.Ok(k.Patch($"/api/purchase-orders/{po}", new
        {
            lines = new[] { new { Uuid = uuid, VariantUuid = widget.VariantUuid, ItemDescription = widget.Name, Quantity = 10m, UnitPrice = 12.3456m,
                PriceSource = 1, SupplierPricelistUuid = l0.G("supplierPricelistUuid"), SystemResolvedPrice = 12.3456m, WarehouseId = wh.Uuid, WarehouseName = wh.Name } }
        }), "resend");
        var again = (await k.Ok(k.Get($"/api/purchase-orders/{po}"), "read")).A("lines")[0];
        again.I("priceSource").Should().Be(1);
        again.D("unitPrice").Should().Be(12.3456m);

        // TS-39: only the quantity changes (the client still sends the old price); the server moves the line to the 100+ tier.
        await k.Ok(k.Patch($"/api/purchase-orders/{po}", new
        {
            lines = new[] { new { Uuid = again.G("uuid"), VariantUuid = widget.VariantUuid, ItemDescription = widget.Name, Quantity = 150m, UnitPrice = 12.3456m,
                PriceSource = 1, SupplierPricelistUuid = again.G("supplierPricelistUuid"), SystemResolvedPrice = 12.3456m, WarehouseId = wh.Uuid, WarehouseName = wh.Name } }
        }), "qty change");
        var tiered = (await k.Ok(k.Get($"/api/purchase-orders/{po}"), "read")).A("lines")[0];
        tiered.D("unitPrice").Should().Be(10.5m);
        tiered.I("priceSource").Should().Be(1);

        // TS-50 / TS-56: a EUR row on this PKR PO is converted at the rate on file and the audit is kept on the line.
        var euroPo = (await k.Ok(k.Post("/api/purchase-orders", new
        {
            SupplierId = vendor.Uuid, SupplierName = vendor.Name, OrderDate = "2026-10-17",
            Lines = new[] { new { VariantUuid = euroItem.VariantUuid, ItemDescription = euroItem.Name, Quantity = 2m, WarehouseId = wh.Uuid, WarehouseName = wh.Name } }
        }), "create euro PO")).GetGuid();
        var e0 = (await k.Ok(k.Get($"/api/purchase-orders/{euroPo}"), "read euro PO")).A("lines")[0];
        e0.D("unitPrice").Should().Be(15000m);
        e0.I("priceSource").Should().Be(1);
        e0.D("originalCurrencyPrice").Should().Be(50m);
        e0.S("originalCurrencyCode").Should().Be("EUR");
        e0.D("exchangeRateUsed").Should().Be(300m);
        e0.IsNull("exchangeRateDate").Should().BeFalse();

        // resolve-price: poCurrencyId and the A45 currencyId both work; the conversion fields are there.
        var viaNew = await k.Ok(k.Post("/api/purchase/resolve-price", new { variantUuid = euroItem.VariantUuid, supplierUuid = vendor.Uuid, quantity = 2m, orderDate = "2026-10-17", poCurrencyId = pkr }), "poCurrencyId");
        viaNew.D("unitPrice").Should().Be(15000m);
        viaNew.B("currencyConverted").Should().BeTrue();
        viaNew.D("originalPrice").Should().Be(50m);
        viaNew.S("originalCurrencyCode").Should().Be("EUR");
        (await k.Ok(k.Post("/api/purchase/resolve-price", new { variantUuid = euroItem.VariantUuid, supplierUuid = vendor.Uuid, quantity = 2m, orderDate = "2026-10-17", currencyId = pkr }), "legacy name"))
            .D("unitPrice").Should().Be(15000m);

        // TS-51: no EUR->USD rate path -> found, no price, mismatch flag.
        var noRate = await k.Ok(k.Post("/api/purchase/resolve-price", new { variantUuid = euroItem.VariantUuid, supplierUuid = vendor.Uuid, quantity = 2m, orderDate = "2026-10-17", poCurrencyId = usd }), "no rate");
        noRate.B("found").Should().BeTrue();
        noRate.B("currencyMismatchWarning").Should().BeTrue();
        noRate.IsNull("unitPrice").Should().BeTrue();
        noRate.S("warningMessage").Should().Contain("No exchange rate available");

        // Backward compatible (A46-0234): a same-currency call carries no conversion.
        var same = await k.Ok(k.Post("/api/purchase/resolve-price", new { variantUuid = widget.VariantUuid, supplierUuid = vendor.Uuid, quantity = 5m, orderDate = "2026-10-17" }), "same currency");
        same.D("unitPrice").Should().Be(12.3456m);
        same.B("currencyConverted").Should().BeFalse();
        same.IsNull("originalPrice").Should().BeTrue();

        // TS-29: once the PO has left DRAFT its order date cannot change (400).
        await k.CreateApproverPlaceholdersAsync();
        await k.SubmitApproveAndSendPoAsync(po);
        var frozen = await k.Patch($"/api/purchase-orders/{po}", new { orderDate = "2026-11-01" });
        frozen.Status.Should().Be(HttpStatusCode.BadRequest, frozen.ToString());
        (await k.Ok(k.Get($"/api/purchase-orders/{po}"), "read")).S("orderDate").Should().Be("2026-10-15");
    }
}



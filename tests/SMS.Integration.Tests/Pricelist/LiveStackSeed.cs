using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using Xunit;

namespace SMS.Integration.Tests.Pricelist;

/// <summary>
/// Seeds a throwaway LocalDB database for a LIVE (real Kestrel) A45 walk. Only runs when SMS_E2E_LIVE_SEED_OUT is set
/// (together with SMS_E2E_DB_NAME and SMS_E2E_KEEP_DB=1); otherwise it is a no-op.
/// </summary>
public sealed class LiveStackSeed : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    public LiveStackSeed(SapWebApplicationFactory f) => _f = f;

    [Fact]
    public async Task Seed()
    {
        var outPath = Environment.GetEnvironmentVariable("SMS_E2E_LIVE_SEED_OUT");
        if (string.IsNullOrEmpty(outPath)) return;

        var root = new SapKit(_f, "LIVE");
        var pkr = await root.EnsureCurrencyAsync("PKR", "Pakistani Rupee", "Rs");
        await root.EnsureCurrencyAsync("USD", "US Dollar", "$");
        await root.EnsureCurrencyAsync("EUR", "Euro", "E");

        const string email = "haroonarshad230@gmail.com";
        var created = await root.Ok(root.Post("/api/system/organizations", new
        {
            OrgCode = "A45LIVE", OrgName = "A45 Live Org", Plan = "ENTERPRISE",
            AdminFirstName = "Haroon", AdminLastName = "Arshad", AdminEmail = email
        }), "create org");
        var orgId = created.G("organizationId");
        await root.SetOrgBaseCurrencyAsync(orgId, pkr);
        await _f.SetPasswordAsync(email, "Password@123");
        await _f.ExecuteAsync("UPDATE auth.UserAccounts SET IsActive = 1 WHERE Email = @e", ("@e", email));
        var k = new SapKit(_f, "L", _f.CreateBearerClient(await _f.LoginAsync(email, "Password@123")));

        await k.EnsureOrgCurrencyAsync("PKR", "Pakistani Rupee", "Rs");
        await k.EnsureOrgCurrencyAsync("USD", "US Dollar", "$");
        await k.EnsureOrgCurrencyAsync("EUR", "Euro", "E");
        await k.CreateRateAsync("USD", "PKR", 280m, new DateTime(2026, 1, 1));
        await k.CreateRateAsync("EUR", "PKR", 300m, new DateTime(2026, 1, 1));

        var acme = await k.CreateVendorAsync("Acme Supplies");
        var beta = await k.CreateVendorAsync("Beta Traders");
        var widget = await k.CreateProductAsync("Widget", 20m, 30m);
        var wh = await k.CreateWarehouseAsync();

        var gizmoCreated = await k.Ok(k.Post("/api/products", new
        {
            Name = "Gizmo", UomCode = "PCS",
            Variants = new[]
            {
                new { VariantName = "Small", PurchasePrice = 10m, SellingPrice = (decimal?)15m, IsDefault = true, IsAvailableForRetail = true, IsAvailableForMirMiv = true },
                new { VariantName = "Large", PurchasePrice = 12m, SellingPrice = (decimal?)18m, IsDefault = false, IsAvailableForRetail = true, IsAvailableForMirMiv = true }
            }
        }), "create gizmo");
        var gizmo = await k.Ok(k.Get($"/api/products/{gizmoCreated.I("id")}"), "read gizmo");

        var seed = new
        {
            orgId, email, password = "Password@123", pkr,
            usd = await k.EnsureCurrencyAsync("USD", "US Dollar", "$"), eur = await k.EnsureCurrencyAsync("EUR", "Euro", "E"),
            acme = new { acme.Uuid, acme.Name }, beta = new { beta.Uuid, beta.Name },
            widget = new { widget.ProductId, widget.ProductUuid, widget.VariantUuid, widget.Name },
            gizmo = new { productId = gizmoCreated.I("id"), productUuid = gizmo.G("uuid"), name = gizmo.S("name"), variants = gizmo.A("variants").Select(v => new { uuid = v.G("uuid"), name = v.S("variantName") }) },
            warehouse = new { wh.Uuid, wh.Name }
        };
        await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(seed));
    }
}

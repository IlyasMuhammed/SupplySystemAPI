using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SMS.Integration.Tests.SapAlignment;
using SMS.Shared.Common;
using Xunit;
using static SMS.Integration.Tests.MultiCurrency.A35;

namespace SMS.Integration.Tests.MultiCurrency;

/// <summary>
/// A35 (QA) — TEN's surface on the real host: <c>GET/PUT api/organization/currency-settings</c> (API-CONTRACT §4: the
/// unstored read = PKR fallback while the legacy read stays null; 400 for a currency that is not active in the org; 409 for
/// a base in use and for the rate currency once rates exist; account-code length) and the business-partner default
/// currencies (§5: create, alias <c>preferredCurrency</c>, null = unchanged, <c>clearDefault*</c> flags, value + clear → 400,
/// not-an-org-currency → 400).
/// <para>Run alone: <c>dotnet test &lt;out&gt;\SMS.Integration.Tests.dll --filter FullyQualifiedName~MultiCurrencySettingsE2ETests</c>.</para>
/// </summary>
public sealed class MultiCurrencySettingsE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _root;

    public MultiCurrencySettingsE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _root = new SapKit(factory, "MCT");
    }

    /// <summary>XTS (the ISO "testing" code): in the global catalog, never in an org's seed set, never configured by these tests.</summary>
    private async Task<Guid> CatalogOnlyAsync()
    {
        foreach (var c in (await _root.Ok(_root.Get("/api/lookups/currencies"), "catalog")).Items())
            if (c.S("code")?.Trim() == "XTS") return c.G("id");
        return (await _root.Ok(_root.Post("/api/lookups/currencies", new { name = "Test Currency", code = "XTS", symbol = "XT" }), "catalog XTS")).GetGuid();
    }

    [Fact]
    public async Task An_org_without_BaseCurrency_has_no_settings_row_reads_as_PKR_and_the_legacy_read_stays_null()
    {
        var o = await _root.NewOrgAsync("NOBASE");
        (await _f.QueryAsync("SELECT BaseCurrency FROM tenant.Organizations WHERE Id = @o", ("@o", o.OrgId))).Single()["BaseCurrency"]
            .Should().BeNull("a new org created without a base currency");
        (await _f.QueryAsync("SELECT 1 AS X FROM tenant.organization_currency_settings WHERE OrganizationId = @o", ("@o", o.OrgId)))
            .Should().BeEmpty("no settings row until someone saves one");

        var seeded = await _f.QueryAsync("SELECT Code, IsActive FROM finance.org_currencies WHERE OrganizationId = @o", ("@o", o.OrgId));
        seeded.Should().Contain(r => (string)r["Code"]! == "PKR", "P1-14: a new organization is provisioned with its currencies, PKR included");
        (await _f.QueryAsync("SELECT CurrencyCode FROM finance.currency_rates WHERE OrganizationId = @o AND Source = 'SYSTEM'", ("@o", o.OrgId)))
            .Should().ContainSingle("P1-14: the rate currency's SYSTEM 1.0 row").Which["CurrencyCode"].Should().Be("PKR");

        var s = await o.K.SettingsAsync();
        s.B("isStored").Should().BeFalse();
        foreach (var f in new[] { "saleBaseCurrencyCode", "purchaseBaseCurrencyCode", "serviceBaseCurrencyCode", "rateCurrencyCode" })
            s.S(f).Should().Be("PKR", $"{f} — {J.Short(s)}");

        await using var scope = _f.Services.CreateAsyncScope();
        var ocs = scope.ServiceProvider.GetRequiredService<IOrganizationCurrencyService>();
        (await ocs.GetBaseCurrencyIdAsync(o.OrgId)).Should().BeNull("the legacy read keeps its meaning: no base configured");
        (await ocs.GetBaseCurrencyIdAsync(o.OrgId, TransactionDomain.Sale)).Should().Be(s.G("saleBaseCurrencyId"));
        var snap = await ocs.GetSettingsAsync(o.OrgId);
        snap.IsStored.Should().BeFalse();
        snap.RateCurrencyId.Should().Be(s.G("rateCurrencyId"));
    }

    [Fact]
    public async Task Saving_settings_validates_currencies_and_account_codes_and_mirrors_the_sale_base_to_the_organization()
    {
        var o = await _root.NewOrgAsync("SETV");
        var k = o.K;
        var pkr = await k.PkrAsync();
        var usd = await k.UsdAsync();
        var gbp = await k.CurrencyAsync("GBP", "Pound Sterling", "£");
        await k.Ok(k.Put($"/api/currencies/{gbp}", new { code = "GBP", isActive = false }), "deactivate GBP");
        var chf = await CatalogOnlyAsync(); // global catalog only, not this org's (CHF is in every org's seed, D-1)

        var inactive = await k.TrySetBasesAsync(pkr, gbp, pkr);
        inactive.Status.Should().Be(HttpStatusCode.BadRequest, $"BR-C3-02 — {inactive}");
        inactive.Message.Should().Be("GBP is not an active currency of this organization.");
        var foreign = await k.TrySetBasesAsync(chf, pkr, pkr);
        foreign.Status.Should().Be(HttpStatusCode.BadRequest, $"XTS is not configured here — {foreign}");

        var longCode = await k.Put("/api/organization/currency-settings", new
        {
            saleBaseCurrencyId = pkr, purchaseBaseCurrencyId = pkr, serviceBaseCurrencyId = pkr, exchangeGainAccountCode = new string('7', 21)
        });
        longCode.Status.Should().Be(HttpStatusCode.BadRequest);
        longCode.Message.Should().Be("Account codes can be at most 20 characters");

        var saved = await k.SetBasesAsync(usd, pkr, pkr);
        saved.B("isStored").Should().BeTrue();
        saved.S("saleBaseCurrencyCode").Should().Be("USD");
        saved.S("exchangeGainAccountCode").Should().Be("7110");
        (await _f.QueryAsync("SELECT BaseCurrency FROM tenant.Organizations WHERE Id = @o", ("@o", o.OrgId))).Single()["BaseCurrency"]
            .Should().Be(usd, "D-7: Organization.BaseCurrency follows the sale base");
        await k.SetBasesAsync(pkr, pkr, pkr);
    }

    [Fact]
    public async Task The_rate_currency_is_read_only_409_before_and_after_rates_exist()
    {
        // REV-07 (2026-10-07): the rate currency cannot be changed in A35 at all — 409 even before any rate exists.
        var o = await _root.NewOrgAsync("RATEC");
        var k = o.K;
        var pkr = await k.PkrAsync();
        var usd = await k.UsdAsync();
        var aed = await k.AedAsync();
        await k.SetBasesAsync(pkr, pkr, pkr);

        var before = await k.TrySetBasesAsync(pkr, pkr, pkr, usd);
        before.Status.Should().Be(HttpStatusCode.Conflict, $"REV-07: read-only even with no rates — {before}");
        before.Message.Should().Be("The rate currency can't be changed.");
        (await k.TrySetBasesAsync(pkr, pkr, pkr, pkr)).Status.Should().Be(HttpStatusCode.OK, "the same rate currency is no change");

        await k.AddRateAsync(aed, 76.30m, Today.AddDays(-1));
        (await k.SettingsAsync()).P("locks").P("rateCurrency").B("locked").Should().BeTrue();
        var after = await k.TrySetBasesAsync(pkr, pkr, pkr, usd);
        after.Status.Should().Be(HttpStatusCode.Conflict, $"{after}");
        after.Message.Should().Be("The rate currency can't be changed.");
        (await k.TrySetBasesAsync(pkr, pkr, pkr, null)).Status.Should().Be(HttpStatusCode.OK, "rateCurrencyId null = unchanged");
        (await k.SettingsAsync()).S("rateCurrencyCode").Should().Be("PKR");
    }

    [Fact]
    public async Task Partner_default_currencies_create_alias_unchanged_clear_and_validation()
    {
        var o = await _root.NewOrgAsync("BPDEF");
        var k = o.K;
        await k.PkrAsync();
        var usd = await k.UsdAsync();
        var eur = await k.EurAsync();
        var aed = await k.AedAsync();
        var chf = await CatalogOnlyAsync();

        // Customer through api/partners: create with a sale default, then clear it with the flag on PUT.
        var customer = await k.CustomerAsync("Default Customer", aed);
        var c = await k.Ok(k.Get($"/api/partners/{customer.Uuid}"), "read customer");
        (c.NG("defaultSaleCurrencyId"), c.S("defaultSaleCurrencyCode")).Should().Be(((Guid?)aed, "AED"));

        await k.Ok(k.Put($"/api/partners/{customer.Uuid}", new
        {
            PartnerCode = c.S("partnerCode"), CompanyName = customer.Name, PartnerType = "CUSTOMER", IsCustomer = true, IsActive = true
        }), "PUT without currency fields");
        (await k.Ok(k.Get($"/api/partners/{customer.Uuid}"), "read")).NG("defaultSaleCurrencyId").Should().Be(aed, "null = unchanged");
        await k.Ok(k.Put($"/api/partners/{customer.Uuid}", new
        {
            PartnerCode = c.S("partnerCode"), CompanyName = customer.Name, PartnerType = "CUSTOMER", IsCustomer = true, IsActive = true,
            ClearDefaultSaleCurrency = true
        }), "PUT with the clear flag");
        (await k.Ok(k.Get($"/api/partners/{customer.Uuid}"), "read")).NG("defaultSaleCurrencyId").Should().BeNull();

        var badCustomer = await k.Post("/api/partners", new
        {
            PartnerCode = $"C{Guid.NewGuid():N}"[..10], CompanyName = k.Next("Bad"), PartnerType = "CUSTOMER", IsCustomer = true, IsActive = true,
            DefaultSaleCurrencyId = chf
        });
        badCustomer.Status.Should().Be(HttpStatusCode.BadRequest, $"BR-C4-01 — {badCustomer}");
        badCustomer.Message.Should().Be("XTS is not an active currency of this organization.");

        // Supplier through api/suppliers: the alias, PATCH semantics, value + clear → 400.
        var name = k.Next("Alias Vendor");
        var vendor = (await k.Ok(k.Post("/api/suppliers", new { SupplierName = name, SupplierCode = $"V{Guid.NewGuid():N}"[..10], PreferredCurrency = usd }),
            "create vendor with the old field")).GetGuid();
        var v = await k.Ok(k.Get($"/api/suppliers/{vendor}"), "read vendor");
        (v.NG("defaultPurchaseCurrencyId"), v.S("defaultPurchaseCurrencyCode")).Should().Be(((Guid?)usd, "USD"));

        await k.Ok(k.Patch($"/api/suppliers/{vendor}", new { PreferredCurrency = usd, DefaultPurchaseCurrencyId = eur, DefaultSaleCurrencyId = aed }),
            "both purchase fields: the new one wins");
        v = await k.Ok(k.Get($"/api/suppliers/{vendor}"), "read vendor");
        v.NG("defaultPurchaseCurrencyId").Should().Be(eur);
        v.NG("defaultSaleCurrencyId").Should().Be(aed);

        await k.Ok(k.Patch($"/api/suppliers/{vendor}", new { Notes = "touch" }), "PATCH without currency fields");
        (await k.Ok(k.Get($"/api/suppliers/{vendor}"), "read")).NG("defaultPurchaseCurrencyId").Should().Be(eur, "null = unchanged");

        var both = await k.Patch($"/api/suppliers/{vendor}", new { DefaultSaleCurrencyId = aed, ClearDefaultSaleCurrency = true });
        both.Status.Should().Be(HttpStatusCode.BadRequest, $"value + clear flag — {both}");

        await k.Ok(k.Patch($"/api/suppliers/{vendor}", new { ClearDefaultPurchaseCurrency = true, ClearDefaultSaleCurrency = true }), "clear both");
        v = await k.Ok(k.Get($"/api/suppliers/{vendor}"), "read");
        v.NG("defaultPurchaseCurrencyId").Should().BeNull();
        v.NG("defaultSaleCurrencyId").Should().BeNull();

        (await k.Patch($"/api/suppliers/{vendor}", new { DefaultPurchaseCurrencyId = chf })).Status
            .Should().Be(HttpStatusCode.BadRequest, "not an org currency");
    }
}

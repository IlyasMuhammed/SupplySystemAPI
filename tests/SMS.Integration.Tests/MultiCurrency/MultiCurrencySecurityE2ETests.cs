using System.Net;
using FluentAssertions;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using Xunit;
using static SMS.Integration.Tests.MultiCurrency.A35;

namespace SMS.Integration.Tests.MultiCurrency;

/// <summary>
/// A35 (QA) — security and tenancy of everything A35 adds, on the real host:
/// <list type="bullet">
/// <item><b>Gating (D-16):</b> a user with no permission gets 403 on every new action; a user holding only CURRENCY_VIEW +
/// CURRENCY_RATE_VIEW reads and converts but cannot write, run revaluation or read the register; anonymous = 401.</item>
/// <item><b>Isolation:</b> another org's currency configuration, rate row and exchange-difference rows are invisible — 404 on
/// by-id reads/writes, absent from lists — for its peers <b>and the super admin</b> (the EF tenant filter is off for it);
/// revaluation in one org never touches another's rows; a currency only another org configured is "not an active
/// currency" here. T-C1-05: the same code in two orgs.</item>
/// <item><b>Settings immutability (T-C3-03/04, D-8):</b> sale base change refused (409) once a confirmed SO exists; a
/// domain with no locked document can still change.</item>
/// </list>
/// <para>Run alone: <c>dotnet test &lt;out&gt;\SMS.Integration.Tests.dll --filter FullyQualifiedName~MultiCurrencySecurityE2ETests</c>.</para>
/// </summary>
public sealed class MultiCurrencySecurityE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _root; // the seeded platform super admin, acting in org 1

    public MultiCurrencySecurityE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _root = new SapKit(factory, "MCS");
    }

    private static readonly Guid Unknown = Guid.NewGuid();

    private static IEnumerable<(string Name, HttpMethod Method, string Url, object? Body, bool Read)> Endpoints(Guid currency, Guid rateId) =>
    [
        ("list currencies",      HttpMethod.Get,  "/api/currencies", null, true),
        ("get currency",         HttpMethod.Get,  $"/api/currencies/{currency}", null, true),
        ("add currency",         HttpMethod.Post, "/api/currencies", new { code = "QQQ", name = "Q", symbol = "Q" }, false),
        ("update currency",      HttpMethod.Put,  $"/api/currencies/{currency}", new { code = "PKR", displayOrder = 1 }, false),
        ("list rates",           HttpMethod.Get,  "/api/currency-rates", null, true),
        ("active rates",         HttpMethod.Get,  "/api/currency-rates/active", null, true),
        ("rate on date",         HttpMethod.Get,  $"/api/currency-rates/{currency}?date={Day(0)}", null, true),
        ("rate history",         HttpMethod.Get,  $"/api/currency-rates/history/{currency}", null, true),
        ("add rate",             HttpMethod.Post, "/api/currency-rates", new { currencyId = currency, rate = 2m, effectiveFrom = Day(1) }, false),
        ("correct rate",         HttpMethod.Put,  $"/api/currency-rates/{rateId}", new { rate = 2m, effectiveFrom = Day(-30) }, false),
        ("convert",              HttpMethod.Post, "/api/currency/convert", new { amount = 1m, fromCurrencyId = currency }, true),
        ("read settings",        HttpMethod.Get,  "/api/organization/currency-settings", null, true),
        ("save settings",        HttpMethod.Put,  "/api/organization/currency-settings", new { saleBaseCurrencyId = currency, purchaseBaseCurrencyId = currency, serviceBaseCurrencyId = currency }, false),
        ("exchange differences", HttpMethod.Get,  "/api/finance/exchange-differences", null, false),
        ("run revaluation",      HttpMethod.Post, "/api/finance/exchange-revaluation/run", new { revaluationDate = Day(0) }, false),
    ];

    [Fact]
    public async Task Every_new_action_is_gated_and_view_only_users_cannot_write()
    {
        var o = await _root.NewOrgAsync("GATE");
        var k = o.K;
        var pkr = await k.PkrAsync();
        var usd = await k.UsdAsync();
        var rate = await k.AddRateAsync(usd, 278.05m, Today.AddDays(-30));

        var nobody = await k.LoginWithPermissionsAsync("nocur");
        var viewer = await k.LoginWithPermissionsAsync("curview", "CURRENCY_VIEW", "CURRENCY_RATE_VIEW");
        var anonymous = _f.CreateAnonymousClient();

        foreach (var (name, method, url, body, read) in Endpoints(usd, rate.G("id")))
        {
            (await k.Send(method, url, body, anonymous)).Status.Should().Be(HttpStatusCode.Unauthorized, $"{name}: anonymous");
            (await k.Send(method, url, body, nobody)).Status.Should().Be(HttpStatusCode.Forbidden, $"{name}: no permission");
            var asViewer = await k.Send(method, url, body, viewer);
            if (read) asViewer.Status.Should().Be(HttpStatusCode.OK, $"{name}: a viewer may — {asViewer}");
            else asViewer.Status.Should().Be(HttpStatusCode.Forbidden, $"{name}: a viewer may not — {asViewer}");
        }

        // The org admin (D-16 grants every A35 code to Org Admin) may do all of it.
        (await k.Ok(k.Get("/api/finance/exchange-differences"), "org admin reads the register")).A("data").Should().BeEmpty();
        (await k.SettingsAsync()).G("saleBaseCurrencyId").Should().Be(pkr);
    }

    [Fact]
    public async Task Another_orgs_currency_rate_and_differences_are_invisible_even_to_the_super_admin()
    {
        var two = await _root.NewOrgAsync("ISO2");
        var three = await _root.NewOrgAsync("ISO3");

        // Org 2: MXN (configured only there), a rate row, and an UNREALIZED register row on an open EUR invoice.
        var k2 = two.K;
        var pkr2 = await k2.PkrAsync();
        var mxn = await k2.MxnAsync();
        var eur2 = await k2.EurAsync();
        await k2.SetBasesAsync(pkr2, pkr2, pkr2);
        var mxnRate = await k2.AddRateAsync(mxn, 15.10m, Today.AddDays(-30));
        await k2.AddRateAsync(eur2, 316.48m, Today.AddDays(-30));
        var item = await k2.CreateProductAsync("Iso Widget", 1m, 100m);
        await k2.SellingPriceAsync(item, 5_000m, eur2);
        await k2.StockAsync(item);
        var cust2 = await k2.CustomerAsync("Iso Customer", null);
        var so2 = await k2.SaleOrderAsync(cust2, eur2, Line(item, 1m));
        await k2.ConfirmAsync(so2);
        var inv2 = await k2.InvoiceAndIssueAsync(so2);
        await k2.AddRateAsync(eur2, 317.00m, Today.AddDays(5));
        await k2.Ok(k2.TryRevalueAsync(Today.AddDays(5)), "org 2 revaluation");
        var org2Rows = await k2.DifferencesOfAsync(inv2.G("uuid"), "UNREALIZED");
        org2Rows.Should().ContainSingle();
        var org2RowId = org2Rows[0].G("id");

        // The super admin (org 1) and org 3's admin: nothing of org 2's is reachable.
        (await _root.PkrAsync()).Should().NotBeEmpty();
        foreach (var (who, k) in new[] { ("super admin", _root), ("org 3 admin", three.K) })
        {
            (await k.Get($"/api/currencies/{mxn}")).Status.Should().Be(HttpStatusCode.NotFound, $"{who}: MXN is org 2's");
            (await k.Put($"/api/currencies/{mxn}", new { code = "MXN", isActive = false })).Status
                .Should().Be(HttpStatusCode.NotFound, $"{who}: cannot edit org 2's configuration");
            (await k.Put($"/api/currency-rates/{mxnRate.G("id")}", new { rate = 99m, effectiveFrom = Day(-30) })).Status
                .Should().Be(HttpStatusCode.NotFound, $"{who}: org 2's rate row");
            var history = await k.Get($"/api/currency-rates/history/{mxn}");
            if (history.Status == HttpStatusCode.OK) history.Result.Items().Should().BeEmpty($"{who}: no org 2 rows in history");
            else history.Status.Should().Be(HttpStatusCode.NotFound);
            (await k.Ok(k.Get("/api/currency-rates"), $"{who} lists rates")).Items()
                .Should().NotContain(r => r.G("id") == mxnRate.G("id"), $"{who}: org 2's rate is not listed");
            (await k.DifferencesAsync("")).Should().NotContain(r => r.G("id") == org2RowId, $"{who}: org 2's register row");
            (await k.Get($"/api/sale-orders/{so2}")).Status.Should().Be(HttpStatusCode.NotFound, $"{who}: org 2's SO");
            (await k.Post($"/api/sale-orders/{so2}/confirm")).Status.Should().Be(HttpStatusCode.NotFound, $"{who}: org 2's SO confirm");
            (await k.TryConvertAsync(1m, mxn, null, Today)).Status.Should().Be(HttpStatusCode.BadRequest, $"{who}: MXN is not this org's currency");
            (await k.TryAddRateAsync(mxn, 1m, Today)).Status.Should().Be(HttpStatusCode.BadRequest, $"{who}: cannot rate a currency it has not configured");
        }

        // Revaluation in org 3 / org 1 leaves org 2's row exactly as it was.
        await three.K.Ok(three.K.TryRevalueAsync(Today.AddDays(5)), "org 3 revaluation");
        await _root.Ok(_root.TryRevalueAsync(Today.AddDays(5)), "org 1 revaluation by the super admin");
        var still = await k2.DifferencesOfAsync(inv2.G("uuid"), "UNREALIZED");
        still.Should().ContainSingle().Which.G("id").Should().Be(org2RowId);

        // T-C1-05: the same code is configured independently in two orgs.
        await three.K.MxnAsync();
        (await three.K.OrgCurrenciesAsync()).Should().ContainSingle(c => c.S("code") == "MXN");
        (await k2.OrgCurrenciesAsync()).Should().ContainSingle(c => c.S("code") == "MXN");
        (await three.K.Post("/api/currencies", new { code = "MXN", name = "Mexican Peso", symbol = "MX$" })).Status
            .Should().Be(HttpStatusCode.Conflict, "T-C1-04: a duplicate in the same org");
    }

    [Fact]
    public async Task T_C3_03_and_04_a_base_with_locked_documents_cannot_change_one_without_can()
    {
        var o = await _root.NewOrgAsync("BASE");
        var k = o.K;
        var pkr = await k.PkrAsync();
        var eur = await k.EurAsync();
        var usd = await k.UsdAsync();
        await k.SetBasesAsync(pkr, pkr, pkr);

        // T-C3-04: no documents yet — the sale base can move to EUR and back.
        await k.SetBasesAsync(eur, pkr, pkr);
        await k.SetBasesAsync(pkr, pkr, pkr);

        var item = await k.CreateProductAsync("Lock Widget", 1m, 100m);
        await k.StockAsync(item); // an approved PKR PO now exists → the purchase base is locked too
        var customer = await k.CustomerAsync("Lock Customer", null);
        await k.ConfirmAsync(await k.SaleOrderAsync(customer, pkr, Line(item, 1m)));

        var settings = await k.SettingsAsync();
        settings.P("locks").P("sale").B("locked").Should().BeTrue();
        settings.P("locks").P("purchase").B("locked").Should().BeTrue();
        settings.P("locks").P("service").B("locked").Should().BeFalse();

        var refused = await k.TrySetBasesAsync(eur, pkr, pkr);
        refused.Status.Should().Be(HttpStatusCode.Conflict, $"T-C3-03 — {refused}");
        refused.Message.Should().StartWith("Cannot change the sale base currency — ");
        (await k.TrySetBasesAsync(pkr, usd, pkr)).Status.Should().Be(HttpStatusCode.Conflict, "approved PO");
        await k.SetBasesAsync(pkr, pkr, usd); // service base: no documents exist (D-18)
        (await k.SettingsAsync()).G("saleBaseCurrencyId").Should().Be(pkr);

        // A base cannot be deactivated (T-C1-07).
        var deact = await k.Put($"/api/currencies/{pkr}", new { code = "PKR", isActive = false });
        deact.Status.Should().Be(HttpStatusCode.BadRequest);
        deact.Message.Should().StartWith("Cannot deactivate — ", "PKR is the sale/purchase base and the rate currency");
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Jobs;
using SMS.Modules.Integration.Models;
using SMS.Shared.Integration.QuickBooks;
using SMS.Shared.Pagination;
using Xunit;

namespace SMS.Integration.Tests.QuickBooks;

/// <summary>
/// QBI-34: another system pushing through the API-key data endpoints of the real host — the
/// authentication matrix, dependency handling for an external caller, a create that timed out but
/// happened, and an invoice built exactly as plan D-5 says. Only Intuit is faked.
/// <para>Run on its own: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~QuickBooksApiKeyTests</c>.</para>
/// </summary>
public sealed class QuickBooksApiKeyTests : IClassFixture<QuickBooksWebApplicationFactory>
{
    private const string Admin = "/api/integrations/quickbooks";
    private const string Data  = "/api/gateway/quickbooks/v1";
    private const string Realm = "9130000000000077";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly QuickBooksWebApplicationFactory _f;
    private readonly HttpClient _admin;

    public QuickBooksApiKeyTests(QuickBooksWebApplicationFactory factory)
    {
        _f     = factory;
        _admin = factory.CreateAdminClient();
    }

    [Fact]
    public async Task Another_system_pushes_customers_items_and_invoices_with_an_api_key()
    {
        await ConnectAndGoLiveAsync();

        // ── Issue a key (JWT, admin screen) ──────────────────────────────────────────
        var client = await Post<ApiClientModel>("/api-clients", new CreateApiClientRequest
        {
            Name = "E2E Web Shop", Scopes = ["customers:write", "items:write", "invoices:write", "status:read"]
        });
        var issued = await Post<IssuedApiKeyModel>($"/api-clients/{client.Id}/keys", new IssueApiKeyRequest());
        issued.ApiKey.Should().StartWith("sqb_");
        issued.TenantId.Should().Be(_f.OrganizationId);
        (await _admin.GetStringAsync($"{Admin}/api-clients")).Should().NotContain(issued.ApiKey, "a key is shown once, never listed");

        using var shop = Keyed(issued.ApiKey, issued.TenantId);

        // ── The authentication matrix, through the real pipeline ─────────────────────
        var customer = new CustomerPayload { DisplayName = "Shop Customer One", CurrencyCode = "PKR", Email = "one@shop.test" };

        using (var none = _f.CreateAnonymousClient())
            (await none.PutAsJsonAsync($"{Data}/customers/SHOP-C1", customer)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using (var wrongTenant = Keyed(issued.ApiKey, Guid.NewGuid()))
            (await wrongTenant.PutAsJsonAsync($"{Data}/customers/SHOP-C1", customer)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using (var badKey = Keyed("sqb_" + new string('x', 40), issued.TenantId))
            (await badKey.PutAsJsonAsync($"{Data}/customers/SHOP-C1", customer)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _admin.PutAsJsonAsync($"{Data}/customers/SHOP-C1", customer)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "a user's JWT does not open the API-key endpoints");
        (await shop.PutAsJsonAsync($"{Data}/vendors/SHOP-V1", new VendorPayload { DisplayName = "Shop Vendor" })).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "this key has no vendors:write scope");

        var invalid = await shop.PutAsJsonAsync($"{Data}/customers/SHOP-BAD", new CustomerPayload { DisplayName = "Bad: Name" });
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await invalid.Content.ReadAsStringAsync()).Should().Contain("NAME_HAS_COLON");

        // ── A customer, then an invoice that needs an item nobody has sent yet ───────
        var accepted = await shop.PutAsJsonAsync($"{Data}/customers/SHOP-C1", customer);
        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted, await accepted.Content.ReadAsStringAsync());

        var invoice = new SalesInvoicePayload
        {
            DocNumber = "SHOP-1001", CustomerExternalId = "SHOP-C1", TxnDate = DateTime.UtcNow.Date, DueDate = DateTime.UtcNow.Date.AddDays(30),
            CurrencyCode = "PKR", ExpectedTaxAmount = 0m, ExpectedTotal = 180m,
            Lines = [new SalesInvoiceLinePayload { LineNo = 1, ItemExternalId = "SHOP-ITEM1", Quantity = 2m, UnitPrice = 100m, DiscountPercent = 10m, TaxPercent = 0m }]
        };
        var waiting = await shop.PutAsJsonAsync($"{Data}/sales-invoices/SHOP-I1", invoice);
        waiting.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var waitingBody = await waiting.Content.ReadAsStringAsync();
        waitingBody.Should().Contain("WaitingOnDependency").And.Contain("SHOP-ITEM1",
            "an external caller is told exactly which records to send first");

        (await shop.PutAsJsonAsync($"{Data}/items/SHOP-ITEM1",
                new ItemPayload { Name = "Shop Item", Sku = "SHOP-SKU-1", SalesPrice = 100m, IsSold = true, IsPurchased = false }))
            .StatusCode.Should().Be(HttpStatusCode.Accepted);

        // ── The customer's create times out — but QuickBooks did create it ───────────
        _f.Company.TimeOutAfterNextCreateOf = SyncKind.Customer;
        await RunOutboxAsync(rounds: 1);

        var customerItem = (await Get<PaginatedResponse<SyncItemModel>>("/sync/items?kind=Customer&search=SHOP-C1&page=1&pageSize=10"))
            .Data.Single(i => i.ExternalId == "SHOP-C1");
        customerItem.State.Should().NotBe("Synced", "the outcome of that create is unknown");

        // An admin presses Retry: the lookup finds the record the timed-out call created and links it.
        var retried = await Post<SyncItemModel>($"/sync/items/{customerItem.Id}/retry", null);
        retried.State.Should().Be("Synced");
        _f.Company.Records(SyncKind.Customer).Count(r => r.Name == "Shop Customer One")
            .Should().Be(1, "a timed-out create must never become two customers");

        // ── Item, then the invoice: refs resolved, gross lines, one discount line ────
        await RunOutboxAsync(rounds: 4);

        var created = _f.Company.Writes.Where(w => w.Operation == "Create").ToList();
        var item = _f.Company.Records(SyncKind.Item).Single(r => r.Name == "Shop Item");
        var qboInvoice = created.Single(c => c.Kind == SyncKind.SalesInvoice).Entity.Should().BeOfType<RemoteInvoice>().Subject;
        qboInvoice.DocNumber.Should().Be("SHOP-1001");
        qboInvoice.CustomerId.Should().Be(_f.Company.Records(SyncKind.Customer).Single(r => r.Name == "Shop Customer One").Id);
        qboInvoice.Lines.Should().ContainSingle().Which.Should().Match<RemoteSalesLine>(l =>
            l.ItemId == item.Id && l.Quantity == 2m && l.UnitPrice == 100m && l.Amount == 200m && l.TaxCodeId == "NON");
        qboInvoice.DiscountAmount.Should().Be(20m, "plan D-5: gross lines and one discount line");
        qboInvoice.DiscountAccountId.Should().Be("2");
        (qboInvoice.Lines.Sum(l => l.Amount) - qboInvoice.DiscountAmount).Should().Be(invoice.ExpectedTotal);

        var status = await shop.GetFromJsonAsync<JsonElement>($"{Data}/SalesInvoice/SHOP-I1", Json);
        status.GetRawText().Should().Contain("Synced");

        // ── Same payload again: nothing to send ───────────────────────────────────────
        _f.Company.ResetCalls();
        (await shop.PutAsJsonAsync($"{Data}/sales-invoices/SHOP-I1", invoice)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await RunOutboxAsync(rounds: 1);
        _f.Company.Writes.Should().BeEmpty();

        // ── Void through the API ──────────────────────────────────────────────────────
        (await shop.PostAsync($"{Data}/sales-invoices/SHOP-I1/void", null)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await RunOutboxAsync(rounds: 1);
        _f.Company.Writes.Should().ContainSingle(w => w.Operation == "Void");
        _f.Company.Records(SyncKind.SalesInvoice).Single(r => r.Name == "SHOP-1001").Active.Should().BeFalse();

        // ── A revoked key stops working at once ───────────────────────────────────────
        var revoke = await _admin.DeleteAsync($"{Admin}/api-clients/{client.Id}/keys/{issued.KeyId}");
        revoke.StatusCode.Should().Be(HttpStatusCode.OK);
        (await shop.PutAsJsonAsync($"{Data}/customers/SHOP-C2", customer)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Every record this client sent is kept under its own name, apart from SCM's.
        (await _f.QueryAsync("SELECT DISTINCT SourceSystem FROM integration.EntityMaps WHERE ExternalId LIKE 'SHOP-%'"))
            .Select(r => (string)r["SourceSystem"]!).Should().Equal("E2E Web Shop");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────

    private async Task ConnectAndGoLiveAsync()
    {
        var consent = await Post<ConnectResponse>("/connect", null);
        var state = Uri.UnescapeDataString(new Uri(consent.ConsentUrl).Query.TrimStart('?').Split('&')
            .Single(p => p.StartsWith("state=")).Substring("state=".Length));
        using (var anonymous = _f.CreateAnonymousClient())
        {
            var cb = await anonymous.GetAsync($"{Admin}/callback?code=api-code&state={Uri.EscapeDataString(state)}&realmId={Realm}");
            cb.Headers.Location!.ToString().Should().EndWith("result=connected");
        }

        await Put<IntegrationSettingsModel>("/settings", new UpdateIntegrationSettingsRequest
        {
            DefaultIncomeAccountId = "1", DefaultExpenseAccountId = "3", FreightExpenseAccountId = "4", DiscountAccountId = "2",
            ItemTypeDefault = "NonInventory", PartnerScope = "OnlyWhenReferenced"
        });
        await Put<List<TaxCodeMappingModel>>("/tax-mappings", new SaveTaxCodeMappingsRequest
        {
            Mappings = [new() { TaxPercent = 0m, QboTaxCodeId = "NON" }]
        });
        await Post<IntegrationSettingsModel>("/matching/complete", new ConfirmMatchingCompleteRequest { Confirmed = true });
        (await Post<IntegrationSettingsModel>("/mode", new SetModeRequest { Mode = "Live" })).Mode.Should().Be("Live");
    }

    private HttpClient Keyed(string key, Guid tenant)
    {
        var client = _f.CreateAnonymousClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant.ToString());
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private async Task RunOutboxAsync(int rounds)
    {
        for (var i = 0; i < rounds; i++)
        {
            await _f.RunInScopeAsync<DependencyJob>(job => job.RunOnceAsync());
            await _f.RunInScopeAsync<SyncOutboxJob>(job => job.RunOnceAsync());
        }
    }

    private async Task<T> Get<T>(string path) => await _f.ReadResultAsync<T>(await _admin.GetAsync(Admin + path));

    private async Task<T> Post<T>(string path, object? body) =>
        await _f.ReadResultAsync<T>(body is null ? await _admin.PostAsync(Admin + path, null) : await _admin.PostAsJsonAsync(Admin + path, body));

    private async Task<T> Put<T>(string path, object body) => await _f.ReadResultAsync<T>(await _admin.PutAsJsonAsync(Admin + path, body));
}

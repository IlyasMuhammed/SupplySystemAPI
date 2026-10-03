using FluentAssertions;
using Newtonsoft.Json.Linq;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Providers.QuickBooks;
using SMS.Modules.Integration.Tests.QuickBooks.Support;
using SMS.Shared.Integration.QuickBooks;
using static SMS.Modules.Integration.Tests.QuickBooks.Support.QboTestKit;

namespace SMS.Modules.Integration.Tests.QuickBooks;

/// <summary>
/// The provider with Intuit's real SDK (SdkQboClient → DataService / QueryService → HttpWebRequest →
/// FaultHandler) talking to <see cref="QboStubServer"/> on 127.0.0.1. Nothing leaves the machine; this
/// proves the request shapes, URLs and the exception shapes the translator relies on.
/// </summary>
public sealed class QuickBooksProviderSdkLoopbackTests : IDisposable
{
    private const string Time = "\"time\":\"2026-09-30T10:00:00.000-07:00\"";
    private readonly QboStubServer _server = new();
    private readonly QuickBooksAccountingProvider _provider;

    public QuickBooksProviderSdkLoopbackTests()
    {
        _provider = Provider(new StubContextFactory(() => NewServiceContext(_server.BaseUrl)), new SdkQboClientFactory());
    }

    public void Dispose() => _server.Dispose();

    private string CompanyPath(string rest) => $"/v3/company/{RealmId}/{rest}";

    private static string Entity(string name, string json) => "{\"" + name + "\":" + json + "," + Time + "}";

    private static string QueryResponse(string entity, params string[] rows) =>
        rows.Length == 0
            ? "{\"QueryResponse\":{}," + Time + "}"
            : "{\"QueryResponse\":{\"" + entity + "\":[" + string.Join(",", rows) + "],\"startPosition\":1,\"maxResults\":" + rows.Length + "}," + Time + "}";

    // ── Success paths ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_posts_json_to_the_entity_resource_with_bearer_token_and_minor_version()
    {
        _server.Reply(200, Entity("Customer", """{"Id":"58","SyncToken":"0","DisplayName":"King's Groceries","Active":true,"domain":"QBO","sparse":false}"""));

        var result = await _provider.CreateAsync(Ctx(), FullCustomer());

        result.Outcome.Should().Be(ProviderOutcomeKind.Succeeded);
        result.Value.Should().Be(new RemoteRecord("58", "0", "King's Groceries", null, null, null, true));

        var request = _server.Requests.Single();
        request.Method.Should().Be("POST");
        request.PathAndQuery.Should().StartWith(CompanyPath("customer")).And.Contain("minorversion=75");
        request.Header("Content-Type").Should().StartWith("application/json");
        request.Header("Authorization").Should().Be($"Bearer {AccessToken}");

        var body = JObject.Parse(request.Body);
        body["DisplayName"]!.Value<string>().Should().Be("King's Groceries");
        body.ContainsKey("Id").Should().BeFalse();
        body.ContainsKey("sparse").Should().BeFalse();

        JToken.DeepEquals(JObject.Parse(result.RequestJson!), body).Should().BeTrue("the logged request is exactly the body sent");
        result.RequestJson.Should().NotContain(AccessToken);
    }

    [Fact]
    public async Task Update_posts_a_sparse_body()
    {
        _server.Reply(200, Entity("Vendor", """{"Id":"31","SyncToken":"5","DisplayName":"Acme Supplies","Active":true}"""));

        var result = await _provider.UpdateAsync(Ctx(), new RemoteVendor { DisplayName = "Acme Supplies", AccountNumber = "ACC-9" }, "31", "4");

        result.Value.Should().Be(new RemoteRecord("31", "5", "Acme Supplies", null));
        var request = _server.Requests.Single();
        request.PathAndQuery.Should().StartWith(CompanyPath("vendor"));
        var body = JObject.Parse(request.Body);
        body["sparse"]!.Value<bool>().Should().BeTrue();
        body["Id"]!.Value<string>().Should().Be("31");
        body["SyncToken"]!.Value<string>().Should().Be("4");
        body["AcctNum"]!.Value<string>().Should().Be("ACC-9");
        body.Properties().Select(p => p.Name).Should().BeEquivalentTo("DisplayName", "AcctNum", "Active", "Id", "SyncToken", "sparse");
    }

    [Fact]
    public async Task Invoice_create_reads_totals_back()
    {
        _server.Reply(200, Entity("Invoice", """
            {"Id":"145","SyncToken":"0","DocNumber":"INV-1001","TotalAmt":69.35,"TxnTaxDetail":{"TotalTax":9.35},
             "Line":[{"Id":"1","LineNum":1,"Amount":25.0,"DetailType":"SalesItemLineDetail","SalesItemLineDetail":{"ItemRef":{"value":"11"},"UnitPrice":12.5,"Qty":2}}]}
            """));

        var result = await _provider.CreateAsync(Ctx(), FullInvoice());

        result.Value.Should().Be(new RemoteRecord("145", "0", null, "INV-1001", 69.35m, 9.35m, true));
        var body = JObject.Parse(_server.Requests.Single().Body);
        body["Line"]![0]!["SalesItemLineDetail"]!["UnitPrice"]!.Value<decimal>().Should().Be(12.50m);
        body["Line"]![2]!["DetailType"]!.Value<string>().Should().Be("DiscountLineDetail");
    }

    [Fact]
    public async Task A_foreign_bill_reaches_QuickBooks_with_its_exchange_rate_and_currency()
    {
        _server.Reply(200, Entity("Bill", """{"Id":"200","SyncToken":"0","DocNumber":"SUP-INV-77","TotalAmt":87.5,"CurrencyRef":{"value":"USD"},"ExchangeRate":278.5}"""));
        var bill = FullBill();
        bill.CurrencyCode = "USD";
        bill.ExchangeRate = 278.5m;

        var result = await _provider.CreateAsync(Ctx(), bill);

        result.Outcome.Should().Be(ProviderOutcomeKind.Succeeded);
        var request = _server.Requests.Single();
        request.PathAndQuery.Should().StartWith(CompanyPath("bill"));
        var body = JObject.Parse(request.Body);
        body["ExchangeRate"]!.Value<decimal>().Should().Be(278.5m);
        body["CurrencyRef"]!["value"]!.Value<string>().Should().Be("USD");
        JObject.Parse(result.RequestJson!)["ExchangeRate"]!.Value<decimal>().Should().Be(278.5m, "the sync log shows the rate that was sent");
    }

    [Fact]
    public async Task Get_by_id_is_a_GET_on_the_entity_id()
    {
        _server.Reply(200, Entity("Item", """{"Id":"11","SyncToken":"2","Name":"Widget","Active":false,"Type":"NonInventory"}"""));

        var result = await _provider.GetByIdAsync(Ctx(), SyncKind.Item, "11");

        result.Value.Should().Be(new RemoteRecord("11", "2", "Widget", null, null, null, false));
        var request = _server.Requests.Single();
        request.Method.Should().Be("GET");
        request.PathAndQuery.Should().StartWith(CompanyPath("item/11"));
    }

    [Fact]
    public async Task Find_posts_the_escaped_query_as_text()
    {
        _server.Reply(200, QueryResponse("Customer", """{"Id":"58","SyncToken":"1","DisplayName":"O'Brien","Active":true}"""));

        var result = await _provider.FindAsync(Ctx(), SyncKind.Customer, new RemoteLookup(Name: "O'Brien"));

        result.Value.Should().Be(new RemoteRecord("58", "1", "O'Brien", null));
        var request = _server.Requests.Single();
        request.Method.Should().Be("POST");
        request.PathAndQuery.Should().StartWith(CompanyPath("query"));
        request.Header("Content-Type").Should().StartWith("application/text");
        request.Body.Should().Be(@"select * from Customer where DisplayName = 'O\'Brien' and Active IN (true, false)");
    }

    [Fact]
    public async Task Find_with_an_empty_query_response_is_null()
    {
        _server.Reply(200, QueryResponse("Invoice"));

        var result = await _provider.FindAsync(Ctx(), SyncKind.SalesInvoice, new RemoteLookup(DocNumber: "NOPE"));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task Unicode_names_reach_QuickBooks_intact()
    {
        _server.Reply(200, QueryResponse("Vendor"));

        await _provider.FindAsync(Ctx(), SyncKind.Vendor, new RemoteLookup(Name: "Café Zürich 株式会社"));

        _server.Requests.Single().Body.Should().Contain("'Café Zürich 株式会社'");
    }

    [Fact]
    public async Task List_maps_rows()
    {
        _server.Reply(200, QueryResponse("Vendor",
            """{"Id":"1","SyncToken":"0","DisplayName":"A","AcctNum":"V-1","PrimaryEmailAddr":{"Address":"a@x.example"},"TaxIdentifier":"XXXX1234","CurrencyRef":{"value":"PKR"},"Active":true}""",
            """{"Id":"2","SyncToken":"0","DisplayName":"B","Active":false}"""));

        var result = await _provider.ListAsync(Ctx(), SyncKind.Vendor, 1, 100);

        result.Value.Should().Equal(
            new RemoteListEntry("1", "A", null, "a@x.example", "XXXX1234", "V-1", null, "PKR", true),
            new RemoteListEntry("2", "B", null, null, null, null, null, null, false));
        _server.Requests.Single().Body.Should().Be("select * from Vendor where Active IN (true, false) STARTPOSITION 1 MAXRESULTS 100");
    }

    [Fact]
    public async Task Void_posts_operation_void_with_id_and_token()
    {
        _server.Reply(200, Entity("Invoice", """{"Id":"145","SyncToken":"3","DocNumber":"INV-1001","PrivateNote":"Voided","TotalAmt":0}"""));

        var result = await _provider.VoidInvoiceAsync(Ctx(), "145", "2");

        result.Value.Should().Be(new RemoteRecord("145", "3", null, "INV-1001", 0m, null, false));
        var request = _server.Requests.Single();
        request.Method.Should().Be("POST");
        request.PathAndQuery.Should().StartWith(CompanyPath("invoice")).And.Contain("operation=void");
        JObject.Parse(request.Body).Properties().Select(p => p.Name).Should().BeEquivalentTo("Id", "SyncToken");
    }

    [Fact]
    public async Task Company_info_is_read_by_realm()
    {
        _server.Reply(200, Entity("CompanyInfo", """{"Id":"1","SyncToken":"3","CompanyName":"Sandbox Co","LegalName":"Sandbox Co Ltd","Country":"PK","Email":{"Address":"x@sandbox.example"}}"""));

        var result = await _provider.GetCompanyInfoAsync(Ctx());

        result.Value.Should().Be(new RemoteCompanyInfo("Sandbox Co", "Sandbox Co Ltd", "PK", "x@sandbox.example"));
        _server.Requests.Single().PathAndQuery.Should().StartWith(CompanyPath($"companyinfo/{RealmId}"));
    }

    [Fact]
    public async Task Reference_data_end_to_end()
    {
        _server
            .Reply(200, Entity("Preferences", """{"CurrencyPrefs":{"MultiCurrencyEnabled":true,"HomeCurrency":{"value":"PKR"}},"SalesFormsPrefs":{"CustomTxnNumbers":true},"TaxPrefs":{"UsingSalesTax":true},"Id":"1","SyncToken":"0"}"""))
            .Reply(200, Entity("CompanyInfo", """{"Id":"1","SyncToken":"0","CompanyName":"Sandbox Co","Country":"PK"}"""))
            .Reply(200, QueryResponse("Account", """{"Id":"79","SyncToken":"0","Name":"Sales","AccountType":"Income","Classification":"Revenue","AccountSubType":"SalesOfProductIncome","Active":true}"""))
            .Reply(200, QueryResponse("TaxCode", """{"Id":"3","SyncToken":"0","Name":"GST 17","Taxable":true,"Active":true,"SalesTaxRateList":{"TaxRateDetail":[{"TaxRateRef":{"value":"4"}}]}}"""))
            .Reply(200, QueryResponse("Term", """{"Id":"3","SyncToken":"0","Name":"Net 30","DueDays":30,"Active":true}"""))
            .Reply(200, QueryResponse("TaxRate", """{"Id":"4","SyncToken":"0","Name":"GST (Sales)","RateValue":17,"Active":true}"""))
            .Reply(200, QueryResponse("CompanyCurrency", """{"Id":"1","SyncToken":"0","Code":"USD","Name":"United States Dollar","Active":true}"""));

        var result = await _provider.GetReferenceDataAsync(Ctx());

        result.IsSuccess.Should().BeTrue(result.Message);
        var data = result.Value!;
        data.Preferences.Should().Be(new RemotePreferences("PKR", true, true, true));
        data.CompanyInfo.Should().Be(new RemoteCompanyInfo("Sandbox Co", null, "PK", null));
        data.Accounts.Should().Equal(new RemoteAccount("79", "Sales", "Income", "SalesOfProductIncome", "Revenue", null, true));
        data.TaxCodes.Should().Equal(new RemoteTaxCode("3", "GST 17", null, true, 17m, true));
        data.Terms.Should().Equal(new RemoteTerm("3", "Net 30", 30, true));
        data.Currencies.Should().Equal(new RemoteCurrency("USD", "United States Dollar"));

        var requests = _server.Requests;
        requests.Should().HaveCount(7);
        requests[0].PathAndQuery.Should().StartWith(CompanyPath("preferences"));
        requests[1].PathAndQuery.Should().StartWith(CompanyPath($"companyinfo/{RealmId}"));
        requests.Skip(2).Select(r => r.Body).Should().Equal(
            "select * from Account STARTPOSITION 1 MAXRESULTS 1000",
            "select * from TaxCode STARTPOSITION 1 MAXRESULTS 1000",
            "select * from Term STARTPOSITION 1 MAXRESULTS 1000",
            "select * from TaxRate STARTPOSITION 1 MAXRESULTS 1000",
            "select * from CompanyCurrency STARTPOSITION 1 MAXRESULTS 1000");
    }

    // ── Failure paths: the exception shapes the translator depends on ──────────────────────

    [Fact]
    public async Task Duplicate_name_400_is_Duplicate_with_the_intuit_tid()
    {
        _server.Reply(400, ValidationFault("6240", "Duplicate Name Exists Error",
            "The name supplied already exists. : Another customer, vendor or employee is already using this name."), intuitTid: "1-abc-dup");

        var result = await _provider.CreateAsync(Ctx(), FullCustomer());

        result.Outcome.Should().Be(ProviderOutcomeKind.Duplicate);
        result.ErrorCode.Should().Be("6240");
        result.IntuitTid.Should().Be("1-abc-dup");
        result.Message.Should().Contain("already exists");
    }

    [Fact]
    public async Task Stale_object_400_is_StaleObject()
    {
        _server.Reply(400, ValidationFault("5010", "Stale Object Error", "Stale Object Error : You and root were working on this at the same time."));

        (await _provider.UpdateAsync(Ctx(), FullCustomer(), "58", "1")).Outcome.Should().Be(ProviderOutcomeKind.StaleObject);
    }

    [Fact]
    public async Task Validation_400_is_Refused_with_the_field()
    {
        _server.Reply(400, ValidationFault("2050", "String length is either shorter or longer than supported by specification",
            "String length specified does not match the supported length. Min:0 wanted, Max:100 allowed.", "DisplayName"));

        var result = await _provider.CreateAsync(Ctx(), FullCustomer());

        result.Outcome.Should().Be(ProviderOutcomeKind.Refused);
        result.ErrorCode.Should().Be("2050");
        result.ErrorField.Should().Be("DisplayName");
    }

    [Fact]
    public async Task Object_not_found_400_on_read_is_NotFound()
    {
        _server.Reply(400, ValidationFault("610", "Object Not Found", "Object Not Found : Something you're trying to use has been made inactive."));

        (await _provider.GetByIdAsync(Ctx(), SyncKind.Bill, "999")).Outcome.Should().Be(ProviderOutcomeKind.NotFound);
    }

    [Fact]
    public async Task Unauthorized_401_is_AuthRevoked()
    {
        _server.Reply(401, FaultJson("AuthenticationFault", ("3200", "message=AuthenticationFailed; errorCode=003200; statusCode=401", null, null)), intuitTid: "tid-auth");

        var result = await _provider.CreateAsync(Ctx(), FullCustomer());

        result.Outcome.Should().Be(ProviderOutcomeKind.AuthRevoked);
        result.ErrorCode.Should().Be("3200");
        result.IntuitTid.Should().Be("tid-auth");
    }

    [Fact]
    public async Task Too_many_requests_429_is_Throttled()
    {
        _server.Reply(429, FaultJson("ServiceFault", ("003001", "message=ThrottleExceeded; errorCode=003001; statusCode=429", null, null)));

        (await _provider.CreateAsync(Ctx(), FullCustomer())).Outcome.Should().Be(ProviderOutcomeKind.Throttled);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(404)]
    public async Task Server_errors_are_Transient(int status)
    {
        _server.Reply(status, "{\"error\":\"upstream\"}", intuitTid: "tid-5xx");

        var result = await _provider.CreateAsync(Ctx(), FullCustomer());

        result.Outcome.Should().Be(ProviderOutcomeKind.Transient);
        result.ErrorCode.Should().Be(status.ToString());
        result.IntuitTid.Should().Be("tid-5xx");
    }

    [Fact]
    public async Task Forbidden_403_is_AuthRevoked()
    {
        _server.Reply(403, FaultJson("AuthorizationFault", ("003100", "message=ApplicationAuthorizationFailed; errorCode=003100; statusCode=403", null, null)));

        var result = await _provider.CreateAsync(Ctx(), FullCustomer());

        result.Outcome.Should().Be(ProviderOutcomeKind.AuthRevoked);
        result.ErrorCode.Should().Be("403");
    }

    [Fact]
    public async Task A_dropped_connection_is_Transient()
    {
        _server.DropConnection();

        var result = await _provider.CreateAsync(Ctx(), FullCustomer());

        result.Outcome.Should().Be(ProviderOutcomeKind.Transient);
        result.ErrorCode.Should().Be("CommunicationException");
    }

    [Fact]
    public async Task A_fault_inside_a_200_body_is_still_a_refusal()
    {
        _server.Reply(200, ValidationFault("2500", "Invalid Reference Id", "Invalid Reference Id : Accounts element id 999 not found"));

        var result = await _provider.CreateAsync(Ctx(), FullItem());

        result.Outcome.Should().Be(ProviderOutcomeKind.Refused);
        result.ErrorCode.Should().Be("2500");
    }

    [Fact]
    public async Task An_unreadable_200_body_is_Transient()
    {
        _server.Reply(200, "<html>proxy error</html>");

        (await _provider.CreateAsync(Ctx(), FullCustomer())).Outcome.Should().Be(ProviderOutcomeKind.Transient);
    }

    [Fact]
    public async Task A_400_with_an_unreadable_body_is_Refused()
    {
        _server.Reply(400, "<html>bad request</html>");

        var result = await _provider.CreateAsync(Ctx(), FullCustomer());

        result.Outcome.Should().Be(ProviderOutcomeKind.Refused);
        result.ErrorCode.Should().Be("400");
    }

    [Fact]
    public async Task A_required_reference_part_failing_names_the_part()
    {
        _server
            .Reply(200, Entity("Preferences", """{"CurrencyPrefs":{"MultiCurrencyEnabled":false,"HomeCurrency":{"value":"PKR"}},"Id":"1","SyncToken":"0"}"""))
            .Reply(503, "{}");

        var result = await _provider.GetReferenceDataAsync(Ctx());

        result.Outcome.Should().Be(ProviderOutcomeKind.Transient);
        result.Message.Should().StartWith("CompanyInfo: ");
    }

    [Fact]
    public async Task Optional_reference_parts_may_fail()
    {
        _server
            .Reply(200, Entity("Preferences", """{"CurrencyPrefs":{"MultiCurrencyEnabled":true,"HomeCurrency":{"value":"PKR"}},"Id":"1","SyncToken":"0"}"""))
            .Reply(200, Entity("CompanyInfo", """{"Id":"1","SyncToken":"0","CompanyName":"Sandbox Co"}"""))
            .Reply(200, QueryResponse("Account"))
            .Reply(200, QueryResponse("TaxCode", """{"Id":"3","SyncToken":"0","Name":"GST 17","SalesTaxRateList":{"TaxRateDetail":[{"TaxRateRef":{"value":"4"}}]}}"""))
            .Reply(200, QueryResponse("Term"))
            .Reply(500, "{}")
            .Reply(400, ValidationFault("5030", "Feature not supported"));

        var result = await _provider.GetReferenceDataAsync(Ctx());

        result.IsSuccess.Should().BeTrue(result.Message);
        result.Value!.TaxCodes.Single().RatePercent.Should().BeNull();
        result.Value.Currencies.Should().BeEmpty();
    }
}

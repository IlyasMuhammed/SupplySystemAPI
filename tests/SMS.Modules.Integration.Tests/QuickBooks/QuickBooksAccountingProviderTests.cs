using FluentAssertions;
using Newtonsoft.Json.Linq;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Providers.QuickBooks;
using SMS.Modules.Integration.Tests.QuickBooks.Support;
using SMS.Shared.Integration.QuickBooks;
using static SMS.Modules.Integration.Tests.QuickBooks.Support.QboTestKit;
using Qbo = Intuit.Ipp.Data;

namespace SMS.Modules.Integration.Tests.QuickBooks;

/// <summary>Provider behaviour against a scripted client (no SDK HTTP). The real SDK path is in the loopback tests.</summary>
public class QuickBooksAccountingProviderTests
{
    private readonly FakeQboClient _client = new();
    private readonly StubContextFactory _contexts = new();
    private QuickBooksAccountingProvider Sut => Provider(_contexts, _client);

    [Fact]
    public void Provider_key_is_QBO()
    {
        Sut.ProviderKey.Should().Be(ProviderKeys.QuickBooksOnline).And.Be("QBO");
    }

    // ── Create ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_customer_adds_the_mapped_customer_and_returns_its_identity()
    {
        _client.OnAdd = e => SavedCustomer("58", "0", ((Qbo.Customer)e).DisplayName);

        var result = await Sut.CreateAsync(Ctx(), FullCustomer());

        result.Outcome.Should().Be(ProviderOutcomeKind.Succeeded);
        result.Value.Should().Be(new RemoteRecord("58", "0", "King's Groceries", null, null, null, true));
        var sent = _client.Calls.Should().ContainSingle().Which;
        sent.Method.Should().Be("Add");
        var customer = sent.Argument.Should().BeOfType<Qbo.Customer>().Subject;
        customer.PrimaryTaxIdentifier.Should().Be("3520212345671");
        customer.sparseSpecified.Should().BeFalse("a create is never sparse");
        customer.Id.Should().BeNull();

        _contexts.Calls.Should().Be(1);
        _contexts.LastContext.Should().Be(Ctx());
        result.DurationMs.Should().BeGreaterThanOrEqualTo(0);
        JObject.Parse(result.RequestJson!)["DisplayName"]!.Value<string>().Should().Be("King's Groceries");
        JObject.Parse(result.ResponseJson!)["Id"]!.Value<string>().Should().Be("58");
    }

    [Fact]
    public async Task Create_each_kind_sends_the_matching_SDK_type()
    {
        _client.OnAdd = e => e switch
        {
            Qbo.Vendor v  => new Qbo.Vendor { Id = "31", SyncToken = "0", DisplayName = v.DisplayName },
            Qbo.Item i    => new Qbo.Item { Id = "11", SyncToken = "0", Name = i.Name },
            Qbo.Invoice   => new Qbo.Invoice { Id = "145", SyncToken = "0", DocNumber = "INV-1001", TotalAmt = 70m, TotalAmtSpecified = true,
                                 TxnTaxDetail = new Qbo.TxnTaxDetail { TotalTax = 10m, TotalTaxSpecified = true } },
            Qbo.Bill      => new Qbo.Bill { Id = "200", SyncToken = "0", DocNumber = "SUP-INV-77" },
            _ => throw new InvalidOperationException()
        };

        (await Sut.CreateAsync(Ctx(), FullVendor())).Value.Should().Be(new RemoteRecord("31", "0", "Acme Supplies", null));
        (await Sut.CreateAsync(Ctx(), FullItem())).Value.Should().Be(new RemoteRecord("11", "0", "Widget - Blue", null));
        (await Sut.CreateAsync(Ctx(), FullInvoice())).Value.Should().Be(new RemoteRecord("145", "0", null, "INV-1001", 70m, 10m));
        (await Sut.CreateAsync(Ctx(), FullBill())).Value.Should().Be(new RemoteRecord("200", "0", null, "SUP-INV-77"));

        _client.Calls.Select(c => c.Argument.GetType()).Should().Equal(typeof(Qbo.Vendor), typeof(Qbo.Item), typeof(Qbo.Invoice), typeof(Qbo.Bill));
    }

    [Fact]
    public async Task A_response_of_the_wrong_kind_is_an_unknown_outcome()
    {
        _client.OnAdd = _ => new Qbo.Vendor { Id = "1", SyncToken = "0" };

        var result = await Sut.CreateAsync(Ctx(), FullCustomer());

        result.Outcome.Should().Be(ProviderOutcomeKind.Transient);
        result.Message.Should().Contain("Vendor").And.Contain("Customer");
    }

    [Fact]
    public async Task A_record_without_an_id_is_an_unknown_outcome()
    {
        _client.OnAdd = _ => new Qbo.Customer { SyncToken = "0", DisplayName = "x" };

        (await Sut.CreateAsync(Ctx(), FullCustomer())).Outcome.Should().Be(ProviderOutcomeKind.Transient);
    }

    [Fact]
    public async Task Sdk_failure_is_translated_and_keeps_request_tid_and_duration()
    {
        _client.OnAdd = _ => throw Http400(ValidationFault("6240", "Duplicate Name Exists Error", "The name supplied already exists."), tid: "abc-123");

        var result = await Sut.CreateAsync(Ctx(), FullCustomer());

        result.Outcome.Should().Be(ProviderOutcomeKind.Duplicate);
        result.ErrorCode.Should().Be("6240");
        result.IntuitTid.Should().Be("abc-123");
        result.Value.Should().BeNull();
        result.RequestJson.Should().Contain("King's Groceries");
        JObject.Parse(result.ResponseJson!)["errorCode"]!.Value<string>().Should().Be("6240");
        result.DurationMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Object_not_found_on_create_is_refused_not_not_found()
    {
        _client.OnAdd = _ => throw Http400(ValidationFault("610", "Object Not Found", "Something you're trying to use has been made inactive."));

        (await Sut.CreateAsync(Ctx(), FullInvoice())).Outcome.Should().Be(ProviderOutcomeKind.Refused);
    }

    [Fact]
    public async Task Request_and_response_json_are_redacted()
    {
        var customer = FullCustomer();
        customer.Notes = "Authorization: Bearer eyJsecret.token.value";
        _client.OnAdd = e =>
        {
            var saved = SavedCustomer();
            saved.Notes = ((Qbo.Customer)e).Notes;
            return saved;
        };

        var result = await Sut.CreateAsync(Ctx(), customer);

        result.IsSuccess.Should().BeTrue();
        result.RequestJson.Should().NotContain("eyJsecret.token.value").And.Contain("REDACTED");
        result.ResponseJson.Should().NotContain("eyJsecret.token.value").And.Contain("REDACTED");
    }

    [Fact]
    public async Task The_access_token_never_appears_in_the_logged_json()
    {
        _client.OnAdd = _ => SavedCustomer();

        var result = await Sut.CreateAsync(Ctx(), FullCustomer());

        result.RequestJson.Should().NotContain(AccessToken);
        result.ResponseJson.Should().NotContain(AccessToken);
    }

    // ── Update ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_is_sparse_with_id_and_token_and_leaves_absent_fields_unset()
    {
        _client.OnUpdate = _ => SavedCustomer("58", "4");

        var result = await Sut.UpdateAsync(Ctx(), new RemoteCustomer { DisplayName = "King's Groceries", Email = "new@kings.example" }, "58", "3");

        result.Value.Should().Be(new RemoteRecord("58", "4", "King's Groceries", null));
        var sent = (Qbo.Customer)_client.Calls.Single(c => c.Method == "Update").Argument;
        sent.Id.Should().Be("58");
        sent.SyncToken.Should().Be("3");
        sent.sparse.Should().BeTrue();
        sent.sparseSpecified.Should().BeTrue();
        sent.PrimaryEmailAddr!.Address.Should().Be("new@kings.example");
        sent.PrimaryPhone.Should().BeNull();
        sent.BillAddr.Should().BeNull();
        sent.Notes.Should().BeNull();
        sent.CurrencyRef.Should().BeNull();

        var body = JObject.Parse(result.RequestJson!);
        body["sparse"]!.Value<bool>().Should().BeTrue();
        body.ContainsKey("PrimaryPhone").Should().BeFalse();
    }

    [Fact]
    public async Task Update_invoice_is_sparse_too()
    {
        _client.OnUpdate = _ => new Qbo.Invoice { Id = "145", SyncToken = "6", DocNumber = "INV-1001" };

        var result = await Sut.UpdateAsync(Ctx(), FullInvoice(), "145", "5");

        result.Value!.SyncToken.Should().Be("6");
        var sent = (Qbo.Invoice)_client.Calls.Single().Argument;
        sent.sparse.Should().BeTrue();
        sent.SyncToken.Should().Be("5");
    }

    [Theory]
    [InlineData("", "1")]
    [InlineData("58", " ")]
    public async Task Update_requires_id_and_token(string id, string token)
    {
        var act = () => Sut.UpdateAsync(Ctx(), FullCustomer(), id, token);

        await act.Should().ThrowAsync<ArgumentException>();
        _contexts.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Stale_token_on_update_is_StaleObject()
    {
        _client.OnUpdate = _ => throw Http400(ValidationFault("5010", "Stale Object Error", "You and root were working on this at the same time."));

        (await Sut.UpdateAsync(Ctx(), FullCustomer(), "58", "1")).Outcome.Should().Be(ProviderOutcomeKind.StaleObject);
    }

    // ── Get / Find / List / Void ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SyncKind.Customer, typeof(Qbo.Customer))]
    [InlineData(SyncKind.Vendor, typeof(Qbo.Vendor))]
    [InlineData(SyncKind.Item, typeof(Qbo.Item))]
    [InlineData(SyncKind.SalesInvoice, typeof(Qbo.Invoice))]
    [InlineData(SyncKind.Bill, typeof(Qbo.Bill))]
    public async Task Get_by_id_asks_for_the_right_entity(SyncKind kind, Type sdkType)
    {
        _client.OnFindById = e =>
        {
            var found = (Qbo.IntuitEntity)Activator.CreateInstance(e.GetType())!;
            found.Id = ((Qbo.IntuitEntity)e).Id;
            found.SyncToken = "9";
            return (Qbo.IEntity)found;
        };

        var result = await Sut.GetByIdAsync(Ctx(), kind, "77");

        result.IsSuccess.Should().BeTrue();
        result.Value!.RemoteId.Should().Be("77");
        result.Value.SyncToken.Should().Be("9");
        var probe = _client.Calls.Single().Argument;
        probe.GetType().Should().Be(sdkType);
        ((Qbo.IntuitEntity)probe).Id.Should().Be("77");
        JObject.Parse(result.RequestJson!)["findById"]!["entity"]!.Value<string>().Should().Be(sdkType.Name);
    }

    [Fact]
    public async Task Get_by_id_with_nothing_returned_is_NotFound()
    {
        _client.OnFindById = _ => null;

        var result = await Sut.GetByIdAsync(Ctx(), SyncKind.Customer, "404");

        result.Outcome.Should().Be(ProviderOutcomeKind.NotFound);
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task Get_by_id_object_not_found_fault_is_NotFound()
    {
        _client.OnFindById = _ => throw Http400(ValidationFault("610", "Object Not Found", "Object Not Found : Something you're trying to use has been made inactive."));

        (await Sut.GetByIdAsync(Ctx(), SyncKind.Item, "404")).Outcome.Should().Be(ProviderOutcomeKind.NotFound);
    }

    [Fact]
    public async Task Find_customer_by_display_name_escapes_and_includes_inactive()
    {
        _client.OnQuery = (_, _) => [SavedCustomer("58", "2", "O'Brien")];

        var result = await Sut.FindAsync(Ctx(), SyncKind.Customer, new RemoteLookup(Name: "O'Brien"));

        result.Value.Should().Be(new RemoteRecord("58", "2", "O'Brien", null));
        _client.Queries.Single().Should().Be(@"select * from Customer where DisplayName = 'O\'Brien' and Active IN (true, false)");
        JObject.Parse(result.RequestJson!)["query"]!.Value<string>().Should().Be(_client.Queries.Single());
        JObject.Parse(result.ResponseJson!)["count"]!.Value<int>().Should().Be(1);
    }

    [Fact]
    public async Task Find_returns_null_when_nothing_matches()
    {
        _client.OnQuery = (_, _) => [];

        var result = await Sut.FindAsync(Ctx(), SyncKind.Vendor, new RemoteLookup(Name: "Nobody"));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task Find_prefers_an_active_record()
    {
        _client.OnQuery = (_, _) =>
        [
            new Qbo.Item { Id = "1", SyncToken = "0", Name = "Widget", Active = false, ActiveSpecified = true },
            new Qbo.Item { Id = "2", SyncToken = "0", Name = "Widget", Active = true, ActiveSpecified = true }
        ];

        (await Sut.FindAsync(Ctx(), SyncKind.Item, new RemoteLookup(Name: "Widget"))).Value!.RemoteId.Should().Be("2");
    }

    [Fact]
    public async Task Find_item_falls_back_to_sku()
    {
        _client.OnQuery = (_, _) => [new Qbo.Item { Id = "11", SyncToken = "0", Name = "Widget", Sku = "WID" }];

        var result = await Sut.FindAsync(Ctx(), SyncKind.Item, new RemoteLookup { Sku = "WID" });

        result.Value!.RemoteId.Should().Be("11");
        _client.Queries.Single().Should().Be("select * from Item where Sku = 'WID' and Active IN (true, false)");
    }

    [Fact]
    public async Task Find_item_prefers_name_over_sku()
    {
        _client.OnQuery = (_, _) => [];

        await Sut.FindAsync(Ctx(), SyncKind.Item, new RemoteLookup(Name: "Widget") { Sku = "WID" });

        _client.Queries.Single().Should().Contain("Name = 'Widget'");
    }

    [Fact]
    public async Task Find_invoice_by_doc_number()
    {
        _client.OnQuery = (t, _) => t == typeof(Qbo.Invoice) ? [new Qbo.Invoice { Id = "145", SyncToken = "1", DocNumber = "INV-1001" }] : [];

        var result = await Sut.FindAsync(Ctx(), SyncKind.SalesInvoice, new RemoteLookup(DocNumber: "INV-1001"));

        result.Value.Should().Be(new RemoteRecord("145", "1", null, "INV-1001"));
        _client.Queries.Single().Should().Be("select * from Invoice where DocNumber = 'INV-1001'");
    }

    [Fact]
    public async Task Find_bill_filters_by_vendor_when_given()
    {
        _client.OnQuery = (_, _) =>
        [
            new Qbo.Bill { Id = "1", SyncToken = "0", DocNumber = "77", VendorRef = new Qbo.ReferenceType { Value = "30" } },
            new Qbo.Bill { Id = "2", SyncToken = "0", DocNumber = "77", VendorRef = new Qbo.ReferenceType { Value = "31" } }
        ];

        var forVendor = await Sut.FindAsync(Ctx(), SyncKind.Bill, new RemoteLookup(DocNumber: "77", VendorRemoteId: "31"));
        var otherVendor = await Sut.FindAsync(Ctx(), SyncKind.Bill, new RemoteLookup(DocNumber: "77", VendorRemoteId: "99"));
        var anyVendor = await Sut.FindAsync(Ctx(), SyncKind.Bill, new RemoteLookup(DocNumber: "77"));

        forVendor.Value!.RemoteId.Should().Be("2");
        otherVendor.Value.Should().BeNull();
        anyVendor.Value!.RemoteId.Should().Be("1");
        _client.Queries.Should().AllBe("select * from Bill where DocNumber = '77'");
    }

    private static readonly Dictionary<string, RemoteLookup> EmptyLookups = new()
    {
        ["none"]        = new RemoteLookup(),
        ["docOnly"]     = new RemoteLookup(DocNumber: "X"),
        ["blankName"]   = new RemoteLookup(Name: " "),
        ["nameOnly"]    = new RemoteLookup(Name: "X"),
        ["vendorOnly"]  = new RemoteLookup(VendorRemoteId: "31")
    };

    [Theory]
    [InlineData(SyncKind.Customer, "none")]
    [InlineData(SyncKind.Customer, "docOnly")]
    [InlineData(SyncKind.Vendor, "blankName")]
    [InlineData(SyncKind.Item, "docOnly")]
    [InlineData(SyncKind.SalesInvoice, "nameOnly")]
    [InlineData(SyncKind.Bill, "vendorOnly")]
    public async Task Find_with_nothing_to_search_by_is_a_caller_error(SyncKind kind, string lookupName)
    {
        var lookup = EmptyLookups[lookupName];
        var act = () => Sut.FindAsync(Ctx(), kind, lookup);

        await act.Should().ThrowAsync<ArgumentException>();
        _contexts.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Void_sends_id_and_token_and_returns_an_inactive_record()
    {
        _client.OnVoid = _ => new Qbo.Invoice { Id = "145", SyncToken = "3", DocNumber = "INV-1001", PrivateNote = "Voided", TotalAmt = 0, TotalAmtSpecified = true };

        var result = await Sut.VoidInvoiceAsync(Ctx(), "145", "2");

        result.Value.Should().Be(new RemoteRecord("145", "3", null, "INV-1001", 0m, null, false));
        var sent = _client.Calls.Single().Argument.Should().BeOfType<Qbo.Invoice>().Subject;
        sent.Id.Should().Be("145");
        sent.SyncToken.Should().Be("2");
        sent.Line.Should().BeNull();
        JObject.Parse(result.RequestJson!).Properties().Select(p => p.Name).Should().BeEquivalentTo("Id", "SyncToken");
    }

    [Theory]
    [InlineData(SyncKind.Customer)]
    [InlineData(SyncKind.Vendor)]
    [InlineData(SyncKind.Item)]
    public async Task List_pages_are_capped_at_1000_and_mapped(SyncKind kind)
    {
        _client.OnQuery = (t, _) => t == typeof(Qbo.Customer) ? [SavedCustomer("1", "0", "A")]
            : t == typeof(Qbo.Vendor) ? [new Qbo.Vendor { Id = "2", SyncToken = "0", DisplayName = "B", AcctNum = "V-1" }]
            : [new Qbo.Item { Id = "3", SyncToken = "0", Name = "C", Sku = "S-1" }];

        var result = await Sut.ListAsync(Ctx(), kind, 1, 5000);

        _client.Queries.Single().Should().EndWith("STARTPOSITION 1 MAXRESULTS 1000");
        var entry = result.Value.Should().ContainSingle().Subject;
        switch (kind)
        {
            case SyncKind.Customer: entry.Name.Should().Be("A"); break;
            case SyncKind.Vendor: entry.AccountNumber.Should().Be("V-1"); break;
            default: entry.Sku.Should().Be("S-1"); break;
        }
    }

    [Theory]
    [InlineData(SyncKind.SalesInvoice)]
    [InlineData(SyncKind.Bill)]
    public async Task Documents_cannot_be_listed(SyncKind kind)
    {
        var act = () => Sut.ListAsync(Ctx(), kind, 1, 10);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Company_info_reads_the_realm()
    {
        _client.OnFindById = e => new Qbo.CompanyInfo { Id = ((Qbo.CompanyInfo)e).Id, CompanyName = "Sandbox Co", Country = "PK" };

        var result = await Sut.GetCompanyInfoAsync(Ctx());

        result.Value.Should().Be(new RemoteCompanyInfo("Sandbox Co", null, "PK", null));
        ((Qbo.CompanyInfo)_client.Calls.Single().Argument).Id.Should().Be(RealmId);
    }

    // ── Reference data ─────────────────────────────────────────────────────────────────────

    private void ScriptReferenceData(bool multiCurrency, Func<string, IEnumerable<object>?>? overrideQuery = null)
    {
        _client.OnFindById = e => e switch
        {
            Qbo.Preferences => new Qbo.Preferences
            {
                CurrencyPrefs   = new Qbo.CurrencyPrefs { MultiCurrencyEnabled = multiCurrency, MultiCurrencyEnabledSpecified = true, HomeCurrency = new Qbo.ReferenceType { Value = "PKR" } },
                SalesFormsPrefs = new Qbo.SalesFormsPrefs { CustomTxnNumbers = true, CustomTxnNumbersSpecified = true },
                TaxPrefs        = new Qbo.TaxPrefs { UsingSalesTax = true, UsingSalesTaxSpecified = true }
            },
            Qbo.CompanyInfo => new Qbo.CompanyInfo { Id = RealmId, CompanyName = "Sandbox Co", LegalName = "Sandbox Co Ltd", Country = "PK" },
            _ => null
        };
        _client.OnQuery = (_, query) =>
        {
            if (overrideQuery?.Invoke(query) is { } scripted) return scripted;
            if (query.Contains("from Account")) return [new Qbo.Account { Id = "79", Name = "Sales", AccountType = Qbo.AccountTypeEnum.Income, AccountTypeSpecified = true }];
            if (query.Contains("from TaxCode")) return [new Qbo.TaxCode
            {
                Id = "3", Name = "GST 17",
                SalesTaxRateList = new Qbo.TaxRateList { TaxRateDetail = [new Qbo.TaxRateDetail { TaxRateRef = new Qbo.ReferenceType { Value = "4" } }] }
            }];
            if (query.Contains("from TaxRate")) return [new Qbo.TaxRate { Id = "4", Name = "GST", RateValue = 17m, RateValueSpecified = true }];
            if (query.Contains("from Term")) return [new Qbo.Term { Id = "3", Name = "Net 30", ItemsElementName = [Qbo.ItemsChoiceType.DueDays], AnyIntuitObjects = [30] }];
            if (query.Contains("from CompanyCurrency")) return [new Qbo.CompanyCurrency { Id = "1", Code = "USD", Name = "US Dollar", Active = true, ActiveSpecified = true },
                                                                new Qbo.CompanyCurrency { Id = "2", Code = "EUR", Name = "Euro", Active = false, ActiveSpecified = true }];
            return [];
        };
    }

    [Fact]
    public async Task Reference_data_reads_every_part()
    {
        ScriptReferenceData(multiCurrency: true);

        var result = await Sut.GetReferenceDataAsync(Ctx());

        result.IsSuccess.Should().BeTrue();
        var data = result.Value!;
        data.Accounts.Should().Equal(new RemoteAccount("79", "Sales", "Income", null, null, null, true));
        data.TaxCodes.Should().Equal(new RemoteTaxCode("3", "GST 17", null, true, 17m, true));
        data.Terms.Should().Equal(new RemoteTerm("3", "Net 30", 30, true));
        data.Currencies.Should().Equal(new RemoteCurrency("USD", "US Dollar"));
        data.Preferences.Should().Be(new RemotePreferences("PKR", true, true, true));
        data.CompanyInfo.Should().Be(new RemoteCompanyInfo("Sandbox Co", "Sandbox Co Ltd", "PK", null));

        var summary = JObject.Parse(result.ResponseJson!);
        summary["accounts"]!.Value<int>().Should().Be(1);
        summary["currencies"]!.Value<int>().Should().Be(2);
        summary["preferences"]!["CurrencyPrefs"]!["HomeCurrency"]!["value"]!.Value<string>().Should().Be("PKR");
        JObject.Parse(result.RequestJson!)["operations"]!.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Currencies_are_not_read_when_multicurrency_is_off()
    {
        ScriptReferenceData(multiCurrency: false);

        var result = await Sut.GetReferenceDataAsync(Ctx());

        result.IsSuccess.Should().BeTrue();
        result.Value!.Currencies.Should().BeEmpty();
        _client.Queries.Should().NotContain(q => q.Contains("CompanyCurrency"));
    }

    [Fact]
    public async Task A_tax_rate_failure_does_not_fail_the_call()
    {
        ScriptReferenceData(multiCurrency: false, q => q.Contains("from TaxRate") ? throw HttpEndpoint(500) : null);

        var result = await Sut.GetReferenceDataAsync(Ctx());

        result.IsSuccess.Should().BeTrue();
        result.Value!.TaxCodes.Single().RatePercent.Should().BeNull();
        JObject.Parse(result.ResponseJson!)["taxRates"]!.Value<string>().Should().StartWith("failed: Transient");
    }

    [Fact]
    public async Task A_currency_failure_does_not_fail_the_call()
    {
        ScriptReferenceData(multiCurrency: true, q => q.Contains("CompanyCurrency") ? throw Http400(ValidationFault("5030", "Feature not supported")) : null);

        var result = await Sut.GetReferenceDataAsync(Ctx());

        result.IsSuccess.Should().BeTrue();
        result.Value!.Currencies.Should().BeEmpty();
    }

    [Fact]
    public async Task A_required_part_failing_fails_the_call_and_names_the_part()
    {
        ScriptReferenceData(multiCurrency: false, q => q.Contains("from Account") ? throw Http429() : null);

        var result = await Sut.GetReferenceDataAsync(Ctx());

        result.Outcome.Should().Be(ProviderOutcomeKind.Throttled);
        result.Message.Should().StartWith("Accounts: ");
        result.IntuitTid.Should().Be("tid-429");
    }

    [Fact]
    public async Task Reference_data_pages_until_a_short_page()
    {
        var calls = 0;
        ScriptReferenceData(multiCurrency: false, q =>
        {
            if (!q.Contains("from Account")) return null;
            calls++;
            var count = calls == 1 ? QboQueryBuilder.MaxResultsCap : 3;
            return Enumerable.Range(0, count).Select(i => (object)new Qbo.Account { Id = $"{calls}-{i}", Name = "A" }).ToList();
        });

        var result = await Sut.GetReferenceDataAsync(Ctx());

        result.Value!.Accounts.Should().HaveCount(QboQueryBuilder.MaxResultsCap + 3);
        _client.Queries.Where(q => q.Contains("from Account")).Should().Equal(
            "select * from Account STARTPOSITION 1 MAXRESULTS 1000",
            "select * from Account STARTPOSITION 1001 MAXRESULTS 1000");
    }

    // ── Connection problems & cancellation ─────────────────────────────────────────────────

    [Theory]
    [InlineData(nameof(ConnectionStatus.Revoked))]
    [InlineData(nameof(ConnectionStatus.Expired))]
    [InlineData(nameof(ConnectionStatus.NotConnected))]
    public async Task An_unusable_connection_is_AuthRevoked_and_nothing_is_sent(string statusName)
    {
        var status = Enum.Parse<ConnectionStatus>(statusName);
        var sut = Provider(StubContextFactory.Throwing(new ConnectionUnavailableException(status, "cannot use it")), _client);

        var result = await sut.CreateAsync(Ctx(), FullCustomer());

        result.Outcome.Should().Be(ProviderOutcomeKind.AuthRevoked);
        result.ErrorCode.Should().Be($"ConnectionUnavailable:{status}");
        result.DurationMs.Should().Be(0);
        result.RequestJson.Should().Contain("King's Groceries", "the log still shows what would have been sent");
        _client.ClientsCreated.Should().Be(0);
        _client.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_revoked_grant_while_building_the_context_is_AuthRevoked()
    {
        var sut = Provider(StubContextFactory.Throwing(new AuthorizationRevokedException("invalid_grant")), _client);

        (await sut.GetReferenceDataAsync(Ctx())).Outcome.Should().Be(ProviderOutcomeKind.AuthRevoked);
    }

    [Fact]
    public async Task A_failed_token_refresh_is_Transient()
    {
        var sut = Provider(StubContextFactory.Throwing(new InvalidOperationException("refresh failed", new HttpRequestException("unreachable"))), _client);

        var result = await sut.FindAsync(Ctx(), SyncKind.Customer, new RemoteLookup(Name: "x"));

        result.Outcome.Should().Be(ProviderOutcomeKind.Transient);
        _client.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_cancelled_token_throws_before_anything_happens()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => Sut.CreateAsync(Ctx(), FullCustomer(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _contexts.Calls.Should().Be(0);
        _client.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancellation_between_reference_parts_stops_the_read()
    {
        using var cts = new CancellationTokenSource();
        ScriptReferenceData(multiCurrency: false, q =>
        {
            if (q.Contains("from Account")) cts.Cancel();
            return null;
        });

        var act = () => Sut.GetReferenceDataAsync(Ctx(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _client.Queries.Should().NotContain(q => q.Contains("from TaxCode"));
    }

    [Fact]
    public async Task A_timeout_from_the_sdk_is_not_mistaken_for_caller_cancellation()
    {
        _client.OnAdd = _ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout");

        var result = await Sut.CreateAsync(Ctx(), FullCustomer(), CancellationToken.None);

        result.Outcome.Should().Be(ProviderOutcomeKind.Transient);
    }

    // ── Deep links ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Production", SyncKind.Customer, "58", "https://app.qbo.intuit.com/app/customerdetail?nameId=58")]
    [InlineData("Production", SyncKind.Vendor, "31", "https://app.qbo.intuit.com/app/vendordetail?nameId=31")]
    [InlineData("Production", SyncKind.SalesInvoice, "145", "https://app.qbo.intuit.com/app/invoice?txnId=145")]
    [InlineData("Production", SyncKind.Bill, "200", "https://app.qbo.intuit.com/app/bill?txnId=200")]
    [InlineData("Sandbox", SyncKind.Customer, "58", "https://app.sandbox.qbo.intuit.com/app/customerdetail?nameId=58")]
    [InlineData("Sandbox", SyncKind.Vendor, "31", "https://app.sandbox.qbo.intuit.com/app/vendordetail?nameId=31")]
    [InlineData("Sandbox", SyncKind.SalesInvoice, "145", "https://app.sandbox.qbo.intuit.com/app/invoice?txnId=145")]
    [InlineData("Sandbox", SyncKind.Bill, "200", "https://app.sandbox.qbo.intuit.com/app/bill?txnId=200")]
    public void Deep_links(string environment, SyncKind kind, string id, string expected)
    {
        Sut.BuildDeepLink(Enum.Parse<IntegrationEnvironment>(environment), kind, id).Should().Be(expected);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Sandbox")]
    public void Items_have_no_deep_link(string environment)
    {
        Sut.BuildDeepLink(Enum.Parse<IntegrationEnvironment>(environment), SyncKind.Item, "11").Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Blank_ids_have_no_deep_link(string id)
    {
        Sut.BuildDeepLink(IntegrationEnvironment.Production, SyncKind.Customer, id).Should().BeNull();
    }

    [Fact]
    public void Deep_link_ids_are_url_encoded()
    {
        Sut.BuildDeepLink(IntegrationEnvironment.Production, SyncKind.Customer, "5&x=1 #")
            .Should().Be("https://app.qbo.intuit.com/app/customerdetail?nameId=5%26x%3D1%20%23");
    }
}

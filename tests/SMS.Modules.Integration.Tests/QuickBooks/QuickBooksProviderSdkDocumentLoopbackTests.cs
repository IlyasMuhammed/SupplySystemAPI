using FluentAssertions;
using Newtonsoft.Json.Linq;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Providers.QuickBooks;
using SMS.Modules.Integration.Tests.QuickBooks.Support;
using SMS.Shared.Integration.QuickBooks;
using static SMS.Modules.Integration.Tests.QuickBooks.Support.QboTestKit;

namespace SMS.Modules.Integration.Tests.QuickBooks;

/// <summary>
/// A44 (D-19..D-24, D-51..D-53, D-56..D-59): the six new QuickBooks objects through Intuit's real SDK against
/// <see cref="QboStubServer"/> on 127.0.0.1 — resource names, operation query strings (void vs delete), the bodies
/// as serialized by the SDK, and how failures come back. Nothing leaves the machine.
/// </summary>
public sealed class QuickBooksProviderSdkDocumentLoopbackTests : IDisposable
{
    private const string Time = "\"time\":\"2026-10-16T10:00:00.000-07:00\"";
    private readonly QboStubServer _server = new();
    private readonly QuickBooksAccountingProvider _provider;

    public QuickBooksProviderSdkDocumentLoopbackTests()
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

    private static JObject Body(StubRequest request) => JObject.Parse(request.Body);

    // ── Create: one resource per kind, the SDK body as mapped ───────────────────────────────

    [Fact]
    public async Task Payment_create_posts_linked_invoices_to_the_payment_resource()
    {
        _server.Reply(200, Entity("Payment", """{"Id":"900","SyncToken":"0","PaymentRefNum":"RCPT-0031","TotalAmt":1000,"UnappliedAmt":150}"""));

        var result = await _provider.CreateAsync(Ctx(), QboPaymentMapperTests.FullPayment());

        result.IsSuccess.Should().BeTrue(result.Message);
        result.Value.Should().Be(new RemoteRecord("900", "0", null, "RCPT-0031", 1000m, null, true));
        var request = _server.Requests.Single();
        request.Method.Should().Be("POST");
        request.PathAndQuery.Should().StartWith(CompanyPath("payment?")).And.Contain("minorversion=75").And.NotContain("operation");
        var body = Body(request);
        body["TotalAmt"]!.Value<decimal>().Should().Be(1000m);
        body["Line"]![0]!["LinkedTxn"]![0]!["TxnType"]!.Value<string>().Should().Be("Invoice");
        body["Line"]![0]!["LinkedTxn"]![0]!["TxnId"]!.Value<string>().Should().Be("145");
        body["DepositToAccountRef"]!["value"]!.Value<string>().Should().Be("35");
        JToken.DeepEquals(JObject.Parse(result.RequestJson!), body).Should().BeTrue("the logged request is exactly the body sent");
    }

    [Theory]
    [InlineData(false, "CheckPayment", "BankAccountRef")]
    [InlineData(true, "CreditCardPayment", "CCAccountRef")]
    public async Task Bill_payment_create_sends_the_pay_type_choice(bool card, string choice, string accountRef)
    {
        var payType = card ? RemoteBillPayType.CreditCard : RemoteBillPayType.Check;
        _server.Reply(200, Entity("BillPayment", $$"""{"Id":"77","SyncToken":"0","DocNumber":"SP-0009","TotalAmt":87.5,"PayType":"{{payType}}"}"""));

        var result = await _provider.CreateAsync(Ctx(), QboPaymentMapperTests.FullBillPayment(payType));

        result.Value.Should().Be(new RemoteRecord("77", "0", null, "SP-0009", 87.5m, null, true));
        var request = _server.Requests.Single();
        request.PathAndQuery.Should().StartWith(CompanyPath("billpayment?"));
        var body = Body(request);
        body["PayType"]!.Value<string>().Should().Be(payType.ToString());
        body[choice]![accountRef]!["value"]!.Value<string>().Should().Be("36");
        body["Line"]![0]!["LinkedTxn"]![0]!["TxnType"]!.Value<string>().Should().Be("Bill");
    }

    [Fact]
    public async Task Bill_payment_response_with_the_choice_reads_back()
    {
        _server.Reply(200, Entity("BillPayment", """
            {"Id":"77","SyncToken":"1","DocNumber":"SP-0009","TotalAmt":87.5,"PayType":"Check",
             "CheckPayment":{"BankAccountRef":{"value":"36","name":"Checking"},"PrintStatus":"NeedToPrint"},
             "Line":[{"Amount":87.5,"LinkedTxn":[{"TxnId":"200","TxnType":"Bill"}]}],"VendorRef":{"value":"31"}}
            """));

        var result = await _provider.GetByIdAsync(Ctx(), SyncKind.BillPayment, "77");

        result.Value.Should().Be(new RemoteRecord("77", "1", null, "SP-0009", 87.5m, null, true));
        _server.Requests.Single().PathAndQuery.Should().StartWith(CompanyPath("billpayment/77"));
    }

    [Theory]
    [InlineData("SalesReceipt", "salesreceipt")]
    [InlineData("CreditMemo", "creditmemo")]
    [InlineData("RefundReceipt", "refundreceipt")]
    public async Task Sales_document_create_posts_to_its_resource_and_reads_totals(string type, string resource)
    {
        _server.Reply(200, Entity(type, """{"Id":"501","SyncToken":"0","DocNumber":"SCN-0007","TotalAmt":70.55,"TxnTaxDetail":{"TotalTax":10.55}}"""));
        RemoteEntity source = type switch
        {
            "SalesReceipt" => QboSalesDocumentMapperTests.Receipt(),
            "CreditMemo"   => QboSalesDocumentMapperTests.Memo(),
            _              => QboSalesDocumentMapperTests.Refund()
        };

        var result = await _provider.CreateAsync(Ctx(), source);

        result.Value.Should().Be(new RemoteRecord("501", "0", null, "SCN-0007", 70.55m, 10.55m, true));
        var request = _server.Requests.Single();
        request.PathAndQuery.Should().StartWith(CompanyPath(resource + "?"));
        var body = Body(request);
        body["GlobalTaxCalculation"]!.Value<string>().Should().Be("TaxExcluded");
        body["ClassRef"]!["value"]!.Value<string>().Should().Be("300");
        body["DepartmentRef"]!["value"]!.Value<string>().Should().Be("2");
        ((JArray)body["Line"]!).Should().HaveCount(3);
        body["Line"]![0]!["SalesItemLineDetail"]!["TaxCodeRef"]!["value"]!.Value<string>().Should().Be("5");
        body["Line"]![2]!["DetailType"]!.Value<string>().Should().Be("DiscountLineDetail");
        if (type == "CreditMemo") body.Should().NotContainKey("DepositToAccountRef");
        else body["DepositToAccountRef"]!["value"]!.Value<string>().Should().Be("35");
    }

    [Fact]
    public async Task Vendor_credit_create_posts_bill_style_lines()
    {
        _server.Reply(200, Entity("VendorCredit", """{"Id":"88","SyncToken":"0","DocNumber":"CN-0004","TotalAmt":54.5}"""));

        var result = await _provider.CreateAsync(Ctx(), QboVendorCreditMapperTests.FullCredit());

        result.Value.Should().Be(new RemoteRecord("88", "0", null, "CN-0004", 54.5m, null, true));
        var request = _server.Requests.Single();
        request.PathAndQuery.Should().StartWith(CompanyPath("vendorcredit?"));
        var lines = (JArray)Body(request)["Line"]!;
        lines[0]["DetailType"]!.Value<string>().Should().Be("AccountBasedExpenseLineDetail");
        lines[1]["DetailType"]!.Value<string>().Should().Be("ItemBasedExpenseLineDetail");
    }

    [Fact]
    public async Task A_foreign_payment_reaches_QuickBooks_with_its_rate()
    {
        _server.Reply(200, Entity("Payment", """{"Id":"901","SyncToken":"0","TotalAmt":100,"CurrencyRef":{"value":"USD"},"ExchangeRate":280.25}"""));
        var payment = QboPaymentMapperTests.FullPayment();
        payment.CurrencyCode = "USD";
        payment.ExchangeRate = 280.25m;

        await _provider.CreateAsync(Ctx(), payment);

        var body = Body(_server.Requests.Single());
        body["ExchangeRate"]!.Value<decimal>().Should().Be(280.25m);
        body["CurrencyRef"]!["value"]!.Value<string>().Should().Be("USD");
    }

    // ── Credit memo applied to its invoice (D-23 ON_ACCOUNT) ─────────────────────────────────

    [Fact]
    public async Task Applied_credit_memo_creates_the_memo_then_a_zero_payment_linking_both()
    {
        _server
            .Reply(200, Entity("CreditMemo", """{"Id":"702","SyncToken":"0","DocNumber":"SCN-0007","TotalAmt":70.55}"""))
            .Reply(200, Entity("Payment", """{"Id":"903","SyncToken":"0","TotalAmt":0}"""));
        var memo = QboSalesDocumentMapperTests.Memo();
        memo.ApplyToInvoiceId = "145";
        memo.ApplyAmount = 99m;

        var result = await _provider.CreateAsync(Ctx(), memo);

        result.IsSuccess.Should().BeTrue(result.Message);
        result.Value!.RemoteId.Should().Be("702");
        result.Value.Note.Should().BeNull();
        var requests = _server.Requests;
        requests.Should().HaveCount(2);
        requests[0].PathAndQuery.Should().StartWith(CompanyPath("creditmemo?"));
        requests[1].PathAndQuery.Should().StartWith(CompanyPath("payment?"));
        var application = Body(requests[1]);
        application["TotalAmt"]!.Value<decimal>().Should().Be(0m);
        application["CustomerRef"]!["value"]!.Value<string>().Should().Be("58");
        var lines = (JArray)application["Line"]!;
        lines.Select(l => l["LinkedTxn"]![0]!["TxnType"]!.Value<string>()).Should().Equal("Invoice", "CreditMemo");
        lines.Select(l => l["LinkedTxn"]![0]!["TxnId"]!.Value<string>()).Should().Equal("145", "702");
        lines.Should().OnlyContain(l => l["Amount"]!.Value<decimal>() == 70.55m, "QuickBooks' own total of the memo wins over SCM's");
    }

    [Fact]
    public async Task A_refused_application_keeps_the_credit_memo_and_notes_it()
    {
        _server
            .Reply(200, Entity("CreditMemo", """{"Id":"702","SyncToken":"0","DocNumber":"SCN-0007","TotalAmt":70.55}"""))
            .Reply(400, ValidationFault("6000", "A business validation error has occurred while processing your request",
                "Business Validation Error: The amount applied is more than the open balance of the invoice."));
        var memo = QboSalesDocumentMapperTests.Memo();
        memo.ApplyToInvoiceId = "145";

        var result = await _provider.CreateAsync(Ctx(), memo);

        result.IsSuccess.Should().BeTrue("the credit memo exists in QuickBooks; it must never be created twice");
        result.Value!.RemoteId.Should().Be("702");
        result.Value.Note.Should().Contain("6000").And.Contain("by hand");
    }

    [Fact]
    public async Task A_lost_application_response_is_noted_too()
    {
        _server
            .Reply(200, Entity("CreditMemo", """{"Id":"702","SyncToken":"0","TotalAmt":70.55}"""))
            .DropConnection();
        var memo = QboSalesDocumentMapperTests.Memo();
        memo.ApplyToInvoiceId = "145";

        var result = await _provider.CreateAsync(Ctx(), memo);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Note.Should().Contain("CommunicationException");
    }

    [Fact]
    public async Task A_credit_memo_update_never_applies_again()
    {
        _server.Reply(200, Entity("CreditMemo", """{"Id":"702","SyncToken":"3","DocNumber":"SCN-0007","TotalAmt":70.55}"""));
        var memo = QboSalesDocumentMapperTests.Memo();
        memo.ApplyToInvoiceId = "145";

        var result = await _provider.UpdateAsync(Ctx(), memo, "702", "2");

        result.Value!.SyncToken.Should().Be("3");
        _server.Requests.Should().ContainSingle().Which.PathAndQuery.Should().StartWith(CompanyPath("creditmemo?"));
    }

    // ── Update: sparse with Id + SyncToken ──────────────────────────────────────────────────

    [Theory]
    [InlineData("Payment", "payment")]
    [InlineData("BillPayment", "billpayment")]
    [InlineData("SalesReceipt", "salesreceipt")]
    [InlineData("RefundReceipt", "refundreceipt")]
    [InlineData("VendorCredit", "vendorcredit")]
    public async Task Update_is_a_sparse_post_with_id_and_token(string type, string resource)
    {
        _server.Reply(200, Entity(type, """{"Id":"600","SyncToken":"5","TotalAmt":1}"""));
        RemoteEntity source = type switch
        {
            "Payment"       => QboPaymentMapperTests.FullPayment(),
            "BillPayment"   => QboPaymentMapperTests.FullBillPayment(),
            "SalesReceipt"  => QboSalesDocumentMapperTests.Receipt(),
            "RefundReceipt" => QboSalesDocumentMapperTests.Refund(),
            _               => QboVendorCreditMapperTests.FullCredit()
        };

        var result = await _provider.UpdateAsync(Ctx(), source, "600", "4");

        result.IsSuccess.Should().BeTrue(result.Message);
        result.Value!.SyncToken.Should().Be("5");
        var request = _server.Requests.Single();
        request.Method.Should().Be("POST");
        request.PathAndQuery.Should().StartWith(CompanyPath(resource + "?")).And.NotContain("operation=void");
        var body = Body(request);
        body["sparse"]!.Value<bool>().Should().BeTrue();
        body["Id"]!.Value<string>().Should().Be("600");
        body["SyncToken"]!.Value<string>().Should().Be("4");
    }

    // ── Get by id ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SyncKind.Payment, "Payment", "payment")]
    [InlineData(SyncKind.SalesReceipt, "SalesReceipt", "salesreceipt")]
    [InlineData(SyncKind.CreditMemo, "CreditMemo", "creditmemo")]
    [InlineData(SyncKind.RefundReceipt, "RefundReceipt", "refundreceipt")]
    [InlineData(SyncKind.VendorCredit, "VendorCredit", "vendorcredit")]
    public async Task Get_by_id_is_a_GET_on_the_resource(SyncKind kind, string type, string resource)
    {
        _server.Reply(200, Entity(type, """{"Id":"42","SyncToken":"2","TotalAmt":5}"""));

        var result = await _provider.GetByIdAsync(Ctx(), kind, "42");

        result.Value!.RemoteId.Should().Be("42");
        result.Value.TotalAmount.Should().Be(5m);
        var request = _server.Requests.Single();
        request.Method.Should().Be("GET");
        request.PathAndQuery.Should().StartWith(CompanyPath(resource + "/42"));
    }

    [Fact]
    public async Task A_voided_payment_reads_as_inactive()
    {
        _server.Reply(200, Entity("Payment", """{"Id":"900","SyncToken":"3","TotalAmt":0,"PrivateNote":"Voided","status":"Voided"}"""));

        var result = await _provider.GetByIdAsync(Ctx(), SyncKind.Payment, "900");

        result.IsSuccess.Should().BeTrue(result.Message);
        result.Value!.TotalAmount.Should().Be(0m);
    }

    // ── Find (unknown-outcome recovery) ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(SyncKind.BillPayment, "BillPayment")]
    [InlineData(SyncKind.SalesReceipt, "SalesReceipt")]
    [InlineData(SyncKind.CreditMemo, "CreditMemo")]
    [InlineData(SyncKind.RefundReceipt, "RefundReceipt")]
    [InlineData(SyncKind.VendorCredit, "VendorCredit")]
    public async Task Find_by_doc_number_queries_the_entity(SyncKind kind, string entity)
    {
        _server.Reply(200, QueryResponse(entity, """{"Id":"42","SyncToken":"1","DocNumber":"O'Neil-1","TotalAmt":5}"""));

        var result = await _provider.FindAsync(Ctx(), kind, new RemoteLookup(DocNumber: "O'Neil-1"));

        result.Value.Should().Be(new RemoteRecord("42", "1", null, "O'Neil-1", 5m, null, true));
        var request = _server.Requests.Single();
        request.PathAndQuery.Should().StartWith(CompanyPath("query"));
        request.Body.Should().Be($@"select * from {entity} where DocNumber = 'O\'Neil-1'");
    }

    [Fact]
    public async Task Find_filters_customer_documents_by_customer_on_the_results()
    {
        _server.Reply(200, QueryResponse("SalesReceipt",
            """{"Id":"1","SyncToken":"0","DocNumber":"POS-1","CustomerRef":{"value":"99"}}""",
            """{"Id":"2","SyncToken":"0","DocNumber":"POS-1","CustomerRef":{"value":"58"}}"""));

        var result = await _provider.FindAsync(Ctx(), SyncKind.SalesReceipt, new RemoteLookup(DocNumber: "POS-1") { CustomerRemoteId = "58" });

        result.Value!.RemoteId.Should().Be("2");
    }

    [Fact]
    public async Task Find_filters_vendor_documents_by_vendor_on_the_results()
    {
        _server.Reply(200, QueryResponse("VendorCredit",
            """{"Id":"1","SyncToken":"0","DocNumber":"CN-1","VendorRef":{"value":"99"}}""",
            """{"Id":"2","SyncToken":"0","DocNumber":"CN-1","VendorRef":{"value":"31"}}"""));

        var result = await _provider.FindAsync(Ctx(), SyncKind.VendorCredit, new RemoteLookup(DocNumber: "CN-1", VendorRemoteId: "31"));

        result.Value!.RemoteId.Should().Be("2");
    }

    [Fact]
    public async Task Find_payment_queries_the_day_then_matches_reference_and_customer()
    {
        _server.Reply(200, QueryResponse("Payment",
            """{"Id":"1","SyncToken":"0","PaymentRefNum":"RCPT-0031","CustomerRef":{"value":"99"},"TotalAmt":5}""",
            """{"Id":"2","SyncToken":"0","PaymentRefNum":"RCPT-0030","CustomerRef":{"value":"58"},"TotalAmt":5}""",
            """{"Id":"3","SyncToken":"0","PaymentRefNum":" rcpt-0031 ","CustomerRef":{"value":"58"},"TotalAmt":5}"""));

        var result = await _provider.FindAsync(Ctx(), SyncKind.Payment,
            new RemoteLookup(DocNumber: "RCPT-0031") { CustomerRemoteId = "58", TxnDate = new DateTime(2026, 10, 15, 23, 0, 0, DateTimeKind.Local) });

        result.Value!.RemoteId.Should().Be("3", "the reference is compared trimmed and case-insensitively");
        _server.Requests.Single().Body.Should().Be("select * from Payment where TxnDate = '2026-10-15' MAXRESULTS 1000");
    }

    [Fact]
    public async Task Find_payment_with_no_match_is_null()
    {
        _server.Reply(200, QueryResponse("Payment", """{"Id":"1","SyncToken":"0","CustomerRef":{"value":"58"}}"""));

        var result = await _provider.FindAsync(Ctx(), SyncKind.Payment,
            new RemoteLookup(DocNumber: "RCPT-0031") { TxnDate = new DateTime(2026, 10, 15) });

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull("a payment without a reference never matches");
    }

    [Fact]
    public async Task Find_payment_without_a_date_is_a_caller_mistake()
    {
        var act = () => _provider.FindAsync(Ctx(), SyncKind.Payment, new RemoteLookup(DocNumber: "RCPT-0031"));

        await act.Should().ThrowAsync<ArgumentException>();
        _server.Requests.Should().BeEmpty();
    }

    // ── Void / delete / refuse (D-51, D-56) ──────────────────────────────────────────────────

    [Theory]
    [InlineData(SyncKind.Payment, "Payment", "payment")]
    [InlineData(SyncKind.SalesReceipt, "SalesReceipt", "salesreceipt")]
    public async Task Void_of_a_payment_or_sales_receipt_is_a_sparse_update_with_include_void(SyncKind kind, string type, string resource)
    {
        _server.Reply(200, Entity(type, """{"Id":"900","SyncToken":"3","TotalAmt":0,"PrivateNote":"Voided"}"""));

        var result = await _provider.VoidAsync(Ctx(), kind, "900", "2");

        result.IsSuccess.Should().BeTrue(result.Message);
        result.Value!.Active.Should().BeFalse();
        result.Value.SyncToken.Should().Be("3");
        var request = _server.Requests.Single();
        request.Method.Should().Be("POST");
        request.PathAndQuery.Should().StartWith(CompanyPath(resource + "?"))
            .And.Contain("operation=update").And.Contain("include=void");
        var body = Body(request);
        body["Id"]!.Value<string>().Should().Be("900");
        body["SyncToken"]!.Value<string>().Should().Be("2");
        body["sparse"]!.Value<bool>().Should().BeTrue("QuickBooks voids these through a sparse update (D-56)");
        body.Properties().Select(p => p.Name).Should().BeEquivalentTo("Id", "SyncToken", "sparse");
    }

    /// <summary>
    /// D-56: what SDK 14.7.1.6's own DataService.Void does per entity — pinned so an SDK upgrade that changes it shows up.
    /// </summary>
    [Theory]
    [InlineData("Invoice", "invoice?operation=void&")]
    [InlineData("Payment", "payment?operation=update&include=void&")]
    [InlineData("SalesReceipt", "salesreceipt?operation=update&include=void&")]
    public void Sdk_void_url_per_entity(string type, string pathAndQuery)
    {
        _server.Reply(200, Entity(type, """{"Id":"1","SyncToken":"1"}"""));

        new SdkQboClient(NewServiceContext(_server.BaseUrl)).Void(SdkEntity(type));

        _server.Requests.Single().PathAndQuery.Should().StartWith(CompanyPath(pathAndQuery));
    }

    [Theory]
    [InlineData("BillPayment")]
    [InlineData("RefundReceipt")]
    [InlineData("CreditMemo")]
    [InlineData("VendorCredit")]
    public void Sdk_void_refuses_other_entities_before_any_request(string type)
    {
        var act = () => new SdkQboClient(NewServiceContext(_server.BaseUrl)).Void(SdkEntity(type));

        act.Should().Throw<Intuit.Ipp.Exception.IdsException>().WithMessage("*SalesReceipt and Payment*");
        _server.Requests.Should().BeEmpty("which is why the provider deletes bill payments and refund receipts (D-51)");
    }

    private static Intuit.Ipp.Data.IEntity SdkEntity(string type)
    {
        var entity = (Intuit.Ipp.Data.IntuitEntity)Activator.CreateInstance(typeof(Intuit.Ipp.Data.Invoice).Assembly.GetType("Intuit.Ipp.Data." + type)!)!;
        entity.Id        = "1";
        entity.SyncToken = "0";
        return (Intuit.Ipp.Data.IEntity)entity;
    }

    [Theory]
    [InlineData(SyncKind.BillPayment, "BillPayment", "billpayment")]
    [InlineData(SyncKind.RefundReceipt, "RefundReceipt", "refundreceipt")]
    public async Task Bill_payment_and_refund_receipt_are_deleted(SyncKind kind, string type, string resource)
    {
        _server.Reply(200, Entity(type, """{"Id":"77","status":"Deleted","domain":"QBO"}"""));

        var result = await _provider.VoidAsync(Ctx(), kind, "77", "1");

        result.IsSuccess.Should().BeTrue(result.Message);
        result.Value!.RemoteId.Should().Be("77");
        result.Value.Active.Should().BeFalse();
        var request = _server.Requests.Single();
        request.Method.Should().Be("POST");
        request.PathAndQuery.Should().StartWith(CompanyPath(resource + "?")).And.Contain("operation=delete");
        var body = Body(request);
        body["Id"]!.Value<string>().Should().Be("77");
        body["SyncToken"]!.Value<string>().Should().Be("1");
    }

    [Theory]
    [InlineData(SyncKind.CreditMemo)]
    [InlineData(SyncKind.VendorCredit)]
    [InlineData(SyncKind.Bill)]
    public async Task Credits_and_bills_are_refused_without_a_call(SyncKind kind)
    {
        var result = await _provider.VoidAsync(Ctx(), kind, "702", "0");

        result.Outcome.Should().Be(ProviderOutcomeKind.Refused);
        result.ErrorCode.Should().Be(IAccountingProvider.VoidUnsupportedCode);
        _server.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Void_of_an_invoice_through_the_generic_entry_is_unchanged()
    {
        _server.Reply(200, Entity("Invoice", """{"Id":"145","SyncToken":"3","DocNumber":"INV-1001","TotalAmt":0}"""));

        var result = await _provider.VoidAsync(Ctx(), SyncKind.SalesInvoice, "145", "2");

        result.Value.Should().Be(new RemoteRecord("145", "3", null, "INV-1001", 0m, null, false));
        var request = _server.Requests.Single();
        request.PathAndQuery.Should().StartWith(CompanyPath("invoice?")).And.Contain("operation=void");
        Body(request).Properties().Select(p => p.Name).Should().BeEquivalentTo("Id", "SyncToken");
    }

    // ── Failures specific to the new objects ────────────────────────────────────────────────

    [Fact]
    public async Task A_stale_token_on_void_is_StaleObject()
    {
        _server.Reply(400, ValidationFault("5010", "Stale Object Error", "Stale Object Error : You and root were working on this at the same time."));

        (await _provider.VoidAsync(Ctx(), SyncKind.Payment, "900", "1")).Outcome.Should().Be(ProviderOutcomeKind.StaleObject);
    }

    [Fact]
    public async Task Voiding_a_record_already_gone_is_NotFound()
    {
        _server.Reply(400, ValidationFault("610", "Object Not Found", "Object Not Found : Something you're trying to use has been made inactive."));

        (await _provider.VoidAsync(Ctx(), SyncKind.BillPayment, "77", "1")).Outcome.Should().Be(ProviderOutcomeKind.NotFound);
    }

    [Fact]
    public async Task A_payment_over_the_invoice_balance_is_Refused_with_the_detail()
    {
        _server.Reply(400, ValidationFault("6000", "A business validation error has occurred while processing your request",
            "Business Validation Error: The amount received exceeds the open balance of the linked transaction."));

        var result = await _provider.CreateAsync(Ctx(), QboPaymentMapperTests.FullPayment());

        result.Outcome.Should().Be(ProviderOutcomeKind.Refused);
        result.ErrorCode.Should().Be("6000");
        result.Message.Should().Contain("open balance");
    }

    [Fact]
    public async Task A_linked_invoice_that_does_not_exist_on_create_is_Refused()
    {
        _server.Reply(400, ValidationFault("610", "Object Not Found", "Object Not Found : Something you're trying to use has been made inactive.", "LinkedTxn"));

        var result = await _provider.CreateAsync(Ctx(), QboPaymentMapperTests.FullPayment());

        result.Outcome.Should().Be(ProviderOutcomeKind.Refused, "on a create, 610 is about a referenced record");
        result.ErrorField.Should().Be("LinkedTxn");
    }

    [Fact]
    public async Task A_duplicate_bill_payment_number_is_Duplicate()
    {
        _server.Reply(400, ValidationFault("6140", "Duplicate Document Number Error",
            "Duplicate Document Number Error : You must specify a different number. This number has already been used."));

        (await _provider.CreateAsync(Ctx(), QboPaymentMapperTests.FullBillPayment())).Outcome.Should().Be(ProviderOutcomeKind.Duplicate);
    }

    [Fact]
    public async Task A_refund_without_its_account_is_Refused_with_the_field()
    {
        _server.Reply(400, ValidationFault("2020", "Required param missing, need to supply the required value for the API",
            "Required parameter DepositToAccountRef is missing in the request"));
        var refund = QboSalesDocumentMapperTests.Refund();
        refund.RefundFromAccountId = null;

        var result = await _provider.CreateAsync(Ctx(), refund);

        result.Outcome.Should().Be(ProviderOutcomeKind.Refused);
        result.ErrorField.Should().Be("DepositToAccountRef");
    }

    [Fact]
    public async Task A_dropped_connection_on_a_receipt_create_is_Transient()
    {
        _server.DropConnection();

        var result = await _provider.CreateAsync(Ctx(), QboSalesDocumentMapperTests.Receipt());

        result.Outcome.Should().Be(ProviderOutcomeKind.Transient, "the outcome is unknown: the executor looks it up by DocNumber");
    }

    // ── D-55a: item income-account fix ──────────────────────────────────────────────────────

    [Fact]
    public async Task Item_income_account_fix_is_a_sparse_update_carrying_only_the_account()
    {
        _server.Reply(200, Entity("Item", """{"Id":"11","SyncToken":"3","Name":"Widget","Active":true,"Type":"Inventory","IncomeAccountRef":{"value":"79"}}"""));

        var result = await _provider.UpdateAsync(Ctx(), new RemoteItemIncomeAccountFix { IncomeAccountId = "79" }, "11", "2");

        result.IsSuccess.Should().BeTrue(result.Message);
        result.Value.Should().Be(new RemoteRecord("11", "3", "Widget", null));
        var request = _server.Requests.Single();
        request.Method.Should().Be("POST");
        request.PathAndQuery.Should().StartWith(CompanyPath("item?")).And.NotContain("operation");
        var body = Body(request);
        body.Properties().Select(p => p.Name).Should().BeEquivalentTo(new[] { "Id", "SyncToken", "sparse", "IncomeAccountRef" },
            "no Name, Type or Active may travel: the accountant's item stays as it is");
        body["IncomeAccountRef"]!["value"]!.Value<string>().Should().Be("79");
        body["sparse"]!.Value<bool>().Should().BeTrue();
    }

    // ── Deep links ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SyncKind.Payment, "/app/recvpayment?txnId=9")]
    [InlineData(SyncKind.BillPayment, "/app/billpayment?txnId=9")]
    [InlineData(SyncKind.SalesReceipt, "/app/salesreceipt?txnId=9")]
    [InlineData(SyncKind.CreditMemo, "/app/creditmemo?txnId=9")]
    [InlineData(SyncKind.RefundReceipt, "/app/refundreceipt?txnId=9")]
    [InlineData(SyncKind.VendorCredit, "/app/vendorcredit?txnId=9")]
    public void Deep_links_for_the_new_kinds(SyncKind kind, string path)
    {
        _provider.BuildDeepLink(SMS.Modules.Integration.Domain.IntegrationEnvironment.Production, kind, "9")
            .Should().Be(QuickBooksAccountingProvider.ProductionAppBaseUrl + path);
    }
}

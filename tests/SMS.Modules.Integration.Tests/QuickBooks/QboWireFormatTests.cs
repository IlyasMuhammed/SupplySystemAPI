using FluentAssertions;
using Newtonsoft.Json.Linq;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Providers.QuickBooks;
using SMS.Modules.Integration.Providers.QuickBooks.Mapping;
using SMS.Modules.Integration.Tests.QuickBooks.Support;

namespace SMS.Modules.Integration.Tests.QuickBooks;

/// <summary>
/// What actually goes on the wire: mapped entities serialized with the SDK's own JsonObjectSerializer
/// (the one the SDK uses for a JSON request body). A field whose <c>…Specified</c> flag is not set is
/// not in the JSON at all — which is what keeps a sparse update from clearing it.
/// </summary>
public class QboWireFormatTests
{
    private static JObject Json(object sdkEntity) => JObject.Parse(QboJson.Entity(sdkEntity)!);

    [Fact]
    public void Customer_create_body()
    {
        var json = Json(QboCustomerMapper.ToSdk(QboTestKit.FullCustomer()));

        json["DisplayName"]!.Value<string>().Should().Be("King's Groceries");
        json["CompanyName"]!.Value<string>().Should().Be("King's Groceries (Pvt) Ltd");
        json["PrimaryEmailAddr"]!["Address"]!.Value<string>().Should().Be("accounts@kings.example");
        json["PrimaryPhone"]!["FreeFormNumber"]!.Value<string>().Should().Be("+92 42 111 222 333");
        json["Fax"]!["FreeFormNumber"]!.Value<string>().Should().Be("+92 42 111 222 334");
        json["WebAddr"]!["URI"]!.Value<string>().Should().Be("https://kings.example");
        json["BillAddr"]!["CountrySubDivisionCode"]!.Value<string>().Should().Be("PB");
        json["CurrencyRef"]!["value"]!.Value<string>().Should().Be("PKR");
        json["SalesTermRef"]!["value"]!.Value<string>().Should().Be("3");
        json["PrimaryTaxIdentifier"]!.Value<string>().Should().Be("3520212345671");
        json["Notes"]!.Value<string>().Should().Be("Deliver before noon");
        json["Active"]!.Value<bool>().Should().BeTrue();
        json.Should().NotContainKey("Id").And.NotContainKey("SyncToken").And.NotContainKey("sparse");
        json.Should().NotContainKey("Taxable").And.NotContainKey("Balance").And.NotContainKey("Job");
    }

    [Fact]
    public void Inactive_customer_serializes_Active_false()
    {
        var source = QboTestKit.FullCustomer();
        source.Active = false;

        Json(QboCustomerMapper.ToSdk(source))["Active"]!.Value<bool>().Should().BeFalse();
    }

    [Fact]
    public void Sparse_update_body_carries_id_token_and_sparse_and_only_fields_with_values()
    {
        var sdk = QboMap.AsSparseUpdate(QboCustomerMapper.ToSdk(new RemoteCustomer { DisplayName = "Renamed", Active = true }), "58", "3");

        var json = Json(sdk);

        json["Id"]!.Value<string>().Should().Be("58");
        json["SyncToken"]!.Value<string>().Should().Be("3");
        json["sparse"]!.Value<bool>().Should().BeTrue();
        json.Properties().Select(p => p.Name).Should().BeEquivalentTo("Id", "SyncToken", "sparse", "DisplayName", "Active");
    }

    [Theory]
    [InlineData(null, "3")]
    [InlineData("58", null)]
    [InlineData(" ", "3")]
    public void Sparse_update_requires_id_and_token(string? id, string? token)
    {
        var act = () => QboMap.AsSparseUpdate(new Intuit.Ipp.Data.Customer(), id!, token!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Vendor_body_uses_vendor_field_names()
    {
        var json = Json(QboVendorMapper.ToSdk(QboTestKit.FullVendor()));

        json["TaxIdentifier"]!.Value<string>().Should().Be("NTN-7788");
        json["AcctNum"]!.Value<string>().Should().Be("ACC-0042");
        json["TermRef"]!["value"]!.Value<string>().Should().Be("4");
        json.Should().NotContainKey("SalesTermRef").And.NotContainKey("PrimaryTaxIdentifier");
    }

    [Fact]
    public void Item_body()
    {
        var json = Json(QboItemMapper.ToSdk(QboTestKit.FullItem()));

        json["Type"]!.Value<string>().Should().Be("NonInventory");
        json["UnitPrice"]!.Value<decimal>().Should().Be(12.50m);
        json["PurchaseCost"]!.Value<decimal>().Should().Be(7.25m);
        json["IncomeAccountRef"]!["value"]!.Value<string>().Should().Be("79");
        json["ExpenseAccountRef"]!["value"]!.Value<string>().Should().Be("80");
        json.Should().NotContainKey("TrackQtyOnHand").And.NotContainKey("QtyOnHand");
    }

    [Fact]
    public void Item_without_prices_does_not_send_zero_prices()
    {
        var json = Json(QboItemMapper.ToSdk(new RemoteItem { Name = "No price" }));

        json.Should().NotContainKey("UnitPrice").And.NotContainKey("PurchaseCost");
    }

    [Fact]
    public void Invoice_body_places_amount_on_the_line_and_qty_unitprice_on_the_detail()
    {
        var json = Json(QboInvoiceMapper.ToSdk(QboTestKit.FullInvoice(discount: 5m)));

        json["TxnDate"]!.Value<string>().Should().Be("2026-09-30");
        json["DueDate"]!.Value<string>().Should().Be("2026-10-30");
        json["GlobalTaxCalculation"]!.Value<string>().Should().Be("TaxExcluded");
        json["CustomerRef"]!["value"]!.Value<string>().Should().Be("58");
        json["CustomerMemo"]!["value"]!.Value<string>().Should().Be("Thank you for your business");

        var lines = (JArray)json["Line"]!;
        lines.Should().HaveCount(3);

        var sales = lines[0];
        sales["Amount"]!.Value<decimal>().Should().Be(25m);
        sales["DetailType"]!.Value<string>().Should().Be("SalesItemLineDetail");
        sales["SalesItemLineDetail"]!["Qty"]!.Value<decimal>().Should().Be(2m);
        sales["SalesItemLineDetail"]!["UnitPrice"]!.Value<decimal>().Should().Be(12.50m);
        sales["SalesItemLineDetail"]!["ItemRef"]!["value"]!.Value<string>().Should().Be("11");
        sales["SalesItemLineDetail"]!["TaxCodeRef"]!["value"]!.Value<string>().Should().Be("5");
        ((JObject)sales).Should().NotContainKey("Qty").And.NotContainKey("UnitPrice");

        var discount = lines[2];
        discount["DetailType"]!.Value<string>().Should().Be("DiscountLineDetail");
        discount["Amount"]!.Value<decimal>().Should().Be(5m);
        discount["DiscountLineDetail"]!["PercentBased"]!.Value<bool>().Should().BeFalse();
        discount["DiscountLineDetail"]!["DiscountAccountRef"]!["value"]!.Value<string>().Should().Be("86");
    }

    [Fact]
    public void Local_dates_are_not_shifted_by_the_serializer()
    {
        var source = QboTestKit.FullInvoice();
        source.TxnDate = new DateTime(2026, 9, 30, 0, 30, 0, DateTimeKind.Local);

        Json(QboInvoiceMapper.ToSdk(source))["TxnDate"]!.Value<string>().Should().Be("2026-09-30");
    }

    [Fact]
    public void Bill_body_item_and_account_lines()
    {
        var json = Json(QboBillMapper.ToSdk(QboTestKit.FullBill()));

        json["VendorRef"]!["value"]!.Value<string>().Should().Be("31");
        json["TxnDate"]!.Value<string>().Should().Be("2026-09-28");
        json["GlobalTaxCalculation"]!.Value<string>().Should().Be("TaxExcluded");

        var lines = (JArray)json["Line"]!;
        lines[0]["DetailType"]!.Value<string>().Should().Be("ItemBasedExpenseLineDetail");
        lines[0]["ItemBasedExpenseLineDetail"]!["UnitPrice"]!.Value<decimal>().Should().Be(7.25m);
        lines[0]["ItemBasedExpenseLineDetail"]!["Qty"]!.Value<decimal>().Should().Be(10m);
        lines[1]["DetailType"]!.Value<string>().Should().Be("AccountBasedExpenseLineDetail");
        lines[1]["AccountBasedExpenseLineDetail"]!["AccountRef"]!["value"]!.Value<string>().Should().Be("81");
        lines[1]["Amount"]!.Value<decimal>().Should().Be(15m);
    }

    [Fact]
    public void Foreign_invoice_and_bill_bodies_carry_ExchangeRate_next_to_CurrencyRef()
    {
        var invoice = QboTestKit.FullInvoice();
        invoice.CurrencyCode = "USD";
        invoice.ExchangeRate = 278.5m;
        var bill = QboTestKit.FullBill();
        bill.CurrencyCode = "EUR";
        bill.ExchangeRate = 301.12345678m;

        var invoiceJson = Json(QboInvoiceMapper.ToSdk(invoice));
        var billJson    = Json(QboBillMapper.ToSdk(bill));

        invoiceJson["CurrencyRef"]!["value"]!.Value<string>().Should().Be("USD");
        invoiceJson["ExchangeRate"]!.Value<decimal>().Should().Be(278.5m);
        billJson["CurrencyRef"]!["value"]!.Value<string>().Should().Be("EUR");
        billJson["ExchangeRate"]!.Value<decimal>().Should().Be(301.12345678m, "all eight places reach the wire");
    }

    [Fact]
    public void Home_currency_bodies_have_no_ExchangeRate_at_all()
    {
        Json(QboInvoiceMapper.ToSdk(QboTestKit.FullInvoice())).Should().NotContainKey("ExchangeRate");
        Json(QboBillMapper.ToSdk(QboTestKit.FullBill())).Should().NotContainKey("ExchangeRate");
    }

    [Fact]
    public void A_sparse_update_of_a_foreign_invoice_keeps_its_rate()
    {
        var invoice = QboTestKit.FullInvoice();
        invoice.CurrencyCode = "USD";
        invoice.ExchangeRate = 280m;

        var json = Json(QboMap.AsSparseUpdate(QboInvoiceMapper.ToSdk(invoice), "145", "3"));

        json["sparse"]!.Value<bool>().Should().BeTrue();
        json["ExchangeRate"]!.Value<decimal>().Should().Be(280m);
    }

    [Fact]
    public void Void_body_is_just_id_and_token()
    {
        var json = Json(new Intuit.Ipp.Data.Invoice { Id = "145", SyncToken = "2" });

        json.Properties().Select(p => p.Name).Should().BeEquivalentTo("Id", "SyncToken");
    }

    [Fact]
    public void List_log_is_capped_and_counted()
    {
        var rows = Enumerable.Range(1, 30).Select(i => QboTestKit.SavedCustomer(i.ToString())).ToList();

        var json = JObject.Parse(QboJson.List(rows));

        json["count"]!.Value<int>().Should().Be(30);
        ((JArray)json["items"]!).Should().HaveCount(QboJson.MaxLoggedListItems);
        json["truncated"]!.Value<bool>().Should().BeTrue();
        json["items"]![0]!["DisplayName"]!.Value<string>().Should().Be("King's Groceries");
    }
}

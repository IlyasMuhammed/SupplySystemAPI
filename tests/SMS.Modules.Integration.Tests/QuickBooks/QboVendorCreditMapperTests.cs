using FluentAssertions;
using Intuit.Ipp.Data;
using Newtonsoft.Json.Linq;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Providers.QuickBooks;
using SMS.Modules.Integration.Providers.QuickBooks.Mapping;
using SMS.Modules.Integration.Tests.QuickBooks.Support;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Tests.QuickBooks;

/// <summary>A44 D-24: supplier credit / debit notes as QuickBooks vendor credits — lines exactly as on a bill.</summary>
public class QboVendorCreditMapperTests
{
    internal static RemoteVendorCredit FullCredit() => new()
    {
        VendorId     = "31",
        DocNumber    = "CN-0004",
        TxnDate      = new DateTime(2026, 10, 12),
        CurrencyCode = "PKR",
        Lines =
        {
            new RemoteBillLine { AccountId = "81", Description = "Price difference", Amount = 40m, TaxCodeId = "8", ClassId = "300" },
            new RemoteBillLine { ItemId = "11", Description = "Returned widgets", Quantity = 2m, UnitPrice = 7.25m, Amount = 14.5m, TaxCodeId = "7" }
        },
        PrivateNote  = "CN CN-0004 · CREDIT_NOTE",
        DepartmentId = "2"
    };

    private static JObject Json(object sdk) => JObject.Parse(QboJson.Entity(sdk)!);

    [Fact]
    public void Header_fields_and_flags()
    {
        var credit = QboVendorCreditMapper.ToSdk(FullCredit());

        credit.VendorRef!.Value.Should().Be("31");
        credit.DocNumber.Should().Be("CN-0004");
        credit.TxnDate.Should().Be(new DateTime(2026, 10, 12));
        credit.TxnDateSpecified.Should().BeTrue();
        credit.CurrencyRef!.Value.Should().Be("PKR");
        credit.GlobalTaxCalculation.Should().Be(GlobalTaxCalculationEnum.TaxExcluded);
        credit.GlobalTaxCalculationSpecified.Should().BeTrue();
        credit.PrivateNote.Should().Be("CN CN-0004 · CREDIT_NOTE");
        credit.DepartmentRef!.Value.Should().Be("2");
        credit.TotalAmtSpecified.Should().BeFalse();
        credit.ExchangeRateSpecified.Should().BeFalse();
    }

    [Fact]
    public void Account_line_with_tax_code_and_class()
    {
        var line = QboVendorCreditMapper.ToSdk(FullCredit()).Line[0];

        line.Amount.Should().Be(40m);
        line.AmountSpecified.Should().BeTrue();
        line.Description.Should().Be("Price difference");
        line.DetailType.Should().Be(LineDetailTypeEnum.AccountBasedExpenseLineDetail);
        var detail = line.AnyIntuitObject.Should().BeOfType<AccountBasedExpenseLineDetail>().Subject;
        detail.AccountRef!.Value.Should().Be("81");
        detail.TaxCodeRef!.Value.Should().Be("8");
        detail.ClassRef!.Value.Should().Be("300");
    }

    [Fact]
    public void Item_line_with_qty_and_unit_price_and_no_class()
    {
        var line = QboVendorCreditMapper.ToSdk(FullCredit()).Line[1];

        line.DetailType.Should().Be(LineDetailTypeEnum.ItemBasedExpenseLineDetail);
        var detail = line.AnyIntuitObject.Should().BeOfType<ItemBasedExpenseLineDetail>().Subject;
        detail.ItemRef!.Value.Should().Be("11");
        detail.Qty.Should().Be(2m);
        detail.QtySpecified.Should().BeTrue();
        detail.AnyIntuitObject.Should().Be(7.25m);
        detail.ItemElementName.Should().Be(ItemChoiceType.UnitPrice);
        detail.TaxCodeRef!.Value.Should().Be("7");
        detail.ClassRef.Should().BeNull();
    }

    [Fact]
    public void Lines_match_the_bill_mapper_exactly()
    {
        var source = FullCredit();
        var bill = QboBillMapper.ToSdk(new RemoteBill { VendorId = "31", DocNumber = "X", TxnDate = source.TxnDate, Lines = source.Lines });

        Json(QboVendorCreditMapper.ToSdk(source))["Line"]!.ToString().Should().Be(Json(bill)["Line"]!.ToString());
    }

    [Fact]
    public void Wire_body()
    {
        var json = Json(QboVendorCreditMapper.ToSdk(FullCredit()));

        json["VendorRef"]!["value"]!.Value<string>().Should().Be("31");
        json["TxnDate"]!.Value<string>().Should().Be("2026-10-12");
        json["GlobalTaxCalculation"]!.Value<string>().Should().Be("TaxExcluded");
        json["DepartmentRef"]!["value"]!.Value<string>().Should().Be("2");
        var lines = (JArray)json["Line"]!;
        lines[0]["AccountBasedExpenseLineDetail"]!["AccountRef"]!["value"]!.Value<string>().Should().Be("81");
        lines[0]["AccountBasedExpenseLineDetail"]!["ClassRef"]!["value"]!.Value<string>().Should().Be("300");
        lines[1]["ItemBasedExpenseLineDetail"]!["UnitPrice"]!.Value<decimal>().Should().Be(7.25m);
        json.Should().NotContainKey("ExchangeRate").And.NotContainKey("TotalAmt").And.NotContainKey("APAccountRef");
    }

    [Fact]
    public void A_foreign_credit_carries_its_rate()
    {
        var source = FullCredit();
        source.CurrencyCode = "USD";
        source.ExchangeRate = 278.5m;

        var credit = QboVendorCreditMapper.ToSdk(source);

        credit.ExchangeRate.Should().Be(278.5m);
        credit.ExchangeRateSpecified.Should().BeTrue();
        Json(credit)["CurrencyRef"]!["value"]!.Value<string>().Should().Be("USD");
    }

    [Fact]
    public void Optional_fields_absent_are_not_on_the_wire()
    {
        var json = Json(QboVendorCreditMapper.ToSdk(new RemoteVendorCredit
        {
            VendorId = "31", DocNumber = "", TxnDate = new DateTime(2026, 10, 12),
            Lines = { new RemoteBillLine { Amount = 5m } }
        }));

        json.Properties().Select(p => p.Name).Should().BeEquivalentTo("VendorRef", "TxnDate", "GlobalTaxCalculation", "Line");
        var detail = (JObject)json["Line"]![0]!["AccountBasedExpenseLineDetail"]!;
        detail.Should().NotContainKey("AccountRef").And.NotContainKey("TaxCodeRef").And.NotContainKey("ClassRef");
    }

    [Fact]
    public void Record_reads_totals_and_tax()
    {
        var record = QboEntityMapper.ToRecord(SyncKind.VendorCredit, new VendorCredit
        {
            Id = "88", SyncToken = "0", DocNumber = "CN-0004", TotalAmt = 63.6m, TotalAmtSpecified = true,
            TxnTaxDetail = new TxnTaxDetail { TotalTax = 9.1m, TotalTaxSpecified = true }
        });

        record.Should().Be(new RemoteRecord("88", "0", null, "CN-0004", 63.6m, 9.1m, true));
    }

    [Fact]
    public void Existing_bill_mapping_is_unchanged()
    {
        // The vendor credit reuses QboBillMapper.ToLine; the bill body must stay what it was before A44.
        var json = Json(QboBillMapper.ToSdk(QboTestKit.FullBill()));

        json.Should().NotContainKey("DepartmentRef");
        ((JObject)json["Line"]![1]!["AccountBasedExpenseLineDetail"]!).Properties().Select(p => p.Name)
            .Should().BeEquivalentTo("AccountRef", "TaxCodeRef");
    }
}

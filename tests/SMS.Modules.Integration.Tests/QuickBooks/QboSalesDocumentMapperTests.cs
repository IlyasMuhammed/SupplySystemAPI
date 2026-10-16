using FluentAssertions;
using Intuit.Ipp.Data;
using Newtonsoft.Json.Linq;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Providers.QuickBooks;
using SMS.Modules.Integration.Providers.QuickBooks.Mapping;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Tests.QuickBooks;

/// <summary>
/// A44 D-21 / D-23: sales receipts, credit memos and refund receipts. They share the invoice's lines (gross sales line,
/// one fixed discount line, TaxExcluded), so these tests pin the shared part once per type and each type's own refs.
/// </summary>
public class QboSalesDocumentMapperTests
{
    internal static T Fill<T>(T doc, decimal discount = 5m) where T : RemoteSalesDocument
    {
        doc.CustomerId        = "58";
        doc.DocNumber         = "SCN-0007";
        doc.TxnDate           = new DateTime(2026, 10, 15);
        doc.CurrencyCode      = "PKR";
        doc.Lines.Add(new RemoteSalesLine { ItemId = "11", Description = "Blue widgets", Quantity = 2m, UnitPrice = 12.50m, Amount = 25m, TaxCodeId = "5" });
        doc.Lines.Add(new RemoteSalesLine { ItemId = "12", Description = "Installation", Quantity = 1m, UnitPrice = 40m, Amount = 40m, TaxCodeId = "6" });
        doc.DiscountAmount    = discount;
        doc.DiscountAccountId = "86";
        doc.PrivateNote       = "POS POS-1 · till 2";
        doc.ClassId           = "300";
        doc.DepartmentId      = "2";
        return doc;
    }

    internal static RemoteSalesReceipt Receipt(decimal discount = 5m)
    {
        var receipt = Fill(new RemoteSalesReceipt(), discount);
        receipt.DepositToAccountId = "35";
        receipt.PaymentMethodId    = "1";
        return receipt;
    }

    internal static RemoteCreditMemo Memo(decimal discount = 5m) => Fill(new RemoteCreditMemo(), discount);

    internal static RemoteRefundReceipt Refund(decimal discount = 5m)
    {
        var refund = Fill(new RemoteRefundReceipt(), discount);
        refund.RefundFromAccountId = "35";
        refund.PaymentMethodId     = "1";
        return refund;
    }

    public static TheoryData<string> Types => new() { "SalesReceipt", "CreditMemo", "RefundReceipt" };

    private static SalesTransaction Map(string type, decimal discount = 5m, Action<RemoteSalesDocument>? change = null)
    {
        RemoteSalesDocument source = type switch
        {
            "SalesReceipt"  => Receipt(discount),
            "CreditMemo"    => Memo(discount),
            "RefundReceipt" => Refund(discount),
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
        change?.Invoke(source);
        return (SalesTransaction)QboEntityMapper.ToSdk(source);
    }

    private static JObject Json(object sdk) => JObject.Parse(QboJson.Entity(sdk)!);

    // ── What all three share ────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Types))]
    public void Header_fields_and_flags(string type)
    {
        var doc = Map(type);

        doc.GetType().Name.Should().Be(type);
        doc.CustomerRef!.Value.Should().Be("58");
        doc.DocNumber.Should().Be("SCN-0007");
        doc.TxnDate.Should().Be(new DateTime(2026, 10, 15));
        doc.TxnDateSpecified.Should().BeTrue();
        doc.CurrencyRef!.Value.Should().Be("PKR");
        doc.GlobalTaxCalculation.Should().Be(GlobalTaxCalculationEnum.TaxExcluded);
        doc.GlobalTaxCalculationSpecified.Should().BeTrue();
        doc.PrivateNote.Should().Be("POS POS-1 · till 2");
        doc.ClassRef!.Value.Should().Be("300");
        doc.DepartmentRef!.Value.Should().Be("2");
        doc.TotalAmtSpecified.Should().BeFalse("totals are QuickBooks' to compute");
        doc.ExchangeRateSpecified.Should().BeFalse("a home-currency document never carries a rate");
        doc.CustomerMemo.Should().BeNull();
        doc.SalesTermRef.Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void Lines_are_the_invoice_lines_with_tax_codes_and_the_discount_line_last(string type)
    {
        var doc = Map(type, discount: 7.5m);

        doc.Line.Should().HaveCount(3);
        var first = doc.Line[0];
        first.Amount.Should().Be(25m);
        first.AmountSpecified.Should().BeTrue();
        first.Description.Should().Be("Blue widgets");
        first.DetailType.Should().Be(LineDetailTypeEnum.SalesItemLineDetail);
        var detail = first.AnyIntuitObject.Should().BeOfType<SalesItemLineDetail>().Subject;
        detail.ItemRef!.Value.Should().Be("11");
        detail.Qty.Should().Be(2m);
        detail.QtySpecified.Should().BeTrue();
        detail.AnyIntuitObject.Should().Be(12.50m);
        detail.ItemElementName.Should().Be(ItemChoiceType.UnitPrice);
        detail.TaxCodeRef!.Value.Should().Be("5");
        ((SalesItemLineDetail)doc.Line[1].AnyIntuitObject).TaxCodeRef!.Value.Should().Be("6");

        var discount = doc.Line[2];
        discount.Amount.Should().Be(7.5m);
        discount.DetailType.Should().Be(LineDetailTypeEnum.DiscountLineDetail);
        var discountDetail = discount.AnyIntuitObject.Should().BeOfType<DiscountLineDetail>().Subject;
        discountDetail.PercentBased.Should().BeFalse();
        discountDetail.PercentBasedSpecified.Should().BeTrue();
        discountDetail.DiscountAccountRef!.Value.Should().Be("86");
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void Lines_match_the_invoice_mapper_exactly(string type)
    {
        var doc = Map(type);
        var invoice = QboInvoiceMapper.ToSdk(new RemoteInvoice
        {
            CustomerId = "58", DocNumber = "SCN-0007", TxnDate = new DateTime(2026, 10, 15),
            Lines = Memo().Lines, DiscountAmount = 5m, DiscountAccountId = "86"
        });

        Json(doc)["Line"]!.ToString().Should().Be(Json(invoice)["Line"]!.ToString(),
            "sales receipts, credit memos and refund receipts reuse the invoice's line mapping");
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void No_discount_line_when_there_is_no_discount(string type)
    {
        Map(type, discount: 0m).Line.Should().HaveCount(2)
            .And.OnlyContain(l => l.DetailType == LineDetailTypeEnum.SalesItemLineDetail);
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void A_foreign_document_carries_its_rate_with_the_specified_flag(string type)
    {
        var doc = Map(type, change: d => { d.CurrencyCode = "USD"; d.ExchangeRate = 278.5m; });

        doc.CurrencyRef!.Value.Should().Be("USD");
        doc.ExchangeRate.Should().Be(278.5m);
        doc.ExchangeRateSpecified.Should().BeTrue();
        var json = Json(doc);
        json["ExchangeRate"]!.Value<decimal>().Should().Be(278.5m);
        json["CurrencyRef"]!["value"]!.Value<string>().Should().Be("USD");
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void Class_and_department_are_left_out_when_not_configured(string type)
    {
        var doc = Map(type, change: d => { d.ClassId = null; d.DepartmentId = " "; });

        doc.ClassRef.Should().BeNull();
        doc.DepartmentRef.Should().BeNull();
        Json(doc).Should().NotContainKey("ClassRef").And.NotContainKey("DepartmentRef");
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void A_line_class_is_sent_on_the_line_only_when_given(string type)
    {
        var doc = Map(type, change: d => d.Lines[0].ClassId = "301");

        ((SalesItemLineDetail)doc.Line[0].AnyIntuitObject).ClassRef!.Value.Should().Be("301");
        ((SalesItemLineDetail)doc.Line[1].AnyIntuitObject).ClassRef.Should().BeNull();
        var lines = (JArray)Json(doc)["Line"]!;
        lines[0]["SalesItemLineDetail"]!["ClassRef"]!["value"]!.Value<string>().Should().Be("301");
        ((JObject)lines[1]["SalesItemLineDetail"]!).Should().NotContainKey("ClassRef", "D-58: absent = unchanged from before A44");
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void Optional_fields_absent_are_not_on_the_wire(string type)
    {
        var doc = Map(type, discount: 0m, change: d =>
        {
            d.DocNumber = ""; d.CurrencyCode = null; d.PrivateNote = null; d.ClassId = null; d.DepartmentId = null;
            switch (d)
            {
                case RemoteSalesReceipt r:  r.DepositToAccountId = null; r.PaymentMethodId = null; break;
                case RemoteRefundReceipt r: r.RefundFromAccountId = null; r.PaymentMethodId = null; break;
            }
        });

        var json = Json(doc);
        json.Properties().Select(p => p.Name).Should().BeEquivalentTo("CustomerRef", "TxnDate", "GlobalTaxCalculation", "Line");
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void Dates_are_calendar_dates(string type)
    {
        var doc = Map(type, change: d => d.TxnDate = new DateTime(2026, 10, 15, 0, 30, 0, DateTimeKind.Local));

        Json(doc)["TxnDate"]!.Value<string>().Should().Be("2026-10-15");
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void Wire_body_of_the_shared_part(string type)
    {
        var json = Json(Map(type));

        json["CustomerRef"]!["value"]!.Value<string>().Should().Be("58");
        json["GlobalTaxCalculation"]!.Value<string>().Should().Be("TaxExcluded");
        json["ClassRef"]!["value"]!.Value<string>().Should().Be("300");
        json["DepartmentRef"]!["value"]!.Value<string>().Should().Be("2");
        var lines = (JArray)json["Line"]!;
        lines[0]["SalesItemLineDetail"]!["UnitPrice"]!.Value<decimal>().Should().Be(12.50m);
        lines[0]["SalesItemLineDetail"]!["TaxCodeRef"]!["value"]!.Value<string>().Should().Be("5");
        lines[2]["DiscountLineDetail"]!["PercentBased"]!.Value<bool>().Should().BeFalse();
        json.Should().NotContainKey("Id").And.NotContainKey("sparse").And.NotContainKey("TotalAmt");
    }

    // ── Each type's own references ───────────────────────────────────────────────────────────

    [Fact]
    public void Sales_receipt_deposits_to_its_account_with_its_payment_method()
    {
        var receipt = QboSalesDocumentMapper.ToSdk(Receipt());

        receipt.DepositToAccountRef!.Value.Should().Be("35");
        receipt.PaymentMethodRef!.Value.Should().Be("1");
        var json = Json(receipt);
        json["DepositToAccountRef"]!["value"]!.Value<string>().Should().Be("35");
        json["PaymentMethodRef"]!["value"]!.Value<string>().Should().Be("1");
    }

    [Fact]
    public void Sales_receipt_without_a_deposit_account_leaves_it_to_QuickBooks()
    {
        var source = Receipt();
        source.DepositToAccountId = null;
        source.PaymentMethodId = "";

        var json = Json(QboSalesDocumentMapper.ToSdk(source));

        json.Should().NotContainKey("DepositToAccountRef", "QuickBooks then uses Undeposited Funds")
            .And.NotContainKey("PaymentMethodRef");
    }

    [Fact]
    public void Refund_receipt_sends_its_refund_account_as_DepositToAccountRef()
    {
        var refund = QboSalesDocumentMapper.ToSdk(Refund());

        refund.DepositToAccountRef!.Value.Should().Be("35");
        refund.PaymentMethodRef!.Value.Should().Be("1");
        Json(refund)["DepositToAccountRef"]!["value"]!.Value<string>().Should().Be("35");
    }

    [Fact]
    public void Credit_memo_carries_no_cash_account_and_no_application()
    {
        var source = Memo();
        source.ApplyToInvoiceId = "145";
        source.ApplyAmount = 60m;

        var json = Json(QboSalesDocumentMapper.ToSdk(source));

        json.Should().NotContainKey("DepositToAccountRef").And.NotContainKey("PaymentMethodRef")
            .And.NotContainKey("LinkedTxn", "the application is a separate payment, made by the provider after the create");
    }

    [Fact]
    public void Sparse_update_keeps_lines_and_refs()
    {
        var json = Json(QboMap.AsSparseUpdate(QboSalesDocumentMapper.ToSdk(Receipt()), "501", "2"));

        json["sparse"]!.Value<bool>().Should().BeTrue();
        json["Id"]!.Value<string>().Should().Be("501");
        json["SyncToken"]!.Value<string>().Should().Be("2");
        ((JArray)json["Line"]!).Should().HaveCount(3);
        json["DepositToAccountRef"]!["value"]!.Value<string>().Should().Be("35");
    }

    // ── Reading back ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Records_read_id_token_number_totals_and_tax()
    {
        var receipt = new SalesReceipt
        {
            Id = "501", SyncToken = "1", DocNumber = "POS-1", TotalAmt = 70.55m, TotalAmtSpecified = true,
            TxnTaxDetail = new TxnTaxDetail { TotalTax = 10.55m, TotalTaxSpecified = true }
        };

        QboEntityMapper.ToRecord(SyncKind.SalesReceipt, receipt)
            .Should().Be(new RemoteRecord("501", "1", null, "POS-1", 70.55m, 10.55m, true));
        QboEntityMapper.ToRecord(SyncKind.CreditMemo, new CreditMemo { Id = "7", SyncToken = "0", DocNumber = "SCN-1" })
            .Should().Be(new RemoteRecord("7", "0", null, "SCN-1", null, null, true));
        QboEntityMapper.ToRecord(SyncKind.RefundReceipt,
                new RefundReceipt { Id = "8", SyncToken = "4", status = EntityStatusEnum.Voided, statusSpecified = true })
            .Active.Should().BeFalse();
    }

    [Fact]
    public void A_record_without_an_id_is_an_error_and_the_wrong_type_is_refused()
    {
        var noId = () => QboSalesDocumentMapper.ToRecord(new SalesReceipt { SyncToken = "0" }, "sales receipt");
        noId.Should().Throw<InvalidOperationException>().WithMessage("*sales receipt without an Id*");

        var wrong = () => QboEntityMapper.ToRecord(SyncKind.CreditMemo, new SalesReceipt { Id = "1" });
        wrong.Should().Throw<InvalidOperationException>().WithMessage("*SalesReceipt where a CreditMemo was expected*");
    }
}

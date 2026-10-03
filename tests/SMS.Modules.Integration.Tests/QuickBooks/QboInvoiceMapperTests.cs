using FluentAssertions;
using Intuit.Ipp.Data;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Providers.QuickBooks.Mapping;
using SMS.Modules.Integration.Tests.QuickBooks.Support;

namespace SMS.Modules.Integration.Tests.QuickBooks;

public class QboInvoiceMapperTests
{
    [Fact]
    public void Header_fields_and_flags()
    {
        var inv = QboInvoiceMapper.ToSdk(QboTestKit.FullInvoice());

        inv.CustomerRef!.Value.Should().Be("58");
        inv.DocNumber.Should().Be("INV-1001");
        inv.TxnDate.Should().Be(new DateTime(2026, 9, 30));
        inv.TxnDateSpecified.Should().BeTrue();
        inv.DueDate.Should().Be(new DateTime(2026, 10, 30));
        inv.DueDateSpecified.Should().BeTrue();
        inv.CurrencyRef!.Value.Should().Be("PKR");
        inv.GlobalTaxCalculation.Should().Be(GlobalTaxCalculationEnum.TaxExcluded);
        inv.GlobalTaxCalculationSpecified.Should().BeTrue();
        inv.CustomerMemo!.Value.Should().Be("Thank you for your business");
        inv.PrivateNote.Should().Be("SO SO-17 · DLV DN-9");
        inv.AutoDocNumberSpecified.Should().BeFalse("we always supply our own number");
        inv.TotalAmtSpecified.Should().BeFalse("totals are QuickBooks' to compute");
    }

    [Fact]
    public void One_sales_line_per_source_line_with_amount_on_the_line_and_qty_price_on_the_detail()
    {
        var inv = QboInvoiceMapper.ToSdk(QboTestKit.FullInvoice(discount: 0m));

        inv.Line.Should().HaveCount(2);

        var line = inv.Line[0];
        line.Amount.Should().Be(25m);
        line.AmountSpecified.Should().BeTrue();
        line.Description.Should().Be("Blue widgets");
        line.DetailType.Should().Be(LineDetailTypeEnum.SalesItemLineDetail);
        line.DetailTypeSpecified.Should().BeTrue();

        var detail = line.AnyIntuitObject.Should().BeOfType<SalesItemLineDetail>().Subject;
        detail.ItemRef!.Value.Should().Be("11");
        detail.Qty.Should().Be(2m);
        detail.QtySpecified.Should().BeTrue();
        detail.AnyIntuitObject.Should().Be(12.50m, "UnitPrice lives in the detail's xs:choice");
        detail.ItemElementName.Should().Be(ItemChoiceType.UnitPrice);
        detail.TaxCodeRef!.Value.Should().Be("5");

        var second = (SalesItemLineDetail)inv.Line[1].AnyIntuitObject;
        second.ItemRef!.Value.Should().Be("12");
        second.AnyIntuitObject.Should().Be(40m);
        inv.Line[1].Amount.Should().Be(40m);
    }

    [Fact]
    public void Amount_is_sent_as_given_not_recomputed()
    {
        var source = QboTestKit.FullInvoice(0m);
        source.Lines[0].Amount = 24.99m;

        QboInvoiceMapper.ToSdk(source).Line[0].Amount.Should().Be(24.99m);
    }

    [Fact]
    public void Discount_line_is_added_last_when_the_discount_is_positive()
    {
        var inv = QboInvoiceMapper.ToSdk(QboTestKit.FullInvoice(discount: 7.5m));

        inv.Line.Should().HaveCount(3);
        var line = inv.Line[2];
        line.Amount.Should().Be(7.5m);
        line.AmountSpecified.Should().BeTrue();
        line.DetailType.Should().Be(LineDetailTypeEnum.DiscountLineDetail);
        line.DetailTypeSpecified.Should().BeTrue();
        var detail = line.AnyIntuitObject.Should().BeOfType<DiscountLineDetail>().Subject;
        detail.PercentBased.Should().BeFalse();
        detail.PercentBasedSpecified.Should().BeTrue("a fixed amount must say so, or QuickBooks may read it as a percent");
        detail.DiscountAccountRef!.Value.Should().Be("86");
        detail.DiscountPercentSpecified.Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void No_discount_line_when_the_discount_is_not_positive(decimal discount)
    {
        var inv = QboInvoiceMapper.ToSdk(QboTestKit.FullInvoice(discount));

        inv.Line.Should().HaveCount(2);
        inv.Line.Should().OnlyContain(l => l.DetailType == LineDetailTypeEnum.SalesItemLineDetail);
    }

    [Fact]
    public void Discount_without_an_account_leaves_the_account_unset()
    {
        var source = QboTestKit.FullInvoice(3m);
        source.DiscountAccountId = null;

        var detail = (DiscountLineDetail)QboInvoiceMapper.ToSdk(source).Line[2].AnyIntuitObject;
        detail.DiscountAccountRef.Should().BeNull();
    }

    [Fact]
    public void Optional_header_and_line_fields_left_unset_when_absent()
    {
        var inv = QboInvoiceMapper.ToSdk(new RemoteInvoice
        {
            CustomerId = "58",
            DocNumber = "",
            TxnDate = new DateTime(2026, 1, 2),
            Lines = { new RemoteSalesLine { Quantity = 1, UnitPrice = 5, Amount = 5 } }
        });

        inv.DocNumber.Should().BeNull();
        inv.DueDateSpecified.Should().BeFalse();
        inv.CurrencyRef.Should().BeNull();
        inv.CustomerMemo.Should().BeNull();
        inv.PrivateNote.Should().BeNull();

        var line = inv.Line.Single();
        line.Description.Should().BeNull();
        var detail = (SalesItemLineDetail)line.AnyIntuitObject;
        detail.ItemRef.Should().BeNull();
        detail.TaxCodeRef.Should().BeNull();
    }

    [Fact]
    public void Dates_are_calendar_dates_whatever_their_kind()
    {
        var local = new DateTime(2026, 9, 30, 23, 30, 0, DateTimeKind.Local);
        var utc   = new DateTime(2026, 9, 30, 1, 0, 0, DateTimeKind.Utc);

        var inv = QboInvoiceMapper.ToSdk(new RemoteInvoice { CustomerId = "1", DocNumber = "X", TxnDate = local, DueDate = utc });

        inv.TxnDate.Should().Be(new DateTime(2026, 9, 30));
        inv.TxnDate.Kind.Should().Be(DateTimeKind.Unspecified);
        inv.DueDate.Should().Be(new DateTime(2026, 9, 30));
        inv.DueDate.Kind.Should().Be(DateTimeKind.Unspecified);
    }

    [Fact]
    public void A_foreign_invoice_carries_its_exchange_rate_with_the_specified_flag()
    {
        var source = QboTestKit.FullInvoice();
        source.CurrencyCode = "USD";
        source.ExchangeRate = 278.5m;

        var inv = QboInvoiceMapper.ToSdk(source);

        inv.CurrencyRef!.Value.Should().Be("USD");
        inv.ExchangeRate.Should().Be(278.5m);
        inv.ExchangeRateSpecified.Should().BeTrue("the SDK drops a value whose flag is false");
    }

    [Fact]
    public void Without_a_rate_none_is_sent()
    {
        var inv = QboInvoiceMapper.ToSdk(QboTestKit.FullInvoice());

        inv.ExchangeRateSpecified.Should().BeFalse("a home-currency invoice never carries a rate");
    }

    [Fact]
    public void Record_reads_totals_tax_and_doc_number()
    {
        var invoice = new Invoice
        {
            Id = "145", SyncToken = "2", DocNumber = "INV-1001",
            TotalAmt = 82.25m, TotalAmtSpecified = true,
            TxnTaxDetail = new TxnTaxDetail { TotalTax = 12.25m, TotalTaxSpecified = true }
        };

        QboInvoiceMapper.ToRecord(invoice).Should().Be(new RemoteRecord("145", "2", null, "INV-1001", 82.25m, 12.25m, true));
    }

    [Fact]
    public void Record_leaves_totals_null_when_QuickBooks_did_not_send_them()
    {
        var record = QboInvoiceMapper.ToRecord(new Invoice { Id = "1", SyncToken = "0", TxnTaxDetail = new TxnTaxDetail() });

        record.TotalAmount.Should().BeNull();
        record.TotalTax.Should().BeNull();
    }

    [Fact]
    public void Voided_invoice_reads_as_inactive()
    {
        var record = QboInvoiceMapper.ToRecord(new Invoice { Id = "1", SyncToken = "3", status = EntityStatusEnum.Voided, statusSpecified = true });

        record.Active.Should().BeFalse();
    }
}

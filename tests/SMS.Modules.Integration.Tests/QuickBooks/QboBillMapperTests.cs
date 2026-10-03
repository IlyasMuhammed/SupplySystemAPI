using FluentAssertions;
using Intuit.Ipp.Data;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Providers.QuickBooks.Mapping;
using SMS.Modules.Integration.Tests.QuickBooks.Support;

namespace SMS.Modules.Integration.Tests.QuickBooks;

public class QboBillMapperTests
{
    [Fact]
    public void Header_fields_and_flags()
    {
        var bill = QboBillMapper.ToSdk(QboTestKit.FullBill());

        bill.VendorRef!.Value.Should().Be("31");
        bill.DocNumber.Should().Be("SUP-INV-77");
        bill.TxnDate.Should().Be(new DateTime(2026, 9, 28));
        bill.TxnDateSpecified.Should().BeTrue();
        bill.DueDate.Should().Be(new DateTime(2026, 10, 28));
        bill.DueDateSpecified.Should().BeTrue();
        bill.CurrencyRef!.Value.Should().Be("PKR");
        bill.PrivateNote.Should().Be("INV-77 · PO PO-3 · GRN GRN-5");
        bill.GlobalTaxCalculation.Should().Be(GlobalTaxCalculationEnum.TaxExcluded);
        bill.GlobalTaxCalculationSpecified.Should().BeTrue();
        bill.TotalAmtSpecified.Should().BeFalse();
    }

    [Fact]
    public void A_line_with_an_item_is_item_based()
    {
        var line = QboBillMapper.ToSdk(QboTestKit.FullBill()).Line[0];

        line.Amount.Should().Be(72.50m);
        line.AmountSpecified.Should().BeTrue();
        line.Description.Should().Be("Widgets");
        line.DetailType.Should().Be(LineDetailTypeEnum.ItemBasedExpenseLineDetail);
        line.DetailTypeSpecified.Should().BeTrue();
        var detail = line.AnyIntuitObject.Should().BeOfType<ItemBasedExpenseLineDetail>().Subject;
        detail.ItemRef!.Value.Should().Be("11");
        detail.Qty.Should().Be(10m);
        detail.QtySpecified.Should().BeTrue();
        detail.AnyIntuitObject.Should().Be(7.25m);
        detail.ItemElementName.Should().Be(ItemChoiceType.UnitPrice);
        detail.TaxCodeRef!.Value.Should().Be("7");
        detail.BillableStatusSpecified.Should().BeFalse();
    }

    [Fact]
    public void A_line_without_an_item_is_account_based()
    {
        var line = QboBillMapper.ToSdk(QboTestKit.FullBill()).Line[1];

        line.Amount.Should().Be(15m);
        line.AmountSpecified.Should().BeTrue();
        line.Description.Should().Be("Freight");
        line.DetailType.Should().Be(LineDetailTypeEnum.AccountBasedExpenseLineDetail);
        line.DetailTypeSpecified.Should().BeTrue();
        var detail = line.AnyIntuitObject.Should().BeOfType<AccountBasedExpenseLineDetail>().Subject;
        detail.AccountRef!.Value.Should().Be("81");
        detail.TaxCodeRef!.Value.Should().Be("8");
        detail.BillableStatusSpecified.Should().BeFalse();
        detail.TaxAmountSpecified.Should().BeFalse();
    }

    [Fact]
    public void Item_takes_precedence_over_account_when_both_are_given()
    {
        var bill = QboBillMapper.ToSdk(new RemoteBill
        {
            VendorId = "1", DocNumber = "B", TxnDate = DateTime.Today,
            Lines = { new RemoteBillLine { ItemId = "11", AccountId = "81", Amount = 1 } }
        });

        bill.Line[0].AnyIntuitObject.Should().BeOfType<ItemBasedExpenseLineDetail>();
    }

    [Fact]
    public void A_blank_item_id_means_account_based()
    {
        var bill = QboBillMapper.ToSdk(new RemoteBill
        {
            VendorId = "1", DocNumber = "B", TxnDate = DateTime.Today,
            Lines = { new RemoteBillLine { ItemId = " ", AccountId = "81", Amount = 1 } }
        });

        bill.Line[0].AnyIntuitObject.Should().BeOfType<AccountBasedExpenseLineDetail>();
    }

    [Fact]
    public void Item_line_without_quantity_or_price_leaves_them_unset()
    {
        var bill = QboBillMapper.ToSdk(new RemoteBill
        {
            VendorId = "1", DocNumber = "B", TxnDate = DateTime.Today,
            Lines = { new RemoteBillLine { ItemId = "11", Amount = 50 } }
        });

        var detail = (ItemBasedExpenseLineDetail)bill.Line[0].AnyIntuitObject;
        detail.QtySpecified.Should().BeFalse();
        detail.AnyIntuitObject.Should().BeNull();
        detail.TaxCodeRef.Should().BeNull();
        bill.Line[0].Amount.Should().Be(50m);
    }

    [Fact]
    public void Optional_header_fields_left_unset()
    {
        var bill = QboBillMapper.ToSdk(new RemoteBill { VendorId = "1", DocNumber = " ", TxnDate = DateTime.Today });

        bill.DocNumber.Should().BeNull();
        bill.DueDateSpecified.Should().BeFalse();
        bill.CurrencyRef.Should().BeNull();
        bill.PrivateNote.Should().BeNull();
        bill.Line.Should().BeEmpty();
    }

    [Fact]
    public void A_foreign_bill_carries_its_exchange_rate_and_a_home_one_does_not()
    {
        var foreign = QboTestKit.FullBill();
        foreign.CurrencyCode = "USD";
        foreign.ExchangeRate = 277.77777778m;

        var bill = QboBillMapper.ToSdk(foreign);
        bill.CurrencyRef!.Value.Should().Be("USD");
        bill.ExchangeRate.Should().Be(277.77777778m);
        bill.ExchangeRateSpecified.Should().BeTrue();

        QboBillMapper.ToSdk(QboTestKit.FullBill()).ExchangeRateSpecified.Should().BeFalse();
    }

    [Fact]
    public void Record_reads_totals()
    {
        var bill = new Bill
        {
            Id = "200", SyncToken = "1", DocNumber = "SUP-INV-77",
            TotalAmt = 101.5m, TotalAmtSpecified = true,
            TxnTaxDetail = new TxnTaxDetail { TotalTax = 14m, TotalTaxSpecified = true }
        };

        QboBillMapper.ToRecord(bill).Should().Be(new RemoteRecord("200", "1", null, "SUP-INV-77", 101.5m, 14m, true));
    }
}

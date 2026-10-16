using FluentAssertions;
using Intuit.Ipp.Data;
using Newtonsoft.Json.Linq;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Providers.QuickBooks;
using SMS.Modules.Integration.Providers.QuickBooks.Mapping;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Tests.QuickBooks;

/// <summary>A44 D-19 / D-20 / D-23: customer payments, the credit-memo application payment, bill payments.</summary>
public class QboPaymentMapperTests
{
    internal static RemotePayment FullPayment() => new()
    {
        CustomerId         = "58",
        ReferenceNumber    = "RCPT-0031",
        TxnDate            = new DateTime(2026, 10, 15),
        CurrencyCode       = "PKR",
        TotalAmount        = 1000m,
        Lines              = { new RemotePaymentLine { InvoiceId = "145", Amount = 600m }, new RemotePaymentLine { InvoiceId = "146", Amount = 250m } },
        DepositToAccountId = "35",
        PaymentMethodId    = "2",
        PrivateNote        = "Cheque 004512 · HBL"
    };

    internal static RemoteBillPayment FullBillPayment(RemoteBillPayType payType = RemoteBillPayType.Check) => new()
    {
        VendorId     = "31",
        DocNumber    = "SP-0009",
        TxnDate      = new DateTime(2026, 10, 14),
        CurrencyCode = "PKR",
        TotalAmount  = 87.5m,
        PayType      = payType,
        AccountId    = "36",
        Lines        = { new RemoteBillPaymentLine { BillId = "200", Amount = 87.5m } },
        PrivateNote  = "SP-0009 · BANK_TRANSFER",
        DepartmentId = "2"
    };

    private static JObject Json(object sdk) => JObject.Parse(QboJson.Entity(sdk)!);

    // ── Payment ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Payment_header_fields_and_flags()
    {
        var payment = QboPaymentMapper.ToSdk(FullPayment());

        payment.CustomerRef!.Value.Should().Be("58");
        payment.PaymentRefNum.Should().Be("RCPT-0031", "QuickBooks payments have no DocNumber; SCM's number travels as the reference");
        payment.TxnDate.Should().Be(new DateTime(2026, 10, 15));
        payment.TxnDate.Kind.Should().Be(DateTimeKind.Unspecified);
        payment.TxnDateSpecified.Should().BeTrue();
        payment.CurrencyRef!.Value.Should().Be("PKR");
        payment.TotalAmt.Should().Be(1000m);
        payment.TotalAmtSpecified.Should().BeTrue("the total is the whole receipt, not the sum of the lines");
        payment.DepositToAccountRef!.Value.Should().Be("35");
        payment.PaymentMethodRef!.Value.Should().Be("2");
        payment.PrivateNote.Should().Be("Cheque 004512 · HBL");
        payment.ExchangeRateSpecified.Should().BeFalse();
        payment.UnappliedAmtSpecified.Should().BeFalse("QuickBooks computes the unapplied amount");
    }

    [Fact]
    public void Payment_lines_link_each_invoice()
    {
        var lines = QboPaymentMapper.ToSdk(FullPayment()).Line;

        lines.Should().HaveCount(2);
        lines[0].Amount.Should().Be(600m);
        lines[0].AmountSpecified.Should().BeTrue();
        lines[0].LinkedTxn.Should().ContainSingle().Which.Should().BeEquivalentTo(new { TxnId = "145", TxnType = "Invoice" });
        lines[1].LinkedTxn.Single().TxnId.Should().Be("146");
        lines[1].Amount.Should().Be(250m);
        lines.Should().OnlyContain(l => !l.DetailTypeSpecified, "payment lines carry no detail type");
    }

    [Fact]
    public void Payment_wire_body()
    {
        var json = Json(QboPaymentMapper.ToSdk(FullPayment()));

        json["TotalAmt"]!.Value<decimal>().Should().Be(1000m);
        json["PaymentRefNum"]!.Value<string>().Should().Be("RCPT-0031");
        json["TxnDate"]!.Value<string>().Should().Be("2026-10-15");
        json["DepositToAccountRef"]!["value"]!.Value<string>().Should().Be("35");
        json["PaymentMethodRef"]!["value"]!.Value<string>().Should().Be("2");
        var line = (JObject)((JArray)json["Line"]!)[0];
        line["Amount"]!.Value<decimal>().Should().Be(600m);
        line["LinkedTxn"]![0]!["TxnId"]!.Value<string>().Should().Be("145");
        line["LinkedTxn"]![0]!["TxnType"]!.Value<string>().Should().Be("Invoice");
        line.Should().NotContainKey("DetailType");
        json.Should().NotContainKey("DocNumber").And.NotContainKey("ExchangeRate").And.NotContainKey("UnappliedAmt");
    }

    [Fact]
    public void An_unapplied_payment_has_no_lines_but_its_total()
    {
        var source = FullPayment();
        source.Lines.Clear();

        var json = Json(QboPaymentMapper.ToSdk(source));

        json["TotalAmt"]!.Value<decimal>().Should().Be(1000m);
        (json["Line"] is null || !json["Line"]!.HasValues).Should().BeTrue("the whole amount stays as credit on the customer");
    }

    [Fact]
    public void A_foreign_payment_carries_its_rate()
    {
        var source = FullPayment();
        source.CurrencyCode = "USD";
        source.ExchangeRate = 280.25m;

        var payment = QboPaymentMapper.ToSdk(source);

        payment.ExchangeRate.Should().Be(280.25m);
        payment.ExchangeRateSpecified.Should().BeTrue();
        Json(payment)["ExchangeRate"]!.Value<decimal>().Should().Be(280.25m);
        Json(payment)["CurrencyRef"]!["value"]!.Value<string>().Should().Be("USD");
    }

    [Fact]
    public void Payment_optional_refs_left_out_when_absent()
    {
        var json = Json(QboPaymentMapper.ToSdk(new RemotePayment
        {
            CustomerId = "58", ReferenceNumber = " ", TxnDate = new DateTime(2026, 10, 15), TotalAmount = 10m
        }));

        json.Properties().Select(p => p.Name).Should().BeEquivalentTo("CustomerRef", "TxnDate", "TotalAmt", "Line");
    }

    [Fact]
    public void Payment_record_reads_the_reference_as_doc_number()
    {
        var record = QboPaymentMapper.ToRecord(new Payment
        {
            Id = "900", SyncToken = "1", PaymentRefNum = "RCPT-0031", TotalAmt = 1000m, TotalAmtSpecified = true
        });

        record.Should().Be(new RemoteRecord("900", "1", null, "RCPT-0031", 1000m, null, true));
        QboPaymentMapper.ToRecord(new Payment { Id = "900", SyncToken = "2", status = EntityStatusEnum.Voided, statusSpecified = true })
            .Active.Should().BeFalse();
    }

    // ── Credit application (D-23) ─────────────────────────────────────────────────────────────

    [Fact]
    public void Credit_application_is_a_zero_payment_linking_invoice_and_credit_memo_for_the_same_amount()
    {
        var payment = QboPaymentMapper.CreditApplication("58", "145", "702", 60m, new DateTime(2026, 10, 15), "PKR", null, "SCN-0007");

        payment.CustomerRef!.Value.Should().Be("58");
        payment.TotalAmt.Should().Be(0m);
        payment.TotalAmtSpecified.Should().BeTrue("a zero total must be sent, or QuickBooks reads the lines as cash received");
        payment.PaymentRefNum.Should().Be("SCN-0007");
        payment.PrivateNote.Should().Be("Applies credit memo SCN-0007 (SCM).");
        payment.DepositToAccountRef.Should().BeNull();
        payment.ExchangeRateSpecified.Should().BeFalse();
        payment.Line.Should().HaveCount(2);
        payment.Line[0].Amount.Should().Be(60m);
        payment.Line[0].LinkedTxn.Single().Should().BeEquivalentTo(new { TxnId = "145", TxnType = "Invoice" });
        payment.Line[1].Amount.Should().Be(60m);
        payment.Line[1].LinkedTxn.Single().Should().BeEquivalentTo(new { TxnId = "702", TxnType = "CreditMemo" });

        var json = Json(payment);
        json["TotalAmt"]!.Value<decimal>().Should().Be(0m);
        json["Line"]![1]!["LinkedTxn"]![0]!["TxnType"]!.Value<string>().Should().Be("CreditMemo");
    }

    [Fact]
    public void Credit_application_in_a_foreign_currency_carries_the_rate_and_without_a_reference_no_note()
    {
        var payment = QboPaymentMapper.CreditApplication("58", "145", "702", 60m, new DateTime(2026, 10, 15), "USD", 279m, null);

        payment.CurrencyRef!.Value.Should().Be("USD");
        payment.ExchangeRate.Should().Be(279m);
        payment.ExchangeRateSpecified.Should().BeTrue();
        payment.PaymentRefNum.Should().BeNull();
        payment.PrivateNote.Should().BeNull();
    }

    // ── Bill payment ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Bill_payment_by_cheque_pays_from_the_bank_account()
    {
        var payment = QboPaymentMapper.ToSdk(FullBillPayment());

        payment.VendorRef!.Value.Should().Be("31");
        payment.DocNumber.Should().Be("SP-0009");
        payment.TxnDate.Should().Be(new DateTime(2026, 10, 14));
        payment.TxnDateSpecified.Should().BeTrue();
        payment.CurrencyRef!.Value.Should().Be("PKR");
        payment.TotalAmt.Should().Be(87.5m);
        payment.TotalAmtSpecified.Should().BeTrue();
        payment.PrivateNote.Should().Be("SP-0009 · BANK_TRANSFER");
        payment.DepartmentRef!.Value.Should().Be("2");
        payment.PayType.Should().Be(BillPaymentTypeEnum.Check);
        payment.PayTypeSpecified.Should().BeTrue();
        payment.AnyIntuitObject.Should().BeOfType<BillPaymentCheck>().Which.BankAccountRef!.Value.Should().Be("36");
        payment.Line.Single().LinkedTxn.Single().Should().BeEquivalentTo(new { TxnId = "200", TxnType = "Bill" });
        payment.Line.Single().Amount.Should().Be(87.5m);
    }

    [Fact]
    public void Bill_payment_by_card_pays_from_the_card_account()
    {
        var payment = QboPaymentMapper.ToSdk(FullBillPayment(RemoteBillPayType.CreditCard));

        payment.PayType.Should().Be(BillPaymentTypeEnum.CreditCard);
        payment.AnyIntuitObject.Should().BeOfType<BillPaymentCreditCard>().Which.CCAccountRef!.Value.Should().Be("36");
    }

    [Fact]
    public void Bill_payment_wire_body_names_the_choice_CheckPayment_or_CreditCardPayment()
    {
        var check = Json(QboPaymentMapper.ToSdk(FullBillPayment()));
        var card  = Json(QboPaymentMapper.ToSdk(FullBillPayment(RemoteBillPayType.CreditCard)));

        check["PayType"]!.Value<string>().Should().Be("Check");
        check["CheckPayment"]!["BankAccountRef"]!["value"]!.Value<string>().Should().Be("36");
        check.Should().NotContainKey("CreditCardPayment").And.NotContainKey("AnyIntuitObject");
        check["Line"]![0]!["LinkedTxn"]![0]!["TxnType"]!.Value<string>().Should().Be("Bill");
        check["DocNumber"]!.Value<string>().Should().Be("SP-0009");
        check["DepartmentRef"]!["value"]!.Value<string>().Should().Be("2");

        card["PayType"]!.Value<string>().Should().Be("CreditCard");
        card["CreditCardPayment"]!["CCAccountRef"]!["value"]!.Value<string>().Should().Be("36");
        card.Should().NotContainKey("CheckPayment");
    }

    [Fact]
    public void An_advance_bill_payment_has_no_lines()
    {
        var source = FullBillPayment();
        source.Lines.Clear();

        var json = Json(QboPaymentMapper.ToSdk(source));

        json["TotalAmt"]!.Value<decimal>().Should().Be(87.5m);
        (json["Line"] is null || !json["Line"]!.HasValues).Should().BeTrue();
    }

    [Fact]
    public void A_foreign_bill_payment_carries_its_rate_and_home_currency_none()
    {
        var source = FullBillPayment();
        source.CurrencyCode = "EUR";
        source.ExchangeRate = 301.12345678m;

        Json(QboPaymentMapper.ToSdk(source))["ExchangeRate"]!.Value<decimal>().Should().Be(301.12345678m);
        Json(QboPaymentMapper.ToSdk(FullBillPayment())).Should().NotContainKey("ExchangeRate");
    }

    [Fact]
    public void Bill_payment_without_an_account_or_department_leaves_them_out()
    {
        var source = FullBillPayment();
        source.AccountId = null;
        source.DepartmentId = null;
        source.DocNumber = "";

        var json = Json(QboPaymentMapper.ToSdk(source));

        json.Should().NotContainKey("DepartmentRef").And.NotContainKey("DocNumber");
        (json["CheckPayment"] is not JObject cp || !cp.ContainsKey("BankAccountRef")).Should().BeTrue();
    }

    [Fact]
    public void Bill_payment_record()
    {
        var record = QboEntityMapper.ToRecord(SyncKind.BillPayment, new BillPayment
        {
            Id = "77", SyncToken = "0", DocNumber = "SP-0009", TotalAmt = 87.5m, TotalAmtSpecified = true
        });

        record.Should().Be(new RemoteRecord("77", "0", null, "SP-0009", 87.5m, null, true));
    }

    [Fact]
    public void Entity_mapper_dispatches_both_payment_kinds()
    {
        QboEntityMapper.ToSdk(FullPayment()).Should().BeOfType<Payment>();
        QboEntityMapper.ToSdk(FullBillPayment()).Should().BeOfType<BillPayment>();
        QboEntityMapper.EntityName(SyncKind.Payment).Should().Be("Payment");
        QboEntityMapper.EntityName(SyncKind.BillPayment).Should().Be("BillPayment");
    }
}

using Intuit.Ipp.Data;
using SMS.Modules.Integration.Core.Providers;
using static SMS.Modules.Integration.Providers.QuickBooks.Mapping.QboMap;

namespace SMS.Modules.Integration.Providers.QuickBooks.Mapping;

/// <summary>
/// A44 D-19 / D-20 / D-23: RemotePayment ↔ SDK <see cref="Payment"/>, RemoteBillPayment ↔ SDK <see cref="BillPayment"/>,
/// and the zero-total Payment that applies a credit memo to an invoice.
/// <list type="bullet">
/// <item>Payment: <c>TotalAmt</c> is the whole receipt; one line per applied invoice with a <c>LinkedTxn</c>
/// (TxnType Invoice). What the lines do not apply stays on the customer as unapplied credit. QuickBooks payments have no
/// DocNumber — SCM's number travels as <c>PaymentRefNum</c>.</item>
/// <item>BillPayment: PayType Check (BankAccountRef on a <c>BillPaymentCheck</c>, JSON <c>CheckPayment</c>) or CreditCard
/// (CCAccountRef on a <c>BillPaymentCreditCard</c>, JSON <c>CreditCardPayment</c>); one line per bill (LinkedTxn TxnType
/// Bill). No lines = an advance (vendor credit balance in QuickBooks).</item>
/// </list>
/// </summary>
internal static class QboPaymentMapper
{
    public static Payment ToSdk(RemotePayment source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var payment = new Payment
        {
            CustomerRef         = new ReferenceType { Value = source.CustomerId },
            PaymentRefNum       = Value(source.ReferenceNumber),
            TxnDate             = Date(source.TxnDate),
            TxnDateSpecified    = true,
            CurrencyRef         = Ref(source.CurrencyCode),
            TotalAmt            = source.TotalAmount,
            TotalAmtSpecified   = true,
            DepositToAccountRef = Ref(source.DepositToAccountId),
            PaymentMethodRef    = Ref(source.PaymentMethodId),
            PrivateNote         = Value(source.PrivateNote),
            Line                = source.Lines.Select(l => Linked(l.InvoiceId, "Invoice", l.Amount)).ToArray()
        };

        if (source.ExchangeRate is { } rate)
        {
            payment.ExchangeRate          = rate;
            payment.ExchangeRateSpecified = true;
        }
        return payment;
    }

    /// <summary>
    /// D-23: QuickBooks applies a credit memo to an invoice only through a payment — a zero-total one with two lines of
    /// the same amount: the invoice (paid) and the credit memo (used).
    /// </summary>
    public static Payment CreditApplication(string customerId, string invoiceId, string creditMemoId, decimal amount,
                                            DateTime txnDate, string? currencyCode, decimal? exchangeRate, string? reference)
    {
        var payment = new Payment
        {
            CustomerRef       = new ReferenceType { Value = customerId },
            PaymentRefNum     = Value(reference),
            TxnDate           = Date(txnDate),
            TxnDateSpecified  = true,
            CurrencyRef       = Ref(currencyCode),
            TotalAmt          = 0m,
            TotalAmtSpecified = true,
            PrivateNote       = Value(reference is null ? null : $"Applies credit memo {reference} (SCM)."),
            Line              = [Linked(invoiceId, "Invoice", amount), Linked(creditMemoId, "CreditMemo", amount)]
        };
        if (exchangeRate is { } rate)
        {
            payment.ExchangeRate          = rate;
            payment.ExchangeRateSpecified = true;
        }
        return payment;
    }

    public static BillPayment ToSdk(RemoteBillPayment source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var billPayment = new BillPayment
        {
            VendorRef         = new ReferenceType { Value = source.VendorId },
            DocNumber         = Value(source.DocNumber),
            TxnDate           = Date(source.TxnDate),
            TxnDateSpecified  = true,
            CurrencyRef       = Ref(source.CurrencyCode),
            TotalAmt          = source.TotalAmount,
            TotalAmtSpecified = true,
            PrivateNote       = Value(source.PrivateNote),
            DepartmentRef     = Ref(source.DepartmentId),
            PayType           = source.PayType == RemoteBillPayType.CreditCard ? BillPaymentTypeEnum.CreditCard : BillPaymentTypeEnum.Check,
            PayTypeSpecified  = true,
            AnyIntuitObject   = source.PayType == RemoteBillPayType.CreditCard
                ? new BillPaymentCreditCard { CCAccountRef = Ref(source.AccountId) }
                : new BillPaymentCheck { BankAccountRef = Ref(source.AccountId) },
            Line              = source.Lines.Select(l => Linked(l.BillId, "Bill", l.Amount)).ToArray()
        };

        if (source.ExchangeRate is { } rate)
        {
            billPayment.ExchangeRate          = rate;
            billPayment.ExchangeRateSpecified = true;
        }
        return billPayment;
    }

    private static Line Linked(string txnId, string txnType, decimal amount) => new()
    {
        Amount          = amount,
        AmountSpecified = true,
        LinkedTxn       = [new LinkedTxn { TxnId = txnId, TxnType = txnType }]
    };

    public static RemoteRecord ToRecord(Payment payment) =>
        new(RequireId(payment, "payment"),
            payment.SyncToken ?? string.Empty,
            Name: null,
            payment.PaymentRefNum,
            Read(payment.TotalAmt, payment.TotalAmtSpecified),
            TotalTax: null,
            ReadTxnActive(payment));

    public static RemoteRecord ToRecord(BillPayment billPayment) =>
        new(RequireId(billPayment, "bill payment"),
            billPayment.SyncToken ?? string.Empty,
            Name: null,
            billPayment.DocNumber,
            Read(billPayment.TotalAmt, billPayment.TotalAmtSpecified),
            TotalTax: null,
            ReadTxnActive(billPayment));
}

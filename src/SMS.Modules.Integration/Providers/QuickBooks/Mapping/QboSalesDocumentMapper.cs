using Intuit.Ipp.Data;
using SMS.Modules.Integration.Core.Providers;
using static SMS.Modules.Integration.Providers.QuickBooks.Mapping.QboMap;

namespace SMS.Modules.Integration.Providers.QuickBooks.Mapping;

/// <summary>
/// A44 (D-21, D-23): RemoteSalesReceipt / RemoteCreditMemo / RemoteRefundReceipt ↔ SDK <see cref="SalesReceipt"/>,
/// <see cref="CreditMemo"/>, <see cref="RefundReceipt"/>. They are SDK <c>SalesTransaction</c>s like the invoice and
/// carry the same lines (<see cref="QboInvoiceMapper.SalesLine"/>, <see cref="QboInvoiceMapper.DiscountLine"/>): one
/// <c>SalesItemLineDetail</c> per line, one fixed-amount discount line (plan D-5), <c>GlobalTaxCalculation =
/// TaxExcluded</c>, ExchangeRate only for a foreign-currency document (plan S-10).
/// Class / Department (Location) / payment method / deposit account only when given (D-15). Class is sent at the
/// header only (D-58).
/// </summary>
internal static class QboSalesDocumentMapper
{
    public static SalesReceipt ToSdk(RemoteSalesReceipt source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var receipt = Fill(new SalesReceipt(), source);
        receipt.DepositToAccountRef = Ref(source.DepositToAccountId);
        receipt.PaymentMethodRef    = Ref(source.PaymentMethodId);
        return receipt;
    }

    public static CreditMemo ToSdk(RemoteCreditMemo source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Fill(new CreditMemo(), source);
    }

    /// <summary>QuickBooks calls the account a refund is paid from the refund receipt's DepositToAccountRef.</summary>
    public static RefundReceipt ToSdk(RemoteRefundReceipt source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var refund = Fill(new RefundReceipt(), source);
        refund.DepositToAccountRef = Ref(source.RefundFromAccountId);
        refund.PaymentMethodRef    = Ref(source.PaymentMethodId);
        return refund;
    }

    private static T Fill<T>(T target, RemoteSalesDocument source) where T : SalesTransaction
    {
        target.CustomerRef                   = new ReferenceType { Value = source.CustomerId };
        target.DocNumber                     = Value(source.DocNumber);
        target.TxnDate                       = Date(source.TxnDate);
        target.TxnDateSpecified              = true;
        target.CurrencyRef                   = Ref(source.CurrencyCode);
        target.GlobalTaxCalculation          = GlobalTaxCalculationEnum.TaxExcluded;
        target.GlobalTaxCalculationSpecified = true;
        target.PrivateNote                   = Value(source.PrivateNote);
        target.ClassRef                      = Ref(source.ClassId);
        target.DepartmentRef                 = Ref(source.DepartmentId);

        if (source.ExchangeRate is { } rate)
        {
            target.ExchangeRate          = rate;
            target.ExchangeRateSpecified = true;
        }

        var lines = new List<Line>(source.Lines.Count + 1);
        lines.AddRange(source.Lines.Select(QboInvoiceMapper.SalesLine));
        if (source.DiscountAmount > 0)
            lines.Add(QboInvoiceMapper.DiscountLine(source.DiscountAmount, source.DiscountAccountId));
        target.Line = lines.ToArray();
        return target;
    }

    public static RemoteRecord ToRecord(SalesTransaction document, string what) =>
        new(RequireId(document, what),
            document.SyncToken ?? string.Empty,
            Name: null,
            document.DocNumber,
            Read(document.TotalAmt, document.TotalAmtSpecified),
            document.TxnTaxDetail is { } tax ? Read(tax.TotalTax, tax.TotalTaxSpecified) : null,
            ReadTxnActive(document));
}

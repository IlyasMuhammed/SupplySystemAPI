using Intuit.Ipp.Data;
using SMS.Modules.Integration.Core.Providers;
using static SMS.Modules.Integration.Providers.QuickBooks.Mapping.QboMap;

namespace SMS.Modules.Integration.Providers.QuickBooks.Mapping;

/// <summary>
/// A44 D-24: RemoteVendorCredit ↔ SDK <see cref="VendorCredit"/>. Lines as on a bill (<see cref="QboBillMapper.ToLine"/>:
/// item-based when an item is given, else account-based on the D-16 expense default; class on the line); TaxExcluded;
/// ExchangeRate only for a foreign-currency credit.
/// </summary>
internal static class QboVendorCreditMapper
{
    public static VendorCredit ToSdk(RemoteVendorCredit source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var credit = new VendorCredit
        {
            VendorRef                     = new ReferenceType { Value = source.VendorId },
            DocNumber                     = Value(source.DocNumber),
            TxnDate                       = Date(source.TxnDate),
            TxnDateSpecified              = true,
            CurrencyRef                   = Ref(source.CurrencyCode),
            GlobalTaxCalculation          = GlobalTaxCalculationEnum.TaxExcluded,
            GlobalTaxCalculationSpecified = true,
            PrivateNote                   = Value(source.PrivateNote),
            DepartmentRef                 = Ref(source.DepartmentId),
            Line                          = source.Lines.Select(QboBillMapper.ToLine).ToArray()
        };

        if (source.ExchangeRate is { } rate)
        {
            credit.ExchangeRate          = rate;
            credit.ExchangeRateSpecified = true;
        }
        return credit;
    }

    public static RemoteRecord ToRecord(VendorCredit credit) =>
        new(RequireId(credit, "vendor credit"),
            credit.SyncToken ?? string.Empty,
            Name: null,
            credit.DocNumber,
            Read(credit.TotalAmt, credit.TotalAmtSpecified),
            credit.TxnTaxDetail is { } tax ? Read(tax.TotalTax, tax.TotalTaxSpecified) : null,
            ReadTxnActive(credit));
}

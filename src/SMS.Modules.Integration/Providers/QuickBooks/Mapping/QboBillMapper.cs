using Intuit.Ipp.Data;
using SMS.Modules.Integration.Core.Providers;
using static SMS.Modules.Integration.Providers.QuickBooks.Mapping.QboMap;

namespace SMS.Modules.Integration.Providers.QuickBooks.Mapping;

/// <summary>
/// RemoteBill ↔ SDK <see cref="Bill"/>. A line with an ItemId becomes an <c>ItemBasedExpenseLineDetail</c>
/// (ItemRef, Qty, UnitPrice via the xs:choice, TaxCodeRef); any other line an
/// <c>AccountBasedExpenseLineDetail</c> (AccountRef, TaxCodeRef). Amount and Description live on the Line.
/// <c>GlobalTaxCalculation = TaxExcluded</c> as on invoices — bill line amounts are net and the caller's
/// tax total travels separately. <c>ExchangeRate</c> is set (with its <c>…Specified</c> flag) only for a
/// foreign-currency bill (plan S-10).
/// </summary>
internal static class QboBillMapper
{
    public static Bill ToSdk(RemoteBill source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var bill = new Bill
        {
            VendorRef                     = new ReferenceType { Value = source.VendorId },
            DocNumber                     = Value(source.DocNumber),
            TxnDate                       = Date(source.TxnDate),
            TxnDateSpecified              = true,
            CurrencyRef                   = Ref(source.CurrencyCode),
            GlobalTaxCalculation          = GlobalTaxCalculationEnum.TaxExcluded,
            GlobalTaxCalculationSpecified = true,
            PrivateNote                   = Value(source.PrivateNote),
            Line                          = source.Lines.Select(ToLine).ToArray()
        };

        if (source.DueDate is { } due)
        {
            bill.DueDate          = Date(due);
            bill.DueDateSpecified = true;
        }

        // Plan S-10: home units per one unit of the bill's currency; foreign-currency bills only.
        if (source.ExchangeRate is { } rate)
        {
            bill.ExchangeRate          = rate;
            bill.ExchangeRateSpecified = true;
        }
        return bill;
    }

    private static Line ToLine(RemoteBillLine source)
    {
        var line = new Line
        {
            Description     = Value(source.Description),
            Amount          = source.Amount,
            AmountSpecified = true
        };

        if (Has(source.ItemId))
        {
            var detail = new ItemBasedExpenseLineDetail
            {
                ItemRef    = Ref(source.ItemId),
                TaxCodeRef = Ref(source.TaxCodeId)
            };
            if (source.Quantity is { } qty)
            {
                detail.Qty          = qty;
                detail.QtySpecified = true;
            }
            if (source.UnitPrice is { } unitPrice)
            {
                detail.AnyIntuitObject = unitPrice;
                detail.ItemElementName = ItemChoiceType.UnitPrice;
            }
            line.DetailType          = LineDetailTypeEnum.ItemBasedExpenseLineDetail;
            line.DetailTypeSpecified = true;
            line.AnyIntuitObject     = detail;
        }
        else
        {
            line.DetailType          = LineDetailTypeEnum.AccountBasedExpenseLineDetail;
            line.DetailTypeSpecified = true;
            line.AnyIntuitObject     = new AccountBasedExpenseLineDetail
            {
                AccountRef = Ref(source.AccountId),
                TaxCodeRef = Ref(source.TaxCodeId)
            };
        }
        return line;
    }

    public static RemoteRecord ToRecord(Bill bill) =>
        new(RequireId(bill, "bill"),
            bill.SyncToken ?? string.Empty,
            Name: null,
            bill.DocNumber,
            Read(bill.TotalAmt, bill.TotalAmtSpecified),
            bill.TxnTaxDetail is { } tax ? Read(tax.TotalTax, tax.TotalTaxSpecified) : null,
            ReadTxnActive(bill));
}

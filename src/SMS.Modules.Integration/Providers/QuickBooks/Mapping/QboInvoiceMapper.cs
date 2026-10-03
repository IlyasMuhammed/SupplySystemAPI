using Intuit.Ipp.Data;
using SMS.Modules.Integration.Core.Providers;
using static SMS.Modules.Integration.Providers.QuickBooks.Mapping.QboMap;

namespace SMS.Modules.Integration.Providers.QuickBooks.Mapping;

/// <summary>
/// RemoteInvoice ↔ SDK <see cref="Invoice"/>.
/// <list type="bullet">
/// <item>One <c>SalesItemLineDetail</c> line per sales line: <c>Line.Amount</c> is the given Amount;
/// Qty sits on the detail with <c>QtySpecified</c>; UnitPrice sits on the detail's <c>AnyIntuitObject</c>
/// with <c>ItemElementName = UnitPrice</c> (the SDK's xs:choice — there is no UnitPrice property).</item>
/// <item>One fixed-amount <c>DiscountLineDetail</c> line, only when DiscountAmount &gt; 0 (plan D-5).</item>
/// <item><c>GlobalTaxCalculation = TaxExcluded</c>: line amounts are net, QuickBooks adds tax.</item>
/// <item><c>ExchangeRate</c> (on the SDK's Transaction base, with <c>ExchangeRateSpecified</c>) only when given (plan S-10).</item>
/// </list>
/// </summary>
internal static class QboInvoiceMapper
{
    public static Invoice ToSdk(RemoteInvoice source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var invoice = new Invoice
        {
            CustomerRef                  = new ReferenceType { Value = source.CustomerId },
            DocNumber                    = Value(source.DocNumber),
            TxnDate                      = Date(source.TxnDate),
            TxnDateSpecified             = true,
            CurrencyRef                  = Ref(source.CurrencyCode),
            GlobalTaxCalculation         = GlobalTaxCalculationEnum.TaxExcluded,
            GlobalTaxCalculationSpecified = true,
            CustomerMemo                 = Has(source.CustomerMemo) ? new MemoRef { Value = source.CustomerMemo } : null,
            PrivateNote                  = Value(source.PrivateNote)
        };

        if (source.DueDate is { } due)
        {
            invoice.DueDate          = Date(due);
            invoice.DueDateSpecified = true;
        }

        // Plan S-10: a foreign-currency invoice carries SMS's rate (home units per one foreign unit), so
        // QuickBooks does not substitute its own. Never sent for a home-currency invoice.
        if (source.ExchangeRate is { } rate)
        {
            invoice.ExchangeRate          = rate;
            invoice.ExchangeRateSpecified = true;
        }

        var lines = new List<Line>(source.Lines.Count + 1);
        lines.AddRange(source.Lines.Select(SalesLine));
        if (source.DiscountAmount > 0)
            lines.Add(DiscountLine(source.DiscountAmount, source.DiscountAccountId));
        invoice.Line = lines.ToArray();

        return invoice;
    }

    private static Line SalesLine(RemoteSalesLine source) => new()
    {
        Description         = Value(source.Description),
        Amount              = source.Amount,
        AmountSpecified     = true,
        DetailType          = LineDetailTypeEnum.SalesItemLineDetail,
        DetailTypeSpecified = true,
        AnyIntuitObject     = new SalesItemLineDetail
        {
            ItemRef         = Ref(source.ItemId),
            Qty             = source.Quantity,
            QtySpecified    = true,
            AnyIntuitObject = source.UnitPrice,
            ItemElementName = ItemChoiceType.UnitPrice,
            TaxCodeRef      = Ref(source.TaxCodeId)
        }
    };

    private static Line DiscountLine(decimal amount, string? discountAccountId) => new()
    {
        Amount              = amount,
        AmountSpecified     = true,
        DetailType          = LineDetailTypeEnum.DiscountLineDetail,
        DetailTypeSpecified = true,
        AnyIntuitObject     = new DiscountLineDetail
        {
            PercentBased          = false,
            PercentBasedSpecified = true,
            DiscountAccountRef    = Ref(discountAccountId)
        }
    };

    public static RemoteRecord ToRecord(Invoice invoice) =>
        new(RequireId(invoice, "invoice"),
            invoice.SyncToken ?? string.Empty,
            Name: null,
            invoice.DocNumber,
            Read(invoice.TotalAmt, invoice.TotalAmtSpecified),
            invoice.TxnTaxDetail is { } tax ? Read(tax.TotalTax, tax.TotalTaxSpecified) : null,
            ReadTxnActive(invoice));
}

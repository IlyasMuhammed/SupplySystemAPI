using SMS.Modules.Finance.Domain;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Finance.Integration;

/// <summary>
/// A sales invoice as the QuickBooks gateway wants to hear about it: lines at their gross price with the
/// line's discount and tax rate, and the invoice's own tax and grand total for the gateway to check its
/// arithmetic against.
/// </summary>
internal static class SalesInvoicePayloadFactory
{
    /// <param name="invoice">The invoice, with <see cref="SalesInvoice.Lines"/> loaded.</param>
    public static SalesInvoicePayload Build(SalesInvoice invoice)
    {
        var lines = invoice.Lines.OrderBy(l => l.LineNo).ToList();
        var (rate, rateCurrency) = DocumentRate(invoice.CurrencyCode, invoice.ExchangeRate, invoice.BaseCurrencyCode);

        return new SalesInvoicePayload
        {
            ExternalId           = invoice.UUID.ToString(),
            DocNumber            = invoice.InvoiceNumber,
            CustomerExternalId   = invoice.PartnerId.ToString(),
            TxnDate              = invoice.InvoiceDate,
            DueDate              = invoice.DueDate,
            CurrencyCode         = invoice.CurrencyCode?.Trim().ToUpperInvariant() ?? string.Empty,
            Status               = MapStatus(invoice.Status),
            Lines = lines.Select(l => new SalesInvoiceLinePayload
            {
                LineNo          = l.LineNo,
                ItemExternalId  = l.VariantUuid.ToString(),
                Description     = QuickBooksSupport.Clean(l.Description),
                Quantity        = l.Quantity,
                UnitPrice       = l.UnitPrice,
                DiscountPercent = l.DiscountPercent,
                TaxPercent      = l.TaxPercent,
                // SAP alignment S-11: the line's tax code snapshot, mapped by code before its rate. Null on lines
                // from before tax codes — omitted from the gateway's stored JSON, so their fingerprints hold.
                TaxCode         = QuickBooksSupport.Clean(l.TaxCode)
            }).ToList(),
            HeaderDiscountAmount = HeaderDiscount(invoice, lines),
            ExpectedTaxAmount    = invoice.TaxAmount,
            ExpectedTotal        = invoice.GrandTotal,
            CustomerMemo         = QuickBooksSupport.Clean(invoice.Notes),
            PrivateNote          = PrivateNote(invoice),
            // S-5/S-10: the rate fixed when the invoice was issued, so QuickBooks books it at the rate SMS did.
            ExchangeRate             = rate,
            ExchangeRateCurrencyCode = rateCurrency
        };
    }

    /// <summary>
    /// A document's exchange-rate snapshot (S-5) as the QuickBooks payload carries it: only for a foreign-currency
    /// document with a usable snapshot — 1 unit of its currency in the base currency. A home-currency document (rate
    /// 1), or one without a snapshot (none was on file, or a document from before snapshots), carries none, so its
    /// payload — and fingerprint — is exactly what it was.
    /// </summary>
    internal static (decimal? Rate, string? CurrencyCode) DocumentRate(string? currency, decimal? rate, string? baseCurrency)
    {
        var from = currency?.Trim().ToUpperInvariant();
        var to   = baseCurrency?.Trim().ToUpperInvariant();
        return rate is > 0m && !string.IsNullOrEmpty(from) && !string.IsNullOrEmpty(to) && from != to
            ? (rate, to)
            : (null, null);
    }

    internal static SalesInvoicePayloadStatus MapStatus(string? status) => status switch
    {
        SalesInvoiceStatuses.CreditNote => SalesInvoicePayloadStatus.CreditNote,
        SalesInvoiceStatuses.Cancelled  => SalesInvoicePayloadStatus.Cancelled,
        // ISSUED, PARTIALLY_PAID, PAID, OVERDUE: to QuickBooks all of them are simply an issued invoice —
        // payments are not synced (plan D-8). A DRAFT is never sent at all.
        _                               => SalesInvoicePayloadStatus.Issued
    };

    /// <summary>
    /// The line discounts as SCM totals them: every line's <c>qty × price × disc%</c>, summed, then rounded
    /// once to the cent (<see cref="MidpointRounding.AwayFromZero"/>) — exactly <c>SalesInvoiceTotals.Header</c>'s
    /// discount, and exactly what the gateway computes from the lines it is sent.
    /// </summary>
    internal static decimal LineDiscounts(IEnumerable<SalesInvoiceLine> lines) =>
        Math.Round(lines.Sum(l => l.Quantity * l.UnitPrice * l.DiscountPercent / 100m), 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// <b>SCM has no header discount</b>, so this is zero for every invoice <c>SalesInvoiceTotals</c> builds:
    /// its <c>DiscountAmount</c> <i>is</i> the rounded sum of the line discounts. The gateway rounds the way
    /// SCM does — <c>round(Σ gross) − (round(Σ line discount) + HeaderDiscountAmount) + tax = total</c> — and
    /// takes the line discounts off itself, so sending <c>DiscountAmount</c> here would discount every line
    /// twice.
    /// <para>
    /// Whatever the invoice's stored discount holds beyond its lines' (a figure edited by hand, say) is sent
    /// as a header discount; never less than zero, which the gateway refuses.
    /// </para>
    /// </summary>
    internal static decimal HeaderDiscount(SalesInvoice invoice, IEnumerable<SalesInvoiceLine> lines) =>
        Math.Max(0m, invoice.DiscountAmount - LineDiscounts(lines));

    /// <summary><c>SO {number}</c>, and <c> · DLV {number}</c> when the invoice bills a delivery.</summary>
    internal static string PrivateNote(SalesInvoice invoice)
    {
        var note = $"SO {invoice.SaleOrderNumber}";
        return string.IsNullOrWhiteSpace(invoice.DeliveryNumber) ? note : $"{note} · DLV {invoice.DeliveryNumber.Trim()}";
    }
}

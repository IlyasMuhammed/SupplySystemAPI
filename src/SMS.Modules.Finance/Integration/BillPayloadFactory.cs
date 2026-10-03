using SMS.Modules.Finance.Domain;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Finance.Integration;

/// <summary>
/// A supplier invoice as the QuickBooks gateway wants to hear about it: a bill. Supplier invoice lines name
/// no variant, only the purchase order line they bill, so the caller resolves those first
/// (<see cref="IPurchaseOrderLineVariants"/>) and hands the answer in.
/// </summary>
internal static class BillPayloadFactory
{
    /// <summary>
    /// What Logistics stamps on a payable it raises for a carrier's freight bill
    /// (<c>InvoiceSettlementService.PostingSourceType</c>). Repeated here because Finance does not
    /// reference Logistics; the value is also the idempotency key of that posting, so it does not drift.
    /// </summary>
    internal const string CarrierInvoiceSourceType = "CARRIER_INVOICE";

    /// <summary>The supplier invoice match status at which a bill is booked, and so sent.</summary>
    internal const string ApprovedMatchStatus = "Approved";

    internal static bool IsApproved(Invoice invoice) =>
        string.Equals(invoice.MatchStatus, ApprovedMatchStatus, StringComparison.OrdinalIgnoreCase);

    internal static bool IsCarrierBill(Invoice invoice) =>
        string.Equals(invoice.SourceType?.Trim(), CarrierInvoiceSourceType, StringComparison.OrdinalIgnoreCase);

    /// <param name="invoice">The supplier invoice, with <see cref="Invoice.Lines"/> loaded.</param>
    /// <param name="poLineVariants">
    /// Purchase order line uuid → the variant it ordered (null when the line named none). Lines whose PO line
    /// is missing from it are not goods QuickBooks can put against an item.
    /// </param>
    public static BillPayload Build(Invoice invoice, IReadOnlyDictionary<Guid, Guid?> poLineVariants)
    {
        var carrier = IsCarrierBill(invoice);
        var docNumber = QuickBooksSupport.Clean(invoice.SupplierInvoiceNo) ?? invoice.InvoiceNumber;
        var tax = HeaderTax(invoice);
        var (rate, rateCurrency) = SalesInvoicePayloadFactory.DocumentRate(invoice.Currency, invoice.ExchangeRate, invoice.BaseCurrencyCode);

        var lines = invoice.Lines
            .OrderBy(l => l.LineNo)
            .Select(l => Line(l, carrier, poLineVariants, tax))
            .ToList();

        // An invoice may be keyed in as a header only — a subtotal and no lines. A bill of no lines would
        // not add up to its own total, so the subtotal goes as one line of its own.
        if (lines.Count == 0 && invoice.Subtotal != 0m)
            lines.Add(new BillLinePayload
            {
                LineNo      = 1,
                Category    = carrier ? BillLineCategory.Freight : BillLineCategory.Other,
                Description = QuickBooksSupport.Clean(invoice.PoNumber) is { } po
                    ? $"Purchase order {po}"
                    : $"Supplier invoice {docNumber}",
                Amount      = invoice.Subtotal,
                TaxPercent  = tax.Percent,
                TaxCode     = tax.Code
            });

        return new BillPayload
        {
            ExternalId        = invoice.UUID.ToString(),
            DocNumber         = docNumber,
            VendorExternalId  = invoice.SupplierId.ToString(),
            TxnDate           = invoice.InvoiceDate,
            DueDate           = invoice.DueDate,
            CurrencyCode      = invoice.Currency?.Trim().ToUpperInvariant() ?? string.Empty,
            Lines             = lines,
            ExpectedTaxAmount = invoice.TaxAmount,
            ExpectedTotal     = invoice.TotalAmount,
            PrivateNote       = PrivateNote(invoice),
            // S-5/S-10: the rate fixed when the invoice was approved, so QuickBooks books it at the rate SMS did.
            ExchangeRate             = rate,
            ExchangeRateCurrencyCode = rateCurrency
        };
    }

    /// <summary>
    /// A carrier's bill is freight, every line of it. A line billing a purchase order line that ordered a
    /// known variant is goods against that item. Anything else — a PO line that cannot be found or named no
    /// variant, or a line with no PO line at all — is "other", for the default expense account.
    /// </summary>
    private static BillLinePayload Line(
        InvoiceLine line, bool carrier, IReadOnlyDictionary<Guid, Guid?> poLineVariants, (string? Code, decimal? Percent) tax)
    {
        Guid? item = !carrier
                  && line.PoLineUuid is { } poLine
                  && poLineVariants.TryGetValue(poLine, out var variant)
            ? variant
            : null;

        return new BillLinePayload
        {
            LineNo         = line.LineNo,
            ItemExternalId = item?.ToString(),
            Category       = carrier ? BillLineCategory.Freight
                           : item is not null ? BillLineCategory.Goods
                           : BillLineCategory.Other,
            Description    = QuickBooksSupport.Clean(line.ItemDescription),
            Quantity       = line.QtyInvoiced,
            UnitPrice      = line.UnitPrice,
            Amount         = line.LineTotal,
            TaxPercent     = tax.Percent,
            TaxCode        = tax.Code
        };
    }

    /// <summary>
    /// Supplier invoice lines carry no tax of their own: the invoice's purchase tax code (SAP alignment) is a
    /// header snapshot that applies to the whole subtotal, so every line carries it — QuickBooks then taxes
    /// each line with the code it is mapped to. An invoice from before tax codes (or with its tax keyed as an
    /// amount) has neither, and its lines stay without a rate: the gateway uses the default purchase tax code
    /// (plan D-10), and the payload — so its fingerprint — is exactly what it was.
    /// </summary>
    internal static (string? Code, decimal? Percent) HeaderTax(Invoice invoice) =>
        (QuickBooksSupport.Clean(invoice.TaxCode), invoice.TaxPercent);

    /// <summary><c>{InvoiceNumber}</c>, then <c> · PO {number}</c> and <c> · GRN {number}</c> where there are any.</summary>
    internal static string PrivateNote(Invoice invoice)
    {
        var note = invoice.InvoiceNumber;
        if (QuickBooksSupport.Clean(invoice.PoNumber)  is { } po)  note += $" · PO {po}";
        if (QuickBooksSupport.Clean(invoice.GrnNumber) is { } grn) note += $" · GRN {grn}";
        return note;
    }

    /// <summary>Every purchase order line the invoices' lines bill, once each — what to ask Demand about.</summary>
    internal static List<Guid> PoLinesOf(IEnumerable<Invoice> invoices) =>
        invoices
            .Where(i => !IsCarrierBill(i))
            .SelectMany(i => i.Lines)
            .Where(l => l.PoLineUuid.HasValue)
            .Select(l => l.PoLineUuid!.Value)
            .Distinct()
            .ToList();
}

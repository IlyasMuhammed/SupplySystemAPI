using SMS.Modules.Finance.Domain;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Finance.Integration.Posting;

/// <summary>The currency a supplier note is in — notes carry none of their own (D-75).</summary>
internal sealed record NoteCurrency(string Code, decimal? Rate, string? BaseCode);

/// <summary>
/// A44 (D-24, API-CONTRACT §3) — the payables side as postings: an approved supplier invoice (bill) and supplier
/// credit / debit notes (vendor credits). The bill's document is exactly <see cref="BillPayloadFactory.Build"/>.
/// </summary>
internal static class PurchasePostings
{
    /// <summary>
    /// Dr INVENTORY (goods lines) + Dr PURCHASE (freight, other) + Dr TAX_RECEIVABLE / Cr PAYABLE. Built from the bill's
    /// own lines (not the invoice's stored total, which a credit / debit note may since have reduced — the note is posted
    /// as a vendor credit of its own, so taking it off here too would count it twice).
    /// </summary>
    public static FinancialTransaction Bill(Invoice invoice, BillPayload payload)
    {
        var goods = payload.Lines.Where(l => l.Category == BillLineCategory.Goods).Sum(l => l.Amount);
        var other = payload.Lines.Where(l => l.Category != BillLineCategory.Goods).Sum(l => l.Amount);
        var lines = new GlLines(invoice.SupplierId)
            .Debit(PostingRoles.Inventory, goods, "Goods")
            .Debit(PostingRoles.Purchase, other, BillPayloadFactory.IsCarrierBill(invoice) ? "Freight" : "Purchases")
            .Debit(PostingRoles.TaxReceivable, payload.ExpectedTaxAmount, "Input tax", QuickBooksSupport.Clean(invoice.TaxCode));
        lines.Credit(PostingRoles.Payable, lines.TotalDebit, $"Supplier invoice {invoice.InvoiceNumber}");

        return new FinancialTransaction
        {
            DocumentType         = FinDocType.PurchaseBill,
            SourceDocumentId     = invoice.UUID,
            SourceDocumentNumber = invoice.InvoiceNumber,
            SourceModule         = PostingSourceModules.Finance,
            TransactionDate      = invoice.InvoiceDate,
            PartnerId            = invoice.SupplierId,
            PartnerType          = SMS.Shared.Finance.Posting.PartnerType.Vendor,
            PartnerName          = invoice.SupplierName,
            CurrencyCode         = payload.CurrencyCode,
            ExchangeRate         = payload.ExchangeRate,
            Description          = $"Supplier invoice {invoice.InvoiceNumber}" + (QuickBooksSupport.Clean(invoice.SupplierInvoiceNo) is { } no ? $" ({no})" : ""),
            Lines                = lines.Build(),
            Metadata = new FinancialDocumentMetadata
            {
                DueDate   = invoice.DueDate,
                Reference = QuickBooksSupport.Clean(invoice.SupplierInvoiceNo),
                Document  = payload
            }
        };
    }

    /// <summary>D-26: a reversed supplier invoice — the GL reverses it, QuickBooks flags the bill for the accountant.</summary>
    public static VoidRequest ReverseBill(Guid invoiceUuid, string invoiceNumber, string reason, int? userId) => new()
    {
        SourceDocumentId     = invoiceUuid,
        SourceModule         = PostingSourceModules.Finance,
        DocumentType         = FinDocType.PurchaseBill,
        SourceDocumentNumber = invoiceNumber,
        Reason               = reason,
        VoidedBy             = userId,
        VoidDate             = DateTime.UtcNow
    };

    /// <summary>A supplier's credit note (CN): what they credit us for a return.</summary>
    public static FinancialTransaction VendorCredit(CreditNote note, NoteCurrency currency) =>
        VendorCredit(note.UUID, note.CreditNoteNumber, QuickBooksSupport.Clean(note.SupplierCreditNoteNo) ?? note.CreditNoteNumber,
            note.SupplierId, note.SupplierName, note.CreditDate, note.CreditAmount, currency, VendorCreditSources.CreditNote,
            $"Credit note {note.CreditNoteNumber} for return {note.SroNumber}",
            $"{note.CreditNoteNumber} · SRO {note.SroNumber}" + (note.InvoiceNumber is { } inv ? $" · invoice {inv}" : ""),
            note.InvoiceUuid);

    /// <summary>A debit note we issued (DN): what we recover from the supplier.</summary>
    public static FinancialTransaction VendorCredit(DebitNote note, NoteCurrency currency) =>
        VendorCredit(note.UUID, note.DebitNoteNumber, note.DebitNoteNumber,
            note.SupplierId, note.SupplierName, (note.IssuedAt ?? note.CreatedDate).Date, note.DebitAmount, currency, VendorCreditSources.DebitNote,
            $"Debit note {note.DebitNoteNumber} for return {note.SroNumber}: {note.DebitReason}",
            $"{note.DebitNoteNumber} · SRO {note.SroNumber} · {note.DebitReason}" + (note.InvoiceNumber is { } inv ? $" · invoice {inv}" : ""),
            note.InvoiceUuid);

    /// <summary>
    /// Dr PAYABLE / Cr PURCHASE. SCM notes carry no item lines and no tax: one line on the default expense account
    /// (QuickBooks) / DEFAULT_PURCHASE (GL).
    /// </summary>
    private static FinancialTransaction VendorCredit(
        Guid uuid, string number, string docNumber, Guid supplier, string supplierName, DateTime date, decimal amount,
        NoteCurrency currency, string sourceKind, string description, string note, Guid? invoice)
    {
        var (rate, rateCurrency) = SalesInvoicePayloadFactory.DocumentRate(currency.Code, currency.Rate, currency.BaseCode);
        var payload = new VendorCreditPayload
        {
            ExternalId        = uuid.ToString(),
            DocNumber         = docNumber,
            VendorExternalId  = supplier.ToString(),
            TxnDate           = date,
            CurrencyCode      = PostingSupport.Currency(currency.Code),
            Lines             = [new BillLinePayload { LineNo = 1, Category = BillLineCategory.Other, Description = description, Amount = amount }],
            ExpectedTaxAmount = 0m,
            ExpectedTotal     = amount,
            PrivateNote       = note,
            SourceKind        = sourceKind,
            ExchangeRate             = rate,
            ExchangeRateCurrencyCode = rateCurrency
        };

        var lines = new GlLines(supplier)
            .Debit(PostingRoles.Payable, amount, description)
            .Credit(PostingRoles.Purchase, amount, "Purchase returns");

        return new FinancialTransaction
        {
            DocumentType         = FinDocType.VendorCredit,
            SourceDocumentId     = uuid,
            SourceDocumentNumber = number,
            SourceModule         = PostingSourceModules.Finance,
            TransactionDate      = date,
            PartnerId            = supplier,
            PartnerType          = SMS.Shared.Finance.Posting.PartnerType.Vendor,
            PartnerName          = supplierName,
            CurrencyCode         = payload.CurrencyCode,
            ExchangeRate         = rate,
            Description          = description,
            Lines                = lines.Build(),
            Metadata = new FinancialDocumentMetadata { Reference = docNumber, LinkedDocumentId = invoice, Document = payload }
        };
    }

    /// <summary>D-75: a debit note written off after it was sent — the GL reverses it, QuickBooks flags its vendor credit.</summary>
    public static VoidRequest ReverseVendorCredit(DebitNote note, string reason, int? userId) => new()
    {
        SourceDocumentId     = note.UUID,
        SourceModule         = PostingSourceModules.Finance,
        DocumentType         = FinDocType.VendorCredit,
        SourceDocumentNumber = note.DebitNoteNumber,
        Reason               = reason,
        VoidedBy             = userId,
        VoidDate             = DateTime.UtcNow
    };
}

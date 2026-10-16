using System.Globalization;
using SMS.Modules.Finance.Domain;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Finance.Integration.Posting;

/// <summary>
/// A44 (D-4, API-CONTRACT §3) — sales documents as postings: the GL lines plus the provider-neutral document. The
/// invoice's document is exactly <see cref="SalesInvoicePayloadFactory.Build"/> (unchanged builder → unchanged
/// fingerprints at QuickBooks); the POS documents (sales receipt, credit memo, refund receipt) are built from the same
/// lines the same way.
/// </summary>
internal static class SalesPostings
{
    /// <summary>"pos" for a POS document (sale, its credit notes), "finance" for everything else (D-13 keys).</summary>
    public static string ModuleOf(SalesInvoice document) =>
        PosDocuments.IsPos(document) ? PostingSourceModules.Pos : PostingSourceModules.Finance;

    public static VoidRequest Void(SalesInvoice document, FinDocType type, string reason, int? userId, DateTime? when) => new()
    {
        SourceDocumentId     = document.UUID,
        SourceModule         = ModuleOf(document),
        DocumentType         = type,
        SourceDocumentNumber = document.InvoiceNumber,
        Reason               = reason,
        VoidedBy             = userId,
        VoidDate             = when
    };

    // ── Sales invoice ────────────────────────────────────────────────────────

    /// <summary>
    /// An issued invoice: Dr RECEIVABLE (grand total) + Dr DISCOUNT_GIVEN (header discount) / Cr REVENUE (net of line
    /// discounts) + Cr TAX_PAYABLE (split by tax code).
    /// </summary>
    public static FinancialTransaction Invoice(SalesInvoice invoice)
    {
        var payload = SalesInvoicePayloadFactory.Build(invoice);
        var lines = new GlLines(invoice.PartnerId)
            .Debit(PostingRoles.Receivable, invoice.GrandTotal, $"Invoice {invoice.InvoiceNumber}")
            .Debit(PostingRoles.DiscountGiven, payload.HeaderDiscountAmount, "Discount");
        RevenueAndTax(lines, invoice, payload.HeaderDiscountAmount, credit: true);

        return Transaction(FinDocType.SalesInvoice, invoice, payload, lines,
            $"Sales invoice {invoice.InvoiceNumber}" + (PosDocuments.IsPos(invoice) ? $" (POS {invoice.SourceNumber})" : ""),
            dueDate: invoice.DueDate);
    }

    // ── POS sale paid in full at the till ───────────────────────────────────

    /// <summary>
    /// D-21: one sales receipt — Dr CASH/BANK per tender + Dr DISCOUNT_GIVEN / Cr REVENUE + Cr TAX_PAYABLE; no receivable.
    /// The deposit account follows the largest tender; the others are listed in the private note.
    /// </summary>
    public static FinancialTransaction SalesReceipt(SalesInvoice invoice, IReadOnlyCollection<PosTender> tenders)
    {
        var invoicePayload = SalesInvoicePayloadFactory.Build(invoice);
        var payload = Fill(new SalesReceiptPayload { Method = PosDocuments.MainMethod(tenders) }, invoicePayload);
        payload.SourceType = SalesInvoicePayloadSources.Pos;
        if (tenders.Count > 1) payload.PrivateNote = $"{invoicePayload.PrivateNote} · {Tenders(tenders)}";

        var lines = new GlLines(invoice.PartnerId);
        foreach (var t in tenders) lines.Debit(PostingRoles.MoneyFor(t.Method), t.Amount, $"{t.Method} {t.Number}");
        lines.Debit(PostingRoles.DiscountGiven, payload.HeaderDiscountAmount, "Discount");
        RevenueAndTax(lines, invoice, payload.HeaderDiscountAmount, credit: true);

        return Transaction(FinDocType.SalesReceipt, invoice, payload, lines, $"POS sale {invoice.SourceNumber} ({invoice.InvoiceNumber}), paid at the till");
    }

    // ── POS credit note (return) ─────────────────────────────────────────────

    /// <summary>
    /// D-23: a POS credit note as a refund receipt (all refunds cash-like) or a credit memo (on account / store credit;
    /// mixed → credit memo flagged for the accountant). GL: Dr REVENUE + Dr TAX_PAYABLE / Cr DISCOUNT_GIVEN + Cr RECEIVABLE
    /// (credit memo) or Cr CASH/BANK per refund (refund receipt); the cash refunds of a mixed one add Dr RECEIVABLE / Cr
    /// CASH/BANK, so the GL matches the customer ledger whatever goes to QuickBooks.
    /// </summary>
    /// <param name="originalIsInvoice">Whether the sale it credits went as an invoice (not a sales receipt) — only then can it be applied to it.</param>
    public static FinancialTransaction CreditNote(
        SalesInvoice creditNote, SalesInvoice? original, IReadOnlyCollection<PosTender> refunds, bool originalIsInvoice)
    {
        var kind    = PosDocuments.ClassifyCredit(creditNote.GrandTotal, refunds);
        var lines   = CreditLines(creditNote);
        var header  = CreditHeaderDiscount(creditNote, lines);
        var origRef = originalIsInvoice ? original?.UUID.ToString() : null;
        var note    = $"Credit note for {original?.InvoiceNumber ?? "(unknown invoice)"}"
                    + (string.IsNullOrWhiteSpace(creditNote.SourceNumber) ? "" : $" · POS {creditNote.SourceNumber.Trim()}");
        var cash    = refunds.Where(r => PosDocuments.IsCashLike(r.Method)).ToList();

        var gl = new GlLines(creditNote.PartnerId);
        RevenueAndTax(gl, creditNote, header, credit: false);
        gl.Credit(PostingRoles.DiscountGiven, header, "Discount");

        if (kind == PosCreditKind.RefundReceipt)
        {
            foreach (var r in refunds) gl.Credit(PostingRoles.MoneyFor(r.Method), r.Amount, $"Refund {r.Method} {r.Number}");

            var refund = Fill(new RefundReceiptPayload { Method = PosDocuments.MainMethod(refunds), OriginalInvoiceExternalId = origRef }, creditNote, lines, header);
            refund.PrivateNote = refunds.Count > 1 ? $"{note} · {Tenders(refunds)}" : note;
            return Transaction(FinDocType.RefundReceipt, creditNote, refund, gl, $"POS return {creditNote.SourceNumber} ({creditNote.InvoiceNumber}), refunded",
                linked: original?.UUID);
        }

        gl.Credit(PostingRoles.Receivable, creditNote.GrandTotal, $"Credit note {creditNote.InvoiceNumber}");
        foreach (var r in cash)
            gl.Debit(PostingRoles.Receivable, r.Amount, $"Refund {r.Number}").Credit(PostingRoles.MoneyFor(r.Method), r.Amount, $"Refund {r.Method} {r.Number}");

        var applied = originalIsInvoice ? creditNote.CreditedAmount : 0m;
        var memo = Fill(new CreditMemoPayload { OriginalInvoiceExternalId = origRef, ApplyToOriginal = applied > 0m }, creditNote, lines, header);
        memo.PrivateNote = note;

        var properties = new Dictionary<string, string>();
        if (applied > 0m) properties[PostingProperties.AppliedAmount] = applied.ToString("0.00", CultureInfo.InvariantCulture);
        if (kind == PosCreditKind.Mixed) properties[PostingProperties.FlagForAccountant] = MixedRefundNote(creditNote, cash);

        return Transaction(FinDocType.CreditNote, creditNote, memo, gl, $"POS return {creditNote.SourceNumber} ({creditNote.InvoiceNumber}), credited",
            linked: original?.UUID, properties: properties.Count == 0 ? null : properties);
    }

    /// <summary>What the accountant is told about a mixed refund (D-23): the credit memo went, the cash refunds did not.</summary>
    internal static string MixedRefundNote(SalesInvoice creditNote, IReadOnlyCollection<PosTender> cash) =>
        $"POS return {creditNote.SourceNumber} (credit note {creditNote.InvoiceNumber}) was refunded partly in money: "
      + string.Join(", ", cash.Select(r => $"{r.Number} {r.Method} {r.Amount:0.00}"))
      + $" of {creditNote.GrandTotal:0.00} {creditNote.CurrencyCode}. It was sent as one credit memo; record the refund of "
      + $"{cash.Sum(r => r.Amount):0.00} against it in QuickBooks by hand, then mark this item resolved.";

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Revenue and tax, on the credit side for a sale (<paramref name="credit"/>) or the debit side for a credit note.
    /// Revenue = grand total − tax + the header discount (so the lines balance by construction); tax split by tax code
    /// in proportion to each code's lines (their own tax amount where stored — POS — else qty × price × (1 − disc%) × tax%).
    /// </summary>
    private static void RevenueAndTax(GlLines lines, SalesInvoice document, decimal headerDiscount, bool credit)
    {
        var revenue = document.GrandTotal - document.TaxAmount + headerDiscount;
        if (credit) lines.Credit(PostingRoles.Revenue, revenue, "Sales"); else lines.Debit(PostingRoles.Revenue, revenue, "Sales returned");

        var weights = document.Lines
            .GroupBy(l => string.IsNullOrWhiteSpace(l.TaxCode) ? "" : l.TaxCode.Trim())
            .Select(g => (Key: g.Key, Weight: g.Sum(l => l.TaxAmount ?? l.Quantity * l.UnitPrice * (1m - l.DiscountPercent / 100m) * l.TaxPercent / 100m)))
            .ToList();
        if (weights.Count == 0) weights.Add(("", 0m));

        foreach (var (code, amount) in GlLines.Apportion(document.TaxAmount, weights))
        {
            var taxCode = code.Length == 0 ? null : code;
            if (credit) lines.Credit(PostingRoles.TaxPayable, amount, "Tax", taxCode); else lines.Debit(PostingRoles.TaxPayable, amount, "Tax", taxCode);
        }
    }

    /// <summary>A credit note's lines as the gateway takes them: gross unit price, no per-line discount (it is in the header).</summary>
    private static List<SalesInvoiceLinePayload> CreditLines(SalesInvoice creditNote) =>
        creditNote.Lines.OrderBy(l => l.LineNo).Select(l => new SalesInvoiceLinePayload
        {
            LineNo          = l.LineNo,
            ItemExternalId  = l.VariantUuid.ToString(),
            Description     = QuickBooksSupport.Clean(l.Description),
            Quantity        = l.Quantity,
            UnitPrice       = l.UnitPrice,
            DiscountPercent = 0m,
            TaxPercent      = l.TaxPercent,
            TaxCode         = QuickBooksSupport.Clean(l.TaxCode)
        }).ToList();

    /// <summary>
    /// A credit note's lines are the original's gross unit price × the returned quantity, but it credits only the net
    /// returned: the difference (the sale's discount on those units) goes as a header discount, so
    /// round(Σ gross) − header + tax = total, the gateway's arithmetic. Never below zero.
    /// </summary>
    internal static decimal CreditHeaderDiscount(SalesInvoice creditNote, IEnumerable<SalesInvoiceLinePayload> lines) =>
        Math.Max(0m, GlLines.Round(lines.Sum(l => l.Quantity * l.UnitPrice)) - (creditNote.GrandTotal - creditNote.TaxAmount));

    private static T Fill<T>(T target, SalesInvoicePayload source) where T : SalesDocumentPayload
    {
        target.ExternalId               = source.ExternalId;
        target.DocNumber                = source.DocNumber;
        target.CustomerExternalId       = source.CustomerExternalId;
        target.TxnDate                  = source.TxnDate;
        target.CurrencyCode             = source.CurrencyCode;
        target.Lines                    = source.Lines;
        target.HeaderDiscountAmount     = source.HeaderDiscountAmount;
        target.ExpectedTaxAmount        = source.ExpectedTaxAmount;
        target.ExpectedTotal            = source.ExpectedTotal;
        target.PrivateNote              = source.PrivateNote;
        target.ExchangeRate             = source.ExchangeRate;
        target.ExchangeRateCurrencyCode = source.ExchangeRateCurrencyCode;
        target.SourceType               = source.SourceType;
        return target;
    }

    private static T Fill<T>(T target, SalesInvoice creditNote, List<SalesInvoiceLinePayload> lines, decimal header) where T : SalesDocumentPayload
    {
        var (rate, rateCurrency) = SalesInvoicePayloadFactory.DocumentRate(creditNote.CurrencyCode, creditNote.ExchangeRate, creditNote.BaseCurrencyCode);
        target.ExternalId               = creditNote.UUID.ToString();
        target.DocNumber                = creditNote.InvoiceNumber;
        target.CustomerExternalId       = creditNote.PartnerId.ToString();
        target.TxnDate                  = creditNote.InvoiceDate;
        target.CurrencyCode             = PostingSupport.Currency(creditNote.CurrencyCode);
        target.Lines                    = lines;
        target.HeaderDiscountAmount     = header;
        target.ExpectedTaxAmount        = creditNote.TaxAmount;
        target.ExpectedTotal            = creditNote.GrandTotal;
        target.ExchangeRate             = rate;
        target.ExchangeRateCurrencyCode = rateCurrency;
        target.SourceType               = PosDocuments.IsPos(creditNote) ? SalesInvoicePayloadSources.Pos : null;
        return target;
    }

    private static string Tenders(IEnumerable<PosTender> tenders) =>
        string.Join(", ", tenders.Select(t => $"{t.Method} {t.Amount:0.00}"));

    private static FinancialTransaction Transaction(
        FinDocType type, SalesInvoice document, IAccountingDocument payload, GlLines lines, string description,
        DateTime? dueDate = null, Guid? linked = null, IReadOnlyDictionary<string, string>? properties = null)
    {
        var (rate, _) = SalesInvoicePayloadFactory.DocumentRate(document.CurrencyCode, document.ExchangeRate, document.BaseCurrencyCode);
        return new FinancialTransaction
        {
            DocumentType         = type,
            SourceDocumentId     = document.UUID,
            SourceDocumentNumber = document.InvoiceNumber,
            SourceModule         = ModuleOf(document),
            TransactionDate      = document.InvoiceDate,
            PartnerId            = document.PartnerId,
            PartnerType          = SMS.Shared.Finance.Posting.PartnerType.Customer,
            PartnerName          = document.PartnerName,
            CurrencyCode         = PostingSupport.Currency(document.CurrencyCode),
            ExchangeRate         = rate,
            Description          = description,
            Lines                = lines.Build(),
            Metadata = new FinancialDocumentMetadata
            {
                DueDate          = dueDate,
                Reference        = PosDocuments.IsPos(document) ? document.SourceNumber : QuickBooksSupport.Clean(document.SaleOrderNumber),
                LinkedDocumentId = linked,
                Properties       = properties,
                Document         = payload
            }
        };
    }
}

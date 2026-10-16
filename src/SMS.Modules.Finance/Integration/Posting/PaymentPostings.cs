using SMS.Modules.Finance.Domain;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Finance.Integration.Posting;

/// <summary>One allocation of a payment, as the posting needs it.</summary>
/// <param name="IsOpening">The document is an imported opening balance — never in QuickBooks, so not linked there (D-74).</param>
internal sealed record PostingApplication(Guid DocumentUuid, string DocumentNumber, decimal Amount, bool IsOpening);

/// <summary>
/// A44 (D-19, D-20) — customer receipts and supplier payments as postings. Exchange differences: the payment is
/// converted at its own (locked) rate, the receivable / payable it settles was booked at the documents' rates; the
/// realized difference SCM stored (+ gain / − loss, base) goes to DEFAULT_EXCHANGE_GAIN_LOSS, expressed in document
/// currency (÷ rate) like every other line, the receivable / payable line taking the rest.
/// </summary>
internal static class PaymentPostings
{
    // ── Customer payment → Payment ───────────────────────────────────────────

    /// <summary>"pos" for a POS tender payment (SourceUuid set), "finance" for a back-office receipt.</summary>
    public static string ModuleOf(CustomerPayment payment) =>
        payment.SourceUuid is null ? PostingSourceModules.Finance : PostingSourceModules.Pos;

    /// <summary>D-19: STORE_CREDIT and EXCHANGE move no money, so they are not posted.</summary>
    public static bool IsPosted(CustomerPayment payment) => PosDocuments.IsCashLike(payment.PaymentMethod);

    /// <summary>
    /// A RECEIVED payment: Dr CASH (CASH) / BANK (others) amount ; Cr RECEIVABLE ; ± EXCHANGE_GAIN_LOSS. Its allocations
    /// are the payment's applications (opening-balance invoices left out: never in QuickBooks); the rest is unapplied.
    /// </summary>
    /// <param name="baseCurrencyCode">The sale base the payment's rate converts into (null when unknown).</param>
    public static FinancialTransaction CustomerPayment(
        CustomerPayment payment, IReadOnlyList<PostingApplication> applications, string? baseCurrencyCode)
    {
        var currency = PostingSupport.Currency(payment.CurrencyCode);
        var (rate, rateCurrency) = SalesInvoicePayloadFactory.DocumentRate(currency, payment.ExchangeRate, baseCurrencyCode);
        var linked = applications.Where(a => !a.IsOpening).ToList();
        var opening = applications.Where(a => a.IsOpening).ToList();

        var payload = new PaymentPayload
        {
            ExternalId         = payment.UUID.ToString(),
            DocNumber          = payment.PaymentNumber,
            CustomerExternalId = payment.PartnerId.ToString(),
            TxnDate            = payment.PaymentDate,
            CurrencyCode       = currency,
            Amount             = payment.Amount,
            Method             = payment.PaymentMethod.Trim().ToUpperInvariant(),
            Reference          = QuickBooksSupport.Clean(payment.ChequeNumber) ?? QuickBooksSupport.Clean(payment.BankReference),
            Applications       = linked.Select(a => new PaymentApplicationPayload { InvoiceExternalId = a.DocumentUuid.ToString(), Amount = a.Amount }).ToList(),
            PrivateNote        = Note(QuickBooksSupport.Clean(payment.Notes), opening),
            ExchangeRate             = rate,
            ExchangeRateCurrencyCode = rateCurrency,
            SourceType         = payment.SourceUuid is null ? null : SalesInvoicePayloadSources.Pos
        };

        var difference = rate is { } r ? (payment.ExchangeDifference ?? 0m) / r : 0m;
        var lines = new GlLines(payment.PartnerId)
            .Debit(PostingRoles.MoneyFor(payment.PaymentMethod), payment.Amount, $"{payload.Method} {payment.PaymentNumber}")
            .Credit(PostingRoles.Receivable, payment.Amount - GlLines.Round(difference), $"Payment {payment.PaymentNumber}")
            .Credit(PostingRoles.ExchangeGainLoss, difference, "Realized exchange difference");

        return new FinancialTransaction
        {
            DocumentType         = FinDocType.Payment,
            SourceDocumentId     = payment.UUID,
            SourceDocumentNumber = payment.PaymentNumber,
            SourceModule         = ModuleOf(payment),
            TransactionDate      = payment.PaymentDate,
            PartnerId            = payment.PartnerId,
            PartnerType          = SMS.Shared.Finance.Posting.PartnerType.Customer,
            PartnerName          = payment.PartnerName,
            CurrencyCode         = currency,
            ExchangeRate         = rate,
            Description          = $"Customer payment {payment.PaymentNumber} by {payload.Method}"
                                 + (applications.Count == 0 ? ", on account" : $", applied to {string.Join(", ", applications.Select(a => a.DocumentNumber).Take(5))}"),
            Lines                = lines.Build(),
            Metadata = new FinancialDocumentMetadata { Reference = payload.Reference, Document = payload }
        };
    }

    public static VoidRequest VoidCustomerPayment(CustomerPayment payment, string reason, int? userId, DateTime? when) => new()
    {
        SourceDocumentId     = payment.UUID,
        SourceModule         = ModuleOf(payment),
        DocumentType         = FinDocType.Payment,
        SourceDocumentNumber = payment.PaymentNumber,
        Reason               = reason,
        VoidedBy             = userId,
        VoidDate             = when
    };

    // ── Supplier payment → BillPayment ───────────────────────────────────────

    internal const string AdvancePayment = "ADVANCE_PAYMENT";

    /// <summary>D-20: a purchase-return settlement moves no money to the supplier — the vendor credit settles it.</summary>
    public static bool IsPosted(SupplierPayment payment) =>
        !string.Equals(payment.PaymentType, Repositories.SupplierPaymentRepository.PurchaseReturnSettlement, StringComparison.OrdinalIgnoreCase);

    /// <summary>SCM's supplier payment methods in the posting vocabulary (<see cref="PaymentMethodCodes"/>).</summary>
    public static string Method(string? method) => method?.Trim().ToUpperInvariant() switch
    {
        "CASH"        => PaymentMethodCodes.Cash,
        "CHEQUE"      => PaymentMethodCodes.Cheque,
        "ONLINE_WIRE" => PaymentMethodCodes.Online,
        "ONLINE"      => PaymentMethodCodes.Online,
        "CARD"        => PaymentMethodCodes.Card,
        "MOBILE"      => PaymentMethodCodes.Mobile,
        _             => PaymentMethodCodes.BankTransfer
    };

    /// <summary>
    /// A POSTED payment: Dr PAYABLE / Cr BANK (CASH for cash) ± EXCHANGE_GAIN_LOSS. A STANDARD payment's lines are its
    /// applications; an advance has none (a debit balance with the vendor).
    /// </summary>
    public static FinancialTransaction SupplierPayment(
        SupplierPayment payment, IReadOnlyList<PostingApplication> applications, string? baseCurrencyCode)
    {
        var currency = PostingSupport.Currency(payment.CurrencyCode);
        var (rate, rateCurrency) = SalesInvoicePayloadFactory.DocumentRate(currency, payment.ExchangeRate, baseCurrencyCode);
        var advance = string.Equals(payment.PaymentType, AdvancePayment, StringComparison.OrdinalIgnoreCase);
        var linked  = advance ? [] : applications.Where(a => !a.IsOpening).ToList();
        var opening = advance ? [] : applications.Where(a => a.IsOpening).ToList();
        var method  = Method(payment.PaymentMethod);

        var payload = new BillPaymentPayload
        {
            ExternalId       = payment.UUID.ToString(),
            DocNumber        = payment.PaymentNumber,
            VendorExternalId = payment.SupplierId.ToString(),
            TxnDate          = payment.PaymentDate,
            CurrencyCode     = currency,
            Amount           = payment.TotalAmount,
            Method           = method,
            Reference        = QuickBooksSupport.Clean(payment.ChequeNo),
            Applications     = linked.Select(a => new BillPaymentApplicationPayload { BillExternalId = a.DocumentUuid.ToString(), Amount = a.Amount }).ToList(),
            PrivateNote      = Note(advance ? $"Advance payment {payment.PaymentNumber}" : QuickBooksSupport.Clean(payment.Notes), opening),
            ExchangeRate             = rate,
            ExchangeRateCurrencyCode = rateCurrency
        };

        var difference = rate is { } r ? (payment.ExchangeDifference ?? 0m) / r : 0m;
        var lines = new GlLines(payment.SupplierId)
            .Debit(PostingRoles.Payable, payment.TotalAmount + GlLines.Round(difference), $"Payment {payment.PaymentNumber}")
            .Credit(PostingRoles.MoneyFor(method), payment.TotalAmount, $"{method} {payment.PaymentNumber}")
            .Credit(PostingRoles.ExchangeGainLoss, difference, "Realized exchange difference");

        return new FinancialTransaction
        {
            DocumentType         = FinDocType.BillPayment,
            SourceDocumentId     = payment.UUID,
            SourceDocumentNumber = payment.PaymentNumber,
            SourceModule         = PostingSourceModules.Finance,
            TransactionDate      = payment.PaymentDate,
            PartnerId            = payment.SupplierId,
            PartnerType          = SMS.Shared.Finance.Posting.PartnerType.Vendor,
            PartnerName          = payment.SupplierName,
            CurrencyCode         = currency,
            ExchangeRate         = rate,
            Description          = $"Supplier payment {payment.PaymentNumber} by {method}" + (advance ? " (advance)" : ""),
            Lines                = lines.Build(),
            Metadata = new FinancialDocumentMetadata { Reference = payload.Reference, Document = payload }
        };
    }

    public static VoidRequest VoidSupplierPayment(SupplierPayment payment, string reason, int? userId, DateTime? when) => new()
    {
        SourceDocumentId     = payment.UUID,
        SourceModule         = PostingSourceModules.Finance,
        DocumentType         = FinDocType.BillPayment,
        SourceDocumentNumber = payment.PaymentNumber,
        Reason               = reason,
        VoidedBy             = userId,
        VoidDate             = when
    };

    /// <summary>The note, plus which opening-balance documents part of the money settles (not linked in QuickBooks).</summary>
    private static string? Note(string? note, IReadOnlyCollection<PostingApplication> opening)
    {
        if (opening.Count == 0) return note;
        var text = $"Also settles opening balance {string.Join(", ", opening.Select(o => $"{o.DocumentNumber} ({o.Amount:0.00})"))}";
        return note is null ? text : $"{note} · {text}";
    }
}

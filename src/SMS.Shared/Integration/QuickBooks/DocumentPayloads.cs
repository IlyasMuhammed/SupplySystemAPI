using System.Text.Json.Serialization;
using SMS.Shared.Finance.Posting;

namespace SMS.Shared.Integration.QuickBooks;

// A44 (D-19..D-24) — the payment and credit documents, in the same neutral style as Payloads.cs: SCM uuids as
// ExternalId, SCM codes, amounts in document currency. Optional fields are omitted from the stored JSON when
// null so fingerprints stay stable. See docs/financial-posting/API-CONTRACT.md §2.

/// <summary>Values of the payment Method fields (SCM CustomerPayment / SupplierPayment methods).</summary>
public static class PaymentMethodCodes
{
    public const string Cash         = "CASH";
    public const string Cheque       = "CHEQUE";
    public const string BankTransfer = "BANK_TRANSFER";
    public const string Card         = "CARD";
    public const string Online       = "ONLINE";
    public const string Mobile       = "MOBILE";

    public static readonly IReadOnlyList<string> All = [Cash, Cheque, BankTransfer, Card, Online, Mobile];
}

public sealed class PaymentApplicationPayload
{
    public string  InvoiceExternalId { get; set; } = string.Empty;
    public decimal Amount            { get; set; }
}

/// <summary>A customer receipt (SCM CustomerPayment RECEIVED) → QBO Payment.</summary>
public sealed class PaymentPayload : IAccountingDocument
{
    public string   ExternalId         { get; set; } = string.Empty;
    public string   DocNumber          { get; set; } = string.Empty;
    public string   CustomerExternalId { get; set; } = string.Empty;
    public DateTime TxnDate            { get; set; }
    public string   CurrencyCode       { get; set; } = string.Empty;
    public decimal  Amount             { get; set; }
    /// <summary>See <see cref="PaymentMethodCodes"/>.</summary>
    public string   Method             { get; set; } = PaymentMethodCodes.Cash;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string?  Reference   { get; set; }
    public List<PaymentApplicationPayload> Applications { get; set; } = new();
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string?  PrivateNote { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public decimal? ExchangeRate { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string?  ExchangeRateCurrencyCode { get; set; }
    /// <summary>"POS" for a till tender.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string?  SourceType  { get; set; }
}

public sealed class BillPaymentApplicationPayload
{
    public string  BillExternalId { get; set; } = string.Empty;
    public decimal Amount         { get; set; }
}

/// <summary>A supplier payment (SCM SupplierPayment POSTED) → QBO BillPayment.</summary>
public sealed class BillPaymentPayload : IAccountingDocument
{
    public string   ExternalId       { get; set; } = string.Empty;
    public string   DocNumber        { get; set; } = string.Empty;
    public string   VendorExternalId { get; set; } = string.Empty;
    public DateTime TxnDate          { get; set; }
    public string   CurrencyCode     { get; set; } = string.Empty;
    public decimal  Amount           { get; set; }
    public string   Method           { get; set; } = PaymentMethodCodes.BankTransfer;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string?  Reference   { get; set; }
    public List<BillPaymentApplicationPayload> Applications { get; set; } = new();
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string?  PrivateNote { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public decimal? ExchangeRate { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string?  ExchangeRateCurrencyCode { get; set; }
}

/// <summary>What sales receipts, credit memos and refund receipts have in common with an invoice.</summary>
public abstract class SalesDocumentPayload : IAccountingDocument
{
    public string   ExternalId           { get; set; } = string.Empty;
    public string   DocNumber            { get; set; } = string.Empty;
    /// <summary>Null for a walk-in POS customer the gateway sends under its default customer.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? CustomerExternalId { get; set; }
    public DateTime TxnDate              { get; set; }
    public string   CurrencyCode         { get; set; } = string.Empty;
    public List<SalesInvoiceLinePayload> Lines { get; set; } = new();
    public decimal  HeaderDiscountAmount { get; set; }
    public decimal  ExpectedTaxAmount    { get; set; }
    public decimal  ExpectedTotal        { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string?  PrivateNote { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public decimal? ExchangeRate { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string?  ExchangeRateCurrencyCode { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string?  SourceType { get; set; }
    /// <summary>
    /// A44 D-23 (mixed tenders): sent as usual, then — once QuickBooks has it — flagged for the accountant with this reason
    /// (e.g. "cash refunds 500.00 not entered"). The QuickBooks target fills it from <c>Metadata.Properties["FlagForAccountant"]</c>.
    /// Omitted when null (fingerprints unchanged).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string?  FlagForAccountant { get; set; }
}

/// <summary>A POS sale paid in full at the till with cash-like tenders (D-21) → QBO SalesReceipt.</summary>
public sealed class SalesReceiptPayload : SalesDocumentPayload
{
    /// <summary>The tender that decides the deposit account (the largest one).</summary>
    public string Method { get; set; } = PaymentMethodCodes.Cash;
}

/// <summary>A customer credit note (SCM SalesInvoice CREDIT_NOTE) → QBO CreditMemo (D-23).</summary>
public sealed class CreditMemoPayload : SalesDocumentPayload
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? OriginalInvoiceExternalId { get; set; }
    /// <summary>ON_ACCOUNT: applied to the original invoice in QuickBooks too.</summary>
    public bool ApplyToOriginal { get; set; }
    /// <summary>
    /// The amount applied to the original invoice when less than the credit's total (SCM: what was put on account against
    /// it). Null = the whole total. The QuickBooks target fills it from <c>Metadata.Properties["AppliedAmount"]</c>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public decimal? AppliedAmount { get; set; }
}

/// <summary>A credit note refunded in cash-like tenders (D-23) → QBO RefundReceipt.</summary>
public sealed class RefundReceiptPayload : SalesDocumentPayload
{
    public string Method { get; set; } = PaymentMethodCodes.Cash;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? OriginalInvoiceExternalId { get; set; }
}

/// <summary>Values of <see cref="VendorCreditPayload.SourceKind"/>.</summary>
public static class VendorCreditSources
{
    public const string CreditNote = "CREDIT_NOTE";
    public const string DebitNote  = "DEBIT_NOTE";
}

/// <summary>A supplier credit note or a debit note we issued (D-24) → QBO VendorCredit.</summary>
public sealed class VendorCreditPayload : IAccountingDocument
{
    public string   ExternalId        { get; set; } = string.Empty;
    public string   DocNumber         { get; set; } = string.Empty;
    public string   VendorExternalId  { get; set; } = string.Empty;
    public DateTime TxnDate           { get; set; }
    public string   CurrencyCode      { get; set; } = string.Empty;
    public List<BillLinePayload> Lines { get; set; } = new();
    public decimal  ExpectedTaxAmount { get; set; }
    public decimal  ExpectedTotal     { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string?  PrivateNote { get; set; }
    /// <summary>See <see cref="VendorCreditSources"/>.</summary>
    public string   SourceKind        { get; set; } = VendorCreditSources.CreditNote;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public decimal? ExchangeRate { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string?  ExchangeRateCurrencyCode { get; set; }
}

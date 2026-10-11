using SMS.Shared.Common;

namespace SMS.Modules.Finance.Domain;

// A29-P7-02 §9.3/§9.4 — money received from a customer, and how it was applied. The receivable
// counterpart of SupplierPayment/SupplierPaymentLine: a payment is a header on its own, and its
// allocations say which invoices it settled, so one receipt can cover several invoices (§9.4) or
// none at all yet (an advance, allocated later).

/// <summary>§9.3's closed vocabulary, persisted as-is.</summary>
internal static class CustomerPaymentMethods
{
    public const string Cash         = "CASH";
    public const string Cheque       = "CHEQUE";
    public const string BankTransfer = "BANK_TRANSFER";
    public const string Card         = "CARD";
    public const string Online       = "ONLINE";
    /// <summary>A38 (D-8) — mobile wallet / mobile money taken at a POS counter.</summary>
    public const string Mobile       = "MOBILE";

    /// <summary>A39 (XFIN, D-3) — POS store credit redeemed as a payment / issued as a refund. No money moves.</summary>
    public const string StoreCredit  = "STORE_CREDIT";
    /// <summary>A39 (XFIN, D-6) — the value an exchange carries from its return part (refund) to its new sale (payment). No money moves.</summary>
    public const string Exchange     = "EXCHANGE";

    /// <summary>What a person can record in the back office (money that really came in).</summary>
    public static readonly IReadOnlyList<string> All = [Cash, Cheque, BankTransfer, Card, Online, Mobile];

    /// <summary>A39 — internal POS methods: recorded only by the counter-sale service, never keyed by hand, never cash.</summary>
    public static readonly IReadOnlyList<string> NonCash = [StoreCredit, Exchange];

    /// <summary>Every method a stored payment / refund can carry (list filters, labels).</summary>
    public static readonly IReadOnlyList<string> Recorded = [.. All, .. NonCash];
}

internal static class CustomerPaymentStatuses
{
    /// <summary>The money is in; its allocations reduce the invoices they name.</summary>
    public const string Received = "RECEIVED";
    /// <summary>A cheque the bank returned — the receivable stands again.</summary>
    public const string Bounced  = "BOUNCED";
    /// <summary>Undone by us (keyed in error, refunded).</summary>
    public const string Reversed = "REVERSED";

    public static readonly IReadOnlyList<string> All = [Received, Bounced, Reversed];
}

internal class CustomerPayment : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     UUID           { get; set; }
    public Guid     OrganizationId { get; set; }

    /// <summary>The customer. Bare UUID onto suppliers.BusinessPartners.</summary>
    public Guid     PartnerId      { get; set; }
    public string   PartnerName    { get; set; } = string.Empty;

    /// <summary>CPAY-YYYYMMDD-NNNN.</summary>
    public string   PaymentNumber  { get; set; } = string.Empty;
    public DateTime PaymentDate    { get; set; }
    public decimal  Amount         { get; set; }

    /// <summary>See <see cref="CustomerPaymentMethods"/>.</summary>
    public string   PaymentMethod  { get; set; } = string.Empty;
    public string?  ChequeNumber   { get; set; }
    public string?  BankReference  { get; set; }
    public string   CurrencyCode   { get; set; } = "PKR";
    public string?  Notes          { get; set; }

    // ── A35 (D-10, D-12): locked when the payment is recorded (RECEIVED = posted), at the payment date, sale base ──
    public Guid?    CurrencyId         { get; set; }
    public decimal? ExchangeRate       { get; set; }
    public Guid?    BaseCurrencyId     { get; set; }
    public decimal? AmountBase         { get; set; }
    /// <summary>Σ of the allocations' realized differences (+ gain / − loss, sale base).</summary>
    public decimal? ExchangeDifference { get; set; }

    /// <summary>See <see cref="CustomerPaymentStatuses"/>.</summary>
    public string   Status         { get; set; } = CustomerPaymentStatuses.Received;

    /// <summary>A38 — the POS tender this payment was recorded for (null for a back-office receipt).</summary>
    public Guid?    SourceUuid     { get; set; }

    public int       CreatedBy    { get; set; }
    public DateTime  CreatedDate  { get; set; }
    public int?      ModifiedBy   { get; set; }
    public DateTime? ModifiedDate { get; set; }

    public ICollection<PaymentAllocation> Allocations { get; set; } = new List<PaymentAllocation>();
}

/// <summary>
/// One payment applied to one invoice. The invoice side is a real foreign key with no cascade: an
/// invoice that has money against it cannot simply vanish, and deleting the payment is what removes
/// its allocations.
/// </summary>
internal class PaymentAllocation : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid UUID           { get; set; }
    public Guid OrganizationId { get; set; }

    public int             CustomerPaymentId { get; set; }
    public CustomerPayment CustomerPayment   { get; set; } = null!;

    public int          SalesInvoiceId { get; set; }
    public SalesInvoice SalesInvoice   { get; set; } = null!;

    public decimal  AllocatedAmount { get; set; }
    public DateTime AllocatedAt     { get; set; }
    public int      AllocatedBy     { get; set; }

    /// <summary>A35 C7 — realized gain (+) / loss (−) on this allocation in the sale base (null before A35).</summary>
    public decimal? ExchangeDifference { get; set; }
}

/// <summary>
/// A38 (FIN, D-10) — money paid back to a customer against a credit note (a POS return refunded in cash, card, …). Its
/// customer-ledger entry is a REFUND debit: the credit note took the amount off what the customer owes, the refund puts
/// it back because the customer has been paid. Append-only; never allocated to invoices.
/// </summary>
internal class CustomerRefund : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid UUID           { get; set; }
    public Guid OrganizationId { get; set; }

    /// <summary>CRF-YYYYMMDD-NNNN.</summary>
    public string   RefundNumber  { get; set; } = string.Empty;
    public Guid     PartnerId     { get; set; }
    /// <summary>The credit note this refund pays out (a sales_invoices row with status CREDIT_NOTE). Bare UUID.</summary>
    public Guid     CreditNoteUuid { get; set; }
    public DateTime RefundDate    { get; set; }
    public decimal  Amount        { get; set; }
    /// <summary>See <see cref="CustomerPaymentMethods"/>.</summary>
    public string   Method        { get; set; } = string.Empty;
    public string?  Reference     { get; set; }
    public string   CurrencyCode  { get; set; } = "PKR";
    /// <summary>The POS refund tender it was recorded for.</summary>
    public Guid?    SourceUuid    { get; set; }
    public string?  Notes         { get; set; }

    public int      CreatedBy     { get; set; }
    public DateTime CreatedDate   { get; set; }
}

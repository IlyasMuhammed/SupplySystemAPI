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

    public static readonly IReadOnlyList<string> All = [Cash, Cheque, BankTransfer, Card, Online];
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

    /// <summary>See <see cref="CustomerPaymentStatuses"/>.</summary>
    public string   Status         { get; set; } = CustomerPaymentStatuses.Received;

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
}

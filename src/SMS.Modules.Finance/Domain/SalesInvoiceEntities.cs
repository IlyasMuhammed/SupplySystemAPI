using SMS.Shared.Common;

namespace SMS.Modules.Finance.Domain;

// A29-P7-01 §9.1/§9.2 — what the company bills a customer for a sale order. The receivable side's
// counterpart to Invoice (a supplier's bill to us): the same shape of money columns and the same
// per-org numbering, but its own table, because the two are read, aged and paid by different
// people against different ledgers, and one table with a direction flag is how a payables report
// ends up quietly summing receivables.
//
// Cross-module references (SaleOrderUuid, PartnerId, SoLineUuid, VariantUuid) are bare UUIDs with
// no FK, as every other Finance entity does — Demand, Suppliers and Inventory own those rows.

/// <summary>The closed status vocabulary of §9.1, persisted as-is.</summary>
internal static class SalesInvoiceStatuses
{
    /// <summary>Being prepared; not yet in the customer's ledger.</summary>
    public const string Draft         = "DRAFT";
    /// <summary>Sent to the customer; the receivable is booked (§9.5 — CustomerLedger DEBIT).</summary>
    public const string Issued        = "ISSUED";
    public const string PartiallyPaid = "PARTIALLY_PAID";
    public const string Paid          = "PAID";
    /// <summary>Set by a Hangfire job once the due date has passed with a balance still owing.</summary>
    public const string Overdue       = "OVERDUE";
    public const string Cancelled     = "CANCELLED";
    /// <summary>A negative invoice — the customer is owed, not owing.</summary>
    public const string CreditNote    = "CREDIT_NOTE";

    public static readonly IReadOnlyList<string> All =
        [Draft, Issued, PartiallyPaid, Paid, Overdue, Cancelled, CreditNote];
}

internal class SalesInvoice : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     UUID           { get; set; }
    public Guid     OrganizationId { get; set; }

    /// <summary>Copied from the sale order (§9.1), so one trace spans SO → delivery → invoice → payment.</summary>
    public Guid     TraceId        { get; set; }

    /// <summary>SINV-YYYYMMDD-NNNN.</summary>
    public string   InvoiceNumber  { get; set; } = string.Empty;

    /// <summary>The sale order this bills. Bare UUID — sale orders live in Demand.</summary>
    public Guid     SaleOrderUuid    { get; set; }
    /// <summary>Denormalised so the invoice reads and prints without a call into Demand.</summary>
    public string   SaleOrderNumber  { get; set; } = string.Empty;

    /// <summary>
    /// The delivery this invoice bills (A29-P7-04). Bare UUID — deliveries live in Logistics. At most
    /// one live (not deleted, not cancelled) invoice may exist per delivery: a filtered unique index
    /// is what stops a double click or a retry billing the same goods twice. Several invoices per
    /// sale order (§9.5) are several deliveries, each invoiced once.
    /// </summary>
    public Guid?    DeliveryUuid   { get; set; }
    /// <summary>Denormalised so the invoice prints "against DLV-…" without a call into Logistics.</summary>
    public string?  DeliveryNumber { get; set; }

    /// <summary>The customer. Bare UUID onto suppliers.BusinessPartners.</summary>
    public Guid     PartnerId      { get; set; }
    public string   PartnerName    { get; set; } = string.Empty;

    public DateTime InvoiceDate    { get; set; }
    public DateTime DueDate        { get; set; }

    public decimal  Subtotal       { get; set; }
    public decimal  TaxAmount      { get; set; }
    public decimal  DiscountAmount { get; set; }
    public decimal  GrandTotal     { get; set; }
    /// <summary>Running total of allocated customer payments (§9.4).</summary>
    public decimal  AmountPaid     { get; set; }
    /// <summary>GrandTotal − AmountPaid. Stored, not computed, because it is what aging is indexed on.</summary>
    public decimal  BalanceDue     { get; set; }

    /// <summary>See <see cref="SalesInvoiceStatuses"/>.</summary>
    public string   Status         { get; set; } = SalesInvoiceStatuses.Draft;

    public string   CurrencyCode   { get; set; } = "PKR";
    public string?  Notes          { get; set; }

    public bool      IsActive     { get; set; } = true;
    public bool      IsDelete     { get; set; }
    public int       CreatedBy    { get; set; }
    public DateTime  CreatedDate  { get; set; }
    public int?      ModifiedBy   { get; set; }
    public DateTime? ModifiedDate { get; set; }

    public ICollection<SalesInvoiceLine> Lines { get; set; } = new List<SalesInvoiceLine>();

    /// <summary>The customer payments applied to this invoice (§9.4). Their sum is <see cref="AmountPaid"/>.</summary>
    public ICollection<PaymentAllocation> Allocations { get; set; } = new List<PaymentAllocation>();
}

internal class SalesInvoiceLine : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid UUID           { get; set; }
    public Guid OrganizationId { get; set; }

    public int          SalesInvoiceId { get; set; }
    public SalesInvoice SalesInvoice   { get; set; } = null!;

    public int LineNo { get; set; }

    /// <summary>The sale order line billed. What "invoiced qty ≤ delivered qty" (§9.5) is checked against.</summary>
    public Guid SoLineUuid  { get; set; }
    /// <summary>Bare UUID onto the Inventory variant.</summary>
    public Guid VariantUuid { get; set; }

    public string  Description     { get; set; } = string.Empty;
    public decimal Quantity        { get; set; }
    public decimal UnitPrice       { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal TaxPercent      { get; set; }
    public decimal LineTotal       { get; set; }
}

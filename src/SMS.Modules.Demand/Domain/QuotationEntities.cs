using SMS.Shared.Common;

namespace SMS.Modules.Demand.Domain;

internal class Quotation : ITenantScopedEntity
{
    public int Id { get; set; }
    public Guid UUID { get; set; }
    public Guid TraceId { get; set; }
    public Guid OrganizationId { get; set; }
    public string QuotationNumber { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty; // PR | PO | STANDALONE
    public Guid? SourceId { get; set; }
    public string Status { get; set; } = "DRAFT"; // DRAFT | SENT | AWARDED | CANCELLED
    public DateTime? DueDate { get; set; }
    public string? Notes { get; set; }
    public string? CancellationReason { get; set; }

    // Sealed-bid: VendorResponse pricing (via GetComparisonAsync) and AwardAsync are both
    // blocked until this is set via OpenBidsAsync — bids stay hidden from everyone, including
    // admins, until an explicit "Open Bids" action.
    public DateTime? BidsOpenedAt { get; set; }
    public int?      BidsOpenedBy { get; set; }

    public bool IsActive { get; set; } = true;
    public bool IsDelete { get; set; }
    public int CreatedBy { get; set; }
    public DateTime CreatedDate { get; set; }
    public int? ModifiedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }

    public ICollection<QuotationLine> Lines { get; set; } = new List<QuotationLine>();
    public ICollection<VendorResponse> VendorResponses { get; set; } = new List<VendorResponse>();
    public ICollection<QuotationInvitedSupplier> InvitedSuppliers { get; set; } = new List<QuotationInvitedSupplier>();
}

internal class QuotationLine : ITenantScopedEntity
{
    public int Id { get; set; }
    public Guid UUID { get; set; }
    public Guid OrganizationId { get; set; }
    public int QuotationId { get; set; }
    public int LineNo { get; set; }
    public Guid? SourcePrLineUuid { get; set; } // UUID reference to pr_lines (no FK — cross-module)
    public Guid? SourcePoLineUuid { get; set; } // UUID reference to po_lines (no FK — cross-module)
    public Guid? ProductId { get; set; } // UUID reference to inventory Products (no FK — cross-module)
    public string ItemDescription { get; set; } = string.Empty;
    public string? Specification { get; set; }
    public string? UnitOfMeasure { get; set; }
    public decimal Quantity { get; set; }
    public DateTime? RequiredDate { get; set; }
    public string? BudgetCode { get; set; }

    public Quotation Quotation { get; set; } = null!;
    public ICollection<VendorResponseLine> ResponseLines { get; set; } = new List<VendorResponseLine>();
}

internal class VendorResponse : ITenantScopedEntity
{
    public int Id { get; set; }
    public Guid UUID { get; set; }
    public Guid OrganizationId { get; set; }
    public int QuotationId { get; set; }
    public Guid SupplierId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public string Status { get; set; } = "PENDING"; // PENDING | AWARDED | REJECTED
    public decimal TotalAmount { get; set; }
    public DateTime? ResponseDate { get; set; }
    public string? Notes { get; set; }
    public int CreatedBy { get; set; }
    public DateTime CreatedDate { get; set; }

    public Quotation Quotation { get; set; } = null!;
    public ICollection<VendorResponseLine> Lines { get; set; } = new List<VendorResponseLine>();
}

internal class VendorResponseLine : ITenantScopedEntity
{
    public int Id { get; set; }
    public Guid UUID { get; set; }
    public Guid OrganizationId { get; set; }
    public int VendorResponseId { get; set; }
    public int QuotationLineId { get; set; }
    public decimal NetUnitPrice { get; set; }
    public decimal Quantity { get; set; }
    public decimal LineTotal { get; set; }
    public int? LeadTimeDays { get; set; }
    public string? Notes { get; set; }

    public VendorResponse VendorResponse { get; set; } = null!;
    public QuotationLine QuotationLine { get; set; } = null!;
}

internal class QuotationInvitedSupplier : ITenantScopedEntity
{
    public int Id { get; set; }
    public Guid OrganizationId { get; set; }
    public int QuotationId { get; set; }
    public Guid SupplierId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public DateTime InvitedAt { get; set; }

    public Quotation Quotation { get; set; } = null!;
}

internal class RfqAccessLink : ITenantScopedEntity
{
    public int Id { get; set; }
    public Guid OrganizationId { get; set; }
    public int QuotationId { get; set; }
    public Guid SupplierId { get; set; }
    public int ContactId { get; set; }        // cross-module ref to supplier contact — no FK
    public string TokenHash { get; set; } = string.Empty;  // SHA-256 hex (64 chars), unique index
    public string Status { get; set; } = "PENDING";  // PENDING | ACCESSED | EXPIRED | REVOKED | CONSUMED
    public DateTime GeneratedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? FirstOpenedAt { get; set; }  // set once on first valid open; never overwritten
    public DateTime? ConsumedAt { get; set; }      // set when supplier submits response
    public string? ConsumedIp { get; set; }        // IP address of the submitting browser
    public int? ResponseId { get; set; }           // FK to vendor_responses.id — no navigation needed
    public int AccessCount { get; set; }
    public int CreatedBy { get; set; }

    public string? SupplierEmail         { get; set; }  // supplier company email — dispatch target
    public string? PortalLinkUrl         { get; set; }  // full portal URL with embedded raw token
    public DateTime? EmailSentAt         { get; set; }  // set after successful email dispatch
    public string? ContactMobileNumber   { get; set; }  // verified contact mobile (E.164) — WhatsApp target
    public DateTime? WhatsAppSentAt      { get; set; }  // set once the provider accepted the send request (NOT delivery confirmation)
    public string? WhatsAppProviderMessageId { get; set; }  // Twilio SID — correlates the async status callback back to this link
    public string? WhatsAppStatus        { get; set; }  // QUEUED | SENT | DELIVERED | READ | FAILED | UNDELIVERED — updated by the Twilio status callback webhook
    public DateTime? WhatsAppStatusUpdatedAt { get; set; }

    public Quotation Quotation { get; set; } = null!;
}

// Stub entity for PO module price writeback (still live — read in QuotationRepository for
// repeat-purchase price lookups; verified before scoping, not dead code despite the comment).
internal class PoLine : ITenantScopedEntity
{
    public int Id { get; set; }
    public Guid UUID { get; set; }
    public Guid OrganizationId { get; set; }
    public string ItemDescription { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}

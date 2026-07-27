namespace SMS.Modules.Demand.Models;

// ── Requests ─────────────────────────────────────────────────────────────────

public class CreateQuotationRequest
{
    public string Title { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty; // PR | PO | STANDALONE
    public Guid? SourceId { get; set; }
    public DateTime? DueDate { get; set; }
    public string? Notes { get; set; }
    public List<CreateQuotationLineRequest> Lines { get; set; } = [];
    // Client-generated id so attachments uploaded before save can be linked via the same
    // DocumentId — becomes the Quotation's own UUID on save.
    public Guid? QuotationUuid { get; set; }
}

public class CreateQuotationLineRequest
{
    public Guid? SourcePrLineUuid { get; set; }
    public Guid? SourcePoLineUuid { get; set; }
    public string ItemDescription { get; set; } = string.Empty;
    public string? Specification { get; set; }
    public string? UnitOfMeasure { get; set; }
    public decimal Quantity { get; set; }
    public DateTime? RequiredDate { get; set; }
    public string? BudgetCode { get; set; }
}

public class SendQuotationRequest
{
    public List<InviteSupplierRequest> Suppliers { get; set; } = [];
}

public class InviteSupplierRequest
{
    public Guid SupplierId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
}

public class RecordVendorResponseRequest
{
    public Guid SupplierId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public DateTime? ResponseDate { get; set; }
    public string? Notes { get; set; }
    public List<VendorResponseLineRequest> Lines { get; set; } = [];
}

public class VendorResponseLineRequest
{
    public Guid QuotationLineUuid { get; set; }
    public decimal NetUnitPrice { get; set; }
    public decimal Quantity { get; set; }
    public int? LeadTimeDays { get; set; }
    public string? Notes { get; set; }
}

public class AwardQuotationRequest
{
    public Guid VendorResponseUuid { get; set; }
}

public class SendWithLinkRequest
{
    public List<SupplierContactPair> Suppliers { get; set; } = [];
}

public class SupplierContactPair
{
    public Guid    SupplierId           { get; set; }
    public string  SupplierName         { get; set; } = string.Empty;
    public int     ContactId            { get; set; }
    public string? SupplierEmail        { get; set; }  // Supplier.Email — email dispatch target
    public string? ContactMobileNumber  { get; set; }  // verified contact mobile (E.164) — WhatsApp target
}

public class SendWithLinkResult
{
    public List<GeneratedLinkModel> Links            { get; set; } = [];
    public string?                  EmailWarning    { get; set; }
    public string?                  WhatsAppWarning { get; set; }
}

public class GeneratedLinkModel
{
    public Guid   SupplierId { get; set; }
    public int    ContactId  { get; set; }
    public string LinkUrl    { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
}

public class CancelQuotationRequest
{
    public string Reason { get; set; } = string.Empty;
}

public class PatchQuotationRequest
{
    public string? Title { get; set; }
    public DateTime? DueDate { get; set; }
    public string? Notes { get; set; }
    public List<CreateQuotationLineRequest>? Lines { get; set; }
}

// ── Filters ──────────────────────────────────────────────────────────────────

public class QuotationListFilter
{
    public string? Status { get; set; }
    public string? SourceType { get; set; }
    public string? Search { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

// ── Response models ───────────────────────────────────────────────────────────

public class QuotationListItemModel
{
    public Guid UUID { get; set; }
    public Guid TraceId { get; set; }
    public string QuotationNumber { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public Guid? SourceId { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime? DueDate { get; set; }
    public int ResponseCount { get; set; }
    public DateTime CreatedDate { get; set; }
}

public class QuotationDetailModel
{
    public Guid UUID { get; set; }
    public Guid TraceId { get; set; }
    public string QuotationNumber { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public Guid? SourceId { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime? DueDate { get; set; }
    public string? Notes { get; set; }
    public string? CancellationReason { get; set; }
    public int CreatedBy { get; set; }
    public DateTime CreatedDate { get; set; }
    public int SubmittedResponseCount { get; set; }
    public DateTime? BidsOpenedAt { get; set; }
    public int?      BidsOpenedBy { get; set; }
    public List<QuotationLineModel> Lines { get; set; } = [];
    public List<QuotationInvitedSupplierModel> InvitedSuppliers { get; set; } = [];
}

public class QuotationLineModel
{
    public Guid UUID { get; set; }
    public int LineNo { get; set; }
    public Guid? SourcePrLineUuid { get; set; }
    public Guid? SourcePoLineUuid { get; set; }
    public string ItemDescription { get; set; } = string.Empty;
    public string? Specification { get; set; }
    public string? UnitOfMeasure { get; set; }
    public decimal Quantity { get; set; }
    public DateTime? RequiredDate { get; set; }
    public string? BudgetCode { get; set; }
}

public class QuotationInvitedSupplierModel
{
    public Guid SupplierId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public DateTime InvitedAt { get; set; }
}

public class VendorResponseModel
{
    public Guid UUID { get; set; }
    public Guid SupplierId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public DateTime? ResponseDate { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedDate { get; set; }
    public List<VendorResponseLineModel> Lines { get; set; } = [];
}

// ── Public RFQ portal ─────────────────────────────────────────────────────────

public class RfqPublicPayload
{
    public string QuotationNumber { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime IssueDate { get; set; }
    public DateTime? DueDate { get; set; }
    public string? Notes { get; set; }
    public string? SupplierName { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? FirstOpenedAt { get; set; }
    public List<RfqPublicLineModel> Lines { get; set; } = [];
}

public class RfqPublicLineModel
{
    public Guid LineUuid { get; set; }
    public int LineNo { get; set; }
    public string ItemDescription { get; set; } = string.Empty;
    public string? Specification { get; set; }
    public string? UnitOfMeasure { get; set; }
    public decimal Quantity { get; set; }
    public DateTime? RequiredDate { get; set; }
}

public class RfqPortalResponse
{
    public string Status { get; set; } = string.Empty;  // VALID | CONSUMED | EXPIRED | INVALID
    public RfqPublicPayload? Payload { get; set; }
}

// ── Public RFQ submission ─────────────────────────────────────────────────────

public class RfqSubmitRequest
{
    public List<RfqSubmitLineRequest> Lines { get; set; } = [];
    public string? Notes { get; set; }
    // Client-generated id (see rfq-page.component.ts) so attachments uploaded before
    // submission can be linked to this response via the same DocumentId.
    public Guid? ResponseUuid { get; set; }
}

public class RfqSubmitLineRequest
{
    public Guid LineUuid { get; set; }
    // String types on the wire (form inputs) — parsed and validated by
    // RfqSubmissionService.HandleSubmissionAsync when CanSupply is true. Superseded FSD §5.2's
    // "no validation by design" — the automated RFQ workflow now requires instant validation.
    public string UnitPrice { get; set; } = string.Empty;
    public string DeliveryDays { get; set; } = string.Empty;
    public bool CanSupply { get; set; } = true;
    public string? Remarks { get; set; }
}

public class RfqSubmitResult
{
    public string Status { get; set; } = string.Empty;  // SUBMITTED | CONSUMED | EXPIRED | INVALID | VALIDATION_ERROR
    public Guid? ResponseUuid { get; set; }
    public string? QuotationNumber { get; set; }
    public string? SupplierName { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public List<string>? ValidationErrors { get; set; }
}

public class VendorResponseLineModel
{
    public Guid UUID { get; set; }
    public Guid QuotationLineUuid { get; set; }
    public string ItemDescription { get; set; } = string.Empty;
    public decimal NetUnitPrice { get; set; }
    public decimal Quantity { get; set; }
    public decimal LineTotal { get; set; }
    public int? LeadTimeDays { get; set; }
    public string? Notes { get; set; }
}


public class RfqAccessLinkModel
{
    public int       LinkId         { get; set; }
    public Guid      SupplierId     { get; set; }
    public string    SupplierName   { get; set; } = string.Empty;
    public int       ContactId      { get; set; }
    public string?   SupplierEmail  { get; set; }
    public string    Status         { get; set; } = string.Empty;
    public DateTime  GeneratedAt    { get; set; }
    public DateTime  ExpiresAt      { get; set; }
    public DateTime? EmailSentAt    { get; set; }
    public DateTime? WhatsAppSentAt { get; set; }
    public string?   WhatsAppStatus { get; set; }
    public DateTime? FirstOpenedAt  { get; set; }
    public DateTime? ConsumedAt     { get; set; }
}

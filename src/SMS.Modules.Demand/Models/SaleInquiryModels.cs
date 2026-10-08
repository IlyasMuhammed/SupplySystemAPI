namespace SMS.Modules.Demand.Models;

// A32 C1 — Sale Inquiry request/response shapes. See docs/sales-preorder/API-CONTRACT.md §2.
// Date-only fields (receivedDate, deadlines, delivery dates) are sent and returned as "yyyy-MM-dd"
// (midnight, no UTC shift). All ids exposed are UUIDs; users are int ids.

public class SaleInquiryListFilter
{
    /// <summary>One status code; omit for all.</summary>
    public string?   Status           { get; set; }
    public Guid?     PartnerId        { get; set; }
    public DateTime? ReceivedFrom     { get; set; }
    public DateTime? ReceivedTo       { get; set; }
    public int?      AssignedToUserId { get; set; }
    /// <summary>Inquiry number or customer reference, contains.</summary>
    public string?   Search           { get; set; }
    public int       Page             { get; set; } = 1;
    public int       PageSize         { get; set; } = 20;
}

public class SaleInquiryListItemModel
{
    public Guid      Uuid                 { get; set; }
    public string    InquiryNumber        { get; set; } = string.Empty;
    public Guid      PartnerId            { get; set; }
    public string?   PartnerName          { get; set; }
    public string?   CustomerReference    { get; set; }
    public DateTime  ReceivedDate         { get; set; }
    public DateTime? ResponseDeadline     { get; set; }
    public string    Status               { get; set; } = string.Empty;
    public int?      AssignedToUserId     { get; set; }
    public string?   AssignedToUserName   { get; set; }
    public int       LineCount            { get; set; }
    /// <summary>Lines still PENDING or UNDER_REVIEW — what stands between UNDER_REVIEW and REVIEW_COMPLETE.</summary>
    public int       PendingLineCount     { get; set; }
    public DateTime  CreatedDate          { get; set; }
}

public class SaleInquiryModel
{
    public Guid      Uuid                  { get; set; }
    public Guid      TraceId               { get; set; }
    public string    InquiryNumber         { get; set; } = string.Empty;
    public Guid      PartnerId             { get; set; }
    public string?   PartnerName           { get; set; }
    public string?   CustomerReference     { get; set; }
    public DateTime? CustomerReferenceDate { get; set; }
    public string    Status                { get; set; } = string.Empty;
    public DateTime  ReceivedDate          { get; set; }
    public DateTime? ResponseDeadline      { get; set; }
    public int?      AssignedToUserId      { get; set; }
    public string?   AssignedToUserName    { get; set; }
    public string?   Notes                 { get; set; }
    public string?   DeclineReason         { get; set; }
    /// <summary>A35 D-10/D-14 — the currency the customer asks in (informational; no rate). The quotation inherits it.</summary>
    public Guid?     CurrencyId            { get; set; }
    public string?   CurrencyCode          { get; set; }
    public int       CreatedBy             { get; set; }
    public DateTime  CreatedDate           { get; set; }
    public DateTime? ModifiedDate          { get; set; }
    /// <summary>Status codes this inquiry may move to next via PATCH /status (server-computed, for the buttons).</summary>
    public List<string> AllowedNextStatuses { get; set; } = [];
    /// <summary>False once QUOTED or DECLINED (BR-C1-06): no header or line changes.</summary>
    public bool      IsEditable            { get; set; }
    public List<SaleInquiryLineModel> Lines { get; set; } = [];
    /// <summary>Quotations created from this inquiry (normally 0 or 1).</summary>
    public List<SalesDocumentLinkModel> Quotations { get; set; } = [];
}

public class SaleInquiryLineModel
{
    public Guid      Uuid                       { get; set; }
    public int       LineNumber                 { get; set; }
    public Guid?     ProductUuid                { get; set; }
    public Guid?     VariantUuid                { get; set; }
    /// <summary>Resolved from the catalog on read; null when no variant or unknown.</summary>
    public string?   VariantSku                 { get; set; }
    public string?   VariantName                { get; set; }
    public string    ProductDescription         { get; set; } = string.Empty;
    public decimal   RequestedQuantity          { get; set; }
    public string?   RequestedUomCode           { get; set; }
    public DateTime? RequestedDeliveryDate      { get; set; }
    public string    LineStatus                 { get; set; } = string.Empty;
    public decimal?  CanSupplyQuantity          { get; set; }
    public DateTime? EstimatedDeliveryDate      { get; set; }
    public Guid?     RejectionReasonUuid        { get; set; }
    public string?   RejectionReasonCode        { get; set; }
    public string?   RejectionReasonDescription { get; set; }
    public string?   RejectionNotes             { get; set; }
    public Guid?     AlternativeProductUuid     { get; set; }
    public Guid?     AlternativeVariantUuid     { get; set; }
    public string?   AlternativeVariantSku      { get; set; }
    public string?   AlternativeVariantName     { get; set; }
    public string?   AlternativeNotes           { get; set; }
    public bool      RequiresProcurement        { get; set; }
    public int?      ProcurementLeadDays        { get; set; }
    public int?      ReviewedByUserId           { get; set; }
    public string?   ReviewedByUserName         { get; set; }
    public DateTime? ReviewedAt                 { get; set; }
    public string?   Notes                      { get; set; }

    // A34 D-15 (API-CONTRACT §5.1). The manual date is EstimatedDeliveryDate.
    public int?      CalculatedLeadTimeDays     { get; set; }
    public DateTime? CalculatedDeliveryDate     { get; set; }
    public DateTime? LeadTimeCalculatedAt       { get; set; }
    public DateTime? EffectiveDeliveryDate      { get; set; }
    public string    DeliveryDateSource         { get; set; } = DeliveryDateSources.None;
}

/// <summary>POST /api/sale-inquiries. Created as RECEIVED; lines optional (they can be added after).</summary>
public class CreateSaleInquiryRequest
{
    public Guid      PartnerId             { get; set; }
    public string?   CustomerReference     { get; set; }
    public DateTime? CustomerReferenceDate { get; set; }
    /// <summary>Today when omitted.</summary>
    public DateTime? ReceivedDate          { get; set; }
    public DateTime? ResponseDeadline      { get; set; }
    public int?      AssignedToUserId      { get; set; }
    public string?   Notes                 { get; set; }
    /// <summary>A35 D-14 — the customer's default sale currency when omitted, else the organization's sale base.</summary>
    public Guid?     CurrencyId            { get; set; }
    public List<SaleInquiryLineRequest> Lines { get; set; } = [];
}

/// <summary>PUT /api/sale-inquiries/{uuid} — header only; replaces every field here. Customer cannot change.</summary>
public class UpdateSaleInquiryRequest
{
    public string?   CustomerReference     { get; set; }
    public DateTime? CustomerReferenceDate { get; set; }
    public DateTime  ReceivedDate          { get; set; }
    public DateTime? ResponseDeadline      { get; set; }
    public int?      AssignedToUserId      { get; set; }
    public string?   Notes                 { get; set; }
    /// <summary>A35 — omitted keeps the inquiry's currency.</summary>
    public Guid?     CurrencyId            { get; set; }
}

/// <summary>
/// POST /api/sale-inquiries/{uuid}/lines (and lines[] of a create) — what the customer asked for.
/// Evaluation fields are set with <see cref="UpdateSaleInquiryLineRequest"/>.
/// </summary>
public class SaleInquiryLineRequest
{
    public Guid?     ProductUuid           { get; set; }
    public Guid?     VariantUuid           { get; set; }
    public string    ProductDescription    { get; set; } = string.Empty;
    public decimal   RequestedQuantity     { get; set; }
    public string?   RequestedUomCode      { get; set; }
    public DateTime? RequestedDeliveryDate { get; set; }
    public string?   Notes                 { get; set; }
}

/// <summary>
/// PUT /api/sale-inquiries/{uuid}/lines/{lineUuid} — the request fields plus the evaluation (§3.5). Replaces
/// every field. Rules: CAN_SUPPLY needs estimatedDeliveryDate; PARTIAL needs 0 &lt; canSupplyQuantity &lt;
/// requestedQuantity and estimatedDeliveryDate; CANNOT_SUPPLY needs rejectionReasonUuid (an active reason of
/// this organization). Fields that do not apply to the status are cleared by the server.
/// </summary>
public class UpdateSaleInquiryLineRequest : SaleInquiryLineRequest
{
    /// <summary>PENDING | CAN_SUPPLY | PARTIAL | CANNOT_SUPPLY | UNDER_REVIEW.</summary>
    public string    LineStatus             { get; set; } = "PENDING";
    public decimal?  CanSupplyQuantity      { get; set; }
    public DateTime? EstimatedDeliveryDate  { get; set; }
    public Guid?     RejectionReasonUuid    { get; set; }
    public string?   RejectionNotes         { get; set; }
    public Guid?     AlternativeProductUuid { get; set; }
    public Guid?     AlternativeVariantUuid { get; set; }
    public string?   AlternativeNotes       { get; set; }
    public bool      RequiresProcurement    { get; set; }
    public int?      ProcurementLeadDays    { get; set; }
}

/// <summary>PATCH /api/sale-inquiries/{uuid}/status.</summary>
public class ChangeSaleInquiryStatusRequest
{
    /// <summary>UNDER_REVIEW | REVIEW_COMPLETE | DECLINED. QUOTED is system-only (create-quotation).</summary>
    public string  Status { get; set; } = string.Empty;
    /// <summary>Required for DECLINED; kept as the inquiry's DeclineReason.</summary>
    public string? Reason { get; set; }
}

/// <summary>A linked document in the chain (inquiry → quotation → sale order), for headers and links.</summary>
public class SalesDocumentLinkModel
{
    public Guid    Uuid   { get; set; }
    public string  Number { get; set; } = string.Empty;
    public string  Status { get; set; } = string.Empty;
}

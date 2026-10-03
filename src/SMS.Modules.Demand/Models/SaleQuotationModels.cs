namespace SMS.Modules.Demand.Models;

// A32 C2 — Sale Quotation request/response shapes (seller-side; NOT the buyer-side RFQ QuotationModels).
// See docs/sales-preorder/API-CONTRACT.md §3. Date-only fields are "yyyy-MM-dd", no UTC shift.

public class SaleQuotationListFilter
{
    public string?   Status            { get; set; }
    public Guid?     PartnerId         { get; set; }
    public Guid?     SourceInquiryUuid { get; set; }
    /// <summary>valid_to on or after / on or before.</summary>
    public DateTime? ValidToFrom       { get; set; }
    public DateTime? ValidToTo         { get; set; }
    /// <summary>Quotation number or customer reference, contains.</summary>
    public string?   Search            { get; set; }
    public int       Page              { get; set; } = 1;
    public int       PageSize          { get; set; } = 20;
}

public class SaleQuotationListItemModel
{
    public Guid      Uuid                { get; set; }
    public string    QuotationNumber     { get; set; } = string.Empty;
    public Guid      PartnerId           { get; set; }
    public string?   PartnerName         { get; set; }
    public string?   CustomerReference   { get; set; }
    public Guid?     SourceInquiryUuid   { get; set; }
    public string?   SourceInquiryNumber { get; set; }
    public Guid      CurrencyId          { get; set; }
    public string?   CurrencyCode        { get; set; }
    public DateTime  ValidFrom           { get; set; }
    public DateTime  ValidTo             { get; set; }
    public string    Status              { get; set; } = string.Empty;
    public decimal   GrandTotal          { get; set; }
    public int       LineCount           { get; set; }
    public DateTime? SentAt              { get; set; }
    public DateTime  CreatedDate         { get; set; }
}

public class SaleQuotationModel
{
    public Guid      Uuid                  { get; set; }
    public Guid      TraceId               { get; set; }
    public string    QuotationNumber       { get; set; } = string.Empty;
    public Guid      PartnerId             { get; set; }
    public string?   PartnerName           { get; set; }
    public string?   CustomerReference     { get; set; }
    public DateTime? CustomerReferenceDate { get; set; }
    /// <summary>The inquiry it came from; null when created on its own.</summary>
    public SalesDocumentLinkModel? SourceInquiry { get; set; }
    /// <summary>The sale order it was converted to; null until CONVERTED.</summary>
    public SalesDocumentLinkModel? SaleOrder     { get; set; }
    public Guid      CurrencyId            { get; set; }
    public string?   CurrencyCode          { get; set; }
    public DateTime  ValidFrom             { get; set; }
    public DateTime  ValidTo               { get; set; }
    public string    Status                { get; set; } = string.Empty;
    public string?   PaymentTerms          { get; set; }
    public string?   DeliveryTerms         { get; set; }
    public decimal   Subtotal              { get; set; }
    public decimal   TaxAmount             { get; set; }
    public decimal   DiscountAmount        { get; set; }
    public decimal   GrandTotal            { get; set; }
    public string?   Notes                 { get; set; }
    public string?   InternalNotes         { get; set; }
    public DateTime? SentAt                { get; set; }
    public int?      SentByUserId          { get; set; }
    public string?   SentByUserName        { get; set; }
    public int       CreatedBy             { get; set; }
    public DateTime  CreatedDate           { get; set; }
    public DateTime? ModifiedDate          { get; set; }
    /// <summary>True only in DRAFT (BR-C2-05): header and lines can change.</summary>
    public bool      IsEditable            { get; set; }
    /// <summary>Server-computed, for the buttons: any of SEND, RECORD_RESPONSE, ACCEPT, REJECT, CONVERT, COPY.</summary>
    public List<string> AllowedActions     { get; set; } = [];
    public List<SaleQuotationLineModel> Lines { get; set; } = [];
}

public class SaleQuotationLineModel
{
    public Guid      Uuid                       { get; set; }
    public int       LineNumber                 { get; set; }
    public Guid?     SourceInquiryLineUuid      { get; set; }
    public Guid?     VariantUuid                { get; set; }
    /// <summary>Resolved from the catalog on read.</summary>
    public string?   VariantSku                 { get; set; }
    public string?   VariantName                { get; set; }
    public string    ProductDescription         { get; set; } = string.Empty;
    public decimal   Quantity                   { get; set; }
    public string?   UomCode                    { get; set; }
    public decimal   UnitPrice                  { get; set; }
    public decimal   DiscountPercent            { get; set; }
    public decimal   TaxPercent                 { get; set; }
    public Guid?     TaxCodeUuid                { get; set; }
    public string?   TaxCode                    { get; set; }
    public decimal   TaxAmount                  { get; set; }
    public decimal   LineTotal                  { get; set; }
    public DateTime? PromisedDeliveryDate       { get; set; }
    /// <summary>NORMAL | ALTERNATIVE | REJECTED.</summary>
    public string    LineType                   { get; set; } = string.Empty;
    public Guid?     RejectionReasonUuid        { get; set; }
    public string?   RejectionReasonCode        { get; set; }
    public string?   RejectionReasonDescription { get; set; }
    public string?   RejectionNotes             { get; set; }
    /// <summary>ALTERNATIVE only: the REJECTED line it replaces.</summary>
    public Guid?     AlternativeForLineUuid     { get; set; }
    public int?      AlternativeForLineNumber   { get; set; }
    public string?   AlternativeNotes           { get; set; }
    /// <summary>PENDING | ACCEPTED | REJECTED | COUNTER. Always PENDING on a REJECTED line.</summary>
    public string    CustomerResponse           { get; set; } = string.Empty;
    public DateTime? CustomerResponseDate       { get; set; }
    public string?   CustomerResponseNotes      { get; set; }
    public decimal?  CustomerCounterPrice       { get; set; }
    public string?   Notes                      { get; set; }
}

/// <summary>POST /api/sale-quotations — an independent quotation (no inquiry). Created as DRAFT.</summary>
public class CreateSaleQuotationRequest
{
    public Guid      PartnerId             { get; set; }
    public string?   CustomerReference     { get; set; }
    public DateTime? CustomerReferenceDate { get; set; }
    /// <summary>The organization's base currency when omitted.</summary>
    public Guid?     CurrencyId            { get; set; }
    /// <summary>Today when omitted.</summary>
    public DateTime? ValidFrom             { get; set; }
    /// <summary>Required; on or after validFrom (BR-C2-03).</summary>
    public DateTime  ValidTo               { get; set; }
    public string?   PaymentTerms          { get; set; }
    public string?   DeliveryTerms         { get; set; }
    public string?   Notes                 { get; set; }
    public string?   InternalNotes         { get; set; }
    public List<SaleQuotationLineRequest> Lines { get; set; } = [];
}

/// <summary>PUT /api/sale-quotations/{uuid} — header only, DRAFT only. Replaces every field. Customer cannot change.</summary>
public class UpdateSaleQuotationRequest
{
    public string?   CustomerReference     { get; set; }
    public DateTime? CustomerReferenceDate { get; set; }
    public Guid?     CurrencyId            { get; set; }
    public DateTime  ValidFrom             { get; set; }
    public DateTime  ValidTo               { get; set; }
    public string?   PaymentTerms          { get; set; }
    public string?   DeliveryTerms         { get; set; }
    public string?   Notes                 { get; set; }
    public string?   InternalNotes         { get; set; }
}

/// <summary>
/// One line of a create, POST …/lines, or PUT …/lines/{lineUuid} (DRAFT only). Rules (QUO):
/// NORMAL/ALTERNATIVE need variantUuid and quantity &gt; 0; REJECTED needs rejectionReasonUuid and carries no
/// price; ALTERNATIVE needs alternativeForLineUuid (an existing line) or alternativeForLineNumber (a line of the
/// same request/quotation) pointing to a REJECTED line of the same quotation (BR-C2-06).
/// unitPrice omitted = resolved by the sale price waterfall (IPricingService) for the customer at validFrom,
/// converted to the quotation currency; given = taken as quoted. Tax: taxCodeUuid wins (its rate is
/// snapshotted), else taxPercent 0–100 with at most 2 decimals — the SaleOrderLine rules.
/// </summary>
public class SaleQuotationLineRequest
{
    /// <summary>NORMAL (default) | ALTERNATIVE | REJECTED.</summary>
    public string    LineType                 { get; set; } = "NORMAL";
    public Guid?     VariantUuid              { get; set; }
    /// <summary>Defaults to the variant's display name when blank.</summary>
    public string?   ProductDescription       { get; set; }
    public decimal   Quantity                 { get; set; }
    public string?   UomCode                  { get; set; }
    public decimal?  UnitPrice                { get; set; }
    public decimal   DiscountPercent          { get; set; }
    public decimal   TaxPercent               { get; set; }
    public Guid?     TaxCodeUuid              { get; set; }
    public DateTime? PromisedDeliveryDate     { get; set; }
    public Guid?     RejectionReasonUuid      { get; set; }
    public string?   RejectionNotes           { get; set; }
    public Guid?     AlternativeForLineUuid   { get; set; }
    public int?      AlternativeForLineNumber { get; set; }
    public string?   AlternativeNotes         { get; set; }
    public string?   Notes                    { get; set; }
}

/// <summary>PATCH /api/sale-quotations/{uuid}/lines/{lineUuid}/customer-response — SENT only, not on REJECTED lines.</summary>
public class RecordCustomerResponseRequest
{
    /// <summary>PENDING | ACCEPTED | REJECTED | COUNTER.</summary>
    public string    Response           { get; set; } = string.Empty;
    /// <summary>Today when omitted.</summary>
    public DateTime? ResponseDate       { get; set; }
    public string?   Notes              { get; set; }
    /// <summary>Required &gt; 0 for COUNTER (BR-C2-09).</summary>
    public decimal?  CounterPrice       { get; set; }
    /// <summary>
    /// With Response = ACCEPTED on a line currently COUNTER: the seller accepts the customer's counter price —
    /// the line's unitPrice becomes customerCounterPrice and totals are recomputed (§4.5). The only price change
    /// allowed after DRAFT.
    /// </summary>
    public bool      AcceptCounterPrice { get; set; }
}

/// <summary>POST /api/sale-quotations/{uuid}/reject.</summary>
public class RejectSaleQuotationRequest
{
    public string? Reason { get; set; }
}

/// <summary>
/// POST /api/sale-inquiries/{uuid}/create-quotation — the inquiry must be REVIEW_COMPLETE; it becomes QUOTED.
/// Lines are pre-populated from the evaluation (see API-CONTRACT §2.4).
/// </summary>
public class CreateSaleQuotationFromInquiryRequest
{
    public Guid?     CurrencyId    { get; set; }
    public DateTime? ValidFrom     { get; set; }
    public DateTime  ValidTo       { get; set; }
    public string?   PaymentTerms  { get; set; }
    public string?   DeliveryTerms { get; set; }
    public string?   Notes         { get; set; }
    public string?   InternalNotes { get; set; }
}

/// <summary>
/// POST /api/sale-quotations/{uuid}/convert-to-order — ACCEPTED only. The sale order is a DRAFT with the
/// accepted lines at their quoted prices; the order fields here are what a direct order would ask for.
/// </summary>
public class ConvertSaleQuotationToOrderRequest
{
    /// <summary>Today when omitted.</summary>
    public DateTime? OrderDate              { get; set; }
    public DateTime? ExpectedDeliveryDate   { get; set; }
    /// <summary>SHIP | SELF_PICKUP; the organization's default when omitted.</summary>
    public string?   DeliveryMode           { get; set; }
    /// <summary>Required for SHIP — a logistics address uuid.</summary>
    public Guid?     ShippingAddressId      { get; set; }
    public int?      IntimationDepartmentId { get; set; }
    public string?   Notes                  { get; set; }
    public string?   CustomerPoReference    { get; set; }
    public DateTime? CustomerPoDate         { get; set; }
}

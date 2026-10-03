using SMS.Shared.Common;

namespace SMS.Modules.Demand.Domain;

// A32 (Sales Pre-Order Pipeline) §3.2/§3.3/§4.2/§4.3/§7.2, on this repo's conventions (Part A):
// int Id + Guid UUID, Guid OrganizationId (ITenantScopedEntity), PascalCase columns, statuses as strings
// via EnumCode<T>. Cross-module references are unenforced scalar Guids — PartnerId → BusinessPartners.UUID,
// ProductUuid/VariantUuid → Inventory, CurrencyId → Lookups — and users are int ids. References inside
// Demand (inquiry ↔ quotation ↔ sale order, lines → rejection reason) are real int FKs.
// Attachments are NOT tables here: they live in the generic WorkflowEngine attachment store under the
// interface codes SALE_INQUIRY / SALE_QUOTATION / CUSTOMER_PO / SALE_ORDER (documentId = the document's UUID).
// Dates the spec types as DATE (received, deadlines, validity, delivery dates) are DateTime mapped to
// SQL `date` — the frontend sends them with its date-only helpers, no UTC shift.

/// <summary>§3.2 — a customer's request, evaluated line by line before anything is quoted.</summary>
internal class SaleInquiry : ITenantScopedEntity
{
    public int       Id                    { get; set; }
    public Guid      UUID                  { get; set; } = Guid.NewGuid();
    public Guid      TraceId               { get; set; }
    public Guid      OrganizationId        { get; set; }
    /// <summary>INQ-YYYY-NNNNN, unique per organization (IDocumentNumberGenerator, prefix "INQ").</summary>
    public string    InquiryNumber         { get; set; } = string.Empty;
    /// <summary>The customer — BusinessPartners.UUID; must be a partner flagged IsCustomer (BR-C1-01).</summary>
    public Guid      PartnerId             { get; set; }
    public string?   CustomerReference     { get; set; }
    public DateTime? CustomerReferenceDate { get; set; }
    public string    Status                { get; set; } = EnumCode<SaleInquiryStatus>.Of(SaleInquiryStatus.Received);
    public DateTime  ReceivedDate          { get; set; }
    public DateTime? ResponseDeadline      { get; set; }
    public int?      AssignedToUserId      { get; set; }
    public string?   Notes                 { get; set; }
    /// <summary>Why the organization declined the whole inquiry (set with the DECLINED transition).</summary>
    public string?   DeclineReason         { get; set; }
    public int       CreatedBy             { get; set; }
    public DateTime  CreatedDate           { get; set; } = DateTime.UtcNow;
    public int?      ModifiedBy            { get; set; }
    public DateTime? ModifiedDate          { get; set; }

    public ICollection<SaleInquiryLine> Lines { get; set; } = new List<SaleInquiryLine>();
}

/// <summary>§3.3 — one requested item and the organization's evaluation of it.</summary>
internal class SaleInquiryLine : ITenantScopedEntity
{
    public int       Id                     { get; set; }
    public Guid      UUID                   { get; set; } = Guid.NewGuid();
    public Guid      OrganizationId         { get; set; }
    public int       SaleInquiryId          { get; set; }
    /// <summary>1-based, unique within the inquiry.</summary>
    public int       LineNumber             { get; set; }
    /// <summary>Null when the customer describes something not in the catalog.</summary>
    public Guid?     ProductUuid            { get; set; }
    public Guid?     VariantUuid            { get; set; }
    /// <summary>The customer's own words — always kept, even when a product is identified.</summary>
    public string    ProductDescription     { get; set; } = string.Empty;
    public decimal   RequestedQuantity      { get; set; }
    /// <summary>A unit-of-measure code (the catalog keeps UoM as a code, e.g. "KG"), as the customer asked.</summary>
    public string?   RequestedUomCode       { get; set; }
    public DateTime? RequestedDeliveryDate  { get; set; }
    public string    LineStatus             { get; set; } = EnumCode<SaleInquiryLineStatus>.Of(SaleInquiryLineStatus.Pending);
    public decimal?  CanSupplyQuantity      { get; set; }
    public DateTime? EstimatedDeliveryDate  { get; set; }
    public int?      RejectionReasonId      { get; set; }
    public string?   RejectionNotes         { get; set; }
    public Guid?     AlternativeProductUuid { get; set; }
    public Guid?     AlternativeVariantUuid { get; set; }
    public string?   AlternativeNotes       { get; set; }
    public bool      RequiresProcurement    { get; set; }
    public int?      ProcurementLeadDays    { get; set; }
    public int?      ReviewedByUserId       { get; set; }
    public DateTime? ReviewedAt             { get; set; }
    public string?   Notes                  { get; set; }
    public DateTime  CreatedDate            { get; set; } = DateTime.UtcNow;
    public DateTime? ModifiedDate           { get; set; }

    public SaleInquiry      SaleInquiry     { get; set; } = null!;
    public RejectionReason? RejectionReason { get; set; }
}

/// <summary>§4.2 — the organization's priced proposal to a customer. Not the buyer-side RFQ "Quotation".</summary>
internal class SaleQuotation : ITenantScopedEntity
{
    public int       Id                    { get; set; }
    public Guid      UUID                  { get; set; } = Guid.NewGuid();
    public Guid      TraceId               { get; set; }
    public Guid      OrganizationId        { get; set; }
    /// <summary>SQ-YYYY-NNNNN, unique per organization (IDocumentNumberGenerator, prefix "SQ").</summary>
    public string    QuotationNumber       { get; set; } = string.Empty;
    public Guid      PartnerId             { get; set; }
    public string?   CustomerReference     { get; set; }
    public DateTime? CustomerReferenceDate { get; set; }
    /// <summary>The inquiry it was created from; null when created on its own.</summary>
    public int?      SourceInquiryId       { get; set; }
    /// <summary>Lookups currency UUID, like SaleOrder.CurrencyId (the spec's currency_code).</summary>
    public Guid      CurrencyId            { get; set; }
    public DateTime  ValidFrom             { get; set; }
    public DateTime  ValidTo               { get; set; }
    public string    Status                { get; set; } = EnumCode<SaleQuotationStatus>.Of(SaleQuotationStatus.Draft);
    public string?   PaymentTerms          { get; set; }
    public string?   DeliveryTerms         { get; set; }
    public decimal   Subtotal              { get; set; }
    public decimal   TaxAmount             { get; set; }
    public decimal   DiscountAmount        { get; set; }
    public decimal   GrandTotal            { get; set; }
    /// <summary>Printed on the quotation.</summary>
    public string?   Notes                 { get; set; }
    /// <summary>Never sent to the customer.</summary>
    public string?   InternalNotes         { get; set; }
    public DateTime? SentAt                { get; set; }
    public int?      SentByUserId          { get; set; }
    public int       CreatedBy             { get; set; }
    public DateTime  CreatedDate           { get; set; } = DateTime.UtcNow;
    public int?      ModifiedBy            { get; set; }
    public DateTime? ModifiedDate          { get; set; }

    public SaleInquiry? SourceInquiry { get; set; }
    public ICollection<SaleQuotationLine> Lines { get; set; } = new List<SaleQuotationLine>();
}

/// <summary>§4.3 — one quoted, rejected or alternative line.</summary>
internal class SaleQuotationLine : ITenantScopedEntity
{
    public int       Id                    { get; set; }
    public Guid      UUID                  { get; set; } = Guid.NewGuid();
    public Guid      OrganizationId        { get; set; }
    public int       SaleQuotationId       { get; set; }
    public int       LineNumber            { get; set; }
    public int?      SourceInquiryLineId   { get; set; }
    /// <summary>
    /// Required on NORMAL and ALTERNATIVE lines (service rule). Nullable — a deviation from the spec's NOT NULL —
    /// so a REJECTED line can carry an inquiry line the customer described in free text (no catalog item).
    /// </summary>
    public Guid?     VariantUuid           { get; set; }
    public string    ProductDescription    { get; set; } = string.Empty;
    public decimal   Quantity              { get; set; }
    /// <summary>Unit-of-measure code snapshot (the variant's, unless entered).</summary>
    public string?   UomCode               { get; set; }
    public decimal   UnitPrice             { get; set; }
    public decimal   DiscountPercent       { get; set; }
    /// <summary>The rate taxed at — the tax code's rate when one is picked (SAP alignment), else as entered.</summary>
    public decimal   TaxPercent            { get; set; }
    public Guid?     TaxCodeUuid           { get; set; }
    public string?   TaxCode               { get; set; }
    public decimal   TaxAmount             { get; set; }
    /// <summary>qty × price × (1 − disc%) × (1 + tax%), rounded to 2 — the SaleOrderLine formula.</summary>
    public decimal   LineTotal             { get; set; }
    public DateTime? PromisedDeliveryDate  { get; set; }
    public string    LineType              { get; set; } = EnumCode<SaleQuotationLineType>.Of(SaleQuotationLineType.Normal);
    public int?      RejectionReasonId     { get; set; }
    public string?   RejectionNotes        { get; set; }
    /// <summary>ALTERNATIVE only: the REJECTED line of the same quotation this replaces.</summary>
    public int?      AlternativeForLineId  { get; set; }
    public string?   AlternativeNotes      { get; set; }
    public string    CustomerResponse      { get; set; } = EnumCode<SaleQuotationCustomerResponse>.Of(SaleQuotationCustomerResponse.Pending);
    public DateTime? CustomerResponseDate  { get; set; }
    public string?   CustomerResponseNotes { get; set; }
    public decimal?  CustomerCounterPrice  { get; set; }
    public string?   Notes                 { get; set; }
    public DateTime  CreatedDate           { get; set; } = DateTime.UtcNow;
    public DateTime? ModifiedDate          { get; set; }

    public SaleQuotation      SaleQuotation      { get; set; } = null!;
    public SaleInquiryLine?   SourceInquiryLine  { get; set; }
    public RejectionReason?   RejectionReason    { get; set; }
    public SaleQuotationLine? AlternativeForLine { get; set; }
}

/// <summary>
/// §7.2 — why a line is declined. Demand-owned (Part A: Lookups does not migrate at startup). Seeded per
/// organization with the ten §7.3 codes (IsSystem = true): those can be renamed and deactivated, never deleted.
/// </summary>
internal class RejectionReason : ITenantScopedEntity
{
    public int       Id             { get; set; }
    public Guid      UUID           { get; set; } = Guid.NewGuid();
    public Guid      OrganizationId { get; set; }
    /// <summary>Up to 10 characters, upper case, unique per organization (BR-C5-01).</summary>
    public string    Code           { get; set; } = string.Empty;
    public string    Description    { get; set; } = string.Empty;
    public bool      IsActive       { get; set; } = true;
    /// <summary>One of the ten seeded codes — cannot be deleted (BR-C5-02).</summary>
    public bool      IsSystem       { get; set; }
    public int       DisplayOrder   { get; set; }
    public int?      CreatedBy      { get; set; }
    public DateTime  CreatedDate    { get; set; } = DateTime.UtcNow;
    public int?      ModifiedBy     { get; set; }
    public DateTime? ModifiedDate   { get; set; }
}

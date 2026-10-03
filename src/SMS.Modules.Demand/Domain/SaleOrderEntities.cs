using SMS.Shared.Common;

namespace SMS.Modules.Demand.Domain;

// A29-P3-05 §4.1. PartnerId, SelectedSupplierId and ShippingAddressId are unenforced scalar Guid
// FKs -> other modules' UUID columns (BusinessPartners, logistics.Addresses) — Demand doesn't share
// a DbContext with Suppliers or Logistics, same convention as PurchaseOrder.SupplierId already
// uses. LinkedPoId is same-module (PurchaseOrder lives in this DbContext too) but stays a plain
// scalar int rather than a real FK/relationship: a PO can be linked to a line well after both rows
// already exist (§13's trace_id linkage, and back-to-back POs created via a background job), and a
// hard FK would force insert/commit ordering this workflow doesn't actually have.
internal class SaleOrder : ITenantScopedEntity
{
    public int       Id                     { get; set; }
    public Guid      UUID                   { get; set; } = Guid.NewGuid();
    public Guid      TraceId                { get; set; }
    public Guid      OrganizationId         { get; set; }
    public string    SoNumber               { get; set; } = string.Empty;
    public Guid      PartnerId              { get; set; }
    public DateTime  OrderDate              { get; set; }
    public DateTime? ExpectedDeliveryDate   { get; set; }
    public Guid      CurrencyId             { get; set; }
    public decimal   Subtotal               { get; set; }
    public decimal   TaxAmount              { get; set; }
    public decimal   DiscountAmount         { get; set; }
    public decimal   GrandTotal             { get; set; }
    public string    Status                 { get; set; } = EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Draft);
    public bool      RequiresShipment       { get; set; } = true;
    public string    DeliveryMode           { get; set; } = EnumCode<Domain.DeliveryMode>.Of(Domain.DeliveryMode.Ship);
    public Guid?     ShippingAddressId      { get; set; }
    public int?      IntimationDepartmentId { get; set; }
    public string?   Notes                  { get; set; }
    public bool      IsDeleted              { get; set; }
    public int       CreatedBy              { get; set; }
    public DateTime  CreatedDate            { get; set; } = DateTime.UtcNow;
    public int?      ModifiedBy             { get; set; }
    public DateTime? ModifiedDate           { get; set; }

    // A32 C3 (PD-01/PD-03). Every order from before A32 is MANUAL (column default). The two source ids are
    // real FKs inside Demand; no navigation properties, deliberately — SaleOrderModel's SourceQuotation /
    // SourceInquiry are {uuid, number, status} links resolved by the service, and AutoMapper would otherwise
    // try to map the entities onto them.
    public string    SourceType               { get; set; } = EnumCode<SaleOrderSourceType>.Of(SaleOrderSourceType.Manual);
    public int?      SourceQuotationId        { get; set; }
    public int?      SourceInquiryId          { get; set; }
    public string?   CustomerPoReference      { get; set; }
    public DateTime? CustomerPoDate           { get; set; }
    /// <summary>The CUSTOMER_PO file in the generic attachment store (WorkflowEngine document_attachments.UUID).</summary>
    public Guid?     CustomerPoAttachmentUuid { get; set; }

    public ICollection<SaleOrderLine> Lines { get; set; } = new List<SaleOrderLine>();
}

// A29-P3-05 §4.2. FulfillmentMode is nullable: §4.3 decides it at CONFIRM, not at line creation —
// a DRAFT line legitimately has none yet.
internal class SaleOrderLine : ITenantScopedEntity
{
    public int      Id                    { get; set; }
    public Guid     UUID                  { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId        { get; set; }
    public int      SaleOrderId           { get; set; }
    public Guid     VariantUuid           { get; set; }
    public decimal  Quantity              { get; set; }
    public decimal  UnitPrice             { get; set; }
    public decimal  DiscountPercent       { get; set; }
    public decimal  TaxPercent            { get; set; }
    /// <summary>SAP alignment — the tax code picked for this line (Finance master, via ITaxCodeLookup). TaxPercent keeps its rate as a snapshot. Null on lines from before tax codes.</summary>
    public Guid?    TaxCodeUuid           { get; set; }
    /// <summary>The code's text as it was when picked — copied onto the sales invoice line.</summary>
    public string?  TaxCode               { get; set; }
    public decimal  LineTotal             { get; set; }
    public decimal  FulfilledQty          { get; set; }
    public decimal  InvoicedQty           { get; set; }
    public string?  FulfillmentMode       { get; set; }
    public decimal? AvailableQtyAtConfirm { get; set; }
    public decimal? DeficitQty            { get; set; }
    public int?     LinkedPoId            { get; set; }
    public Guid?    SelectedSupplierId    { get; set; }
    public decimal? Margin                { get; set; }
    public decimal? MarginPercent         { get; set; }
    public string   Status                { get; set; } = EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.Open);
    public string?  Notes                 { get; set; }

    public SaleOrder SaleOrder { get; set; } = null!;
}

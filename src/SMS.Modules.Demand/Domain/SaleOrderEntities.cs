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

    /// <summary>
    /// A33 D-12 / REV-01: set (UTC) in the confirm's own commit when deliveries are to be created automatically, and
    /// cleared once a delivery creator call for the order returns without throwing (confirm, the sweep, the recovery
    /// button), or when the order is cancelled. The sweep retries only orders where it is still set, so a delivery a user
    /// cancelled on purpose is never re-created behind their back.
    /// </summary>
    public DateTime? DeliveryCreationPendingSince { get; set; }

    /// <summary>
    /// A34 D-17: set (UTC) in the confirm's own commit when the order has make-to-order lines, cleared once production
    /// creation ran (confirm, the sweep, the "Create production orders" button), or when the order is cancelled. The
    /// production sweep retries only orders where it is still set.
    /// </summary>
    public DateTime? ProductionCreationPendingSince { get; set; }

    // A35 D-10/D-11/D-12 — the rate locked at CONFIRMED (units of the sale base per 1 unit of CurrencyId; 1 when the
    // order is in the base) and the order's amounts in the base. All null while DRAFT (null rate = not locked yet), and
    // never recalculated once set (BR-C5-06). GrandTotalBase = Σ line LineTotalBase (line totals include tax here).
    public decimal?  ExchangeRate       { get; set; }
    public Guid?     BaseCurrencyId     { get; set; }
    public DateTime? RateLockedAt       { get; set; }
    public decimal?  SubtotalBase       { get; set; }
    public decimal?  TaxAmountBase      { get; set; }
    public decimal?  DiscountAmountBase { get; set; }
    public decimal?  GrandTotalBase     { get; set; }

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

    // A33 C3: the fulfillment route, an unenforced Guid to logistics.fulfillment_routes.UUID (no cross-context FK).
    // While the order is DRAFT this is the salesperson's override (null = inherit from the variant, then the org
    // default). At confirm it is overwritten with the route the line resolved to, and RouteSource records which tier
    // gave it (D-16). That snapshot is what the deliveries, and any later remainder, follow.
    public Guid?    FulfillmentRouteUuid  { get; set; }
    /// <summary>The route's code at confirm (codes are immutable, L-4), kept so the line still names it if the route is later deleted.</summary>
    public string?  FulfillmentRouteCode  { get; set; }
    /// <summary>LINE_OVERRIDE | VARIANT | ORG_DEFAULT, written at confirm (D-16); null while DRAFT and on lines confirmed before A33.</summary>
    public string?  RouteSource           { get; set; }

    // A34 D-15: lead time. The calculated date comes from ILeadTimeCalculator (POST …/lines/{line}/lead-time); the
    // manual date is the salesperson's (PUT …/delivery-date, or copied from the quotation's promised date). The
    // effective date (manual ?? calculated) is derived in the models, never stored. All dates are date-only.
    public int?      CalculatedLeadTimeDays { get; set; }
    public DateTime? CalculatedDeliveryDate { get; set; }
    public DateTime? LeadTimeCalculatedAt   { get; set; }
    public DateTime? ManualDeliveryDate     { get; set; }

    /// <summary>A34: the confirmed route's category (STOCK | MANUFACTURE), snapshotted at confirm with the route (D-16 A33); null while DRAFT.</summary>
    public string?  FulfillmentRouteCategory { get; set; }

    /// <summary>A34 D-21: planned − accepted when a make-to-order production order yields less than the line; null otherwise.</summary>
    public decimal? ProductionShortfallQty  { get; set; }

    /// <summary>A35 D-10: UnitPrice / LineTotal × the order's locked rate, at the base currency's decimals; null until CONFIRMED.</summary>
    public decimal? UnitPriceBase { get; set; }
    public decimal? LineTotalBase { get; set; }

    public SaleOrder SaleOrder { get; set; } = null!;
}

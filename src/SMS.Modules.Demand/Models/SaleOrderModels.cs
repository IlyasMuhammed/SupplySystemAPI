namespace SMS.Modules.Demand.Models;

// A29-P3-05 §4.1/§4.2 — read-shape DTOs, for AutoMapper's entity projection and this task's
// FluentValidation rules.
// A29-P3-06 — write-shape DTOs for ISaleOrderService's create/update.

public class CreateSaleOrderLineRequest
{
    public Guid    VariantUuid     { get; set; }
    public decimal Quantity        { get; set; }
    public decimal DiscountPercent { get; set; }
    /// <summary>
    /// Used only when <see cref="TaxCodeUuid"/> is not given (a caller from before tax codes). With a
    /// code, the code's own rate is what the line is taxed at and this value is ignored.
    /// </summary>
    public decimal TaxPercent      { get; set; }
    /// <summary>
    /// SAP alignment (S-3) — the Finance tax code for this line: it must exist, be active and be usable on
    /// sales. Its rate is copied onto the line as TaxPercent. Send it back on every update of a draft —
    /// a draft's lines are rebuilt from the request, so a line sent without it loses its code.
    /// </summary>
    public Guid?   TaxCodeUuid     { get; set; }
    /// <summary>
    /// A33 BR-C3-01: the line's fulfillment route override, an active route of the caller's organization; null means
    /// inherit (the variant's route, then the organization default). DRAFT only, and like the tax code it must be sent
    /// back on every update, because a draft's lines are rebuilt from the request.
    /// </summary>
    public Guid?   FulfillmentRouteUuid { get; set; }
}

public class CreateSaleOrderRequest
{
    public Guid      PartnerId              { get; set; }
    public DateTime? OrderDate              { get; set; }
    public DateTime? ExpectedDeliveryDate   { get; set; }
    // Falls back to the org's base currency when omitted, matching CreateVariantSupplierRequest
    // and CreatePricingRuleRequest's existing convention.
    public Guid?     CurrencyId             { get; set; }
    public string    DeliveryMode           { get; set; } = string.Empty;
    public Guid?     ShippingAddressId      { get; set; }
    public int?      IntimationDepartmentId { get; set; }
    public string?   Notes                  { get; set; }
    // A32 C3 (PD-03). Only MANUAL (the default when omitted) is accepted here: FROM_QUOTATION orders are made
    // by POST /api/sale-quotations/{uuid}/convert-to-order, PORTAL and INTER_TENANT are not built yet (A33).
    public string?   SourceType             { get; set; }
    /// <summary>The customer's PO number, free text up to 50 (BR-C3-04). A duplicate within the org is a warning, not an error.</summary>
    public string?   CustomerPoReference    { get; set; }
    public DateTime? CustomerPoDate         { get; set; }
    public List<CreateSaleOrderLineRequest> Lines { get; set; } = [];
}

/// <summary>
/// A32 PD-04 — what <see cref="Services.ISaleOrderService.CreateFromQuotationAsync"/> takes: the quotation's
/// accepted lines at their quoted price. Partner, currency and the inquiry link come from the quotation itself
/// (looked up by <see cref="SourceQuotationUuid"/> in the caller's organization), never from the caller.
/// </summary>
public class CreateSaleOrderFromQuotationCommand
{
    public Guid      SourceQuotationUuid    { get; set; }
    public DateTime? OrderDate              { get; set; }
    public DateTime? ExpectedDeliveryDate   { get; set; }
    public string?   DeliveryMode           { get; set; }
    public Guid?     ShippingAddressId      { get; set; }
    public int?      IntimationDepartmentId { get; set; }
    public string?   Notes                  { get; set; }
    public string?   CustomerPoReference    { get; set; }
    public DateTime? CustomerPoDate         { get; set; }
    public List<QuotedSaleOrderLine> Lines  { get; set; } = [];
}

/// <summary>One accepted quotation line. Its price is taken as quoted — not re-resolved — and its tax code re-checked.</summary>
public class QuotedSaleOrderLine
{
    public Guid    VariantUuid     { get; set; }
    public decimal Quantity        { get; set; }
    public decimal UnitPrice       { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal TaxPercent      { get; set; }
    public Guid?   TaxCodeUuid     { get; set; }
}

/// <summary>
/// A32 PD-05 — PUT /api/sale-orders/{uuid}/customer-po. Allowed on any order not CANCELLED or CLOSED (a
/// customer's PO often arrives after the order is confirmed). Replaces all three fields.
/// </summary>
public class UpdateSaleOrderCustomerPoRequest
{
    public string?   CustomerPoReference      { get; set; }
    public DateTime? CustomerPoDate           { get; set; }
    /// <summary>
    /// A file already uploaded through api/attachments with interfaceCode CUSTOMER_PO and documentId = this order's
    /// uuid; null clears the link (the file itself stays in the attachment list).
    /// </summary>
    public Guid?     CustomerPoAttachmentUuid { get; set; }
}

/// <summary>A32 BR-C3-05 — another order of this organization already carrying the same customer PO reference.</summary>
public class CustomerPoDuplicateModel
{
    public Guid     Uuid        { get; set; }
    public string   SoNumber    { get; set; } = string.Empty;
    public Guid     PartnerId   { get; set; }
    public string   Status      { get; set; } = string.Empty;
    public DateTime OrderDate   { get; set; }
}

// PartnerId is deliberately not editable — reassigning the customer on an existing order is a
// different action than amending it, and nothing in this task names that flow.
public class UpdateSaleOrderRequest
{
    public DateTime? ExpectedDeliveryDate   { get; set; }
    public Guid?     CurrencyId             { get; set; }
    public string    DeliveryMode           { get; set; } = string.Empty;
    public Guid?     ShippingAddressId      { get; set; }
    public int?      IntimationDepartmentId { get; set; }
    public string?   Notes                  { get; set; }
    // A32 C3 — editable here while DRAFT; after that through PUT …/customer-po.
    public string?   CustomerPoReference    { get; set; }
    public DateTime? CustomerPoDate         { get; set; }
    public List<CreateSaleOrderLineRequest> Lines { get; set; } = [];
}

/// <summary>What a new sale order starts as, so a form can open on it and offer only what is allowed.</summary>
public class SaleOrderDefaultsModel
{
    /// <summary>SHIP or SELF_PICKUP.</summary>
    public string DeliveryMode      { get; set; } = string.Empty;
    /// <summary>False when every order has to be shipped.</summary>
    public bool   SelfPickupEnabled { get; set; }
}

public class SaleOrderListFilter
{
    public string? Status        { get; set; }
    public Guid?   PartnerId     { get; set; }
    public DateTime? OrderDateFrom { get; set; }
    public DateTime? OrderDateTo   { get; set; }
    public string? Search        { get; set; } // SoNumber or (A32) CustomerPoReference, contains
    /// <summary>A32 — MANUAL | FROM_QUOTATION | PORTAL | INTER_TENANT.</summary>
    public string? SourceType    { get; set; }
    public int     Page          { get; set; } = 1;
    public int     PageSize      { get; set; } = 20;
}

public class SaleOrderModel
{
    public Guid      Uuid                   { get; set; }
    public Guid      TraceId                { get; set; }
    public string    SoNumber               { get; set; } = string.Empty;
    public Guid      PartnerId              { get; set; }
    public DateTime  OrderDate              { get; set; }
    public DateTime? ExpectedDeliveryDate   { get; set; }
    public Guid      CurrencyId             { get; set; }
    public decimal   Subtotal               { get; set; }
    public decimal   TaxAmount              { get; set; }
    public decimal   DiscountAmount         { get; set; }
    public decimal   GrandTotal             { get; set; }
    public string    Status                 { get; set; } = string.Empty;
    public bool      RequiresShipment       { get; set; }
    public string    DeliveryMode           { get; set; } = string.Empty;
    public Guid?     ShippingAddressId      { get; set; }
    public int?      IntimationDepartmentId { get; set; }
    public string?   Notes                  { get; set; }
    public DateTime  CreatedDate            { get; set; }
    public DateTime? ModifiedDate           { get; set; }

    // A32 C3. SourceType is MANUAL for every order from before A32.
    public string    SourceType               { get; set; } = "MANUAL";
    public SalesDocumentLinkModel? SourceQuotation { get; set; }
    public SalesDocumentLinkModel? SourceInquiry   { get; set; }
    public string?   CustomerPoReference      { get; set; }
    public DateTime? CustomerPoDate           { get; set; }
    /// <summary>The CUSTOMER_PO attachment (api/attachments) linked as the customer's PO document.</summary>
    public Guid?     CustomerPoAttachmentUuid { get; set; }

    public List<SaleOrderLineModel> Lines { get; set; } = [];

    // A33 (detail only). False when the organization has no Logistics module (D-11): no route, no gate, no auto-create.
    public bool      RoutesEnabled            { get; set; }
    /// <summary>Why the order cannot be confirmed yet; always empty unless DRAFT.</summary>
    public List<ConfirmBlockerModel> ConfirmBlockers { get; set; } = [];
}

/// <summary>A33: one reason a DRAFT order cannot be confirmed (API-CONTRACT.md §5).</summary>
public class ConfirmBlockerModel
{
    public Guid?   LineUuid   { get; set; }
    /// <summary>1-based position of the line by Id ("Line N"); null for an order-level blocker.</summary>
    public int?    LineNumber { get; set; }
    /// <summary>ROUTE_MISSING | ROUTE_INACTIVE | ROUTE_UNKNOWN | SHIPPING_ADDRESS_REQUIRED | SELF_PICKUP_DISABLED.</summary>
    public string  Code       { get; set; } = string.Empty;
    public string  Message    { get; set; } = string.Empty;
}

public static class ConfirmBlockerCodes
{
    public const string RouteMissing            = "ROUTE_MISSING";
    public const string RouteInactive           = "ROUTE_INACTIVE";
    public const string RouteUnknown            = "ROUTE_UNKNOWN";
    public const string ShippingAddressRequired = "SHIPPING_ADDRESS_REQUIRED";
    public const string SelfPickupDisabled      = "SELF_PICKUP_DISABLED";
}

// ── A33 delivery preview (BR-C3-05), confirm and cancel results ─────────────

public class SaleOrderDeliveryPreviewRequest
{
    /// <summary>The order being edited, when it has been saved; must be the caller's own.</summary>
    public Guid?   SaleOrderUuid     { get; set; }
    /// <summary>SHIP | SELF_PICKUP; picks the organization default of that class (D-4). Blank = the organization's default mode.</summary>
    public string? DeliveryMode      { get; set; }
    public Guid?   ShippingAddressId { get; set; }
    public List<SaleOrderDeliveryPreviewLineRequest> Lines { get; set; } = [];
}

/// <summary>A33 — PUT api/sale-orders/{uuid}/lines/{lineUuid}/fulfillment-route. Null = inherit (variant, then org default).</summary>
public class UpdateSaleOrderLineRouteRequest
{
    public Guid? FulfillmentRouteUuid { get; set; }
}

public class SaleOrderDeliveryPreviewLineRequest
{
    public Guid    VariantUuid          { get; set; }
    public decimal Quantity             { get; set; }
    public Guid?   FulfillmentRouteUuid { get; set; }
}

public class SaleOrderDeliveryPreviewModel
{
    public bool RoutesEnabled { get; set; }
    /// <summary>No blockers.</summary>
    public bool CanConfirm    { get; set; }
    /// <summary>= Groups.Count.</summary>
    public int  DeliveryCount { get; set; }
    public List<DeliveryPreviewLineModel>  Lines    { get; set; } = [];
    public List<DeliveryPreviewGroupModel> Groups   { get; set; } = [];
    public List<ConfirmBlockerModel>       Blockers { get; set; } = [];
}

public class DeliveryPreviewLineModel
{
    public Guid?   LineUuid             { get; set; }
    public int     LineNumber           { get; set; }
    public Guid    VariantUuid          { get; set; }
    public string? ItemDescription      { get; set; }
    public decimal Quantity             { get; set; }
    public Guid?   FulfillmentRouteUuid { get; set; }
    public Guid?   EffectiveRouteUuid   { get; set; }
    public string? EffectiveRouteCode   { get; set; }
    public string? EffectiveRouteName   { get; set; }
    public List<string> EffectiveRouteSteps { get; set; } = [];
    public string  RouteSource          { get; set; } = "NONE";
    public string? RouteBlocker         { get; set; }
}

/// <summary>One delivery confirming would create (route × ship-from warehouse; the warehouse only once stock is held).</summary>
public class DeliveryPreviewGroupModel
{
    public Guid    RouteUuid        { get; set; }
    public string  RouteCode        { get; set; } = string.Empty;
    public string  RouteName        { get; set; } = string.Empty;
    public List<string> Steps       { get; set; } = [];
    public string  StepsText        { get; set; } = string.Empty;
    public bool    RequiresShipping { get; set; }
    /// <summary>SHIP | SELF_PICKUP, from the route (D-4).</summary>
    public string  DeliveryMode     { get; set; } = string.Empty;
    public Guid?   WarehouseUuid    { get; set; }
    public string? WarehouseName    { get; set; }
    public List<int> LineNumbers    { get; set; } = [];
}

public class CreatedSaleOrderDeliveryModel
{
    public Guid    DeliveryUuid          { get; set; }
    public string  DeliveryNumber        { get; set; } = string.Empty;
    public Guid    RouteUuid             { get; set; }
    public string  RouteCode             { get; set; } = string.Empty;
    public string  DeliveryMode          { get; set; } = string.Empty;
    public Guid?   ShipFromWarehouseUuid { get; set; }
    public int     LineCount             { get; set; }
}

public class SkippedSaleOrderLineModel
{
    public Guid   SoLineUuid { get; set; }
    public string Reason     { get; set; } = string.Empty;
}

/// <summary>A33: what confirming did. Additive over the old bare message.</summary>
public class SaleOrderConfirmResultModel
{
    public string Status { get; set; } = string.Empty;
    public List<CreatedSaleOrderDeliveryModel> Deliveries   { get; set; } = [];
    public List<SkippedSaleOrderLineModel>     SkippedLines { get; set; } = [];
    /// <summary>The order is confirmed but its deliveries were not created: show DeliveryMessage and the recovery button.</summary>
    public bool    DeliveryCreationFailed { get; set; }
    public string? DeliveryMessage        { get; set; }
}

public class SaleOrderDeliveryRefModel
{
    public Guid   DeliveryUuid   { get; set; }
    public string DeliveryNumber { get; set; } = string.Empty;
    public string Status         { get; set; } = string.Empty;
}

/// <summary>A33 D-15: deliveries not yet goods-issued were cancelled; issued ones stay and need a manual reversal.</summary>
public class SaleOrderCancelResultModel
{
    public List<SaleOrderDeliveryRefModel> CancelledDeliveries { get; set; } = [];
    public List<SaleOrderDeliveryRefModel> IssuedDeliveries    { get; set; } = [];
}

public class SaleOrderLineModel
{
    public Guid     Uuid                  { get; set; }
    public Guid     VariantUuid           { get; set; }
    // A29-P6-08 — what the line is, for a screen: a sale order line stores only the variant id.
    // Resolved on read through IProductVariantResolver; null when the variant is unknown here.
    public string?  VariantSku            { get; set; }
    public string?  VariantName           { get; set; }
    public string?  ItemDescription       { get; set; }
    public string?  UnitOfMeasure         { get; set; }
    public decimal  Quantity              { get; set; }
    public decimal  UnitPrice             { get; set; }
    public decimal  DiscountPercent       { get; set; }
    /// <summary>The rate the line is taxed at — its tax code's rate when it was picked, else as entered.</summary>
    public decimal  TaxPercent            { get; set; }
    /// <summary>The tax code picked for the line; null on a line with no code (entered before tax codes, or by percentage).</summary>
    public Guid?    TaxCodeUuid           { get; set; }
    /// <summary>The code's text as it was when picked, e.g. "GST17".</summary>
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
    public string   Status                { get; set; } = string.Empty;
    public string?  Notes                 { get; set; }

    // A32 C4 — computed at read time from the stock ledger, never stored (detail only; 0/"RED" on list rows).
    /// <summary>Stock the order itself holds for this line right now (ACTIVE SALES_ORDER reservations) — what Release frees.</summary>
    public decimal  ReservedQty           { get; set; }
    /// <summary>What Reserve could still hold: quantity − fulfilled − reserved − already held by the order's deliveries, never below 0.</summary>
    public decimal  ReservableQty         { get; set; }
    /// <summary>GREEN | BLUE | YELLOW | RED | GREY (§6.3).</summary>
    public string   DeliveryIndicator     { get; set; } = "RED";

    // A33 C3 (detail only): live while DRAFT, the confirm-time snapshot afterwards (D-16).
    /// <summary>The line's override while DRAFT (null = inherit); the route it was confirmed with afterwards.</summary>
    public Guid?    FulfillmentRouteUuid  { get; set; }
    public Guid?    EffectiveRouteUuid    { get; set; }
    public string?  EffectiveRouteCode    { get; set; }
    public string?  EffectiveRouteName    { get; set; }
    public List<string> EffectiveRouteSteps { get; set; } = [];
    /// <summary>LINE_OVERRIDE | VARIANT | ORG_DEFAULT | NONE (DROP_SHIP lines: NONE with no blocker, D-5).</summary>
    public string   RouteSource           { get; set; } = "NONE";
    /// <summary>A <see cref="ConfirmBlockerCodes"/> value when this line blocks confirmation.</summary>
    public string?  RouteBlocker          { get; set; }
}

// A29-P3-07 §4.5's /availability preview — what §4.3's confirm-time check would see right now,
// without reserving or changing anything. Best-single-warehouse figure, matching
// IStockReservationService.GetAvailableAsync's own semantics.
public class SaleOrderLineAvailabilityModel
{
    public Guid     VariantUuid { get; set; }
    public decimal  OrderedQty  { get; set; }
    public decimal  AvailableQty { get; set; }
    public decimal  DeficitQty  { get; set; }
    public Guid?    WarehouseUuid { get; set; }
    public string?  WarehouseName { get; set; }
}

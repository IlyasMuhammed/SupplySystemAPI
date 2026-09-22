namespace SMS.Modules.Demand.Models;

// A29-P3-05 §4.1/§4.2 — read-shape DTOs, for AutoMapper's entity projection and this task's
// FluentValidation rules.
// A29-P3-06 — write-shape DTOs for ISaleOrderService's create/update.

public class CreateSaleOrderLineRequest
{
    public Guid    VariantUuid     { get; set; }
    public decimal Quantity        { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal TaxPercent      { get; set; }
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
    public List<CreateSaleOrderLineRequest> Lines { get; set; } = [];
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
    public string? Search        { get; set; } // SoNumber, contains
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

    public List<SaleOrderLineModel> Lines { get; set; } = [];
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
    public decimal  TaxPercent            { get; set; }
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

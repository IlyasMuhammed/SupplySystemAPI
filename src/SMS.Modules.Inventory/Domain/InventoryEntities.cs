using SMS.Shared.Common;

namespace SMS.Modules.Inventory.Domain;

internal class ProductCategory : ITenantScopedEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public Guid OrganizationId { get; set; }

    public ICollection<ProductSubCategory> SubCategories { get; set; } = new List<ProductSubCategory>();
    public ICollection<Product> Products { get; set; } = new List<Product>();
    public ICollection<CategoryAttribute> CategoryAttributes { get; set; } = new List<CategoryAttribute>();
}

internal class ProductSubCategory : ITenantScopedEntity
{
    public int Id { get; set; }
    public int CategoryId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public Guid OrganizationId { get; set; }

    public ProductCategory Category { get; set; } = null!;
    public ICollection<Product> Products { get; set; } = new List<Product>();
}

internal class Product : ITenantScopedEntity
{
    public int Id { get; set; }
    public Guid Uuid { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string Sku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ShortName { get; set; }
    public string? Description { get; set; }
    public int? CategoryId { get; set; }
    public int? SubCategoryId { get; set; }
    public string? Brand { get; set; }
    public string? UomCode { get; set; }
    // Physical attributes
    public decimal? WeightKg { get; set; }
    public string? Dimensions { get; set; }
    public int? ShelfLifeDays { get; set; }
    // Tracking flags
    public bool IsBatchTracked      { get; set; }
    public bool IsSerialTracked     { get; set; }
    public bool IsDirectConsumption { get; set; }
    // Stock parameters
    public decimal? ReorderPoint { get; set; }
    public decimal? ReorderQty { get; set; }
    public decimal? MinStockLevel { get; set; }
    public decimal? MaxStockLevel { get; set; }
    public int? LeadTimeDays { get; set; }
    // Supplier
    public int? PreferredSupplierId { get; set; }
    // Meta
    public string Status { get; set; } = "ACTIVE";
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
    public string? ImageUrl { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedDate { get; set; }
    public int CreatedBy { get; set; }

    public ProductCategory? Category { get; set; }
    public ProductSubCategory? SubCategory { get; set; }
    public ICollection<ProductVariant> Variants { get; set; } = new List<ProductVariant>();
}

// FSD Addendum 26 (PV-001) — the actual purchasable/stockable unit. Product is now a parent
// container (family); every variant carries its own SKU, barcode, and pricing. A product with no
// real variants (e.g. Cement) still gets exactly one auto-created is_default=true variant, so
// every downstream module can always transact against a variant_id, never a bare product_id.
internal class ProductVariant : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     Uuid           { get; set; } = Guid.NewGuid();
    public int      ProductId      { get; set; }
    public Guid     OrganizationId { get; set; }
    public string   Sku            { get; set; } = string.Empty;
    public string   VariantName    { get; set; } = string.Empty;
    public string?  Barcode        { get; set; }
    public decimal  PurchasePrice  { get; set; }
    public decimal? SellingPrice   { get; set; }
    public decimal? LastPurchasePrice { get; set; }
    public decimal? WeightKg       { get; set; }
    public string?  Dimensions     { get; set; }
    public bool     IsDefault      { get; set; }
    public bool     IsActive       { get; set; } = true;

    // Which channels/documents this variant may be used from. Independent flags, not a single
    // type: a variant can serve several at once (e.g. sold at retail and consumed on a production
    // order). Unchecked by default — a variant claims a channel only once someone has verified it
    // belongs there, never automatically for the whole existing catalog when this was added.
    public bool     IsAvailableForRetail     { get; set; }
    public bool     IsAvailableForPos        { get; set; }
    public bool     IsAvailableForMirMiv     { get; set; }
    public bool     IsAvailableForProduction { get; set; }
    public bool     IsAvailableForServices   { get; set; }

    public decimal? ReorderPoint   { get; set; }
    public int?     SortOrder      { get; set; }
    public DateTime CreatedDate    { get; set; } = DateTime.UtcNow;
    public int      CreatedBy      { get; set; }

    // A29-P2-01 §2.1 — the vendor to preselect on a new PO line for this variant. Unenforced
    // scalar FK -> suppliers.BusinessPartners.UUID (IsVendor must be true), same cross-module
    // convention as VariantSupplier.SupplierId below: Inventory and Suppliers don't share a
    // DbContext, so this is validated in the service layer, not a physical FK.
    public Guid?    DefaultSupplierId { get; set; }
    public int?     LeadTimeDays      { get; set; }

    public Product Product { get; set; } = null!;
    public ICollection<VariantAttributeValue> AttributeValues { get; set; } = new List<VariantAttributeValue>();
    // PV-005 — stock is tracked per variant per warehouse, not per parent product.
    public ICollection<InventoryItem> InventoryItems { get; set; } = new List<InventoryItem>();
}

// RC-001 (FSD Addendum 28) — a supplier's quoted rate for one variant, with validity dates and
// discount tiers. SupplierId is an unenforced scalar FK -> Suppliers.Supplier.UUID (Inventory and
// Suppliers don't share a DbContext, same convention as Supplier.PreferredCurrency -> Lookups).
internal class VariantSupplier : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     Uuid           { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId { get; set; }
    public int      VariantId      { get; set; }
    public Guid     SupplierId     { get; set; }

    public decimal  VendorUnitCost { get; set; }
    public int?     LeadTimeDays   { get; set; }
    public bool     IsActive       { get; set; } = true;
    // RC-002 — the supplier's own part/catalog number for this item (distinct from our SKU).
    public string?  VendorPartNo   { get; set; }
    // RC-002 — at most one preferred supplier per variant, enforced in
    // VariantSupplierService.SetPreferredAsync (clears every other row for the same VariantId).
    public bool     IsPreferred    { get; set; }

    // ── RC-001's 9 new fields ────────────────────────────────────────────────
    public DateTime  EffectiveFrom   { get; set; } = DateTime.UtcNow.Date;
    public DateTime? EffectiveTo     { get; set; }
    public Guid      CurrencyId      { get; set; }
    public decimal?  MinOrderValue   { get; set; }
    // RC-004 — minimum order QUANTITY, distinct from MinOrderValue (a monetary threshold).
    public int?      MinOrderQty     { get; set; }
    // JSON array of {qtyFrom, qtyTo, discountPct} — mirrors AttributeDefinition.DropdownOptions'
    // nvarchar(max)-JSON-column convention.
    public string?   DiscountTiers   { get; set; }
    public string?   QuotationRef    { get; set; }
    public string?   Notes           { get; set; }
    public DateTime? LastReviewedAt  { get; set; }
    public int?      LastReviewedBy  { get; set; }
    // RC-007 — set once the daily expiry sweep has sent its one-time "Rate Expiring" notification
    // for this row, so the job never re-notifies the same expiring rate on a later run.
    public DateTime? ExpiryNotifiedAt { get; set; }

    public int       CreatedBy    { get; set; }
    public DateTime  CreatedDate  { get; set; } = DateTime.UtcNow;
    public int?      ModifiedBy   { get; set; }
    public DateTime? ModifiedDate { get; set; }

    public ProductVariant Variant { get; set; } = null!;
}

// A29-P2-02 §2.2 — a unit price for a variant, optionally scoped to one partner (PartnerId null =
// an org-wide list price) and to a quantity band. Several rules can be active for the same variant
// at once (different PriceType, partner, quantity band, or date range); which one wins for a given
// sale/purchase is a later pricing-resolution task's job, not this table's. PartnerId is an
// unenforced scalar FK -> suppliers.BusinessPartners.UUID, same cross-module convention as
// VariantSupplier.SupplierId above.
internal class PricingRule : ITenantScopedEntity
{
    public int       Id             { get; set; }
    public Guid      Uuid           { get; set; } = Guid.NewGuid();
    public Guid      OrganizationId { get; set; }
    public int       VariantId      { get; set; }
    public Guid?     PartnerId      { get; set; }
    public string    PriceType      { get; set; } = PricingRuleType.Selling;
    public decimal?  MinQty         { get; set; }
    public decimal?  MaxQty         { get; set; }
    public decimal   UnitPrice      { get; set; }
    public Guid      CurrencyId     { get; set; }
    public DateTime  EffectiveFrom  { get; set; } = DateTime.UtcNow.Date;
    public DateTime? EffectiveTo    { get; set; }
    public bool      IsActive       { get; set; } = true;

    public int       CreatedBy      { get; set; }
    public DateTime  CreatedDate    { get; set; } = DateTime.UtcNow;

    public ProductVariant Variant { get; set; } = null!;
}

// The fixed set §2.2 names for PricingRule.PriceType — a plain string discriminator with no
// derived behaviour (unlike PartnerType's flag-derived combinations), so it follows
// ReservationSourceType's convention rather than the Code-attribute enum one.
internal static class PricingRuleType
{
    public const string Selling     = "SELLING";
    public const string Cost        = "COST";
    public const string Promotional = "PROMOTIONAL";
    public const string Contract    = "CONTRACT";
}

// RC-001 — automatic field-level audit trail for VariantSupplier, written to the SAME DbContext
// as the rate row it describes so one SaveChangesAsync() commits both atomically.
internal class SupplierRateHistory : ITenantScopedEntity
{
    public int       Id                { get; set; }
    public Guid      OrganizationId    { get; set; }
    public int       VariantSupplierId { get; set; }
    public string    FieldChanged      { get; set; } = string.Empty;
    public string?   OldValue          { get; set; }
    public string?   NewValue          { get; set; }
    public string?   ChangeReason      { get; set; }
    public int       ChangedBy         { get; set; }
    public DateTime  ChangedAt         { get; set; } = DateTime.UtcNow;

    // RC-005 — set only on rows a bulk-adjust confirm creates (never on ordinary single-row edits,
    // never on an undo's own reversal rows), so undo can find exactly what it created via one query.
    // Modeled as a navigation (not just a scalar FK) so EF Core fixes up the newly-generated
    // BulkRateOperation.Id onto these rows within the SAME SaveChangesAsync() call — the bulk op
    // row and its history rows are inserted together, id and all, no second round trip needed.
    public int?      BulkOperationId   { get; set; }
    public BulkRateOperation? BulkOperation { get; set; }

    public VariantSupplier VariantSupplier { get; set; } = null!;
}

// RC-005 — logs one bulk rate-adjustment operation (percentage or fixed-amount, applied to N
// VariantSupplier rows at once), for the "undo within 24 hours" feature. Same DbContext as
// VariantSupplier/SupplierRateHistory so confirm/undo each commit atomically in one SaveChanges.
internal class BulkRateOperation : ITenantScopedEntity
{
    public int      Id                { get; set; }
    public Guid     Uuid              { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId    { get; set; }
    public string   Method            { get; set; } = string.Empty; // PERCENTAGE | FIXED
    public decimal  Value             { get; set; }
    public int      AffectedCount     { get; set; }
    public decimal  TotalImpactAmount { get; set; }
    public string   ChangeReason      { get; set; } = string.Empty;
    public int      PerformedBy       { get; set; }
    public DateTime PerformedAt       { get; set; } = DateTime.UtcNow;
    public bool     IsUndone          { get; set; }
}

// FSD Addendum 26 (PV-002) — RAM/CPU/Color/Size/... are never columns; every attribute a
// category needs is metadata defined here once per org, then linked to whichever categories
// use it via CategoryAttribute, and stored per-variant via VariantAttributeValue. Adding a new
// attribute for a new industry is an Admin action, not a schema change.
internal class AttributeDefinition : ITenantScopedEntity
{
    public int      Id              { get; set; }
    public Guid     Uuid            { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId  { get; set; }
    public string   AttributeName   { get; set; } = string.Empty;  // internal name, e.g. "cpu"
    public string   DisplayName     { get; set; } = string.Empty;  // user-visible label, e.g. "CPU"
    // TEXT | NUMBER | DECIMAL | DATE | BOOLEAN | DROPDOWN | MULTI_SELECT
    public string   DataType        { get; set; } = string.Empty;
    // TEXTBOX | NUMBERBOX | DATEPICKER | TOGGLE | DROPDOWN | MULTI_SELECT | TEXTAREA
    public string   ControlType     { get; set; } = string.Empty;
    // JSON array of allowed values — required when DataType is DROPDOWN/MULTI_SELECT.
    public string?  DropdownOptions { get; set; }
    public string?  DefaultValue    { get; set; }
    public string?  ValidationRegex { get; set; }
    public bool     IsRequired      { get; set; }
    public bool     IsSearchable    { get; set; }
    public bool     IsFilterable    { get; set; }
    public int      SortOrder       { get; set; }
    public bool     IsActive        { get; set; } = true;

    public ICollection<CategoryAttribute> CategoryAttributes { get; set; } = new List<CategoryAttribute>();
    public ICollection<VariantAttributeValue> VariantValues { get; set; } = new List<VariantAttributeValue>();
}

// Links an AttributeDefinition to a ProductCategory — which categories show which attributes on
// the dynamic form, with a per-category is_required override and its own display order.
internal class CategoryAttribute : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public int  CategoryId     { get; set; }
    public int  AttributeId    { get; set; }
    public Guid OrganizationId { get; set; }
    // Overrides AttributeDefinition.IsRequired for this specific category (e.g. Color is
    // optional globally but required for Garments).
    public bool IsRequired     { get; set; }
    public int  DisplayOrder   { get; set; }

    public ProductCategory      Category  { get; set; } = null!;
    public AttributeDefinition  Attribute { get; set; } = null!;
}

// The actual value a specific variant has for a specific attribute — always stored as text;
// the application layer parses/validates against AttributeDefinition.DataType.
internal class VariantAttributeValue : ITenantScopedEntity
{
    public int    Id             { get; set; }
    public int    VariantId      { get; set; }
    public int    AttributeId    { get; set; }
    public Guid   OrganizationId { get; set; }
    public string Value          { get; set; } = string.Empty;

    public ProductVariant       Variant   { get; set; } = null!;
    public AttributeDefinition  Attribute { get; set; } = null!;
}

// FSD §8 (PV-006) — denormalised, one row per variant, kept in sync by
// IProductSearchIndexService via Hangfire whenever a variant or its attribute values change.
// SearchableText is the concatenation of product name + variant name + sku + barcode + every
// searchable attribute value, and carries the SQL Server Full-Text index used by product search.
internal class ProductSearchIndex : ITenantScopedEntity
{
    public int     Id             { get; set; }
    public int     VariantId      { get; set; }
    public int     ProductId      { get; set; }
    public Guid    OrganizationId { get; set; }
    public string  ProductName    { get; set; } = string.Empty;
    public string  ProductCode    { get; set; } = string.Empty;  // parent product's own SKU
    public string  Sku            { get; set; } = string.Empty;  // variant SKU
    public string? Barcode        { get; set; }
    public string  VariantName    { get; set; } = string.Empty;
    public string? CategoryName   { get; set; }
    public string? Brand          { get; set; }
    public string  SearchableText { get; set; } = string.Empty;
    // Not in the FSD's literal column list, but required to keep RebuildAllAsync's "active
    // variants only" contract enforceable at query time without re-joining ProductVariants on
    // every search request.
    public bool     IsActive      { get; set; } = true;
    public DateTime UpdatedDate   { get; set; } = DateTime.UtcNow;

    public ProductVariant Variant { get; set; } = null!;
    public Product        Product { get; set; } = null!;
}

internal class Warehouse : ITenantScopedEntity
{
    public int Id { get; set; }
    public Guid Uuid { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? Country { get; set; }
    public string? ContactName { get; set; }
    public string? ContactPhone { get; set; }
    public string? GoogleMapsUrl { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public int CreatedBy { get; set; }

    public ICollection<Zone> Zones { get; set; } = new List<Zone>();
    public ICollection<InventoryItem> InventoryItems { get; set; } = new List<InventoryItem>();
}

internal class Zone : ITenantScopedEntity
{
    public int Id { get; set; }
    public int WarehouseId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid OrganizationId { get; set; }

    public Warehouse Warehouse { get; set; } = null!;
    public ICollection<Bin> Bins { get; set; } = new List<Bin>();
    public ICollection<Rack> Racks { get; set; } = new List<Rack>();
}

internal class Rack : ITenantScopedEntity
{
    public int Id { get; set; }
    public int ZoneId { get; set; }
    public string RackCode { get; set; } = string.Empty;
    public string? RackName { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid OrganizationId { get; set; }

    public Zone Zone { get; set; } = null!;
    public ICollection<Shelf> Shelves { get; set; } = new List<Shelf>();
    public ICollection<Bin> Bins { get; set; } = new List<Bin>();
}

internal class Shelf : ITenantScopedEntity
{
    public int Id { get; set; }
    public int RackId { get; set; }
    public string ShelfCode { get; set; } = string.Empty;
    public string? ShelfLevel { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid OrganizationId { get; set; }

    public Rack Rack { get; set; } = null!;
    public ICollection<Bin> Bins { get; set; } = new List<Bin>();
}

internal class Bin : ITenantScopedEntity
{
    public int Id { get; set; }
    public int ZoneId { get; set; }
    public int? RackId { get; set; }
    public int? ShelfId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid OrganizationId { get; set; }

    public Zone Zone { get; set; } = null!;
    public Rack? Rack { get; set; }
    public Shelf? Shelf { get; set; }
    public ICollection<InventoryItem> InventoryItems { get; set; } = new List<InventoryItem>();
}

// PV-005 — stock is now tracked per variant per warehouse, not per (parent) product. Every
// row keys off ProductVariant, never Product directly.
internal class InventoryItem : ITenantScopedEntity
{
    public int Id { get; set; }
    public Guid Uuid { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public int VariantId { get; set; }
    public int WarehouseId { get; set; }
    public int? ZoneId { get; set; }
    public int? BinId { get; set; }
    // Quantities
    public decimal QtyOnHand { get; set; }
    public decimal QtyReserved { get; set; }
    // QtyAvailable = QtyOnHand - QtyReserved — computed, never stored
    public decimal QtyOnOrder { get; set; }
    // Batch / serial / expiry tracking
    public string? BatchNumber { get; set; }
    public string? SerialNumber { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public DateTime? LastCountDate { get; set; }
    // Costing
    public string? ValuationMethod { get; set; }  // FIFO | LIFO | WAVG
    public decimal? UnitCost { get; set; }
    // TotalValue = QtyOnHand * UnitCost — computed, never stored
    // Legacy
    public decimal? ReorderPoint { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    public ProductVariant Variant { get; set; } = null!;
    public Warehouse Warehouse { get; set; } = null!;
    public Zone? Zone { get; set; }
    public Bin? Bin { get; set; }
    public ICollection<StockAdjustment> Adjustments { get; set; } = new List<StockAdjustment>();
}

internal class InventoryLedgerEntry : ITenantScopedEntity
{
    public Guid LedgerId { get; set; }
    public Guid OrganizationId { get; set; }
    public int VariantId { get; set; }
    public int WarehouseId { get; set; }
    public DateTime TransactionDate { get; set; }
    public string TransactionType { get; set; } = string.Empty;  // GRN_RECEIPT | STOCK_ADJUSTMENT | RETURN_DISPATCH
    public string ReferenceType { get; set; } = string.Empty;    // GRN | ADJUSTMENT | SRO
    public Guid ReferenceId { get; set; }
    public string ReferenceNumber { get; set; } = string.Empty;
    public decimal? QuantityIn { get; set; }
    public decimal? QuantityOut { get; set; }
    public decimal BalanceAfter { get; set; }
    public decimal UnitCost { get; set; }
    public decimal TransactionValue { get; set; }
    public string? Notes { get; set; }
    public int CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }

    public ProductVariant Variant { get; set; } = null!;
    public Warehouse Warehouse { get; set; } = null!;
}

internal class StockAdjustment : ITenantScopedEntity
{
    public int Id { get; set; }
    public Guid Uuid { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string? AdjNumber { get; set; }       // ADJ-YYYY-NNNNN
    public int InventoryItemId { get; set; }
    // Denormalised for easy querying (set at creation time from InventoryItem)
    public int VariantId { get; set; }
    public int WarehouseId { get; set; }
    // Adjustment type: Write-off | Damage | Count | Transfer
    public string? AdjType { get; set; }
    // Reason: Damage | Expiry | Theft | Count Variance | Other
    public string? Reason { get; set; }
    public string? ReferenceDoc { get; set; }
    public decimal QtyBefore { get; set; }
    public decimal QtyAdjusted { get; set; }
    public decimal QtyAfter { get; set; }
    public decimal? UnitCost { get; set; }
    public string? Notes { get; set; }
    public string Status { get; set; } = "PENDING_APPROVAL";
    // PENDING_APPROVAL | AUTO_APPROVED | APPROVED | REJECTED
    public string? RejectionReason { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public int CreatedBy { get; set; }
    public int? ReviewedBy { get; set; }
    public DateTime? ReviewedDate { get; set; }

    public InventoryItem InventoryItem { get; set; } = null!;
}

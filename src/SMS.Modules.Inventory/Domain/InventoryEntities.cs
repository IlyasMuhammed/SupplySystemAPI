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
    public ICollection<InventoryItem> InventoryItems { get; set; } = new List<InventoryItem>();
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
    public decimal? ReorderPoint   { get; set; }
    public int?     SortOrder      { get; set; }
    public DateTime CreatedDate    { get; set; } = DateTime.UtcNow;
    public int      CreatedBy      { get; set; }

    public Product Product { get; set; } = null!;
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

internal class InventoryItem : ITenantScopedEntity
{
    public int Id { get; set; }
    public Guid Uuid { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public int ProductId { get; set; }
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

    public Product Product { get; set; } = null!;
    public Warehouse Warehouse { get; set; } = null!;
    public Zone? Zone { get; set; }
    public Bin? Bin { get; set; }
    public ICollection<StockAdjustment> Adjustments { get; set; } = new List<StockAdjustment>();
}

internal class InventoryLedgerEntry : ITenantScopedEntity
{
    public Guid LedgerId { get; set; }
    public Guid OrganizationId { get; set; }
    public int ProductId { get; set; }
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

    public Product Product { get; set; } = null!;
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
    public int ProductId { get; set; }
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

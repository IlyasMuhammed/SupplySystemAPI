namespace SMS.Modules.Inventory.Models;

// ── Category ──────────────────────────────────────────────────────────────────

public class CategoryModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedDate { get; set; }
    public List<SubCategoryModel> SubCategories { get; set; } = new();
}

public class SubCategoryModel
{
    public int Id { get; set; }
    public int CategoryId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; }
}

public class CreateCategoryRequest
{
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public class CreateSubCategoryRequest
{
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public class UpdateCategoryRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
}

public class CategoryDeleteResult
{
    public bool Deleted { get; set; }
    public int ReferencedProductCount { get; set; }
    public int ReferencedSubCategoryCount { get; set; }
}

public class UpdateSubCategoryRequest
{
    public string Name { get; set; } = string.Empty;
    public int CategoryId { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
}

public class SubCategoryDeleteResult
{
    public bool Deleted { get; set; }
    public int ReferencedProductCount { get; set; }
}

public class SubCategoryListFilter
{
    public int? CategoryId { get; set; }
    public string? Search { get; set; }
    public bool? IsActive { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

public class SubCategoryListDto
{
    public int SubCategoryId { get; set; }
    public string SubCategoryCode { get; set; } = string.Empty;
    public string SubCategoryName { get; set; } = string.Empty;
    public int ParentCategoryId { get; set; }
    public string ParentCategoryName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public int ProductCount { get; set; }
}

// ── Product list / detail ─────────────────────────────────────────────────────

public class ProductListItemModel
{
    public int Id { get; set; }
    public Guid Uuid { get; set; }
    public string Sku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ShortName { get; set; }
    public string? Brand { get; set; }
    public int? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public int? SubCategoryId { get; set; }
    public string? SubCategoryName { get; set; }
    public string? UomCode { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool IsBatchTracked { get; set; }
    public bool IsSerialTracked { get; set; }
    public DateTime CreatedDate { get; set; }
    public int VariantCount { get; set; }
    // Convenience rollup for pickers elsewhere in the app (PO/PR/MIR/SRO line entry, stock
    // adjustments) that need a price to pre-fill as soon as a product is selected — sourced from
    // the product's default variant, since Product itself no longer carries its own price (PV-001).
    public decimal? DefaultVariantPurchasePrice { get; set; }
    // Carried on the list projection too (not just ProductDetailModel) so the product list page
    // and every product/variant picker (PO/PR/Quotation/MIR line entry) can show a thumbnail
    // without a second round trip per row.
    public string? ImageUrl { get; set; }
}

public class ProductDetailModel : ProductListItemModel
{
    public string? Description { get; set; }
    public decimal? WeightKg { get; set; }
    public string? Dimensions { get; set; }
    public int? ShelfLifeDays { get; set; }
    public decimal? ReorderPoint { get; set; }
    public decimal? ReorderQty { get; set; }
    public decimal? MinStockLevel { get; set; }
    public decimal? MaxStockLevel { get; set; }
    public int? LeadTimeDays { get; set; }
    public int? PreferredSupplierId { get; set; }
    public string? Notes { get; set; }
    public DateTime? UpdatedDate { get; set; }
    public int CreatedBy { get; set; }
    public List<ProductVariantModel> Variants { get; set; } = [];
}

// ── Product Variants (FSD Addendum 26 / PV-001) ────────────────────────────────

public class ProductVariantModel
{
    public int Id { get; set; }
    public Guid Uuid { get; set; }
    public string Sku { get; set; } = string.Empty;
    public string VariantName { get; set; } = string.Empty;
    public string? Barcode { get; set; }
    public decimal PurchasePrice { get; set; }
    public decimal? SellingPrice { get; set; }
    public decimal? LastPurchasePrice { get; set; }
    public decimal? WeightKg { get; set; }
    public string? Dimensions { get; set; }
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; }
    public bool IsAvailableForRetail { get; set; }
    public bool IsAvailableForPos { get; set; }
    public bool IsAvailableForMirMiv { get; set; }
    public bool IsAvailableForProduction { get; set; }
    public bool IsAvailableForServices { get; set; }
    public decimal? ReorderPoint { get; set; }
    public int? SortOrder { get; set; }
    public DateTime CreatedDate { get; set; }
}

// PV-004 — resolves a scanned barcode straight to its variant during GRN receiving, along with
// enough context (product + price) for the UI to display a match/mismatch against the PO line.
public class VariantLookupModel
{
    public Guid Uuid { get; set; }
    public string Sku { get; set; } = string.Empty;
    public string VariantName { get; set; } = string.Empty;
    public string? Barcode { get; set; }
    public decimal PurchasePrice { get; set; }
    public int ProductId { get; set; }
    public Guid ProductUuid { get; set; }
    public string ProductName { get; set; } = string.Empty;
}

// PV-005 — product-level rollup across every one of its variants' stock. Computed on the fly
// (SUM across InventoryItem rows joined through ProductVariant), never stored.
public class ProductStockSummaryModel
{
    public int ProductId { get; set; }
    public Guid ProductUuid { get; set; }
    public decimal TotalOnHand { get; set; }
    public decimal TotalReserved { get; set; }
    public decimal TotalAvailable { get; set; }
    public List<VariantStockSummaryItem> Variants { get; set; } = [];
}

public class VariantStockSummaryItem
{
    public Guid VariantUuid { get; set; }
    public string Sku { get; set; } = string.Empty;
    public string VariantName { get; set; } = string.Empty;
    public decimal OnHand { get; set; }
    public decimal Reserved { get; set; }
    public decimal Available { get; set; }
}

// PV-006 — full-text search result row. MatchedTerms lists which of the query's words were
// actually found in this row's display fields (product/variant name, sku, barcode, attribute
// values) — a lightweight stand-in for character-offset highlighting.
public class ProductSearchResultItem
{
    public Guid VariantUuid { get; set; }
    public string Sku { get; set; } = string.Empty;
    public string VariantName { get; set; } = string.Empty;
    public string? Barcode { get; set; }
    public decimal PurchasePrice { get; set; }
    public int ProductId { get; set; }
    public Guid ProductUuid { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string? ProductCode { get; set; }
    public string? CategoryName { get; set; }
    public string? Brand { get; set; }
    public List<string> MatchedTerms { get; set; } = [];
    public List<VariantAttributeValueModel> Attributes { get; set; } = [];
}

public class ProductSearchFilter
{
    public string? Query { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

public class CreateProductVariantRequest
{
    public string? Sku { get; set; }
    public string VariantName { get; set; } = string.Empty;
    public string? Barcode { get; set; }
    public decimal PurchasePrice { get; set; }
    public decimal? SellingPrice { get; set; }
    public decimal? Weight { get; set; }
    public string? Dimensions { get; set; }
    public bool IsDefault { get; set; }
    public bool IsAvailableForRetail { get; set; }
    public bool IsAvailableForPos { get; set; }
    public bool IsAvailableForMirMiv { get; set; }
    public bool IsAvailableForProduction { get; set; }
    public bool IsAvailableForServices { get; set; }
    public decimal? ReorderPoint { get; set; }
    public int? SortOrder { get; set; }
}

// PV-007 — result of adding a variant to an existing product.
public class CreateVariantResult
{
    public Guid Uuid { get; set; }
    public string Sku { get; set; } = string.Empty;
}

// PV-007 — outcome of a variant delete request: whether it existed, was blocked because it's
// the product's only remaining variant, or was actually removed (soft or hard, per the caller's
// own transacted-check — this enum doesn't distinguish which).
public enum VariantDeleteOutcome
{
    NotFound,
    IsLastVariant,
    Deleted
}

// PV-007 — what the caller (frontend) actually needs to know after a delete request: whether it
// happened, and whether the variant was soft-deleted (still exists, is_active=false, because
// it's been transacted) or hard-deleted (row removed entirely). "Blocked" (only variant left on
// the product) surfaces as an UnprocessableEntityException instead — see InventoryService.
public class VariantDeleteResult
{
    public bool Found { get; set; }
    public bool SoftDeleted { get; set; }
}

public class ProductListFilter
{
    public int? CategoryId { get; set; }
    public string? Status { get; set; }
    public string? Search { get; set; }
    public bool ActiveOnly { get; set; } = true;
    // A SMS.Shared.Common.VariantAvailabilityChannel value — only products with at least one
    // active variant checked for that channel are returned. Null/omitted means no such filtering.
    public string? AvailableFor { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

// ── Product requests ──────────────────────────────────────────────────────────

public class CreateProductRequest
{
    public string? Sku { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ShortName { get; set; }
    public string? Description { get; set; }
    public int? CategoryId { get; set; }
    public int? SubCategoryId { get; set; }
    public string? Brand { get; set; }
    public string? UomCode { get; set; }
    public decimal? WeightKg { get; set; }
    public string? Dimensions { get; set; }
    public int? ShelfLifeDays { get; set; }
    public bool IsBatchTracked { get; set; }
    public bool IsSerialTracked { get; set; }
    public decimal? ReorderPoint { get; set; }
    public decimal? ReorderQty { get; set; }
    public decimal? MinStockLevel { get; set; }
    public decimal? MaxStockLevel { get; set; }
    public int? LeadTimeDays { get; set; }
    public int? PreferredSupplierId { get; set; }
    public string? Notes { get; set; }
    public string? ImageUrl { get; set; }

    // ── Variant seeding (PV-001) ────────────────────────────────────────────────
    // If Variants is supplied (non-empty), those exact variants are created and exactly one
    // must have IsDefault=true. Otherwise, a single default variant is auto-created using
    // PurchasePrice/SellingPrice/Barcode below — required in that case (FSD §1.2).
    public List<CreateProductVariantRequest>? Variants { get; set; }
    public decimal? PurchasePrice { get; set; }
    public decimal? SellingPrice { get; set; }
    public string? Barcode { get; set; }
}

public class PatchProductRequest
{
    public string? Name { get; set; }
    public string? ShortName { get; set; }
    public string? Description { get; set; }
    public int? CategoryId { get; set; }
    public int? SubCategoryId { get; set; }
    public string? Brand { get; set; }
    public string? UomCode { get; set; }
    public decimal? WeightKg { get; set; }
    public string? Dimensions { get; set; }
    public int? ShelfLifeDays { get; set; }
    public bool? IsBatchTracked { get; set; }
    public bool? IsSerialTracked { get; set; }
    public decimal? ReorderPoint { get; set; }
    public decimal? ReorderQty { get; set; }
    public decimal? MinStockLevel { get; set; }
    public decimal? MaxStockLevel { get; set; }
    public int? LeadTimeDays { get; set; }
    public int? PreferredSupplierId { get; set; }
    public string? Notes { get; set; }
    public string? ImageUrl { get; set; }
    public string? Status { get; set; }
}

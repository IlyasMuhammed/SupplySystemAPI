using System.Text.Json.Serialization;

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
    // A30 §6 — SMS.Shared.Common.ProductType / SupplyMethod codes.
    public string ProductType { get; set; } = string.Empty;
    public string SupplyMethod { get; set; } = string.Empty;
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
    public bool IsSaleable { get; set; }
    public bool IsPurchasable { get; set; }
    public bool IsStockable { get; set; }
    public bool IsManufacturable { get; set; }
    public int? DefaultProductionWarehouseId { get; set; }
    public string? DefaultProductionWarehouseName { get; set; }
    // A36 D-2 — service configuration (null/false for non-service products).
    public string? ServiceInvoicingPolicy { get; set; }
    public string? ServiceBillingModel { get; set; }
    public decimal? EstimatedDurationHours { get; set; }
    public bool HasServiceBom { get; set; }
    public bool IsSubcontractable { get; set; }
    /// <summary>
    /// A36 D-3 (read-only) — a service with <see cref="HasServiceBom"/> that has an ACTIVE, effective BOM (for any of its
    /// variants or product-general). False → the UI warns "no active service BOM"; service orders then plan ad hoc.
    /// Filled by InventoryService from Material's IBomStructureReader; always false without Material in the host.
    /// </summary>
    public bool HasActiveServiceBom { get; set; }
    // A37 D-10 — read-only, derived: ProductType == SERVICE.
    public bool IsServiceable => ProductType == SMS.Shared.Common.ProductType.Service;
    public string? ServiceCategory { get; set; }
    public bool RequiresSiteVisit { get; set; }
    public string? Notes { get; set; }
    public DateTime? UpdatedDate { get; set; }
    public DateTime ModifiedAt { get; set; }
    public int CreatedBy { get; set; }
    public List<ProductVariantModel> Variants { get; set; } = [];

    /// <summary>
    /// A37 D-10 (PRD-CAP-03) — present only while MODULE_MANUFACTURING / MODULE_SERVICES is enabled for the caller's
    /// organization; otherwise null, and a null is left out of the JSON (key absent, not null). Set by InventoryService.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProductProductionSettingsModel? ProductionSettings { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProductServiceSettingsModel? ServiceSettings { get; set; }

    // The product's organization — whose modules decide the two blocks above. Internal, so never serialized.
    internal Guid OrganizationId { get; set; }
}

/// <summary>A37 §2 — the manufacturing view of a product (flat fields stay on the detail for compatibility).</summary>
public class ProductProductionSettingsModel
{
    public string SupplyMethod { get; set; } = string.Empty;
    public int? DefaultProductionWarehouseId { get; set; }
    public string? DefaultProductionWarehouseName { get; set; }
    public Guid? ActiveBomUuid { get; set; }
    public string? ActiveBomNumber { get; set; }
    /// <summary>The default variant's A34 override, else Product.LeadTimeDays for a MANUFACTURE product, else null.</summary>
    public int? ManufacturingLeadTimeDays { get; set; }
}

/// <summary>A37 §2 — the service view of a product (A36 fields + the A37 two).</summary>
public class ProductServiceSettingsModel
{
    public string? InvoicingPolicy { get; set; }
    public string? BillingModel { get; set; }
    public decimal? EstimatedDurationHours { get; set; }
    public bool HasServiceBom { get; set; }
    public bool IsSubcontractable { get; set; }
    public string? ServiceCategory { get; set; }
    public bool RequiresSiteVisit { get; set; }
    public Guid? ActiveServiceBomUuid { get; set; }
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
    public decimal? SaleOrderMinQty { get; set; }
    public decimal? SaleOrderMaxQty { get; set; }
    public DateTime CreatedDate { get; set; }

    // A33 C2 — the variant's default fulfillment route. Code and name come from Logistics (IFulfillmentRouteLookup)
    // and are null when the route is gone or the host has no Logistics; the uuid is what the variant stores.
    public Guid? FulfillmentRouteUuid { get; set; }
    public string? FulfillmentRouteCode { get; set; }
    public string? FulfillmentRouteName { get; set; }

    // A34 §4.1 (D-4) — the variant's own route's category (null when it has no route of its own, or the route is gone)
    // and the "Make to order" tag: true exactly when that category is MANUFACTURE.
    public string? FulfillmentRouteCategory { get; set; }
    public bool IsMakeToOrder { get; set; }

    // A34 §4.1 — the stored lead-time overrides, read-only here (edited through PUT api/variants/{uuid}/lead-times;
    // the variant PATCH ignores them). Null = use the default. SupplierLeadTimeDays is ProductVariant.LeadTimeDays (D-11).
    public int? SupplierLeadTimeDays { get; set; }
    public int? ManufacturingLeadTimeDays { get; set; }
    public int? ManufacturingBufferDays { get; set; }
    public int? QualityInspectionDays { get; set; }
    public int? InternalTransferDays { get; set; }
    public int? PickPackDays { get; set; }
    public int? ShippingLeadTimeDays { get; set; }
    public int? SalesBufferDays { get; set; }

    // The variant's organization — whose routes its route uuid names. Internal, so never serialized.
    internal Guid OrganizationId { get; set; }
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
    // A31-C1 — see ProductVariant.SaleOrderMinQty/MaxQty. NULL or 0 on either side = unconstrained.
    public decimal? SaleOrderMinQty { get; set; }
    public decimal? SaleOrderMaxQty { get; set; }
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
    // A30 §6 — SMS.Shared.Common.ProductType / SupplyMethod codes. Null/omitted means no filtering.
    public string? ProductType { get; set; }
    public string? SupplyMethod { get; set; }
    // A34 §4.3 — STOCK / MANUFACTURE (case-insensitive): products with at least one active variant whose own route has
    // that category. Variants without a route are not matched (they follow the org default).
    public string? RouteCategory { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;

    // Set by InventoryService from RouteCategory: the organization's active routes of that category. Null = no filter.
    internal IReadOnlyList<Guid>? RouteUuids { get; set; }
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

    // ── Manufacturing classification (A30 §6) ───────────────────────────────────
    // Omitted → STOCK_ITEM supplied by PURCHASE, saleable/purchasable/stockable; the same
    // defaults every product that existed before these fields carries. Flags left null take the
    // §6.1 defaults for the chosen type.
    public string? ProductType { get; set; }
    public string? SupplyMethod { get; set; }
    public bool? IsSaleable { get; set; }
    public bool? IsPurchasable { get; set; }
    public bool? IsStockable { get; set; }
    public int? DefaultProductionWarehouseId { get; set; }

    // ── Service configuration (A36 D-2, SVC-P-01..07) — only for ProductType SERVICE ──
    public string? ServiceInvoicingPolicy { get; set; }
    public string? ServiceBillingModel { get; set; }
    public decimal? EstimatedDurationHours { get; set; }
    public bool? HasServiceBom { get; set; }
    public bool? IsSubcontractable { get; set; }
    // A37 D-10 — service only (a ServiceCategory code; the site-visit flag).
    public string? ServiceCategory { get; set; }
    public bool? RequiresSiteVisit { get; set; }

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
    // A30 §6 — overlaid on the current classification and validated as a whole, so a type change
    // that needs a different supply method must send both.
    public string? ProductType { get; set; }
    public string? SupplyMethod { get; set; }
    public bool? IsSaleable { get; set; }
    public bool? IsPurchasable { get; set; }
    public bool? IsStockable { get; set; }
    public int? DefaultProductionWarehouseId { get; set; }
    // A36 D-2 — overlaid on the product's current service configuration and validated as a whole (SVC-P-01..07).
    // The two codes and the duration: omitted = unchanged, an explicit null clears (System.Text.Json calls a setter
    // only for a property present in the body, so the setters record that it was sent). The flags: null = unchanged,
    // false clears. When the product stops being a SERVICE, its stored service configuration is cleared.
    private string?  _serviceInvoicingPolicy;
    private string?  _serviceBillingModel;
    private decimal? _estimatedDurationHours;

    public string? ServiceInvoicingPolicy
    {
        get => _serviceInvoicingPolicy;
        set { _serviceInvoicingPolicy = value; ServiceInvoicingPolicySent = true; }
    }
    public string? ServiceBillingModel
    {
        get => _serviceBillingModel;
        set { _serviceBillingModel = value; ServiceBillingModelSent = true; }
    }
    public decimal? EstimatedDurationHours
    {
        get => _estimatedDurationHours;
        set { _estimatedDurationHours = value; EstimatedDurationHoursSent = true; }
    }
    public bool? HasServiceBom { get; set; }
    public bool? IsSubcontractable { get; set; }
    // A37 D-10 — same rules: the category omitted = unchanged, explicit null clears; the flag null = unchanged.
    private string? _serviceCategory;
    public string? ServiceCategory
    {
        get => _serviceCategory;
        set { _serviceCategory = value; ServiceCategorySent = true; }
    }
    public bool? RequiresSiteVisit { get; set; }

    internal bool ServiceInvoicingPolicySent { get; private set; }
    internal bool ServiceBillingModelSent    { get; private set; }
    internal bool EstimatedDurationHoursSent { get; private set; }
    internal bool ServiceCategorySent        { get; private set; }
    internal bool AnyServiceFieldSent =>
        ServiceInvoicingPolicySent || ServiceBillingModelSent || EstimatedDurationHoursSent ||
        HasServiceBom.HasValue || IsSubcontractable.HasValue || ServiceCategorySent || RequiresSiteVisit.HasValue;
}

// A30 §28.1 — PATCH /api/products/{id}/manufacturing-config. Type and supply method are required;
// a flag left null is re-defaulted from the new type's §6.1 row rather than kept from the old one,
// because the old flags were chosen for the old type.
public class ManufacturingConfigRequest
{
    public string ProductType { get; set; } = string.Empty;
    public string SupplyMethod { get; set; } = string.Empty;
    public bool? IsSaleable { get; set; }
    public bool? IsPurchasable { get; set; }
    public bool? IsStockable { get; set; }
    public int? DefaultProductionWarehouseId { get; set; }
    public int? LeadTimeDays { get; set; }
}

using SMS.Modules.Inventory.Models;
using SMS.Shared.Pagination;

namespace SMS.Modules.Inventory.Repositories;

internal interface IInventoryRepository
{
    // ── Categories ────────────────────────────────────────────────────────────
    Task<List<CategoryModel>> GetCategoriesAsync();
    Task<CategoryModel?> GetCategoryByIdAsync(int id);
    Task<int> CreateCategoryAsync(CreateCategoryRequest req, int userId);
    Task<bool> UpdateCategoryAsync(int id, UpdateCategoryRequest req);
    Task<CategoryDeleteResult> DeleteCategoryAsync(int id);
    Task<bool> DeactivateCategoryAsync(int id);
    Task<int> CreateSubCategoryAsync(int categoryId, CreateSubCategoryRequest req, int userId);
    Task<bool> UpdateSubCategoryAsync(int subId, UpdateSubCategoryRequest req);
    Task<SubCategoryDeleteResult> DeleteSubCategoryAsync(int subId);
    Task<bool> DeactivateSubCategoryAsync(int subId);
    Task<PaginatedResponse<SubCategoryListDto>> GetSubCategoriesAsync(SubCategoryListFilter filter);
    Task<bool> CategoryExistsAsync(int id);

    // ── Products ──────────────────────────────────────────────────────────────
    Task<PaginatedResponse<ProductListItemModel>> GetProductsAsync(ProductListFilter filter);
    Task<ProductDetailModel?> GetProductByIdAsync(int id);
    Task<(int id, string sku)> CreateProductAsync(CreateProductRequest req, int userId);
    Task<bool> PatchProductAsync(int id, PatchProductRequest req);
    Task<bool> SoftDeleteProductAsync(int id);
    Task<bool> SkuExistsAsync(string sku);
    Task<VariantLookupModel?> GetVariantByBarcodeAsync(string barcode);

    // PV-006 — full-text search against the denormalised ProductSearchIndex.
    Task<PaginatedResponse<ProductSearchResultItem>> SearchProductsAsync(ProductSearchFilter filter);

    // PV-007 — variant CRUD on an existing product (create-time variant seeding stays in
    // CreateProductAsync's BuildVariantsAsync). Null return means "product/variant not found".
    Task<(Guid uuid, int id, string sku)?> CreateVariantAsync(int productId, CreateProductVariantRequest req, int userId);
    Task<int?> UpdateVariantAsync(Guid variantUuid, CreateProductVariantRequest req);
    Task<bool> VariantHasLocalReferencesAsync(Guid variantUuid);
    Task<(VariantDeleteOutcome outcome, int? variantId)> DeleteVariantAsync(Guid variantUuid, bool softDelete);

    // ── Warehouses ────────────────────────────────────────────────────────────
    Task<List<WarehouseModel>> GetWarehousesAsync();
    Task<WarehouseModel?> GetWarehouseByIdAsync(int id);
    Task<int> CreateWarehouseAsync(CreateWarehouseRequest req, int userId);
    Task<bool> UpdateWarehouseAsync(int id, PatchWarehouseRequest req);
    Task<bool> DeleteWarehouseAsync(int id);

    // ── Structure ─────────────────────────────────────────────────────────────
    Task<WarehouseStructureModel> GetWarehouseStructureAsync(int warehouseId);
    Task<int> CreateZoneAsync(int warehouseId, CreateZoneRequest req);
    Task<bool> UpdateZoneAsync(int id, UpdateZoneRequest req);
    Task<StructureDeactivateResult> DeactivateZoneAsync(int id);
    Task<List<RackModel>> GetRacksAsync(int zoneId);
    Task<int> CreateRackAsync(int zoneId, CreateRackRequest req);
    Task<bool> UpdateRackAsync(int id, UpdateRackRequest req);
    Task<StructureDeactivateResult> DeactivateRackAsync(int id);
    Task<List<ShelfModel>> GetShelvesAsync(int rackId);
    Task<int> CreateShelfAsync(int rackId, CreateShelfRequest req);
    Task<bool> UpdateShelfAsync(int id, UpdateShelfRequest req);
    Task<StructureDeactivateResult> DeactivateShelfAsync(int id);
    Task<int> CreateBinAsync(int zoneId, CreateBinRequest req);
    Task<int> CreateStructuredBinAsync(int shelfId, CreateBinRequest req);
    Task<bool> UpdateBinAsync(int id, UpdateBinRequest req);
    Task<StructureDeactivateResult> DeactivateBinAsync(int id);

    // ── Stock levels ──────────────────────────────────────────────────────────
    Task<PaginatedResponse<StockLevelModel>> GetWarehouseStockAsync(int warehouseId, StockLevelFilter filter);
    Task<List<ProductStockModel>> GetProductStockAsync(int productId);
    Task<List<VariantWarehouseStockModel>> GetVariantStockByWarehouseAsync(Guid variantUuid);
    Task<ProductStockSummaryModel?> GetProductStockSummaryAsync(int productId);
    Task<List<ReorderAlertModel>> GetReorderAlertsAsync();
    Task<bool> MoveBinAsync(int inventoryItemId, int binId);

    // ── Stock adjustments ─────────────────────────────────────────────────────
    Task<PaginatedResponse<StockAdjustmentModel>> GetAdjustmentsAsync(AdjustmentListFilter filter);
    Task<StockAdjustmentResult> CreateAdjustmentAsync(CreateAdjustmentRequest req, int userId);
    Task<bool> ApproveAdjustmentAsync(Guid uuid, int reviewerId);
    Task<bool> RejectAdjustmentAsync(Guid uuid, string reason, int reviewerId);

    // ── Dynamic Attributes (FSD Addendum 26 §4) ──────────────────────────────────
    Task<List<AttributeDefinitionModel>> GetAttributesAsync();
    Task<Guid> CreateAttributeAsync(CreateAttributeDefinitionRequest req);
    Task<bool> UpdateAttributeAsync(Guid uuid, UpdateAttributeDefinitionRequest req);
    Task<AttributeDeleteResult> DeleteAttributeAsync(Guid uuid);
    Task<List<CategoryAttributeModel>> GetCategoryAttributesAsync(int categoryId);
    Task<bool> LinkCategoryAttributeAsync(int categoryId, CreateCategoryAttributeRequest req);
    Task<bool> UnlinkCategoryAttributeAsync(int categoryId, Guid attributeUuid);
    Task<List<CategoryAttributeModel>> SetCategoryAttributesAsync(int categoryId, SetCategoryAttributesRequest req);
    Task<List<VariantAttributeValueModel>> GetVariantAttributeValuesAsync(Guid variantUuid);
    // Returns the variant's internal id on success (so callers can enqueue a search-index
    // rebuild for it), or null if the variant uuid doesn't resolve to anything.
    Task<int?> SetVariantAttributeValuesAsync(Guid variantUuid, SetVariantAttributeValuesRequest req);
}

using Hangfire;
using SMS.Modules.Inventory.Integration;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Repositories;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Inventory.Services;

internal sealed class InventoryService : IInventoryService
{
    private readonly IInventoryRepository _repo;
    private readonly IProductSearchIndexService _searchIndex;
    private readonly IBackgroundJobClient _jobs;
    private readonly IEnumerable<IVariantReferenceChecker> _variantCheckers;
    private readonly VariantQuickBooksPublisher? _quickBooks;
    private readonly IFulfillmentRouteLookup? _routeLookup;
    private readonly ITenantContext? _tenantContext;

    /// <param name="quickBooks">
    /// Tells the QuickBooks gateway about variants after each save that changes what it would send. Optional
    /// so a caller that builds this service by hand needs nothing extra; the module registers it.
    /// </param>
    /// <param name="routeLookup">
    /// A33 — names each variant's fulfillment route on the product detail. Logistics registers it; without it the
    /// route uuid is still shown, with no code or name (D-11).
    /// </param>
    /// <param name="tenantContext">A34 §4.3 — whose routes the <c>routeCategory</c> filter reads (the caller's own organization).</param>
    public InventoryService(
        IInventoryRepository repo, IProductSearchIndexService searchIndex, IBackgroundJobClient jobs,
        IEnumerable<IVariantReferenceChecker> variantCheckers, VariantQuickBooksPublisher? quickBooks = null,
        IFulfillmentRouteLookup? routeLookup = null, ITenantContext? tenantContext = null)
    {
        _repo            = repo;
        _searchIndex     = searchIndex;
        _jobs            = jobs;
        _variantCheckers = variantCheckers;
        _quickBooks      = quickBooks;
        _routeLookup     = routeLookup;
        _tenantContext   = tenantContext;
    }

    // After the repository's own save has committed. Neither throws (see the publisher).
    private Task PublishProductToQuickBooksAsync(int productId) =>
        _quickBooks is null ? Task.CompletedTask : _quickBooks.PublishProductAsync(productId);

    private Task PublishVariantToQuickBooksAsync(Guid variantUuid) =>
        _quickBooks is null ? Task.CompletedTask : _quickBooks.PublishVariantAsync(variantUuid);

    // ── Categories ────────────────────────────────────────────────────────────

    public Task<List<CategoryModel>> GetCategoriesAsync()
        => _repo.GetCategoriesAsync();

    public Task<CategoryModel?> GetCategoryByIdAsync(int id)
        => _repo.GetCategoryByIdAsync(id);

    public Task<int> CreateCategoryAsync(CreateCategoryRequest req, int userId)
        => _repo.CreateCategoryAsync(req, userId);

    public Task<bool> UpdateCategoryAsync(int id, UpdateCategoryRequest req)
        => _repo.UpdateCategoryAsync(id, req);

    public Task<CategoryDeleteResult> DeleteCategoryAsync(int id)
        => _repo.DeleteCategoryAsync(id);

    public Task<bool> DeactivateCategoryAsync(int id)
        => _repo.DeactivateCategoryAsync(id);

    public Task<int> CreateSubCategoryAsync(int categoryId, CreateSubCategoryRequest req, int userId)
        => _repo.CreateSubCategoryAsync(categoryId, req, userId);

    public Task<bool> UpdateSubCategoryAsync(int subId, UpdateSubCategoryRequest req)
        => _repo.UpdateSubCategoryAsync(subId, req);

    public Task<SubCategoryDeleteResult> DeleteSubCategoryAsync(int subId)
        => _repo.DeleteSubCategoryAsync(subId);

    public Task<bool> DeactivateSubCategoryAsync(int subId)
        => _repo.DeactivateSubCategoryAsync(subId);

    public Task<PaginatedResponse<SubCategoryListDto>> GetSubCategoriesAsync(SubCategoryListFilter filter)
        => _repo.GetSubCategoriesAsync(filter);

    // ── Products ──────────────────────────────────────────────────────────────

    public async Task<PaginatedResponse<ProductListItemModel>> GetProductsAsync(ProductListFilter filter)
    {
        if (!string.IsNullOrWhiteSpace(filter.RouteCategory))
            filter.RouteUuids = await RouteUuidsOfCategoryAsync(filter.RouteCategory);
        return await _repo.GetProductsAsync(filter);
    }

    // A34 §4.3 — the caller's own organization's active routes of the category (routes live in Logistics, so the
    // repository filters variants by these uuids). No Logistics in the host: no routes, so nothing matches.
    private async Task<IReadOnlyList<Guid>> RouteUuidsOfCategoryAsync(string requested)
    {
        var category = requested.Trim().ToUpperInvariant();
        if (!FulfillmentRouteCategory.IsKnown(category))
            throw new BadRequestException(
                $"Unknown route category '{requested.Trim()}'. Categories are {string.Join(", ", FulfillmentRouteCategory.All)}.");

        var org = _tenantContext?.OrganizationId ?? Guid.Empty;
        if (_routeLookup is null || org == Guid.Empty) return [];

        var routes = await _routeLookup.ListActiveAsync(org);
        return routes.Where(r => r.Category == category).Select(r => r.Uuid).ToList();
    }

    public async Task<ProductDetailModel?> GetProductByIdAsync(int id)
    {
        var detail = await _repo.GetProductByIdAsync(id);
        if (detail is not null) await NameVariantRoutesAsync(detail.Variants);
        return detail;
    }

    // A33 C2 — each variant's route code and name, read from Logistics for the variant's own organization (the
    // route belongs to it: BR-C2-01). A route that is gone keeps its uuid and gets no name.
    private async Task NameVariantRoutesAsync(IEnumerable<ProductVariantModel> variants)
    {
        if (_routeLookup is null) return;

        foreach (var ofOrg in variants.Where(v => v.FulfillmentRouteUuid.HasValue).GroupBy(v => v.OrganizationId))
        {
            var routes = await _routeLookup.GetAsync(
                ofOrg.Key, ofOrg.Select(v => v.FulfillmentRouteUuid!.Value).Distinct().ToList());
            foreach (var variant in ofOrg)
            {
                if (!routes.TryGetValue(variant.FulfillmentRouteUuid!.Value, out var route)) continue;
                variant.FulfillmentRouteCode = route.Code;
                variant.FulfillmentRouteName = route.Name;
                // A34 §4.1 (D-4) — the "Make to order" tag.
                variant.FulfillmentRouteCategory = route.Category;
                variant.IsMakeToOrder            = route.IsManufacture;
            }
        }
    }

    public async Task<(int id, string sku)> CreateProductAsync(CreateProductRequest req, int userId)
    {
        var result = await _repo.CreateProductAsync(req, userId);
        await EnqueueRebuildForProductVariantsAsync(result.id);
        await PublishProductToQuickBooksAsync(result.id);
        return result;
    }

    public async Task<bool> PatchProductAsync(int id, PatchProductRequest req)
    {
        var ok = await _repo.PatchProductAsync(id, req);
        // Product-level fields (name, brand, category) are denormalised into every one of the
        // product's variants' search rows, so a product patch must reindex all of them too —
        // not just literal "variant create/update" from the FSD's wording.
        if (ok) await EnqueueRebuildForProductVariantsAsync(id);
        // Likewise every one of its QuickBooks items: name, description and flags come from the product.
        if (ok) await PublishProductToQuickBooksAsync(id);
        return ok;
    }

    // Classification is not part of the search index, so no reindex here — but it is what decides an
    // item's kind and whether it is sold/purchased, so QuickBooks hears about it.
    public async Task<bool> SetManufacturingConfigAsync(int id, ManufacturingConfigRequest req)
    {
        var ok = await _repo.SetManufacturingConfigAsync(id, req);
        if (ok) await PublishProductToQuickBooksAsync(id);
        return ok;
    }

    public async Task<bool> SoftDeleteProductAsync(int id)
    {
        var ok = await _repo.SoftDeleteProductAsync(id);
        if (ok) await PublishProductToQuickBooksAsync(id);
        return ok;
    }

    public Task<VariantLookupModel?> GetVariantByBarcodeAsync(string barcode)
        => _repo.GetVariantByBarcodeAsync(barcode);

    // ── Search (PV-006) ──────────────────────────────────────────────────────

    public Task<PaginatedResponse<ProductSearchResultItem>> SearchProductsAsync(ProductSearchFilter filter)
        => _repo.SearchProductsAsync(filter);

    public Task RebuildSearchIndexAsync()
        => _searchIndex.RebuildAllAsync();

    // ── Variants (PV-007) ────────────────────────────────────────────────────

    public async Task<CreateVariantResult?> CreateVariantAsync(int productId, CreateProductVariantRequest req, int userId)
    {
        var result = await _repo.CreateVariantAsync(productId, req, userId);
        if (result is null) return null;

        _jobs.Enqueue<IProductSearchIndexService>(s => s.RebuildForVariantAsync(result.Value.id));
        // The new variant — and, when it is the product's second, the first one too, which then starts
        // carrying its variant name in QuickBooks.
        if (_quickBooks is not null)
            await _quickBooks.PublishVariantAddedAsync(productId, result.Value.uuid);
        return new CreateVariantResult { Uuid = result.Value.uuid, Sku = result.Value.sku };
    }

    public async Task<bool> UpdateVariantAsync(Guid variantUuid, CreateProductVariantRequest req)
    {
        var variantId = await _repo.UpdateVariantAsync(variantUuid, req);
        if (variantId.HasValue)
        {
            _jobs.Enqueue<IProductSearchIndexService>(s => s.RebuildForVariantAsync(variantId.Value));
            await PublishVariantToQuickBooksAsync(variantUuid);
        }
        return variantId.HasValue;
    }

    public async Task<VariantDeleteResult> DeleteVariantAsync(Guid variantUuid)
    {
        // Transacted anywhere (this module's own stock tables, or a PO/GRN/MIR line in another
        // module) -> soft-delete only, so historical documents keep showing the variant.
        var hasLocalReferences = await _repo.VariantHasLocalReferencesAsync(variantUuid);
        var hasExternalReferences = false;
        if (!hasLocalReferences)
        {
            foreach (var checker in _variantCheckers)
            {
                if (await checker.IsVariantReferencedAsync(variantUuid))
                {
                    hasExternalReferences = true;
                    break;
                }
            }
        }
        var softDelete = hasLocalReferences || hasExternalReferences;

        // Read before the delete: a hard delete leaves nothing to tell QuickBooks about afterwards.
        var quickBooksSnapshot = _quickBooks is null ? null : await _quickBooks.CaptureBeforeDeleteAsync(variantUuid);

        var (outcome, variantId) = await _repo.DeleteVariantAsync(variantUuid, softDelete);

        switch (outcome)
        {
            case VariantDeleteOutcome.NotFound:
                return new VariantDeleteResult { Found = false };
            case VariantDeleteOutcome.IsLastVariant:
                throw new UnprocessableEntityException(
                    "Cannot delete the only variant of a product — every product must have at least one.");
            default: // Deleted
                if (softDelete && variantId.HasValue)
                    _jobs.Enqueue<IProductSearchIndexService>(s => s.RebuildForVariantAsync(variantId.Value));
                if (_quickBooks is not null)
                    await _quickBooks.PublishDeletedAsync(quickBooksSnapshot, hardDeleted: !softDelete);
                return new VariantDeleteResult { Found = true, SoftDeleted = softDelete };
        }
    }

    private async Task EnqueueRebuildForProductVariantsAsync(int productId)
    {
        var product = await _repo.GetProductByIdAsync(productId);
        if (product is null) return;
        foreach (var v in product.Variants)
            _jobs.Enqueue<IProductSearchIndexService>(s => s.RebuildForVariantAsync(v.Id));
    }

    // ── Warehouses ────────────────────────────────────────────────────────────

    public Task<List<WarehouseModel>> GetWarehousesAsync()
        => _repo.GetWarehousesAsync();

    public Task<WarehouseModel?> GetWarehouseByIdAsync(int id)
        => _repo.GetWarehouseByIdAsync(id);

    public Task<int> CreateWarehouseAsync(CreateWarehouseRequest req, int userId)
        => _repo.CreateWarehouseAsync(req, userId);

    public Task<bool> UpdateWarehouseAsync(int id, PatchWarehouseRequest req)
        => _repo.UpdateWarehouseAsync(id, req);

    public Task<bool> DeleteWarehouseAsync(int id)
        => _repo.DeleteWarehouseAsync(id);

    public Task<WarehouseStructureModel> GetWarehouseStructureAsync(int warehouseId)
        => _repo.GetWarehouseStructureAsync(warehouseId);

    public Task<int> CreateZoneAsync(int warehouseId, CreateZoneRequest req)
        => _repo.CreateZoneAsync(warehouseId, req);

    public Task<bool> UpdateZoneAsync(int id, UpdateZoneRequest req)
        => _repo.UpdateZoneAsync(id, req);

    public Task<StructureDeactivateResult> DeactivateZoneAsync(int id)
        => _repo.DeactivateZoneAsync(id);

    public Task<List<RackModel>> GetRacksAsync(int zoneId)
        => _repo.GetRacksAsync(zoneId);

    public Task<int> CreateRackAsync(int zoneId, CreateRackRequest req)
        => _repo.CreateRackAsync(zoneId, req);

    public Task<bool> UpdateRackAsync(int id, UpdateRackRequest req)
        => _repo.UpdateRackAsync(id, req);

    public Task<StructureDeactivateResult> DeactivateRackAsync(int id)
        => _repo.DeactivateRackAsync(id);

    public Task<List<ShelfModel>> GetShelvesAsync(int rackId)
        => _repo.GetShelvesAsync(rackId);

    public Task<int> CreateShelfAsync(int rackId, CreateShelfRequest req)
        => _repo.CreateShelfAsync(rackId, req);

    public Task<bool> UpdateShelfAsync(int id, UpdateShelfRequest req)
        => _repo.UpdateShelfAsync(id, req);

    public Task<StructureDeactivateResult> DeactivateShelfAsync(int id)
        => _repo.DeactivateShelfAsync(id);

    public Task<int> CreateBinAsync(int zoneId, CreateBinRequest req)
        => _repo.CreateBinAsync(zoneId, req);

    public Task<int> CreateStructuredBinAsync(int shelfId, CreateBinRequest req)
        => _repo.CreateStructuredBinAsync(shelfId, req);

    public Task<bool> UpdateBinAsync(int id, UpdateBinRequest req)
        => _repo.UpdateBinAsync(id, req);

    public Task<StructureDeactivateResult> DeactivateBinAsync(int id)
        => _repo.DeactivateBinAsync(id);

    // ── Stock ─────────────────────────────────────────────────────────────────

    public Task<PaginatedResponse<StockLevelModel>> GetWarehouseStockAsync(int warehouseId, StockLevelFilter filter)
        => _repo.GetWarehouseStockAsync(warehouseId, filter);

    public Task<List<ProductStockModel>> GetProductStockAsync(int productId)
        => _repo.GetProductStockAsync(productId);

    public Task<List<VariantWarehouseStockModel>> GetVariantStockByWarehouseAsync(Guid variantUuid)
        => _repo.GetVariantStockByWarehouseAsync(variantUuid);

    public Task<ProductStockSummaryModel?> GetProductStockSummaryAsync(int productId)
        => _repo.GetProductStockSummaryAsync(productId);

    public Task<List<ReorderAlertModel>> GetReorderAlertsAsync()
        => _repo.GetReorderAlertsAsync();

    public Task<bool> MoveBinAsync(int inventoryItemId, int binId)
        => _repo.MoveBinAsync(inventoryItemId, binId);

    // ── Adjustments ───────────────────────────────────────────────────────────

    public Task<PaginatedResponse<StockAdjustmentModel>> GetAdjustmentsAsync(AdjustmentListFilter filter)
        => _repo.GetAdjustmentsAsync(filter);

    public Task<StockAdjustmentResult> CreateAdjustmentAsync(CreateAdjustmentRequest req, int userId)
        => _repo.CreateAdjustmentAsync(req, userId);

    public Task<bool> ApproveAdjustmentAsync(Guid uuid, int reviewerId)
        => _repo.ApproveAdjustmentAsync(uuid, reviewerId);

    public Task<bool> RejectAdjustmentAsync(Guid uuid, string reason, int reviewerId)
        => _repo.RejectAdjustmentAsync(uuid, reason, reviewerId);

    // ── Dynamic Attributes ────────────────────────────────────────────────────

    public Task<List<AttributeDefinitionModel>> GetAttributesAsync()
        => _repo.GetAttributesAsync();

    public Task<Guid> CreateAttributeAsync(CreateAttributeDefinitionRequest req)
        => _repo.CreateAttributeAsync(req);

    public Task<bool> UpdateAttributeAsync(Guid uuid, UpdateAttributeDefinitionRequest req)
        => _repo.UpdateAttributeAsync(uuid, req);

    public Task<AttributeDeleteResult> DeleteAttributeAsync(Guid uuid)
        => _repo.DeleteAttributeAsync(uuid);

    public Task<List<CategoryAttributeModel>> GetCategoryAttributesAsync(int categoryId)
        => _repo.GetCategoryAttributesAsync(categoryId);

    public Task<bool> LinkCategoryAttributeAsync(int categoryId, CreateCategoryAttributeRequest req)
        => _repo.LinkCategoryAttributeAsync(categoryId, req);

    public Task<bool> UnlinkCategoryAttributeAsync(int categoryId, Guid attributeUuid)
        => _repo.UnlinkCategoryAttributeAsync(categoryId, attributeUuid);

    public Task<List<CategoryAttributeModel>> SetCategoryAttributesAsync(int categoryId, SetCategoryAttributesRequest req)
        => _repo.SetCategoryAttributesAsync(categoryId, req);

    public Task<List<VariantAttributeValueModel>> GetVariantAttributeValuesAsync(Guid variantUuid)
        => _repo.GetVariantAttributeValuesAsync(variantUuid);

    public async Task<bool> SetVariantAttributeValuesAsync(Guid variantUuid, SetVariantAttributeValuesRequest req)
    {
        var variantId = await _repo.SetVariantAttributeValuesAsync(variantUuid, req);
        if (variantId.HasValue)
            _jobs.Enqueue<IProductSearchIndexService>(s => s.RebuildForVariantAsync(variantId.Value));
        return variantId.HasValue;
    }
}

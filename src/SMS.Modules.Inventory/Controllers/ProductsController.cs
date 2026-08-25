using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Constants;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Inventory.Controllers;

[ApiController]
//[Authorize]
[RequiresFeature("MODULE_INVENTORY")]
public class ProductsController : ControllerBase
{
    private readonly IInventoryService _service;
    public ProductsController(IInventoryService service) => _service = service;

    // ── Product Categories ────────────────────────────────────────────────────

    [HttpGet("api/product-categories")]
 //   [RequirePermission(PermissionCodes.INVENTORY_VIEW)]
    public async Task<IActionResult> GetCategories()
    {
        var list = await _service.GetCategoriesAsync();
        return Ok(ApiResponse<List<CategoryModel>>.Ok(list));
    }

    [HttpPost("api/product-categories")]
 //   [RequirePermission(PermissionCodes.STOCK_MANAGE)]
    public async Task<IActionResult> CreateCategory([FromBody] CreateCategoryRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Code))
            return BadRequest(ApiResponse.Fail("Name and Code are required."));
        var id = await _service.CreateCategoryAsync(req, User.GetUserId());
        return Ok(ApiResponse<int>.Ok(id, StaticResponseMessage.recordCreatedSuccessfully));
    }

    [HttpPut("api/product-categories/{id:int}")]
    public async Task<IActionResult> UpdateCategory(int id, [FromBody] UpdateCategoryRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(ApiResponse.Fail("Name is required."));
        try
        {
            var updated = await _service.UpdateCategoryAsync(id, req);
            if (!updated) return NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
            return Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully));
        }
        catch (BadRequestException ex)
        {
            return BadRequest(ApiResponse.Fail(ex.Message));
        }
    }

    [HttpDelete("api/product-categories/{id:int}")]
    public async Task<IActionResult> DeleteCategory(int id)
    {
        var result = await _service.DeleteCategoryAsync(id);
        if (!result.Deleted && result.ReferencedProductCount == 0 && result.ReferencedSubCategoryCount == 0)
            return NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
        if (!result.Deleted)
            return Conflict(ApiResponse.Fail("Category has references and cannot be deleted.", result));
        return Ok(ApiResponse.Ok(StaticResponseMessage.recordDeletedSuccessfully));
    }

    [HttpPatch("api/product-categories/{id:int}/deactivate")]
    public async Task<IActionResult> DeactivateCategory(int id)
    {
        var deactivated = await _service.DeactivateCategoryAsync(id);
        if (!deactivated) return NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
        return Ok(ApiResponse.Ok("Category deactivated successfully."));
    }

    [HttpGet("api/product-categories/sub-categories")]
    public async Task<IActionResult> GetSubCategories(
        [FromQuery] int? categoryId,
        [FromQuery] string? search,
        [FromQuery] bool? isActive,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var filter = new SubCategoryListFilter
        {
            CategoryId = categoryId,
            Search     = search,
            IsActive   = isActive,
            Page       = page,
            PageSize   = pageSize
        };
        var result = await _service.GetSubCategoriesAsync(filter);
        return Ok(ApiResponse<PaginatedResponse<SubCategoryListDto>>.Ok(result));
    }

    [HttpGet("api/product-categories/{categoryId:int}/sub-categories")]
    public async Task<IActionResult> GetSubCategoriesByCategory(int categoryId,
        [FromQuery] string? search,
        [FromQuery] bool? isActive,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var filter = new SubCategoryListFilter
        {
            CategoryId = categoryId,
            Search     = search,
            IsActive   = isActive,
            Page       = page,
            PageSize   = pageSize
        };
        var result = await _service.GetSubCategoriesAsync(filter);
        return Ok(ApiResponse<PaginatedResponse<SubCategoryListDto>>.Ok(result));
    }

    [HttpPost("api/product-categories/{categoryId:int}/sub-categories")]
 //   [RequirePermission(PermissionCodes.STOCK_MANAGE)]
    public async Task<IActionResult> CreateSubCategory(int categoryId, [FromBody] CreateSubCategoryRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Code))
            return BadRequest(ApiResponse.Fail("Name and Code are required."));
        var id = await _service.CreateSubCategoryAsync(categoryId, req, User.GetUserId());
        return Ok(ApiResponse<int>.Ok(id, StaticResponseMessage.recordCreatedSuccessfully));
    }

    [HttpPut("api/product-categories/sub-categories/{id:int}")]
    public async Task<IActionResult> UpdateSubCategory(int id, [FromBody] UpdateSubCategoryRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(ApiResponse.Fail("Name is required."));
        try
        {
            var updated = await _service.UpdateSubCategoryAsync(id, req);
            if (!updated) return NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
            return Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully));
        }
        catch (BadRequestException ex)
        {
            return BadRequest(ApiResponse.Fail(ex.Message));
        }
    }

    [HttpDelete("api/product-categories/sub-categories/{id:int}")]
    public async Task<IActionResult> DeleteSubCategory(int id)
    {
        var result = await _service.DeleteSubCategoryAsync(id);
        if (!result.Deleted && result.ReferencedProductCount == 0)
            return NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
        if (!result.Deleted)
            return Conflict(ApiResponse.Fail("Sub-category has references and cannot be deleted.", result));
        return Ok(ApiResponse.Ok(StaticResponseMessage.recordDeletedSuccessfully));
    }

    [HttpPatch("api/product-categories/sub-categories/{id:int}/deactivate")]
    public async Task<IActionResult> DeactivateSubCategory(int id)
    {
        var deactivated = await _service.DeactivateSubCategoryAsync(id);
        if (!deactivated) return NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
        return Ok(ApiResponse.Ok("Sub-category deactivated successfully."));
    }

    // ── Products ──────────────────────────────────────────────────────────────

    [HttpGet("api/products")]
//    [RequirePermission(PermissionCodes.INVENTORY_VIEW)]
    public async Task<IActionResult> GetProducts(
        [FromQuery] int? categoryId,
        [FromQuery] string? status,
        [FromQuery] string? search,
        [FromQuery] bool activeOnly = true,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var filter = new ProductListFilter
        {
            CategoryId = categoryId,
            Status = status,
            Search = search,
            ActiveOnly = activeOnly,
            Page = page,
            PageSize = pageSize
        };
        var result = await _service.GetProductsAsync(filter);
        return Ok(ApiResponse<PaginatedResponse<ProductListItemModel>>.Ok(result));
    }

    // PV-006 — full-text search (SQL Server FREETEXT against the denormalised
    // ProductSearchIndex): product name, variant name, sku, barcode, and searchable attribute
    // values. Empty/omitted q returns a paginated list of every active variant.
    [HttpGet("api/products/search")]
    public async Task<IActionResult> SearchProducts(
        [FromQuery] string? q,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var filter = new ProductSearchFilter { Query = q, Page = page, PageSize = pageSize };
        var result = await _service.SearchProductsAsync(filter);
        return Ok(ApiResponse<PaginatedResponse<ProductSearchResultItem>>.Ok(result));
    }

    [HttpGet("api/products/{id:int}")]
 //   [RequirePermission(PermissionCodes.INVENTORY_VIEW)]
    public async Task<IActionResult> GetProduct(int id)
    {
        var product = await _service.GetProductByIdAsync(id);
        if (product == null) return NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
        return Ok(ApiResponse<ProductDetailModel>.Ok(product));
    }

    [HttpPost("api/products")]
  //  [RequirePermission(PermissionCodes.STOCK_MANAGE)]
    public async Task<IActionResult> CreateProduct([FromBody] CreateProductRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(ApiResponse.Fail("Product name is required."));
        try
        {
            var (id, sku) = await _service.CreateProductAsync(req, User.GetUserId());
            return Ok(ApiResponse<object>.Ok(new { id, sku }, StaticResponseMessage.recordCreatedSuccessfully));
        }
        catch (ConflictException ex)
        {
            return Conflict(ApiResponse.Fail(ex.Message));
        }
        catch (BadRequestException ex)
        {
            return BadRequest(ApiResponse.Fail(ex.Message));
        }
    }

    [HttpPatch("api/products/{id:int}")]
  //  [RequirePermission(PermissionCodes.STOCK_MANAGE)]
    public async Task<IActionResult> PatchProduct(int id, [FromBody] PatchProductRequest req)
    {
        var updated = await _service.PatchProductAsync(id, req);
        if (!updated) return NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
        return Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully));
    }

    [HttpDelete("api/products/{id:int}")]
 //   [RequirePermission(PermissionCodes.STOCK_MANAGE)]
    public async Task<IActionResult> DeleteProduct(int id)
    {
        var deleted = await _service.SoftDeleteProductAsync(id);
        if (!deleted) return NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
        return Ok(ApiResponse.Ok(StaticResponseMessage.recordDeletedSuccessfully));
    }

    // ── Product stock across warehouses ───────────────────────────────────────

    [HttpGet("api/products/{id:int}/stock")]
  //  [RequirePermission(PermissionCodes.INVENTORY_VIEW)]
    public async Task<IActionResult> GetProductStock(int id)
    {
        var stock = await _service.GetProductStockAsync(id);
        return Ok(ApiResponse<List<ProductStockModel>>.Ok(stock));
    }

    // PV-005 — SUM(qty_on_hand)/SUM(qty_reserved)/SUM(qty_available) across every variant of
    // this product, computed on the fly.
    [HttpGet("api/products/{id:int}/stock-summary")]
    public async Task<IActionResult> GetProductStockSummary(int id)
    {
        var summary = await _service.GetProductStockSummaryAsync(id);
        if (summary is null) return NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
        return Ok(ApiResponse<ProductStockSummaryModel>.Ok(summary));
    }

    // One row per warehouse for a single variant (bins summed) — for pickers like MIR line
    // creation that need "how much of THIS variant is available in THIS warehouse", where
    // GetProductStock's per-bin/per-variant rows would otherwise look like duplicate warehouses.
    [HttpGet("api/variants/{variantUuid:guid}/stock")]
    public async Task<IActionResult> GetVariantStock(Guid variantUuid)
    {
        var stock = await _service.GetVariantStockByWarehouseAsync(variantUuid);
        return Ok(ApiResponse<List<VariantWarehouseStockModel>>.Ok(stock));
    }

    // ── Variants (PV-004) ─────────────────────────────────────────────────────

    // GRN barcode scan: resolves a scanned code straight to its variant, product, and price.
    [HttpGet("api/variants/lookup")]
    public async Task<IActionResult> LookupVariantByBarcode([FromQuery] string barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode))
            return BadRequest(ApiResponse.Fail("Barcode is required."));
        var variant = await _service.GetVariantByBarcodeAsync(barcode);
        if (variant is null) return NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
        return Ok(ApiResponse<VariantLookupModel>.Ok(variant));
    }

    // ── Variant CRUD (PV-007) ────────────────────────────────────────────────

    [HttpPost("api/products/{id:int}/variants")]
    public async Task<IActionResult> CreateVariant(int id, [FromBody] CreateProductVariantRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.VariantName))
            return BadRequest(ApiResponse.Fail("Variant name is required."));
        try
        {
            var result = await _service.CreateVariantAsync(id, req, User.GetUserId());
            if (result is null) return NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
            return Ok(ApiResponse<CreateVariantResult>.Ok(result, StaticResponseMessage.recordCreatedSuccessfully));
        }
        catch (ConflictException ex)
        {
            return Conflict(ApiResponse.Fail(ex.Message));
        }
        catch (BadRequestException ex)
        {
            return BadRequest(ApiResponse.Fail(ex.Message));
        }
    }

    [HttpPatch("api/variants/{uuid:guid}")]
    public async Task<IActionResult> UpdateVariant(Guid uuid, [FromBody] CreateProductVariantRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.VariantName))
            return BadRequest(ApiResponse.Fail("Variant name is required."));
        try
        {
            var updated = await _service.UpdateVariantAsync(uuid, req);
            if (!updated) return NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
            return Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully));
        }
        catch (ConflictException ex)
        {
            return Conflict(ApiResponse.Fail(ex.Message));
        }
        catch (BadRequestException ex)
        {
            return BadRequest(ApiResponse.Fail(ex.Message));
        }
    }

    // Soft-deletes (is_active=false) if the variant has ever been transacted (any PO/GRN/MIR
    // line, or a local inventory/ledger/adjustment row); hard-deletes the row otherwise.
    [HttpDelete("api/variants/{uuid:guid}")]
    public async Task<IActionResult> DeleteVariant(Guid uuid)
    {
        var result = await _service.DeleteVariantAsync(uuid);
        if (!result.Found) return NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));

        var message = result.SoftDeleted
            ? "Variant has been transacted — deactivated instead of deleted."
            : StaticResponseMessage.recordDeletedSuccessfully;
        return Ok(ApiResponse<object>.Ok(new { softDeleted = result.SoftDeleted }, message));
    }

    // PV-006 — admin-triggered full rebuild of ProductSearchIndex (initial population, or
    // recovery after a bulk data change). Runs inline rather than via Hangfire, matching the
    // existing supplier-scorecard "recalculate" admin-action convention.
    [HttpPost("api/products/search-index/rebuild")]
    [RequirePermission(PermissionCodes.SYSTEM_CONFIGURE)]
    public async Task<IActionResult> RebuildSearchIndex()
    {
        await _service.RebuildSearchIndexAsync();
        return Ok(ApiResponse.Ok("Product search index rebuild completed."));
    }
}

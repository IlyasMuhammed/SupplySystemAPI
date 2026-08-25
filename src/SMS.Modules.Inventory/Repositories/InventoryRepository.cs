using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Inventory.Repositories;

internal sealed class InventoryRepository : IInventoryRepository
{
    private readonly InventoryDbContext _db;
    private readonly IInventoryLedgerService _ledger;

    private const decimal QTY_THRESHOLD   = 100m;
    private const decimal VALUE_THRESHOLD = 50_000m;

    public InventoryRepository(InventoryDbContext db, IInventoryLedgerService ledger)
    {
        _db     = db;
        _ledger = ledger;
    }

    // ── Categories ────────────────────────────────────────────────────────────

    public async Task<List<CategoryModel>> GetCategoriesAsync()
    {
        return await _db.ProductCategories
            .Where(c => c.IsActive)
            .OrderBy(c => c.Name)
            .Select(c => new CategoryModel
            {
                Id          = c.Id,
                Name        = c.Name,
                Code        = c.Code,
                Description = c.Description,
                IsActive    = c.IsActive,
                CreatedDate = c.CreatedDate,
                SubCategories = c.SubCategories
                    .Where(s => s.IsActive)
                    .OrderBy(s => s.Name)
                    .Select(s => new SubCategoryModel
                    {
                        Id          = s.Id,
                        CategoryId  = s.CategoryId,
                        Name        = s.Name,
                        Code        = s.Code,
                        Description = s.Description,
                        IsActive    = s.IsActive
                    }).ToList()
            })
            .ToListAsync();
    }

    public async Task<CategoryModel?> GetCategoryByIdAsync(int id)
    {
        return await _db.ProductCategories
            .Where(c => c.Id == id)
            .Select(c => new CategoryModel
            {
                Id          = c.Id,
                Name        = c.Name,
                Code        = c.Code,
                Description = c.Description,
                IsActive    = c.IsActive,
                CreatedDate = c.CreatedDate,
                SubCategories = c.SubCategories
                    .OrderBy(s => s.Name)
                    .Select(s => new SubCategoryModel
                    {
                        Id          = s.Id,
                        CategoryId  = s.CategoryId,
                        Name        = s.Name,
                        Code        = s.Code,
                        Description = s.Description,
                        IsActive    = s.IsActive
                    }).ToList()
            })
            .FirstOrDefaultAsync();
    }

    public async Task<int> CreateCategoryAsync(CreateCategoryRequest req, int userId)
    {
        var entity = new ProductCategory
        {
            Name        = req.Name,
            Code        = req.Code,
            Description = req.Description,
            IsActive    = true,
            CreatedDate = DateTime.UtcNow
        };
        _db.ProductCategories.Add(entity);
        await _db.SaveChangesAsync();
        return entity.Id;
    }

    public async Task<int> CreateSubCategoryAsync(int categoryId, CreateSubCategoryRequest req, int userId)
    {
        var entity = new ProductSubCategory
        {
            CategoryId  = categoryId,
            Name        = req.Name,
            Code        = req.Code,
            Description = req.Description,
            IsActive    = true,
            CreatedDate = DateTime.UtcNow
        };
        _db.ProductSubCategories.Add(entity);
        await _db.SaveChangesAsync();
        return entity.Id;
    }

    public async Task<bool> UpdateCategoryAsync(int id, UpdateCategoryRequest req)
    {
        var entity = await _db.ProductCategories.FindAsync(id);
        if (entity == null) return false;

        var duplicate = await _db.ProductCategories
            .AnyAsync(c => c.Id != id && c.Name.ToLower() == req.Name.Trim().ToLower());
        if (duplicate)
            throw new BadRequestException("A category with this name already exists.");

        entity.Name        = req.Name.Trim();
        entity.Description = req.Description;
        entity.IsActive    = req.IsActive;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<CategoryDeleteResult> DeleteCategoryAsync(int id)
    {
        var entity = await _db.ProductCategories.FindAsync(id);
        if (entity == null)
            return new CategoryDeleteResult { Deleted = false };

        var productCount    = await _db.Products.CountAsync(p => p.CategoryId == id);
        var subCategoryCount = await _db.ProductSubCategories.CountAsync(s => s.CategoryId == id);

        if (productCount > 0 || subCategoryCount > 0)
            return new CategoryDeleteResult
            {
                Deleted                   = false,
                ReferencedProductCount    = productCount,
                ReferencedSubCategoryCount = subCategoryCount
            };

        _db.ProductCategories.Remove(entity);
        await _db.SaveChangesAsync();
        return new CategoryDeleteResult { Deleted = true };
    }

    public async Task<bool> DeactivateCategoryAsync(int id)
    {
        var entity = await _db.ProductCategories.FindAsync(id);
        if (entity == null) return false;
        entity.IsActive = false;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> UpdateSubCategoryAsync(int subId, UpdateSubCategoryRequest req)
    {
        var entity = await _db.ProductSubCategories.FindAsync(subId);
        if (entity == null) return false;

        var duplicate = await _db.ProductSubCategories
            .AnyAsync(s => s.Id != subId && s.CategoryId == req.CategoryId
                        && s.IsActive && s.Name.ToLower() == req.Name.Trim().ToLower());
        if (duplicate)
            throw new BadRequestException("A sub-category with this name already exists under the selected parent category.");

        entity.CategoryId  = req.CategoryId;
        entity.Name        = req.Name.Trim();
        entity.Description = req.Description;
        entity.IsActive    = req.IsActive;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<SubCategoryDeleteResult> DeleteSubCategoryAsync(int subId)
    {
        var entity = await _db.ProductSubCategories.FindAsync(subId);
        if (entity == null)
            return new SubCategoryDeleteResult { Deleted = false };

        var productCount = await _db.Products.CountAsync(p => p.SubCategoryId == subId);
        if (productCount > 0)
            return new SubCategoryDeleteResult { Deleted = false, ReferencedProductCount = productCount };

        _db.ProductSubCategories.Remove(entity);
        await _db.SaveChangesAsync();
        return new SubCategoryDeleteResult { Deleted = true };
    }

    public async Task<bool> DeactivateSubCategoryAsync(int subId)
    {
        var entity = await _db.ProductSubCategories.FindAsync(subId);
        if (entity == null) return false;
        entity.IsActive = false;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<PaginatedResponse<SubCategoryListDto>> GetSubCategoriesAsync(SubCategoryListFilter filter)
    {
        var query = _db.ProductSubCategories.AsQueryable();

        if (filter.CategoryId.HasValue)
            query = query.Where(s => s.CategoryId == filter.CategoryId.Value);

        if (filter.IsActive.HasValue)
            query = query.Where(s => s.IsActive == filter.IsActive.Value);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.ToLower();
            query = query.Where(s =>
                s.Name.ToLower().Contains(search) ||
                (s.Code != null && s.Code.ToLower().Contains(search)) ||
                s.Category.Name.ToLower().Contains(search));
        }

        var total    = await query.CountAsync();
        var page     = filter.Page < 1     ? 1  : filter.Page;
        var pageSize = filter.PageSize < 1 ? 20 : filter.PageSize;

        var items = await query
            .OrderBy(s => s.Category.Name)
            .ThenBy(s => s.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(s => new SubCategoryListDto
            {
                SubCategoryId      = s.Id,
                SubCategoryCode    = s.Code ?? string.Empty,
                SubCategoryName    = s.Name,
                ParentCategoryId   = s.CategoryId,
                ParentCategoryName = s.Category.Name,
                IsActive           = s.IsActive,
                ProductCount       = s.Products.Count(p => p.IsActive)
            })
            .ToListAsync();

        return new PaginatedResponse<SubCategoryListDto>
        {
            Data         = items,
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling((double)total / pageSize)
        };
    }

    public async Task<bool> CategoryExistsAsync(int id)
    {
        return await _db.ProductCategories.AnyAsync(c => c.Id == id && c.IsActive);
    }

    // ── Products ──────────────────────────────────────────────────────────────

    public async Task<PaginatedResponse<ProductListItemModel>> GetProductsAsync(ProductListFilter filter)
    {
        var query = _db.Products.AsQueryable();

        if (filter.ActiveOnly)
            query = query.Where(p => p.IsActive);

        if (filter.CategoryId.HasValue)
            query = query.Where(p => p.CategoryId == filter.CategoryId.Value);

        if (!string.IsNullOrWhiteSpace(filter.Status))
            query = query.Where(p => p.Status == filter.Status);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.ToLower();
            query = query.Where(p =>
                p.Name.ToLower().Contains(search) ||
                p.Sku.ToLower().Contains(search)  ||
                (p.ShortName != null && p.ShortName.ToLower().Contains(search)) ||
                p.Variants.Any(v => v.Barcode != null && v.Barcode.ToLower().Contains(search)));
        }

        var total = await query.CountAsync();

        var page     = filter.Page < 1     ? 1  : filter.Page;
        var pageSize = filter.PageSize < 1 ? 20 : filter.PageSize;

        var items = await query
            .OrderByDescending(p => p.CreatedDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new ProductListItemModel
            {
                Id              = p.Id,
                Uuid            = p.Uuid,
                Sku             = p.Sku,
                Name            = p.Name,
                ShortName       = p.ShortName,
                Brand           = p.Brand,
                CategoryId      = p.CategoryId,
                CategoryName    = p.Category != null ? p.Category.Name : null,
                SubCategoryId   = p.SubCategoryId,
                SubCategoryName = p.SubCategory != null ? p.SubCategory.Name : null,
                UomCode         = p.UomCode,
                Status          = p.Status,
                IsBatchTracked  = p.IsBatchTracked,
                IsSerialTracked = p.IsSerialTracked,
                CreatedDate     = p.CreatedDate,
                VariantCount    = p.Variants.Count,
                ImageUrl        = p.ImageUrl,
                DefaultVariantPurchasePrice = p.Variants
                    .Where(v => v.IsDefault)
                    .Select(v => (decimal?)v.PurchasePrice)
                    .FirstOrDefault()
            })
            .ToListAsync();

        return new PaginatedResponse<ProductListItemModel>
        {
            Data         = items,
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling((double)total / pageSize)
        };
    }

    public async Task<ProductDetailModel?> GetProductByIdAsync(int id)
    {
        return await _db.Products
            .Where(p => p.Id == id)
            .Select(p => new ProductDetailModel
            {
                Id                  = p.Id,
                Uuid                = p.Uuid,
                Sku                 = p.Sku,
                Name                = p.Name,
                ShortName           = p.ShortName,
                Description         = p.Description,
                Brand               = p.Brand,
                CategoryId          = p.CategoryId,
                CategoryName        = p.Category != null ? p.Category.Name : null,
                SubCategoryId       = p.SubCategoryId,
                SubCategoryName     = p.SubCategory != null ? p.SubCategory.Name : null,
                UomCode             = p.UomCode,
                WeightKg            = p.WeightKg,
                Dimensions          = p.Dimensions,
                ShelfLifeDays       = p.ShelfLifeDays,
                IsBatchTracked      = p.IsBatchTracked,
                IsSerialTracked     = p.IsSerialTracked,
                ReorderPoint        = p.ReorderPoint,
                ReorderQty          = p.ReorderQty,
                MinStockLevel       = p.MinStockLevel,
                MaxStockLevel       = p.MaxStockLevel,
                LeadTimeDays        = p.LeadTimeDays,
                PreferredSupplierId = p.PreferredSupplierId,
                Notes               = p.Notes,
                ImageUrl            = p.ImageUrl,
                Status              = p.Status,
                CreatedDate         = p.CreatedDate,
                UpdatedDate         = p.UpdatedDate,
                CreatedBy           = p.CreatedBy,
                VariantCount        = p.Variants.Count,
                DefaultVariantPurchasePrice = p.Variants
                    .Where(v => v.IsDefault)
                    .Select(v => (decimal?)v.PurchasePrice)
                    .FirstOrDefault(),
                Variants = p.Variants
                    .OrderBy(v => v.SortOrder ?? 0).ThenBy(v => v.Id)
                    .Select(v => new ProductVariantModel
                    {
                        Id                = v.Id,
                        Uuid              = v.Uuid,
                        Sku               = v.Sku,
                        VariantName       = v.VariantName,
                        Barcode           = v.Barcode,
                        PurchasePrice     = v.PurchasePrice,
                        SellingPrice      = v.SellingPrice,
                        LastPurchasePrice = v.LastPurchasePrice,
                        WeightKg          = v.WeightKg,
                        Dimensions        = v.Dimensions,
                        IsDefault         = v.IsDefault,
                        IsActive          = v.IsActive,
                        ReorderPoint      = v.ReorderPoint,
                        SortOrder         = v.SortOrder,
                        CreatedDate       = v.CreatedDate
                    }).ToList()
            })
            .FirstOrDefaultAsync();
    }

    // PV-004 — GRN barcode scan resolves straight to a variant (not a bare product), since price
    // and receiving are always variant-scoped once PV-001 is in place.
    public async Task<VariantLookupModel?> GetVariantByBarcodeAsync(string barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode)) return null;

        return await _db.ProductVariants
            .Where(v => v.Barcode == barcode && v.IsActive)
            .Select(v => new VariantLookupModel
            {
                Uuid          = v.Uuid,
                Sku           = v.Sku,
                VariantName   = v.VariantName,
                Barcode       = v.Barcode,
                PurchasePrice = v.PurchasePrice,
                ProductId     = v.ProductId,
                ProductUuid   = v.Product.Uuid,
                ProductName   = v.Product.Name
            })
            .FirstOrDefaultAsync();
    }

    // PV-006 — full-text search against the denormalised ProductSearchIndex (kept current by
    // IProductSearchIndexService). FREETEXT does natural-language word matching, so a
    // multi-word query like "Dell i7" matches rows containing both words in any order/form.
    // Empty query -> unfiltered, paginated list of every active variant.
    public async Task<PaginatedResponse<ProductSearchResultItem>> SearchProductsAsync(ProductSearchFilter filter)
    {
        var query = _db.ProductSearchIndexEntries.Where(x => x.IsActive).AsQueryable();

        var hasQuery = !string.IsNullOrWhiteSpace(filter.Query);
        if (hasQuery)
            query = query.Where(x => EF.Functions.FreeText(x.SearchableText, filter.Query!));

        var total    = await query.CountAsync();
        var page     = filter.Page < 1 ? 1 : filter.Page;
        var pageSize = filter.PageSize < 1 ? 20 : filter.PageSize;

        var rows = await query
            .OrderBy(x => x.ProductName).ThenBy(x => x.VariantName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new
            {
                x.VariantId, x.ProductId, x.ProductName, x.ProductCode, x.Sku,
                x.Barcode, x.VariantName, x.CategoryName, x.Brand
            })
            .ToListAsync();

        var variantIds = rows.Select(r => r.VariantId).ToList();

        // Uuid/price aren't denormalised into the index — resolved here in one batch instead of
        // widening the index with columns that only ever matter to the handful of result rows
        // actually returned.
        var variantExtras = await _db.ProductVariants
            .Where(v => variantIds.Contains(v.Id))
            .Select(v => new { v.Id, v.Uuid, v.PurchasePrice, ProductUuid = v.Product.Uuid })
            .ToDictionaryAsync(v => v.Id);

        var attributesByVariant = (await _db.VariantAttributeValues
            .Where(av => variantIds.Contains(av.VariantId) && av.Attribute.IsSearchable)
            .Select(av => new
            {
                av.VariantId, av.Attribute.Uuid, av.Attribute.AttributeName,
                av.Attribute.DisplayName, av.Attribute.DataType, av.Value
            })
            .ToListAsync())
            .GroupBy(a => a.VariantId)
            .ToDictionary(g => g.Key, g => g.Select(a => new VariantAttributeValueModel
            {
                AttributeUuid = a.Uuid,
                AttributeName = a.AttributeName,
                DisplayName   = a.DisplayName,
                DataType      = a.DataType,
                Value         = a.Value
            }).ToList());

        var queryTerms = hasQuery
            ? filter.Query!.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

        var items = rows
            .Where(r => variantExtras.ContainsKey(r.VariantId))  // drop rows whose variant has since been deleted
            .Select(r =>
            {
                var extra = variantExtras[r.VariantId];
                var attrs = attributesByVariant.TryGetValue(r.VariantId, out var a) ? a : [];
                var haystack = string.Join(" ", new[] { r.ProductName, r.VariantName, r.Sku, r.Barcode }
                    .Concat(attrs.Select(x => x.Value))
                    .Where(s => !string.IsNullOrEmpty(s)));
                var matchedTerms = queryTerms
                    .Where(t => haystack.Contains(t, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new ProductSearchResultItem
                {
                    VariantUuid   = extra.Uuid,
                    Sku           = r.Sku,
                    VariantName   = r.VariantName,
                    Barcode       = r.Barcode,
                    PurchasePrice = extra.PurchasePrice,
                    ProductId     = r.ProductId,
                    ProductUuid   = extra.ProductUuid,
                    ProductName   = r.ProductName,
                    ProductCode   = r.ProductCode,
                    CategoryName  = r.CategoryName,
                    Brand         = r.Brand,
                    MatchedTerms  = matchedTerms,
                    Attributes    = attrs
                };
            })
            .ToList();

        return new PaginatedResponse<ProductSearchResultItem>
        {
            Data         = items,
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling((double)total / pageSize)
        };
    }

    public async Task<(int id, string sku)> CreateProductAsync(CreateProductRequest req, int userId)
    {
        if (req.CategoryId.HasValue && !await CategoryExistsAsync(req.CategoryId.Value))
            throw new BadRequestException($"Category with ID {req.CategoryId.Value} does not exist.");

        if (await _db.Products.AnyAsync(p => p.IsActive && p.Name.ToLower() == req.Name.Trim().ToLower()))
            throw new ConflictException($"A product named '{req.Name.Trim()}' already exists. Use a unique name.");

        string sku;
        if (string.IsNullOrWhiteSpace(req.Sku))
        {
            sku = await GenerateSkuAsync();
        }
        else
        {
            if (await SkuExistsAsync(req.Sku))
                throw new ConflictException("A product with this SKU already exists.");
            sku = req.Sku;
        }

        var entity = new Product
        {
            Sku                 = sku,
            Name                = req.Name.Trim(),
            ShortName           = req.ShortName,
            Description         = req.Description,
            CategoryId          = req.CategoryId,
            SubCategoryId       = req.SubCategoryId,
            Brand               = req.Brand,
            UomCode             = req.UomCode,
            WeightKg            = req.WeightKg,
            Dimensions          = req.Dimensions,
            ShelfLifeDays       = req.ShelfLifeDays,
            IsBatchTracked      = req.IsBatchTracked,
            IsSerialTracked     = req.IsSerialTracked,
            ReorderPoint        = req.ReorderPoint,
            ReorderQty          = req.ReorderQty,
            MinStockLevel       = req.MinStockLevel,
            MaxStockLevel       = req.MaxStockLevel,
            LeadTimeDays        = req.LeadTimeDays,
            PreferredSupplierId = req.PreferredSupplierId,
            Notes               = req.Notes,
            ImageUrl            = req.ImageUrl,
            Status              = "ACTIVE",
            IsActive            = true,
            CreatedDate         = DateTime.UtcNow,
            CreatedBy           = userId
        };

        entity.Variants = await BuildVariantsAsync(req, sku, userId);

        _db.Products.Add(entity);
        await _db.SaveChangesAsync();
        return (entity.Id, entity.Sku);
    }

    // PV-001 — every product must end up with at least one variant, and exactly one of them
    // is_default=true (FSD Addendum 26 §1.2/§3.1). Explicit Variants win when supplied (e.g. the
    // Dell Latitude's two SKUs); otherwise a single default variant is auto-created from
    // PurchasePrice/SellingPrice/Barcode on the request, so simple products (e.g. Cement) never
    // need the caller to think about variants at all — same code path either way downstream.
    private async Task<List<ProductVariant>> BuildVariantsAsync(CreateProductRequest req, string productSku, int userId)
    {
        var now = DateTime.UtcNow;

        if (req.Variants is { Count: > 0 })
        {
            var defaultCount = req.Variants.Count(v => v.IsDefault);
            if (defaultCount != 1)
                throw new BadRequestException("Exactly one variant must be marked as the default (is_default=true).");

            var variants = new List<ProductVariant>();
            for (var i = 0; i < req.Variants.Count; i++)
            {
                var v = req.Variants[i];
                if (string.IsNullOrWhiteSpace(v.VariantName))
                    throw new BadRequestException("Each variant requires a variant name.");

                var variantSku = string.IsNullOrWhiteSpace(v.Sku) ? $"{productSku}-{i + 1}" : v.Sku.Trim();

                if (await _db.ProductVariants.AnyAsync(x => x.Sku == variantSku))
                    throw new ConflictException($"A variant with SKU '{variantSku}' already exists.");
                if (!string.IsNullOrWhiteSpace(v.Barcode) && await _db.ProductVariants.AnyAsync(x => x.Barcode == v.Barcode))
                    throw new ConflictException($"A variant with barcode '{v.Barcode}' already exists.");

                variants.Add(new ProductVariant
                {
                    Sku           = variantSku,
                    VariantName   = v.VariantName.Trim(),
                    Barcode       = v.Barcode,
                    PurchasePrice = v.PurchasePrice,
                    SellingPrice  = v.SellingPrice,
                    WeightKg      = v.Weight,
                    Dimensions    = v.Dimensions,
                    IsDefault     = v.IsDefault,
                    IsActive      = true,
                    ReorderPoint  = v.ReorderPoint,
                    SortOrder     = v.SortOrder ?? i,
                    CreatedDate   = now,
                    CreatedBy     = userId
                });
            }
            return variants;
        }

        // No explicit variants — auto-create the single default (FSD §1.2, "backward compatibility").
        if (!req.PurchasePrice.HasValue)
            throw new BadRequestException("PurchasePrice is required when no variants are specified.");

        var defaultSku = $"{productSku}-DEFAULT";
        if (await _db.ProductVariants.AnyAsync(x => x.Sku == defaultSku))
            throw new ConflictException($"A variant with SKU '{defaultSku}' already exists.");
        if (!string.IsNullOrWhiteSpace(req.Barcode) && await _db.ProductVariants.AnyAsync(x => x.Barcode == req.Barcode))
            throw new ConflictException($"A variant with barcode '{req.Barcode}' already exists.");

        return
        [
            new ProductVariant
            {
                Sku           = defaultSku,
                VariantName   = req.Name.Trim(),
                Barcode       = req.Barcode,
                PurchasePrice = req.PurchasePrice.Value,
                SellingPrice  = req.SellingPrice,
                WeightKg      = req.WeightKg,
                Dimensions    = req.Dimensions,
                IsDefault     = true,
                IsActive      = true,
                SortOrder     = 0,
                CreatedDate   = now,
                CreatedBy     = userId
            }
        ];
    }

    public async Task<bool> PatchProductAsync(int id, PatchProductRequest req)
    {
        var entity = await _db.Products.FindAsync(id);
        if (entity == null) return false;

        if (req.Name is not null)
        {
            var trimmed = req.Name.Trim();
            if (await _db.Products.AnyAsync(p => p.Id != id && p.IsActive && p.Name.ToLower() == trimmed.ToLower()))
                throw new ConflictException($"A product named '{trimmed}' already exists. Use a unique name.");
            entity.Name = trimmed;
        }
        if (req.ShortName         is not null) entity.ShortName         = req.ShortName;
        if (req.Description       is not null) entity.Description       = req.Description;
        if (req.CategoryId        .HasValue)   entity.CategoryId        = req.CategoryId;
        if (req.SubCategoryId     .HasValue)   entity.SubCategoryId     = req.SubCategoryId;
        if (req.Brand             is not null) entity.Brand             = req.Brand;
        if (req.UomCode           is not null) entity.UomCode           = req.UomCode;
        if (req.WeightKg          .HasValue)   entity.WeightKg          = req.WeightKg;
        if (req.Dimensions        is not null) entity.Dimensions        = req.Dimensions;
        if (req.ShelfLifeDays     .HasValue)   entity.ShelfLifeDays     = req.ShelfLifeDays;
        if (req.IsBatchTracked    .HasValue)   entity.IsBatchTracked    = req.IsBatchTracked.Value;
        if (req.IsSerialTracked   .HasValue)   entity.IsSerialTracked   = req.IsSerialTracked.Value;
        if (req.ReorderPoint      .HasValue)   entity.ReorderPoint      = req.ReorderPoint;
        if (req.ReorderQty        .HasValue)   entity.ReorderQty        = req.ReorderQty;
        if (req.MinStockLevel     .HasValue)   entity.MinStockLevel     = req.MinStockLevel;
        if (req.MaxStockLevel     .HasValue)   entity.MaxStockLevel     = req.MaxStockLevel;
        if (req.LeadTimeDays      .HasValue)   entity.LeadTimeDays      = req.LeadTimeDays;
        if (req.PreferredSupplierId.HasValue)  entity.PreferredSupplierId = req.PreferredSupplierId;
        if (req.Notes             is not null) entity.Notes             = req.Notes;
        if (req.ImageUrl          is not null) entity.ImageUrl          = req.ImageUrl;
        if (req.Status            is not null) entity.Status            = req.Status;

        entity.UpdatedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> SoftDeleteProductAsync(int id)
    {
        var entity = await _db.Products.FindAsync(id);
        if (entity == null) return false;

        entity.IsActive    = false;
        entity.Status      = "INACTIVE";
        entity.UpdatedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> SkuExistsAsync(string sku)
    {
        return await _db.Products.AnyAsync(p => p.Sku == sku);
    }

    // ── Variants (PV-007) ────────────────────────────────────────────────────

    public async Task<(Guid uuid, int id, string sku)?> CreateVariantAsync(int productId, CreateProductVariantRequest req, int userId)
    {
        var product = await _db.Products.FindAsync(productId);
        if (product is null) return null;

        if (string.IsNullOrWhiteSpace(req.VariantName))
            throw new BadRequestException("Variant name is required.");

        var existingCount = await _db.ProductVariants.CountAsync(v => v.ProductId == productId);
        var variantSku = string.IsNullOrWhiteSpace(req.Sku) ? $"{product.Sku}-{existingCount + 1}" : req.Sku.Trim();

        if (await _db.ProductVariants.AnyAsync(x => x.Sku == variantSku))
            throw new ConflictException($"A variant with SKU '{variantSku}' already exists.");
        if (!string.IsNullOrWhiteSpace(req.Barcode) && await _db.ProductVariants.AnyAsync(x => x.Barcode == req.Barcode))
            throw new ConflictException($"A variant with barcode '{req.Barcode}' already exists.");

        // Only one is_default=true per product — setting this one unsets whichever variant
        // currently holds it (FSD §3.1).
        if (req.IsDefault)
            await UnsetExistingDefaultAsync(productId);

        var variant = new ProductVariant
        {
            ProductId     = productId,
            Sku           = variantSku,
            VariantName   = req.VariantName.Trim(),
            Barcode       = req.Barcode,
            PurchasePrice = req.PurchasePrice,
            SellingPrice  = req.SellingPrice,
            WeightKg      = req.Weight,
            Dimensions    = req.Dimensions,
            IsDefault     = req.IsDefault,
            IsActive      = true,
            ReorderPoint  = req.ReorderPoint,
            SortOrder     = req.SortOrder ?? existingCount,
            CreatedDate   = DateTime.UtcNow,
            CreatedBy     = userId
        };
        _db.ProductVariants.Add(variant);
        await _db.SaveChangesAsync();
        return (variant.Uuid, variant.Id, variant.Sku);
    }

    public async Task<int?> UpdateVariantAsync(Guid variantUuid, CreateProductVariantRequest req)
    {
        var variant = await _db.ProductVariants.FirstOrDefaultAsync(v => v.Uuid == variantUuid);
        if (variant is null) return null;

        if (string.IsNullOrWhiteSpace(req.VariantName))
            throw new BadRequestException("Variant name is required.");

        var newSku = string.IsNullOrWhiteSpace(req.Sku) ? variant.Sku : req.Sku.Trim();
        if (newSku != variant.Sku && await _db.ProductVariants.AnyAsync(x => x.Sku == newSku && x.Id != variant.Id))
            throw new ConflictException($"A variant with SKU '{newSku}' already exists.");

        var newBarcode = string.IsNullOrWhiteSpace(req.Barcode) ? null : req.Barcode.Trim();
        if (newBarcode is not null && newBarcode != variant.Barcode &&
            await _db.ProductVariants.AnyAsync(x => x.Barcode == newBarcode && x.Id != variant.Id))
            throw new ConflictException($"A variant with barcode '{newBarcode}' already exists.");

        if (req.IsDefault && !variant.IsDefault)
            await UnsetExistingDefaultAsync(variant.ProductId, exceptVariantId: variant.Id);

        variant.Sku           = newSku;
        variant.VariantName   = req.VariantName.Trim();
        variant.Barcode       = newBarcode;
        variant.PurchasePrice = req.PurchasePrice;
        variant.SellingPrice  = req.SellingPrice;
        variant.WeightKg      = req.Weight;
        variant.Dimensions    = req.Dimensions;
        variant.IsDefault     = req.IsDefault;
        variant.ReorderPoint  = req.ReorderPoint;
        variant.SortOrder     = req.SortOrder ?? variant.SortOrder;

        await _db.SaveChangesAsync();
        return variant.Id;
    }

    private async Task UnsetExistingDefaultAsync(int productId, int? exceptVariantId = null)
    {
        var currentDefaults = await _db.ProductVariants
            .Where(v => v.ProductId == productId && v.IsDefault && v.Id != exceptVariantId)
            .ToListAsync();
        foreach (var v in currentDefaults) v.IsDefault = false;
    }

    public async Task<bool> VariantHasLocalReferencesAsync(Guid variantUuid)
    {
        var variant = await _db.ProductVariants.FirstOrDefaultAsync(v => v.Uuid == variantUuid);
        if (variant is null) return false;

        return await _db.InventoryItems.AnyAsync(i => i.VariantId == variant.Id)
            || await _db.InventoryLedgerEntries.AnyAsync(l => l.VariantId == variant.Id)
            || await _db.StockAdjustments.AnyAsync(a => a.VariantId == variant.Id);
    }

    public async Task<(VariantDeleteOutcome outcome, int? variantId)> DeleteVariantAsync(Guid variantUuid, bool softDelete)
    {
        var variant = await _db.ProductVariants.FirstOrDefaultAsync(v => v.Uuid == variantUuid);
        if (variant is null) return (VariantDeleteOutcome.NotFound, null);

        var otherActiveVariants = await _db.ProductVariants
            .Where(v => v.ProductId == variant.ProductId && v.Id != variant.Id && v.IsActive)
            .ToListAsync();
        if (otherActiveVariants.Count == 0)
            return (VariantDeleteOutcome.IsLastVariant, variant.Id);

        var wasDefault  = variant.IsDefault;
        var variantId   = variant.Id;

        if (softDelete)
        {
            variant.IsActive = false;
            variant.IsDefault = false;
        }
        else
        {
            _db.ProductVariants.Remove(variant);
        }

        // Deleting the default variant must not leave the product with zero defaults — promote
        // the next remaining active variant (FSD §3.1's "exactly one default" invariant).
        if (wasDefault)
        {
            var promoted = otherActiveVariants.OrderBy(v => v.SortOrder ?? int.MaxValue).ThenBy(v => v.Id).First();
            promoted.IsDefault = true;
        }

        await _db.SaveChangesAsync();
        return (VariantDeleteOutcome.Deleted, variantId);
    }

    // ── Warehouses ────────────────────────────────────────────────────────────

    public async Task<List<WarehouseModel>> GetWarehousesAsync()
    {
        return await _db.Warehouses
            .Where(w => w.IsActive)
            .OrderBy(w => w.Name)
            .Select(w => new WarehouseModel
            {
                Id            = w.Id,
                Uuid          = w.Uuid,
                Code          = w.Code,
                Name          = w.Name,
                Address       = w.Address,
                City          = w.City,
                Country       = w.Country,
                ContactName   = w.ContactName,
                ContactPhone  = w.ContactPhone,
                GoogleMapsUrl = w.GoogleMapsUrl,
                Latitude      = w.Latitude,
                Longitude     = w.Longitude,
                IsActive      = w.IsActive,
                CreatedDate   = w.CreatedDate
            })
            .ToListAsync();
    }

    public async Task<WarehouseModel?> GetWarehouseByIdAsync(int id)
    {
        return await _db.Warehouses
            .Where(w => w.Id == id)
            .Select(w => new WarehouseModel
            {
                Id            = w.Id,
                Uuid          = w.Uuid,
                Code          = w.Code,
                Name          = w.Name,
                Address       = w.Address,
                City          = w.City,
                Country       = w.Country,
                ContactName   = w.ContactName,
                ContactPhone  = w.ContactPhone,
                GoogleMapsUrl = w.GoogleMapsUrl,
                Latitude      = w.Latitude,
                Longitude     = w.Longitude,
                IsActive      = w.IsActive,
                CreatedDate   = w.CreatedDate
            })
            .FirstOrDefaultAsync();
    }

    public async Task<int> CreateWarehouseAsync(CreateWarehouseRequest req, int userId)
    {
        // Auto-generate Google Maps URL from coordinates when URL not explicitly supplied
        var mapsUrl = req.GoogleMapsUrl;
        if (string.IsNullOrWhiteSpace(mapsUrl) && req.Latitude.HasValue && req.Longitude.HasValue)
            mapsUrl = $"https://maps.google.com/?q={req.Latitude.Value},{req.Longitude.Value}";

        var entity = new Warehouse
        {
            Code          = req.Code,
            Name          = req.Name,
            Address       = req.Address,
            City          = req.City,
            Country       = req.Country,
            ContactName   = req.ContactName,
            ContactPhone  = req.ContactPhone,
            GoogleMapsUrl = mapsUrl,
            Latitude      = req.Latitude,
            Longitude     = req.Longitude,
            IsActive      = true,
            CreatedDate   = DateTime.UtcNow,
            CreatedBy     = userId
        };
        _db.Warehouses.Add(entity);
        await _db.SaveChangesAsync();
        return entity.Id;
    }

    public async Task<bool> UpdateWarehouseAsync(int id, PatchWarehouseRequest req)
    {
        var e = await _db.Warehouses.FirstOrDefaultAsync(w => w.Id == id);
        if (e == null) return false;
        if (req.Name         != null) e.Name         = req.Name;
        if (req.Address      != null) e.Address      = req.Address;
        if (req.City         != null) e.City         = req.City;
        if (req.Country      != null) e.Country      = req.Country;
        if (req.ContactName  != null) e.ContactName  = req.ContactName;
        if (req.ContactPhone != null) e.ContactPhone = req.ContactPhone;
        if (req.IsActive.HasValue)    e.IsActive     = req.IsActive.Value;
        if (req.Latitude.HasValue)    e.Latitude     = req.Latitude;
        if (req.Longitude.HasValue)   e.Longitude    = req.Longitude;
        if (req.GoogleMapsUrl != null)
            e.GoogleMapsUrl = req.GoogleMapsUrl;
        else if (req.Latitude.HasValue && req.Longitude.HasValue && string.IsNullOrWhiteSpace(e.GoogleMapsUrl))
            e.GoogleMapsUrl = $"https://maps.google.com/?q={req.Latitude.Value},{req.Longitude.Value}";
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteWarehouseAsync(int id)
    {
        var e = await _db.Warehouses.FirstOrDefaultAsync(w => w.Id == id);
        if (e == null) return false;
        e.IsActive = false;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<int> CreateZoneAsync(int warehouseId, CreateZoneRequest req)
    {
        var entity = new Zone
        {
            WarehouseId = warehouseId,
            Name        = req.Name,
            Code        = req.Code,
            Description = req.Description,
            IsActive    = true
        };
        _db.Zones.Add(entity);
        await _db.SaveChangesAsync();
        return entity.Id;
    }

    public async Task<List<RackModel>> GetRacksAsync(int zoneId)
    {
        return await _db.Racks
            .Where(r => r.ZoneId == zoneId && r.IsActive)
            .OrderBy(r => r.RackCode)
            .Select(r => new RackModel
            {
                Id       = r.Id,
                ZoneId   = r.ZoneId,
                RackCode = r.RackCode,
                RackName = r.RackName,
                IsActive = r.IsActive
            })
            .ToListAsync();
    }

    public async Task<int> CreateRackAsync(int zoneId, CreateRackRequest req)
    {
        var duplicate = await _db.Racks.AnyAsync(r =>
            r.ZoneId == zoneId && r.RackCode == req.RackCode.Trim().ToUpper());
        if (duplicate)
            throw new ConflictException($"Rack code '{req.RackCode}' already exists in this zone.");

        var entity = new Rack
        {
            ZoneId   = zoneId,
            RackCode = req.RackCode.Trim().ToUpper(),
            RackName = req.RackName?.Trim(),
            IsActive = true
        };
        _db.Racks.Add(entity);
        await _db.SaveChangesAsync();
        return entity.Id;
    }

    public async Task<List<ShelfModel>> GetShelvesAsync(int rackId)
    {
        return await _db.Shelves
            .Where(s => s.RackId == rackId && s.IsActive)
            .OrderBy(s => s.ShelfCode)
            .Select(s => new ShelfModel
            {
                Id         = s.Id,
                RackId     = s.RackId,
                ShelfCode  = s.ShelfCode,
                ShelfLevel = s.ShelfLevel,
                IsActive   = s.IsActive
            })
            .ToListAsync();
    }

    public async Task<int> CreateShelfAsync(int rackId, CreateShelfRequest req)
    {
        var duplicate = await _db.Shelves.AnyAsync(s =>
            s.RackId == rackId && s.ShelfCode == req.ShelfCode.Trim().ToUpper());
        if (duplicate)
            throw new ConflictException($"Shelf code '{req.ShelfCode}' already exists in this rack.");

        var entity = new Shelf
        {
            RackId     = rackId,
            ShelfCode  = req.ShelfCode.Trim().ToUpper(),
            ShelfLevel = req.ShelfLevel?.Trim(),
            IsActive   = true
        };
        _db.Shelves.Add(entity);
        await _db.SaveChangesAsync();
        return entity.Id;
    }

    public async Task<int> CreateBinAsync(int zoneId, CreateBinRequest req)
    {
        var entity = new Bin
        {
            ZoneId      = zoneId,
            Code        = req.Code,
            Description = req.Description,
            IsActive    = true
        };
        _db.Bins.Add(entity);
        await _db.SaveChangesAsync();
        return entity.Id;
    }

    // ── Warehouse structure ───────────────────────────────────────────────────

    public async Task<WarehouseStructureModel> GetWarehouseStructureAsync(int warehouseId)
    {
        var zones = await _db.Zones
            .Where(z => z.WarehouseId == warehouseId)
            .OrderBy(z => z.Name)
            .Select(z => new ZoneNodeModel
            {
                Id          = z.Id,
                Name        = z.Name,
                Code        = z.Code,
                Description = z.Description,
                IsActive    = z.IsActive
            })
            .ToListAsync();

        var zoneIds = zones.Select(z => z.Id).ToList();

        var racks = await _db.Racks
            .Where(r => zoneIds.Contains(r.ZoneId))
            .OrderBy(r => r.RackCode)
            .Select(r => new RackNodeModel
            {
                Id       = r.Id,
                ZoneId   = r.ZoneId,
                RackCode = r.RackCode,
                RackName = r.RackName,
                IsActive = r.IsActive
            })
            .ToListAsync();

        var rackIds = racks.Select(r => r.Id).ToList();

        var shelves = await _db.Shelves
            .Where(s => rackIds.Contains(s.RackId))
            .OrderBy(s => s.ShelfCode)
            .Select(s => new ShelfNodeModel
            {
                Id         = s.Id,
                RackId     = s.RackId,
                ShelfCode  = s.ShelfCode,
                ShelfLevel = s.ShelfLevel,
                IsActive   = s.IsActive
            })
            .ToListAsync();

        var shelfIds = shelves.Select(s => s.Id).ToList();

        var bins = await _db.Bins
            .Where(b => zoneIds.Contains(b.ZoneId))
            .OrderBy(b => b.Code)
            .Select(b => new BinNodeModel
            {
                Id          = b.Id,
                ZoneId      = b.ZoneId,
                RackId      = b.RackId,
                ShelfId     = b.ShelfId,
                Code        = b.Code,
                Description = b.Description,
                IsActive    = b.IsActive
            })
            .ToListAsync();

        foreach (var shelf in shelves)
            shelf.Bins = bins.Where(b => b.ShelfId == shelf.Id).ToList();

        foreach (var rack in racks)
        {
            rack.Shelves    = shelves.Where(s => s.RackId == rack.Id).ToList();
            rack.DirectBins = bins.Where(b => b.RackId == rack.Id && b.ShelfId == null).ToList();
        }

        foreach (var zone in zones)
        {
            zone.Racks      = racks.Where(r => r.ZoneId == zone.Id).ToList();
            zone.DirectBins = bins.Where(b => b.ZoneId == zone.Id && b.RackId == null).ToList();
        }

        return new WarehouseStructureModel { WarehouseId = warehouseId, Zones = zones };
    }

    public async Task<bool> UpdateZoneAsync(int id, UpdateZoneRequest req)
    {
        var e = await _db.Zones.FindAsync(id);
        if (e == null) return false;
        if (req.Name        is not null) e.Name        = req.Name.Trim();
        if (req.Code        is not null) e.Code        = req.Code.Trim().ToUpper();
        if (req.Description is not null) e.Description = req.Description.Trim();
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<StructureDeactivateResult> DeactivateZoneAsync(int id)
    {
        var e = await _db.Zones.FindAsync(id);
        if (e == null) return StructureDeactivateResult.Missing();

        var activeRacks  = await _db.Racks.CountAsync(r => r.ZoneId == id && r.IsActive);
        var activeBins   = await _db.Bins.CountAsync(b => b.ZoneId == id && b.RackId == null && b.IsActive);
        var total = activeRacks + activeBins;
        if (total > 0) return StructureDeactivateResult.BlockedBy(total, activeRacks > 0 ? "racks" : "bins");

        e.IsActive = false;
        await _db.SaveChangesAsync();
        return StructureDeactivateResult.Ok();
    }

    public async Task<bool> UpdateRackAsync(int id, UpdateRackRequest req)
    {
        var e = await _db.Racks.FindAsync(id);
        if (e == null) return false;
        if (req.RackCode is not null) e.RackCode = req.RackCode.Trim().ToUpper();
        if (req.RackName is not null) e.RackName = req.RackName.Trim();
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<StructureDeactivateResult> DeactivateRackAsync(int id)
    {
        var e = await _db.Racks.FindAsync(id);
        if (e == null) return StructureDeactivateResult.Missing();

        var activeShelves = await _db.Shelves.CountAsync(s => s.RackId == id && s.IsActive);
        var activeBins    = await _db.Bins.CountAsync(b => b.RackId == id && b.IsActive);
        var total = activeShelves + activeBins;
        if (total > 0) return StructureDeactivateResult.BlockedBy(total, activeShelves > 0 ? "shelves" : "bins");

        e.IsActive = false;
        await _db.SaveChangesAsync();
        return StructureDeactivateResult.Ok();
    }

    public async Task<bool> UpdateShelfAsync(int id, UpdateShelfRequest req)
    {
        var e = await _db.Shelves.FindAsync(id);
        if (e == null) return false;
        if (req.ShelfCode  is not null) e.ShelfCode  = req.ShelfCode.Trim().ToUpper();
        if (req.ShelfLevel is not null) e.ShelfLevel = req.ShelfLevel.Trim();
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<StructureDeactivateResult> DeactivateShelfAsync(int id)
    {
        var e = await _db.Shelves.FindAsync(id);
        if (e == null) return StructureDeactivateResult.Missing();

        var activeBins = await _db.Bins.CountAsync(b => b.ShelfId == id && b.IsActive);
        if (activeBins > 0) return StructureDeactivateResult.BlockedBy(activeBins, "bins");

        e.IsActive = false;
        await _db.SaveChangesAsync();
        return StructureDeactivateResult.Ok();
    }

    public async Task<int> CreateStructuredBinAsync(int shelfId, CreateBinRequest req)
    {
        var shelf = await _db.Shelves.FindAsync(shelfId)
            ?? throw new BadRequestException($"Shelf {shelfId} not found.");

        var entity = new Bin
        {
            ZoneId      = (await _db.Racks.Where(r => r.Id == shelf.RackId).Select(r => r.ZoneId).FirstAsync()),
            RackId      = shelf.RackId,
            ShelfId     = shelfId,
            Code        = req.Code.Trim().ToUpper(),
            Description = req.Description,
            IsActive    = true
        };
        _db.Bins.Add(entity);
        await _db.SaveChangesAsync();
        return entity.Id;
    }

    public async Task<bool> UpdateBinAsync(int id, UpdateBinRequest req)
    {
        var e = await _db.Bins.FindAsync(id);
        if (e == null) return false;
        if (req.Code        is not null) e.Code        = req.Code.Trim().ToUpper();
        if (req.Description is not null) e.Description = req.Description.Trim();
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<StructureDeactivateResult> DeactivateBinAsync(int id)
    {
        var e = await _db.Bins.FindAsync(id);
        if (e == null) return StructureDeactivateResult.Missing();
        e.IsActive = false;
        await _db.SaveChangesAsync();
        return StructureDeactivateResult.Ok();
    }

    // ── Stock levels ──────────────────────────────────────────────────────────

    public async Task<PaginatedResponse<StockLevelModel>> GetWarehouseStockAsync(int warehouseId, StockLevelFilter filter)
    {
        var query =
            from item     in _db.InventoryItems
            join variant  in _db.ProductVariants   on item.VariantId     equals variant.Id
            join product  in _db.Products          on variant.ProductId  equals product.Id
            join category in _db.ProductCategories on product.CategoryId equals category.Id into catGroup
            from category in catGroup.DefaultIfEmpty()
            join bin      in _db.Bins              on item.BinId         equals bin.Id      into binGroup
            from bin      in binGroup.DefaultIfEmpty()
            where item.WarehouseId == warehouseId
            select new
            {
                item,
                variant,
                product,
                category,
                bin,
                WarehouseName = item.Warehouse.Name
            };

        if (filter.CategoryId.HasValue)
            query = query.Where(x => x.product.CategoryId == filter.CategoryId.Value);

        if (!filter.IncludeZeroStock)
            query = query.Where(x => x.item.QtyOnHand > 0);

        if (filter.BelowReorderOnly)
            query = query.Where(x =>
                (x.item.ReorderPoint != null || x.variant.ReorderPoint != null) &&
                (x.item.QtyOnHand - x.item.QtyReserved) <=
                    (x.item.ReorderPoint ?? x.variant.ReorderPoint ?? 0));

        var total = await query.CountAsync();

        var page     = filter.Page < 1     ? 1  : filter.Page;
        var pageSize = filter.PageSize < 1 ? 20 : filter.PageSize;

        var rows = await query
            .OrderBy(x => x.product.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new
            {
                x.item.Id,
                x.item.VariantId,
                VariantUuid        = x.variant.Uuid,
                VariantSku         = x.variant.Sku,
                VariantName        = x.variant.VariantName,
                ProductId          = x.product.Id,
                ProductUuid        = x.product.Uuid,
                ProductName        = x.product.Name,
                CategoryName       = x.category != null ? x.category.Name : (string?)null,
                UomCode            = x.product.UomCode,
                x.item.WarehouseId,
                WarehouseName      = x.item.Warehouse.Name,
                x.item.BinId,
                BinCode            = x.bin != null ? x.bin.Code : (string?)null,
                ZoneName           = x.bin != null ? x.bin.Zone.Name : (string?)null,
                RackCode           = x.bin != null ? x.bin.Rack!.RackCode : (string?)null,
                ShelfCode          = x.bin != null ? x.bin.Shelf!.ShelfCode : (string?)null,
                x.item.QtyOnHand,
                x.item.QtyReserved,
                x.item.QtyOnOrder,
                x.item.ReorderPoint,
                VariantReorderPoint = x.variant.ReorderPoint,
                x.item.LastUpdated
            })
            .ToListAsync();

        var items = rows.Select(x =>
        {
            var qtyAvailable     = x.QtyOnHand - x.QtyReserved;
            var effectiveReorder = x.ReorderPoint ?? x.VariantReorderPoint;
            var isBelowReorder   = effectiveReorder.HasValue && qtyAvailable <= effectiveReorder.Value;

            var pathParts = new List<string>();
            if (x.ZoneName  != null) pathParts.Add(x.ZoneName);
            if (x.RackCode  != null) pathParts.Add(x.RackCode);
            if (x.ShelfCode != null) pathParts.Add(x.ShelfCode);
            if (x.BinCode   != null) pathParts.Add(x.BinCode);
            var locationPath = pathParts.Count > 0 ? string.Join(" › ", pathParts) : null;

            return new StockLevelModel
            {
                InventoryItemId = x.Id,
                VariantId       = x.VariantId,
                VariantUuid     = x.VariantUuid,
                VariantSku      = x.VariantSku,
                VariantName     = x.VariantName,
                ProductId       = x.ProductId,
                ProductUuid     = x.ProductUuid,
                ProductName     = x.ProductName,
                CategoryName    = x.CategoryName,
                UomCode         = x.UomCode,
                WarehouseId     = x.WarehouseId,
                WarehouseName   = x.WarehouseName,
                BinId           = x.BinId,
                BinCode         = x.BinCode,
                ZoneName        = x.ZoneName,
                RackCode        = x.RackCode,
                ShelfCode       = x.ShelfCode,
                LocationPath    = locationPath,
                QtyOnHand       = x.QtyOnHand,
                QtyReserved     = x.QtyReserved,
                QtyAvailable    = qtyAvailable,
                QtyOnOrder      = x.QtyOnOrder,
                ReorderPoint    = effectiveReorder,
                IsBelowReorder  = isBelowReorder,
                LastUpdated     = x.LastUpdated
            };
        }).ToList();

        return new PaginatedResponse<StockLevelModel>
        {
            Data         = items,
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling((double)total / pageSize)
        };
    }

    public async Task<List<ProductStockModel>> GetProductStockAsync(int productId)
    {
        // Aggregated by row across every variant of the product — this is per-warehouse/per-bin
        // detail (each InventoryItem row, e.g. one per batch), not summed. For a single set of
        // SUM(qty_on_hand)/SUM(qty_reserved)/SUM(qty_available) figures see GetProductStockSummaryAsync.
        return await _db.InventoryItems
            .Where(i => i.Variant.ProductId == productId)
            .Select(i => new ProductStockModel
            {
                WarehouseId   = i.WarehouseId,
                WarehouseUuid = i.Warehouse.Uuid,
                WarehouseCode = i.Warehouse.Code,
                WarehouseName = i.Warehouse.Name,
                BinId         = i.BinId,
                BinCode       = i.Bin != null ? i.Bin.Code : null,
                QtyOnHand     = i.QtyOnHand,
                QtyReserved   = i.QtyReserved,
                QtyAvailable  = i.QtyOnHand - i.QtyReserved,
                QtyOnOrder    = i.QtyOnOrder,
                LastUpdated   = i.LastUpdated
            })
            .OrderBy(x => x.WarehouseName)
            .ToListAsync();
    }

    // One row per warehouse for a single variant, bins summed together — for pickers that need
    // "how much of THIS variant is available in THIS warehouse" (e.g. MIR line creation), where
    // GetProductStockAsync's per-bin/per-variant detail rows would otherwise show as multiple,
    // seemingly-duplicate entries for the same warehouse.
    public async Task<List<VariantWarehouseStockModel>> GetVariantStockByWarehouseAsync(Guid variantUuid)
    {
        return await _db.InventoryItems
            .Where(i => i.Variant.Uuid == variantUuid)
            .GroupBy(i => new { i.WarehouseId, i.Warehouse.Uuid, i.Warehouse.Code, i.Warehouse.Name })
            .Select(g => new VariantWarehouseStockModel
            {
                WarehouseId   = g.Key.WarehouseId,
                WarehouseUuid = g.Key.Uuid,
                WarehouseCode = g.Key.Code,
                WarehouseName = g.Key.Name,
                QtyOnHand     = g.Sum(i => i.QtyOnHand),
                QtyReserved   = g.Sum(i => i.QtyReserved),
                QtyAvailable  = g.Sum(i => i.QtyOnHand - i.QtyReserved)
            })
            .OrderBy(x => x.WarehouseName)
            .ToListAsync();
    }

    // PV-005 — product-level rollup: SUM(qty_on_hand)/SUM(qty_reserved)/SUM(qty_available) across
    // every InventoryItem row for every variant of this product, computed on the fly (never stored).
    public async Task<ProductStockSummaryModel?> GetProductStockSummaryAsync(int productId)
    {
        var product = await _db.Products.AsNoTracking()
            .Where(p => p.Id == productId)
            .Select(p => new { p.Id, p.Uuid })
            .FirstOrDefaultAsync();
        if (product is null) return null;

        var variantTotals = await _db.InventoryItems
            .Where(i => i.Variant.ProductId == productId)
            .GroupBy(i => i.VariantId)
            .Select(g => new
            {
                VariantId = g.Key,
                OnHand    = g.Sum(i => i.QtyOnHand),
                Reserved  = g.Sum(i => i.QtyReserved)
            })
            .ToListAsync();

        var variantIds = variantTotals.Select(v => v.VariantId).ToList();
        var variantInfo = await _db.ProductVariants.AsNoTracking()
            .Where(v => variantIds.Contains(v.Id))
            .Select(v => new { v.Id, v.Uuid, v.Sku, v.VariantName })
            .ToDictionaryAsync(v => v.Id);

        var variants = variantTotals.Select(v =>
        {
            var info = variantInfo.GetValueOrDefault(v.VariantId);
            return new VariantStockSummaryItem
            {
                VariantUuid = info?.Uuid ?? Guid.Empty,
                Sku         = info?.Sku ?? string.Empty,
                VariantName = info?.VariantName ?? string.Empty,
                OnHand      = v.OnHand,
                Reserved    = v.Reserved,
                Available   = v.OnHand - v.Reserved
            };
        }).ToList();

        return new ProductStockSummaryModel
        {
            ProductId      = product.Id,
            ProductUuid    = product.Uuid,
            TotalOnHand    = variants.Sum(v => v.OnHand),
            TotalReserved  = variants.Sum(v => v.Reserved),
            TotalAvailable = variants.Sum(v => v.Available),
            Variants       = variants
        };
    }

    public async Task<List<ReorderAlertModel>> GetReorderAlertsAsync()
    {
        var rows = await (
            from item      in _db.InventoryItems
            join variant   in _db.ProductVariants   on item.VariantId    equals variant.Id
            join product   in _db.Products          on variant.ProductId equals product.Id
            join warehouse in _db.Warehouses        on item.WarehouseId  equals warehouse.Id
            join category  in _db.ProductCategories on product.CategoryId equals category.Id into catGroup
            from category  in catGroup.DefaultIfEmpty()
            select new
            {
                item.VariantId,
                VariantUuid         = variant.Uuid,
                VariantSku          = variant.Sku,
                VariantName         = variant.VariantName,
                ProductId           = product.Id,
                ProductUuid         = product.Uuid,
                ProductName         = product.Name,
                CategoryName        = category != null ? category.Name : (string?)null,
                item.WarehouseId,
                WarehouseUuid       = warehouse.Uuid,
                WarehouseName       = warehouse.Name,
                item.QtyOnHand,
                item.QtyReserved,
                ItemReorderPoint    = item.ReorderPoint,
                VariantReorderPoint = variant.ReorderPoint,
                ProductReorderQty   = product.ReorderQty
            }
        ).ToListAsync();

        return rows
            .Select(x => new
            {
                x.VariantId,
                x.VariantUuid,
                x.VariantSku,
                x.VariantName,
                x.ProductId,
                x.ProductUuid,
                x.ProductName,
                x.CategoryName,
                x.WarehouseId,
                x.WarehouseUuid,
                x.WarehouseName,
                x.QtyOnHand,
                x.QtyReserved,
                x.ProductReorderQty,
                EffectiveReorderPoint = x.ItemReorderPoint ?? x.VariantReorderPoint
            })
            .Where(x => x.EffectiveReorderPoint.HasValue
                     && x.EffectiveReorderPoint.Value > 0
                     && (x.QtyOnHand - x.QtyReserved) <= x.EffectiveReorderPoint.Value)
            .Select(x => new ReorderAlertModel
            {
                VariantId     = x.VariantId,
                VariantUuid   = x.VariantUuid,
                VariantSku    = x.VariantSku,
                VariantName   = x.VariantName,
                ProductId     = x.ProductId,
                ProductUuid   = x.ProductUuid,
                ProductName   = x.ProductName,
                CategoryName  = x.CategoryName,
                WarehouseId   = x.WarehouseId,
                WarehouseUuid = x.WarehouseUuid,
                WarehouseName = x.WarehouseName,
                QtyOnHand     = x.QtyOnHand,
                QtyAvailable  = x.QtyOnHand - x.QtyReserved,
                ReorderPoint  = x.EffectiveReorderPoint!.Value,
                ReorderQty    = x.ProductReorderQty
            })
            .OrderBy(x => x.ProductName)
            .ToList();
    }

    public async Task<bool> MoveBinAsync(int inventoryItemId, int binId)
    {
        var item = await _db.InventoryItems.FindAsync(inventoryItemId);
        if (item == null) return false;

        item.BinId       = binId;
        item.LastUpdated = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    // ── Stock adjustments ─────────────────────────────────────────────────────

    public async Task<PaginatedResponse<StockAdjustmentModel>> GetAdjustmentsAsync(AdjustmentListFilter filter)
    {
        var query =
            from adj     in _db.StockAdjustments
            join item    in _db.InventoryItems on adj.InventoryItemId equals item.Id
            join variant in _db.ProductVariants on item.VariantId      equals variant.Id
            join wh      in _db.Warehouses     on item.WarehouseId    equals wh.Id
            select new { adj, item, variant, wh };

        if (filter.VariantId.HasValue)
            query = query.Where(x => x.item.VariantId == filter.VariantId.Value);

        if (filter.WarehouseId.HasValue)
            query = query.Where(x => x.item.WarehouseId == filter.WarehouseId.Value);

        if (!string.IsNullOrWhiteSpace(filter.Status))
            query = query.Where(x => x.adj.Status == filter.Status);

        if (!string.IsNullOrWhiteSpace(filter.AdjType))
            query = query.Where(x => x.adj.AdjType == filter.AdjType);

        var total = await query.CountAsync();

        var page     = filter.Page < 1     ? 1  : filter.Page;
        var pageSize = filter.PageSize < 1 ? 20 : filter.PageSize;

        var items = await query
            .OrderByDescending(x => x.adj.CreatedDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new StockAdjustmentModel
            {
                Id              = x.adj.Id,
                Uuid            = x.adj.Uuid,
                AdjNumber       = x.adj.AdjNumber,
                InventoryItemId = x.adj.InventoryItemId,
                VariantId       = x.item.VariantId,
                VariantSku      = x.variant.Sku,
                VariantName     = x.variant.VariantName,
                ProductName     = x.variant.Product.Name,
                WarehouseId     = x.item.WarehouseId,
                WarehouseName   = x.wh.Name,
                AdjType         = x.adj.AdjType,
                Reason          = x.adj.Reason,
                ReferenceDoc    = x.adj.ReferenceDoc,
                QtyBefore       = x.adj.QtyBefore,
                QtyAdjusted     = x.adj.QtyAdjusted,
                QtyAfter        = x.adj.QtyAfter,
                UnitCost        = x.adj.UnitCost,
                Notes           = x.adj.Notes,
                Status          = x.adj.Status,
                RejectionReason = x.adj.RejectionReason,
                CreatedDate     = x.adj.CreatedDate,
                CreatedBy       = x.adj.CreatedBy,
                ReviewedBy      = x.adj.ReviewedBy,
                ReviewedDate    = x.adj.ReviewedDate
            })
            .ToListAsync();

        return new PaginatedResponse<StockAdjustmentModel>
        {
            Data         = items,
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling((double)total / pageSize)
        };
    }

    public async Task<StockAdjustmentResult> CreateAdjustmentAsync(CreateAdjustmentRequest req, int userId)
    {
        var strategy = _db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync();

            var warehouseName = await _db.Warehouses
                .Where(w => w.Id == req.WarehouseId)
                .Select(w => w.Name)
                .FirstOrDefaultAsync() ?? string.Empty;

            var item = await _db.InventoryItems
                .FirstOrDefaultAsync(i => i.VariantId == req.VariantId && i.WarehouseId == req.WarehouseId);

            if (item == null)
            {
                item = new InventoryItem
                {
                    VariantId   = req.VariantId,
                    WarehouseId = req.WarehouseId,
                    QtyOnHand   = 0m,
                    QtyReserved = 0m,
                    QtyOnOrder  = 0m,
                    LastUpdated = DateTime.UtcNow
                };
                _db.InventoryItems.Add(item);
                await _db.SaveChangesAsync();
            }

            var qtyBefore = item.QtyOnHand;
            var qtyAfter  = qtyBefore + req.QtyAdjusted;

            var aboveThreshold =
                Math.Abs(req.QtyAdjusted) > QTY_THRESHOLD ||
                (req.UnitCost.HasValue && Math.Abs(req.QtyAdjusted) * req.UnitCost.Value > VALUE_THRESHOLD);

            string status;
            if (aboveThreshold)
            {
                status = "PENDING_APPROVAL";
            }
            else
            {
                status = "AUTO_APPROVED";
                item.QtyOnHand   = qtyAfter;
                item.UnitCost    = req.UnitCost ?? item.UnitCost;
                item.LastUpdated = DateTime.UtcNow;
            }

            var adjNumber = await GenerateAdjNumberAsync();

            if (status == "AUTO_APPROVED")
            {
                await _ledger.CreateEntryAsync(new LedgerEntryCommand
                {
                    VariantId       = req.VariantId,
                    WarehouseId     = req.WarehouseId,
                    TransactionType = "STOCK_ADJUSTMENT",
                    ReferenceType   = "ADJUSTMENT",
                    ReferenceId     = Guid.NewGuid(),
                    ReferenceNumber = adjNumber,
                    QuantityIn      = req.QtyAdjusted > 0 ?  req.QtyAdjusted     : null,
                    QuantityOut     = req.QtyAdjusted < 0 ? -req.QtyAdjusted     : null,
                    UnitCost        = req.UnitCost ?? 0m,
                    Notes           = req.Notes,
                    CreatedBy       = userId,
                    // FSD Addendum 24 (ML-003): an adjustment has no external counterparty — model
                    // an increase as flowing FROM the adjustment INTO the warehouse, and a decrease
                    // as flowing FROM the warehouse INTO the adjustment (write-off).
                    SourceType      = req.QtyAdjusted > 0 ? "ADJUSTMENT" : "WAREHOUSE",
                    SourceName      = req.QtyAdjusted > 0 ? "Stock Adjustment" : warehouseName,
                    DestinationType = req.QtyAdjusted > 0 ? "WAREHOUSE" : "ADJUSTMENT",
                    DestinationName = req.QtyAdjusted > 0 ? warehouseName : "Stock Adjustment"
                });
            }

            var adjustment = new StockAdjustment
            {
                AdjNumber       = adjNumber,
                InventoryItemId = item.Id,
                VariantId       = req.VariantId,
                WarehouseId     = req.WarehouseId,
                AdjType         = req.AdjType,
                Reason          = req.Reason,
                ReferenceDoc    = req.ReferenceDoc,
                QtyBefore       = qtyBefore,
                QtyAdjusted     = req.QtyAdjusted,
                QtyAfter        = qtyAfter,
                UnitCost        = req.UnitCost,
                Notes           = req.Notes,
                Status          = status,
                CreatedDate     = DateTime.UtcNow,
                CreatedBy       = userId
            };

            _db.StockAdjustments.Add(adjustment);
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            return new StockAdjustmentResult
            {
                Id           = adjustment.Id,
                Uuid         = adjustment.Uuid,
                AdjNumber    = adjustment.AdjNumber,
                Status       = adjustment.Status,
                StockUpdated = status == "AUTO_APPROVED"
            };
        });
    }

    public async Task<bool> ApproveAdjustmentAsync(Guid uuid, int reviewerId)
    {
        var strategy = _db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync();

            var adjustment = await _db.StockAdjustments
                .FirstOrDefaultAsync(a => a.Uuid == uuid);

            if (adjustment == null) return false;

            if (adjustment.Status != "PENDING_APPROVAL")
                throw new BadRequestException(
                    $"Adjustment '{uuid}' cannot be approved because its current status is '{adjustment.Status}'.");

            var item = await _db.InventoryItems.FindAsync(adjustment.InventoryItemId)
                ?? throw new NotFoundException("InventoryItem", adjustment.InventoryItemId);

            item.QtyOnHand   += adjustment.QtyAdjusted;
            if (adjustment.UnitCost.HasValue) item.UnitCost = adjustment.UnitCost;
            item.LastUpdated  = DateTime.UtcNow;

            adjustment.Status       = "APPROVED";
            adjustment.ReviewedBy   = reviewerId;
            adjustment.ReviewedDate = DateTime.UtcNow;

            var warehouseName = await _db.Warehouses
                .Where(w => w.Id == adjustment.WarehouseId)
                .Select(w => w.Name)
                .FirstOrDefaultAsync() ?? string.Empty;

            await _ledger.CreateEntryAsync(new LedgerEntryCommand
            {
                VariantId       = adjustment.VariantId,
                WarehouseId     = adjustment.WarehouseId,
                TransactionType = "STOCK_ADJUSTMENT",
                ReferenceType   = "ADJUSTMENT",
                ReferenceId     = adjustment.Uuid,
                ReferenceNumber = adjustment.AdjNumber ?? adjustment.Uuid.ToString("N")[..8],
                QuantityIn      = adjustment.QtyAdjusted > 0 ?  adjustment.QtyAdjusted     : null,
                QuantityOut     = adjustment.QtyAdjusted < 0 ? -adjustment.QtyAdjusted     : null,
                UnitCost        = adjustment.UnitCost ?? 0m,
                Notes           = adjustment.Notes,
                CreatedBy       = reviewerId,
                // FSD Addendum 24 (ML-003) — see CreateAdjustmentAsync for the direction rationale.
                SourceType      = adjustment.QtyAdjusted > 0 ? "ADJUSTMENT" : "WAREHOUSE",
                SourceName      = adjustment.QtyAdjusted > 0 ? "Stock Adjustment" : warehouseName,
                DestinationType = adjustment.QtyAdjusted > 0 ? "WAREHOUSE" : "ADJUSTMENT",
                DestinationName = adjustment.QtyAdjusted > 0 ? warehouseName : "Stock Adjustment"
            }, tx);

            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            return true;
        });
    }

    public async Task<bool> RejectAdjustmentAsync(Guid uuid, string reason, int reviewerId)
    {
        var adjustment = await _db.StockAdjustments
            .FirstOrDefaultAsync(a => a.Uuid == uuid);

        if (adjustment == null) return false;

        if (adjustment.Status != "PENDING_APPROVAL")
            throw new BadRequestException(
                $"Adjustment '{uuid}' cannot be rejected because its current status is '{adjustment.Status}'.");

        adjustment.Status          = "REJECTED";
        adjustment.RejectionReason = reason;
        adjustment.ReviewedBy      = reviewerId;
        adjustment.ReviewedDate    = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return true;
    }

    // ── Dynamic Attributes (FSD Addendum 26 §4) ──────────────────────────────────

    private static readonly HashSet<string> ValidDataTypes =
        ["TEXT", "NUMBER", "DECIMAL", "DATE", "BOOLEAN", "DROPDOWN", "MULTI_SELECT"];

    private static readonly HashSet<string> ValidControlTypes =
        ["TEXTBOX", "NUMBERBOX", "DATEPICKER", "TOGGLE", "DROPDOWN", "MULTI_SELECT", "TEXTAREA"];

    public async Task<List<AttributeDefinitionModel>> GetAttributesAsync()
    {
        var attributes = await _db.AttributeDefinitions
            .OrderBy(a => a.SortOrder).ThenBy(a => a.DisplayName)
            .ToListAsync();

        return attributes.Select(ToAttributeModel).ToList();
    }

    public async Task<Guid> CreateAttributeAsync(CreateAttributeDefinitionRequest req)
    {
        var name = req.AttributeName?.Trim().ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
            throw new BadRequestException("Attribute name is required.");
        if (string.IsNullOrWhiteSpace(req.DisplayName))
            throw new BadRequestException("Display name is required.");
        if (!ValidDataTypes.Contains(req.DataType))
            throw new BadRequestException($"Invalid data type '{req.DataType}'. Must be one of: {string.Join(", ", ValidDataTypes)}.");
        if (!ValidControlTypes.Contains(req.ControlType))
            throw new BadRequestException($"Invalid control type '{req.ControlType}'. Must be one of: {string.Join(", ", ValidControlTypes)}.");

        var isChoiceType = req.DataType is "DROPDOWN" or "MULTI_SELECT";
        if (isChoiceType && (req.DropdownOptions is null || req.DropdownOptions.Count == 0))
            throw new BadRequestException("dropdown_options is required when data_type is DROPDOWN or MULTI_SELECT.");

        if (await _db.AttributeDefinitions.AnyAsync(a => a.AttributeName == name))
            throw new ConflictException($"An attribute named '{name}' already exists.");

        var entity = new AttributeDefinition
        {
            Uuid            = Guid.NewGuid(),
            AttributeName   = name,
            DisplayName     = req.DisplayName.Trim(),
            DataType        = req.DataType,
            ControlType     = req.ControlType,
            DropdownOptions = isChoiceType ? JsonSerializer.Serialize(req.DropdownOptions) : null,
            DefaultValue    = req.DefaultValue,
            ValidationRegex = req.ValidationRegex,
            IsRequired      = req.IsRequired,
            IsSearchable    = req.IsSearchable,
            IsFilterable    = req.IsFilterable,
            SortOrder       = req.SortOrder,
            IsActive        = true
        };

        _db.AttributeDefinitions.Add(entity);
        await _db.SaveChangesAsync();
        return entity.Uuid;
    }

    public async Task<bool> UpdateAttributeAsync(Guid uuid, UpdateAttributeDefinitionRequest req)
    {
        var entity = await _db.AttributeDefinitions.FirstOrDefaultAsync(a => a.Uuid == uuid);
        if (entity == null) return false;

        if (req.ControlType is not null)
        {
            if (!ValidControlTypes.Contains(req.ControlType))
                throw new BadRequestException($"Invalid control type '{req.ControlType}'. Must be one of: {string.Join(", ", ValidControlTypes)}.");
            entity.ControlType = req.ControlType;
        }

        var isChoiceType = entity.DataType is "DROPDOWN" or "MULTI_SELECT";
        if (req.DropdownOptions is not null)
        {
            if (isChoiceType && req.DropdownOptions.Count == 0)
                throw new BadRequestException("dropdown_options cannot be empty for a DROPDOWN/MULTI_SELECT attribute.");
            entity.DropdownOptions = JsonSerializer.Serialize(req.DropdownOptions);
        }

        if (req.DisplayName       is not null) entity.DisplayName     = req.DisplayName.Trim();
        if (req.DefaultValue      is not null) entity.DefaultValue    = req.DefaultValue;
        if (req.ValidationRegex   is not null) entity.ValidationRegex = req.ValidationRegex;
        if (req.IsRequired    .HasValue)        entity.IsRequired     = req.IsRequired.Value;
        if (req.IsSearchable  .HasValue)        entity.IsSearchable   = req.IsSearchable.Value;
        if (req.IsFilterable  .HasValue)        entity.IsFilterable   = req.IsFilterable.Value;
        if (req.SortOrder     .HasValue)        entity.SortOrder      = req.SortOrder.Value;
        if (req.IsActive      .HasValue)        entity.IsActive       = req.IsActive.Value;

        await _db.SaveChangesAsync();
        return true;
    }

    // Hard-deletes when unused, otherwise soft-deactivates and reports what's still referencing
    // it — same two-tier contract as DeleteCategoryAsync above (ProductModels.CategoryDeleteResult).
    // A category link or a recorded variant value both make a hard delete unsafe: the former
    // would silently strip a field off every product form for that category, the latter would
    // orphan historical variant data.
    public async Task<AttributeDeleteResult> DeleteAttributeAsync(Guid uuid)
    {
        var entity = await _db.AttributeDefinitions.FirstOrDefaultAsync(a => a.Uuid == uuid);
        if (entity == null) return new AttributeDeleteResult { Deleted = false };

        var categoryCount = await _db.CategoryAttributes.CountAsync(ca => ca.AttributeId == entity.Id);
        var variantValueCount = await _db.VariantAttributeValues.CountAsync(v => v.AttributeId == entity.Id);

        if (categoryCount > 0 || variantValueCount > 0)
            return new AttributeDeleteResult
            {
                Deleted                     = false,
                ReferencedCategoryCount     = categoryCount,
                ReferencedVariantValueCount = variantValueCount
            };

        _db.AttributeDefinitions.Remove(entity);
        await _db.SaveChangesAsync();
        return new AttributeDeleteResult { Deleted = true };
    }

    public async Task<List<CategoryAttributeModel>> GetCategoryAttributesAsync(int categoryId)
    {
        var raw = await _db.CategoryAttributes
            .Where(ca => ca.CategoryId == categoryId)
            .OrderBy(ca => ca.DisplayOrder)
            .Select(ca => new
            {
                ca.CategoryId,
                AttributeUuid = ca.Attribute.Uuid,
                ca.Attribute.AttributeName,
                ca.Attribute.DisplayName,
                ca.Attribute.DataType,
                ca.Attribute.ControlType,
                ca.Attribute.DropdownOptions,
                ca.Attribute.ValidationRegex,
                ca.IsRequired,
                ca.Attribute.IsSearchable,
                ca.DisplayOrder
            })
            .ToListAsync();

        return raw.Select(x => new CategoryAttributeModel
        {
            CategoryId      = x.CategoryId,
            AttributeUuid   = x.AttributeUuid,
            AttributeName   = x.AttributeName,
            DisplayName     = x.DisplayName,
            DataType        = x.DataType,
            ControlType     = x.ControlType,
            DropdownOptions = ParseDropdownOptions(x.DropdownOptions),
            ValidationRegex = x.ValidationRegex,
            IsRequired      = x.IsRequired,
            IsSearchable    = x.IsSearchable,
            DisplayOrder    = x.DisplayOrder
        }).ToList();
    }

    public async Task<bool> LinkCategoryAttributeAsync(int categoryId, CreateCategoryAttributeRequest req)
    {
        if (!await CategoryExistsAsync(categoryId))
            throw new NotFoundException("Category", categoryId);

        var attribute = await _db.AttributeDefinitions.FirstOrDefaultAsync(a => a.Uuid == req.AttributeUuid)
            ?? throw new NotFoundException("Attribute", req.AttributeUuid);

        if (await _db.CategoryAttributes.AnyAsync(ca => ca.CategoryId == categoryId && ca.AttributeId == attribute.Id))
            throw new ConflictException($"Attribute '{attribute.DisplayName}' is already linked to this category.");

        _db.CategoryAttributes.Add(new CategoryAttribute
        {
            CategoryId   = categoryId,
            AttributeId  = attribute.Id,
            IsRequired   = req.IsRequired ?? attribute.IsRequired,
            DisplayOrder = req.DisplayOrder
        });

        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> UnlinkCategoryAttributeAsync(int categoryId, Guid attributeUuid)
    {
        var link = await _db.CategoryAttributes
            .FirstOrDefaultAsync(ca => ca.CategoryId == categoryId && ca.Attribute.Uuid == attributeUuid);
        if (link == null) return false;

        _db.CategoryAttributes.Remove(link);
        await _db.SaveChangesAsync();
        return true;
    }

    // Configure Attributes admin screen's Save button — replaces the category's entire attribute
    // set (links, unlinks, reorders, and required-overrides) in one atomic write, rather than
    // requiring the frontend to diff and issue individual POST/DELETE calls.
    public async Task<List<CategoryAttributeModel>> SetCategoryAttributesAsync(int categoryId, SetCategoryAttributesRequest req)
    {
        if (!await CategoryExistsAsync(categoryId))
            throw new NotFoundException("Category", categoryId);

        var attributeUuids = req.Attributes.Select(a => a.AttributeUuid).Distinct().ToList();
        var attributes = await _db.AttributeDefinitions
            .Where(a => attributeUuids.Contains(a.Uuid))
            .ToDictionaryAsync(a => a.Uuid);

        foreach (var item in req.Attributes)
        {
            if (!attributes.ContainsKey(item.AttributeUuid))
                throw new NotFoundException("Attribute", item.AttributeUuid);
        }

        var existingLinks = await _db.CategoryAttributes
            .Where(ca => ca.CategoryId == categoryId)
            .ToListAsync();

        var keepIds = req.Attributes.Select(a => attributes[a.AttributeUuid].Id).ToHashSet();
        var toRemove = existingLinks.Where(l => !keepIds.Contains(l.AttributeId)).ToList();
        if (toRemove.Count > 0) _db.CategoryAttributes.RemoveRange(toRemove);

        foreach (var item in req.Attributes)
        {
            var attributeId = attributes[item.AttributeUuid].Id;
            var existing = existingLinks.FirstOrDefault(l => l.AttributeId == attributeId);
            if (existing is not null)
            {
                existing.IsRequired   = item.IsRequired;
                existing.DisplayOrder = item.DisplayOrder;
            }
            else
            {
                _db.CategoryAttributes.Add(new CategoryAttribute
                {
                    CategoryId   = categoryId,
                    AttributeId  = attributeId,
                    IsRequired   = item.IsRequired,
                    DisplayOrder = item.DisplayOrder
                });
            }
        }

        await _db.SaveChangesAsync();
        return await GetCategoryAttributesAsync(categoryId);
    }

    public async Task<List<VariantAttributeValueModel>> GetVariantAttributeValuesAsync(Guid variantUuid)
    {
        return await _db.VariantAttributeValues
            .Where(v => v.Variant.Uuid == variantUuid)
            .Select(v => new VariantAttributeValueModel
            {
                AttributeUuid = v.Attribute.Uuid,
                AttributeName = v.Attribute.AttributeName,
                DisplayName   = v.Attribute.DisplayName,
                DataType      = v.Attribute.DataType,
                Value         = v.Value
            })
            .ToListAsync();
    }

    public async Task<int?> SetVariantAttributeValuesAsync(Guid variantUuid, SetVariantAttributeValuesRequest req)
    {
        var variant = await _db.ProductVariants.FirstOrDefaultAsync(v => v.Uuid == variantUuid);
        if (variant == null) return null;

        var attributeUuids = req.Values.Select(v => v.AttributeUuid).Distinct().ToList();
        var attributes = await _db.AttributeDefinitions
            .Where(a => attributeUuids.Contains(a.Uuid))
            .ToDictionaryAsync(a => a.Uuid);

        // Validate every submitted value before writing any of them — a bad value anywhere in
        // the batch must leave the variant's existing attribute values untouched.
        foreach (var input in req.Values)
        {
            if (!attributes.TryGetValue(input.AttributeUuid, out var attr))
                throw new NotFoundException("Attribute", input.AttributeUuid);
            ValidateValue(attr, input.Value);
        }

        var existing = await _db.VariantAttributeValues
            .Where(v => v.VariantId == variant.Id)
            .ToListAsync();

        foreach (var input in req.Values)
        {
            var attr = attributes[input.AttributeUuid];
            var row  = existing.FirstOrDefault(v => v.AttributeId == attr.Id);
            if (row is null)
            {
                _db.VariantAttributeValues.Add(new VariantAttributeValue
                {
                    VariantId   = variant.Id,
                    AttributeId = attr.Id,
                    Value       = input.Value
                });
            }
            else
            {
                row.Value = input.Value;
            }
        }

        await _db.SaveChangesAsync();
        return variant.Id;
    }

    // Validates a submitted value against its attribute's data_type, dropdown_options, and
    // validation_regex (FSD §6 "Validation config" — enforced server-side, not just client-side).
    private static void ValidateValue(AttributeDefinition attr, string value)
    {
        switch (attr.DataType)
        {
            case "NUMBER":
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    throw new BadRequestException($"Value '{value}' for '{attr.DisplayName}' must be a whole number.");
                break;

            case "DECIMAL":
                if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
                    throw new BadRequestException($"Value '{value}' for '{attr.DisplayName}' must be a valid decimal.");
                break;

            case "BOOLEAN":
                if (!bool.TryParse(value, out _))
                    throw new BadRequestException($"Value '{value}' for '{attr.DisplayName}' must be true or false.");
                break;

            case "DATE":
                if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    throw new BadRequestException($"Value '{value}' for '{attr.DisplayName}' must be a valid date.");
                break;

            case "DROPDOWN":
            {
                var options = ParseDropdownOptions(attr.DropdownOptions);
                if (!options.Contains(value))
                    throw new BadRequestException($"Value '{value}' is not a valid option for '{attr.DisplayName}'.");
                break;
            }

            case "MULTI_SELECT":
            {
                var options = ParseDropdownOptions(attr.DropdownOptions);
                List<string> selected;
                try { selected = JsonSerializer.Deserialize<List<string>>(value) ?? []; }
                catch (JsonException) { throw new BadRequestException($"Value for '{attr.DisplayName}' must be a JSON array of strings."); }

                var invalid = selected.Except(options).ToList();
                if (invalid.Count > 0)
                    throw new BadRequestException($"Value(s) '{string.Join(", ", invalid)}' are not valid options for '{attr.DisplayName}'.");
                break;
            }

            // TEXT — no type-shape check beyond the regex below.
        }

        if (!string.IsNullOrWhiteSpace(attr.ValidationRegex) && !Regex.IsMatch(value, attr.ValidationRegex))
            throw new BadRequestException($"Value '{value}' for '{attr.DisplayName}' does not match the required format.");
    }

    private static List<string> ParseDropdownOptions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        return JsonSerializer.Deserialize<List<string>>(json) ?? [];
    }

    private static AttributeDefinitionModel ToAttributeModel(AttributeDefinition a) => new()
    {
        Uuid            = a.Uuid,
        AttributeName   = a.AttributeName,
        DisplayName     = a.DisplayName,
        DataType        = a.DataType,
        ControlType     = a.ControlType,
        DropdownOptions = ParseDropdownOptions(a.DropdownOptions),
        DefaultValue    = a.DefaultValue,
        ValidationRegex = a.ValidationRegex,
        IsRequired      = a.IsRequired,
        IsSearchable    = a.IsSearchable,
        IsFilterable    = a.IsFilterable,
        SortOrder       = a.SortOrder,
        IsActive        = a.IsActive
    };

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<string> GenerateSkuAsync()
    {
        var year   = DateTime.UtcNow.Year;
        var prefix = $"PRD-{year}-";

        var count = await _db.Products
            .CountAsync(p => p.Sku.StartsWith(prefix));

        string candidate;
        do
        {
            count++;
            candidate = $"{prefix}{count:D5}";
        }
        while (await _db.Products.AnyAsync(p => p.Sku == candidate));

        return candidate;
    }

    private async Task<string> GenerateAdjNumberAsync()
    {
        var year   = DateTime.UtcNow.Year;
        var prefix = $"ADJ-{year}-";

        var count = await _db.StockAdjustments
            .CountAsync(a => a.AdjNumber != null && a.AdjNumber.StartsWith(prefix));

        string candidate;
        do
        {
            count++;
            candidate = $"{prefix}{count:D5}";
        }
        while (await _db.StockAdjustments.AnyAsync(a => a.AdjNumber == candidate));

        return candidate;
    }
}

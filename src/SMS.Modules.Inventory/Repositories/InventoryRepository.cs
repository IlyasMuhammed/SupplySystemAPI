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
                VariantCount    = p.Variants.Count
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
                Variants = p.Variants
                    .OrderBy(v => v.SortOrder ?? 0).ThenBy(v => v.Id)
                    .Select(v => new ProductVariantModel
                    {
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
            join product  in _db.Products          on item.ProductId     equals product.Id
            join category in _db.ProductCategories on product.CategoryId equals category.Id into catGroup
            from category in catGroup.DefaultIfEmpty()
            join bin      in _db.Bins              on item.BinId         equals bin.Id      into binGroup
            from bin      in binGroup.DefaultIfEmpty()
            where item.WarehouseId == warehouseId
            select new
            {
                item,
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
                (x.item.ReorderPoint != null || x.product.ReorderPoint != null) &&
                (x.item.QtyOnHand - x.item.QtyReserved) <=
                    (x.item.ReorderPoint ?? x.product.ReorderPoint ?? 0));

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
                x.item.ProductId,
                ProductUuid        = x.product.Uuid,
                ProductSku         = x.product.Sku,
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
                ProductReorderPoint = x.product.ReorderPoint,
                x.item.LastUpdated
            })
            .ToListAsync();

        var items = rows.Select(x =>
        {
            var qtyAvailable     = x.QtyOnHand - x.QtyReserved;
            var effectiveReorder = x.ReorderPoint ?? x.ProductReorderPoint;
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
                ProductId       = x.ProductId,
                ProductUuid     = x.ProductUuid,
                ProductSku      = x.ProductSku,
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
        return await _db.InventoryItems
            .Where(i => i.ProductId == productId)
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

    public async Task<List<ReorderAlertModel>> GetReorderAlertsAsync()
    {
        var rows = await (
            from item      in _db.InventoryItems
            join product   in _db.Products          on item.ProductId    equals product.Id
            join warehouse in _db.Warehouses        on item.WarehouseId  equals warehouse.Id
            join category  in _db.ProductCategories on product.CategoryId equals category.Id into catGroup
            from category  in catGroup.DefaultIfEmpty()
            select new
            {
                item.ProductId,
                ProductUuid         = product.Uuid,
                ProductSku          = product.Sku,
                ProductName         = product.Name,
                CategoryName        = category != null ? category.Name : (string?)null,
                item.WarehouseId,
                WarehouseUuid       = warehouse.Uuid,
                WarehouseName       = warehouse.Name,
                item.QtyOnHand,
                item.QtyReserved,
                ItemReorderPoint    = item.ReorderPoint,
                ProductReorderPoint = product.ReorderPoint,
                ProductReorderQty   = product.ReorderQty
            }
        ).ToListAsync();

        return rows
            .Select(x => new
            {
                x.ProductId,
                x.ProductUuid,
                x.ProductSku,
                x.ProductName,
                x.CategoryName,
                x.WarehouseId,
                x.WarehouseUuid,
                x.WarehouseName,
                x.QtyOnHand,
                x.QtyReserved,
                x.ProductReorderQty,
                EffectiveReorderPoint = x.ItemReorderPoint ?? x.ProductReorderPoint
            })
            .Where(x => x.EffectiveReorderPoint.HasValue
                     && x.EffectiveReorderPoint.Value > 0
                     && (x.QtyOnHand - x.QtyReserved) <= x.EffectiveReorderPoint.Value)
            .Select(x => new ReorderAlertModel
            {
                ProductId     = x.ProductId,
                ProductUuid   = x.ProductUuid,
                ProductSku    = x.ProductSku,
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
            join product in _db.Products       on item.ProductId      equals product.Id
            join wh      in _db.Warehouses     on item.WarehouseId    equals wh.Id
            select new { adj, item, product, wh };

        if (filter.ProductId.HasValue)
            query = query.Where(x => x.item.ProductId == filter.ProductId.Value);

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
                ProductId       = x.item.ProductId,
                ProductSku      = x.product.Sku,
                ProductName     = x.product.Name,
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
                .FirstOrDefaultAsync(i => i.ProductId == req.ProductId && i.WarehouseId == req.WarehouseId);

            if (item == null)
            {
                item = new InventoryItem
                {
                    ProductId   = req.ProductId,
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
                    ProductId       = req.ProductId,
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
                ProductId       = req.ProductId,
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
                ProductId       = adjustment.ProductId,
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

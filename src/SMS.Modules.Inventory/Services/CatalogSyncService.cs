using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Models;
using SMS.Shared.Common;

namespace SMS.Modules.Inventory.Services;

public interface ICatalogSyncService
{
    Task<CatalogSyncResponse> GetChangesAsync(DateTime? since, int? limit, CancellationToken ct = default);
}

/// <summary>
/// A37 §6 / D-16 — the catalog delta for offline clients: each collection's rows with ModifiedAt strictly after
/// <c>since</c>, at most about <c>limit</c> each (<see cref="SyncPaging"/>), in the caller's tenant scope (query filters).
/// Tax codes and units of measure come from Lookups through <see cref="ISyncLookupReader"/>; without it they are empty.
/// Hard deletes are not reported (residual): only rows that still exist, including deactivated ones.
/// </summary>
internal sealed class CatalogSyncService : ICatalogSyncService
{
    private readonly InventoryDbContext  _db;
    private readonly ISyncLookupReader?  _lookups;

    public CatalogSyncService(InventoryDbContext db, ISyncLookupReader? lookups = null)
    {
        _db      = db;
        _lookups = lookups;
    }

    public async Task<CatalogSyncResponse> GetChangesAsync(DateTime? since, int? limit, CancellationToken ct = default)
    {
        var take   = SyncPaging.ClampLimit(limit);
        var from   = SyncPaging.AsUtc(since);
        var result = new CatalogSyncResponse { ServerTime = DateTime.UtcNow };
        var cut    = new List<DateTime>();

        // EF contexts are not thread-safe: one collection after the other.
        var (products, moreProducts) = await SyncPaging.PageAsync(_db.Products.AsNoTracking(), from, take, ct);
        Track(products, moreProducts, p => p.ModifiedAt);
        result.Products = products.Select(p => new SyncProductModel
        {
            Id = p.Id, Uuid = p.Uuid, Sku = p.Sku, Name = p.Name, ShortName = p.ShortName, CategoryId = p.CategoryId,
            SubCategoryId = p.SubCategoryId, Brand = p.Brand, UomCode = p.UomCode, ProductType = p.ProductType,
            IsSaleable = p.IsSaleable, IsStockable = p.IsStockable, IsBatchTracked = p.IsBatchTracked,
            IsSerialTracked = p.IsSerialTracked, ImageUrl = p.ImageUrl, Status = p.Status, IsActive = p.IsActive,
            ModifiedAt = p.ModifiedAt
        }).ToList();

        var (variants, moreVariants) = await SyncPaging.PageAsync(_db.ProductVariants.AsNoTracking(), from, take, ct);
        Track(variants, moreVariants, v => v.ModifiedAt);
        result.Variants = variants.Select(v => new SyncVariantModel
        {
            Id = v.Id, Uuid = v.Uuid, ProductId = v.ProductId, Sku = v.Sku, VariantName = v.VariantName, Barcode = v.Barcode,
            SellingPrice = v.SellingPrice, IsDefault = v.IsDefault, IsActive = v.IsActive,
            IsAvailableForRetail = v.IsAvailableForRetail, IsAvailableForPos = v.IsAvailableForPos, SortOrder = v.SortOrder,
            ModifiedAt = v.ModifiedAt
        }).ToList();

        var (categories, moreCategories) = await SyncPaging.PageAsync(_db.ProductCategories.AsNoTracking(), from, take, ct);
        Track(categories, moreCategories, c => c.ModifiedAt);
        result.Categories = categories.Select(c => new SyncCategoryModel
        {
            Id = c.Id, Code = c.Code, Name = c.Name, IsActive = c.IsActive, ModifiedAt = c.ModifiedAt
        }).ToList();

        var (warehouses, moreWarehouses) = await SyncPaging.PageAsync(_db.Warehouses.AsNoTracking(), from, take, ct);
        Track(warehouses, moreWarehouses, w => w.ModifiedAt);
        result.Warehouses = warehouses.Select(w => new SyncWarehouseModel
        {
            Id = w.Id, Uuid = w.Uuid, Code = w.Code, Name = w.Name, IsActive = w.IsActive, ModifiedAt = w.ModifiedAt
        }).ToList();

        if (_lookups is not null)
        {
            result.TaxCodes = await LookupsAsync(ISyncLookupReader.TaxCodes);
            result.Uoms     = await LookupsAsync(ISyncLookupReader.Uoms);
        }

        result.HasMore   = cut.Count > 0;
        result.NextSince = cut.Count > 0 ? cut.Min() : result.ServerTime;
        return result;

        void Track<T>(List<T> rows, bool hasMore, Func<T, DateTime> modifiedAt)
        {
            if (hasMore && rows.Count > 0) cut.Add(rows.Max(modifiedAt));
        }

        async Task<List<SyncLookupModel>> LookupsAsync(string kind)
        {
            var page = await _lookups!.GetChangedAsync(kind, from, take, ct);
            Track(page.Rows.ToList(), page.HasMore, r => r.ModifiedAt);
            return page.Rows.Select(r => new SyncLookupModel
            {
                Id = r.Id, Name = r.Name, Notes = r.Notes, IsActive = r.IsActive, SortOrder = r.SortOrder, ModifiedAt = r.ModifiedAt
            }).ToList();
        }
    }
}

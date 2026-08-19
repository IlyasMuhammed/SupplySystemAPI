using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;

namespace SMS.Modules.Inventory.Services;

internal sealed class ProductSearchIndexService : IProductSearchIndexService
{
    private readonly InventoryDbContext _db;

    public ProductSearchIndexService(InventoryDbContext db) => _db = db;

    public async Task RebuildForVariantAsync(int variantId)
    {
        var variant = await _db.ProductVariants
            .Include(v => v.Product).ThenInclude(p => p.Category)
            .FirstOrDefaultAsync(v => v.Id == variantId);
        if (variant is null) return;

        var searchableValues = await _db.VariantAttributeValues
            .Where(av => av.VariantId == variantId && av.Attribute.IsSearchable)
            .OrderBy(av => av.Attribute.SortOrder).ThenBy(av => av.AttributeId)
            .Select(av => av.Value)
            .ToListAsync();

        var parts = new List<string> { variant.Product.Name, variant.VariantName, variant.Sku };
        if (!string.IsNullOrWhiteSpace(variant.Barcode)) parts.Add(variant.Barcode);
        parts.AddRange(searchableValues.Where(v => !string.IsNullOrWhiteSpace(v)));

        var entry = await _db.ProductSearchIndexEntries.FirstOrDefaultAsync(x => x.VariantId == variantId);
        if (entry is null)
        {
            entry = new ProductSearchIndex { VariantId = variantId, ProductId = variant.ProductId };
            _db.ProductSearchIndexEntries.Add(entry);
        }

        entry.ProductName   = variant.Product.Name;
        entry.ProductCode   = variant.Product.Sku;
        entry.Sku           = variant.Sku;
        entry.Barcode       = variant.Barcode;
        entry.VariantName   = variant.VariantName;
        entry.CategoryName  = variant.Product.Category?.Name;
        entry.Brand         = variant.Product.Brand;
        entry.SearchableText = string.Join(" ", parts);
        entry.IsActive      = variant.IsActive;
        entry.UpdatedDate   = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }

    public async Task RebuildAllAsync()
    {
        var variantIds = await _db.ProductVariants
            .Where(v => v.IsActive)
            .Select(v => v.Id)
            .ToListAsync();

        foreach (var variantId in variantIds)
            await RebuildForVariantAsync(variantId);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using SMS.Modules.Inventory.Domain;

namespace SMS.Modules.Inventory.Data;

// FSD Addendum 26 (PV-002) — sample dynamic-attribute catalog for the demo org, matching the
// task's own worked examples (Laptop's 6 attributes, Mobile's storage/ram reuse + imei_required).
// Runs at startup via UseInventoryModule(); every check here is idempotent (by AttributeName /
// Category name), so re-running on an already-seeded database is a no-op.
internal sealed class InventoryDataSeeder
{
    private readonly InventoryDbContext _db;
    private readonly ILogger<InventoryDataSeeder> _logger;
    public InventoryDataSeeder(InventoryDbContext db, ILogger<InventoryDataSeeder> logger)
    {
        _db = db;
        _logger = logger;
    }

    private sealed record AttrSeed(
        string Name, string Display, string DataType, string Control,
        string[]? Options, bool Required, bool Searchable);

    public async Task SeedAsync()
    {
        var laptop = await EnsureCategoryAsync("Laptop", "LAPTOP");
        var mobile = await EnsureCategoryAsync("Mobile", "MOBILE");

        var laptopAttrs = new[]
        {
            new AttrSeed("cpu",         "CPU",         "DROPDOWN", "DROPDOWN",
                ["Intel i3", "Intel i5", "Intel i7", "Intel i9", "AMD Ryzen 5", "AMD Ryzen 7"], true, true),
            new AttrSeed("ram",         "RAM",         "DROPDOWN", "DROPDOWN",
                ["4GB", "8GB", "16GB", "32GB", "64GB"], true, true),
            new AttrSeed("storage",     "Storage",     "DROPDOWN", "DROPDOWN",
                ["128GB SSD", "256GB SSD", "512GB SSD", "1TB SSD", "2TB SSD"], true, true),
            new AttrSeed("screen_size", "Screen Size", "DECIMAL",  "NUMBERBOX", null, false, false),
            new AttrSeed("color",       "Color",       "DROPDOWN", "DROPDOWN",
                ["Silver", "Black", "White", "Gray", "Blue"], false, true),
            new AttrSeed("gpu",         "GPU",         "TEXT",     "TEXTBOX",   null, false, true)
        };

        var order = 1;
        foreach (var a in laptopAttrs)
        {
            // One bad item (e.g. a transient DB error, or a concurrent-startup race between two
            // instances hitting the same unique index) must not silently prevent every attribute
            // after it in the list from ever being seeded on this pass — each item is independently
            // idempotent, so skip-and-continue lets a later restart still converge on the full set.
            try
            {
                var attr = await EnsureAttributeAsync(a);
                await EnsureLinkAsync(laptop.Id, attr.Id, a.Required, order);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to seed Laptop category attribute '{Attribute}'", a.Name);
            }
            order++;
        }

        // Mobile reuses storage + ram, adds imei_required.
        try
        {
            var storageAttr = await EnsureAttributeAsync(laptopAttrs[2]);
            var ramAttr     = await EnsureAttributeAsync(laptopAttrs[1]);
            var imeiAttr    = await EnsureAttributeAsync(
                new AttrSeed("imei_required", "IMEI Required", "BOOLEAN", "TOGGLE", null, true, false));

            await EnsureLinkAsync(mobile.Id, storageAttr.Id, true, 1);
            await EnsureLinkAsync(mobile.Id, ramAttr.Id,      true, 2);
            await EnsureLinkAsync(mobile.Id, imeiAttr.Id,     true, 3);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to seed Mobile category attributes");
        }
    }

    private async Task<ProductCategory> EnsureCategoryAsync(string name, string code)
    {
        var existing = await _db.ProductCategories.FirstOrDefaultAsync(c => c.Name == name);
        if (existing is not null) return existing;

        var category = new ProductCategory { Name = name, Code = code, IsActive = true };
        _db.ProductCategories.Add(category);
        await _db.SaveChangesAsync();
        return category;
    }

    private async Task<AttributeDefinition> EnsureAttributeAsync(AttrSeed a)
    {
        var existing = await _db.AttributeDefinitions.FirstOrDefaultAsync(x => x.AttributeName == a.Name);
        if (existing is not null) return existing;

        var attr = new AttributeDefinition
        {
            Uuid            = Guid.NewGuid(),
            AttributeName   = a.Name,
            DisplayName     = a.Display,
            DataType        = a.DataType,
            ControlType     = a.Control,
            DropdownOptions = a.Options is not null ? JsonSerializer.Serialize(a.Options) : null,
            IsRequired      = a.Required,
            IsSearchable    = a.Searchable,
            IsFilterable    = a.Searchable,
            SortOrder       = 0,
            IsActive        = true
        };
        _db.AttributeDefinitions.Add(attr);
        await _db.SaveChangesAsync();
        return attr;
    }

    private async Task EnsureLinkAsync(int categoryId, int attributeId, bool isRequired, int displayOrder)
    {
        var exists = await _db.CategoryAttributes
            .AnyAsync(ca => ca.CategoryId == categoryId && ca.AttributeId == attributeId);
        if (exists) return;

        _db.CategoryAttributes.Add(new CategoryAttribute
        {
            CategoryId   = categoryId,
            AttributeId  = attributeId,
            IsRequired   = isRequired,
            DisplayOrder = displayOrder
        });
        await _db.SaveChangesAsync();
    }
}

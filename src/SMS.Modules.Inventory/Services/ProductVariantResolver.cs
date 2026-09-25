using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Inventory.Services;

// Implements the SMS.Shared contract so other modules can bridge product-scoped document lines
// to the variant that actually holds stock, without referencing this project.
internal sealed class ProductVariantResolver : IProductVariantResolver
{
    private readonly InventoryDbContext _db;

    public ProductVariantResolver(InventoryDbContext db) => _db = db;

    public async Task<IReadOnlyDictionary<Guid, DefaultVariantResult>> ResolveDefaultVariantsAsync(
        IReadOnlyList<Guid> productUuids)
    {
        if (productUuids is null || productUuids.Count == 0)
            return new Dictionary<Guid, DefaultVariantResult>();

        var wanted = productUuids.Where(id => id != Guid.Empty).Distinct().ToList();
        if (wanted.Count == 0) return new Dictionary<Guid, DefaultVariantResult>();

        var matches = await _db.ProductVariants
            .Where(v => v.IsDefault && v.IsActive && wanted.Contains(v.Product.Uuid))
            .Select(v => new DefaultVariantResult(v.Product.Uuid, v.Uuid, v.Sku, v.VariantName))
            .ToListAsync();

        // GroupBy guards against a product that somehow has two defaults — taking the first is
        // arbitrary but deterministic, and far better than throwing on data we did not write.
        return matches
            .GroupBy(m => m.ProductUuid)
            .ToDictionary(g => g.Key, g => g.First());
    }

    public async Task<IReadOnlyDictionary<Guid, VariantDescription>> DescribeVariantsAsync(
        IReadOnlyList<Guid> variantUuids)
    {
        if (variantUuids is null || variantUuids.Count == 0)
            return new Dictionary<Guid, VariantDescription>();

        var wanted = variantUuids.Where(id => id != Guid.Empty).Distinct().ToList();
        if (wanted.Count == 0) return new Dictionary<Guid, VariantDescription>();

        var matches = await _db.ProductVariants
            .Where(v => v.IsActive && wanted.Contains(v.Uuid))
            .Select(v => new VariantDescription(
                v.Uuid, v.Product.Uuid, v.Sku, v.VariantName, v.Product.Name, v.IsDefault, v.Product.UomCode))
            .ToListAsync();

        return matches.ToDictionary(m => m.VariantUuid);
    }
}

// Implements the SMS.Shared contract for the "Available For" checkboxes on ProductVariant, so a
// module that never references Inventory (Demand's sale orders) can still refuse a variant that
// was not checked for the channel it is being sold through.
internal sealed class VariantAvailabilityService : IVariantAvailabilityService
{
    private readonly InventoryDbContext _db;

    public VariantAvailabilityService(InventoryDbContext db) => _db = db;

    public async Task<VariantChannelAvailability?> GetAvailabilityAsync(Guid variantUuid) =>
        await _db.ProductVariants
            .Where(v => v.Uuid == variantUuid && v.IsActive)
            .Select(v => new VariantChannelAvailability(
                v.IsDefault ? $"{v.Product.Name} ({v.Sku})" : $"{v.Product.Name} - {v.VariantName} ({v.Sku})",
                v.IsAvailableForRetail, v.IsAvailableForPos, v.IsAvailableForMirMiv,
                v.IsAvailableForProduction, v.IsAvailableForServices))
            .FirstOrDefaultAsync();
}

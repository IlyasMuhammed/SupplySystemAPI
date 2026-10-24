using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Inventory.Services;

/// <summary>
/// A38 D-6 (INV, D-26) — Inventory's read side for POS. Explicit organization (tenant filter bypassed, other organizations'
/// rows absent). Every variant is returned, inactive and non-POS ones included, so the PWA can drop what it cached; POS
/// decides what is sellable from IsActive / IsSaleable / IsAvailableForPos. ModifiedAt = the later of the variant's and its
/// product's. Price = the variant's SellingPrice, 0 when unset (POS-B may resolve IPricingService on top, D-6).
/// </summary>
internal sealed class PosCatalogSource : IPosCatalogSource
{
    private readonly InventoryDbContext _db;

    public PosCatalogSource(InventoryDbContext db) => _db = db;

    // Member-init (not a constructor) so EF can keep composing Where/OrderBy on the projection.
    private sealed class Row
    {
        public int      VariantId         { get; init; }
        public Guid     VariantUuid       { get; init; }
        public Guid     ProductUuid       { get; init; }
        public string   Sku               { get; init; } = string.Empty;
        public string   ProductName       { get; init; } = string.Empty;
        public string?  VariantName       { get; init; }
        public string?  Barcode           { get; init; }
        public int?     CategoryId        { get; init; }
        public string?  UomCode           { get; init; }
        public decimal? SellingPrice      { get; init; }
        public string?  ImageUrl          { get; init; }
        public bool     VariantActive     { get; init; }
        public bool     ProductActive     { get; init; }
        public bool     IsSaleable        { get; init; }
        public bool     IsAvailableForPos { get; init; }
        public bool     IsStockable       { get; init; }
        public DateTime ModifiedAt        { get; init; }
    }

    private IQueryable<Row> Rows(Guid organizationId) =>
        from v in _db.ProductVariants.IgnoreQueryFilters().AsNoTracking()
        join p in _db.Products.IgnoreQueryFilters().AsNoTracking() on v.ProductId equals p.Id
        where v.OrganizationId == organizationId && p.OrganizationId == organizationId
        select new Row
        {
            VariantId = v.Id, VariantUuid = v.Uuid, ProductUuid = p.Uuid, Sku = v.Sku, ProductName = p.Name,
            VariantName = v.IsDefault ? null : v.VariantName, Barcode = v.Barcode, CategoryId = p.CategoryId,
            UomCode = p.UomCode, SellingPrice = v.SellingPrice, ImageUrl = p.ImageUrl,
            VariantActive = v.IsActive, ProductActive = p.IsActive, IsSaleable = p.IsSaleable,
            IsAvailableForPos = v.IsAvailableForPos, IsStockable = p.IsStockable,
            ModifiedAt = v.ModifiedAt > p.ModifiedAt ? v.ModifiedAt : p.ModifiedAt
        };

    /// <summary>
    /// Strictly after <paramref name="since"/>, oldest first. A full page is extended with every remaining row sharing the
    /// last ModifiedAt, so a caller resuming from the last item's ModifiedAt (strictly after) cannot skip a tie.
    /// </summary>
    public async Task<IReadOnlyList<PosCatalogVariant>> GetChangedAsync(
        Guid organizationId, DateTime? since, int limit, CancellationToken ct = default)
    {
        var take = Math.Clamp(limit, 1, SyncPaging.MaxLimit);
        var from = SyncPaging.AsUtc(since);

        var query = Rows(organizationId);
        if (from is { } f) query = query.Where(r => r.ModifiedAt > f);

        var page = await query.OrderBy(r => r.ModifiedAt).ThenBy(r => r.VariantId).Take(take).ToListAsync(ct);

        if (page.Count == take)
        {
            var last = page[^1];
            var ties = await query
                .Where(r => r.ModifiedAt == last.ModifiedAt && r.VariantId > last.VariantId)
                .OrderBy(r => r.VariantId)
                .ToListAsync(ct);
            page.AddRange(ties);
        }

        return await MapAsync(organizationId, page, ct);
    }

    public async Task<IReadOnlyList<PosCatalogVariant>> GetByVariantUuidsAsync(
        Guid organizationId, IReadOnlyCollection<Guid> variantUuids, CancellationToken ct = default)
    {
        if (variantUuids.Count == 0) return [];
        var wanted = variantUuids.Distinct().ToList();
        var rows = await Rows(organizationId).Where(r => wanted.Contains(r.VariantUuid)).OrderBy(r => r.VariantId).ToListAsync(ct);
        return await MapAsync(organizationId, rows, ct);
    }

    public async Task<IReadOnlyList<PosCatalogVariant>> GetByProductUuidsAsync(
        Guid organizationId, IReadOnlyCollection<Guid> productUuids, CancellationToken ct = default)
    {
        if (productUuids.Count == 0) return [];
        var wanted = productUuids.Distinct().ToList();
        var rows = await Rows(organizationId).Where(r => wanted.Contains(r.ProductUuid)).OrderBy(r => r.VariantId).ToListAsync(ct);
        return await MapAsync(organizationId, rows, ct);
    }

    /// <summary>The organization's active product categories, by name.</summary>
    public async Task<IReadOnlyList<(int Id, string Name)>> GetCategoriesAsync(Guid organizationId, CancellationToken ct = default)
    {
        var rows = await _db.ProductCategories.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.OrganizationId == organizationId && c.IsActive)
            .OrderBy(c => c.Name).ThenBy(c => c.Id)
            .Select(c => new { c.Id, c.Name })
            .ToListAsync(ct);
        return rows.Select(c => (c.Id, c.Name)).ToList();
    }

    private async Task<IReadOnlyList<PosCatalogVariant>> MapAsync(Guid organizationId, List<Row> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];

        var categoryIds = rows.Where(r => r.CategoryId is not null).Select(r => r.CategoryId!.Value).Distinct().ToList();
        var categories = categoryIds.Count == 0
            ? new Dictionary<int, string>()
            : await _db.ProductCategories.IgnoreQueryFilters().AsNoTracking()
                .Where(c => c.OrganizationId == organizationId && categoryIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        return rows.Select(r => new PosCatalogVariant(
            r.VariantUuid, r.ProductUuid, r.Sku, r.ProductName, r.VariantName, r.Barcode,
            r.CategoryId, r.CategoryId is { } id ? categories.GetValueOrDefault(id) : null,
            r.UomCode, r.SellingPrice ?? 0m, r.ImageUrl,
            r.VariantActive && r.ProductActive, r.IsSaleable, r.IsAvailableForPos,
            DateTime.SpecifyKind(r.ModifiedAt, DateTimeKind.Utc), r.IsStockable)).ToList();
    }
}

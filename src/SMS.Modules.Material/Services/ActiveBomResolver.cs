using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Material.Services;

/// <summary>
/// A34 — the one "which recipe does this variant use today" rule (A30 §11.4), extracted from
/// <c>ProductionOrderRepository.ActiveBomAsync</c> so production order creation, <see cref="BomStructureReader"/> (the
/// lead-time calculator's recursion) and <see cref="ManufacturingReadiness"/> (the D-5 confirm gate) can never disagree.
/// <list type="number">
/// <item>Candidates: status ACTIVE, effective today (EffectiveFrom ≤ today ≤ EffectiveTo, either side open).</item>
/// <item>Pick: the variant's own BOM, else the product-general one (never another variant's); then the highest version.</item>
/// </list>
/// Warehouse plays no part (A31-C6).
/// </summary>
internal static class ActiveBomResolver
{
    internal static IQueryable<BillOfMaterial> Candidates(IQueryable<BillOfMaterial> boms, IReadOnlyCollection<Guid> productUuids, DateTime today)
    {
        var day      = today.Date;
        var products = productUuids.Distinct().ToList();
        return boms.Where(b => products.Contains(b.ProductUuid) && b.Status == BomStatus.Active &&
                               (b.EffectiveFrom == null || b.EffectiveFrom <= day) &&
                               (b.EffectiveTo == null || b.EffectiveTo >= day));
    }

    internal static BillOfMaterial? Pick(IEnumerable<BillOfMaterial> candidates, Guid productUuid, Guid variantUuid) =>
        candidates
            .Where(b => b.ProductUuid == productUuid && (b.ProductVariantUuid == null || b.ProductVariantUuid == variantUuid))
            .OrderByDescending(b => b.ProductVariantUuid.HasValue)
            .ThenByDescending(b => b.Version)
            .FirstOrDefault();

    /// <summary>
    /// Batched and organization-explicit (the EF tenant filter is off for super admins and in Hangfire): the active BOM of
    /// each variant of <paramref name="organizationId"/> that has one; variants of other organizations, or without a BOM,
    /// are absent. One variant read and one BOM read in total.
    /// </summary>
    internal static async Task<IReadOnlyDictionary<Guid, (BillOfMaterial Bom, VariantFacts Variant)>> ResolveAsync(
        MaterialDbContext db, InventoryDbContext inv, Guid organizationId, IReadOnlyCollection<Guid> variantUuids,
        bool includeLines, CancellationToken ct)
    {
        var variants = await VariantsAsync(inv, organizationId, variantUuids, ct);
        if (variants.Count == 0) return new Dictionary<Guid, (BillOfMaterial, VariantFacts)>();

        var query = Candidates(db.BillsOfMaterials.IgnoreQueryFilters().AsNoTracking().Where(b => b.OrganizationId == organizationId),
            variants.Values.Select(v => v.ProductUuid).ToList(), DateTime.UtcNow);
        if (includeLines) query = query.Include(b => b.Lines);
        var candidates = await query.ToListAsync(ct);

        var result = new Dictionary<Guid, (BillOfMaterial, VariantFacts)>();
        foreach (var variant in variants.Values)
            if (Pick(candidates, variant.ProductUuid, variant.Uuid) is { } bom)
                result[variant.Uuid] = (bom, variant);
        return result;
    }

    /// <summary>What the gate and the reader need to know of a variant and its product (organization explicit).</summary>
    internal sealed record VariantFacts(
        Guid Uuid, string VariantName, bool IsDefault, Guid ProductUuid, string ProductName, string SupplyMethod,
        bool HasProductionWarehouse)
    {
        /// <summary>"Product — Variant" (just the product for its default variant), for blocker messages.</summary>
        public string DisplayName =>
            IsDefault || string.IsNullOrWhiteSpace(VariantName) || VariantName == "Default" ? ProductName : $"{ProductName} — {VariantName}";

        public bool IsManufactured => SupplyMethod == Shared.Common.SupplyMethod.Manufacture;
    }

    internal static async Task<Dictionary<Guid, VariantFacts>> VariantsAsync(
        InventoryDbContext inv, Guid organizationId, IReadOnlyCollection<Guid> variantUuids, CancellationToken ct)
    {
        var uuids = variantUuids.Distinct().ToList();
        if (uuids.Count == 0) return [];

        // A production warehouse counts only while it exists, is active and is the organization's own: PO creation
        // refuses anything else.
        return await inv.ProductVariants.IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.OrganizationId == organizationId && uuids.Contains(v.Uuid))
            .Select(v => new VariantFacts(
                v.Uuid, v.VariantName, v.IsDefault, v.Product.Uuid, v.Product.Name, v.Product.SupplyMethod,
                v.Product.DefaultProductionWarehouse != null && v.Product.DefaultProductionWarehouse.IsActive
                    && v.Product.DefaultProductionWarehouse.OrganizationId == organizationId))
            .ToDictionaryAsync(v => v.Uuid, ct);
    }
}

/// <summary>A34 C4 — <see cref="IBomStructureReader"/> over <see cref="ActiveBomResolver"/>.</summary>
internal sealed class BomStructureReader : IBomStructureReader
{
    private readonly MaterialDbContext  _db;
    private readonly InventoryDbContext _inv;

    public BomStructureReader(MaterialDbContext db, InventoryDbContext inv)
    {
        _db  = db;
        _inv = inv;
    }

    public async Task<IReadOnlyDictionary<Guid, BomStructure>> GetActiveBomsAsync(
        Guid organizationId, IReadOnlyCollection<Guid> variantUuids, CancellationToken ct = default)
    {
        var resolved = await ActiveBomResolver.ResolveAsync(_db, _inv, organizationId, variantUuids, includeLines: true, ct);
        return resolved.ToDictionary(r => r.Key, r =>
        {
            var bom = r.Value.Bom;
            return new BomStructure(bom.UUID, bom.BomNumber, bom.Version, bom.BaseQuantity,
                bom.Lines.OrderBy(l => l.Sequence).ThenBy(l => l.Id)
                   .Select(l => new BomInput(l.MaterialVariantUuid, l.MaterialProductUuid, l.Quantity, l.ScrapPercentage))
                   .ToList());
        });
    }
}

/// <summary>
/// A34 D-5 — the material side of the confirm gate, batched. MANUFACTURING_DISABLED (the tenant feature) is the caller's;
/// this answers NOT_MANUFACTURED, BOM_MISSING and PRODUCTION_WAREHOUSE_MISSING with the same rules PO creation applies.
/// </summary>
internal sealed class ManufacturingReadiness : IManufacturingReadiness
{
    private readonly MaterialDbContext  _db;
    private readonly InventoryDbContext _inv;

    public ManufacturingReadiness(MaterialDbContext db, InventoryDbContext inv)
    {
        _db  = db;
        _inv = inv;
    }

    public async Task<IReadOnlyDictionary<Guid, ManufacturingReadinessInfo>> CheckAsync(
        Guid organizationId, IReadOnlyCollection<Guid> variantUuids, CancellationToken ct = default)
    {
        var variants = await ActiveBomResolver.VariantsAsync(_inv, organizationId, variantUuids, ct);
        if (variants.Count == 0) return new Dictionary<Guid, ManufacturingReadinessInfo>();

        var withBom = await ActiveBomResolver.ResolveAsync(_db, _inv, organizationId, variants.Keys.ToList(), includeLines: false, ct);
        return variants.Values.ToDictionary(v => v.Uuid, v => new ManufacturingReadinessInfo(
            v.Uuid, v.DisplayName, v.IsManufactured, withBom.ContainsKey(v.Uuid), v.HasProductionWarehouse));
    }
}

using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Material.Services;

/// <summary>
/// A36 — what Material needs to know about a catalog product, read straight from Inventory's context (which this module
/// already holds — the same arrangement as <see cref="ActiveBomResolver"/> and the BOM repository). Carries the A30
/// classification and the A36 D-2 service configuration, so BOM eligibility and service orders read one shape.
/// </summary>
/// <param name="DefaultVariantUuid">The product's default variant (null when it has none) — a service order's variant when none is named (D-5).</param>
/// <param name="DefaultVariantSellingPrice">That variant's selling price — a Time &amp; Material service's hourly rate (SVC-P-06).</param>
internal sealed record CatalogProductFacts(
    int      Id,
    Guid     Uuid,
    Guid     OrganizationId,
    string   Name,
    string   Sku,
    string?  UomCode,
    string   ProductType,
    string   SupplyMethod,
    bool     IsActive,
    bool     HasServiceBom,
    bool     IsSubcontractable,
    string?  ServiceInvoicingPolicy,
    string?  ServiceBillingModel,
    decimal? EstimatedDurationHours,
    Guid?    DefaultVariantUuid,
    decimal? DefaultVariantSellingPrice)
{
    public bool IsService => ProductType == Shared.Common.ProductType.Service;

    public bool IsManufactured => SupplyMethod == Shared.Common.SupplyMethod.Manufacture;

    /// <summary>A36 D-4 — a BOM may be made for it: manufactured, or a service with service BOM enabled.</summary>
    public bool IsBomEligible => IsManufactured || (IsService && HasServiceBom);
}

internal static class CatalogProductReader
{
    public static IQueryable<CatalogProductFacts> Project(IQueryable<Product> products) =>
        products.Select(p => new CatalogProductFacts(
            p.Id, p.Uuid, p.OrganizationId, p.Name, p.Sku, p.UomCode, p.ProductType, p.SupplyMethod, p.IsActive,
            p.HasServiceBom, p.IsSubcontractable, p.ServiceInvoicingPolicy, p.ServiceBillingModel, p.EstimatedDurationHours,
            p.Variants.Where(v => v.IsDefault).Select(v => (Guid?)v.Uuid).FirstOrDefault(),
            p.Variants.Where(v => v.IsDefault).Select(v => v.SellingPrice).FirstOrDefault()));

    /// <summary>One product of the caller's organization (EF tenant filter), or null.</summary>
    public static Task<CatalogProductFacts?> FindAsync(InventoryDbContext inv, Guid productUuid, CancellationToken ct = default) =>
        Project(inv.Products.AsNoTracking().Where(p => p.Uuid == productUuid)).FirstOrDefaultAsync(ct);

    /// <summary>
    /// Organization-explicit and batched (the tenant filter is off for super admins and in Hangfire): products of
    /// <paramref name="organizationId"/>; others are absent.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, CatalogProductFacts>> GetAsync(
        InventoryDbContext inv, Guid organizationId, IReadOnlyCollection<Guid> productUuids, CancellationToken ct = default)
    {
        var uuids = productUuids.Distinct().ToList();
        if (uuids.Count == 0) return new Dictionary<Guid, CatalogProductFacts>();

        return await Project(inv.Products.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.OrganizationId == organizationId && uuids.Contains(p.Uuid)))
            .ToDictionaryAsync(p => p.Uuid, ct);
    }
}

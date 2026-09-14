using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Inventory.Services;

// Implements the SMS.Shared.Common cross-module interface — mirrors ISupplierContactLookupService's
// placement/reasoning.
internal sealed class VariantSupplierResolver : IVariantSupplierResolver
{
    private readonly InventoryDbContext _db;
    public VariantSupplierResolver(InventoryDbContext db) => _db = db;

    public async Task<ActiveRateInfo?> GetActiveRateAsync(Guid variantUuid, Guid supplierId, DateOnly asOfDate)
    {
        var asOf = asOfDate.ToDateTime(TimeOnly.MinValue);

        var row = await _db.VariantSuppliers
            .Where(x => x.Variant.Uuid == variantUuid
                     && x.SupplierId == supplierId
                     && x.IsActive
                     && x.EffectiveFrom <= asOf
                     && (x.EffectiveTo == null || x.EffectiveTo >= asOf))
            .OrderByDescending(x => x.CreatedDate)
            .ThenByDescending(x => x.Id)
            .Select(x => new
            {
                x.Uuid,
                x.VendorUnitCost,
                x.CurrencyId,
                x.LeadTimeDays,
                x.MinOrderValue,
                x.DiscountTiers,
                x.EffectiveFrom,
                x.EffectiveTo
            })
            .FirstOrDefaultAsync();

        if (row is null) return null;

        return new ActiveRateInfo(
            row.Uuid, row.VendorUnitCost, row.CurrencyId, row.LeadTimeDays,
            row.MinOrderValue, row.DiscountTiers, row.EffectiveFrom, row.EffectiveTo);
    }
}

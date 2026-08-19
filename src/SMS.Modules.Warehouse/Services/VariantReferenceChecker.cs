using Microsoft.EntityFrameworkCore;
using SMS.Modules.Warehouse.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Warehouse.Services;

// PV-007 — lets Inventory's variant-delete flow ask "has this variant ever appeared on a GRN
// line" without a project reference back into Warehouse.
internal sealed class GrnLineVariantReferenceChecker : IVariantReferenceChecker
{
    private readonly WarehouseDbContext _db;
    public GrnLineVariantReferenceChecker(WarehouseDbContext db) => _db = db;

    public Task<bool> IsVariantReferencedAsync(Guid variantUuid) =>
        _db.GrnLines.AnyAsync(l => l.VariantUuid == variantUuid);
}

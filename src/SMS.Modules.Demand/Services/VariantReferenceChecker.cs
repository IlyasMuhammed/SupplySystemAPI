using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Demand.Services;

// PV-007 — lets Inventory's variant-delete flow ask "has this variant ever appeared on a PO
// line" without a project reference back into Demand.
internal sealed class PoLineVariantReferenceChecker : IVariantReferenceChecker
{
    private readonly DemandDbContext _db;
    public PoLineVariantReferenceChecker(DemandDbContext db) => _db = db;

    public Task<bool> IsVariantReferencedAsync(Guid variantUuid) =>
        _db.PurchaseOrderLines.AnyAsync(l => l.VariantUuid == variantUuid);
}

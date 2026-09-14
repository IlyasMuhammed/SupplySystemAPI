using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Demand.Services;

// Lets Suppliers' supplier-delete flow ask "has this supplier ever appeared on a PO or RFQ
// response" without a project reference back into Demand — mirrors IVariantReferenceChecker.
internal sealed class DemandSupplierReferenceChecker : ISupplierReferenceChecker
{
    private readonly DemandDbContext _db;
    public DemandSupplierReferenceChecker(DemandDbContext db) => _db = db;

    public async Task<bool> IsSupplierReferencedAsync(Guid supplierId) =>
        await _db.PurchaseOrders.AnyAsync(p => p.SupplierId == supplierId) ||
        await _db.VendorResponses.AnyAsync(v => v.SupplierId == supplierId);
}

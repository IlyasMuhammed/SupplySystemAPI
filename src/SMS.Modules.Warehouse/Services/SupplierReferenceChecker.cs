using Microsoft.EntityFrameworkCore;
using SMS.Modules.Warehouse.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Warehouse.Services;

// Lets Suppliers' supplier-delete flow ask "has this supplier ever appeared on a GRN or return
// order" without a project reference back into Warehouse — mirrors IVariantReferenceChecker.
internal sealed class WarehouseSupplierReferenceChecker : ISupplierReferenceChecker
{
    private readonly WarehouseDbContext _db;
    public WarehouseSupplierReferenceChecker(WarehouseDbContext db) => _db = db;

    public async Task<bool> IsSupplierReferencedAsync(Guid supplierId) =>
        await _db.Grns.AnyAsync(g => g.SupplierId == supplierId) ||
        await _db.SupplierReturnOrders.AnyAsync(s => s.SupplierId == supplierId);
}

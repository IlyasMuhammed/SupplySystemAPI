using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.Services;

// Lets Suppliers' supplier-delete flow ask "has this supplier ever appeared on an invoice,
// payment, or supplier payment" without a project reference back into Finance — mirrors
// IVariantReferenceChecker.
internal sealed class FinanceSupplierReferenceChecker : ISupplierReferenceChecker
{
    private readonly FinanceDbContext _db;
    public FinanceSupplierReferenceChecker(FinanceDbContext db) => _db = db;

    public async Task<bool> IsSupplierReferencedAsync(Guid supplierId) =>
        await _db.Invoices.AnyAsync(i => i.SupplierId == supplierId) ||
        await _db.Payments.AnyAsync(p => p.SupplierId == supplierId) ||
        await _db.SupplierPayments.AnyAsync(p => p.SupplierId == supplierId);
}

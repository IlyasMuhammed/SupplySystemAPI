using Microsoft.EntityFrameworkCore;
using SMS.Modules.Suppliers.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Suppliers.Services;

// Implements the SMS.Shared.Common cross-module interface so Inventory's Rate Card comparison
// grid (and Finance's invoice/payment supplier-name resolution) can show supplier names without a
// project reference to Suppliers.
internal sealed class SupplierNameLookupService : ISupplierNameLookupService
{
    private readonly SuppliersDbContext _db;
    public SupplierNameLookupService(SuppliersDbContext db) => _db = db;

    public async Task<IReadOnlyDictionary<Guid, string>> GetNamesAsync(IReadOnlyList<Guid> supplierIds)
    {
        if (supplierIds.Count == 0)
            return new Dictionary<Guid, string>();

        // P1-06 (Addendum 29 §1.7) noted every consumer at the time (Rate Card comparison,
        // Scorecard, vendor invoices/payments) was vendor-scoped by construction, and filtered to
        // IsVendor so that stayed true rather than relying on it by accident. A29-P4-07 is the
        // first consumer that isn't — a sale order's PartnerId is a customer — so the filter would
        // now silently return nothing for exactly the id it's asked to resolve. Dropping it only
        // widens the result set (a caller that only ever passes vendor ids sees the same rows as
        // before); nothing needs isolating from it.
        return await _db.BusinessPartners
            .Where(s => supplierIds.Contains(s.UUID))
            .ToDictionaryAsync(s => s.UUID, s => s.SupplierName);
    }
}

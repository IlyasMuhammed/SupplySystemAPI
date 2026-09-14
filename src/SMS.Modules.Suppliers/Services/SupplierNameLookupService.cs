using Microsoft.EntityFrameworkCore;
using SMS.Modules.Suppliers.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Suppliers.Services;

// Implements the SMS.Shared.Common cross-module interface so Inventory's Rate Card comparison
// grid can show supplier names without a project reference to Suppliers.
internal sealed class SupplierNameLookupService : ISupplierNameLookupService
{
    private readonly SuppliersDbContext _db;
    public SupplierNameLookupService(SuppliersDbContext db) => _db = db;

    public async Task<IReadOnlyDictionary<Guid, string>> GetNamesAsync(IReadOnlyList<Guid> supplierIds)
    {
        if (supplierIds.Count == 0)
            return new Dictionary<Guid, string>();

        return await _db.Suppliers
            .Where(s => supplierIds.Contains(s.UUID))
            .ToDictionaryAsync(s => s.UUID, s => s.SupplierName);
    }
}

using Microsoft.EntityFrameworkCore;
using SMS.Modules.Suppliers.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Suppliers.Services;

// Implements the SMS.Shared.Common cross-module interface so Inventory's Rate Card comparison
// grid can show Scorecard grades without a project reference to Suppliers.
internal sealed class SupplierScoreLookupService : ISupplierScoreLookupService
{
    private readonly SuppliersDbContext _db;
    public SupplierScoreLookupService(SuppliersDbContext db) => _db = db;

    public async Task<IReadOnlyDictionary<Guid, string>> GetLatestGradesAsync(IReadOnlyList<Guid> supplierIds)
    {
        if (supplierIds.Count == 0)
            return new Dictionary<Guid, string>();

        var snapshots = await _db.SupplierScoreSnapshots
            .Where(s => supplierIds.Contains(s.SupplierId))
            .Select(s => new { s.SupplierId, s.Grade, s.PeriodEnd })
            .ToListAsync();

        return snapshots
            .GroupBy(s => s.SupplierId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(s => s.PeriodEnd).First().Grade);
    }
}

using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Import;
using SMS.Modules.Suppliers.Data;

namespace SMS.Modules.Suppliers.Services;

/// <summary>
/// A40 (TFIN) — business partners by code for Finance's opening-balance import targets (<see cref="IImportPartnerLookup"/>).
/// Explicit organization (the tenant filter is bypassed and the organization applied by hand); deleted partners are absent.
/// </summary>
internal sealed class ImportPartnerLookup : IImportPartnerLookup
{
    private readonly SuppliersDbContext _db;

    public ImportPartnerLookup(SuppliersDbContext db) => _db = db;

    public async Task<IReadOnlyDictionary<string, ImportPartner>> FindByCodesAsync(
        Guid organizationId, IReadOnlyCollection<string> codes, CancellationToken ct = default)
    {
        var wanted = codes.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim().ToUpperInvariant()).Distinct().ToList();
        if (wanted.Count == 0) return new Dictionary<string, ImportPartner>();

        var rows = await _db.BusinessPartners.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.OrganizationId == organizationId && !p.IsDelete && wanted.Contains(p.SupplierCode.ToUpper()))
            .Select(p => new ImportPartner(p.UUID, p.SupplierCode, p.SupplierName, p.IsCustomer, p.IsVendor, p.IsActive, p.PaymentTermsDays))
            .ToListAsync(ct);

        var result = new Dictionary<string, ImportPartner>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows) result.TryAdd(row.Code.Trim().ToUpperInvariant(), row);
        return result;
    }

    public async Task<IReadOnlyList<string>> ListCodesAsync(Guid organizationId, bool customers, int max, CancellationToken ct = default)
    {
        var q = _db.BusinessPartners.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.OrganizationId == organizationId && !p.IsDelete && p.IsActive && p.SupplierCode != "");
        q = customers ? q.Where(p => p.IsCustomer && !p.IsSystem) : q.Where(p => p.IsVendor);

        return await q.OrderBy(p => p.SupplierCode).Select(p => p.SupplierCode).Take(Math.Max(0, max)).ToListAsync(ct);
    }
}

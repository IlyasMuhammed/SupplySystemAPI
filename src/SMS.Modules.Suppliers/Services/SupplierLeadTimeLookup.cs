using Microsoft.EntityFrameworkCore;
using SMS.Modules.Suppliers.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Suppliers.Services;

/// <summary>
/// A34 D-11 tier 4 — a supplier's own lead time (<c>BusinessPartner.LeadTimeDays</c>) for Inventory's lead-time
/// calculator, which can't reference Suppliers. The organization is explicit and the tenant filter set aside: the
/// caller may be a super admin or a background job. Suppliers with no lead time, deleted ones and other organizations'
/// are absent; an inactive supplier's lead time is still a fact and is returned.
/// </summary>
internal sealed class SupplierLeadTimeLookup : ISupplierLeadTimeLookup
{
    private readonly SuppliersDbContext _db;

    public SupplierLeadTimeLookup(SuppliersDbContext db) => _db = db;

    public async Task<IReadOnlyDictionary<Guid, int>> GetAsync(
        Guid organizationId, IReadOnlyCollection<Guid> supplierUuids, CancellationToken ct = default)
    {
        var wanted = (supplierUuids ?? []).Where(u => u != Guid.Empty).Distinct().ToList();
        if (wanted.Count == 0) return new Dictionary<Guid, int>();

        var rows = await _db.BusinessPartners.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.OrganizationId == organizationId && wanted.Contains(p.UUID) && !p.IsDelete
                        && p.LeadTimeDays != null && p.LeadTimeDays >= 0)
            .Select(p => new { p.UUID, Days = p.LeadTimeDays!.Value })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.UUID, r => r.Days);
    }
}

using Microsoft.EntityFrameworkCore;
using SMS.Modules.Tenancy.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Tenancy.Services;

/// <summary>
/// A32 PA-03 — every organization's id, for another module's startup backfill of per-organization data (Demand's
/// rejection reasons). Inactive organizations included: one can be reactivated and must find its data in place.
/// </summary>
internal sealed class OrganizationDirectory : IOrganizationDirectory
{
    private readonly TenancyDbContext _db;
    public OrganizationDirectory(TenancyDbContext db) => _db = db;

    public async Task<IReadOnlyList<Guid>> GetOrganizationIdsAsync(CancellationToken ct = default) =>
        await _db.Organizations.IgnoreQueryFilters().AsNoTracking().Select(o => o.Id).ToListAsync(ct);
}

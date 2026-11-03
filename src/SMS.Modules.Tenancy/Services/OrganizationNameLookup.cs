using Microsoft.EntityFrameworkCore;
using SMS.Modules.Tenancy.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Tenancy.Services;

/// <summary>A49 — see <see cref="IOrganizationNameLookup"/>.</summary>
internal sealed class OrganizationNameLookup : IOrganizationNameLookup
{
    private readonly TenancyDbContext _db;
    public OrganizationNameLookup(TenancyDbContext db) => _db = db;

    public async Task<string?> GetNameAsync(Guid organizationId, CancellationToken ct = default) =>
        await _db.Organizations.IgnoreQueryFilters().AsNoTracking()
            .Where(o => o.Id == organizationId).Select(o => o.OrgName).FirstOrDefaultAsync(ct);
}

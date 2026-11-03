using Microsoft.EntityFrameworkCore;
using SMS.Modules.Tenancy.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Tenancy.Services;

/// <summary>A49 — see <see cref="IOrganizationTimeZoneLookup"/>.</summary>
internal sealed class OrganizationTimeZoneLookup : IOrganizationTimeZoneLookup
{
    private readonly TenancyDbContext _db;
    public OrganizationTimeZoneLookup(TenancyDbContext db) => _db = db;

    public async Task<string?> GetTimeZoneAsync(Guid organizationId, CancellationToken ct = default)
    {
        var zone = await _db.Organizations.IgnoreQueryFilters().AsNoTracking()
            .Where(o => o.Id == organizationId).Select(o => o.TimeZone).FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(zone) ? null : zone.Trim();
    }
}

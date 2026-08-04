using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SMS.Modules.Tenancy.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Tenancy.Services;

// Implements the SMS.Shared.Common cross-module interface consumed by TenantMiddleware and
// FeatureAuthorizationFilter (SMS.Shared) — same cross-module pattern as OrganizationStatusService.
// Cached per org id with a 5-minute TTL (MT-004 acceptance criteria) so tenant resolution and
// feature checks don't hit the database on every request; TenancyService proactively invalidates
// the entry on writes so most toggles/deactivations take effect immediately rather than waiting
// out the TTL.
internal sealed class TenantSnapshotProvider : ITenantSnapshotProvider
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly TenancyDbContext _db;
    private readonly IMemoryCache _cache;

    public TenantSnapshotProvider(TenancyDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    private static string CacheKey(Guid organizationId) => $"tenant-snapshot:{organizationId}";

    public async Task<TenantSnapshot?> GetSnapshotAsync(Guid organizationId)
    {
        if (_cache.TryGetValue<TenantSnapshot>(CacheKey(organizationId), out var cached))
            return cached;

        var isActive = await _db.Organizations
            .AsNoTracking()
            .Where(o => o.Id == organizationId)
            .Select(o => (bool?)o.IsActive)
            .FirstOrDefaultAsync();

        if (isActive is null) return null;

        var enabledCodes = await _db.OrganizationFeatures
            .AsNoTracking()
            .Where(of => of.OrganizationId == organizationId && of.IsEnabled)
            .Join(_db.FeatureDefinitions.AsNoTracking(),
                of => of.FeatureDefinitionId, fd => fd.Id, (of, fd) => fd.FeatureCode)
            .ToListAsync();

        var snapshot = new TenantSnapshot(isActive.Value, new HashSet<string>(enabledCodes, StringComparer.OrdinalIgnoreCase));
        _cache.Set(CacheKey(organizationId), snapshot, CacheTtl);
        return snapshot;
    }

    public void Invalidate(Guid organizationId) => _cache.Remove(CacheKey(organizationId));
}

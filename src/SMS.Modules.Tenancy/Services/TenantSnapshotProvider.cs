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
// A37 D-6 — the snapshot carries every code's access level (ModuleState.Evaluate); EnabledFeatureCodes = usable now.
// A grace period ending inside the TTL is honoured by the filter, which compares GraceEndsAt with the clock.
internal sealed class TenantSnapshotProvider : ITenantSnapshotProvider
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly TenancyDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _clock;

    public TenantSnapshotProvider(TenancyDbContext db, IMemoryCache cache, TimeProvider? clock = null)
    {
        _db = db;
        _cache = cache;
        _clock = clock ?? TimeProvider.System;
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

        var states = await ModuleState.LoadAsync(_db, organizationId, track: false);
        var snapshot = ModuleState.ToSnapshot(isActive.Value, states, _clock.GetUtcNow().UtcDateTime);
        _cache.Set(CacheKey(organizationId), snapshot, CacheTtl);
        return snapshot;
    }

    public void Invalidate(Guid organizationId) => _cache.Remove(CacheKey(organizationId));
}

/// <summary>A37 D-9 — Shared IModuleGate over the cached snapshot: licensed AND enabled AND parent enabled; grace = off.</summary>
internal sealed class ModuleGate : IModuleGate
{
    private readonly ITenantSnapshotProvider _snapshots;

    public ModuleGate(ITenantSnapshotProvider snapshots) => _snapshots = snapshots;

    public async Task<bool> IsEnabledAsync(Guid organizationId, string featureCode, CancellationToken ct = default)
    {
        var snapshot = await _snapshots.GetSnapshotAsync(organizationId);
        return snapshot is { IsActive: true } && snapshot.EnabledFeatureCodes.Contains(featureCode);
    }
}

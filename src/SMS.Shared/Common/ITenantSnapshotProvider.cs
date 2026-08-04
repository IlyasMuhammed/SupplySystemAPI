namespace SMS.Shared.Common;

// A per-org read model cached with a short TTL (MT-004: 5 minutes) so TenantMiddleware and
// FeatureAuthorizationFilter don't hit the database on every request, while still letting an
// org deactivation or feature toggle propagate to already-issued JWTs within a bounded window.
public sealed record TenantSnapshot(bool IsActive, IReadOnlySet<string> EnabledFeatureCodes);

public interface ITenantSnapshotProvider
{
    // Null means the organization id doesn't resolve to a real row (e.g. a stale/bad claim) —
    // callers should treat that the same as "inactive".
    Task<TenantSnapshot?> GetSnapshotAsync(Guid organizationId);

    // Proactively drops the cached entry so a Super Admin's status/feature change is reflected
    // on the very next request, rather than waiting out the TTL.
    void Invalidate(Guid organizationId);
}

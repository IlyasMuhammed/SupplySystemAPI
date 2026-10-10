namespace SMS.Shared.Common;

// A per-org read model cached with a short TTL (MT-004: 5 minutes) so TenantMiddleware and
// FeatureAuthorizationFilter don't hit the database on every request, while still letting an
// org deactivation or feature toggle propagate to already-issued JWTs within a bounded window.
//
// A37 D-6 — EnabledFeatureCodes keeps its meaning for every existing caller: the codes usable NOW (licensed, switched
// on, and — for a sub-feature — its parent module usable too; a module in its grace period is NOT in it). Access adds
// the per-code state the feature filter needs for the grace / read-only rules. A snapshot built without Access (tests,
// older callers) treats every code outside EnabledFeatureCodes as never licensed.
public sealed record TenantSnapshot(bool IsActive, IReadOnlySet<string> EnabledFeatureCodes)
{
    private static readonly IReadOnlyDictionary<string, FeatureAccess> NoAccess =
        new Dictionary<string, FeatureAccess>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per feature code: how far it may be used now, and which code (itself or its parent module) limits it.</summary>
    public IReadOnlyDictionary<string, FeatureAccess> Access { get; init; } = NoAccess;

    /// <summary>The access for <paramref name="code"/> at <paramref name="utcNow"/> — an expired grace reads as read-only.</summary>
    public FeatureAccess AccessFor(string code, DateTime utcNow)
    {
        if (EnabledFeatureCodes.Contains(code))
            return Access.TryGetValue(code, out var full) ? full with { Level = FeatureAccessLevel.Full } : new FeatureAccess(code, code, code, FeatureAccessLevel.Full, null);
        if (!Access.TryGetValue(code, out var access))
            return new FeatureAccess(code, code, code, FeatureAccessLevel.None, null);
        return access.Level == FeatureAccessLevel.Grace && access.GraceEndsAt is { } ends && ends <= utcNow
            ? access with { Level = FeatureAccessLevel.ReadOnly, GraceEndsAt = null }
            : access;
    }
}

/// <summary>A37 D-6 — Full: everything. Grace: reads, and writes on an existing record. ReadOnly: reads (the org once
/// had it). None: nothing (never licensed / never enabled).</summary>
public enum FeatureAccessLevel { None = 0, ReadOnly = 1, Grace = 2, Full = 3 }

/// <param name="Code">The feature code asked about.</param>
/// <param name="BlockingCode">The code that limits it — itself, or its parent module when that is the lower one.</param>
/// <param name="BlockingName">Display name of <paramref name="BlockingCode"/> (for the 403 message).</param>
public sealed record FeatureAccess(string Code, string BlockingCode, string BlockingName, FeatureAccessLevel Level, DateTime? GraceEndsAt);

public interface ITenantSnapshotProvider
{
    // Null means the organization id doesn't resolve to a real row (e.g. a stale/bad claim) —
    // callers should treat that the same as "inactive".
    Task<TenantSnapshot?> GetSnapshotAsync(Guid organizationId);

    // Proactively drops the cached entry so a Super Admin's status/feature change is reflected
    // on the very next request, rather than waiting out the TTL.
    void Invalidate(Guid organizationId);
}

namespace SMS.Shared.Common;

/// <summary>
/// A49 — tenant.Organizations.TimeZone as stored: free text, not validated when saved, often empty. The caller must validate it
/// (e.g. TimeZoneInfo.TryFindSystemTimeZoneById) and choose its own fallback. Implemented in Tenancy; take it as an optional parameter.
/// </summary>
public interface IOrganizationTimeZoneLookup
{
    /// <summary>The stored value, or null when unset or the organization is unknown.</summary>
    Task<string?> GetTimeZoneAsync(Guid organizationId, CancellationToken ct = default);
}

namespace SMS.Shared.Common;

/// <summary>
/// A49 — an organization's display name, for documents a module generates (HR letters). Implemented in Tenancy.
/// Take it as an optional constructor parameter: a host or test without Tenancy passes null.
/// </summary>
public interface IOrganizationNameLookup
{
    /// <summary>The name, or null for an unknown organization.</summary>
    Task<string?> GetNameAsync(Guid organizationId, CancellationToken ct = default);
}

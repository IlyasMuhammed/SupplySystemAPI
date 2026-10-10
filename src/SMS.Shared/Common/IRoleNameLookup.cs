namespace SMS.Shared.Common;

/// <summary>
/// A36 — role (team) names by id, for a module that shows "Team: …" without referencing Auth (service orders assign a
/// team as an auth role, D-5). Implemented in SMS.Modules.Auth; optional for consumers (absent in unit hosts).
/// </summary>
public interface IRoleNameLookup
{
    /// <summary>Names of the roles that exist; unknown ids are absent.</summary>
    Task<IReadOnlyDictionary<int, string>> GetNamesAsync(IReadOnlyCollection<int> roleIds, CancellationToken ct = default);
}

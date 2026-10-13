using SMS.Shared.Common;

namespace SMS.Modules.Auth.Services;

/// <summary>
/// A41 D-5 — the organization's users for other modules (CRM assignment pickers and owner names). The same explicit-org
/// queries and effective permissions as the POS user lookups (<see cref="PosPinService"/>), without anything PIN-related.
/// </summary>
internal sealed class OrgUserDirectory : IOrgUserDirectory
{
    private readonly IPosPinService _users;

    public OrgUserDirectory(IPosPinService users) => _users = users;

    public async Task<IReadOnlyList<OrgUserInfo>> GetUsersAsync(Guid organizationId, IReadOnlyCollection<int> userIds, CancellationToken ct = default) =>
        Map(await _users.GetUsersAsync(organizationId, userIds, ct));

    public async Task<IReadOnlyList<OrgUserInfo>> SearchAsync(Guid organizationId, string? search, int limit, CancellationToken ct = default) =>
        Map(await _users.SearchUsersAsync(organizationId, search, limit, ct));

    private static IReadOnlyList<OrgUserInfo> Map(IReadOnlyList<PosUserInfo> users) =>
        users.Select(u => new OrgUserInfo(u.UserId, u.Name, u.Email, u.IsActive, u.Permissions)).ToList();
}

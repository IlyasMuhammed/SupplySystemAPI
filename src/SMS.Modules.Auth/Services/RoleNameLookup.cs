using Microsoft.EntityFrameworkCore;
using SMS.Modules.Auth.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Auth.Services;

/// <summary>A36 — <see cref="IRoleNameLookup"/> over the roles the caller can see (global and their organization's own).</summary>
internal sealed class RoleNameLookup : IRoleNameLookup
{
    private readonly AuthDbContext _db;

    public RoleNameLookup(AuthDbContext db) => _db = db;

    public async Task<IReadOnlyDictionary<int, string>> GetNamesAsync(IReadOnlyCollection<int> roleIds, CancellationToken ct = default)
    {
        var ids = roleIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<int, string>();
        return await _db.Roles.AsNoTracking()
            .Where(r => ids.Contains(r.RoleID))
            .ToDictionaryAsync(r => r.RoleID, r => r.Name, ct);
    }
}

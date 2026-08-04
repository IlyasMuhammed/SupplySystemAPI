using Microsoft.EntityFrameworkCore;
using SMS.Modules.Auth.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Auth.Services;

internal sealed class UserQueryService : IUserQueryService
{
    private readonly AuthDbContext _db;

    public UserQueryService(AuthDbContext db) => _db = db;

    public async Task<UserIdentity?> GetUserAsync(int userId)
    {
        var row = await _db.UserAccounts
            .Where(u => u.UserID == userId && u.IsActive && !u.IsDelete)
            .Select(u => new { u.UserID, u.FirstName, u.LastName })
            .FirstOrDefaultAsync();

        return row is null ? null
            : new UserIdentity(row.UserID, BuildName(row.FirstName, row.LastName));
    }

    public async Task<IReadOnlyList<UserIdentity>> GetUsersAsync(IReadOnlyList<int> userIds)
    {
        var rows = await _db.UserAccounts
            .Where(u => userIds.Contains(u.UserID) && u.IsActive && !u.IsDelete)
            .Select(u => new { u.UserID, u.FirstName, u.LastName })
            .ToListAsync();

        return rows.Select(r => new UserIdentity(r.UserID, BuildName(r.FirstName, r.LastName)))
                   .ToList();
    }

    public async Task<IReadOnlyList<UserIdentity>> GetActiveUsersByRoleAsync(int roleId)
    {
        var rows = await _db.UserAccounts
            .Where(u => u.RoleID == roleId && u.IsActive && !u.IsDelete)
            .Select(u => new { u.UserID, u.FirstName, u.LastName })
            .ToListAsync();

        return rows.Select(r => new UserIdentity(r.UserID, BuildName(r.FirstName, r.LastName)))
                   .ToList();
    }

    public async Task<string?> GetUserEmailAsync(int userId)
    {
        return await _db.UserAccounts
            .Where(u => u.UserID == userId && u.IsActive && !u.IsDelete)
            .Select(u => u.Email)
            .FirstOrDefaultAsync();
    }

    public async Task<bool> IsSystemAdminAsync(int userId) =>
        await _db.UserAccounts.AnyAsync(u =>
            u.UserID == userId && u.RoleID == (int)EnumRole.SystemAdmin && u.IsActive && !u.IsDelete);

    public async Task<bool> IsOrgAdminAsync(int userId) =>
        await _db.UserAccounts.AnyAsync(u =>
            u.UserID == userId && u.RoleID == (int)EnumRole.OrgAdmin && u.IsActive && !u.IsDelete);

    public async Task<int?> GetFirstSystemAdminUserIdAsync()
    {
        var userId = await _db.UserAccounts
            .Where(u => u.RoleID == (int)EnumRole.SystemAdmin && u.IsActive && !u.IsDelete)
            .OrderBy(u => u.CreatedDate)
            .Select(u => (int?)u.UserID)
            .FirstOrDefaultAsync();

        return userId;
    }

    private static string BuildName(string firstName, string? lastName)
        => string.IsNullOrWhiteSpace(lastName)
            ? firstName
            : $"{firstName} {lastName}";
}

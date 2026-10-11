using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Auth.Data;
using SMS.Modules.Auth.Domain;
using SMS.Modules.Auth.Repositories;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Auth.Services;

/// <summary>
/// A38 D-4 — cashier PINs and PIN sign-in tokens (<see cref="IPosPinService"/>). Every query takes the organization
/// explicitly and ignores the tenant filter (it is off for super admins and in jobs, and a terminal's caller is not the
/// cashier); another organization's user is simply absent. The PIN itself is never logged or returned; only its hash
/// (ASP.NET Identity v3, the password hasher's own format) leaves this class, for the bound-terminal offline cache.
/// PIN failures never touch the password lockout (FailedLoginAttempts / LockedUntil) — terminal lockout is the POS
/// module's (API-CONTRACT §3).
/// </summary>
internal sealed partial class PosPinService : IPosPinService
{
    private const int MaxSearch = 100;

    private readonly AuthDbContext _db;
    private readonly IPasswordHasher<UserAccount> _hasher;
    private readonly ITokenService _tokens;
    private readonly ISuperAdminService _superAdmins;
    private readonly IOrganizationStatusService _orgStatus;

    public PosPinService(AuthDbContext db, IPasswordHasher<UserAccount> hasher, ITokenService tokens,
        ISuperAdminService superAdmins, IOrganizationStatusService orgStatus)
    {
        _db = db;
        _hasher = hasher;
        _tokens = tokens;
        _superAdmins = superAdmins;
        _orgStatus = orgStatus;
    }

    // [0-9], not \d: .NET's \d also matches non-ASCII digits (e.g. full-width "１２３４"), which no keypad sends.
    [GeneratedRegex(@"^[0-9]{4,6}$")]
    private static partial Regex PinFormat();

    internal static bool IsValidPin(string? pin) => pin is not null && PinFormat().IsMatch(pin);

    /// <summary>Users of the organization that are not deleted (tenant filter off — the organization is explicit).</summary>
    private IQueryable<UserAccount> OrgUsers(Guid organizationId) =>
        _db.UserAccounts.IgnoreQueryFilters().Where(u => u.OrganizationId == organizationId && !u.IsDelete);

    public async Task SetPinAsync(Guid organizationId, int userId, string? pin, CancellationToken ct = default)
    {
        var user = await OrgUsers(organizationId).FirstOrDefaultAsync(u => u.UserID == userId, ct)
            ?? throw new NotFoundException("User not found.");

        if (string.IsNullOrEmpty(pin))
        {
            user.PosPinHash = null;
        }
        else
        {
            if (!IsValidPin(pin)) throw new BadRequestException("PIN must be 4 to 6 digits.");
            user.PosPinHash = _hasher.HashPassword(user, pin);
        }
        user.UpdateDate = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<bool> VerifyAsync(Guid organizationId, int userId, string pin, CancellationToken ct = default)
    {
        if (!IsValidPin(pin)) return false;
        var user = await OrgUsers(organizationId)
            .FirstOrDefaultAsync(u => u.UserID == userId && u.IsActive && u.PosPinHash != null, ct);
        return user is not null && Matches(user, pin);
    }

    /// <summary>
    /// The candidate holding this PIN. Null when none does — and also when more than one does (PINs are unique per store,
    /// enforced by POS; an ambiguous match must never sign in the wrong person). Only active users with a PIN count.
    /// </summary>
    public async Task<int?> FindByPinAsync(Guid organizationId, IReadOnlyCollection<int> candidateUserIds, string pin, CancellationToken ct = default)
    {
        if (!IsValidPin(pin) || candidateUserIds.Count == 0) return null;
        var ids = candidateUserIds.Distinct().ToList();
        var users = await OrgUsers(organizationId)
            .Where(u => ids.Contains(u.UserID) && u.IsActive && u.PosPinHash != null)
            .ToListAsync(ct);

        var matches = users.Where(u => Matches(u, pin)).Select(u => u.UserID).Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    public async Task<IReadOnlyList<PosPinHash>> GetPinHashesAsync(Guid organizationId, IReadOnlyCollection<int> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0) return [];
        var ids = userIds.Distinct().ToList();
        return await OrgUsers(organizationId)
            .Where(u => ids.Contains(u.UserID) && u.IsActive && u.PosPinHash != null)
            .OrderBy(u => u.UserID)
            .Select(u => new PosPinHash(u.UserID, u.PosPinHash!))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<PosUserInfo>> GetUsersAsync(Guid organizationId, IReadOnlyCollection<int> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0) return [];
        var ids = userIds.Distinct().ToList();
        var users = await OrgUsers(organizationId).Where(u => ids.Contains(u.UserID)).OrderBy(u => u.UserID).ToListAsync(ct);
        return await ToInfoAsync(organizationId, users, ct);
    }

    public async Task<IReadOnlyList<PosUserInfo>> SearchUsersAsync(Guid organizationId, string? search, int limit, CancellationToken ct = default)
    {
        var query = OrgUsers(organizationId).Where(u => u.IsActive);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(u => u.FirstName.ToLower().Contains(s) || (u.LastName != null && u.LastName.ToLower().Contains(s))
                || u.Email.ToLower().Contains(s));
        }
        var users = await query
            .OrderBy(u => u.FirstName).ThenBy(u => u.LastName).ThenBy(u => u.UserID)
            .Take(Math.Clamp(limit, 1, MaxSearch))
            .ToListAsync(ct);
        return await ToInfoAsync(organizationId, users, ct);
    }

    /// <summary>
    /// The same access + refresh pair, and the same recorded session, as a password sign-in. Refuses a deleted or
    /// inactive user, an inactive organization and a platform super admin (a 4–6 digit PIN must not unlock platform
    /// administration). Does not clear the password lockout — the two are independent.
    /// </summary>
    public async Task<PosTokenPair> IssueTokensAsync(Guid organizationId, int userId, CancellationToken ct = default)
    {
        var user = await OrgUsers(organizationId).FirstOrDefaultAsync(u => u.UserID == userId, ct)
            ?? throw new UnauthorizedException("This user cannot sign in.");
        if (!user.IsActive)
            throw new UnauthorizedException("This user account is not active.");
        if (!await _orgStatus.IsOrganizationActiveAsync(organizationId))
            throw new UnauthorizedException("This organization has been deactivated.");
        if (await _superAdmins.IsSuperAdminAsync(user.UserID))
            throw new UnauthorizedException("Platform super admins cannot sign in with a PIN.");

        var roleName = await RoleNameAsync(user.RoleID, ct);
        var permissions = (await PermissionsByUserAsync(organizationId, [user], ct))[user.UserID];
        var accessToken = _tokens.GenerateAccessToken(user, roleName, permissions, isSuperAdmin: false);
        var (rawRefresh, refreshHash) = _tokens.GenerateRefreshToken();

        var now = DateTime.UtcNow;
        user.LastLoginAt = now;
        _db.UserSessions.Add(new UserSession
        {
            UserID = user.UserID,
            TokenHash = refreshHash,
            ExpiresAt = now.Add(AuthService.RefreshTokenTtl),
            CreatedAt = now,
            OrganizationId = user.OrganizationId
        });
        await _db.SaveChangesAsync(ct);

        return new PosTokenPair(accessToken, rawRefresh);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private bool Matches(UserAccount user, string pin)
    {
        if (string.IsNullOrEmpty(user.PosPinHash)) return false;
        try
        {
            var result = _hasher.VerifyHashedPassword(user, user.PosPinHash, pin);
            return result is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
        }
        catch (FormatException)
        {
            return false; // a corrupt stored hash is "no match", never a 500 at the till
        }
    }

    private async Task<string> RoleNameAsync(int roleId, CancellationToken ct) =>
        await _db.Roles.IgnoreQueryFilters().Where(r => r.RoleID == roleId).Select(r => r.Name).FirstOrDefaultAsync(ct) ?? string.Empty;

    private async Task<IReadOnlyList<PosUserInfo>> ToInfoAsync(Guid organizationId, List<UserAccount> users, CancellationToken ct)
    {
        if (users.Count == 0) return [];
        var roleIds = users.Select(u => u.RoleID).Distinct().ToList();
        var roleNames = await _db.Roles.IgnoreQueryFilters().Where(r => roleIds.Contains(r.RoleID))
            .ToDictionaryAsync(r => r.RoleID, r => r.Name, ct);
        var permissions = await PermissionsByUserAsync(organizationId, users, ct);

        return users.Select(u => new PosUserInfo(
            u.UserID,
            DisplayName(u),
            u.Email,
            roleNames.GetValueOrDefault(u.RoleID, string.Empty),
            u.IsActive,
            u.PosPinHash != null,
            permissions[u.UserID])).ToList();
    }

    internal static string DisplayName(UserAccount u) =>
        string.Join(' ', new[] { u.FirstName, u.LastName }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();

    /// <summary>Effective permission codes per user — <see cref="AuthRepository.ComputeAllowedPermissions"/>, as the token.</summary>
    private async Task<Dictionary<int, List<string>>> PermissionsByUserAsync(Guid organizationId, IReadOnlyCollection<UserAccount> users, CancellationToken ct)
    {
        var allPerms = await _db.Permissions.ToListAsync(ct);
        var roleIds = users.Select(u => u.RoleID).Distinct().ToList();
        var userIds = users.Select(u => u.UserID).ToList();
        var rolePerms = await _db.RolePermissions.IgnoreQueryFilters().Where(rp => roleIds.Contains(rp.RoleID)).ToListAsync(ct);
        var userPerms = await _db.UserPermissions.IgnoreQueryFilters()
            .Where(up => userIds.Contains(up.UserID) && up.OrganizationId == organizationId).ToListAsync(ct);

        return users.ToDictionary(
            u => u.UserID,
            u => AuthRepository.ComputeAllowedPermissions(
                allPerms,
                rolePerms.Where(rp => rp.RoleID == u.RoleID).ToList(),
                userPerms.Where(up => up.UserID == u.UserID).ToList()));
    }
}

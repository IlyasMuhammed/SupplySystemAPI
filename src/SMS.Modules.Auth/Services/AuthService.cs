using Hangfire;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using SMS.Modules.Auth.Domain;
using SMS.Modules.Auth.Models;
using SMS.Modules.Auth.Repositories;
using SMS.Shared.Common;
using SMS.Shared.Constants;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Auth.Services;

internal sealed class AuthService : IAuthService
{
    private const int LockoutThreshold = 5;
    private static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RefreshTokenTtl = TimeSpan.FromDays(7);

    private readonly IAuthRepository _repo;
    private readonly IEmailService _emailService;
    private readonly ITokenService _tokenService;
    private readonly IPasswordHasher<UserAccount> _hasher;
    private readonly AppSettings _settings;
    private readonly IOrganizationStatusService _orgStatusService;
    private readonly ISuperAdminService _superAdminService;
    private readonly PasswordResetThrottle _resetThrottle;

    /// <param name="resetThrottle">
    /// The app-wide reset-code throttle (a singleton — AddAuthModule registers it). Optional so a host or a
    /// test that builds AuthService by hand still gets a working throttle of its own rather than none.
    /// </param>
    public AuthService(
        IAuthRepository repo,
        IEmailService emailService,
        IOptions<AppSettings> settings,
        ITokenService tokenService,
        IPasswordHasher<UserAccount> hasher,
        IOrganizationStatusService orgStatusService,
        ISuperAdminService superAdminService,
        PasswordResetThrottle? resetThrottle = null)
    {
        _repo = repo;
        _emailService = emailService;
        _settings = settings.Value;
        _tokenService = tokenService;
        _hasher = hasher;
        _orgStatusService = orgStatusService;
        _superAdminService = superAdminService;
        _resetThrottle = resetThrottle ?? new PasswordResetThrottle();
    }

    // ── Login ─────────────────────────────────────────────────────────────────

    public async Task<LoginResponseModel> LoginAsync(LoginRequestModel dto)
    {
        var user = await _repo.FindUserForLoginAsync(dto.Email);
        if (user == null)
            throw new UnauthorizedException(StaticResponseMessage.invalidEmailOrPassword);

        if (user.LockedUntil.HasValue && user.LockedUntil.Value > DateTime.UtcNow)
            throw new AccountLockedException(user.LockedUntil.Value);

        var verification = _hasher.VerifyHashedPassword(user, user.Password, dto.Password);
        if (verification != PasswordVerificationResult.Success)
        {
            await HandleFailedAttemptAsync(user);
            throw new UnauthorizedException(StaticResponseMessage.invalidEmailOrPassword);
        }

        if (!user.IsActive)
            throw new UnauthorizedException(StaticResponseMessage.accountNotActivated);

        // Org deactivation (MT-002) blocks new logins immediately. Refresh is already covered
        // without a duplicate check here: deactivation bulk-deletes UserSessions, so
        // RefreshAsync's FindActiveSessionByHashAsync lookup fails on its own. An already-issued
        // short-lived access token remains valid until its own expiry — an accepted residual
        // window; closing it fully would need a token blacklist.
        if (!await _orgStatusService.IsOrganizationActiveAsync(user.OrganizationId))
            throw new UnauthorizedException("This organization has been deactivated.");

        // Successful login — reset lockout state and record last login
        user.FailedLoginAttempts = 0;
        user.LastFailedAt = null;
        user.LockedUntil = null;
        user.LastLoginAt = DateTime.UtcNow;
        await _repo.SaveAsync(user);

        var (roleName, permissions, accessToken, rawRefresh) = await BuildTokensAsync(user);

        await _repo.AddSessionAsync(new UserSession
        {
            UserID = user.UserID,
            TokenHash = TokenService.ComputeSha256(rawRefresh),
            ExpiresAt = DateTime.UtcNow.Add(RefreshTokenTtl),
            CreatedAt = DateTime.UtcNow
        });

        return new LoginResponseModel
        {
            AccessToken = accessToken,
            RefreshToken = rawRefresh,
            ExpiresIn = TokenService.AccessTokenSeconds,
            User = new UserWithTokenModel
            {
                UserId = user.UserID,
                FirstName = user.FirstName,
                LastName = user.LastName ?? string.Empty,
                Email = user.Email,
                PhoneNo = user.Phone,
                Address = user.Address,
                Role = new DropDownVM { ID = user.RoleID, Value = roleName },
                ProfilePictureUrl = user.ProfilePictureUrl
            }
        };
    }

    // ── Refresh ───────────────────────────────────────────────────────────────

    public async Task<RefreshResponseModel> RefreshAsync(string rawRefreshToken)
    {
        var hash = TokenService.ComputeSha256(rawRefreshToken);
        var session = await _repo.FindActiveSessionByHashAsync(hash);
        if (session == null)
            throw new UnauthorizedException(StaticResponseMessage.invalidOrExpiredRefreshToken);

        var user = await _repo.FindUserByIdAsync(session.UserID);
        if (user == null || !user.IsActive || user.IsDelete)
            throw new UnauthorizedException(StaticResponseMessage.invalidOrExpiredRefreshToken);

        // Rotate: revoke old session before issuing new one
        await _repo.RevokeSessionAsync(session.Id);

        var (_, _, accessToken, rawRefresh) = await BuildTokensAsync(user);

        await _repo.AddSessionAsync(new UserSession
        {
            UserID = user.UserID,
            TokenHash = TokenService.ComputeSha256(rawRefresh),
            ExpiresAt = DateTime.UtcNow.Add(RefreshTokenTtl),
            CreatedAt = DateTime.UtcNow
        });

        return new RefreshResponseModel
        {
            AccessToken = accessToken,
            RefreshToken = rawRefresh,
            // Was 900 while the token actually lived 3600 — a refreshed session told the client it
            // had a quarter of the life it really had.
            ExpiresIn = TokenService.AccessTokenSeconds
        };
    }

    // ── Logout ────────────────────────────────────────────────────────────────

    public async Task LogoutAsync(string rawRefreshToken)
    {
        var hash = TokenService.ComputeSha256(rawRefreshToken);
        var session = await _repo.FindActiveSessionByHashAsync(hash);
        if (session != null)
            await _repo.RevokeSessionAsync(session.Id);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string GenerateTemporaryPassword()
    {
        const string upper   = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        const string lower   = "abcdefghijklmnopqrstuvwxyz";
        const string digits  = "0123456789";
        const string special = "!@#$%^&*";
        const string all     = upper + lower + digits + special;

        var bytes = RandomNumberGenerator.GetBytes(12);
        var chars = new char[12];
        chars[0] = upper[bytes[0]   % upper.Length];
        chars[1] = lower[bytes[1]   % lower.Length];
        chars[2] = digits[bytes[2]  % digits.Length];
        chars[3] = special[bytes[3] % special.Length];
        for (var i = 4; i < 12; i++)
            chars[i] = all[bytes[i] % all.Length];

        return new string(chars.OrderBy(_ => RandomNumberGenerator.GetInt32(100)).ToArray());
    }

    private async Task HandleFailedAttemptAsync(UserAccount user)
    {
        var now = DateTime.UtcNow;
        if (!user.LastFailedAt.HasValue || now - user.LastFailedAt.Value > LockoutWindow)
        {
            user.FailedLoginAttempts = 1;
            user.LastFailedAt = now;
        }
        else
        {
            user.FailedLoginAttempts++;
        }

        if (user.FailedLoginAttempts >= LockoutThreshold)
        {
            user.LockedUntil = now.Add(LockoutDuration);
            user.FailedLoginAttempts = 0;
            user.LastFailedAt = null;
        }

        await _repo.SaveAsync(user);
    }

    private async Task<(string roleName, List<string> permissions, string accessToken, string rawRefresh)>
        BuildTokensAsync(UserAccount user)
    {
        var roleName = await _repo.GetRoleNameAsync(user.RoleID);
        var permissions = await _repo.GetAllowedPermissionsAsync(user.UserID);
        var isSuperAdmin = await _superAdminService.IsSuperAdminAsync(user.UserID);
        var accessToken = _tokenService.GenerateAccessToken(user, roleName, permissions, isSuperAdmin);
        var (rawRefresh, _) = _tokenService.GenerateRefreshToken();
        return (roleName, permissions, accessToken, rawRefresh);
    }

    // ── User management (admin) ───────────────────────────────────────────────

    public async Task<UserDetailModel> AdminCreateUserAsync(CreateUserRequest dto, AuthCaller caller)
    {
        await EnsureRoleAssignableAsync(dto.RoleID, caller);

        var createdByUserId = caller.UserId;
        var normalized = dto.Email.Trim().ToLowerInvariant();
        if (await _repo.EmailExistsAsync(normalized))
            throw new BadRequestException(StaticResponseMessage.accountAlreadyExsistWithThisEmail);

        var tempPassword = GenerateTemporaryPassword();
        var user = new UserAccount
        {
            FirstName  = dto.FirstName,
            LastName   = dto.LastName,
            Email      = normalized,
            Phone      = dto.Phone,
            Address    = dto.Address,
            Department = dto.Department,
            RoleID     = dto.RoleID,
            SupplierType = dto.SupplierType,
            IsActive   = true,
            IsDelete   = false,
            CreatedBy  = createdByUserId,
            CreatedDate = DateTime.UtcNow
        };
        user.Password = _hasher.HashPassword(user, tempPassword);

        var detail = await _repo.CreateUserAsync(user, dto.SupplierIds, createdByUserId);
        detail.TemporaryPassword = tempPassword;

        BackgroundJob.Enqueue(() =>
            _emailService.SendTemporaryPasswordEmail(dto.Email, dto.FirstName, tempPassword));

        return detail;
    }

    public Task<PaginatedResponse<UserListItemModel>> GetUsersAsync(UserListFilter filter) =>
        _repo.GetUsersFilteredAsync(filter);

    public async Task<UserDetailModel> GetUserDetailAsync(int userId)
    {
        var detail = await _repo.GetUserDetailAsync(userId);
        if (detail == null) throw new NotFoundException(StaticResponseMessage.accountNotFound);
        return detail;
    }

    public async Task PatchUserAsync(int userId, PatchUserRequest dto, AuthCaller caller)
    {
        var target = await RequireManageableUserAsync(userId, caller);

        // Your own name and department are yours to edit here; whether your account is active, and which
        // suppliers you can see, are not — that is how one would widen one's own access.
        if (IsSelf(target, caller) && (dto.IsActive is not null || dto.SupplierType is not null || dto.SupplierIds is not null))
            throw new ForbiddenException("You cannot change your own account status or supplier access. Ask another administrator.");

        await _repo.PatchUserAsync(userId, dto, caller.UserId);
    }

    public async Task AssignRoleAsync(int userId, int newRoleId, AuthCaller caller)
    {
        var target = await RequireManageableUserAsync(userId, caller);
        if (IsSelf(target, caller) && !caller.IsSuperAdmin)
            throw new ForbiddenException("You cannot change your own role. Ask another administrator.");
        await EnsureRoleAssignableAsync(newRoleId, caller);

        await _repo.AssignRoleAsync(userId, newRoleId);
        await _repo.RevokeAllUserSessionsAsync(userId);
    }

    public async Task AdminResetPasswordAsync(int userId, AuthCaller caller)
    {
        var user = await RequireManageableUserAsync(userId, caller);

        // The code goes to the account's own mailbox, so this cannot hand anyone else the account — but it
        // replaces the one a forgot-password request sent, so it gets a fresh set of attempts like any new code.
        var code = NewResetCode();
        _repo.SetPasswordResetCode(user.UserID, code, DateTime.UtcNow.Add(ResetCodeLifetime));
        _resetThrottle.Clear(user.Email);

        var email = user.Email;
        BackgroundJob.Enqueue(() => _emailService.SendPasswordResetEmail(email, code));
    }

    public async Task SoftDeleteUserAsync(int userId, AuthCaller caller)
    {
        var target = await RequireManageableUserAsync(userId, caller);
        // Super admins included: the last one deleting themselves leaves nobody to run the platform.
        if (IsSelf(target, caller))
            throw new ForbiddenException("You cannot delete your own account.");

        await _repo.SoftDeleteAsync(userId);
        await _repo.RevokeAllUserSessionsAsync(userId);
    }

    // ── Administration guards ─────────────────────────────────────────────────
    //
    // Every user and role write above and below goes through these. They do not lean on the tenant query
    // filter: a super admin bypasses it by design, and so do anonymous requests and background jobs with no
    // captured organization — "the row came back" is not "the caller may change it".

    /// <summary>
    /// The target user, if the caller may administer them at all: they exist, are not deleted, are in the
    /// caller's own organization (a super admin may cross organizations), and are not a platform super admin
    /// unless the caller is one too. Another organization's user is reported as not found, not forbidden, so
    /// the answer does not confirm the id exists.
    /// </summary>
    private async Task<UserAccount> RequireManageableUserAsync(int userId, AuthCaller caller)
    {
        var user = await _repo.FindUserByIdAsync(userId);
        if (user == null || user.IsDelete || (!caller.IsSuperAdmin && user.OrganizationId != caller.OrganizationId))
            throw new NotFoundException(StaticResponseMessage.accountNotFound);

        if (!caller.IsSuperAdmin && await _superAdminService.IsSuperAdminAsync(user.UserID))
            throw new ForbiddenException("This account belongs to a platform super admin; only a super admin can change it.");

        return user;
    }

    private static bool IsSelf(UserAccount target, AuthCaller caller) => target.UserID == caller.UserId;

    /// <summary>
    /// Whether the caller may give someone this role. It must be one their organization can see (global, or
    /// their own), and — unless they are a super admin — it must not carry SYSTEM_CONFIGURE or
    /// PLATFORM_SUPER_ADMIN: both reach every organization (shared reference data; api/system/organizations),
    /// and the global System Admin role carries both. Until 2026-10-02 neither was checked, so an Org Admin
    /// could give themselves System Admin, or another organization's custom role by id, and manage every tenant.
    /// <para>
    /// Deliberately NOT "only roles whose permissions you hold": an Org Admin must still be able to appoint
    /// their Supply Department Administrator, whose SALE_ORDER_CONFIG_WRITE they do not hold themselves.
    /// </para>
    /// </summary>
    private async Task EnsureRoleAssignableAsync(int roleId, AuthCaller caller)
    {
        var role = await _repo.FindVisibleRoleAsync(roleId);
        if (role == null || (!caller.IsSuperAdmin && !role.IsGlobal && role.OrganizationId != caller.OrganizationId))
            throw new BadRequestException("The selected role does not exist.");

        if (caller.IsSuperAdmin) return;

        var platformCodes = (await _repo.GetGrantedPermissionCodesForRoleAsync(roleId))
            .Intersect(AuthRepository.SuperAdminOnlyPermissionCodes)
            .ToList();
        if (platformCodes.Count > 0)
            throw new ForbiddenException(
                $"The role '{role.Name}' carries platform permissions ({string.Join(", ", platformCodes)}); only a super admin can assign it.");
    }

    /// <summary>
    /// The strict rule for the legacy api/auth endpoints: a non-super-admin may newly grant only permissions
    /// they hold themselves, and never a platform one. A permission already granted is left alone (re-saving
    /// a screen that shows it is not granting it); taking one away is always allowed.
    /// </summary>
    private static void EnsureCanGrant(IEnumerable<PermissionModel> changes, IReadOnlyDictionary<int, string> codes,
        IReadOnlyDictionary<int, bool> current, AuthCaller caller)
    {
        if (caller.IsSuperAdmin) return;

        foreach (var change in changes.Where(c => c.IsAllowed))
        {
            if (current.TryGetValue(change.PermissionID, out var allowed) && allowed) continue;

            var code = codes[change.PermissionID];
            if (AuthRepository.SuperAdminOnlyPermissionCodes.Contains(code))
                throw new ForbiddenException($"Only a super admin can grant {code}.");
            if (!caller.Holds(code))
                throw new ForbiddenException($"You cannot grant {code}: you do not hold it yourself.");
        }
    }

    // ── Org admin invite acceptance (MT-002) ──────────────────────────────────

    public async Task AcceptInviteAsync(string token, string newPassword)
    {
        var user = await _repo.FindUserByInviteTokenAsync(token)
            ?? throw new BadRequestException("This invite link is invalid.");

        if (user.InviteTokenExpiresAt is null || user.InviteTokenExpiresAt < DateTime.UtcNow)
            throw new BadRequestException("This invite link has expired.");

        user.Password             = _hasher.HashPassword(user, newPassword);
        user.IsActive             = true;
        user.InviteToken          = null;
        user.InviteTokenExpiresAt = null;
        user.UpdateDate           = DateTime.UtcNow;
        await _repo.SaveAsync(user);
    }

    // ── Current user profile ──────────────────────────────────────────────────

    public async Task<CurrentUserModel> GetCurrentUserAsync(int userId)
    {
        var user = await _repo.FindUserByIdAsync(userId);
        if (user == null || user.IsDelete)
            throw new NotFoundException("User not found");

        var roleName = await _repo.GetRoleNameAsync(user.RoleID);
        var permissions = await _repo.GetAllowedPermissionsAsync(userId);

        return new CurrentUserModel
        {
            UserId = user.UserID,
            Email = user.Email,
            FirstName = user.FirstName,
            LastName = user.LastName ?? string.Empty,
            Phone = user.Phone,
            Address = user.Address,
            Role = new DropDownVM { ID = user.RoleID, Value = roleName },
            Permissions = permissions,
            ProfilePictureUrl = user.ProfilePictureUrl
        };
    }

    public async Task UpdateProfilePictureUrlAsync(int userId, string? pictureUrl) =>
        await _repo.UpdateProfilePictureAsync(userId, pictureUrl);

    // ── Existing registration / password flows ────────────────────────────────

    public UserAccountModel? FindByEmail(string email)
    {
        var account = _repo.FindByEmail(email);
        if (account == null) return null;
        return new UserAccountModel { UserID = account.UserID, Email = account.Email, FirstName = account.FirstName };
    }

    public int CreateUserAccount(UserAccountModel model)
    {
        model.Token = Guid.NewGuid().ToString();
        var resp = _repo.CreateUserAccount(model);
        if (resp == 1)
            BackgroundJob.Enqueue(() => _emailService.SendActivationEmail(model.Email, model.FirstName, model.Token!));
        return resp;
    }

    public int ActivationTokenValidation(string token) => _repo.ActivationTokenValidation(token);

    // ── Forgot password (anonymous) ───────────────────────────────────────────
    //
    // See PasswordResetThrottle for the limits and why. Both methods answer the same way whether or not the
    // address has an account; AuthController turns that into one uniform response.

    private static readonly TimeSpan ResetCodeLifetime = TimeSpan.FromMinutes(60);

    /// <summary>Six digits from the OS's cryptographic generator — <c>new Random()</c> is predictable.</summary>
    private static string NewResetCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    /// <returns>1 when a code was sent; 0 when nothing was (no such active account, or this address has had its
    /// codes for the hour). The caller must not tell the two apart.</returns>
    public int SendPasswordResetToken(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return 0;

        var account = _repo.FindByEmail(email);
        if (account == null) return 0;
        if (!_resetThrottle.TryIssueCode(account.Email)) return 0;

        var code = NewResetCode();
        _repo.SetPasswordResetCode(account.UserID, code, DateTime.UtcNow.Add(ResetCodeLifetime));

        var to = account.Email;
        BackgroundJob.Enqueue(() => _emailService.SendPasswordResetEmail(to, code));
        return 1;
    }

    /// <returns>
    /// 1 reset; -1 the right code, but expired; -2 this code has had its <see cref="PasswordResetThrottle.MaxAttemptsPerCode"/>
    /// guesses and is spent (a new one must be requested); 0 anything else — wrong code, or no such account,
    /// deliberately indistinguishable.
    /// </returns>
    public int SetPasswordVerification(ForgotPasswordModel dto)
    {
        var email = dto.Email ?? string.Empty;

        // Reserved before the comparison, so parallel guesses share the five rather than getting five each.
        var attempt = _resetThrottle.RegisterAttempt(email);
        var account = string.IsNullOrWhiteSpace(email) ? null : _repo.FindByEmail(email);

        if (attempt > PasswordResetThrottle.MaxAttemptsPerCode)
        {
            if (account != null) _repo.ClearPasswordResetCode(account.UserID);
            return -2;
        }

        if (account == null || !CodesMatch(account.PasswordResetToken, dto.verificationCode))
        {
            if (attempt < PasswordResetThrottle.MaxAttemptsPerCode) return 0;

            // The last allowed guess was wrong: spend the code where it outlives this process — in the database.
            if (account != null) _repo.ClearPasswordResetCode(account.UserID);
            return -2;
        }

        if (account.PasswordResetTokenTime < DateTime.UtcNow) return -1;

        _repo.CompletePasswordReset(account.UserID, dto.Password);
        _resetThrottle.Clear(email);
        return 1;
    }

    /// <summary>Constant-time, so how long a wrong guess takes says nothing about how close it was.</summary>
    private static bool CodesMatch(string? stored, string? given)
    {
        if (string.IsNullOrEmpty(stored) || string.IsNullOrEmpty(given)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(stored), Encoding.UTF8.GetBytes(given.Trim()));
    }

    // dto.UserID is the signed-in user's own id: AuthController sets it from the token, never the body.
    public int UpdatePersonalInformation(UpdatePersonalInfoModel dto) => _repo.UpdatePersonalInformation(dto);

    public int UpdatePasswordInformation(UpdatePasswordModel dto) => _repo.UpdatePasswordInformation(dto);

    // ── Legacy api/auth user & role administration ────────────────────────────

    public Task<PaginatedResponse<UserAccountModel>> GetAllUsersAsync(int page, int pageSize, AuthCaller caller)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        return Task.FromResult(_repo.GetAllUsers(page, pageSize, caller.IsSuperAdmin ? null : caller.OrganizationId));
    }

    public async Task DeactivateUserAsync(int userId, AuthCaller caller)
    {
        var target = await RequireManageableUserAsync(userId, caller);
        if (IsSelf(target, caller))
            throw new ForbiddenException("You cannot deactivate your own account.");

        _repo.InactiveUser(userId);
    }

    public async Task<List<PermissionModel>> GetPermissionsByRoleAsync(int roleId, AuthCaller caller)
    {
        await RequireVisibleRoleAsync(roleId, caller);
        return _repo.GetPermissionsByRole(roleId);
    }

    public async Task SaveRolePermissionsAsync(int roleId, List<PermissionModel> permissions, AuthCaller caller)
    {
        var role = await RequireVisibleRoleAsync(roleId, caller);

        // The global catalog is shared by every organization: changing it is the platform's call alone. The
        // PLATFORM_SUPER_ADMIN permission the endpoint requires is not enough — the global System Admin role
        // carries that code too — so this is the is_super_admin claim (SuperAdminUsers).
        if (role.IsGlobal && !caller.IsSuperAdmin)
            throw new ForbiddenException("This role is shared across every organization and can only be changed by a Super Admin.");

        var codes = await _repo.GetPermissionCodesAsync();
        var known = permissions.Where(p => codes.ContainsKey(p.PermissionID)).ToList();
        EnsureCanGrant(known, codes, await _repo.GetRolePermissionStatesAsync(roleId), caller);

        _repo.SaveRolePermissions(roleId, known);
    }

    public async Task<List<PermissionModel>> GetUserPermissionsAsync(int userId, AuthCaller caller)
    {
        var user = await _repo.FindUserByIdAsync(userId);
        if (user == null || user.IsDelete || (!caller.IsSuperAdmin && user.OrganizationId != caller.OrganizationId))
            throw new NotFoundException(StaticResponseMessage.accountNotFound);

        return _repo.GetUserPermissions(userId);
    }

    public async Task SaveUserPermissionsAsync(int userId, List<PermissionModel> permissions, AuthCaller caller)
    {
        var target = await RequireManageableUserAsync(userId, caller);
        if (IsSelf(target, caller) && !caller.IsSuperAdmin)
            throw new ForbiddenException("You cannot change your own permissions. Ask another administrator.");

        var codes = await _repo.GetPermissionCodesAsync();
        var known = permissions.Where(p => codes.ContainsKey(p.PermissionID)).ToList();
        EnsureCanGrant(known, codes, await _repo.GetUserPermissionOverridesAsync(userId), caller);

        _repo.SaveUserPermissions(userId, target.OrganizationId, known);
    }

    /// <summary>A role the caller's organization can see — global or its own; any role for a super admin.</summary>
    private async Task<Role> RequireVisibleRoleAsync(int roleId, AuthCaller caller)
    {
        var role = await _repo.FindVisibleRoleAsync(roleId);
        if (role == null || (!caller.IsSuperAdmin && !role.IsGlobal && role.OrganizationId != caller.OrganizationId))
            throw new NotFoundException($"Role {roleId} not found.");
        return role;
    }

    // ── Role CRUD ─────────────────────────────────────────────────────────────

    public Task<List<RoleListItemModel>> GetRolesAsync() => _repo.GetRolesAsync();

    public async Task<RoleDetailModel> GetRoleDetailAsync(int roleId)
    {
        var detail = await _repo.GetRoleDetailAsync(roleId);
        if (detail == null) throw new NotFoundException($"Role {roleId} not found.");
        return detail;
    }

    public async Task<RoleListItemModel> CreateRoleAsync(CreateRoleRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new BadRequestException("Role name is required.");
        if (string.IsNullOrWhiteSpace(req.RoleCode))
            throw new BadRequestException("Role code is required.");

        var code = req.RoleCode.Trim().ToUpperInvariant();
        if (await _repo.CodeExistsAsync(code))
            throw new BadRequestException($"A role with code '{code}' already exists.");
        if (await _repo.NameExistsAsync(req.Name))
            throw new BadRequestException("A role with this name already exists.");

        req.RoleCode = code;
        return await _repo.CreateRoleAsync(req);
    }

    public async Task<bool> UpdateRoleAsync(int roleId, UpdateRoleRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new BadRequestException("Role name is required.");
        if (await _repo.NameExistsAsync(req.Name, roleId))
            throw new BadRequestException("A role with this name already exists.");
        return await _repo.UpdateRoleAsync(roleId, req);
    }

    public async Task<bool> ReplaceRolePermissionsAsync(int roleId, ReplacePermissionsRequest req) =>
        await _repo.ReplaceRolePermissionsAsync(roleId, req.AllowedPermissionIds);

    public Task<int> GetActiveUserCountForRoleAsync(int roleId) => _repo.GetActiveUserCountAsync(roleId);

    public Task<bool> DeactivateRoleAsync(int roleId) => _repo.DeactivateRoleAsync(roleId);

    public Task<List<RoleUserModel>> GetRoleUsersAsync(int roleId) => _repo.GetRoleUsersAsync(roleId);
}

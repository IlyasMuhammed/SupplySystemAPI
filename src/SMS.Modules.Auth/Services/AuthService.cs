using Hangfire;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
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

    public AuthService(
        IAuthRepository repo,
        IEmailService emailService,
        IOptions<AppSettings> settings,
        ITokenService tokenService,
        IPasswordHasher<UserAccount> hasher,
        IOrganizationStatusService orgStatusService,
        ISuperAdminService superAdminService)
    {
        _repo = repo;
        _emailService = emailService;
        _settings = settings.Value;
        _tokenService = tokenService;
        _hasher = hasher;
        _orgStatusService = orgStatusService;
        _superAdminService = superAdminService;
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

    public async Task<UserDetailModel> AdminCreateUserAsync(CreateUserRequest dto, int createdByUserId)
    {
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

    public async Task PatchUserAsync(int userId, PatchUserRequest dto, int patchedBy)
    {
        var existing = await _repo.GetUserDetailAsync(userId);
        if (existing == null) throw new NotFoundException(StaticResponseMessage.accountNotFound);
        await _repo.PatchUserAsync(userId, dto, patchedBy);
    }

    public async Task AssignRoleAsync(int userId, int newRoleId)
    {
        var existing = await _repo.GetUserDetailAsync(userId);
        if (existing == null) throw new NotFoundException(StaticResponseMessage.accountNotFound);

        await _repo.AssignRoleAsync(userId, newRoleId);
        await _repo.RevokeAllUserSessionsAsync(userId);
    }

    public async Task AdminResetPasswordAsync(int userId)
    {
        var user = await _repo.FindUserByIdAsync(userId);
        if (user == null || user.IsDelete) throw new NotFoundException(StaticResponseMessage.accountNotFound);

        var code = new Random().Next(100000, 999999).ToString();
        user.PasswordResetToken     = code;
        user.PasswordResetTokenTime = DateTime.UtcNow.AddMinutes(60);
        await _repo.SaveAsync(user);

        BackgroundJob.Enqueue(() => _emailService.SendPasswordResetEmail(user.Email, code));
    }

    public async Task SoftDeleteUserAsync(int userId)
    {
        var existing = await _repo.GetUserDetailAsync(userId);
        if (existing == null) throw new NotFoundException(StaticResponseMessage.accountNotFound);

        await _repo.SoftDeleteAsync(userId);
        await _repo.RevokeAllUserSessionsAsync(userId);
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

    public int SendPasswordResetToken(string email)
    {
        var account = _repo.FindByEmail(email);
        if (account == null) return 0;

        var code = new Random().Next(100000, 999999).ToString();
        _repo.UpdateUserAccount(new UserAccount
        {
            UserID = account.UserID,
            PasswordResetToken = code,
            PasswordResetTokenTime = DateTime.UtcNow.AddMinutes(60)
        });

        BackgroundJob.Enqueue(() => _emailService.SendPasswordResetEmail(email, code));
        return 1;
    }

    public int SetPasswordVerification(ForgotPasswordModel dto) => _repo.SetPasswordVerification(dto);

    public int UpdatePersonalInformation(UpdatePersonalInfoModel dto) => _repo.UpdatePersonalInformation(dto);

    public int UpdatePasswordInformation(UpdatePasswordModel dto) => _repo.UpdatePasswordInformation(dto);

    public PaginatedResponse<UserAccountModel> GetAllUsers(int page, int pageSize) => _repo.GetAllUsers(page, pageSize);

    public int InactiveUser(int userId) => _repo.InactiveUser(userId);

    public List<PermissionModel> GetPermissionsByRole(int roleId) => _repo.GetPermissionsByRole(roleId);

    public int SaveRolePermissions(int roleId, List<PermissionModel> permissions) => _repo.SaveRolePermissions(roleId, permissions);

    public List<PermissionModel> GetUserPermissions(int userId) => _repo.GetUserPermissions(userId);

    public int SaveUserPermissions(int userId, List<PermissionModel> permissions) => _repo.SaveUserPermissions(userId, permissions);

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

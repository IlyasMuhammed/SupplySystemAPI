using SMS.Modules.Auth.Models;
using SMS.Shared.Common;
using SMS.Shared.Pagination;

namespace SMS.Modules.Auth.Services;

public interface IAuthService
{
    // ── Token-based login / refresh / logout ──────────────────────────────────
    Task<LoginResponseModel> LoginAsync(LoginRequestModel dto);
    Task<RefreshResponseModel> RefreshAsync(string rawRefreshToken);
    Task LogoutAsync(string rawRefreshToken);
    Task<CurrentUserModel> GetCurrentUserAsync(int userId);

    // ── Org admin invite acceptance (MT-002) ──────────────────────────────────
    Task AcceptInviteAsync(string token, string newPassword);

    // ── User management (admin) ───────────────────────────────────────────────
    // Every write here takes the caller (see AuthCaller): who is asking decides which roles they may
    // hand out, whether the target is theirs to change, and whether it is themselves or a super admin.
    Task<UserDetailModel> AdminCreateUserAsync(CreateUserRequest dto, AuthCaller caller);
    Task<PaginatedResponse<UserListItemModel>> GetUsersAsync(UserListFilter filter);
    Task<UserDetailModel> GetUserDetailAsync(int userId);
    Task PatchUserAsync(int userId, PatchUserRequest dto, AuthCaller caller);
    Task AssignRoleAsync(int userId, int newRoleId, AuthCaller caller);
    Task AdminResetPasswordAsync(int userId, AuthCaller caller);
    Task SoftDeleteUserAsync(int userId, AuthCaller caller);

    // ── Registration / activation / password flows ────────────────────────────
    UserAccountModel? FindByEmail(string email);
    int CreateUserAccount(UserAccountModel model);
    int ActivationTokenValidation(string token);
    int SendPasswordResetToken(string email);
    int SetPasswordVerification(ForgotPasswordModel dto);
    int UpdatePersonalInformation(UpdatePersonalInfoModel dto);
    int UpdatePasswordInformation(UpdatePasswordModel dto);
    Task UpdateProfilePictureUrlAsync(int userId, string? pictureUrl);

    // ── Legacy api/auth user & role administration ────────────────────────────
    // Duplicates of api/users and api/roles, which are what the frontend calls. Kept for API clients,
    // and held to the stricter rule: nobody but a super admin may grant a permission they do not hold.
    Task<PaginatedResponse<UserAccountModel>> GetAllUsersAsync(int page, int pageSize, AuthCaller caller);
    Task DeactivateUserAsync(int userId, AuthCaller caller);
    Task<List<PermissionModel>> GetPermissionsByRoleAsync(int roleId, AuthCaller caller);
    Task SaveRolePermissionsAsync(int roleId, List<PermissionModel> permissions, AuthCaller caller);
    Task<List<PermissionModel>> GetUserPermissionsAsync(int userId, AuthCaller caller);
    Task SaveUserPermissionsAsync(int userId, List<PermissionModel> permissions, AuthCaller caller);

    // ── Role CRUD ─────────────────────────────────────────────────────────────
    Task<List<RoleListItemModel>> GetRolesAsync();
    Task<RoleDetailModel> GetRoleDetailAsync(int roleId);
    Task<RoleListItemModel> CreateRoleAsync(CreateRoleRequest req);
    Task<bool> UpdateRoleAsync(int roleId, UpdateRoleRequest req);
    Task<bool> ReplaceRolePermissionsAsync(int roleId, ReplacePermissionsRequest req);
    Task<int> GetActiveUserCountForRoleAsync(int roleId);
    Task<bool> DeactivateRoleAsync(int roleId);
    Task<List<RoleUserModel>> GetRoleUsersAsync(int roleId);
}

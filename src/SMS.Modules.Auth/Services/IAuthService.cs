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
    Task<UserDetailModel> AdminCreateUserAsync(CreateUserRequest dto, int createdByUserId);
    Task<PaginatedResponse<UserListItemModel>> GetUsersAsync(UserListFilter filter);
    Task<UserDetailModel> GetUserDetailAsync(int userId);
    Task PatchUserAsync(int userId, PatchUserRequest dto, int patchedBy);
    Task AssignRoleAsync(int userId, int newRoleId);
    Task AdminResetPasswordAsync(int userId);
    Task SoftDeleteUserAsync(int userId);

    // ── Registration / activation / password flows ────────────────────────────
    UserAccountModel? FindByEmail(string email);
    int CreateUserAccount(UserAccountModel model);
    int ActivationTokenValidation(string token);
    int SendPasswordResetToken(string email);
    int SetPasswordVerification(ForgotPasswordModel dto);
    int UpdatePersonalInformation(UpdatePersonalInfoModel dto);
    int UpdatePasswordInformation(UpdatePasswordModel dto);
    Task UpdateProfilePictureUrlAsync(int userId, string? pictureUrl);
    PaginatedResponse<UserAccountModel> GetAllUsers(int page, int pageSize);
    int InactiveUser(int userId);
    List<PermissionModel> GetPermissionsByRole(int roleId);
    int SaveRolePermissions(int roleId, List<PermissionModel> permissions);
    List<PermissionModel> GetUserPermissions(int userId);
    int SaveUserPermissions(int userId, List<PermissionModel> permissions);

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

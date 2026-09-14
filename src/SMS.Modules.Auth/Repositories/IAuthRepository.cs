using SMS.Modules.Auth.Domain;
using SMS.Modules.Auth.Models;
using SMS.Shared.Common;
using SMS.Shared.Pagination;

namespace SMS.Modules.Auth.Repositories;

internal interface IAuthRepository
{
    // ── Existing synchronous methods ──────────────────────────────────────────
    UserAccount? AuthenticateUserAccount(LoginVM model);
    UserAccount? FindByEmail(string email);
    int ActivationTokenValidation(string token);
    int SetPasswordVerification(ForgotPasswordModel dto);
    int CreateUserAccount(UserAccountModel model);
    int UpdateUserAccount(UserAccount userAccount);
    int UpdatePersonalInformation(UpdatePersonalInfoModel dto);
    int UpdatePasswordInformation(UpdatePasswordModel dto);
    PaginatedResponse<UserAccountModel> GetAllUsers(int page, int pageSize);
    int InactiveUser(int userId);
    List<PermissionModel> GetPermissionsByRole(int roleId);
    int SaveRolePermissions(int roleId, List<PermissionModel> permissions);
    int SaveUserPermissions(int userId, List<PermissionModel> permissions);
    List<PermissionModel> GetUserPermissions(int userId);

    // ── Async methods for login / token / session flows ───────────────────────
    Task<UserAccount?> FindUserForLoginAsync(string email);
    Task<UserAccount?> FindUserByIdAsync(int userId);
    Task<UserAccount?> FindUserByInviteTokenAsync(string token);
    Task SaveAsync(UserAccount user);
    Task<string> GetRoleNameAsync(int roleId);
    Task<List<string>> GetAllowedPermissionsAsync(int userId);
    Task<UserSession?> FindActiveSessionByHashAsync(string tokenHash);
    Task AddSessionAsync(UserSession session);
    Task RevokeSessionAsync(Guid sessionId);
    Task<int> DeleteExpiredSessionsAsync();

    // ── User management ───────────────────────────────────────────────────────
    Task UpdateProfilePictureAsync(int userId, string? pictureUrl);
    Task<bool> EmailExistsAsync(string email);
    Task<UserDetailModel> CreateUserAsync(UserAccount user, List<Guid>? supplierIds, int createdBy);
    Task<PaginatedResponse<UserListItemModel>> GetUsersFilteredAsync(UserListFilter filter);
    Task<UserDetailModel?> GetUserDetailAsync(int userId);
    Task PatchUserAsync(int userId, PatchUserRequest dto, int patchedBy);
    Task AssignRoleAsync(int userId, int newRoleId);
    Task RevokeAllUserSessionsAsync(int userId);
    Task SoftDeleteAsync(int userId);

    // ── User↔Supplier access mapping (REQ-2.x) ───────────────────────────────────
    Task<List<Guid>> GetUserSupplierIdsAsync(int userId);
    Task SaveUserSupplierAccessAsync(int userId, List<Guid> supplierIds, int assignedBy);

    // ── Role CRUD ─────────────────────────────────────────────────────────────
    Task<List<RoleListItemModel>> GetRolesAsync();
    Task<RoleDetailModel?> GetRoleDetailAsync(int roleId);
    Task<RoleListItemModel> CreateRoleAsync(CreateRoleRequest req);
    Task<bool> CodeExistsAsync(string code, int? excludeId = null);
    Task<bool> NameExistsAsync(string name, int? excludeId = null);
    Task<bool> UpdateRoleAsync(int roleId, UpdateRoleRequest req);
    Task<bool> ReplaceRolePermissionsAsync(int roleId, List<int> allowedPermissionIds);
    Task<int> GetActiveUserCountAsync(int roleId);
    Task<bool> DeactivateRoleAsync(int roleId);
    Task<List<RoleUserModel>> GetRoleUsersAsync(int roleId);
}

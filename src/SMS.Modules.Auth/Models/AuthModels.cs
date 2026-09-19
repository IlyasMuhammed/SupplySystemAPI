using Microsoft.AspNetCore.Http;
using SMS.Shared.Common;

namespace SMS.Modules.Auth.Models;

public class ProfilePictureUploadRequest
{
    public IFormFile? File { get; set; }
}

public class LoginRequestModel
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class LoginResponseModel
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>
    /// Seconds until the access token expires. No default: it is always set from the token's real
    /// lifetime, and a default of 900 was a number that had stopped being true — a wrong value
    /// here reads as authoritative, whereas a zero is obviously unset.
    /// </summary>
    public int ExpiresIn { get; set; }

    public UserWithTokenModel User { get; set; } = null!;
}

public class RefreshRequestModel
{
    public string RefreshToken { get; set; } = string.Empty;
}

public class AcceptInviteRequest
{
    public string Token { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}

public class RefreshResponseModel
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>Seconds until the access token expires. See <see cref="LoginResponseModel.ExpiresIn"/>.</summary>
    public int ExpiresIn { get; set; }
}

public class LogoutRequestModel
{
    public string RefreshToken { get; set; } = string.Empty;
}

public class UserAccountModel
{
    public int? UserID { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string? MiddleName { get; set; }
    public string? LastName { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? Token { get; set; }
    public bool? IsActive { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? ZipCode { get; set; }
}

public class UserWithTokenModel
{
    public int UserId { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? PhoneNo { get; set; }
    public string? Address { get; set; }
    public DropDownVM? Role { get; set; }
    public string? Token { get; set; }
    public string? ProfilePictureUrl { get; set; }
}

public class ForgotPasswordModel
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? verificationCode { get; set; }
}

public class UpdatePersonalInfoModel
{
    public int UserID { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string? LastName { get; set; }
    public string Email { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public int PaymentMethodId { get; set; }
}

public class UpdatePasswordModel
{
    public int UserID { get; set; }
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
    public string ConfirmPassword { get; set; } = string.Empty;
}

public class PermissionModel
{
    public int PermissionID { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsAllowed { get; set; }
}

// ── User management (POST /api/users, GET /api/users, etc.) ──────────────────

public class CreateUserRequest
{
    public string FirstName { get; set; } = string.Empty;
    public string? LastName { get; set; }
    public string Email { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? Department { get; set; }
    public int RoleID { get; set; }
    // REQ-1.x — "INTERNAL" | "EXTERNAL", required.
    public string SupplierType { get; set; } = string.Empty;
    // REQ-2.x — suppliers this user may see, meaningful only when SupplierType="EXTERNAL".
    public List<Guid>? SupplierIds { get; set; }
}

public class UserListFilter
{
    public int? RoleId { get; set; }
    public string? Status { get; set; }   // "active" | "inactive"
    public string? Department { get; set; }
    public string? Search { get; set; }   // partial match on name / email
    public string? SupplierType { get; set; }   // "INTERNAL" | "EXTERNAL"
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

public class UserListItemModel
{
    public int UserID { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string? LastName { get; set; }
    public string Email { get; set; } = string.Empty;
    public string? Department { get; set; }
    public bool IsActive { get; set; }
    public DropDownVM? Role { get; set; }
    public DateTime CreatedDate { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public string SupplierType { get; set; } = string.Empty;
    public List<Guid> SupplierIds { get; set; } = [];
}

public class UserDetailModel
{
    public int UserID { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string? MiddleName { get; set; }
    public string? LastName { get; set; }
    public string Email { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? Department { get; set; }
    public bool IsActive { get; set; }
    public DropDownVM? Role { get; set; }
    public DateTime CreatedDate { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public string? TemporaryPassword { get; set; }  // only populated on create
    public string SupplierType { get; set; } = string.Empty;
    public List<Guid> SupplierIds { get; set; } = [];
}

public class PatchUserRequest
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Department { get; set; }
    public bool? IsActive { get; set; }
    // REQ-1.x — "INTERNAL" | "EXTERNAL". Nullable per this DTO's existing partial-patch
    // contract (null = don't touch); validated against the 2 allowed values when supplied.
    public string? SupplierType { get; set; }
    // REQ-2.x — null = don't touch (partial-patch contract); a non-null list (including empty)
    // fully replaces the user's mapped suppliers.
    public List<Guid>? SupplierIds { get; set; }
}

public class AssignRoleRequest
{
    public int RoleID { get; set; }
}

// ── Role CRUD (ROLE-001) ──────────────────────────────────────────────────────

public class RoleListItemModel
{
    public int RoleId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string RoleCode { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; }
    // Shared catalog role (usable by every org, editable only by Super Admin) vs. an org-owned
    // custom role (private to and editable only by the org that created it).
    public bool IsGlobal { get; set; }
    public int ActiveUserCount { get; set; }
    public int PermissionCount { get; set; }
}

public class RoleDetailModel
{
    public int RoleId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string RoleCode { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; }
    public bool IsGlobal { get; set; }
    public int ActiveUserCount { get; set; }
    public List<PermissionGroupModel> PermissionGroups { get; set; } = [];
}

public class PermissionGroupModel
{
    public string Module { get; set; } = string.Empty;
    public List<PermissionItemModel> Permissions { get; set; } = [];
}

public class PermissionItemModel
{
    public int PermissionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public bool IsAllowed { get; set; }
}

public class CreateRoleRequest
{
    public string Name { get; set; } = string.Empty;
    public string RoleCode { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public class UpdateRoleRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
}

public class ReplacePermissionsRequest
{
    public List<int> AllowedPermissionIds { get; set; } = [];
}

public class RoleDeactivateConflictResult
{
    public int ActiveUserCount { get; set; }
}

public class RoleUserModel
{
    public int UserId { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string? LastName { get; set; }
    public string Email { get; set; } = string.Empty;
    public string? Department { get; set; }
    public bool IsActive { get; set; }
}

public class CurrentUserModel
{
    public int UserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public DropDownVM Role { get; set; } = null!;
    public List<string> Permissions { get; set; } = [];
    public string? ProfilePictureUrl { get; set; }
}

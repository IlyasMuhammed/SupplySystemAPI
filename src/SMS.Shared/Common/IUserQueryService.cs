namespace SMS.Shared.Common;

/// <summary>
/// Cross-module interface for querying user identities.
/// Defined in SMS.Shared; implemented by SMS.Modules.Auth.
/// </summary>
public interface IUserQueryService
{
    Task<UserIdentity?> GetUserAsync(int userId);
    Task<IReadOnlyList<UserIdentity>> GetUsersAsync(IReadOnlyList<int> userIds);
    Task<IReadOnlyList<UserIdentity>> GetActiveUsersByRoleAsync(int roleId);
    Task<bool> IsSystemAdminAsync(int userId);
    // Used by WorkflowActionService as an approve/reject override, mirroring the existing System
    // Admin override — safe because DocumentApproval/DocumentApprovalStep are tenant-scoped, so an
    // Org Admin's queries already only ever see their own org's pending approvals; this just lets
    // them act on a step they weren't specifically resolved as the assignee for.
    Task<bool> IsOrgAdminAsync(int userId);
    Task<string?> GetUserEmailAsync(int userId);

    // MT-007 — used once by TenancyDataSeeder's migration step to designate the first existing
    // System Admin as Super Admin. "First" = earliest CreatedDate among active System Admin users.
    Task<int?> GetFirstSystemAdminUserIdAsync();
}

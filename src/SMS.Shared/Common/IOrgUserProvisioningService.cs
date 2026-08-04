using System.Data.Common;

namespace SMS.Shared.Common;

// Shared interface so SMS.Modules.Tenancy can create an organization's initial Admin user (and
// later invalidate that org's sessions) without referencing SMS.Modules.Auth directly — AuthDbContext
// and AuthRepository are internal to Auth, so a project reference wouldn't even expose what's
// needed. Same reasoning/pattern as ISupplierContactLookupService and IPoDocumentTemplateService.
// The implementation lives in SMS.Modules.Auth and is resolved via the DI container.
public interface IOrgUserProvisioningService
{
    // Persists the new user against Auth's own DbContext, joined to the caller's already-open
    // transaction (see SMS.Modules.Material/Services/MivService.cs for the established
    // cross-DbContext shared-transaction pattern this mirrors) so the org row and the admin user
    // row commit — or roll back — atomically. Throws SMS.Shared.Exceptions.BadRequestException on
    // duplicate email, exactly like the existing admin-create-user flow.
    Task<int> CreateOrgAdminUserAsync(CreateOrgAdminUserRequest req, DbTransaction sharedTransaction);

    // Physically deletes (not soft-revokes) every active session belonging to users of the given
    // organization — mirrors AuthRepository's existing DeleteExpiredSessionsAsync bulk-delete
    // precedent, deliberately not the soft-revoke-per-user pattern used for single-user role changes.
    Task<int> DeleteActiveSessionsForOrganizationAsync(Guid organizationId);

    // Backs the Super Admin's "view/change organization admin" screen — there's no AdminUserId
    // column on Organization, "the org admin" is purely implicit (UserAccount.OrganizationId +
    // RoleID == OrgAdmin), so the caller needs the org's whole active user list to both show who
    // currently holds that role and offer a pick-list of who else could.
    Task<List<OrgUserSummary>> GetOrgUsersAsync(Guid organizationId);

    // Demotes whichever user(s) currently hold OrgAdmin in this org down to Requester, then
    // promotes newAdminUserId to OrgAdmin — one SaveChangesAsync, atomic within Auth's own
    // DbContext (unlike CreateOrgAdminUserAsync, nothing here touches Tenancy's Organizations
    // table, so no shared cross-context transaction is needed). Throws BadRequestException if
    // newAdminUserId doesn't belong to (or isn't active in) this organization.
    Task ReassignOrgAdminAsync(Guid organizationId, int newAdminUserId);
}

public sealed record CreateOrgAdminUserRequest(
    string FirstName, string LastName, string Email, Guid OrganizationId, string OrganizationName, int RoleId);

public sealed record OrgUserSummary(
    int UserId, string FirstName, string? LastName, string Email, int RoleId, string RoleName, bool IsActive);

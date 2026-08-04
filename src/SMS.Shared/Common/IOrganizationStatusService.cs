namespace SMS.Shared.Common;

// Shared interface so SMS.Modules.Auth can check whether a user's organization is active at
// login time, without referencing SMS.Modules.Tenancy directly. The implementation lives in
// SMS.Modules.Tenancy and is resolved via the DI container — same pattern as
// IOrgUserProvisioningService, just the reverse direction.
public interface IOrganizationStatusService
{
    Task<bool> IsOrganizationActiveAsync(Guid organizationId);
}

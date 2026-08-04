namespace SMS.Shared.Common;

// MT-007 — cross-module interface so SMS.Modules.Auth can check SuperAdminUsers membership at
// login/refresh time (to stamp the is_super_admin JWT claim) without a project reference to
// Tenancy — TenancyDbContext is internal. Same pattern as IOrganizationStatusService.
// SuperAdminUsers is now the sole authoritative source for is_super_admin: no permission-based
// fallback anywhere in the token-issuing path.
public interface ISuperAdminService
{
    Task<bool> IsSuperAdminAsync(int userId);
}

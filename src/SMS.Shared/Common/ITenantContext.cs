namespace SMS.Shared.Common;

// Resolves the current request's tenant. IsSuperAdmin=true bypasses every query filter (see
// TenantScopingExtensions) — this is also how anonymous code paths (login, refresh,
// forgot-password) work correctly without knowing a tenant in advance: they have no authenticated
// HttpContext.User, so TenantContext reports IsSuperAdmin=true for them. A Hangfire background job
// also has no HttpContext, but SMS.API's TenantPropagatingJobFilter captures its originating org at
// enqueue time (see HangfireTenantScope) so the job resolves that real org instead of bypassing.
public interface ITenantContext
{
    Guid OrganizationId { get; }
    bool IsSuperAdmin { get; }
}

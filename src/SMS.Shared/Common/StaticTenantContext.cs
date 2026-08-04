namespace SMS.Shared.Common;

// Plain settable test double for ITenantContext — lets test projects construct a DbContext
// directly (`new XyzDbContext(options, new StaticTenantContext { OrganizationId = ... })`)
// without adding a mocking library dependency just for this.
public sealed class StaticTenantContext : ITenantContext
{
    public Guid OrganizationId { get; set; } = TenantDefaults.ScmDemoOrganizationId;
    public bool IsSuperAdmin { get; set; }
}

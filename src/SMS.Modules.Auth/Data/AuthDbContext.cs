using Microsoft.EntityFrameworkCore;
using SMS.Modules.Auth.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Auth.Data;

internal sealed class AuthDbContext : DbContext, ITenantScopedDbContext
{
    private readonly ITenantContext _tenantContext;
    public ITenantContext TenantContext => _tenantContext;

    public AuthDbContext(DbContextOptions<AuthDbContext> options, ITenantContext tenantContext) : base(options) =>
        _tenantContext = tenantContext;

    internal DbSet<UserAccount>   UserAccounts   => Set<UserAccount>();
    internal DbSet<Department>    Departments    => Set<Department>();
    internal DbSet<Permission>    Permissions    => Set<Permission>();
    internal DbSet<Role>          Roles          => Set<Role>();
    internal DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    internal DbSet<UserPermission> UserPermissions => Set<UserPermission>();
    internal DbSet<UserSession>   UserSessions   => Set<UserSession>();
    internal DbSet<UserSupplierAccess> UserSupplierAccess => Set<UserSupplierAccess>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("auth");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AuthDbContext).Assembly);
        modelBuilder.ApplyTenantQueryFilters(this);
        
        // F35 — each of those filters puts WHERE OrganizationId = @org on every query against
        // every one of these tables, and none of them had an index leading with it.
        modelBuilder.ApplyTenantIndexes();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        this.StampTenantScopedEntities(_tenantContext);
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        this.StampTenantScopedEntities(_tenantContext);
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
}

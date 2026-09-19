using Microsoft.EntityFrameworkCore;
using SMS.Modules.Warehouse.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Warehouse.Data;

internal sealed class WarehouseDbContext : DbContext, ITenantScopedDbContext
{
    private readonly ITenantContext _tenantContext;
    public ITenantContext TenantContext => _tenantContext;

    public WarehouseDbContext(DbContextOptions<WarehouseDbContext> options, ITenantContext tenantContext) : base(options) =>
        _tenantContext = tenantContext;

    internal DbSet<Grn>                    Grns                  => Set<Grn>();
    internal DbSet<GrnLine>               GrnLines              => Set<GrnLine>();
    internal DbSet<SupplierReturnOrder>   SupplierReturnOrders  => Set<SupplierReturnOrder>();
    internal DbSet<SupplierReturnOrderLine> SupplierReturnOrderLines => Set<SupplierReturnOrderLine>();
    internal DbSet<SroAcknowledgmentLink> SroAcknowledgmentLinks => Set<SroAcknowledgmentLink>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("warehouse");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(WarehouseDbContext).Assembly);
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

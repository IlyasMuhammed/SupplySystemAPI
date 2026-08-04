using Microsoft.EntityFrameworkCore;
using SMS.Modules.Suppliers.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Suppliers.Data;

internal sealed class SuppliersDbContext : DbContext, ITenantScopedDbContext
{
    private readonly ITenantContext _tenantContext;
    public ITenantContext TenantContext => _tenantContext;

    public SuppliersDbContext(DbContextOptions<SuppliersDbContext> options, ITenantContext tenantContext) : base(options) =>
        _tenantContext = tenantContext;

    internal DbSet<Supplier> Suppliers => Set<Supplier>();
    internal DbSet<SupplierTypeMapping> SupplierTypeMappings => Set<SupplierTypeMapping>();
    internal DbSet<SupplierIndustryMapping> SupplierIndustryMappings => Set<SupplierIndustryMapping>();
    internal DbSet<SupplierContact> SupplierContacts => Set<SupplierContact>();
    internal DbSet<SupplierDocument> SupplierDocuments => Set<SupplierDocument>();
    internal DbSet<SupplierBankDetail> SupplierBankDetails => Set<SupplierBankDetail>();
    internal DbSet<SupplierType> SupplierTypes => Set<SupplierType>();
    internal DbSet<SupplierCategory> SupplierCategories => Set<SupplierCategory>();

    internal DbSet<ScorecardDimensionWeight> ScorecardDimensionWeights => Set<ScorecardDimensionWeight>();
    internal DbSet<SupplierScoreSnapshot> SupplierScoreSnapshots => Set<SupplierScoreSnapshot>();
    internal DbSet<GrnScoreDetail> GrnScoreDetails => Set<GrnScoreDetail>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("suppliers");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SuppliersDbContext).Assembly);
        modelBuilder.ApplyTenantQueryFilters(this);
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

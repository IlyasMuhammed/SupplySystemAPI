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

    internal DbSet<BusinessPartner> BusinessPartners => Set<BusinessPartner>();
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

        // P1-03 asked for "EF config: HasQueryFilter(p => p.OrgId == _orgId)" on BusinessPartner —
        // ApplyTenantQueryFilters below already does exactly that for every ITenantScopedEntity in
        // this context, BusinessPartner included, and adds the super-admin bypass
        // (IsSuperAdmin || OrganizationId == ...) that a hand-written filter here would not have.
        // A second HasQueryFilter call on the same entity REPLACES the first rather than combining
        // with it, so writing one by hand here would silently drop that bypass — not adding one.
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

using Microsoft.EntityFrameworkCore;
using SMS.Modules.Lookups.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Lookups.Data;

internal sealed class LookupsDbContext : DbContext, ITenantScopedDbContext
{
    private readonly ITenantContext _tenantContext;
    public ITenantContext TenantContext => _tenantContext;

    public LookupsDbContext(DbContextOptions<LookupsDbContext> options, ITenantContext tenantContext) : base(options) =>
        _tenantContext = tenantContext;

    internal DbSet<City> Cities => Set<City>();
    internal DbSet<Country> Countries => Set<Country>();
    internal DbSet<Currency> Currencies => Set<Currency>();
    internal DbSet<DeliveryTerm> DeliveryTerms => Set<DeliveryTerm>();
    internal DbSet<PaymentTerm> PaymentTerms => Set<PaymentTerm>();
    internal DbSet<LookupType> LookupTypes => Set<LookupType>();
    internal DbSet<LookupValue> LookupValues => Set<LookupValue>();
    internal DbSet<PoDocumentTemplate> PoDocumentTemplates => Set<PoDocumentTemplate>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("lookups");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(LookupsDbContext).Assembly);
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

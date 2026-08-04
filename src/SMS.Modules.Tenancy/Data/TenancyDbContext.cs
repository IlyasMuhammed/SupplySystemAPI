using Microsoft.EntityFrameworkCore;
using SMS.Modules.Tenancy.Domain;

namespace SMS.Modules.Tenancy.Data;

internal sealed class TenancyDbContext : DbContext
{
    public TenancyDbContext(DbContextOptions<TenancyDbContext> options) : base(options) { }

    internal DbSet<Organization> Organizations => Set<Organization>();
    internal DbSet<FeatureDefinition> FeatureDefinitions => Set<FeatureDefinition>();
    internal DbSet<OrganizationFeature> OrganizationFeatures => Set<OrganizationFeature>();
    internal DbSet<PlanFeatureTemplate> PlanFeatureTemplates => Set<PlanFeatureTemplate>();
    internal DbSet<SuperAdminUser> SuperAdminUsers => Set<SuperAdminUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("tenant");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TenancyDbContext).Assembly);
    }
}
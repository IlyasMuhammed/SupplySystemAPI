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
    internal DbSet<OrganizationSettings> OrganizationSettings => Set<OrganizationSettings>();
    internal DbSet<SuperAdminUser> SuperAdminUsers => Set<SuperAdminUser>();
    internal DbSet<OrganizationCurrencySettings> OrganizationCurrencySettings => Set<OrganizationCurrencySettings>();
    internal DbSet<FeatureDependency> FeatureDependencies => Set<FeatureDependency>();
    internal DbSet<OrganizationFeatureHistory> OrganizationFeatureHistory => Set<OrganizationFeatureHistory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("tenant");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TenancyDbContext).Assembly);

        // A37 — SQLite (the relational test databases) has no rowversion type: the store generates a value on insert
        // instead, so a new row satisfies NOT NULL. SQL Server keeps its real rowversion.
        if (Database.ProviderName?.EndsWith(".Sqlite", StringComparison.Ordinal) == true)
            modelBuilder.Entity<OrganizationFeature>().Property(x => x.RowVersion).HasDefaultValueSql("randomblob(8)");
    }
}
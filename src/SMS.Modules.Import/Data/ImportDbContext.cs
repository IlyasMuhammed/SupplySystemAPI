using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Import.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Import.Data;

internal sealed class ImportDbContext : DbContext, ITenantScopedDbContext
{
    public const string Schema = "import";

    private readonly ITenantContext _tenantContext;
    public ITenantContext TenantContext => _tenantContext;

    public ImportDbContext(DbContextOptions<ImportDbContext> options, ITenantContext tenantContext) : base(options) =>
        _tenantContext = tenantContext;

    internal DbSet<ImportProfile>           Profiles           => Set<ImportProfile>();
    internal DbSet<ImportProfileField>      ProfileFields      => Set<ImportProfileField>();
    internal DbSet<ImportProfileOverride>   ProfileOverrides   => Set<ImportProfileOverride>();
    internal DbSet<ImportSession>           Sessions           => Set<ImportSession>();
    internal DbSet<ImportColumnMapping>     ColumnMappings     => Set<ImportColumnMapping>();
    internal DbSet<ImportRow>               Rows               => Set<ImportRow>();
    internal DbSet<ImportExternalReference> ExternalReferences => Set<ImportExternalReference>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ImportDbContext).Assembly);
        modelBuilder.ApplyTenantQueryFilters(this);
        modelBuilder.ApplyTenantIndexes();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        BeforeSave();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        BeforeSave();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void BeforeSave()
    {
        this.StampTenantScopedEntities(_tenantContext);
        this.StampModifiedAt();
    }
}

internal sealed class ImportDbContextFactory : IDesignTimeDbContextFactory<ImportDbContext>
{
    public ImportDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ImportDbContext>().UseSqlServer(DesignTimeConnection.Resolve()).Options, new StaticTenantContext());
}

internal sealed class ImportProfileMap : IEntityTypeConfiguration<ImportProfile>
{
    public void Configure(EntityTypeBuilder<ImportProfile> b)
    {
        b.ToTable("profiles");
        b.HasKey(x => x.Id);
        b.Property(x => x.EntityType).HasMaxLength(40).IsRequired();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.MatchFieldsJson).HasMaxLength(1000).IsRequired();
        b.HasIndex(x => x.EntityType).IsUnique();
        b.HasMany(x => x.Fields).WithOne().HasForeignKey(f => f.ProfileId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ImportProfileFieldMap : IEntityTypeConfiguration<ImportProfileField>
{
    public void Configure(EntityTypeBuilder<ImportProfileField> b)
    {
        b.ToTable("profile_fields");
        b.HasKey(x => x.Id);
        b.Property(x => x.FieldName).HasMaxLength(100).IsRequired();
        b.Property(x => x.DisplayName).HasMaxLength(150).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.FieldType).HasMaxLength(20).IsRequired();
        b.Property(x => x.SampleValue).HasMaxLength(200);
        b.Property(x => x.MinValue).HasColumnType("decimal(19,6)");
        b.Property(x => x.MaxValue).HasColumnType("decimal(19,6)");
        b.Property(x => x.ValidationRegex).HasMaxLength(500);
        b.Property(x => x.DefaultValue).HasMaxLength(200);
        b.Property(x => x.LookupList).HasMaxLength(100);
        b.Property(x => x.AliasesJson).HasMaxLength(2000);
        b.HasIndex(x => new { x.ProfileId, x.FieldName }).IsUnique();
    }
}

internal sealed class ImportProfileOverrideMap : IEntityTypeConfiguration<ImportProfileOverride>
{
    public void Configure(EntityTypeBuilder<ImportProfileOverride> b)
    {
        b.ToTable("profile_overrides");
        b.HasKey(x => x.Id);
        b.Property(x => x.EntityType).HasMaxLength(40).IsRequired();
        b.Property(x => x.DuplicateStrategy).HasMaxLength(20).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.EntityType }).IsUnique();
    }
}

internal sealed class ImportSessionMap : IEntityTypeConfiguration<ImportSession>
{
    public void Configure(EntityTypeBuilder<ImportSession> b)
    {
        b.ToTable("sessions");
        b.HasKey(x => x.Id);
        b.Property(x => x.EntityType).HasMaxLength(40).IsRequired();
        b.Property(x => x.FileName).HasMaxLength(260).IsRequired();
        b.Property(x => x.FileType).HasMaxLength(10).IsRequired();
        b.Property(x => x.SheetName).HasMaxLength(100);
        b.Property(x => x.SheetNamesJson).IsRequired();
        b.Property(x => x.HeadersJson).IsRequired();
        b.Property(x => x.Status).HasMaxLength(30).IsRequired();
        b.Property(x => x.DuplicateStrategy).HasMaxLength(20).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.SavedMappingName).HasMaxLength(100);
        b.Property(x => x.ErrorMessage).HasMaxLength(2000);
        b.Property(x => x.JobId).HasMaxLength(100);
        b.Property(x => x.CreatedByName).HasMaxLength(200);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.HasIndex(x => new { x.OrganizationId, x.EntityType, x.Status });
        b.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
        // IMP-FILE-08 — at most one running import per organization and entity type, enforced by the database too.
        b.HasIndex(x => new { x.OrganizationId, x.EntityType }).IsUnique()
            .HasFilter("[Status] = 'IMPORTING'").HasDatabaseName("UX_sessions_one_importing");
        b.HasMany(x => x.Mappings).WithOne().HasForeignKey(m => m.SessionId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ImportColumnMappingMap : IEntityTypeConfiguration<ImportColumnMapping>
{
    public void Configure(EntityTypeBuilder<ImportColumnMapping> b)
    {
        b.ToTable("column_mappings");
        b.HasKey(x => x.Id);
        b.Property(x => x.SourceColumnName).HasMaxLength(300).IsRequired();
        b.Property(x => x.TargetFieldName).HasMaxLength(100);
        b.Property(x => x.ConfidenceScore).HasColumnType("decimal(3,2)");
        b.HasIndex(x => new { x.SessionId, x.SourceColumnIndex }).IsUnique();
    }
}

internal sealed class ImportRowMap : IEntityTypeConfiguration<ImportRow>
{
    public void Configure(EntityTypeBuilder<ImportRow> b)
    {
        b.ToTable("rows");
        b.HasKey(x => x.Id);
        b.Property(x => x.RawJson).IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.ExternalId).HasMaxLength(100);
        b.HasIndex(x => new { x.SessionId, x.RowNumber }).IsUnique();
        b.HasIndex(x => new { x.SessionId, x.Status });
        b.HasOne<ImportSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ImportExternalReferenceMap : IEntityTypeConfiguration<ImportExternalReference>
{
    public void Configure(EntityTypeBuilder<ImportExternalReference> b)
    {
        b.ToTable("external_references");
        b.HasKey(x => x.Id);
        b.Property(x => x.EntityType).HasMaxLength(40).IsRequired();
        b.Property(x => x.ExternalId).HasMaxLength(100).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.EntityType, x.ExternalId }).IsUnique();
        b.HasIndex(x => new { x.OrganizationId, x.EntityType, x.EntityUuid }).IsUnique();
    }
}

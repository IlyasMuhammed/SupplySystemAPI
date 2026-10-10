using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Tenancy.Domain;

namespace SMS.Modules.Tenancy.Data.Maps;

internal sealed class OrganizationMap : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> b)
    {
        b.ToTable("Organizations");
        b.HasKey(x => x.Id);
        b.Property(x => x.OrgCode).HasMaxLength(30).IsRequired();
        b.HasIndex(x => x.OrgCode).IsUnique();
        b.Property(x => x.OrgName).HasMaxLength(200).IsRequired();
        b.Property(x => x.Plan).HasMaxLength(20).IsRequired().HasDefaultValue("BASIC");
        b.Property(x => x.IsActive).HasDefaultValue(true);
        b.Property(x => x.ContactEmail).HasMaxLength(150);
        b.Property(x => x.ContactPhone).HasMaxLength(30);
        b.Property(x => x.Address).HasMaxLength(500);
        b.Property(x => x.Country).HasMaxLength(100);
        b.Property(x => x.TimeZone).HasMaxLength(50);
    }
}

internal sealed class FeatureDefinitionMap : IEntityTypeConfiguration<FeatureDefinition>
{
    public void Configure(EntityTypeBuilder<FeatureDefinition> b)
    {
        b.ToTable("FeatureDefinitions");
        b.HasKey(x => x.Id);
        b.Property(x => x.FeatureCode).HasMaxLength(50).IsRequired();
        b.HasIndex(x => x.FeatureCode).IsUnique();
        b.Property(x => x.FeatureName).HasMaxLength(150).IsRequired();
        b.Property(x => x.Category).HasMaxLength(20).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.IsCore).HasDefaultValue(false);
        // A37 — no EF default on the bools (a store default would swallow an explicit false); the migration's SQL
        // defaults only cover rows written by older code.
        b.Property(x => x.ParentModuleCode).HasMaxLength(50);
        b.Property(x => x.Icon).HasMaxLength(50);
    }
}

internal sealed class FeatureDependencyMap : IEntityTypeConfiguration<FeatureDependency>
{
    public void Configure(EntityTypeBuilder<FeatureDependency> b)
    {
        b.ToTable("feature_dependencies");
        b.HasKey(x => new { x.FeatureCode, x.DependsOnCode }).HasName("PK_feature_dependencies");
        b.Property(x => x.FeatureCode).HasMaxLength(50);
        b.Property(x => x.DependsOnCode).HasMaxLength(50);
    }
}

internal sealed class OrganizationFeatureMap : IEntityTypeConfiguration<OrganizationFeature>
{
    public void Configure(EntityTypeBuilder<OrganizationFeature> b)
    {
        b.ToTable("OrganizationFeatures", t =>
            t.HasCheckConstraint("CK_OrganizationFeatures_GraceOnlyWhenDisabled", "[GracePeriodEndsAt] IS NULL OR [IsEnabled] = 0"));
        b.HasKey(x => x.Id);
        b.Property(x => x.IsEnabled).HasDefaultValue(false);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasIndex(x => new { x.OrganizationId, x.FeatureDefinitionId }).IsUnique();
    }
}

internal sealed class OrganizationFeatureHistoryMap : IEntityTypeConfiguration<OrganizationFeatureHistory>
{
    public void Configure(EntityTypeBuilder<OrganizationFeatureHistory> b)
    {
        // No FK to Organizations: an audit trail outlives what it describes (and test cleanups delete organizations).
        b.ToTable("organization_feature_history");
        b.HasKey(x => x.Id).HasName("PK_organization_feature_history");
        b.Property(x => x.FeatureCode).HasMaxLength(50).IsRequired();
        b.Property(x => x.Action).HasMaxLength(30).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(500);
        b.HasIndex(x => new { x.OrganizationId, x.FeatureCode, x.PerformedAt }).HasDatabaseName("IX_organization_feature_history_org_code_at");
    }
}

internal sealed class PlanFeatureTemplateMap : IEntityTypeConfiguration<PlanFeatureTemplate>
{
    public void Configure(EntityTypeBuilder<PlanFeatureTemplate> b)
    {
        b.ToTable("PlanFeatureTemplates");
        b.HasKey(x => x.Id);
        b.Property(x => x.Plan).HasMaxLength(20).IsRequired();
        b.Property(x => x.IsEnabledByDefault).HasDefaultValue(false);
        b.HasIndex(x => new { x.Plan, x.FeatureDefinitionId }).IsUnique();
    }
}

internal sealed class OrganizationSettingsMap : IEntityTypeConfiguration<OrganizationSettings>
{
    public void Configure(EntityTypeBuilder<OrganizationSettings> b)
    {
        b.ToTable("OrganizationSettings");
        b.HasKey(x => x.OrganizationId);
        b.Property(x => x.AckLinkExpiryDays).IsRequired();
        b.HasOne(x => x.Organization).WithOne()
            .HasForeignKey<OrganizationSettings>(x => x.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class OrganizationCurrencySettingsMap : IEntityTypeConfiguration<OrganizationCurrencySettings>
{
    public const int AccountCodeLength = 20;

    public void Configure(EntityTypeBuilder<OrganizationCurrencySettings> b)
    {
        b.ToTable("organization_currency_settings");
        b.HasKey(x => x.OrganizationId).HasName("PK_organization_currency_settings");
        b.Property(x => x.ExchangeGainAccountCode).HasMaxLength(AccountCodeLength);
        b.Property(x => x.ExchangeLossAccountCode).HasMaxLength(AccountCodeLength);
        b.Property(x => x.UnrealizedGainAccountCode).HasMaxLength(AccountCodeLength);
        b.Property(x => x.UnrealizedLossAccountCode).HasMaxLength(AccountCodeLength);
        // Set by the code on every write (no SQL default: a store-generated default would make EF skip an explicit value
        // equal to the CLR default, and the SQLite test databases cannot run SYSUTCDATETIME()).
        b.Property(x => x.CreatedAt).IsRequired();
        b.Property(x => x.UpdatedAt).IsRequired();
        b.HasOne(x => x.Organization).WithOne()
            .HasForeignKey<OrganizationCurrencySettings>(x => x.OrganizationId)
            .HasConstraintName("FK_organization_currency_settings_Organizations")
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SuperAdminUserMap: IEntityTypeConfiguration<SuperAdminUser>
{
    public void Configure(EntityTypeBuilder<SuperAdminUser> b)
    {
        b.ToTable("SuperAdminUsers");
        b.HasKey(x => x.UserId);
        b.Property(x => x.UserId).ValueGeneratedNever();
        b.Property(x => x.CreatedAt).IsRequired();
    }
}

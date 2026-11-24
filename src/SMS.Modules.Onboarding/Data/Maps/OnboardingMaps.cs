using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Onboarding.Domain;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Onboarding.Data.Maps;

// A50 API-CONTRACT §1. CHECK constraints list the code vocabularies; one default template per (org, type) is a filtered
// unique index. No FKs to other schemas (D-2: the data stays in the owning modules).

internal static class OnboardingColumn
{
    /// <summary>"[Col] IN ('A','B')" for a CHECK constraint.</summary>
    public static string In(string column, IEnumerable<string> values) =>
        $"[{column}] IN ({string.Join(", ", values.Select(v => $"'{v}'"))})";
}

internal sealed class SetupDefaultMap : IEntityTypeConfiguration<SetupDefault>
{
    public void Configure(EntityTypeBuilder<SetupDefault> b)
    {
        b.ToTable("SetupDefaults", t => t.HasCheckConstraint("CK_SetupDefaults_Version", "[Version] >= 1"));
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.CountryCode).HasMaxLength(10).IsRequired();
        b.Property(x => x.IndustryType).HasMaxLength(50).IsRequired();
        b.Property(x => x.DataCategory).HasMaxLength(40).IsRequired();
        b.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
        b.Property(x => x.DataPayload).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasIndex(x => new { x.CountryCode, x.IndustryType, x.DataCategory }).IsUnique().HasDatabaseName("UQ_SD_Key");
    }
}

internal sealed class TenantSetupLogMap : IEntityTypeConfiguration<TenantSetupLog>
{
    public void Configure(EntityTypeBuilder<TenantSetupLog> b)
    {
        b.ToTable("TenantSetupLogs", t => t.HasCheckConstraint("CK_TenantSetupLogs_Action", OnboardingColumn.In(nameof(TenantSetupLog.Action), SetupLogActions.All)));
        b.HasKey(x => x.Id);
        b.Property(x => x.CountryCode).HasMaxLength(10).IsRequired();
        b.Property(x => x.IndustryType).HasMaxLength(50).IsRequired();
        b.Property(x => x.DataCategory).HasMaxLength(40).IsRequired();
        b.Property(x => x.ResolvedCountryCode).HasMaxLength(10);
        b.Property(x => x.ResolvedIndustryType).HasMaxLength(50);
        b.Property(x => x.Action).HasMaxLength(20).IsRequired();
        b.Property(x => x.Message).HasMaxLength(2000);
        b.HasIndex(x => new { x.OrganizationId, x.DataCategory, x.AppliedAt }).HasDatabaseName("IX_TSL_Org");
        b.HasIndex(x => x.SetupDefaultId).HasDatabaseName("IX_TSL_SetupDefault");
    }
}

internal sealed class OrganizationSetupMap : IEntityTypeConfiguration<OrganizationSetup>
{
    public void Configure(EntityTypeBuilder<OrganizationSetup> b)
    {
        b.ToTable("OrganizationSetups", t => t.HasCheckConstraint("CK_OrganizationSetups_Status", OnboardingColumn.In(nameof(OrganizationSetup.SetupStatus), SetupStatuses.All)));
        b.HasKey(x => x.OrganizationId);
        b.Property(x => x.OrganizationId).ValueGeneratedNever();
        b.Property(x => x.CountryCode).HasMaxLength(10);
        b.Property(x => x.IndustryType).HasMaxLength(50);
        b.Property(x => x.SetupStatus).HasMaxLength(20).IsRequired();
        b.Property(x => x.Note).HasMaxLength(500);
        b.Property(x => x.RowVersion).IsRowVersion();
    }
}

internal sealed class SeededRecordMap : IEntityTypeConfiguration<SeededRecord>
{
    public void Configure(EntityTypeBuilder<SeededRecord> b)
    {
        b.ToTable("SeededRecords");
        b.HasKey(x => x.Id);
        b.Property(x => x.Category).HasMaxLength(40).IsRequired();
        b.Property(x => x.NaturalKey).HasMaxLength(200).IsRequired();
        b.Property(x => x.EntityRef).HasMaxLength(100).IsRequired();
        b.Property(x => x.Fingerprint).HasMaxLength(100).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.Category, x.NaturalKey }).IsUnique().HasDatabaseName("UQ_SeededRecords_Key");
    }
}

internal sealed class OnboardingChecklistMap : IEntityTypeConfiguration<OnboardingChecklist>
{
    public void Configure(EntityTypeBuilder<OnboardingChecklist> b)
    {
        b.ToTable("OnboardingChecklists");
        b.HasKey(x => x.OrganizationId);
        b.Property(x => x.OrganizationId).ValueGeneratedNever();
        b.HasMany(x => x.Steps).WithOne().HasForeignKey(x => x.OrganizationId).HasPrincipalKey(x => x.OrganizationId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class OnboardingChecklistStepMap : IEntityTypeConfiguration<OnboardingChecklistStep>
{
    public void Configure(EntityTypeBuilder<OnboardingChecklistStep> b)
    {
        b.ToTable("OnboardingChecklistSteps");
        b.HasKey(x => x.Id);
        b.Property(x => x.StepCode).HasMaxLength(40).IsRequired();
        b.Property(x => x.Title).HasMaxLength(200).IsRequired();
        b.Property(x => x.Link).HasMaxLength(300);
        b.Property(x => x.RequiresModule).HasMaxLength(60);
        b.HasIndex(x => new { x.OrganizationId, x.StepCode }).IsUnique().HasDatabaseName("UQ_OnboardingChecklistSteps_Code");
    }
}

internal sealed class DocumentTemplateMap : IEntityTypeConfiguration<DocumentTemplate>
{
    public void Configure(EntityTypeBuilder<DocumentTemplate> b)
    {
        b.ToTable("DocumentTemplates", t =>
            t.HasCheckConstraint("CK_DocumentTemplates_Type", OnboardingColumn.In(nameof(DocumentTemplate.TemplateType), DocumentTemplateTypes.All)));
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.TemplateType).HasMaxLength(30).IsRequired();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.PrimaryColor).HasMaxLength(7);
        b.Property(x => x.BankDetailsText).HasMaxLength(2000);
        b.Property(x => x.TermsAndConditions).HasMaxLength(4000);
        b.HasIndex(x => new { x.OrganizationId, x.TemplateType }, "IX_DocumentTemplates_Type");
        b.HasIndex(x => new { x.OrganizationId, x.TemplateType }, "UQ_DocumentTemplates_Default").IsUnique().HasFilter("[IsDefault] = 1");
    }
}

internal sealed class EmailTemplateMap : IEntityTypeConfiguration<EmailTemplate>
{
    public void Configure(EntityTypeBuilder<EmailTemplate> b)
    {
        b.ToTable("EmailTemplates", t =>
            t.HasCheckConstraint("CK_EmailTemplates_Type", OnboardingColumn.In(nameof(EmailTemplate.TemplateType), EmailTemplateTypes.All)));
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.TemplateType).HasMaxLength(30).IsRequired();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.SubjectTemplate).HasMaxLength(500).IsRequired();
        b.Property(x => x.BodyHtml).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.TemplateType }, "IX_EmailTemplates_Type");
        b.HasIndex(x => new { x.OrganizationId, x.TemplateType }, "UQ_EmailTemplates_Default").IsUnique().HasFilter("[IsDefault] = 1");
    }
}

internal sealed class DemoRecordMap : IEntityTypeConfiguration<DemoRecord>
{
    public void Configure(EntityTypeBuilder<DemoRecord> b)
    {
        b.ToTable("DemoRecords");
        b.HasKey(x => x.Id);
        b.Property(x => x.EntityType).HasMaxLength(50).IsRequired();
        b.Property(x => x.EntityRef).HasMaxLength(100).IsRequired();
        b.Property(x => x.Contributor).HasMaxLength(100);
        b.HasIndex(x => new { x.OrganizationId, x.EntityType, x.EntityRef }).IsUnique().HasDatabaseName("UQ_DemoRecords_Ref");
    }
}

// A51 P1 — industries master. Code is unique; a country offers an industry at most once.
internal sealed class IndustryMap : IEntityTypeConfiguration<Industry>
{
    public void Configure(EntityTypeBuilder<Industry> b)
    {
        b.ToTable("Industries");
        b.HasKey(x => x.Id);
        b.Property(x => x.Code).HasMaxLength(30).IsRequired();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500);
        b.HasIndex(x => x.Code).IsUnique().HasDatabaseName("UQ_Industries_Code");
    }
}

internal sealed class CountryIndustryMap : IEntityTypeConfiguration<CountryIndustry>
{
    public void Configure(EntityTypeBuilder<CountryIndustry> b)
    {
        b.ToTable("CountryIndustries");
        b.HasKey(x => x.Id);
        b.Property(x => x.CountryCode).HasMaxLength(5).IsRequired();
        b.Property(x => x.IndustryCode).HasMaxLength(30).IsRequired();
        b.HasIndex(x => new { x.CountryCode, x.IndustryCode }).IsUnique().HasDatabaseName("UQ_CountryIndustries_Key");
    }
}

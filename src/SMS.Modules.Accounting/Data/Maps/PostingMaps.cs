using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Accounting.Domain;

namespace SMS.Modules.Accounting.Data.Maps;

// A44 D-8 / D-9 / D-12 — the posting log and the finance posting settings (schema finance, migration A44_FinancialPosting).

internal sealed class FinancialPostingMap : IEntityTypeConfiguration<FinancialPosting>
{
    public void Configure(EntityTypeBuilder<FinancialPosting> b)
    {
        b.ToTable("FinancialPostings", t => t.HasCheckConstraint("CK_FinancialPostings_Kind", GlColumn.In(nameof(FinancialPosting.Kind), PostingKinds.All)));
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.IdempotencyKey).HasMaxLength(200).IsRequired();
        b.Property(x => x.Kind).HasMaxLength(10).IsRequired();
        b.Property(x => x.DocumentType).HasMaxLength(30).IsRequired();
        b.Property(x => x.SourceModule).HasMaxLength(30).IsRequired();
        b.Property(x => x.SourceDocumentNumber).HasMaxLength(100).IsRequired();
        b.Property(x => x.TransactionDate).HasColumnType(GlColumn.Date);
        b.Property(x => x.PartnerType).HasMaxLength(20);
        b.Property(x => x.PartnerName).HasMaxLength(200);
        b.Property(x => x.CurrencyCode).HasMaxLength(10);
        b.Property(x => x.TotalAmount).HasColumnType(GlColumn.Money);
        b.Property(x => x.Fingerprint).HasMaxLength(64).IsRequired();
        b.Property(x => x.TransactionJson).IsRequired();
        b.Property(x => x.DocumentPayloadType).HasMaxLength(100);
        b.HasIndex(x => new { x.OrganizationId, x.IdempotencyKey }).IsUnique().HasDatabaseName("UX_FinancialPostings_Org_Key");
        b.HasIndex(x => new { x.OrganizationId, x.SourceDocumentUuid }).HasDatabaseName("IX_FinancialPostings_Org_Source");
        b.HasIndex(x => new { x.OrganizationId, x.ModifiedAt }).HasDatabaseName("IX_FinancialPostings_Org_Modified");
        b.HasMany(x => x.Targets).WithOne(x => x.Posting).HasForeignKey(x => x.PostingId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class FinancialPostingTargetStateMap : IEntityTypeConfiguration<FinancialPostingTargetState>
{
    public void Configure(EntityTypeBuilder<FinancialPostingTargetState> b)
    {
        b.ToTable("FinancialPostingTargets", t =>
        {
            t.HasCheckConstraint("CK_FinancialPostingTargets_Status", GlColumn.In(nameof(FinancialPostingTargetState.Status), PostingTargetStatuses.All));
            t.HasCheckConstraint("CK_FinancialPostingTargets_Attempts", "[Attempts] >= 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.TargetCode).HasMaxLength(30).IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.LastError).HasMaxLength(1000);
        b.Property(x => x.InternalId).HasMaxLength(100);
        b.Property(x => x.ExternalId).HasMaxLength(100);
        b.Property(x => x.ExternalDocNumber).HasMaxLength(100);
        b.Property(x => x.PostedFingerprint).HasMaxLength(64);
        b.HasIndex(x => new { x.PostingId, x.TargetCode }).IsUnique().HasDatabaseName("UX_FinancialPostingTargets_Posting_Target");
        b.HasIndex(x => new { x.OrganizationId, x.TargetCode, x.Status }).HasDatabaseName("IX_FinancialPostingTargets_Org_Target_Status");
        // The retry job (all organizations): due Failed / stuck Pending rows.
        b.HasIndex(x => new { x.Status, x.NextAttemptAt }).HasDatabaseName("IX_FinancialPostingTargets_Status_Next");
    }
}

internal sealed class TenantFinanceConfigMap : IEntityTypeConfiguration<TenantFinanceConfig>
{
    public void Configure(EntityTypeBuilder<TenantFinanceConfig> b)
    {
        b.ToTable("TenantFinanceConfigs", t => t.HasCheckConstraint("CK_TenantFinanceConfigs_Month", "[FiscalYearStartMonth] BETWEEN 1 AND 12"));
        b.HasKey(x => x.Id);
        b.Property(x => x.DefaultCurrencyCode).HasMaxLength(3);
        b.HasIndex(x => x.OrganizationId).IsUnique().HasDatabaseName("UX_TenantFinanceConfigs_OrgId");
    }
}

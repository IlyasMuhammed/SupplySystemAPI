using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Accounting.Domain;

namespace SMS.Modules.Accounting.Data.Maps;

// A47 C1 / C5 / C8 — the journal engine's own tables (docs/journal-engine/ADDENDUM-47-ANALYSIS.md D-1, D-6, D-7, D-16),
// created by the guarded A47_JournalEngine migration. Same conventions as AccountingMaps.cs.

internal sealed class JournalTypeMap : IEntityTypeConfiguration<JournalType>
{
    public void Configure(EntityTypeBuilder<JournalType> b)
    {
        b.ToTable("JournalTypes", t =>
        {
            t.HasCheckConstraint("CK_JournalTypes_Type", GlColumn.In(nameof(JournalType.Type), JournalTypeKinds.All));
            // §4.2 — uppercase alphanumeric (binary collation: the database's default collation is case-insensitive).
            t.HasCheckConstraint("CK_JournalTypes_Code", "LEN([Code]) BETWEEN 1 AND 10 AND [Code] COLLATE Latin1_General_BIN NOT LIKE '%[^A-Z0-9]%'");
            t.HasCheckConstraint("CK_JournalTypes_Prefix", "LEN([SequencePrefix]) BETWEEN 1 AND 10 AND [SequencePrefix] COLLATE Latin1_General_BIN NOT LIKE '%[^A-Z0-9]%'");
        });
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.Code).HasMaxLength(10).IsRequired();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Type).HasMaxLength(20).IsRequired();
        b.Property(x => x.SequencePrefix).HasMaxLength(10).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique().HasDatabaseName("UX_JournalTypes_Org_Code");
        b.HasIndex(x => new { x.OrganizationId, x.SequencePrefix }).IsUnique().HasDatabaseName("UX_JournalTypes_Org_Prefix");
        b.HasIndex(x => new { x.OrganizationId, x.Type }).HasDatabaseName("IX_JournalTypes_Org_Type");
        b.HasIndex(x => new { x.OrganizationId, x.IsActive }).HasFilter("[IsActive] = 1").HasDatabaseName("IX_JournalTypes_Org_Active");
        // §4.2 — one Opening and one Closing type per organization.
        b.HasIndex(x => new { x.OrganizationId, x.Type }, "UX_JournalTypes_Org_Opening").IsUnique().HasFilter("[Type] = 'Opening'");
        b.HasIndex(x => new { x.OrganizationId, x.Type }, "UX_JournalTypes_Org_Closing").IsUnique().HasFilter("[Type] = 'Closing'");
        b.HasOne(x => x.DefaultDebitAccount).WithMany().HasForeignKey(x => x.DefaultDebitAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.DefaultCreditAccount).WithMany().HasForeignKey(x => x.DefaultCreditAccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class JournalEntrySequenceMap : IEntityTypeConfiguration<JournalEntrySequence>
{
    public void Configure(EntityTypeBuilder<JournalEntrySequence> b)
    {
        b.ToTable("JournalEntrySequences", t => t.HasCheckConstraint("CK_JournalEntrySequences_Current", "[CurrentSequence] >= 0"));
        b.HasKey(x => x.Id);
        // NULLs compare equal in a SQL Server unique index: one row per (type or none, year or none, number year).
        b.HasIndex(x => new { x.OrganizationId, x.JournalTypeId, x.FiscalYearId, x.SequenceYear }).IsUnique()
            .HasDatabaseName("UX_JournalEntrySequences_Org_Type_Year");
        b.HasIndex(x => x.JournalTypeId).HasDatabaseName("IX_JournalEntrySequences_JournalTypeId");
        b.HasIndex(x => x.FiscalYearId).HasDatabaseName("IX_JournalEntrySequences_FiscalYearId");
        b.HasOne(x => x.JournalType).WithMany().HasForeignKey(x => x.JournalTypeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.FiscalYear).WithMany().HasForeignKey(x => x.FiscalYearId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ReconcileGroupMap : IEntityTypeConfiguration<ReconcileGroup>
{
    public void Configure(EntityTypeBuilder<ReconcileGroup> b)
    {
        b.ToTable("ReconcileGroups", t =>
        {
            t.HasCheckConstraint("CK_ReconcileGroups_Type", GlColumn.In(nameof(ReconcileGroup.ReconcileType), ReconcileTypes.All));
            t.HasCheckConstraint("CK_ReconcileGroups_Amount", "[Amount] >= 0");
        });
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.GroupNumber).HasMaxLength(30).IsRequired();
        b.Property(x => x.ReconcileType).HasMaxLength(20).IsRequired();
        b.Property(x => x.Amount).HasColumnType(GlColumn.Money);
        b.Property(x => x.CurrencyCode).HasMaxLength(3);
        b.Property(x => x.CreatedByName).HasMaxLength(200);
        b.HasIndex(x => new { x.OrganizationId, x.GroupNumber }).IsUnique().HasDatabaseName("UX_ReconcileGroups_Org_Number");
        b.HasIndex(x => new { x.OrganizationId, x.AccountId }).HasDatabaseName("IX_ReconcileGroups_Org_Account");
        b.HasIndex(x => x.AccountId).HasDatabaseName("IX_ReconcileGroups_AccountId");
        b.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

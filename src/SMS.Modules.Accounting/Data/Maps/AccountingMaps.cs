using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Accounting.Domain;

namespace SMS.Modules.Accounting.Data.Maps;

// A42 §4–§12 (+ D-10 journal, D-20 history). Table names as the spec; schema finance (shared with the Finance module, which
// owns none of these tables). Money decimal(18,4); dates SQL date. CHECK constraints list the GlCodes vocabularies and the
// FSD's simple row rules. In-module references are FKs with Restrict except template → items, year → periods and
// journal entry → lines (cascade).

internal static class GlColumn
{
    public const string Money = "decimal(18,4)";
    public const string Date  = "date";

    /// <summary>"[Col] IN ('A','B')" for a CHECK constraint.</summary>
    public static string In(string column, IEnumerable<string> values) =>
        $"[{column}] IN ({string.Join(", ", values.Select(v => $"'{v}'"))})";

    /// <summary>Same, allowing NULL.</summary>
    public static string NullOrIn(string column, IEnumerable<string> values) => $"[{column}] IS NULL OR {In(column, values)}";

    /// <summary>The 4-level tree rules shared by template items and accounts (§5.2, §6.2).</summary>
    public static void TreeChecks(TableBuilder t, string table, string parentColumn)
    {
        t.HasCheckConstraint($"CK_{table}_Level", "[Level] BETWEEN 1 AND 4");
        t.HasCheckConstraint($"CK_{table}_RootType", In("RootType", RootTypes.All));
        t.HasCheckConstraint($"CK_{table}_IsGroup", "([Level] IN (1, 2, 3) AND [IsGroup] = 1) OR ([Level] = 4 AND [IsGroup] = 0)");
        t.HasCheckConstraint($"CK_{table}_Parent", $"([Level] = 1 AND [{parentColumn}] IS NULL) OR ([Level] > 1 AND [{parentColumn}] IS NOT NULL)");
        t.HasCheckConstraint($"CK_{table}_AccountType", NullOrIn("AccountType", AccountTypes.All));
    }

    public static void DebitCreditChecks(TableBuilder t, string table)
    {
        t.HasCheckConstraint($"CK_{table}_NotNegative", "[DebitAmount] >= 0 AND [CreditAmount] >= 0");
        t.HasCheckConstraint($"CK_{table}_OneSide", "NOT ([DebitAmount] > 0 AND [CreditAmount] > 0)");
    }
}

internal sealed class CoaTemplateMap : IEntityTypeConfiguration<CoaTemplate>
{
    public void Configure(EntityTypeBuilder<CoaTemplate> b)
    {
        b.ToTable("CoaTemplates");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.TemplateCode).HasMaxLength(50).IsRequired();
        b.Property(x => x.TemplateName).HasMaxLength(200).IsRequired();
        b.Property(x => x.CountryCode).HasMaxLength(3);
        b.Property(x => x.BusinessType).HasMaxLength(100);
        b.Property(x => x.CurrencyCode).HasMaxLength(3);
        b.Property(x => x.Description).HasMaxLength(1000);
        b.Property(x => x.Version).HasMaxLength(20).IsRequired();
        b.Property(x => x.IsGlobal).HasColumnName("IsSystem");
        b.Ignore(x => x.IsSystem);
        // SQL Server treats NULLs as equal in a unique index: one system template per code, one per code per organization.
        b.HasIndex(x => new { x.TemplateCode, x.OrganizationId }).IsUnique().HasFilter("[IsActive] = 1")
            .HasDatabaseName("UX_CoaTemplates_Code_Org");
        b.HasIndex(x => x.CountryCode).HasDatabaseName("IX_CoaTemplates_Country");
        b.HasIndex(x => x.BusinessType).HasDatabaseName("IX_CoaTemplates_BusinessType");
        b.HasMany(x => x.Items).WithOne(x => x.Template).HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class CoaTemplateItemMap : IEntityTypeConfiguration<CoaTemplateItem>
{
    public void Configure(EntityTypeBuilder<CoaTemplateItem> b)
    {
        b.ToTable("CoaTemplateItems", t =>
        {
            GlColumn.TreeChecks(t, "CoaTemplateItems", nameof(CoaTemplateItem.ParentItemId));
            t.HasCheckConstraint("CK_CoaTemplateItems_DefaultKey",
                $"([IsDefaultAccount] = 0 AND [DefaultAccountKey] IS NULL) OR ([IsDefaultAccount] = 1 AND {GlColumn.In("DefaultAccountKey", DefaultKeys.All)})");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.AccountCode).HasMaxLength(20).IsRequired();
        b.Property(x => x.AccountName).HasMaxLength(200).IsRequired();
        b.Property(x => x.RootType).HasMaxLength(20).IsRequired();
        b.Property(x => x.AccountType).HasMaxLength(50);
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.DefaultAccountKey).HasMaxLength(50);
        b.HasIndex(x => new { x.TemplateId, x.AccountCode }).IsUnique().HasDatabaseName("UX_CoaTemplateItems_Template_Code");
        b.HasIndex(x => x.ParentItemId).HasDatabaseName("IX_CoaTemplateItems_Parent");
        b.HasIndex(x => new { x.TemplateId, x.Level }).HasDatabaseName("IX_CoaTemplateItems_Level");
        b.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentItemId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ChartOfAccountMap : IEntityTypeConfiguration<ChartOfAccount>
{
    public void Configure(EntityTypeBuilder<ChartOfAccount> b)
    {
        b.ToTable("ChartOfAccounts", t =>
        {
            GlColumn.TreeChecks(t, "ChartOfAccounts", nameof(ChartOfAccount.ParentAccountId));
            t.HasCheckConstraint("CK_ChartOfAccounts_OpeningNotNegative", "[OpeningBalanceDebit] >= 0 AND [OpeningBalanceCredit] >= 0");
            t.HasCheckConstraint("CK_ChartOfAccounts_OpeningOneSide", "NOT ([OpeningBalanceDebit] > 0 AND [OpeningBalanceCredit] > 0)");
            t.HasCheckConstraint("CK_ChartOfAccounts_Path", "[MaterializedPath] LIKE '/%'");
        });
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.AccountCode).HasMaxLength(20).IsRequired();
        b.Property(x => x.AccountName).HasMaxLength(200).IsRequired();
        b.Property(x => x.MaterializedPath).HasMaxLength(500).IsRequired();
        b.Property(x => x.RootType).HasMaxLength(20).IsRequired();
        b.Property(x => x.AccountType).HasMaxLength(50);
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.ExternalCode).HasMaxLength(50);
        b.Property(x => x.OpeningBalanceDebit).HasColumnType(GlColumn.Money);
        b.Property(x => x.OpeningBalanceCredit).HasColumnType(GlColumn.Money);
        b.Property(x => x.CurrentBalance).HasColumnType(GlColumn.Money);
        b.HasIndex(x => new { x.OrganizationId, x.AccountCode }).IsUnique().HasFilter("[IsActive] = 1")
            .HasDatabaseName("UX_ChartOfAccounts_Code_Org");
        b.HasIndex(x => x.ParentAccountId).HasDatabaseName("IX_ChartOfAccounts_Parent");
        b.HasIndex(x => new { x.OrganizationId, x.RootType }).HasDatabaseName("IX_ChartOfAccounts_RootType");
        b.HasIndex(x => new { x.OrganizationId, x.Level }).HasDatabaseName("IX_ChartOfAccounts_Level");
        b.HasIndex(x => new { x.OrganizationId, x.MaterializedPath }).HasDatabaseName("IX_ChartOfAccounts_Path");
        b.HasIndex(x => new { x.OrganizationId, x.AccountType }).HasFilter("[AccountType] IS NOT NULL")
            .HasDatabaseName("IX_ChartOfAccounts_AccountType");
        b.HasIndex(x => new { x.OrganizationId, x.IsActive, x.IsGroup }).HasFilter("[IsActive] = 1 AND [IsGroup] = 0")
            .HasDatabaseName("IX_ChartOfAccounts_Active_Leaf");
        b.HasOne(x => x.Parent).WithMany(x => x.Children).HasForeignKey(x => x.ParentAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.SourceTemplate).WithMany().HasForeignKey(x => x.SourceTemplateId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class FiscalYearMap : IEntityTypeConfiguration<FiscalYear>
{
    public void Configure(EntityTypeBuilder<FiscalYear> b)
    {
        b.ToTable("FiscalYears", t => t.HasCheckConstraint("CK_FiscalYears_Dates", "[EndDate] > [StartDate]"));
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.FiscalYearName).HasMaxLength(100).IsRequired();
        b.Property(x => x.StartDate).HasColumnType(GlColumn.Date);
        b.Property(x => x.EndDate).HasColumnType(GlColumn.Date);
        b.Property(x => x.ReportingCurrencyCode).HasMaxLength(3).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.FiscalYearName }).IsUnique().HasDatabaseName("UX_FiscalYears_Org_Name");
        b.HasIndex(x => new { x.OrganizationId, x.StartDate, x.EndDate }).HasDatabaseName("IX_FiscalYears_Org_Dates");
        b.HasMany(x => x.Periods).WithOne(x => x.FiscalYear).HasForeignKey(x => x.FiscalYearId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class FiscalPeriodMap : IEntityTypeConfiguration<FiscalPeriod>
{
    public void Configure(EntityTypeBuilder<FiscalPeriod> b)
    {
        b.ToTable("FiscalPeriods", t =>
        {
            t.HasCheckConstraint("CK_FiscalPeriods_Number", "[PeriodNumber] BETWEEN 1 AND 13");
            t.HasCheckConstraint("CK_FiscalPeriods_Status", GlColumn.In(nameof(FiscalPeriod.Status), PeriodStatuses.All));
            t.HasCheckConstraint("CK_FiscalPeriods_Dates", "[EndDate] >= [StartDate]");
        });
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.PeriodName).HasMaxLength(100).IsRequired();
        b.Property(x => x.StartDate).HasColumnType(GlColumn.Date);
        b.Property(x => x.EndDate).HasColumnType(GlColumn.Date);
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.HasIndex(x => new { x.FiscalYearId, x.PeriodNumber, x.OrganizationId }).IsUnique().HasDatabaseName("UX_FiscalPeriods_Year_Number");
        b.HasIndex(x => new { x.OrganizationId, x.Status }).HasDatabaseName("IX_FiscalPeriods_Org_Status");
        b.HasIndex(x => new { x.OrganizationId, x.StartDate, x.EndDate }).HasDatabaseName("IX_FiscalPeriods_Org_Dates");
    }
}

internal sealed class AccountingDefaultMap : IEntityTypeConfiguration<AccountingDefault>
{
    public void Configure(EntityTypeBuilder<AccountingDefault> b)
    {
        b.ToTable("AccountingDefaults", t =>
        {
            t.HasCheckConstraint("CK_AccountingDefaults_Key", GlColumn.In(nameof(AccountingDefault.DefaultKey), DefaultKeys.All));
            t.HasCheckConstraint("CK_AccountingDefaults_Category", GlColumn.In(nameof(AccountingDefault.Category), DefaultCategories.All));
        });
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.DefaultKey).HasMaxLength(50).IsRequired();
        b.Property(x => x.DefaultLabel).HasMaxLength(200).IsRequired();
        b.Property(x => x.Category).HasMaxLength(50).IsRequired();
        b.Property(x => x.ExpectedAccountType).HasMaxLength(50);
        b.Property(x => x.HelpText).HasMaxLength(500);
        b.HasIndex(x => new { x.OrganizationId, x.DefaultKey }).IsUnique().HasDatabaseName("UX_AccountingDefaults_Key_Org");
        b.HasIndex(x => x.AccountId).HasDatabaseName("IX_AccountingDefaults_AccountId");
        b.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CoaSetupStatusMap : IEntityTypeConfiguration<CoaSetupStatus>
{
    public void Configure(EntityTypeBuilder<CoaSetupStatus> b)
    {
        b.ToTable("CoaSetupStatuses", t => t.HasCheckConstraint("CK_CoaSetupStatuses_Status", GlColumn.In(nameof(CoaSetupStatus.Status), SetupStatuses.All)));
        b.HasKey(x => x.Id);
        b.Property(x => x.Status).HasMaxLength(30).IsRequired();
        b.Property(x => x.SetupCompletedByName).HasMaxLength(200);
        b.Property(x => x.ConversionDate).HasColumnType(GlColumn.Date);
        b.HasIndex(x => x.OrganizationId).IsUnique().HasDatabaseName("UX_CoaSetupStatuses_OrgId");
        b.HasOne(x => x.Template).WithMany().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class OpeningBalanceEntryMap : IEntityTypeConfiguration<OpeningBalanceEntry>
{
    public void Configure(EntityTypeBuilder<OpeningBalanceEntry> b)
    {
        b.ToTable("OpeningBalanceEntries", t =>
        {
            GlColumn.DebitCreditChecks(t, "OpeningBalanceEntries");
            t.HasCheckConstraint("CK_OpeningBalanceEntries_Status", GlColumn.In(nameof(OpeningBalanceEntry.EntryStatus), OpeningEntryStatuses.All));
        });
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.DebitAmount).HasColumnType(GlColumn.Money);
        b.Property(x => x.CreditAmount).HasColumnType(GlColumn.Money);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.ConversionDate).HasColumnType(GlColumn.Date);
        b.Property(x => x.EntryStatus).HasMaxLength(20).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.AccountId }).IsUnique().HasDatabaseName("UX_OpeningBalanceEntries_Account_Org");
        b.HasIndex(x => new { x.OrganizationId, x.EntryStatus }).HasDatabaseName("IX_OpeningBalanceEntries_Status");
        b.HasIndex(x => x.AccountId).HasDatabaseName("IX_OpeningBalanceEntries_AccountId");
        b.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.JournalEntry).WithMany().HasForeignKey(x => x.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class JournalEntryMap : IEntityTypeConfiguration<JournalEntry>
{
    public void Configure(EntityTypeBuilder<JournalEntry> b)
    {
        b.ToTable("JournalEntries", t =>
        {
            t.HasCheckConstraint("CK_JournalEntries_Status", GlColumn.In(nameof(JournalEntry.Status), JournalStatuses.All));
            t.HasCheckConstraint("CK_JournalEntries_Totals", "[TotalDebit] >= 0 AND [TotalCredit] >= 0");
        });
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.EntryNumber).HasMaxLength(30).IsRequired();
        b.Property(x => x.EntryType).HasMaxLength(30).IsRequired();
        b.Property(x => x.EntryDate).HasColumnType(GlColumn.Date);
        b.Property(x => x.Reference).HasMaxLength(200);
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.TotalDebit).HasColumnType(GlColumn.Money);
        b.Property(x => x.TotalCredit).HasColumnType(GlColumn.Money);
        b.Property(x => x.CurrencyCode).HasMaxLength(3);
        b.HasIndex(x => new { x.OrganizationId, x.EntryNumber }).IsUnique().HasDatabaseName("UX_JournalEntries_Org_Number");
        b.HasIndex(x => new { x.OrganizationId, x.EntryType, x.Status }).HasDatabaseName("IX_JournalEntries_Org_Type_Status");
        b.HasIndex(x => new { x.OrganizationId, x.EntryDate }).HasDatabaseName("IX_JournalEntries_Org_Date");
        b.HasMany(x => x.Lines).WithOne(x => x.JournalEntry).HasForeignKey(x => x.JournalEntryId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class JournalEntryLineMap : IEntityTypeConfiguration<JournalEntryLine>
{
    public void Configure(EntityTypeBuilder<JournalEntryLine> b)
    {
        b.ToTable("JournalEntryLines", t => GlColumn.DebitCreditChecks(t, "JournalEntryLines"));
        b.HasKey(x => x.Id);
        b.Property(x => x.DebitAmount).HasColumnType(GlColumn.Money);
        b.Property(x => x.CreditAmount).HasColumnType(GlColumn.Money);
        b.Property(x => x.Memo).HasMaxLength(500);
        b.HasIndex(x => new { x.JournalEntryId, x.LineNumber }).IsUnique().HasDatabaseName("UX_JournalEntryLines_Entry_Line");
        b.HasIndex(x => new { x.OrganizationId, x.AccountId }).HasDatabaseName("IX_JournalEntryLines_Org_Account");
        b.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ExternalSystemMappingMap : IEntityTypeConfiguration<ExternalSystemMapping>
{
    public void Configure(EntityTypeBuilder<ExternalSystemMapping> b)
    {
        b.ToTable("ExternalSystemMappings", t =>
        {
            t.HasCheckConstraint("CK_ExternalSystemMappings_SystemType", GlColumn.In(nameof(ExternalSystemMapping.SystemType), ExternalSystemTypes.All));
            t.HasCheckConstraint("CK_ExternalSystemMappings_Direction", GlColumn.In(nameof(ExternalSystemMapping.SyncDirection), SyncDirections.All));
            t.HasCheckConstraint("CK_ExternalSystemMappings_SyncStatus", GlColumn.NullOrIn(nameof(ExternalSystemMapping.SyncStatus), SyncStatuses.All));
        });
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.SystemType).HasMaxLength(50).IsRequired();
        b.Property(x => x.SystemName).HasMaxLength(200).IsRequired();
        b.Property(x => x.ExternalAccountCode).HasMaxLength(100).IsRequired();
        b.Property(x => x.ExternalAccountName).HasMaxLength(200);
        b.Property(x => x.SyncDirection).HasMaxLength(20).IsRequired();
        b.Property(x => x.SyncStatus).HasMaxLength(30);
        b.Property(x => x.SyncErrorMessage).HasMaxLength(1000);
        b.HasIndex(x => new { x.OrganizationId, x.SystemType, x.AccountId }).IsUnique().HasDatabaseName("UX_ExtSysMappings_Sys_Account_Org");
        b.HasIndex(x => x.AccountId).HasDatabaseName("IX_ExtSysMappings_AccountId");
        b.HasIndex(x => new { x.OrganizationId, x.SystemType, x.SyncDirection }).HasFilter("[IsActive] = 1")
            .HasDatabaseName("IX_ExtSysMappings_SyncDirection");
        b.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class GlSetupHistoryMap : IEntityTypeConfiguration<GlSetupHistory>
{
    public void Configure(EntityTypeBuilder<GlSetupHistory> b)
    {
        b.ToTable("GlSetupHistory");
        b.HasKey(x => x.Id);
        b.Property(x => x.EventType).HasMaxLength(60).IsRequired();
        b.Property(x => x.Summary).HasMaxLength(500).IsRequired();
        b.Property(x => x.UserName).HasMaxLength(200);
        b.HasIndex(x => new { x.OrganizationId, x.At }).HasDatabaseName("IX_GlSetupHistory_Org_At");
    }
}

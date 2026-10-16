using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Accounting.Migrations
{
    /// <summary>
    /// A44 (D-8, D-9, D-12, §3) - the financial posting log (<c>FinancialPostings</c>, <c>FinancialPostingTargets</c>), the
    /// per-organization posting settings (<c>TenantFinanceConfigs</c>) and four columns on <c>JournalEntries</c>
    /// (SourceModule, SourceDocumentUuid, IdempotencyKey, ReversesEntryId) with their indexes and the self-FK.
    /// <para>
    /// <b>Guarded and idempotent</b> like A42_AccountingInitial (every API start migrates the shared, drifted database): each
    /// table, column, index and constraint is created only when missing. Additive only - nothing existing is changed.
    /// </para>
    /// </summary>
    public partial class A44_FinancialPosting : Migration
    {
        private static void Table(MigrationBuilder mb, string name, string body) =>
            mb.Sql($"IF OBJECT_ID(N'[finance].[{name}]', N'U') IS NULL\nBEGIN\n    CREATE TABLE [finance].[{name}] (\n{body}\n    );\nEND;");

        private static void Index(MigrationBuilder mb, string table, string name, string ddl) =>
            mb.Sql($"IF OBJECT_ID(N'[finance].[{table}]', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{name}' AND object_id = OBJECT_ID(N'[finance].[{table}]'))\n    {ddl};");

        private static void Column(MigrationBuilder mb, string table, string column, string type) =>
            mb.Sql($"IF OBJECT_ID(N'[finance].[{table}]', N'U') IS NOT NULL AND COL_LENGTH(N'[finance].[{table}]', N'{column}') IS NULL\n    ALTER TABLE [finance].[{table}] ADD [{column}] {type};");

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF SCHEMA_ID(N'finance') IS NULL EXEC(N'CREATE SCHEMA [finance];');");

            // §3 - journal source / posting key / reversal link (all nullable: A42 opening balance journals have none).
            Column(migrationBuilder, "JournalEntries", "SourceModule", "nvarchar(30) NULL");
            Column(migrationBuilder, "JournalEntries", "SourceDocumentUuid", "uniqueidentifier NULL");
            Column(migrationBuilder, "JournalEntries", "IdempotencyKey", "nvarchar(200) NULL");
            Column(migrationBuilder, "JournalEntries", "ReversesEntryId", "int NULL");

            Table(migrationBuilder, "FinancialPostings", """
                [Id] int NOT NULL IDENTITY,
                [Uuid] uniqueidentifier NOT NULL,
                [OrganizationId] uniqueidentifier NOT NULL,
                [IdempotencyKey] nvarchar(200) NOT NULL,
                [Kind] nvarchar(10) NOT NULL,
                [DocumentType] nvarchar(30) NOT NULL,
                [SourceModule] nvarchar(30) NOT NULL,
                [SourceDocumentUuid] uniqueidentifier NOT NULL,
                [SourceDocumentNumber] nvarchar(100) NOT NULL,
                [TransactionDate] date NOT NULL,
                [PartnerUuid] uniqueidentifier NULL,
                [PartnerType] nvarchar(20) NULL,
                [PartnerName] nvarchar(200) NULL,
                [CurrencyCode] nvarchar(10) NULL,
                [TotalAmount] decimal(18,4) NOT NULL,
                [Fingerprint] nvarchar(64) NOT NULL,
                [Version] int NOT NULL,
                [TransactionJson] nvarchar(max) NOT NULL,
                [DocumentJson] nvarchar(max) NULL,
                [DocumentPayloadType] nvarchar(100) NULL,
                [CreatedAt] datetime2 NOT NULL,
                [ModifiedAt] datetime2 NOT NULL,
                CONSTRAINT [PK_FinancialPostings] PRIMARY KEY ([Id]),
                CONSTRAINT [CK_FinancialPostings_Kind] CHECK ([Kind] IN ('Post', 'Void', 'Reverse'))
                """);

            Table(migrationBuilder, "TenantFinanceConfigs", """
                [Id] int NOT NULL IDENTITY,
                [OrganizationId] uniqueidentifier NOT NULL,
                [InternalAccountingEnabled] bit NOT NULL,
                [AutoPostToGL] bit NOT NULL,
                [DefaultCurrencyCode] nvarchar(3) NULL,
                [FiscalYearStartMonth] int NOT NULL,
                [CreatedAt] datetime2 NOT NULL,
                [ModifiedAt] datetime2 NOT NULL,
                [ModifiedBy] int NULL,
                CONSTRAINT [PK_TenantFinanceConfigs] PRIMARY KEY ([Id]),
                CONSTRAINT [CK_TenantFinanceConfigs_Month] CHECK ([FiscalYearStartMonth] BETWEEN 1 AND 12)
                """);

            Table(migrationBuilder, "FinancialPostingTargets", """
                [Id] int NOT NULL IDENTITY,
                [OrganizationId] uniqueidentifier NOT NULL,
                [PostingId] int NOT NULL,
                [TargetCode] nvarchar(30) NOT NULL,
                [Status] nvarchar(20) NOT NULL,
                [Attempts] int NOT NULL,
                [NextAttemptAt] datetime2 NULL,
                [LastAttemptAt] datetime2 NULL,
                [LastError] nvarchar(1000) NULL,
                [InternalId] nvarchar(100) NULL,
                [ExternalId] nvarchar(100) NULL,
                [PostedFingerprint] nvarchar(64) NULL,
                [PostedAt] datetime2 NULL,
                [CreatedAt] datetime2 NOT NULL,
                [ModifiedAt] datetime2 NOT NULL,
                CONSTRAINT [PK_FinancialPostingTargets] PRIMARY KEY ([Id]),
                CONSTRAINT [CK_FinancialPostingTargets_Attempts] CHECK ([Attempts] >= 0),
                CONSTRAINT [CK_FinancialPostingTargets_Status] CHECK ([Status] IN ('Pending', 'Posted', 'Queued', 'Held', 'Failed', 'DeadLetter', 'Skipped')),
                CONSTRAINT [FK_FinancialPostingTargets_FinancialPostings_PostingId] FOREIGN KEY ([PostingId]) REFERENCES [finance].[FinancialPostings] ([Id]) ON DELETE CASCADE
                """);

            Index(migrationBuilder, "JournalEntries", "IX_JournalEntries_Org_Key",
                "CREATE INDEX [IX_JournalEntries_Org_Key] ON [finance].[JournalEntries] ([OrganizationId], [IdempotencyKey]) WHERE [IdempotencyKey] IS NOT NULL");
            Index(migrationBuilder, "JournalEntries", "IX_JournalEntries_Org_Source",
                "CREATE INDEX [IX_JournalEntries_Org_Source] ON [finance].[JournalEntries] ([OrganizationId], [SourceDocumentUuid]) WHERE [SourceDocumentUuid] IS NOT NULL");
            Index(migrationBuilder, "JournalEntries", "IX_JournalEntries_Reverses",
                "CREATE INDEX [IX_JournalEntries_Reverses] ON [finance].[JournalEntries] ([ReversesEntryId]) WHERE [ReversesEntryId] IS NOT NULL");
            Index(migrationBuilder, "FinancialPostings", "IX_FinancialPostings_Org_Modified",
                "CREATE INDEX [IX_FinancialPostings_Org_Modified] ON [finance].[FinancialPostings] ([OrganizationId], [ModifiedAt])");
            Index(migrationBuilder, "FinancialPostings", "IX_FinancialPostings_Org_Source",
                "CREATE INDEX [IX_FinancialPostings_Org_Source] ON [finance].[FinancialPostings] ([OrganizationId], [SourceDocumentUuid])");
            Index(migrationBuilder, "FinancialPostings", "IX_FinancialPostings_Uuid",
                "CREATE UNIQUE INDEX [IX_FinancialPostings_Uuid] ON [finance].[FinancialPostings] ([Uuid])");
            Index(migrationBuilder, "FinancialPostings", "UX_FinancialPostings_Org_Key",
                "CREATE UNIQUE INDEX [UX_FinancialPostings_Org_Key] ON [finance].[FinancialPostings] ([OrganizationId], [IdempotencyKey])");
            Index(migrationBuilder, "FinancialPostingTargets", "IX_FinancialPostingTargets_Org_Target_Status",
                "CREATE INDEX [IX_FinancialPostingTargets_Org_Target_Status] ON [finance].[FinancialPostingTargets] ([OrganizationId], [TargetCode], [Status])");
            Index(migrationBuilder, "FinancialPostingTargets", "IX_FinancialPostingTargets_Status_Next",
                "CREATE INDEX [IX_FinancialPostingTargets_Status_Next] ON [finance].[FinancialPostingTargets] ([Status], [NextAttemptAt])");
            Index(migrationBuilder, "FinancialPostingTargets", "UX_FinancialPostingTargets_Posting_Target",
                "CREATE UNIQUE INDEX [UX_FinancialPostingTargets_Posting_Target] ON [finance].[FinancialPostingTargets] ([PostingId], [TargetCode])");
            Index(migrationBuilder, "TenantFinanceConfigs", "UX_TenantFinanceConfigs_OrgId",
                "CREATE UNIQUE INDEX [UX_TenantFinanceConfigs_OrgId] ON [finance].[TenantFinanceConfigs] ([OrganizationId])");

            migrationBuilder.Sql("""
                IF OBJECT_ID(N'[finance].[JournalEntries]', N'U') IS NOT NULL
                   AND COL_LENGTH(N'[finance].[JournalEntries]', N'ReversesEntryId') IS NOT NULL
                   AND OBJECT_ID(N'[finance].[FK_JournalEntries_JournalEntries_ReversesEntryId]', N'F') IS NULL
                    ALTER TABLE [finance].[JournalEntries] ADD CONSTRAINT [FK_JournalEntries_JournalEntries_ReversesEntryId]
                        FOREIGN KEY ([ReversesEntryId]) REFERENCES [finance].[JournalEntries] ([Id]) ON DELETE NO ACTION;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(name: "FK_JournalEntries_JournalEntries_ReversesEntryId", schema: "finance", table: "JournalEntries");
            migrationBuilder.DropTable(name: "FinancialPostingTargets", schema: "finance");
            migrationBuilder.DropTable(name: "TenantFinanceConfigs", schema: "finance");
            migrationBuilder.DropTable(name: "FinancialPostings", schema: "finance");
            migrationBuilder.DropIndex(name: "IX_JournalEntries_Org_Key", schema: "finance", table: "JournalEntries");
            migrationBuilder.DropIndex(name: "IX_JournalEntries_Org_Source", schema: "finance", table: "JournalEntries");
            migrationBuilder.DropIndex(name: "IX_JournalEntries_Reverses", schema: "finance", table: "JournalEntries");
            migrationBuilder.DropColumn(name: "IdempotencyKey", schema: "finance", table: "JournalEntries");
            migrationBuilder.DropColumn(name: "ReversesEntryId", schema: "finance", table: "JournalEntries");
            migrationBuilder.DropColumn(name: "SourceDocumentUuid", schema: "finance", table: "JournalEntries");
            migrationBuilder.DropColumn(name: "SourceModule", schema: "finance", table: "JournalEntries");
        }
    }
}

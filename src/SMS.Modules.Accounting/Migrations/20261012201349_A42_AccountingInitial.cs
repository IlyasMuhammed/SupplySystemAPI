using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Accounting.Migrations
{
    /// <summary>
    /// A42 (D-2) - the GL setup tables in schema <c>finance</c>: CoaTemplates, CoaTemplateItems, ChartOfAccounts, FiscalYears,
    /// FiscalPeriods, AccountingDefaults, CoaSetupStatuses, OpeningBalanceEntries, ExternalSystemMappings, JournalEntries,
    /// JournalEntryLines and GlSetupHistory, with their CHECK constraints and indexes.
    /// <para>
    /// <b>Guarded and idempotent</b> (every API start migrates the shared, drifted database): the schema, each table and each
    /// index is created only when missing, so a partially applied or hand-created schema does not stop the API start.
    /// Tables are created parents first (FKs are declared inside CREATE TABLE). Additive only.
    /// </para>
    /// </summary>
    public partial class A42_AccountingInitial : Migration
    {
        private static void Table(MigrationBuilder mb, string name, string body) =>
            mb.Sql($"IF OBJECT_ID(N'[finance].[{name}]', N'U') IS NULL\nBEGIN\n    CREATE TABLE [finance].[{name}] (\n{body}\n    );\nEND;");

        private static void Index(MigrationBuilder mb, string table, string name, string ddl) =>
            mb.Sql($"IF OBJECT_ID(N'[finance].[{table}]', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{name}' AND object_id = OBJECT_ID(N'[finance].[{table}]'))\n    {ddl};");

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF SCHEMA_ID(N'finance') IS NULL EXEC(N'CREATE SCHEMA [finance];');");

            Table(migrationBuilder, "CoaTemplates", """
                [Id] int NOT NULL IDENTITY,
                [Uuid] uniqueidentifier NOT NULL,
                [TemplateCode] nvarchar(50) NOT NULL,
                [TemplateName] nvarchar(200) NOT NULL,
                [CountryCode] nvarchar(3) NULL,
                [BusinessType] nvarchar(100) NULL,
                [CurrencyCode] nvarchar(3) NULL,
                [Description] nvarchar(1000) NULL,
                [AccountCount] int NOT NULL,
                [IsActive] bit NOT NULL,
                [Version] nvarchar(20) NOT NULL,
                [SortOrder] int NOT NULL,
                [OrganizationId] uniqueidentifier NULL,
                [IsSystem] bit NOT NULL,
                [CreatedAt] datetime2 NOT NULL,
                [CreatedBy] int NULL,
                [ModifiedAt] datetime2 NOT NULL,
                [ModifiedBy] int NULL,
                CONSTRAINT [PK_CoaTemplates] PRIMARY KEY ([Id])
                """);

            Table(migrationBuilder, "FiscalYears", """
                [Id] int NOT NULL IDENTITY,
                [Uuid] uniqueidentifier NOT NULL,
                [OrganizationId] uniqueidentifier NOT NULL,
                [FiscalYearName] nvarchar(100) NOT NULL,
                [StartDate] date NOT NULL,
                [EndDate] date NOT NULL,
                [IsClosed] bit NOT NULL,
                [ClosedAt] datetime2 NULL,
                [ClosedBy] int NULL,
                [ReportingCurrencyCode] nvarchar(3) NOT NULL,
                [CreatedAt] datetime2 NOT NULL,
                [CreatedBy] int NULL,
                [ModifiedAt] datetime2 NOT NULL,
                [ModifiedBy] int NULL,
                CONSTRAINT [PK_FiscalYears] PRIMARY KEY ([Id]),
                CONSTRAINT [CK_FiscalYears_Dates] CHECK ([EndDate] > [StartDate])
                """);

            Table(migrationBuilder, "GlSetupHistory", """
                [Id] bigint NOT NULL IDENTITY,
                [OrganizationId] uniqueidentifier NOT NULL,
                [EventType] nvarchar(60) NOT NULL,
                [Summary] nvarchar(500) NOT NULL,
                [PayloadJson] nvarchar(max) NULL,
                [UserId] int NULL,
                [UserName] nvarchar(200) NULL,
                [At] datetime2 NOT NULL,
                CONSTRAINT [PK_GlSetupHistory] PRIMARY KEY ([Id])
                """);

            Table(migrationBuilder, "JournalEntries", """
                [Id] int NOT NULL IDENTITY,
                [Uuid] uniqueidentifier NOT NULL,
                [OrganizationId] uniqueidentifier NOT NULL,
                [EntryNumber] nvarchar(30) NOT NULL,
                [EntryType] nvarchar(30) NOT NULL,
                [EntryDate] date NOT NULL,
                [Reference] nvarchar(200) NULL,
                [Description] nvarchar(500) NULL,
                [Status] nvarchar(20) NOT NULL,
                [TotalDebit] decimal(18,4) NOT NULL,
                [TotalCredit] decimal(18,4) NOT NULL,
                [CurrencyCode] nvarchar(3) NULL,
                [PostedAt] datetime2 NULL,
                [PostedBy] int NULL,
                [CreatedAt] datetime2 NOT NULL,
                [CreatedBy] int NULL,
                [ModifiedAt] datetime2 NOT NULL,
                CONSTRAINT [PK_JournalEntries] PRIMARY KEY ([Id]),
                CONSTRAINT [CK_JournalEntries_Status] CHECK ([Status] IN ('Draft', 'Posted')),
                CONSTRAINT [CK_JournalEntries_Totals] CHECK ([TotalDebit] >= 0 AND [TotalCredit] >= 0)
                """);

            Table(migrationBuilder, "ChartOfAccounts", """
                [Id] int NOT NULL IDENTITY,
                [Uuid] uniqueidentifier NOT NULL,
                [OrganizationId] uniqueidentifier NOT NULL,
                [AccountCode] nvarchar(20) NOT NULL,
                [AccountName] nvarchar(200) NOT NULL,
                [ParentAccountId] int NULL,
                [MaterializedPath] nvarchar(500) NOT NULL,
                [Level] tinyint NOT NULL,
                [RootType] nvarchar(20) NOT NULL,
                [IsGroup] bit NOT NULL,
                [AccountType] nvarchar(50) NULL,
                [Description] nvarchar(500) NULL,
                [ExternalCode] nvarchar(50) NULL,
                [IsSystemAccount] bit NOT NULL,
                [IsActive] bit NOT NULL,
                [HasTransactions] bit NOT NULL,
                [OpeningBalanceDebit] decimal(18,4) NOT NULL,
                [OpeningBalanceCredit] decimal(18,4) NOT NULL,
                [CurrentBalance] decimal(18,4) NOT NULL,
                [SortOrder] int NOT NULL,
                [SourceTemplateId] int NULL,
                [CreatedAt] datetime2 NOT NULL,
                [CreatedBy] int NULL,
                [ModifiedAt] datetime2 NOT NULL,
                [ModifiedBy] int NULL,
                CONSTRAINT [PK_ChartOfAccounts] PRIMARY KEY ([Id]),
                CONSTRAINT [CK_ChartOfAccounts_AccountType] CHECK ([AccountType] IS NULL OR [AccountType] IN ('Cash', 'Bank', 'Receivable', 'OtherCurrentAsset', 'FixedAsset', 'AccumulatedDepreciation', 'Payable', 'OtherCurrentLiability', 'LongTermLiability', 'Equity', 'Revenue', 'OtherIncome', 'CostOfGoodsSold', 'Expense', 'OtherExpense', 'Tax', 'Depreciation')),
                CONSTRAINT [CK_ChartOfAccounts_IsGroup] CHECK (([Level] IN (1, 2, 3) AND [IsGroup] = 1) OR ([Level] = 4 AND [IsGroup] = 0)),
                CONSTRAINT [CK_ChartOfAccounts_Level] CHECK ([Level] BETWEEN 1 AND 4),
                CONSTRAINT [CK_ChartOfAccounts_OpeningNotNegative] CHECK ([OpeningBalanceDebit] >= 0 AND [OpeningBalanceCredit] >= 0),
                CONSTRAINT [CK_ChartOfAccounts_OpeningOneSide] CHECK (NOT ([OpeningBalanceDebit] > 0 AND [OpeningBalanceCredit] > 0)),
                CONSTRAINT [CK_ChartOfAccounts_Parent] CHECK (([Level] = 1 AND [ParentAccountId] IS NULL) OR ([Level] > 1 AND [ParentAccountId] IS NOT NULL)),
                CONSTRAINT [CK_ChartOfAccounts_Path] CHECK ([MaterializedPath] LIKE '/%'),
                CONSTRAINT [CK_ChartOfAccounts_RootType] CHECK ([RootType] IN ('Asset', 'Liability', 'Equity', 'Revenue', 'Expense')),
                CONSTRAINT [FK_ChartOfAccounts_ChartOfAccounts_ParentAccountId] FOREIGN KEY ([ParentAccountId]) REFERENCES [finance].[ChartOfAccounts] ([Id]) ON DELETE NO ACTION,
                CONSTRAINT [FK_ChartOfAccounts_CoaTemplates_SourceTemplateId] FOREIGN KEY ([SourceTemplateId]) REFERENCES [finance].[CoaTemplates] ([Id]) ON DELETE NO ACTION
                """);

            Table(migrationBuilder, "CoaSetupStatuses", """
                [Id] int NOT NULL IDENTITY,
                [OrganizationId] uniqueidentifier NOT NULL,
                [Status] nvarchar(30) NOT NULL,
                [TemplateAppliedAt] datetime2 NULL,
                [TemplateId] int NULL,
                [AccountsCreatedCount] int NOT NULL,
                [FiscalYearConfigured] bit NOT NULL,
                [DefaultsConfigured] bit NOT NULL,
                [OpeningBalancesEntered] bit NOT NULL,
                [OpeningBalancesBalanced] bit NOT NULL,
                [OpeningBalancesPostedAt] datetime2 NULL,
                [SetupCompletedAt] datetime2 NULL,
                [SetupCompletedBy] int NULL,
                [SetupCompletedByName] nvarchar(200) NULL,
                [ConversionDate] date NULL,
                [CreatedAt] datetime2 NOT NULL,
                [ModifiedAt] datetime2 NOT NULL,
                CONSTRAINT [PK_CoaSetupStatuses] PRIMARY KEY ([Id]),
                CONSTRAINT [CK_CoaSetupStatuses_Status] CHECK ([Status] IN ('NotStarted', 'AccountsCreated', 'FiscalYearSet', 'DefaultsSet', 'BalancesEntered', 'Complete')),
                CONSTRAINT [FK_CoaSetupStatuses_CoaTemplates_TemplateId] FOREIGN KEY ([TemplateId]) REFERENCES [finance].[CoaTemplates] ([Id]) ON DELETE NO ACTION
                """);

            Table(migrationBuilder, "CoaTemplateItems", """
                [Id] int NOT NULL IDENTITY,
                [TemplateId] int NOT NULL,
                [AccountCode] nvarchar(20) NOT NULL,
                [AccountName] nvarchar(200) NOT NULL,
                [ParentItemId] int NULL,
                [Level] tinyint NOT NULL,
                [RootType] nvarchar(20) NOT NULL,
                [IsGroup] bit NOT NULL,
                [AccountType] nvarchar(50) NULL,
                [Description] nvarchar(500) NULL,
                [SortOrder] int NOT NULL,
                [IsDefaultAccount] bit NOT NULL,
                [DefaultAccountKey] nvarchar(50) NULL,
                CONSTRAINT [PK_CoaTemplateItems] PRIMARY KEY ([Id]),
                CONSTRAINT [CK_CoaTemplateItems_AccountType] CHECK ([AccountType] IS NULL OR [AccountType] IN ('Cash', 'Bank', 'Receivable', 'OtherCurrentAsset', 'FixedAsset', 'AccumulatedDepreciation', 'Payable', 'OtherCurrentLiability', 'LongTermLiability', 'Equity', 'Revenue', 'OtherIncome', 'CostOfGoodsSold', 'Expense', 'OtherExpense', 'Tax', 'Depreciation')),
                CONSTRAINT [CK_CoaTemplateItems_DefaultKey] CHECK (([IsDefaultAccount] = 0 AND [DefaultAccountKey] IS NULL) OR ([IsDefaultAccount] = 1 AND [DefaultAccountKey] IN ('DEFAULT_RECEIVABLE', 'DEFAULT_PAYABLE', 'DEFAULT_CASH', 'DEFAULT_BANK', 'DEFAULT_REVENUE', 'DEFAULT_DISCOUNT_GIVEN', 'DEFAULT_EXCHANGE_GAIN_LOSS', 'DEFAULT_ROUNDING', 'DEFAULT_COGS', 'DEFAULT_PURCHASE', 'DEFAULT_DISCOUNT_RECEIVED', 'DEFAULT_INVENTORY', 'DEFAULT_TAX_RECEIVABLE', 'DEFAULT_TAX_PAYABLE', 'DEFAULT_RETAINED_EARNINGS', 'DEFAULT_OPENING_BALANCE'))),
                CONSTRAINT [CK_CoaTemplateItems_IsGroup] CHECK (([Level] IN (1, 2, 3) AND [IsGroup] = 1) OR ([Level] = 4 AND [IsGroup] = 0)),
                CONSTRAINT [CK_CoaTemplateItems_Level] CHECK ([Level] BETWEEN 1 AND 4),
                CONSTRAINT [CK_CoaTemplateItems_Parent] CHECK (([Level] = 1 AND [ParentItemId] IS NULL) OR ([Level] > 1 AND [ParentItemId] IS NOT NULL)),
                CONSTRAINT [CK_CoaTemplateItems_RootType] CHECK ([RootType] IN ('Asset', 'Liability', 'Equity', 'Revenue', 'Expense')),
                CONSTRAINT [FK_CoaTemplateItems_CoaTemplateItems_ParentItemId] FOREIGN KEY ([ParentItemId]) REFERENCES [finance].[CoaTemplateItems] ([Id]) ON DELETE NO ACTION,
                CONSTRAINT [FK_CoaTemplateItems_CoaTemplates_TemplateId] FOREIGN KEY ([TemplateId]) REFERENCES [finance].[CoaTemplates] ([Id]) ON DELETE CASCADE
                """);

            Table(migrationBuilder, "FiscalPeriods", """
                [Id] int NOT NULL IDENTITY,
                [Uuid] uniqueidentifier NOT NULL,
                [OrganizationId] uniqueidentifier NOT NULL,
                [FiscalYearId] int NOT NULL,
                [PeriodName] nvarchar(100) NOT NULL,
                [PeriodNumber] tinyint NOT NULL,
                [StartDate] date NOT NULL,
                [EndDate] date NOT NULL,
                [Status] nvarchar(20) NOT NULL,
                [CreatedAt] datetime2 NOT NULL,
                [ModifiedAt] datetime2 NOT NULL,
                [ModifiedBy] int NULL,
                CONSTRAINT [PK_FiscalPeriods] PRIMARY KEY ([Id]),
                CONSTRAINT [CK_FiscalPeriods_Dates] CHECK ([EndDate] >= [StartDate]),
                CONSTRAINT [CK_FiscalPeriods_Number] CHECK ([PeriodNumber] BETWEEN 1 AND 13),
                CONSTRAINT [CK_FiscalPeriods_Status] CHECK ([Status] IN ('Open', 'Locked', 'Closed')),
                CONSTRAINT [FK_FiscalPeriods_FiscalYears_FiscalYearId] FOREIGN KEY ([FiscalYearId]) REFERENCES [finance].[FiscalYears] ([Id]) ON DELETE CASCADE
                """);

            Table(migrationBuilder, "AccountingDefaults", """
                [Id] int NOT NULL IDENTITY,
                [Uuid] uniqueidentifier NOT NULL,
                [OrganizationId] uniqueidentifier NOT NULL,
                [DefaultKey] nvarchar(50) NOT NULL,
                [DefaultLabel] nvarchar(200) NOT NULL,
                [AccountId] int NULL,
                [IsRequired] bit NOT NULL,
                [Category] nvarchar(50) NOT NULL,
                [ExpectedAccountType] nvarchar(50) NULL,
                [HelpText] nvarchar(500) NULL,
                [SortOrder] int NOT NULL,
                [CreatedAt] datetime2 NOT NULL,
                [ModifiedAt] datetime2 NOT NULL,
                [ModifiedBy] int NULL,
                CONSTRAINT [PK_AccountingDefaults] PRIMARY KEY ([Id]),
                CONSTRAINT [CK_AccountingDefaults_Category] CHECK ([Category] IN ('Receivables', 'Payables', 'Banking', 'Revenue', 'CostOfSales', 'Inventory', 'Tax', 'Equity')),
                CONSTRAINT [CK_AccountingDefaults_Key] CHECK ([DefaultKey] IN ('DEFAULT_RECEIVABLE', 'DEFAULT_PAYABLE', 'DEFAULT_CASH', 'DEFAULT_BANK', 'DEFAULT_REVENUE', 'DEFAULT_DISCOUNT_GIVEN', 'DEFAULT_EXCHANGE_GAIN_LOSS', 'DEFAULT_ROUNDING', 'DEFAULT_COGS', 'DEFAULT_PURCHASE', 'DEFAULT_DISCOUNT_RECEIVED', 'DEFAULT_INVENTORY', 'DEFAULT_TAX_RECEIVABLE', 'DEFAULT_TAX_PAYABLE', 'DEFAULT_RETAINED_EARNINGS', 'DEFAULT_OPENING_BALANCE')),
                CONSTRAINT [FK_AccountingDefaults_ChartOfAccounts_AccountId] FOREIGN KEY ([AccountId]) REFERENCES [finance].[ChartOfAccounts] ([Id]) ON DELETE NO ACTION
                """);

            Table(migrationBuilder, "ExternalSystemMappings", """
                [Id] int NOT NULL IDENTITY,
                [Uuid] uniqueidentifier NOT NULL,
                [OrganizationId] uniqueidentifier NOT NULL,
                [SystemType] nvarchar(50) NOT NULL,
                [SystemName] nvarchar(200) NOT NULL,
                [AccountId] int NOT NULL,
                [ExternalAccountCode] nvarchar(100) NOT NULL,
                [ExternalAccountName] nvarchar(200) NULL,
                [SyncDirection] nvarchar(20) NOT NULL,
                [IsActive] bit NOT NULL,
                [LastSyncedAt] datetime2 NULL,
                [SyncStatus] nvarchar(30) NULL,
                [SyncErrorMessage] nvarchar(1000) NULL,
                [ConnectionConfigJson] nvarchar(max) NULL,
                [CreatedAt] datetime2 NOT NULL,
                [CreatedBy] int NULL,
                [ModifiedAt] datetime2 NOT NULL,
                [ModifiedBy] int NULL,
                CONSTRAINT [PK_ExternalSystemMappings] PRIMARY KEY ([Id]),
                CONSTRAINT [CK_ExternalSystemMappings_Direction] CHECK ([SyncDirection] IN ('None', 'Inbound', 'Outbound', 'Bidirectional')),
                CONSTRAINT [CK_ExternalSystemMappings_SyncStatus] CHECK ([SyncStatus] IS NULL OR [SyncStatus] IN ('Success', 'Error', 'Pending')),
                CONSTRAINT [CK_ExternalSystemMappings_SystemType] CHECK ([SystemType] IN ('QUICKBOOKS_ONLINE', 'QUICKBOOKS_DESKTOP', 'XERO', 'ERPNEXT', 'TALLY', 'SAGE', 'SAP_B1', 'DYNAMICS_BC', 'ZOHO_BOOKS', 'WAVE', 'ODOO', 'CUSTOM')),
                CONSTRAINT [FK_ExternalSystemMappings_ChartOfAccounts_AccountId] FOREIGN KEY ([AccountId]) REFERENCES [finance].[ChartOfAccounts] ([Id]) ON DELETE NO ACTION
                """);

            Table(migrationBuilder, "JournalEntryLines", """
                [Id] int NOT NULL IDENTITY,
                [OrganizationId] uniqueidentifier NOT NULL,
                [JournalEntryId] int NOT NULL,
                [LineNumber] int NOT NULL,
                [AccountId] int NOT NULL,
                [DebitAmount] decimal(18,4) NOT NULL,
                [CreditAmount] decimal(18,4) NOT NULL,
                [Memo] nvarchar(500) NULL,
                CONSTRAINT [PK_JournalEntryLines] PRIMARY KEY ([Id]),
                CONSTRAINT [CK_JournalEntryLines_NotNegative] CHECK ([DebitAmount] >= 0 AND [CreditAmount] >= 0),
                CONSTRAINT [CK_JournalEntryLines_OneSide] CHECK (NOT ([DebitAmount] > 0 AND [CreditAmount] > 0)),
                CONSTRAINT [FK_JournalEntryLines_ChartOfAccounts_AccountId] FOREIGN KEY ([AccountId]) REFERENCES [finance].[ChartOfAccounts] ([Id]) ON DELETE NO ACTION,
                CONSTRAINT [FK_JournalEntryLines_JournalEntries_JournalEntryId] FOREIGN KEY ([JournalEntryId]) REFERENCES [finance].[JournalEntries] ([Id]) ON DELETE CASCADE
                """);

            Table(migrationBuilder, "OpeningBalanceEntries", """
                [Id] int NOT NULL IDENTITY,
                [Uuid] uniqueidentifier NOT NULL,
                [OrganizationId] uniqueidentifier NOT NULL,
                [AccountId] int NOT NULL,
                [DebitAmount] decimal(18,4) NOT NULL,
                [CreditAmount] decimal(18,4) NOT NULL,
                [Notes] nvarchar(500) NULL,
                [ConversionDate] date NULL,
                [EntryStatus] nvarchar(20) NOT NULL,
                [PostedAt] datetime2 NULL,
                [PostedBy] int NULL,
                [JournalEntryId] int NULL,
                [CreatedAt] datetime2 NOT NULL,
                [CreatedBy] int NULL,
                [ModifiedAt] datetime2 NOT NULL,
                [ModifiedBy] int NULL,
                CONSTRAINT [PK_OpeningBalanceEntries] PRIMARY KEY ([Id]),
                CONSTRAINT [CK_OpeningBalanceEntries_NotNegative] CHECK ([DebitAmount] >= 0 AND [CreditAmount] >= 0),
                CONSTRAINT [CK_OpeningBalanceEntries_OneSide] CHECK (NOT ([DebitAmount] > 0 AND [CreditAmount] > 0)),
                CONSTRAINT [CK_OpeningBalanceEntries_Status] CHECK ([EntryStatus] IN ('Draft', 'Posted')),
                CONSTRAINT [FK_OpeningBalanceEntries_ChartOfAccounts_AccountId] FOREIGN KEY ([AccountId]) REFERENCES [finance].[ChartOfAccounts] ([Id]) ON DELETE NO ACTION,
                CONSTRAINT [FK_OpeningBalanceEntries_JournalEntries_JournalEntryId] FOREIGN KEY ([JournalEntryId]) REFERENCES [finance].[JournalEntries] ([Id]) ON DELETE NO ACTION
                """);

            Index(migrationBuilder, "AccountingDefaults", "IX_AccountingDefaults_AccountId",
                "CREATE INDEX [IX_AccountingDefaults_AccountId] ON [finance].[AccountingDefaults] ([AccountId])");
            Index(migrationBuilder, "AccountingDefaults", "IX_AccountingDefaults_Uuid",
                "CREATE UNIQUE INDEX [IX_AccountingDefaults_Uuid] ON [finance].[AccountingDefaults] ([Uuid])");
            Index(migrationBuilder, "AccountingDefaults", "UX_AccountingDefaults_Key_Org",
                "CREATE UNIQUE INDEX [UX_AccountingDefaults_Key_Org] ON [finance].[AccountingDefaults] ([OrganizationId], [DefaultKey])");
            Index(migrationBuilder, "ChartOfAccounts", "IX_ChartOfAccounts_AccountType",
                "CREATE INDEX [IX_ChartOfAccounts_AccountType] ON [finance].[ChartOfAccounts] ([OrganizationId], [AccountType]) WHERE [AccountType] IS NOT NULL");
            Index(migrationBuilder, "ChartOfAccounts", "IX_ChartOfAccounts_Active_Leaf",
                "CREATE INDEX [IX_ChartOfAccounts_Active_Leaf] ON [finance].[ChartOfAccounts] ([OrganizationId], [IsActive], [IsGroup]) WHERE [IsActive] = 1 AND [IsGroup] = 0");
            Index(migrationBuilder, "ChartOfAccounts", "IX_ChartOfAccounts_Level",
                "CREATE INDEX [IX_ChartOfAccounts_Level] ON [finance].[ChartOfAccounts] ([OrganizationId], [Level])");
            Index(migrationBuilder, "ChartOfAccounts", "IX_ChartOfAccounts_Parent",
                "CREATE INDEX [IX_ChartOfAccounts_Parent] ON [finance].[ChartOfAccounts] ([ParentAccountId])");
            Index(migrationBuilder, "ChartOfAccounts", "IX_ChartOfAccounts_Path",
                "CREATE INDEX [IX_ChartOfAccounts_Path] ON [finance].[ChartOfAccounts] ([OrganizationId], [MaterializedPath])");
            Index(migrationBuilder, "ChartOfAccounts", "IX_ChartOfAccounts_RootType",
                "CREATE INDEX [IX_ChartOfAccounts_RootType] ON [finance].[ChartOfAccounts] ([OrganizationId], [RootType])");
            Index(migrationBuilder, "ChartOfAccounts", "IX_ChartOfAccounts_SourceTemplateId",
                "CREATE INDEX [IX_ChartOfAccounts_SourceTemplateId] ON [finance].[ChartOfAccounts] ([SourceTemplateId])");
            Index(migrationBuilder, "ChartOfAccounts", "IX_ChartOfAccounts_Uuid",
                "CREATE UNIQUE INDEX [IX_ChartOfAccounts_Uuid] ON [finance].[ChartOfAccounts] ([Uuid])");
            Index(migrationBuilder, "ChartOfAccounts", "UX_ChartOfAccounts_Code_Org",
                "CREATE UNIQUE INDEX [UX_ChartOfAccounts_Code_Org] ON [finance].[ChartOfAccounts] ([OrganizationId], [AccountCode]) WHERE [IsActive] = 1");
            Index(migrationBuilder, "CoaSetupStatuses", "IX_CoaSetupStatuses_TemplateId",
                "CREATE INDEX [IX_CoaSetupStatuses_TemplateId] ON [finance].[CoaSetupStatuses] ([TemplateId])");
            Index(migrationBuilder, "CoaSetupStatuses", "UX_CoaSetupStatuses_OrgId",
                "CREATE UNIQUE INDEX [UX_CoaSetupStatuses_OrgId] ON [finance].[CoaSetupStatuses] ([OrganizationId])");
            Index(migrationBuilder, "CoaTemplateItems", "IX_CoaTemplateItems_Level",
                "CREATE INDEX [IX_CoaTemplateItems_Level] ON [finance].[CoaTemplateItems] ([TemplateId], [Level])");
            Index(migrationBuilder, "CoaTemplateItems", "IX_CoaTemplateItems_Parent",
                "CREATE INDEX [IX_CoaTemplateItems_Parent] ON [finance].[CoaTemplateItems] ([ParentItemId])");
            Index(migrationBuilder, "CoaTemplateItems", "UX_CoaTemplateItems_Template_Code",
                "CREATE UNIQUE INDEX [UX_CoaTemplateItems_Template_Code] ON [finance].[CoaTemplateItems] ([TemplateId], [AccountCode])");
            Index(migrationBuilder, "CoaTemplates", "IX_CoaTemplates_BusinessType",
                "CREATE INDEX [IX_CoaTemplates_BusinessType] ON [finance].[CoaTemplates] ([BusinessType])");
            Index(migrationBuilder, "CoaTemplates", "IX_CoaTemplates_Country",
                "CREATE INDEX [IX_CoaTemplates_Country] ON [finance].[CoaTemplates] ([CountryCode])");
            Index(migrationBuilder, "CoaTemplates", "IX_CoaTemplates_OrganizationId",
                "CREATE INDEX [IX_CoaTemplates_OrganizationId] ON [finance].[CoaTemplates] ([OrganizationId])");
            Index(migrationBuilder, "CoaTemplates", "IX_CoaTemplates_Uuid",
                "CREATE UNIQUE INDEX [IX_CoaTemplates_Uuid] ON [finance].[CoaTemplates] ([Uuid])");
            Index(migrationBuilder, "CoaTemplates", "UX_CoaTemplates_Code_Org",
                "CREATE UNIQUE INDEX [UX_CoaTemplates_Code_Org] ON [finance].[CoaTemplates] ([TemplateCode], [OrganizationId]) WHERE [IsActive] = 1");
            Index(migrationBuilder, "ExternalSystemMappings", "IX_ExternalSystemMappings_Uuid",
                "CREATE UNIQUE INDEX [IX_ExternalSystemMappings_Uuid] ON [finance].[ExternalSystemMappings] ([Uuid])");
            Index(migrationBuilder, "ExternalSystemMappings", "IX_ExtSysMappings_AccountId",
                "CREATE INDEX [IX_ExtSysMappings_AccountId] ON [finance].[ExternalSystemMappings] ([AccountId])");
            Index(migrationBuilder, "ExternalSystemMappings", "IX_ExtSysMappings_SyncDirection",
                "CREATE INDEX [IX_ExtSysMappings_SyncDirection] ON [finance].[ExternalSystemMappings] ([OrganizationId], [SystemType], [SyncDirection]) WHERE [IsActive] = 1");
            Index(migrationBuilder, "ExternalSystemMappings", "UX_ExtSysMappings_Sys_Account_Org",
                "CREATE UNIQUE INDEX [UX_ExtSysMappings_Sys_Account_Org] ON [finance].[ExternalSystemMappings] ([OrganizationId], [SystemType], [AccountId])");
            Index(migrationBuilder, "FiscalPeriods", "IX_FiscalPeriods_Org_Dates",
                "CREATE INDEX [IX_FiscalPeriods_Org_Dates] ON [finance].[FiscalPeriods] ([OrganizationId], [StartDate], [EndDate])");
            Index(migrationBuilder, "FiscalPeriods", "IX_FiscalPeriods_Org_Status",
                "CREATE INDEX [IX_FiscalPeriods_Org_Status] ON [finance].[FiscalPeriods] ([OrganizationId], [Status])");
            Index(migrationBuilder, "FiscalPeriods", "IX_FiscalPeriods_Uuid",
                "CREATE UNIQUE INDEX [IX_FiscalPeriods_Uuid] ON [finance].[FiscalPeriods] ([Uuid])");
            Index(migrationBuilder, "FiscalPeriods", "UX_FiscalPeriods_Year_Number",
                "CREATE UNIQUE INDEX [UX_FiscalPeriods_Year_Number] ON [finance].[FiscalPeriods] ([FiscalYearId], [PeriodNumber], [OrganizationId])");
            Index(migrationBuilder, "FiscalYears", "IX_FiscalYears_Org_Dates",
                "CREATE INDEX [IX_FiscalYears_Org_Dates] ON [finance].[FiscalYears] ([OrganizationId], [StartDate], [EndDate])");
            Index(migrationBuilder, "FiscalYears", "IX_FiscalYears_Uuid",
                "CREATE UNIQUE INDEX [IX_FiscalYears_Uuid] ON [finance].[FiscalYears] ([Uuid])");
            Index(migrationBuilder, "FiscalYears", "UX_FiscalYears_Org_Name",
                "CREATE UNIQUE INDEX [UX_FiscalYears_Org_Name] ON [finance].[FiscalYears] ([OrganizationId], [FiscalYearName])");
            Index(migrationBuilder, "GlSetupHistory", "IX_GlSetupHistory_Org_At",
                "CREATE INDEX [IX_GlSetupHistory_Org_At] ON [finance].[GlSetupHistory] ([OrganizationId], [At])");
            Index(migrationBuilder, "JournalEntries", "IX_JournalEntries_Org_Date",
                "CREATE INDEX [IX_JournalEntries_Org_Date] ON [finance].[JournalEntries] ([OrganizationId], [EntryDate])");
            Index(migrationBuilder, "JournalEntries", "IX_JournalEntries_Org_Type_Status",
                "CREATE INDEX [IX_JournalEntries_Org_Type_Status] ON [finance].[JournalEntries] ([OrganizationId], [EntryType], [Status])");
            Index(migrationBuilder, "JournalEntries", "IX_JournalEntries_Uuid",
                "CREATE UNIQUE INDEX [IX_JournalEntries_Uuid] ON [finance].[JournalEntries] ([Uuid])");
            Index(migrationBuilder, "JournalEntries", "UX_JournalEntries_Org_Number",
                "CREATE UNIQUE INDEX [UX_JournalEntries_Org_Number] ON [finance].[JournalEntries] ([OrganizationId], [EntryNumber])");
            Index(migrationBuilder, "JournalEntryLines", "IX_JournalEntryLines_AccountId",
                "CREATE INDEX [IX_JournalEntryLines_AccountId] ON [finance].[JournalEntryLines] ([AccountId])");
            Index(migrationBuilder, "JournalEntryLines", "IX_JournalEntryLines_Org_Account",
                "CREATE INDEX [IX_JournalEntryLines_Org_Account] ON [finance].[JournalEntryLines] ([OrganizationId], [AccountId])");
            Index(migrationBuilder, "JournalEntryLines", "UX_JournalEntryLines_Entry_Line",
                "CREATE UNIQUE INDEX [UX_JournalEntryLines_Entry_Line] ON [finance].[JournalEntryLines] ([JournalEntryId], [LineNumber])");
            Index(migrationBuilder, "OpeningBalanceEntries", "IX_OpeningBalanceEntries_AccountId",
                "CREATE INDEX [IX_OpeningBalanceEntries_AccountId] ON [finance].[OpeningBalanceEntries] ([AccountId])");
            Index(migrationBuilder, "OpeningBalanceEntries", "IX_OpeningBalanceEntries_JournalEntryId",
                "CREATE INDEX [IX_OpeningBalanceEntries_JournalEntryId] ON [finance].[OpeningBalanceEntries] ([JournalEntryId])");
            Index(migrationBuilder, "OpeningBalanceEntries", "IX_OpeningBalanceEntries_Status",
                "CREATE INDEX [IX_OpeningBalanceEntries_Status] ON [finance].[OpeningBalanceEntries] ([OrganizationId], [EntryStatus])");
            Index(migrationBuilder, "OpeningBalanceEntries", "IX_OpeningBalanceEntries_Uuid",
                "CREATE UNIQUE INDEX [IX_OpeningBalanceEntries_Uuid] ON [finance].[OpeningBalanceEntries] ([Uuid])");
            Index(migrationBuilder, "OpeningBalanceEntries", "UX_OpeningBalanceEntries_Account_Org",
                "CREATE UNIQUE INDEX [UX_OpeningBalanceEntries_Account_Org] ON [finance].[OpeningBalanceEntries] ([OrganizationId], [AccountId])");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "AccountingDefaults", schema: "finance");
            migrationBuilder.DropTable(name: "CoaSetupStatuses", schema: "finance");
            migrationBuilder.DropTable(name: "CoaTemplateItems", schema: "finance");
            migrationBuilder.DropTable(name: "ExternalSystemMappings", schema: "finance");
            migrationBuilder.DropTable(name: "FiscalPeriods", schema: "finance");
            migrationBuilder.DropTable(name: "GlSetupHistory", schema: "finance");
            migrationBuilder.DropTable(name: "JournalEntryLines", schema: "finance");
            migrationBuilder.DropTable(name: "OpeningBalanceEntries", schema: "finance");
            migrationBuilder.DropTable(name: "FiscalYears", schema: "finance");
            migrationBuilder.DropTable(name: "ChartOfAccounts", schema: "finance");
            migrationBuilder.DropTable(name: "JournalEntries", schema: "finance");
            migrationBuilder.DropTable(name: "CoaTemplates", schema: "finance");
        }
    }
}

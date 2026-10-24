using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Accounting.Migrations
{
    /// <summary>
    /// A47 (docs/journal-engine/ADDENDUM-47-ANALYSIS.md D-2..D-7, D-16, D-21) - the whole journal-engine schema, in the
    /// spec's order M1 → M8 → M2 → M6 → M3 → M4 → M5 (M7, the HasTransactions trigger, is dropped: D-10):
    /// <list type="bullet">
    /// <item>M1 <c>JournalTypes</c>; M8 the 7 system types for every organization whose A42 setup is Complete <b>or</b> that
    /// already has journals (D-CORE-1: an A42 opening journal can exist before Complete, and every journal needs a type);</item>
    /// <item>M2 <c>AdjustmentOnly</c> in CK_FiscalPeriods_Status;</item>
    /// <item>M6 <c>ReconcileGroups</c>;</item>
    /// <item>M3 the new <c>JournalEntries</c> columns (ALTER, A-4) with their backfill: journal type by D-12, fiscal year /
    /// period by date, opening flag, line count, <c>Reversed</c> + ReversedByEntryId for the A44 reversal pairs (D-4); then
    /// JournalTypeId NOT NULL, FKs, indexes (incl. the D-5 filtered unique keys) and CHECKs;</item>
    /// <item>M4 the new <c>JournalEntryLines</c> columns (D-3) with AmountResidual = Debit − Credit, the line currency =
    /// the header's, CreatedAt = the header's; FKs, indexes, CHECKs;</item>
    /// <item>M5 <c>JournalEntrySequences</c>.</item>
    /// </list>
    /// <para>
    /// <b>Guarded and idempotent</b> like A42/A44 (every API start migrates the shared, drifted database): each table,
    /// column, index and constraint is created only when missing; backfills only touch rows still unset; a CHECK is added
    /// WITH NOCHECK (still enforced on new writes) when drifted rows would violate it. No data is moved or renamed.
    /// </para>
    /// </summary>
    public partial class A47_JournalEngine : Migration
    {
        private static void Table(MigrationBuilder mb, string name, string body) =>
            mb.Sql($"IF OBJECT_ID(N'[finance].[{name}]', N'U') IS NULL\nBEGIN\n    CREATE TABLE [finance].[{name}] (\n{body}\n    );\nEND;");

        private static void Index(MigrationBuilder mb, string table, string name, string ddl) =>
            mb.Sql($"IF OBJECT_ID(N'[finance].[{table}]', N'U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{name}' AND object_id = OBJECT_ID(N'[finance].[{table}]'))\n    {ddl};");

        private static void Column(MigrationBuilder mb, string table, string column, string type) =>
            mb.Sql($"IF OBJECT_ID(N'[finance].[{table}]', N'U') IS NOT NULL AND COL_LENGTH(N'[finance].[{table}]', N'{column}') IS NULL\n    ALTER TABLE [finance].[{table}] ADD [{column}] {type};");

        /// <summary>A NOT NULL column with a named default; <paramref name="backfill"/> (dynamic SQL) runs once, right after it is added.</summary>
        private static void NotNullColumn(MigrationBuilder mb, string table, string column, string type, string defaultSql, string backfill = null) =>
            mb.Sql($"""
                IF OBJECT_ID(N'[finance].[{table}]', N'U') IS NOT NULL AND COL_LENGTH(N'[finance].[{table}]', N'{column}') IS NULL
                BEGIN
                    ALTER TABLE [finance].[{table}] ADD [{column}] {type} NOT NULL CONSTRAINT [DF_{table}_{column}] DEFAULT {defaultSql};
                    {(backfill is null ? "" : $"EXEC(N'{backfill.Replace("'", "''")}');")}
                END;
                """);

        private static void ForeignKey(MigrationBuilder mb, string table, string name, string ddl) =>
            mb.Sql($"IF OBJECT_ID(N'[finance].[{table}]', N'U') IS NOT NULL AND OBJECT_ID(N'[finance].[{name}]', N'F') IS NULL\n    ALTER TABLE [finance].[{table}] ADD CONSTRAINT [{name}] {ddl};");

        /// <summary>A CHECK constraint, WITH NOCHECK when existing (drifted) rows would violate it.</summary>
        private static void Check(MigrationBuilder mb, string table, string name, string expression) =>
            mb.Sql($"""
                IF OBJECT_ID(N'[finance].[{table}]', N'U') IS NOT NULL AND OBJECT_ID(N'[finance].[{name}]', N'C') IS NULL
                BEGIN
                    IF EXISTS (SELECT 1 FROM [finance].[{table}] WHERE NOT ({expression}))
                        EXEC(N'ALTER TABLE [finance].[{table}] WITH NOCHECK ADD CONSTRAINT [{name}] CHECK ({expression.Replace("'", "''")})');
                    ELSE
                        EXEC(N'ALTER TABLE [finance].[{table}] ADD CONSTRAINT [{name}] CHECK ({expression.Replace("'", "''")})');
                END;
                """);

        /// <summary>Replaces a CHECK whose definition does not contain <paramref name="marker"/> yet (a status list grew).</summary>
        private static void ReplaceCheck(MigrationBuilder mb, string table, string name, string expression, string marker)
        {
            mb.Sql($"""
                IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'{name}' AND parent_object_id = OBJECT_ID(N'[finance].[{table}]')
                           AND definition NOT LIKE N'%{marker}%')
                    ALTER TABLE [finance].[{table}] DROP CONSTRAINT [{name}];
                """);
            Check(mb, table, name, expression);
        }

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var mb = migrationBuilder;
            mb.Sql("IF SCHEMA_ID(N'finance') IS NULL EXEC(N'CREATE SCHEMA [finance];');");

            // ── M1 JournalTypes (§4.2) ───────────────────────────────────────────────────────────────────────────────
            Table(mb, "JournalTypes", """
                [Id] int NOT NULL IDENTITY,
                [Uuid] uniqueidentifier NOT NULL,
                [OrganizationId] uniqueidentifier NOT NULL,
                [Code] nvarchar(10) NOT NULL,
                [Name] nvarchar(100) NOT NULL,
                [Type] nvarchar(20) NOT NULL,
                [SequencePrefix] nvarchar(10) NOT NULL,
                [DefaultDebitAccountId] int NULL,
                [DefaultCreditAccountId] int NULL,
                [RequiresPartner] bit NOT NULL,
                [AutoPost] bit NOT NULL,
                [IsSystem] bit NOT NULL,
                [IsActive] bit NOT NULL,
                [SortOrder] int NOT NULL,
                [CreatedAt] datetime2 NOT NULL,
                [CreatedBy] int NULL,
                [ModifiedAt] datetime2 NOT NULL,
                [ModifiedBy] int NULL,
                CONSTRAINT [PK_JournalTypes] PRIMARY KEY ([Id]),
                CONSTRAINT [CK_JournalTypes_Code] CHECK (LEN([Code]) BETWEEN 1 AND 10 AND [Code] COLLATE Latin1_General_BIN NOT LIKE '%[^A-Z0-9]%'),
                CONSTRAINT [CK_JournalTypes_Prefix] CHECK (LEN([SequencePrefix]) BETWEEN 1 AND 10 AND [SequencePrefix] COLLATE Latin1_General_BIN NOT LIKE '%[^A-Z0-9]%'),
                CONSTRAINT [CK_JournalTypes_Type] CHECK ([Type] IN ('Sale', 'Purchase', 'Cash', 'Bank', 'Miscellaneous', 'Opening', 'Closing'))
                """);
            ForeignKey(mb, "JournalTypes", "FK_JournalTypes_ChartOfAccounts_DefaultDebitAccountId",
                "FOREIGN KEY ([DefaultDebitAccountId]) REFERENCES [finance].[ChartOfAccounts] ([Id]) ON DELETE NO ACTION");
            ForeignKey(mb, "JournalTypes", "FK_JournalTypes_ChartOfAccounts_DefaultCreditAccountId",
                "FOREIGN KEY ([DefaultCreditAccountId]) REFERENCES [finance].[ChartOfAccounts] ([Id]) ON DELETE NO ACTION");
            Index(mb, "JournalTypes", "IX_JournalTypes_Uuid", "CREATE UNIQUE INDEX [IX_JournalTypes_Uuid] ON [finance].[JournalTypes] ([Uuid])");
            Index(mb, "JournalTypes", "UX_JournalTypes_Org_Code",
                "CREATE UNIQUE INDEX [UX_JournalTypes_Org_Code] ON [finance].[JournalTypes] ([OrganizationId], [Code])");
            Index(mb, "JournalTypes", "UX_JournalTypes_Org_Prefix",
                "CREATE UNIQUE INDEX [UX_JournalTypes_Org_Prefix] ON [finance].[JournalTypes] ([OrganizationId], [SequencePrefix])");
            Index(mb, "JournalTypes", "IX_JournalTypes_Org_Type",
                "CREATE INDEX [IX_JournalTypes_Org_Type] ON [finance].[JournalTypes] ([OrganizationId], [Type])");
            Index(mb, "JournalTypes", "IX_JournalTypes_Org_Active",
                "CREATE INDEX [IX_JournalTypes_Org_Active] ON [finance].[JournalTypes] ([OrganizationId], [IsActive]) WHERE [IsActive] = 1");
            Index(mb, "JournalTypes", "UX_JournalTypes_Org_Opening",
                "CREATE UNIQUE INDEX [UX_JournalTypes_Org_Opening] ON [finance].[JournalTypes] ([OrganizationId], [Type]) WHERE [Type] = 'Opening'");
            Index(mb, "JournalTypes", "UX_JournalTypes_Org_Closing",
                "CREATE UNIQUE INDEX [UX_JournalTypes_Org_Closing] ON [finance].[JournalTypes] ([OrganizationId], [Type]) WHERE [Type] = 'Closing'");
            Index(mb, "JournalTypes", "IX_JournalTypes_DefaultDebitAccountId",
                "CREATE INDEX [IX_JournalTypes_DefaultDebitAccountId] ON [finance].[JournalTypes] ([DefaultDebitAccountId])");
            Index(mb, "JournalTypes", "IX_JournalTypes_DefaultCreditAccountId",
                "CREATE INDEX [IX_JournalTypes_DefaultCreditAccountId] ON [finance].[JournalTypes] ([DefaultCreditAccountId])");

            // ── M8 the 7 system journal types (§4.3, D-6, D-CORE-1) ──────────────────────────────────────────────────
            // Skipped per organization and code when the code, the prefix or the single Opening/Closing slot is taken.
            mb.Sql("""
                IF OBJECT_ID(N'[finance].[JournalTypes]', N'U') IS NOT NULL AND OBJECT_ID(N'[finance].[JournalEntries]', N'U') IS NOT NULL
                   AND OBJECT_ID(N'[finance].[CoaSetupStatuses]', N'U') IS NOT NULL
                INSERT INTO [finance].[JournalTypes] ([Uuid], [OrganizationId], [Code], [Name], [Type], [SequencePrefix], [DefaultDebitAccountId],
                    [DefaultCreditAccountId], [RequiresPartner], [AutoPost], [IsSystem], [IsActive], [SortOrder], [CreatedAt], [CreatedBy], [ModifiedAt], [ModifiedBy])
                SELECT NEWID(), o.[OrganizationId], d.[Code], d.[Name], d.[Type], d.[Prefix], NULL, NULL, d.[RequiresPartner], d.[AutoPost], 1, 1,
                       d.[SortOrder], SYSUTCDATETIME(), NULL, SYSUTCDATETIME(), NULL
                FROM (SELECT [OrganizationId] FROM [finance].[CoaSetupStatuses] WHERE [Status] = 'Complete'
                      UNION SELECT [OrganizationId] FROM [finance].[JournalEntries]) o
                CROSS JOIN (VALUES
                    (N'SALE',  N'Sales Journal',         N'Sale',          N'SALE',  CAST(1 AS bit), CAST(0 AS bit), 10),
                    (N'PURCH', N'Purchase Journal',      N'Purchase',      N'PURCH', CAST(1 AS bit), CAST(0 AS bit), 20),
                    (N'CASH',  N'Cash Journal',          N'Cash',          N'CASH',  CAST(0 AS bit), CAST(0 AS bit), 30),
                    (N'BANK',  N'Bank Journal',          N'Bank',          N'BANK',  CAST(0 AS bit), CAST(0 AS bit), 40),
                    (N'MISC',  N'Miscellaneous Journal', N'Miscellaneous', N'JV',    CAST(0 AS bit), CAST(0 AS bit), 50),
                    (N'OPEN',  N'Opening Journal',       N'Opening',       N'OPEN',  CAST(0 AS bit), CAST(1 AS bit), 60),
                    (N'CLOSE', N'Closing Journal',       N'Closing',       N'CLOSE', CAST(0 AS bit), CAST(1 AS bit), 70)
                ) d ([Code], [Name], [Type], [Prefix], [RequiresPartner], [AutoPost], [SortOrder])
                WHERE NOT EXISTS (SELECT 1 FROM [finance].[JournalTypes] t WHERE t.[OrganizationId] = o.[OrganizationId] AND t.[Code] = d.[Code])
                  AND NOT EXISTS (SELECT 1 FROM [finance].[JournalTypes] t WHERE t.[OrganizationId] = o.[OrganizationId] AND t.[SequencePrefix] = d.[Prefix])
                  AND NOT (d.[Type] IN (N'Opening', N'Closing')
                           AND EXISTS (SELECT 1 FROM [finance].[JournalTypes] t WHERE t.[OrganizationId] = o.[OrganizationId] AND t.[Type] = d.[Type]));
                """);

            // ── M2 AdjustmentOnly period status (§5.2, D-11) ────────────────────────────────────────────────────────
            ReplaceCheck(mb, "FiscalPeriods", "CK_FiscalPeriods_Status", "[Status] IN ('Open', 'AdjustmentOnly', 'Locked', 'Closed')", "AdjustmentOnly");

            // ── M6 ReconcileGroups (§11.2, D-16) ─────────────────────────────────────────────────────────────────────
            Table(mb, "ReconcileGroups", """
                [Id] int NOT NULL IDENTITY,
                [Uuid] uniqueidentifier NOT NULL,
                [OrganizationId] uniqueidentifier NOT NULL,
                [GroupNumber] nvarchar(30) NOT NULL,
                [ReconcileType] nvarchar(20) NOT NULL,
                [AccountId] int NOT NULL,
                [Amount] decimal(18,4) NOT NULL,
                [CurrencyCode] nvarchar(3) NULL,
                [ReconciledAt] datetime2 NOT NULL,
                [CreatedBy] int NULL,
                [CreatedByName] nvarchar(200) NULL,
                CONSTRAINT [PK_ReconcileGroups] PRIMARY KEY ([Id]),
                CONSTRAINT [CK_ReconcileGroups_Amount] CHECK ([Amount] >= 0),
                CONSTRAINT [CK_ReconcileGroups_Type] CHECK ([ReconcileType] IN ('Full', 'Partial'))
                """);
            ForeignKey(mb, "ReconcileGroups", "FK_ReconcileGroups_ChartOfAccounts_AccountId",
                "FOREIGN KEY ([AccountId]) REFERENCES [finance].[ChartOfAccounts] ([Id]) ON DELETE NO ACTION");
            Index(mb, "ReconcileGroups", "IX_ReconcileGroups_Uuid", "CREATE UNIQUE INDEX [IX_ReconcileGroups_Uuid] ON [finance].[ReconcileGroups] ([Uuid])");
            Index(mb, "ReconcileGroups", "UX_ReconcileGroups_Org_Number",
                "CREATE UNIQUE INDEX [UX_ReconcileGroups_Org_Number] ON [finance].[ReconcileGroups] ([OrganizationId], [GroupNumber])");
            Index(mb, "ReconcileGroups", "IX_ReconcileGroups_Org_Account",
                "CREATE INDEX [IX_ReconcileGroups_Org_Account] ON [finance].[ReconcileGroups] ([OrganizationId], [AccountId])");
            Index(mb, "ReconcileGroups", "IX_ReconcileGroups_AccountId",
                "CREATE INDEX [IX_ReconcileGroups_AccountId] ON [finance].[ReconcileGroups] ([AccountId])");

            // ── M3 JournalEntries (§6.2 as ALTER, D-2) ──────────────────────────────────────────────────────────────
            Column(mb, "JournalEntries", "JournalTypeId", "int NULL"); // NOT NULL after the backfill below
            Column(mb, "JournalEntries", "DocumentDate", "date NULL");
            Column(mb, "JournalEntries", "FiscalYearId", "int NULL");
            Column(mb, "JournalEntries", "FiscalPeriodId", "int NULL");
            Column(mb, "JournalEntries", "ExchangeRate", "decimal(18,6) NULL");
            Column(mb, "JournalEntries", "ReversedByEntryId", "int NULL");
            Column(mb, "JournalEntries", "ReversalReason", "nvarchar(200) NULL");
            NotNullColumn(mb, "JournalEntries", "IsOpeningEntry", "bit", "CAST(0 AS bit)",
                "UPDATE [finance].[JournalEntries] SET [IsOpeningEntry] = 1 WHERE [EntryType] = 'OPENING_BALANCE'");
            NotNullColumn(mb, "JournalEntries", "IsClosingEntry", "bit", "CAST(0 AS bit)");
            NotNullColumn(mb, "JournalEntries", "LineCount", "int", "0",
                "UPDATE j SET [LineCount] = (SELECT COUNT(*) FROM [finance].[JournalEntryLines] l WHERE l.[JournalEntryId] = j.[Id]) FROM [finance].[JournalEntries] j");
            Column(mb, "JournalEntries", "CreatedByName", "nvarchar(200) NULL");
            Column(mb, "JournalEntries", "PostedByName", "nvarchar(200) NULL");
            Column(mb, "JournalEntries", "ModifiedBy", "int NULL");
            Column(mb, "JournalEntries", "ModifiedByName", "nvarchar(200) NULL");

            // D-4 — the status list grows before any row is marked Reversed.
            ReplaceCheck(mb, "JournalEntries", "CK_JournalEntries_Status", "[Status] IN ('Draft', 'Posted', 'Reversed')", "Reversed");

            // D-12 — journal type by EntryType (CASH when a line is on DEFAULT_CASH, else BANK, for money movements).
            mb.Sql("""
                IF COL_LENGTH(N'[finance].[JournalEntries]', N'JournalTypeId') IS NOT NULL AND OBJECT_ID(N'[finance].[JournalTypes]', N'U') IS NOT NULL
                UPDATE j SET [JournalTypeId] = t.[Id]
                FROM [finance].[JournalEntries] j
                CROSS APPLY (SELECT CASE
                    WHEN j.[EntryType] = 'OPENING_BALANCE' THEN N'OPEN'
                    WHEN j.[EntryType] = 'YEAR_END_CLOSE' THEN N'CLOSE'
                    WHEN j.[EntryType] IN ('SALES_INVOICE', 'CREDIT_NOTE', 'SALES_RETURN', 'POS_SALE', 'POS_RETURN', 'SALES_RECEIPT', 'REFUND_RECEIPT') THEN N'SALE'
                    WHEN j.[EntryType] IN ('PURCHASE_BILL', 'VENDOR_CREDIT', 'PURCHASE_RETURN', 'EXPENSE') THEN N'PURCH'
                    WHEN j.[EntryType] IN ('PAYMENT', 'BILL_PAYMENT', 'DEPOSIT_SLIP', 'BANK_FEE') THEN
                        CASE WHEN EXISTS (SELECT 1 FROM [finance].[JournalEntryLines] l
                                          JOIN [finance].[AccountingDefaults] d ON d.[OrganizationId] = j.[OrganizationId]
                                               AND d.[DefaultKey] = 'DEFAULT_CASH' AND d.[AccountId] = l.[AccountId]
                                          WHERE l.[JournalEntryId] = j.[Id]) THEN N'CASH' ELSE N'BANK' END
                    ELSE N'MISC' END AS [Code]) c
                JOIN [finance].[JournalTypes] t ON t.[OrganizationId] = j.[OrganizationId] AND t.[Code] = c.[Code]
                WHERE j.[JournalTypeId] IS NULL;
                """);
            // A type that was not seeded (code/prefix taken by a custom type): any active Miscellaneous type, else any type.
            mb.Sql("""
                IF COL_LENGTH(N'[finance].[JournalEntries]', N'JournalTypeId') IS NOT NULL AND OBJECT_ID(N'[finance].[JournalTypes]', N'U') IS NOT NULL
                UPDATE j SET [JournalTypeId] = (SELECT TOP 1 t.[Id] FROM [finance].[JournalTypes] t WHERE t.[OrganizationId] = j.[OrganizationId]
                                                ORDER BY CASE WHEN t.[Type] = 'Miscellaneous' THEN 0 ELSE 1 END, t.[IsActive] DESC, t.[Id])
                FROM [finance].[JournalEntries] j
                WHERE j.[JournalTypeId] IS NULL;
                """);
            mb.Sql("""
                IF COL_LENGTH(N'[finance].[JournalEntries]', N'JournalTypeId') IS NOT NULL
                   AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[finance].[JournalEntries]') AND name = N'JournalTypeId' AND is_nullable = 1)
                   AND NOT EXISTS (SELECT 1 FROM [finance].[JournalEntries] WHERE [JournalTypeId] IS NULL)
                    ALTER TABLE [finance].[JournalEntries] ALTER COLUMN [JournalTypeId] int NOT NULL;
                """);

            // Fiscal year / period of the date (a regular period before period 13, which shares the year's last day).
            mb.Sql("""
                IF COL_LENGTH(N'[finance].[JournalEntries]', N'FiscalPeriodId') IS NOT NULL
                UPDATE j SET [FiscalPeriodId] = p.[Id], [FiscalYearId] = p.[FiscalYearId]
                FROM [finance].[JournalEntries] j
                CROSS APPLY (SELECT TOP 1 fp.[Id], fp.[FiscalYearId] FROM [finance].[FiscalPeriods] fp
                             WHERE fp.[OrganizationId] = j.[OrganizationId] AND fp.[StartDate] <= j.[EntryDate] AND j.[EntryDate] <= fp.[EndDate]
                             ORDER BY CASE WHEN fp.[PeriodNumber] = 13 THEN 1 ELSE 0 END, fp.[PeriodNumber]) p
                WHERE j.[FiscalPeriodId] IS NULL;
                """);

            // D-4 — an A44 reversal pair: the original is Reversed and points at its (first) Posted reversal (REV-10: a Draft
            // reversal moved no balance; posting it later marks the original, JournalEntryService.PostAsync).
            mb.Sql("""
                IF COL_LENGTH(N'[finance].[JournalEntries]', N'ReversedByEntryId') IS NOT NULL
                UPDATE o SET [ReversedByEntryId] = r.[FirstId], [Status] = CASE WHEN o.[Status] = 'Posted' THEN 'Reversed' ELSE o.[Status] END
                FROM [finance].[JournalEntries] o
                JOIN (SELECT [ReversesEntryId], MIN([Id]) AS [FirstId] FROM [finance].[JournalEntries]
                      WHERE [ReversesEntryId] IS NOT NULL AND [Status] = 'Posted' GROUP BY [ReversesEntryId]) r ON r.[ReversesEntryId] = o.[Id]
                WHERE o.[ReversedByEntryId] IS NULL AND o.[ReversesEntryId] IS NULL;
                """);

            ForeignKey(mb, "JournalEntries", "FK_JournalEntries_JournalTypes_JournalTypeId",
                "FOREIGN KEY ([JournalTypeId]) REFERENCES [finance].[JournalTypes] ([Id]) ON DELETE NO ACTION");
            ForeignKey(mb, "JournalEntries", "FK_JournalEntries_FiscalYears_FiscalYearId",
                "FOREIGN KEY ([FiscalYearId]) REFERENCES [finance].[FiscalYears] ([Id]) ON DELETE NO ACTION");
            ForeignKey(mb, "JournalEntries", "FK_JournalEntries_FiscalPeriods_FiscalPeriodId",
                "FOREIGN KEY ([FiscalPeriodId]) REFERENCES [finance].[FiscalPeriods] ([Id]) ON DELETE NO ACTION");
            ForeignKey(mb, "JournalEntries", "FK_JournalEntries_JournalEntries_ReversedByEntryId",
                "FOREIGN KEY ([ReversedByEntryId]) REFERENCES [finance].[JournalEntries] ([Id]) ON DELETE NO ACTION");
            Index(mb, "JournalEntries", "IX_JournalEntries_JournalTypeId",
                "CREATE INDEX [IX_JournalEntries_JournalTypeId] ON [finance].[JournalEntries] ([JournalTypeId])");
            Index(mb, "JournalEntries", "IX_JournalEntries_FiscalYearId",
                "CREATE INDEX [IX_JournalEntries_FiscalYearId] ON [finance].[JournalEntries] ([FiscalYearId])");
            Index(mb, "JournalEntries", "IX_JournalEntries_FiscalPeriodId",
                "CREATE INDEX [IX_JournalEntries_FiscalPeriodId] ON [finance].[JournalEntries] ([FiscalPeriodId])");
            Index(mb, "JournalEntries", "IX_JournalEntries_ReversedBy",
                "CREATE INDEX [IX_JournalEntries_ReversedBy] ON [finance].[JournalEntries] ([ReversedByEntryId]) WHERE [ReversedByEntryId] IS NOT NULL");
            Index(mb, "JournalEntries", "IX_JournalEntries_Org_Status",
                "CREATE INDEX [IX_JournalEntries_Org_Status] ON [finance].[JournalEntries] ([OrganizationId], [Status])");
            Index(mb, "JournalEntries", "IX_JournalEntries_Org_PostedAt",
                "CREATE INDEX [IX_JournalEntries_Org_PostedAt] ON [finance].[JournalEntries] ([OrganizationId], [PostedAt]) WHERE [Status] = 'Posted'");
            // D-5 — unique keys by prefix (a filtered index allows neither LIKE nor OR: one range per prefix, ':' < ';').
            Index(mb, "JournalEntries", "UX_JournalEntries_Org_ManualKey",
                "CREATE UNIQUE INDEX [UX_JournalEntries_Org_ManualKey] ON [finance].[JournalEntries] ([OrganizationId], [IdempotencyKey]) WHERE [IdempotencyKey] >= N'manual:' AND [IdempotencyKey] < N'manual;'");
            Index(mb, "JournalEntries", "UX_JournalEntries_Org_ReversalKey",
                "CREATE UNIQUE INDEX [UX_JournalEntries_Org_ReversalKey] ON [finance].[JournalEntries] ([OrganizationId], [IdempotencyKey]) WHERE [IdempotencyKey] >= N'reversal:' AND [IdempotencyKey] < N'reversal;'");
            Index(mb, "JournalEntries", "UX_JournalEntries_Org_YearCloseKey",
                "CREATE UNIQUE INDEX [UX_JournalEntries_Org_YearCloseKey] ON [finance].[JournalEntries] ([OrganizationId], [IdempotencyKey]) WHERE [IdempotencyKey] >= N'yearclose:' AND [IdempotencyKey] < N'yearclose;'");
            Check(mb, "JournalEntries", "CK_JournalEntries_Balanced", "ABS([TotalDebit] - [TotalCredit]) < 0.01");
            Check(mb, "JournalEntries", "CK_JournalEntries_OpeningClosing", "NOT ([IsOpeningEntry] = 1 AND [IsClosingEntry] = 1)");
            Check(mb, "JournalEntries", "CK_JournalEntries_ReversalLinks", "NOT ([ReversesEntryId] IS NOT NULL AND [ReversedByEntryId] IS NOT NULL)");
            Check(mb, "JournalEntries", "CK_JournalEntries_ReversedStatus", "[Status] <> 'Reversed' OR [ReversedByEntryId] IS NOT NULL");
            Check(mb, "JournalEntries", "CK_JournalEntries_ExchangeRate", "[ExchangeRate] IS NULL OR [ExchangeRate] > 0");

            // ── M4 JournalEntryLines (§7.2 as ALTER, D-3) ───────────────────────────────────────────────────────────
            // D-CORE-8 — the line's public id (each existing row gets its own NEWID()).
            NotNullColumn(mb, "JournalEntryLines", "Uuid", "uniqueidentifier", "NEWID()");
            Index(mb, "JournalEntryLines", "IX_JournalEntryLines_Uuid",
                "CREATE UNIQUE INDEX [IX_JournalEntryLines_Uuid] ON [finance].[JournalEntryLines] ([Uuid])");
            Column(mb, "JournalEntryLines", "PartnerUuid", "uniqueidentifier NULL");
            Column(mb, "JournalEntryLines", "PartnerType", "nvarchar(20) NULL");
            Column(mb, "JournalEntryLines", "PartnerName", "nvarchar(200) NULL");
            Column(mb, "JournalEntryLines", "CurrencyCode", "nvarchar(3) NULL");
            Column(mb, "JournalEntryLines", "AmountCurrency", "decimal(18,4) NULL");
            Column(mb, "JournalEntryLines", "ExchangeRate", "decimal(18,6) NULL");
            Column(mb, "JournalEntryLines", "CostCenter", "nvarchar(50) NULL");
            Column(mb, "JournalEntryLines", "Project", "nvarchar(50) NULL");
            Column(mb, "JournalEntryLines", "TaxCode", "nvarchar(50) NULL");
            Column(mb, "JournalEntryLines", "TaxAmount", "decimal(18,4) NULL");
            NotNullColumn(mb, "JournalEntryLines", "Reconciled", "bit", "CAST(0 AS bit)");
            Column(mb, "JournalEntryLines", "ReconcileGroupId", "int NULL");
            Column(mb, "JournalEntryLines", "ClearingEntryId", "int NULL");
            Column(mb, "JournalEntryLines", "ClearingDate", "date NULL");
            NotNullColumn(mb, "JournalEntryLines", "AmountResidual", "decimal(18,4)", "0",
                "UPDATE [finance].[JournalEntryLines] SET [AmountResidual] = [DebitAmount] - [CreditAmount]");
            NotNullColumn(mb, "JournalEntryLines", "CreatedAt", "datetime2", "SYSUTCDATETIME()",
                "UPDATE l SET [CreatedAt] = j.[CreatedAt] FROM [finance].[JournalEntryLines] l JOIN [finance].[JournalEntries] j ON j.[Id] = l.[JournalEntryId]");
            // The transaction currency of the existing lines is the header's (A44 converted to base; A42 is in base).
            mb.Sql("""
                IF COL_LENGTH(N'[finance].[JournalEntryLines]', N'AmountCurrency') IS NOT NULL
                UPDATE l SET [CurrencyCode] = j.[CurrencyCode], [AmountCurrency] = l.[DebitAmount] - l.[CreditAmount]
                FROM [finance].[JournalEntryLines] l JOIN [finance].[JournalEntries] j ON j.[Id] = l.[JournalEntryId]
                WHERE l.[CurrencyCode] IS NULL AND l.[AmountCurrency] IS NULL;
                """);
            ForeignKey(mb, "JournalEntryLines", "FK_JournalEntryLines_ReconcileGroups_ReconcileGroupId",
                "FOREIGN KEY ([ReconcileGroupId]) REFERENCES [finance].[ReconcileGroups] ([Id]) ON DELETE NO ACTION");
            ForeignKey(mb, "JournalEntryLines", "FK_JournalEntryLines_JournalEntries_ClearingEntryId",
                "FOREIGN KEY ([ClearingEntryId]) REFERENCES [finance].[JournalEntries] ([Id]) ON DELETE NO ACTION");
            Index(mb, "JournalEntryLines", "IX_JournalEntryLines_Org_Account_Clearing",
                "CREATE INDEX [IX_JournalEntryLines_Org_Account_Clearing] ON [finance].[JournalEntryLines] ([OrganizationId], [AccountId], [ClearingDate])");
            Index(mb, "JournalEntryLines", "IX_JournalEntryLines_Org_Partner",
                "CREATE INDEX [IX_JournalEntryLines_Org_Partner] ON [finance].[JournalEntryLines] ([OrganizationId], [PartnerUuid], [PartnerType], [Reconciled]) WHERE [PartnerUuid] IS NOT NULL");
            Index(mb, "JournalEntryLines", "IX_JournalEntryLines_ReconcileGroup",
                "CREATE INDEX [IX_JournalEntryLines_ReconcileGroup] ON [finance].[JournalEntryLines] ([ReconcileGroupId]) WHERE [ReconcileGroupId] IS NOT NULL");
            Index(mb, "JournalEntryLines", "IX_JournalEntryLines_Org_OpenItems",
                "CREATE INDEX [IX_JournalEntryLines_Org_OpenItems] ON [finance].[JournalEntryLines] ([OrganizationId], [AccountId], [Reconciled]) WHERE [Reconciled] = 0");
            Index(mb, "JournalEntryLines", "IX_JournalEntryLines_Org_CostCenter",
                "CREATE INDEX [IX_JournalEntryLines_Org_CostCenter] ON [finance].[JournalEntryLines] ([OrganizationId], [CostCenter]) WHERE [CostCenter] IS NOT NULL");
            Index(mb, "JournalEntryLines", "IX_JournalEntryLines_ClearingEntry",
                "CREATE INDEX [IX_JournalEntryLines_ClearingEntry] ON [finance].[JournalEntryLines] ([ClearingEntryId]) WHERE [ClearingEntryId] IS NOT NULL");
            Check(mb, "JournalEntryLines", "CK_JournalEntryLines_PartnerType", "[PartnerType] IS NULL OR [PartnerType] IN ('Customer', 'Vendor')");
            Check(mb, "JournalEntryLines", "CK_JournalEntryLines_Reconciled", "[Reconciled] = 0 OR [AmountResidual] = 0");
            Check(mb, "JournalEntryLines", "CK_JournalEntryLines_ExchangeRate", "[ExchangeRate] IS NULL OR [ExchangeRate] > 0");

            // ── M5 JournalEntrySequences (§8.2, D-7) ────────────────────────────────────────────────────────────────
            Table(mb, "JournalEntrySequences", """
                [Id] int NOT NULL IDENTITY,
                [OrganizationId] uniqueidentifier NOT NULL,
                [JournalTypeId] int NULL,
                [FiscalYearId] int NULL,
                [SequenceYear] int NOT NULL,
                [CurrentSequence] int NOT NULL,
                [CreatedAt] datetime2 NOT NULL,
                [ModifiedAt] datetime2 NOT NULL,
                CONSTRAINT [PK_JournalEntrySequences] PRIMARY KEY ([Id]),
                CONSTRAINT [CK_JournalEntrySequences_Current] CHECK ([CurrentSequence] >= 0)
                """);
            ForeignKey(mb, "JournalEntrySequences", "FK_JournalEntrySequences_JournalTypes_JournalTypeId",
                "FOREIGN KEY ([JournalTypeId]) REFERENCES [finance].[JournalTypes] ([Id]) ON DELETE NO ACTION");
            ForeignKey(mb, "JournalEntrySequences", "FK_JournalEntrySequences_FiscalYears_FiscalYearId",
                "FOREIGN KEY ([FiscalYearId]) REFERENCES [finance].[FiscalYears] ([Id]) ON DELETE NO ACTION");
            Index(mb, "JournalEntrySequences", "UX_JournalEntrySequences_Org_Type_Year",
                "CREATE UNIQUE INDEX [UX_JournalEntrySequences_Org_Type_Year] ON [finance].[JournalEntrySequences] ([OrganizationId], [JournalTypeId], [FiscalYearId], [SequenceYear])");
            Index(mb, "JournalEntrySequences", "IX_JournalEntrySequences_JournalTypeId",
                "CREATE INDEX [IX_JournalEntrySequences_JournalTypeId] ON [finance].[JournalEntrySequences] ([JournalTypeId])");
            Index(mb, "JournalEntrySequences", "IX_JournalEntrySequences_FiscalYearId",
                "CREATE INDEX [IX_JournalEntrySequences_FiscalYearId] ON [finance].[JournalEntrySequences] ([FiscalYearId])");
        }

        /// <summary>
        /// REV-12 — <b>lossy, LocalDB / test use only: never run it on SMSGlobal.</b> It is not guarded (it drops objects a guarded
        /// Up may have skipped) and it throws information away (Reversed → Posted, AdjustmentOnly → Open, every A47 column/table
        /// and its data).
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var mb = migrationBuilder;
            mb.DropTable(name: "JournalEntrySequences", schema: "finance");

            mb.DropForeignKey(name: "FK_JournalEntryLines_ReconcileGroups_ReconcileGroupId", schema: "finance", table: "JournalEntryLines");
            mb.DropForeignKey(name: "FK_JournalEntryLines_JournalEntries_ClearingEntryId", schema: "finance", table: "JournalEntryLines");
            foreach (var ix in new[] { "IX_JournalEntryLines_Uuid", "IX_JournalEntryLines_Org_Account_Clearing", "IX_JournalEntryLines_Org_Partner", "IX_JournalEntryLines_ReconcileGroup",
                                       "IX_JournalEntryLines_Org_OpenItems", "IX_JournalEntryLines_Org_CostCenter", "IX_JournalEntryLines_ClearingEntry" })
                mb.DropIndex(name: ix, schema: "finance", table: "JournalEntryLines");
            foreach (var ck in new[] { "CK_JournalEntryLines_PartnerType", "CK_JournalEntryLines_Reconciled", "CK_JournalEntryLines_ExchangeRate" })
                mb.DropCheckConstraint(name: ck, schema: "finance", table: "JournalEntryLines");
            foreach (var col in new[] { "Uuid", "PartnerUuid", "PartnerType", "PartnerName", "CurrencyCode", "AmountCurrency", "ExchangeRate", "CostCenter", "Project",
                                        "TaxCode", "TaxAmount", "Reconciled", "ReconcileGroupId", "ClearingEntryId", "ClearingDate", "AmountResidual", "CreatedAt" })
                mb.DropColumn(name: col, schema: "finance", table: "JournalEntryLines");

            mb.DropForeignKey(name: "FK_JournalEntries_JournalTypes_JournalTypeId", schema: "finance", table: "JournalEntries");
            mb.DropForeignKey(name: "FK_JournalEntries_FiscalYears_FiscalYearId", schema: "finance", table: "JournalEntries");
            mb.DropForeignKey(name: "FK_JournalEntries_FiscalPeriods_FiscalPeriodId", schema: "finance", table: "JournalEntries");
            mb.DropForeignKey(name: "FK_JournalEntries_JournalEntries_ReversedByEntryId", schema: "finance", table: "JournalEntries");
            foreach (var ix in new[] { "IX_JournalEntries_JournalTypeId", "IX_JournalEntries_FiscalYearId", "IX_JournalEntries_FiscalPeriodId",
                                       "IX_JournalEntries_ReversedBy", "IX_JournalEntries_Org_Status", "IX_JournalEntries_Org_PostedAt",
                                       "UX_JournalEntries_Org_ManualKey", "UX_JournalEntries_Org_ReversalKey", "UX_JournalEntries_Org_YearCloseKey" })
                mb.DropIndex(name: ix, schema: "finance", table: "JournalEntries");
            foreach (var ck in new[] { "CK_JournalEntries_Balanced", "CK_JournalEntries_OpeningClosing", "CK_JournalEntries_ReversalLinks",
                                       "CK_JournalEntries_ReversedStatus", "CK_JournalEntries_ExchangeRate", "CK_JournalEntries_Status" })
                mb.DropCheckConstraint(name: ck, schema: "finance", table: "JournalEntries");
            // A reversed journal was posted: it goes back to Posted for the A42/A44 status list.
            mb.Sql("UPDATE [finance].[JournalEntries] SET [Status] = 'Posted' WHERE [Status] = 'Reversed';");
            mb.AddCheckConstraint(name: "CK_JournalEntries_Status", schema: "finance", table: "JournalEntries", sql: "[Status] IN ('Draft', 'Posted')");
            foreach (var col in new[] { "JournalTypeId", "DocumentDate", "FiscalYearId", "FiscalPeriodId", "ExchangeRate", "ReversedByEntryId", "ReversalReason",
                                        "IsOpeningEntry", "IsClosingEntry", "LineCount", "CreatedByName", "PostedByName", "ModifiedBy", "ModifiedByName" })
                mb.DropColumn(name: col, schema: "finance", table: "JournalEntries");

            mb.DropTable(name: "ReconcileGroups", schema: "finance");

            mb.DropCheckConstraint(name: "CK_FiscalPeriods_Status", schema: "finance", table: "FiscalPeriods");
            mb.Sql("UPDATE [finance].[FiscalPeriods] SET [Status] = 'Open' WHERE [Status] = 'AdjustmentOnly';");
            mb.AddCheckConstraint(name: "CK_FiscalPeriods_Status", schema: "finance", table: "FiscalPeriods", sql: "[Status] IN ('Open', 'Locked', 'Closed')");

            mb.DropTable(name: "JournalTypes", schema: "finance");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Accounting.Migrations
{
    /// <summary>
    /// A47 GW (C11, D-GW-5) — data fix for the A44 reversal pairs written before the gateway went through the journal engine:
    /// <list type="number">
    /// <item>D-4 — a pair not yet marked (written between A47_JournalEngine and C11): the original becomes <c>Reversed</c> and
    /// points at its first reversal (<c>ReversedByEntryId</c>), the same statement as the A47 backfill;</item>
    /// <item>BR-RV-008 / D-RR-4 — per (pair, account): the original's lines and the reversal's lines on that account go into one
    /// Full <c>ReconcileGroups</c> row (<c>REC/{year}/{seq:D4}</c> from the organization's reconciliation counter, year = today's
    /// year, as <c>JournalReconciler</c> numbers them), residual 0, <c>Reconciled</c>, cleared by the reversal on its date — like a
    /// reversal written now. Only reversals with an A44 key (not <c>reversal:</c> — RR reconciled those itself), and an account is
    /// skipped when any of its lines in the pair is already in a group, reconciled or partly cleared (a user's reconciliation wins),
    /// or when the lines do not net to zero.</item>
    /// </list>
    /// <para>
    /// <b>Guarded and idempotent</b> (every API start migrates the shared database): runs only when the A47 columns exist; touches
    /// only pairs/lines still unmarked/unreconciled, so a second run changes nothing. The counter row is raised past every number
    /// it hands out (and past any REC number already used). One batch (temp tables), inside the migration's transaction.
    /// Down is a no-op: the groups are ordinary reconciliations (undo them with unreconcile).
    /// </para>
    /// </summary>
    public partial class A47_GatewayReversalReconcile : Migration
    {
        internal const string FixSql = """
            SET NOCOUNT ON;
            DECLARE @now datetime2 = SYSUTCDATETIME();
            DECLARE @year int = YEAR(@now);
            DECLARE @prefix nvarchar(20) = N'REC/' + RIGHT(N'000' + CAST(@year AS nvarchar(4)), 4) + N'/';

            -- 0. REV-10: only a Posted reversal reverses. Un-mark an original whose reversal is still a Draft (the
            --    A47_JournalEngine backfill marked it whatever the reversal's status).
            UPDATE o SET [ReversedByEntryId] = NULL, [Status] = CASE WHEN o.[Status] = 'Reversed' THEN 'Posted' ELSE o.[Status] END
            FROM [finance].[JournalEntries] o
            JOIN [finance].[JournalEntries] r ON r.[Id] = o.[ReversedByEntryId]
            WHERE r.[Status] = 'Draft';

            -- 1. D-4: mark the pairs not yet marked (the first Posted reversal only, REV-10).
            UPDATE o SET [ReversedByEntryId] = r.[FirstId], [Status] = CASE WHEN o.[Status] = 'Posted' THEN 'Reversed' ELSE o.[Status] END
            FROM [finance].[JournalEntries] o
            JOIN (SELECT [ReversesEntryId], MIN([Id]) AS [FirstId] FROM [finance].[JournalEntries]
                  WHERE [ReversesEntryId] IS NOT NULL AND [Status] = 'Posted' GROUP BY [ReversesEntryId]) r ON r.[ReversesEntryId] = o.[Id]
            WHERE o.[ReversedByEntryId] IS NULL AND o.[ReversesEntryId] IS NULL;

            -- 2. BR-RV-008: the (pair, account) groups still to make.
            CREATE TABLE #c (
                [Id] int IDENTITY PRIMARY KEY, [OrganizationId] uniqueidentifier NOT NULL, [OriginalId] int NOT NULL, [ReversalId] int NOT NULL,
                [AccountId] int NOT NULL, [Amount] decimal(18,4) NOT NULL, [ClearingDate] date NOT NULL, [Seq] int NULL,
                [GroupUuid] uniqueidentifier NOT NULL, [GroupId] int NULL);
            INSERT INTO #c ([OrganizationId], [OriginalId], [ReversalId], [AccountId], [Amount], [ClearingDate], [GroupUuid])
            SELECT o.[OrganizationId], o.[Id], r.[Id], l.[AccountId],
                   ROUND(SUM(CASE WHEN l.[AmountResidual] > 0 THEN l.[AmountResidual] ELSE 0 END), 4), r.[EntryDate], NEWID()
            FROM [finance].[JournalEntries] o
            JOIN [finance].[JournalEntries] r ON r.[Id] = o.[ReversedByEntryId] AND r.[ReversesEntryId] = o.[Id] AND r.[OrganizationId] = o.[OrganizationId]
            JOIN [finance].[JournalEntryLines] l ON l.[JournalEntryId] = o.[Id] OR l.[JournalEntryId] = r.[Id]
            WHERE o.[Status] = 'Reversed' AND r.[Status] = 'Posted'
              AND (r.[IdempotencyKey] IS NULL OR NOT (r.[IdempotencyKey] >= N'reversal:' AND r.[IdempotencyKey] < N'reversal;'))
            GROUP BY o.[OrganizationId], o.[Id], r.[Id], l.[AccountId], r.[EntryDate]
            HAVING MAX(CASE WHEN l.[ReconcileGroupId] IS NOT NULL OR l.[Reconciled] = 1 OR l.[AmountResidual] <> l.[DebitAmount] - l.[CreditAmount] THEN 1 ELSE 0 END) = 0
               AND SUM(CASE WHEN l.[JournalEntryId] = o.[Id] THEN 1 ELSE 0 END) > 0
               AND SUM(CASE WHEN l.[JournalEntryId] = r.[Id] THEN 1 ELSE 0 END) > 0
               AND SUM(CASE WHEN l.[AmountResidual] > 0 THEN 1 ELSE 0 END) > 0
               AND SUM(CASE WHEN l.[AmountResidual] < 0 THEN 1 ELSE 0 END) > 0
               AND ABS(SUM(l.[AmountResidual])) < 0.01;

            IF EXISTS (SELECT 1 FROM #c)
            BEGIN
                -- 3. Numbers: per organization after max(counter, highest REC number of the year already used).
                ;WITH n AS (SELECT [Id], ROW_NUMBER() OVER (PARTITION BY [OrganizationId] ORDER BY [OriginalId], [AccountId]) AS [rn] FROM #c)
                UPDATE c SET [Seq] = n.[rn] FROM #c c JOIN n ON n.[Id] = c.[Id];

                CREATE TABLE #b ([OrganizationId] uniqueidentifier PRIMARY KEY, [Base] int NOT NULL, [Cnt] int NOT NULL);
                INSERT INTO #b ([OrganizationId], [Base], [Cnt]) SELECT [OrganizationId], 0, COUNT(*) FROM #c GROUP BY [OrganizationId];
                UPDATE b SET [Base] = ISNULL((SELECT MAX(s.[CurrentSequence]) FROM [finance].[JournalEntrySequences] s WITH (UPDLOCK, HOLDLOCK)
                                              WHERE s.[OrganizationId] = b.[OrganizationId] AND s.[JournalTypeId] IS NULL AND s.[FiscalYearId] IS NULL
                                                AND s.[SequenceYear] = @year), 0)
                FROM #b b;
                UPDATE b SET [Base] = x.[M]
                FROM #b b
                CROSS APPLY (SELECT MAX(TRY_CAST(SUBSTRING(g.[GroupNumber], LEN(@prefix) + 1, 30) AS int)) AS [M]
                             FROM [finance].[ReconcileGroups] g
                             WHERE g.[OrganizationId] = b.[OrganizationId] AND LEFT(g.[GroupNumber], LEN(@prefix)) = @prefix) x
                WHERE x.[M] > b.[Base];

                -- 4. The groups (Full; currency = the reporting currency of the clearing date's fiscal year, else the latest).
                INSERT INTO [finance].[ReconcileGroups] ([Uuid], [OrganizationId], [GroupNumber], [ReconcileType], [AccountId], [Amount], [CurrencyCode], [ReconciledAt], [CreatedBy], [CreatedByName])
                SELECT c.[GroupUuid], c.[OrganizationId],
                       @prefix + CASE WHEN b.[Base] + c.[Seq] < 10000 THEN RIGHT(N'000' + CAST(b.[Base] + c.[Seq] AS nvarchar(10)), 4) ELSE CAST(b.[Base] + c.[Seq] AS nvarchar(10)) END,
                       N'Full', c.[AccountId], c.[Amount], cur.[Code], @now, NULL, NULL
                FROM #c c
                JOIN #b b ON b.[OrganizationId] = c.[OrganizationId]
                OUTER APPLY (SELECT TOP 1 NULLIF(LEFT(LTRIM(RTRIM(y.[ReportingCurrencyCode])), 3), N'') AS [Code]
                             FROM [finance].[FiscalYears] y WHERE y.[OrganizationId] = c.[OrganizationId]
                             ORDER BY CASE WHEN y.[StartDate] <= c.[ClearingDate] AND c.[ClearingDate] <= y.[EndDate] THEN 0 ELSE 1 END, y.[StartDate] DESC) cur;
                UPDATE c SET [GroupId] = g.[Id] FROM #c c JOIN [finance].[ReconcileGroups] g ON g.[Uuid] = c.[GroupUuid];

                -- 5. The lines: cleared by the reversal on its date.
                UPDATE l SET [ReconcileGroupId] = c.[GroupId], [AmountResidual] = 0, [Reconciled] = 1, [ClearingEntryId] = c.[ReversalId], [ClearingDate] = c.[ClearingDate]
                FROM [finance].[JournalEntryLines] l
                JOIN #c c ON l.[AccountId] = c.[AccountId] AND (l.[JournalEntryId] = c.[OriginalId] OR l.[JournalEntryId] = c.[ReversalId]);

                -- 6. The counter past every number handed out.
                UPDATE s SET [CurrentSequence] = b.[Base] + b.[Cnt], [ModifiedAt] = @now
                FROM [finance].[JournalEntrySequences] s
                JOIN #b b ON b.[OrganizationId] = s.[OrganizationId]
                WHERE s.[JournalTypeId] IS NULL AND s.[FiscalYearId] IS NULL AND s.[SequenceYear] = @year;
                INSERT INTO [finance].[JournalEntrySequences] ([OrganizationId], [JournalTypeId], [FiscalYearId], [SequenceYear], [CurrentSequence], [CreatedAt], [ModifiedAt])
                SELECT b.[OrganizationId], NULL, NULL, @year, b.[Base] + b.[Cnt], @now, @now
                FROM #b b
                WHERE NOT EXISTS (SELECT 1 FROM [finance].[JournalEntrySequences] s WHERE s.[OrganizationId] = b.[OrganizationId]
                                  AND s.[JournalTypeId] IS NULL AND s.[FiscalYearId] IS NULL AND s.[SequenceYear] = @year);
                DROP TABLE #b;
            END;
            DROP TABLE #c;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                IF OBJECT_ID(N'[finance].[ReconcileGroups]', N'U') IS NOT NULL
                   AND OBJECT_ID(N'[finance].[JournalEntrySequences]', N'U') IS NOT NULL
                   AND COL_LENGTH(N'[finance].[JournalEntries]', N'ReversedByEntryId') IS NOT NULL
                   AND COL_LENGTH(N'[finance].[JournalEntryLines]', N'ReconcileGroupId') IS NOT NULL
                   AND COL_LENGTH(N'[finance].[JournalEntryLines]', N'AmountResidual') IS NOT NULL
                    EXEC(N'{FixSql.Replace("'", "''")}');
                """);
        }

        /// <summary>
        /// REV-12 — <b>lossy, LocalDB / test use only: never run it on SMSGlobal.</b> It is not guarded (it drops objects a guarded
        /// Up may have skipped) and it throws information away (the reconciliations and journal links it made).
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data only: the groups are ordinary reconciliations (unreconcile undoes one); A47_JournalEngine's Down drops them.
        }
    }
}

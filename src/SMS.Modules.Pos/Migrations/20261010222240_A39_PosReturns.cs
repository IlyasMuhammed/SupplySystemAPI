using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Pos.Migrations
{
    /// <summary>
    /// A39 — returns, exchanges &amp; store credit (docs/pos-returns/). Additive and guarded: every statement checks
    /// COL_LENGTH / OBJECT_ID / sys.indexes first, because API startup migrates shared databases and a re-run must not fail.
    /// No data is seeded here (D-4: reasons and the org default policy are seeded lazily per organization).
    /// </summary>
    public partial class A39_PosReturns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── columns (M039.4, M039.6, M039.7) ──
            migrationBuilder.Sql("""
                IF COL_LENGTH(N'pos.shifts', N'TotalStoreCreditIssued') IS NULL
                    ALTER TABLE [pos].[shifts] ADD [TotalStoreCreditIssued] decimal(18,2) NOT NULL CONSTRAINT [DF_shifts_TotalStoreCreditIssued] DEFAULT 0.0;
                IF COL_LENGTH(N'pos.shifts', N'TotalStoreCreditRedeemed') IS NULL
                    ALTER TABLE [pos].[shifts] ADD [TotalStoreCreditRedeemed] decimal(18,2) NOT NULL CONSTRAINT [DF_shifts_TotalStoreCreditRedeemed] DEFAULT 0.0;
                IF COL_LENGTH(N'pos.payments', N'CreditNoteId') IS NULL
                    ALTER TABLE [pos].[payments] ADD [CreditNoteId] int NULL;
                IF COL_LENGTH(N'pos.orders', N'AuthorizedAt') IS NULL
                    ALTER TABLE [pos].[orders] ADD [AuthorizedAt] datetime2 NULL;
                IF COL_LENGTH(N'pos.orders', N'AuthorizedByUserId') IS NULL
                    ALTER TABLE [pos].[orders] ADD [AuthorizedByUserId] int NULL;
                IF COL_LENGTH(N'pos.orders', N'IsCrossStore') IS NULL
                    ALTER TABLE [pos].[orders] ADD [IsCrossStore] bit NOT NULL CONSTRAINT [DF_orders_IsCrossStore] DEFAULT CAST(0 AS bit);
                IF COL_LENGTH(N'pos.orders', N'OriginalStoreId') IS NULL
                    ALTER TABLE [pos].[orders] ADD [OriginalStoreId] int NULL;
                IF COL_LENGTH(N'pos.orders', N'RestockingFeeAmount') IS NULL
                    ALTER TABLE [pos].[orders] ADD [RestockingFeeAmount] decimal(18,2) NOT NULL CONSTRAINT [DF_orders_RestockingFeeAmount] DEFAULT 0.0;
                IF COL_LENGTH(N'pos.orders', N'ReturnReasonId') IS NULL
                    ALTER TABLE [pos].[orders] ADD [ReturnReasonId] int NULL;
                IF COL_LENGTH(N'pos.orders', N'StoreCreditNoteId') IS NULL
                    ALTER TABLE [pos].[orders] ADD [StoreCreditNoteId] int NULL;
                """);

            // ── tables ──
            migrationBuilder.Sql("""
                IF OBJECT_ID(N'pos.return_authorizations', N'U') IS NULL
                CREATE TABLE [pos].[return_authorizations] (
                    [Id] int NOT NULL IDENTITY,
                    [OrganizationId] uniqueidentifier NOT NULL,
                    [TokenHash] nvarchar(64) NOT NULL,
                    [TerminalId] int NOT NULL,
                    [OriginalOrderId] int NOT NULL,
                    [ManagerUserId] int NOT NULL,
                    [RequestedBy] int NOT NULL,
                    [AmountCeiling] decimal(18,2) NOT NULL,
                    [CanOverride] bit NOT NULL,
                    [CanCrossStore] bit NOT NULL,
                    [CreatedAt] datetime2 NOT NULL,
                    [ExpiresAt] datetime2 NOT NULL,
                    [UsedAt] datetime2 NULL,
                    [UsedByOrderId] int NULL,
                    CONSTRAINT [PK_return_authorizations] PRIMARY KEY ([Id])
                );
                """);
            migrationBuilder.Sql("""
                IF OBJECT_ID(N'pos.return_policies', N'U') IS NULL
                CREATE TABLE [pos].[return_policies] (
                    [Id] int NOT NULL IDENTITY,
                    [Uuid] uniqueidentifier NOT NULL,
                    [OrganizationId] uniqueidentifier NOT NULL,
                    [StoreId] int NULL,
                    [Name] nvarchar(100) NOT NULL,
                    [ReturnWindowDays] int NULL,
                    [ExchangeWindowDays] int NULL,
                    [RequireReceipt] bit NOT NULL,
                    [AllowNoReceiptExchange] bit NOT NULL,
                    [AllowCrossStoreReturn] bit NOT NULL,
                    [CrossStoreRequiresManager] bit NOT NULL,
                    [RestockingFeePct] decimal(9,4) NOT NULL,
                    [MaxReturnNoAuth] decimal(18,2) NULL,
                    [RefundToOriginalMethod] bit NOT NULL,
                    [AllowCashRefund] bit NOT NULL,
                    [AllowCreditNoteRefund] bit NOT NULL,
                    [CreditNoteExpiryDays] int NULL,
                    [IsActive] bit NOT NULL,
                    [ModifiedBy] int NULL,
                    [CreatedAt] datetime2 NOT NULL,
                    [ModifiedAt] datetime2 NOT NULL,
                    [RowVersion] rowversion NOT NULL,
                    CONSTRAINT [PK_return_policies] PRIMARY KEY ([Id]),
                    CONSTRAINT [FK_return_policies_stores_StoreId] FOREIGN KEY ([StoreId]) REFERENCES [pos].[stores] ([Id]) ON DELETE NO ACTION
                );
                """);
            migrationBuilder.Sql("""
                IF OBJECT_ID(N'pos.return_reasons', N'U') IS NULL
                CREATE TABLE [pos].[return_reasons] (
                    [Id] int NOT NULL IDENTITY,
                    [Uuid] uniqueidentifier NOT NULL,
                    [OrganizationId] uniqueidentifier NOT NULL,
                    [Code] nvarchar(20) NOT NULL,
                    [Name] nvarchar(100) NOT NULL,
                    [Description] nvarchar(500) NULL,
                    [RequiresNote] bit NOT NULL,
                    [RequiresManager] bit NOT NULL,
                    [AppliesTo] nvarchar(10) NOT NULL,
                    [RestockingFeeOverride] decimal(9,4) NULL,
                    [SortOrder] int NOT NULL,
                    [IsActive] bit NOT NULL,
                    [CreatedAt] datetime2 NOT NULL,
                    [ModifiedAt] datetime2 NOT NULL,
                    CONSTRAINT [PK_return_reasons] PRIMARY KEY ([Id])
                );
                """);
            migrationBuilder.Sql("""
                IF OBJECT_ID(N'pos.store_credit_notes', N'U') IS NULL
                CREATE TABLE [pos].[store_credit_notes] (
                    [Id] int NOT NULL IDENTITY,
                    [Uuid] uniqueidentifier NOT NULL,
                    [OrganizationId] uniqueidentifier NOT NULL,
                    [Number] nvarchar(50) NOT NULL,
                    [OfflineNumber] nvarchar(50) NULL,
                    [LocalId] nvarchar(64) NULL,
                    [CustomerUuid] uniqueidentifier NULL,
                    [CustomerName] nvarchar(300) NULL,
                    [IssuedStoreId] int NOT NULL,
                    [IssuedShiftId] int NULL,
                    [ReturnOrderId] int NULL,
                    [OriginalAmount] decimal(18,2) NOT NULL,
                    [RemainingBalance] decimal(18,2) NOT NULL,
                    [Status] nvarchar(20) NOT NULL,
                    [IssuedBy] int NOT NULL,
                    [IssuedAt] datetime2 NOT NULL,
                    [ExpiresAt] datetime2 NULL,
                    [LastUsedAt] datetime2 NULL,
                    [LastUsedStoreId] int NULL,
                    [Note] nvarchar(500) NULL,
                    [CancelledBy] int NULL,
                    [CancelledAt] datetime2 NULL,
                    [ModifiedAt] datetime2 NOT NULL,
                    [RowVersion] rowversion NOT NULL,
                    CONSTRAINT [PK_store_credit_notes] PRIMARY KEY ([Id]),
                    CONSTRAINT [CK_store_credit_notes_Balance] CHECK ([RemainingBalance] >= 0),
                    CONSTRAINT [FK_store_credit_notes_orders_ReturnOrderId] FOREIGN KEY ([ReturnOrderId]) REFERENCES [pos].[orders] ([Id]) ON DELETE NO ACTION,
                    CONSTRAINT [FK_store_credit_notes_stores_IssuedStoreId] FOREIGN KEY ([IssuedStoreId]) REFERENCES [pos].[stores] ([Id]) ON DELETE NO ACTION
                );
                """);
            migrationBuilder.Sql("""
                IF OBJECT_ID(N'pos.store_credit_transactions', N'U') IS NULL
                CREATE TABLE [pos].[store_credit_transactions] (
                    [Id] int NOT NULL IDENTITY,
                    [Uuid] uniqueidentifier NOT NULL,
                    [OrganizationId] uniqueidentifier NOT NULL,
                    [CreditNoteId] int NOT NULL,
                    [Type] nvarchar(10) NOT NULL,
                    [Amount] decimal(18,2) NOT NULL,
                    [BalanceAfter] decimal(18,2) NOT NULL,
                    [OrderId] int NULL,
                    [StoreId] int NOT NULL,
                    [PerformedBy] int NOT NULL,
                    [PerformedAt] datetime2 NOT NULL,
                    [Note] nvarchar(500) NULL,
                    CONSTRAINT [PK_store_credit_transactions] PRIMARY KEY ([Id]),
                    CONSTRAINT [FK_store_credit_transactions_orders_OrderId] FOREIGN KEY ([OrderId]) REFERENCES [pos].[orders] ([Id]) ON DELETE NO ACTION,
                    CONSTRAINT [FK_store_credit_transactions_store_credit_notes_CreditNoteId] FOREIGN KEY ([CreditNoteId]) REFERENCES [pos].[store_credit_notes] ([Id]) ON DELETE NO ACTION
                );
                """);

            // ── indexes ──
            foreach (var (name, table, sql) in Indexes)
                migrationBuilder.Sql($"""
                    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{name}' AND object_id = OBJECT_ID(N'pos.{table}'))
                        {sql};
                    """);

            // ── foreign keys onto the new tables ──
            migrationBuilder.Sql("""
                IF OBJECT_ID(N'pos.FK_orders_return_reasons_ReturnReasonId', N'F') IS NULL
                    ALTER TABLE [pos].[orders] ADD CONSTRAINT [FK_orders_return_reasons_ReturnReasonId]
                        FOREIGN KEY ([ReturnReasonId]) REFERENCES [pos].[return_reasons] ([Id]) ON DELETE NO ACTION;
                IF OBJECT_ID(N'pos.FK_payments_store_credit_notes_CreditNoteId', N'F') IS NULL
                    ALTER TABLE [pos].[payments] ADD CONSTRAINT [FK_payments_store_credit_notes_CreditNoteId]
                        FOREIGN KEY ([CreditNoteId]) REFERENCES [pos].[store_credit_notes] ([Id]) ON DELETE NO ACTION;
                """);
        }

        private static readonly (string Name, string Table, string Sql)[] Indexes =
        [
            ("IX_payments_CreditNoteId", "payments", "CREATE INDEX [IX_payments_CreditNoteId] ON [pos].[payments] ([CreditNoteId])"),
            ("IX_orders_OrganizationId_IsCrossStore_CreatedAt", "orders",
                "CREATE INDEX [IX_orders_OrganizationId_IsCrossStore_CreatedAt] ON [pos].[orders] ([OrganizationId], [IsCrossStore], [CreatedAt]) WHERE [IsCrossStore] = 1"),
            ("IX_orders_ReturnReasonId", "orders", "CREATE INDEX [IX_orders_ReturnReasonId] ON [pos].[orders] ([ReturnReasonId])"),
            ("IX_return_authorizations_OrganizationId_ExpiresAt", "return_authorizations",
                "CREATE INDEX [IX_return_authorizations_OrganizationId_ExpiresAt] ON [pos].[return_authorizations] ([OrganizationId], [ExpiresAt])"),
            ("IX_return_authorizations_TokenHash", "return_authorizations",
                "CREATE UNIQUE INDEX [IX_return_authorizations_TokenHash] ON [pos].[return_authorizations] ([TokenHash])"),
            ("IX_return_policies_StoreId", "return_policies", "CREATE INDEX [IX_return_policies_StoreId] ON [pos].[return_policies] ([StoreId])"),
            ("IX_return_policies_Uuid", "return_policies", "CREATE UNIQUE INDEX [IX_return_policies_Uuid] ON [pos].[return_policies] ([Uuid])"),
            ("UX_return_policies_OrgDefault", "return_policies",
                "CREATE UNIQUE INDEX [UX_return_policies_OrgDefault] ON [pos].[return_policies] ([OrganizationId]) WHERE [StoreId] IS NULL"),
            ("UX_return_policies_Store", "return_policies",
                "CREATE UNIQUE INDEX [UX_return_policies_Store] ON [pos].[return_policies] ([OrganizationId], [StoreId]) WHERE [StoreId] IS NOT NULL"),
            ("IX_return_reasons_OrganizationId_Code", "return_reasons",
                "CREATE UNIQUE INDEX [IX_return_reasons_OrganizationId_Code] ON [pos].[return_reasons] ([OrganizationId], [Code])"),
            ("IX_return_reasons_OrganizationId_ModifiedAt", "return_reasons",
                "CREATE INDEX [IX_return_reasons_OrganizationId_ModifiedAt] ON [pos].[return_reasons] ([OrganizationId], [ModifiedAt])"),
            ("IX_return_reasons_Uuid", "return_reasons", "CREATE UNIQUE INDEX [IX_return_reasons_Uuid] ON [pos].[return_reasons] ([Uuid])"),
            ("IX_store_credit_notes_IssuedStoreId", "store_credit_notes",
                "CREATE INDEX [IX_store_credit_notes_IssuedStoreId] ON [pos].[store_credit_notes] ([IssuedStoreId])"),
            ("IX_store_credit_notes_OrganizationId_CustomerUuid", "store_credit_notes",
                "CREATE INDEX [IX_store_credit_notes_OrganizationId_CustomerUuid] ON [pos].[store_credit_notes] ([OrganizationId], [CustomerUuid])"),
            ("IX_store_credit_notes_OrganizationId_LocalId", "store_credit_notes",
                "CREATE UNIQUE INDEX [IX_store_credit_notes_OrganizationId_LocalId] ON [pos].[store_credit_notes] ([OrganizationId], [LocalId]) WHERE [LocalId] IS NOT NULL"),
            ("IX_store_credit_notes_OrganizationId_Number", "store_credit_notes",
                "CREATE UNIQUE INDEX [IX_store_credit_notes_OrganizationId_Number] ON [pos].[store_credit_notes] ([OrganizationId], [Number])"),
            ("IX_store_credit_notes_OrganizationId_OfflineNumber", "store_credit_notes",
                "CREATE INDEX [IX_store_credit_notes_OrganizationId_OfflineNumber] ON [pos].[store_credit_notes] ([OrganizationId], [OfflineNumber]) WHERE [OfflineNumber] IS NOT NULL"),
            ("IX_store_credit_notes_OrganizationId_Status_ExpiresAt", "store_credit_notes",
                "CREATE INDEX [IX_store_credit_notes_OrganizationId_Status_ExpiresAt] ON [pos].[store_credit_notes] ([OrganizationId], [Status], [ExpiresAt])"),
            ("IX_store_credit_notes_ReturnOrderId", "store_credit_notes",
                "CREATE INDEX [IX_store_credit_notes_ReturnOrderId] ON [pos].[store_credit_notes] ([ReturnOrderId])"),
            ("IX_store_credit_notes_Uuid", "store_credit_notes", "CREATE UNIQUE INDEX [IX_store_credit_notes_Uuid] ON [pos].[store_credit_notes] ([Uuid])"),
            ("IX_store_credit_transactions_CreditNoteId", "store_credit_transactions",
                "CREATE INDEX [IX_store_credit_transactions_CreditNoteId] ON [pos].[store_credit_transactions] ([CreditNoteId])"),
            ("IX_store_credit_transactions_OrderId", "store_credit_transactions",
                "CREATE INDEX [IX_store_credit_transactions_OrderId] ON [pos].[store_credit_transactions] ([OrderId])"),
            ("IX_store_credit_transactions_OrganizationId", "store_credit_transactions",
                "CREATE INDEX [IX_store_credit_transactions_OrganizationId] ON [pos].[store_credit_transactions] ([OrganizationId])"),
            ("IX_store_credit_transactions_Uuid", "store_credit_transactions",
                "CREATE UNIQUE INDEX [IX_store_credit_transactions_Uuid] ON [pos].[store_credit_transactions] ([Uuid])")
        ];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF OBJECT_ID(N'pos.FK_orders_return_reasons_ReturnReasonId', N'F') IS NOT NULL
                    ALTER TABLE [pos].[orders] DROP CONSTRAINT [FK_orders_return_reasons_ReturnReasonId];
                IF OBJECT_ID(N'pos.FK_payments_store_credit_notes_CreditNoteId', N'F') IS NOT NULL
                    ALTER TABLE [pos].[payments] DROP CONSTRAINT [FK_payments_store_credit_notes_CreditNoteId];
                IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_payments_CreditNoteId' AND object_id = OBJECT_ID(N'pos.payments'))
                    DROP INDEX [IX_payments_CreditNoteId] ON [pos].[payments];
                IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_orders_OrganizationId_IsCrossStore_CreatedAt' AND object_id = OBJECT_ID(N'pos.orders'))
                    DROP INDEX [IX_orders_OrganizationId_IsCrossStore_CreatedAt] ON [pos].[orders];
                IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_orders_ReturnReasonId' AND object_id = OBJECT_ID(N'pos.orders'))
                    DROP INDEX [IX_orders_ReturnReasonId] ON [pos].[orders];
                IF OBJECT_ID(N'pos.store_credit_transactions', N'U') IS NOT NULL DROP TABLE [pos].[store_credit_transactions];
                IF OBJECT_ID(N'pos.store_credit_notes', N'U') IS NOT NULL DROP TABLE [pos].[store_credit_notes];
                IF OBJECT_ID(N'pos.return_reasons', N'U') IS NOT NULL DROP TABLE [pos].[return_reasons];
                IF OBJECT_ID(N'pos.return_policies', N'U') IS NOT NULL DROP TABLE [pos].[return_policies];
                IF OBJECT_ID(N'pos.return_authorizations', N'U') IS NOT NULL DROP TABLE [pos].[return_authorizations];
                IF COL_LENGTH(N'pos.payments', N'CreditNoteId') IS NOT NULL ALTER TABLE [pos].[payments] DROP COLUMN [CreditNoteId];
                IF COL_LENGTH(N'pos.orders', N'AuthorizedAt') IS NOT NULL ALTER TABLE [pos].[orders] DROP COLUMN [AuthorizedAt];
                IF COL_LENGTH(N'pos.orders', N'AuthorizedByUserId') IS NOT NULL ALTER TABLE [pos].[orders] DROP COLUMN [AuthorizedByUserId];
                IF COL_LENGTH(N'pos.orders', N'OriginalStoreId') IS NOT NULL ALTER TABLE [pos].[orders] DROP COLUMN [OriginalStoreId];
                IF COL_LENGTH(N'pos.orders', N'ReturnReasonId') IS NOT NULL ALTER TABLE [pos].[orders] DROP COLUMN [ReturnReasonId];
                IF COL_LENGTH(N'pos.orders', N'StoreCreditNoteId') IS NOT NULL ALTER TABLE [pos].[orders] DROP COLUMN [StoreCreditNoteId];
                IF COL_LENGTH(N'pos.orders', N'IsCrossStore') IS NOT NULL
                BEGIN
                    IF OBJECT_ID(N'pos.DF_orders_IsCrossStore', N'D') IS NOT NULL ALTER TABLE [pos].[orders] DROP CONSTRAINT [DF_orders_IsCrossStore];
                    ALTER TABLE [pos].[orders] DROP COLUMN [IsCrossStore];
                END
                IF COL_LENGTH(N'pos.orders', N'RestockingFeeAmount') IS NOT NULL
                BEGIN
                    IF OBJECT_ID(N'pos.DF_orders_RestockingFeeAmount', N'D') IS NOT NULL ALTER TABLE [pos].[orders] DROP CONSTRAINT [DF_orders_RestockingFeeAmount];
                    ALTER TABLE [pos].[orders] DROP COLUMN [RestockingFeeAmount];
                END
                IF COL_LENGTH(N'pos.shifts', N'TotalStoreCreditIssued') IS NOT NULL
                BEGIN
                    IF OBJECT_ID(N'pos.DF_shifts_TotalStoreCreditIssued', N'D') IS NOT NULL ALTER TABLE [pos].[shifts] DROP CONSTRAINT [DF_shifts_TotalStoreCreditIssued];
                    ALTER TABLE [pos].[shifts] DROP COLUMN [TotalStoreCreditIssued];
                END
                IF COL_LENGTH(N'pos.shifts', N'TotalStoreCreditRedeemed') IS NOT NULL
                BEGIN
                    IF OBJECT_ID(N'pos.DF_shifts_TotalStoreCreditRedeemed', N'D') IS NOT NULL ALTER TABLE [pos].[shifts] DROP CONSTRAINT [DF_shifts_TotalStoreCreditRedeemed];
                    ALTER TABLE [pos].[shifts] DROP COLUMN [TotalStoreCreditRedeemed];
                END
                """);
        }
    }
}

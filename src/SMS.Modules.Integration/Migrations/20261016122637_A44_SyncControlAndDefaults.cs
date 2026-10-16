using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Integration.Migrations
{
    /// <summary>
    /// A44 (docs/financial-posting/ADDENDUM-44-ANALYSIS.md): sync control per document type (D-14), provider defaults and
    /// per-document overrides (D-15), and QuickBooks records imported as remote-only link candidates (D-17).
    /// <para>
    /// Existing connections get one sync-control row per document type: the five <c>AutoPush*</c> switches are copied
    /// as they are, POS sales / returns follow "Sync POS sales" (and sales invoices, which POS sales always needed) and
    /// are held while off, and the four types SCM never sent before — customer payments, credit notes, bill payments, vendor
    /// credits — start off without hold, so nothing an accountant may have keyed by hand is doubled on deploy; an admin turns
    /// them on under Sync control (D-61).
    /// The <c>AutoPush*</c> columns stay (R-1). Guarded: the API migrates the shared database on start, and a re-run or a
    /// drifted database must not fail — and must never overwrite a row an admin has changed since.
    /// </para>
    /// </summary>
    public partial class A44_SyncControlAndDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(CreateTablesSql);
            migrationBuilder.Sql(CopyAutoPushSql);
        }

        /// <summary>Every table and index, each behind its own existence check.</summary>
        internal const string CreateTablesSql = """
            IF OBJECT_ID(N'integration.SyncControlConfigs', N'U') IS NULL
            BEGIN
                CREATE TABLE [integration].[SyncControlConfigs] (
                    [Id] int NOT NULL IDENTITY(1, 1),
                    [OrganizationId] uniqueidentifier NOT NULL,
                    [ConnectionId] int NOT NULL,
                    [DocumentType] nvarchar(50) NOT NULL,
                    [IsEnabled] bit NOT NULL,
                    [HoldWhenDisabled] bit NOT NULL,
                    [DisplayLabel] nvarchar(100) NOT NULL,
                    [ModifiedBy] int NULL,
                    [ModifiedAt] datetime2 NULL,
                    CONSTRAINT [PK_SyncControlConfigs] PRIMARY KEY ([Id])
                );
            END;
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SyncControlConfigs_ConnectionId_DocumentType'
                           AND object_id = OBJECT_ID(N'integration.SyncControlConfigs'))
                CREATE UNIQUE INDEX [IX_SyncControlConfigs_ConnectionId_DocumentType]
                    ON [integration].[SyncControlConfigs] ([ConnectionId], [DocumentType]);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SyncControlConfigs_OrganizationId'
                           AND object_id = OBJECT_ID(N'integration.SyncControlConfigs'))
                CREATE INDEX [IX_SyncControlConfigs_OrganizationId]
                    ON [integration].[SyncControlConfigs] ([OrganizationId]);

            IF OBJECT_ID(N'integration.ProviderDefaults', N'U') IS NULL
            BEGIN
                CREATE TABLE [integration].[ProviderDefaults] (
                    [Id] int NOT NULL IDENTITY(1, 1),
                    [OrganizationId] uniqueidentifier NOT NULL,
                    [ConnectionId] int NOT NULL,
                    [SettingKey] nvarchar(100) NOT NULL,
                    [SettingValue] nvarchar(200) NOT NULL,
                    [Description] nvarchar(300) NULL,
                    [ModifiedBy] int NULL,
                    [ModifiedAt] datetime2 NULL,
                    CONSTRAINT [PK_ProviderDefaults] PRIMARY KEY ([Id])
                );
            END;
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ProviderDefaults_ConnectionId_SettingKey'
                           AND object_id = OBJECT_ID(N'integration.ProviderDefaults'))
                CREATE UNIQUE INDEX [IX_ProviderDefaults_ConnectionId_SettingKey]
                    ON [integration].[ProviderDefaults] ([ConnectionId], [SettingKey]);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ProviderDefaults_OrganizationId'
                           AND object_id = OBJECT_ID(N'integration.ProviderDefaults'))
                CREATE INDEX [IX_ProviderDefaults_OrganizationId]
                    ON [integration].[ProviderDefaults] ([OrganizationId]);

            IF OBJECT_ID(N'integration.DocumentOverrides', N'U') IS NULL
            BEGIN
                CREATE TABLE [integration].[DocumentOverrides] (
                    [Id] int NOT NULL IDENTITY(1, 1),
                    [OrganizationId] uniqueidentifier NOT NULL,
                    [ConnectionId] int NOT NULL,
                    [DocumentType] nvarchar(50) NOT NULL,
                    [SettingKey] nvarchar(100) NOT NULL,
                    [SettingValue] nvarchar(200) NOT NULL,
                    [ModifiedBy] int NULL,
                    [ModifiedAt] datetime2 NULL,
                    CONSTRAINT [PK_DocumentOverrides] PRIMARY KEY ([Id])
                );
            END;
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_DocumentOverrides_ConnectionId_DocumentType_SettingKey'
                           AND object_id = OBJECT_ID(N'integration.DocumentOverrides'))
                CREATE UNIQUE INDEX [IX_DocumentOverrides_ConnectionId_DocumentType_SettingKey]
                    ON [integration].[DocumentOverrides] ([ConnectionId], [DocumentType], [SettingKey]);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_DocumentOverrides_OrganizationId'
                           AND object_id = OBJECT_ID(N'integration.DocumentOverrides'))
                CREATE INDEX [IX_DocumentOverrides_OrganizationId]
                    ON [integration].[DocumentOverrides] ([OrganizationId]);

            IF OBJECT_ID(N'integration.RemoteEntities', N'U') IS NULL
            BEGIN
                CREATE TABLE [integration].[RemoteEntities] (
                    [Id] int NOT NULL IDENTITY(1, 1),
                    [OrganizationId] uniqueidentifier NOT NULL,
                    [ConnectionId] int NOT NULL,
                    [Kind] nvarchar(20) NOT NULL,
                    [RemoteId] nvarchar(50) NOT NULL,
                    [Name] nvarchar(200) NOT NULL,
                    [CompanyName] nvarchar(200) NULL,
                    [Email] nvarchar(200) NULL,
                    [TaxId] nvarchar(50) NULL,
                    [AccountNumber] nvarchar(100) NULL,
                    [Sku] nvarchar(100) NULL,
                    [CurrencyCode] nvarchar(10) NULL,
                    [Active] bit NOT NULL,
                    [ImportedAt] datetime2 NOT NULL,
                    CONSTRAINT [PK_RemoteEntities] PRIMARY KEY ([Id])
                );
            END;
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RemoteEntities_ConnectionId_Kind_RemoteId'
                           AND object_id = OBJECT_ID(N'integration.RemoteEntities'))
                CREATE UNIQUE INDEX [IX_RemoteEntities_ConnectionId_Kind_RemoteId]
                    ON [integration].[RemoteEntities] ([ConnectionId], [Kind], [RemoteId]);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RemoteEntities_OrganizationId'
                           AND object_id = OBJECT_ID(N'integration.RemoteEntities'))
                CREATE INDEX [IX_RemoteEntities_OrganizationId]
                    ON [integration].[RemoteEntities] ([OrganizationId]);
            """;

        /// <summary>
        /// D-14 / D-61 / D-62: one row per existing connection and document type, only where none exists yet. Dynamic SQL
        /// because a Settings table from before QBI_AutoPushPosSales has no such column (POS sales are then off), and a
        /// static reference to it would not compile. Kept as a constant so the tests run exactly this on LocalDB.
        /// </summary>
        internal const string CopyAutoPushSql = """
            IF OBJECT_ID(N'integration.Settings', N'U') IS NOT NULL
               AND OBJECT_ID(N'integration.SyncControlConfigs', N'U') IS NOT NULL
            BEGIN
                DECLARE @pos nvarchar(200) =
                    CASE WHEN COL_LENGTH(N'integration.Settings', N'AutoPushPosSales') IS NULL
                         THEN N'CAST(0 AS bit)'
                         ELSE N'CAST(CASE WHEN s.[AutoPushPosSales] = 1 AND s.[AutoPushSalesInvoices] = 1 THEN 1 ELSE 0 END AS bit)' END;
                DECLARE @sql nvarchar(max) = N'
                    INSERT INTO [integration].[SyncControlConfigs]
                        ([OrganizationId], [ConnectionId], [DocumentType], [IsEnabled], [HoldWhenDisabled], [DisplayLabel], [ModifiedBy], [ModifiedAt])
                    SELECT s.[OrganizationId], s.[ConnectionId], v.[DocumentType], v.[IsEnabled], v.[HoldWhenDisabled], v.[DisplayLabel], NULL, SYSUTCDATETIME()
                    FROM [integration].[Settings] s
                    CROSS APPLY (VALUES
                        (N''Customer'',     s.[AutoPushCustomers],     CAST(0 AS bit), N''Customers''),
                        (N''Vendor'',       s.[AutoPushVendors],       CAST(0 AS bit), N''Vendors''),
                        (N''Item'',         s.[AutoPushItems],         CAST(0 AS bit), N''Items''),
                        (N''SalesInvoice'', s.[AutoPushSalesInvoices], CAST(0 AS bit), N''Sales invoices''),
                        (N''CreditNote'',   CAST(0 AS bit),            CAST(0 AS bit), N''Credit notes''),
                        (N''Payment'',      CAST(0 AS bit),            CAST(0 AS bit), N''Customer payments''),
                        (N''POSSale'',      ' + @pos + N',             s.[AutoPushSalesInvoices], N''POS sales''),
                        (N''POSReturn'',    ' + @pos + N',             s.[AutoPushSalesInvoices], N''POS returns''),
                        (N''PurchaseBill'', s.[AutoPushBills],         CAST(0 AS bit), N''Purchase bills''),
                        (N''BillPayment'',  CAST(0 AS bit),            CAST(0 AS bit), N''Bill payments''),
                        (N''VendorCredit'', CAST(0 AS bit),            CAST(0 AS bit), N''Vendor credits'')
                    ) v([DocumentType], [IsEnabled], [HoldWhenDisabled], [DisplayLabel])
                    WHERE NOT EXISTS (SELECT 1 FROM [integration].[SyncControlConfigs] c
                                      WHERE c.[ConnectionId] = s.[ConnectionId] AND c.[DocumentType] = v.[DocumentType]);';
                EXEC sp_executesql @sql;
            END;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF OBJECT_ID(N'integration.RemoteEntities', N'U') IS NOT NULL
                    DROP TABLE [integration].[RemoteEntities];
                IF OBJECT_ID(N'integration.DocumentOverrides', N'U') IS NOT NULL
                    DROP TABLE [integration].[DocumentOverrides];
                IF OBJECT_ID(N'integration.ProviderDefaults', N'U') IS NOT NULL
                    DROP TABLE [integration].[ProviderDefaults];
                IF OBJECT_ID(N'integration.SyncControlConfigs', N'U') IS NOT NULL
                    DROP TABLE [integration].[SyncControlConfigs];
                """);
        }
    }
}

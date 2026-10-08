using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Finance.Migrations
{
    /// <summary>
    /// A35 P1-01 / P1-06 / D-15 (docs/multi-currency/ADDENDUM-35-ANALYSIS.md §4, Finance 1st owner CUR) — additive and
    /// idempotent: creates <c>finance.org_currencies</c>, <c>finance.currency_rates</c> and <c>finance.exchange_differences</c>
    /// with their CHECK constraints and indexes. Every API start migrates the drifted shared database, so every statement is
    /// guarded (table / index existence) and a re-run is a no-op. One statement per <c>Sql()</c> call.
    /// <para>
    /// No rows here: org currencies, the rate currency's SYSTEM 1.0 row and the conversion of the frozen legacy
    /// <c>finance.exchange_rates</c> into ranges (D-3) are done by the C# startup backfill / provisioning handler
    /// (<c>CurrencyBootstrapper</c>), which can resolve each organization's rate currency through Tenancy.
    /// </para>
    /// </summary>
    public partial class A35_CurrencyCore : Migration
    {
        private static readonly (string Table, string Name, string Sql)[] Indexes =
        {
            ("currency_rates", "IX_currency_rates_Active",
                "CREATE INDEX [IX_currency_rates_Active] ON [finance].[currency_rates] ([OrganizationId], [EffectiveTo]) WHERE [EffectiveTo] = '9999-12-31';"),
            ("currency_rates", "IX_currency_rates_Lookup",
                "CREATE INDEX [IX_currency_rates_Lookup] ON [finance].[currency_rates] ([OrganizationId], [CurrencyId], [EffectiveFrom], [EffectiveTo]);"),
            ("currency_rates", "IX_currency_rates_OrganizationId_CurrencyId_EffectiveFrom",
                "CREATE UNIQUE INDEX [IX_currency_rates_OrganizationId_CurrencyId_EffectiveFrom] ON [finance].[currency_rates] ([OrganizationId], [CurrencyId], [EffectiveFrom]);"),
            ("currency_rates", "IX_currency_rates_Uuid",
                "CREATE UNIQUE INDEX [IX_currency_rates_Uuid] ON [finance].[currency_rates] ([Uuid]);"),
            ("exchange_differences", "IX_exchange_differences_OrganizationId_DocumentType_DocumentId",
                "CREATE INDEX [IX_exchange_differences_OrganizationId_DocumentType_DocumentId] ON [finance].[exchange_differences] ([OrganizationId], [DocumentType], [DocumentId]);"),
            ("exchange_differences", "IX_exchange_differences_OrganizationId_Kind_PostedAt",
                "CREATE INDEX [IX_exchange_differences_OrganizationId_Kind_PostedAt] ON [finance].[exchange_differences] ([OrganizationId], [Kind], [PostedAt]);"),
            ("exchange_differences", "IX_exchange_differences_OrganizationId_PaymentType_PaymentId",
                "CREATE INDEX [IX_exchange_differences_OrganizationId_PaymentType_PaymentId] ON [finance].[exchange_differences] ([OrganizationId], [PaymentType], [PaymentId]);"),
            ("exchange_differences", "IX_exchange_differences_Uuid",
                "CREATE UNIQUE INDEX [IX_exchange_differences_Uuid] ON [finance].[exchange_differences] ([Uuid]);"),
            ("exchange_differences", "UX_exchange_differences_Revaluation",
                "CREATE UNIQUE INDEX [UX_exchange_differences_Revaluation] ON [finance].[exchange_differences] ([OrganizationId], [DocumentType], [DocumentId], [RevaluationDate]) WHERE [Kind] = 'UNREALIZED' AND [RevaluationDate] IS NOT NULL;"),
            ("org_currencies", "IX_org_currencies_OrganizationId_Code",
                "CREATE UNIQUE INDEX [IX_org_currencies_OrganizationId_Code] ON [finance].[org_currencies] ([OrganizationId], [Code]);"),
            ("org_currencies", "IX_org_currencies_OrganizationId_CurrencyId",
                "CREATE UNIQUE INDEX [IX_org_currencies_OrganizationId_CurrencyId] ON [finance].[org_currencies] ([OrganizationId], [CurrencyId]);"),
            ("org_currencies", "IX_org_currencies_OrganizationId_IsActive_DisplayOrder",
                "CREATE INDEX [IX_org_currencies_OrganizationId_IsActive_DisplayOrder] ON [finance].[org_currencies] ([OrganizationId], [IsActive], [DisplayOrder]);"),
            ("org_currencies", "IX_org_currencies_Uuid",
                "CREATE UNIQUE INDEX [IX_org_currencies_Uuid] ON [finance].[org_currencies] ([Uuid]);"),
        };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "IF OBJECT_ID(N'finance.currency_rates', N'U') IS NULL " +
                "CREATE TABLE [finance].[currency_rates] (" +
                "[Id] int IDENTITY(1,1) NOT NULL, " +
                "[Uuid] uniqueidentifier NOT NULL, " +
                "[OrganizationId] uniqueidentifier NOT NULL, " +
                "[CurrencyId] uniqueidentifier NOT NULL, " +
                "[CurrencyCode] nvarchar(3) NOT NULL, " +
                "[Rate] decimal(18,10) NOT NULL, " +
                "[InverseRate] decimal(18,10) NOT NULL, " +
                "[EffectiveFrom] date NOT NULL, " +
                "[EffectiveTo] date NOT NULL, " +
                "[Source] nvarchar(30) NOT NULL CONSTRAINT [DF_currency_rates_Source] DEFAULT N'MANUAL', " +
                "[Notes] nvarchar(200) NULL, " +
                "[CreatedBy] int NULL, " +
                "[CreatedDate] datetime2 NOT NULL, " +
                "[ModifiedBy] int NULL, " +
                "[ModifiedDate] datetime2 NULL, " +
                "CONSTRAINT [PK_currency_rates] PRIMARY KEY ([Id]), " +
                "CONSTRAINT [CK_currency_rates_DateRange] CHECK ([EffectiveTo] >= [EffectiveFrom]), " +
                "CONSTRAINT [CK_currency_rates_InversePositive] CHECK ([InverseRate] > 0), " +
                "CONSTRAINT [CK_currency_rates_RatePositive] CHECK ([Rate] > 0), " +
                "CONSTRAINT [CK_currency_rates_Source] CHECK ([Source] IN ('MANUAL','API_SBP','API_ECB','API_OPENEXCHANGE','API_FOREX','SYSTEM')));");

            migrationBuilder.Sql(
                "IF OBJECT_ID(N'finance.exchange_differences', N'U') IS NULL " +
                "CREATE TABLE [finance].[exchange_differences] (" +
                "[Id] int IDENTITY(1,1) NOT NULL, " +
                "[Uuid] uniqueidentifier NOT NULL, " +
                "[OrganizationId] uniqueidentifier NOT NULL, " +
                "[Kind] nvarchar(12) NOT NULL, " +
                "[Side] nvarchar(12) NOT NULL, " +
                "[DocumentType] nvarchar(30) NOT NULL, " +
                "[DocumentId] int NOT NULL, " +
                "[DocumentUuid] uniqueidentifier NOT NULL, " +
                "[DocumentNo] nvarchar(50) NULL, " +
                "[PaymentType] nvarchar(30) NULL, " +
                "[PaymentId] int NULL, " +
                "[PaymentUuid] uniqueidentifier NULL, " +
                "[PaymentNo] nvarchar(50) NULL, " +
                "[AllocationId] int NULL, " +
                "[PartnerId] uniqueidentifier NULL, " +
                "[CurrencyId] uniqueidentifier NULL, " +
                "[CurrencyCode] nvarchar(10) NOT NULL, " +
                "[AmountCurrency] decimal(18,4) NOT NULL, " +
                "[BookedRate] decimal(18,10) NOT NULL, " +
                "[SettlementRate] decimal(18,10) NOT NULL, " +
                "[BaseCurrencyId] uniqueidentifier NULL, " +
                "[BaseCurrencyCode] nvarchar(10) NOT NULL, " +
                "[BookedAmountBase] decimal(18,4) NOT NULL, " +
                "[SettledAmountBase] decimal(18,4) NOT NULL, " +
                "[DifferenceBase] decimal(18,4) NOT NULL, " +
                "[AccountCode] nvarchar(20) NULL, " +
                "[PostedAt] datetime2 NOT NULL, " +
                "[RevaluationDate] date NULL, " +
                "[CreatedBy] int NULL, " +
                "[CreatedDate] datetime2 NOT NULL, " +
                "CONSTRAINT [PK_exchange_differences] PRIMARY KEY ([Id]), " +
                "CONSTRAINT [CK_exchange_differences_Kind] CHECK ([Kind] IN ('REALIZED','UNREALIZED')), " +
                "CONSTRAINT [CK_exchange_differences_Side] CHECK ([Side] IN ('RECEIVABLE','PAYABLE')));");

            migrationBuilder.Sql(
                "IF OBJECT_ID(N'finance.org_currencies', N'U') IS NULL " +
                "CREATE TABLE [finance].[org_currencies] (" +
                "[Id] int IDENTITY(1,1) NOT NULL, " +
                "[Uuid] uniqueidentifier NOT NULL, " +
                "[OrganizationId] uniqueidentifier NOT NULL, " +
                "[CurrencyId] uniqueidentifier NOT NULL, " +
                "[Code] nvarchar(3) NOT NULL, " +
                "[Name] nvarchar(60) NOT NULL, " +
                "[Symbol] nvarchar(5) NOT NULL, " +
                "[DecimalPlaces] int NOT NULL CONSTRAINT [DF_org_currencies_DecimalPlaces] DEFAULT 2, " +
                "[Rounding] decimal(18,6) NOT NULL CONSTRAINT [DF_org_currencies_Rounding] DEFAULT 0.01, " +
                "[SymbolPosition] nvarchar(6) NOT NULL CONSTRAINT [DF_org_currencies_SymbolPosition] DEFAULT N'before', " +
                "[IsActive] bit NOT NULL CONSTRAINT [DF_org_currencies_IsActive] DEFAULT CAST(1 AS bit), " +
                "[DisplayOrder] int NOT NULL, " +
                "[CreatedBy] int NULL, " +
                "[CreatedDate] datetime2 NOT NULL, " +
                "[ModifiedBy] int NULL, " +
                "[ModifiedDate] datetime2 NULL, " +
                "CONSTRAINT [PK_org_currencies] PRIMARY KEY ([Id]), " +
                "CONSTRAINT [CK_org_currencies_Code] CHECK (LEN([Code]) = 3 AND [Code] = UPPER([Code]) COLLATE Latin1_General_CS_AS), " +
                "CONSTRAINT [CK_org_currencies_DecimalPlaces] CHECK ([DecimalPlaces] BETWEEN 0 AND 3), " +
                "CONSTRAINT [CK_org_currencies_Rounding] CHECK ([Rounding] > 0), " +
                "CONSTRAINT [CK_org_currencies_SymbolPosition] CHECK ([SymbolPosition] IN ('before','after')));");

            foreach (var (table, name, sql) in Indexes)
                migrationBuilder.Sql(
                    $"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{name}' AND object_id = OBJECT_ID(N'finance.{table}')) " + sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF OBJECT_ID(N'finance.currency_rates', N'U') IS NOT NULL DROP TABLE [finance].[currency_rates];");
            migrationBuilder.Sql("IF OBJECT_ID(N'finance.exchange_differences', N'U') IS NOT NULL DROP TABLE [finance].[exchange_differences];");
            migrationBuilder.Sql("IF OBJECT_ID(N'finance.org_currencies', N'U') IS NOT NULL DROP TABLE [finance].[org_currencies];");
        }
    }
}

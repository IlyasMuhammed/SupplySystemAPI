using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Tenancy.Migrations
{
    /// <summary>
    /// A35 P2-01 (M3) + P1-13 settings part (docs/multi-currency/ADDENDUM-35-ANALYSIS.md D-7) — additive and idempotent:
    /// <list type="bullet">
    /// <item><c>tenant.organization_currency_settings</c>, 1:1 with Organizations (PK = OrganizationId, cascade FK): sale,
    /// purchase, service base and rate currency (Guid ids into the global lookups.Currencies, D-1/D-2), four free-text GL
    /// account codes, audit columns;</item>
    /// <item>backfill: one row per organization that has a <c>BaseCurrency</c> and no row, every currency = that base
    /// (D-7: a row's sale base always equals Organization.BaseCurrency). An organization without a base gets no row: the
    /// service reads a missing row as PKR for the new per-domain reads while the legacy read keeps answering null.</item>
    /// </list>
    /// Every API start migrates the drifted shared database, so each statement is guarded and a re-run is a no-op.
    /// </summary>
    public partial class A35_OrganizationCurrencySettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "IF OBJECT_ID(N'tenant.organization_currency_settings', N'U') IS NULL " +
                "CREATE TABLE [tenant].[organization_currency_settings] (" +
                "[OrganizationId] uniqueidentifier NOT NULL, " +
                "[SaleBaseCurrencyId] uniqueidentifier NOT NULL, " +
                "[PurchaseBaseCurrencyId] uniqueidentifier NOT NULL, " +
                "[ServiceBaseCurrencyId] uniqueidentifier NOT NULL, " +
                "[RateCurrencyId] uniqueidentifier NOT NULL, " +
                "[ExchangeGainAccountCode] nvarchar(20) NULL, " +
                "[ExchangeLossAccountCode] nvarchar(20) NULL, " +
                "[UnrealizedGainAccountCode] nvarchar(20) NULL, " +
                "[UnrealizedLossAccountCode] nvarchar(20) NULL, " +
                "[CreatedAt] datetime2 NOT NULL, " +
                "[UpdatedAt] datetime2 NOT NULL, " +
                "[ModifiedBy] int NULL, " +
                "CONSTRAINT [PK_organization_currency_settings] PRIMARY KEY ([OrganizationId]), " +
                "CONSTRAINT [FK_organization_currency_settings_Organizations] FOREIGN KEY ([OrganizationId]) " +
                "REFERENCES [tenant].[Organizations] ([Id]) ON DELETE CASCADE);");

            migrationBuilder.Sql(
                "INSERT INTO [tenant].[organization_currency_settings] " +
                "([OrganizationId], [SaleBaseCurrencyId], [PurchaseBaseCurrencyId], [ServiceBaseCurrencyId], [RateCurrencyId], " +
                "[CreatedAt], [UpdatedAt]) " +
                "SELECT o.[Id], o.[BaseCurrency], o.[BaseCurrency], o.[BaseCurrency], o.[BaseCurrency], SYSUTCDATETIME(), SYSUTCDATETIME() " +
                "FROM [tenant].[Organizations] o " +
                "WHERE o.[BaseCurrency] IS NOT NULL AND NOT EXISTS (SELECT 1 FROM [tenant].[organization_currency_settings] s " +
                "WHERE s.[OrganizationId] = o.[Id]);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "IF OBJECT_ID(N'tenant.organization_currency_settings', N'U') IS NOT NULL " +
                "DROP TABLE [tenant].[organization_currency_settings];");
        }
    }
}

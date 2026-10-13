using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Finance.Migrations
{
    /// <summary>
    /// A40 (TFIN, D-7) — finance.org_settings: one row per organization holding the opening balance date
    /// (<c>api/finance/opening-balance-date</c>, <c>IOpeningBalanceDateProvider</c>).
    /// <para>
    /// <b>Guarded and idempotent</b> (every API start migrates the shared, drifted database): the table and its index are
    /// created only when missing. Additive only.
    /// </para>
    /// </summary>
    public partial class A40_FinanceOrgSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"IF OBJECT_ID('finance.org_settings') IS NULL
CREATE TABLE [finance].[org_settings] (
    [Id] int NOT NULL IDENTITY(1,1),
    [OrganizationId] uniqueidentifier NOT NULL,
    [OpeningBalanceDate] date NULL,
    [CreatedBy] int NOT NULL,
    [CreatedDate] datetime2 NOT NULL,
    [ModifiedBy] int NULL,
    [ModifiedDate] datetime2 NULL,
    CONSTRAINT [PK_org_settings] PRIMARY KEY ([Id])
);");

            migrationBuilder.Sql(@"IF OBJECT_ID('finance.org_settings') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_org_settings_OrganizationId' AND object_id = OBJECT_ID('finance.org_settings'))
CREATE UNIQUE INDEX [IX_org_settings_OrganizationId] ON [finance].[org_settings] ([OrganizationId]);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "org_settings",
                schema: "finance");
        }
    }
}

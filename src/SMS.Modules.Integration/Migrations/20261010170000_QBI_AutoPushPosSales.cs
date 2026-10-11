using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Integration.Migrations
{
    /// <inheritdoc />
    public partial class QBI_AutoPushPosSales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // "Sync POS sales" — off for every existing organization (POS payments are not synced, so the admin opts in).
            // Guarded: the API migrates the shared database on start, and a re-run or a drifted database must not fail.
            migrationBuilder.Sql("""
                IF COL_LENGTH(N'integration.Settings', N'AutoPushPosSales') IS NULL
                    ALTER TABLE [integration].[Settings]
                        ADD [AutoPushPosSales] bit NOT NULL
                        CONSTRAINT [DF_Settings_AutoPushPosSales] DEFAULT CAST(0 AS bit);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF OBJECT_ID(N'integration.DF_Settings_AutoPushPosSales', N'D') IS NOT NULL
                    ALTER TABLE [integration].[Settings] DROP CONSTRAINT [DF_Settings_AutoPushPosSales];
                IF COL_LENGTH(N'integration.Settings', N'AutoPushPosSales') IS NOT NULL
                    ALTER TABLE [integration].[Settings] DROP COLUMN [AutoPushPosSales];
                """);
        }
    }
}

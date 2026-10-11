using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Auth.Data.Migrations
{
    /// <summary>
    /// A38 D-4 (docs/pos/ADDENDUM-38-ANALYSIS.md) — auth.UserAccounts.PosPinHash, the cashier's POS PIN hashed with the
    /// password hasher. Additive and guarded: every API start migrates the drifted shared database.
    /// </summary>
    public partial class A38_UserPosPinHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF COL_LENGTH('auth.UserAccounts', 'PosPinHash') IS NULL
                    ALTER TABLE [auth].[UserAccounts] ADD [PosPinHash] nvarchar(256) NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF COL_LENGTH('auth.UserAccounts', 'PosPinHash') IS NOT NULL
                    ALTER TABLE [auth].[UserAccounts] DROP COLUMN [PosPinHash];
                """);
        }
    }
}

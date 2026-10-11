using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Pos.Migrations
{
    /// <inheritdoc />
    public partial class A38_PosOrderSyncedBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A38 (POSB) — the user who pushed an order whose cashier was named by the device. Guarded: startup migrates
            // shared databases, a re-run must not fail.
            migrationBuilder.Sql("""
                IF COL_LENGTH(N'pos.orders', N'SyncedByUserId') IS NULL
                    ALTER TABLE [pos].[orders] ADD [SyncedByUserId] int NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF COL_LENGTH(N'pos.orders', N'SyncedByUserId') IS NOT NULL
                    ALTER TABLE [pos].[orders] DROP COLUMN [SyncedByUserId];
                """);
        }
    }
}

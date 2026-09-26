using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Inventory.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryItemRowVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Guarded: the shared dev database has drifted from the migration history and every
            // API start replays this. SQL Server fills a rowversion column for existing rows itself.
            migrationBuilder.Sql("""
                IF COL_LENGTH('inventory.InventoryItems', 'RowVersion') IS NULL
                    ALTER TABLE [inventory].[InventoryItems] ADD [RowVersion] rowversion NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF COL_LENGTH('inventory.InventoryItems', 'RowVersion') IS NOT NULL
                    ALTER TABLE [inventory].[InventoryItems] DROP COLUMN [RowVersion];
                """);
        }
    }
}

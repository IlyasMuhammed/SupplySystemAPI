using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Inventory.Migrations
{
    /// <inheritdoc />
    public partial class A37_SyncModifiedAtAndServiceCategory : Migration
    {
        // A37 D-16 — the four synced tables and the column ModifiedAt is backfilled from.
        private static readonly (string Table, string From)[] Synced =
        [
            ("Products",          "COALESCE([UpdatedDate], [CreatedDate])"),
            ("ProductVariants",   "[CreatedDate]"),
            ("ProductCategories", "[CreatedDate]"),
            ("Warehouses",        "[CreatedDate]")
        ];

        /// <inheritdoc />
        // A37 D-10 — inventory.Products.ServiceCategory (nullable code) and RequiresSiteVisit (bit, 0 for every existing
        // product). D-16 — ModifiedAt on Products / ProductVariants / ProductCategories / Warehouses, backfilled from the
        // created/updated dates, plus an (OrganizationId, ModifiedAt) index each for the sync delta. Additive and guarded:
        // every API start replays migrations on the drifted shared database. Backfills and indexes run through EXEC (a
        // batch that adds a column cannot name it in a statement compiled with it) and only in the batch that adds the column;
        // rows written outside EF default to SYSUTCDATETIME(). No triggers (EF's OUTPUT clause).
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF COL_LENGTH('inventory.Products', 'ServiceCategory') IS NULL
                    ALTER TABLE [inventory].[Products] ADD [ServiceCategory] nvarchar(30) NULL;
                IF COL_LENGTH('inventory.Products', 'RequiresSiteVisit') IS NULL
                    ALTER TABLE [inventory].[Products] ADD [RequiresSiteVisit] bit NOT NULL
                        CONSTRAINT [DF_Products_RequiresSiteVisit] DEFAULT 0;
                """);

            foreach (var (table, from) in Synced)
                migrationBuilder.Sql($"""
                    IF COL_LENGTH('inventory.{table}', 'ModifiedAt') IS NULL
                    BEGIN
                        ALTER TABLE [inventory].[{table}] ADD [ModifiedAt] datetime2 NOT NULL
                            CONSTRAINT [DF_{table}_ModifiedAt] DEFAULT SYSUTCDATETIME();
                        EXEC('UPDATE [inventory].[{table}] SET [ModifiedAt] = {from};');
                    END
                    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_{table}_OrganizationId_ModifiedAt'
                                   AND object_id = OBJECT_ID('inventory.{table}'))
                        EXEC('CREATE INDEX [IX_{table}_OrganizationId_ModifiedAt] ON [inventory].[{table}] ([OrganizationId], [ModifiedAt]);');
                    """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var (table, _) in Synced)
                migrationBuilder.Sql($"""
                    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_{table}_OrganizationId_ModifiedAt'
                               AND object_id = OBJECT_ID('inventory.{table}'))
                        DROP INDEX [IX_{table}_OrganizationId_ModifiedAt] ON [inventory].[{table}];
                    IF OBJECT_ID('inventory.DF_{table}_ModifiedAt', 'D') IS NOT NULL
                        ALTER TABLE [inventory].[{table}] DROP CONSTRAINT [DF_{table}_ModifiedAt];
                    IF COL_LENGTH('inventory.{table}', 'ModifiedAt') IS NOT NULL
                        ALTER TABLE [inventory].[{table}] DROP COLUMN [ModifiedAt];
                    """);
            migrationBuilder.Sql("""
                IF OBJECT_ID('inventory.DF_Products_RequiresSiteVisit', 'D') IS NOT NULL
                    ALTER TABLE [inventory].[Products] DROP CONSTRAINT [DF_Products_RequiresSiteVisit];
                IF COL_LENGTH('inventory.Products', 'RequiresSiteVisit') IS NOT NULL
                    ALTER TABLE [inventory].[Products] DROP COLUMN [RequiresSiteVisit];
                IF COL_LENGTH('inventory.Products', 'ServiceCategory') IS NOT NULL
                    ALTER TABLE [inventory].[Products] DROP COLUMN [ServiceCategory];
                """);
        }
    }
}
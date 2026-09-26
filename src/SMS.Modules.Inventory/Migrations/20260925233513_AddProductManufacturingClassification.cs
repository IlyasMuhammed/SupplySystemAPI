using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Inventory.Migrations
{
    /// <inheritdoc />
    public partial class AddProductManufacturingClassification : Migration
    {
        /// <inheritdoc />
        // A30 M1 + M2 in one: the DEFAULT constraints give every pre-existing product the §6.6
        // defaults (a purchased, saleable, stockable stock item), so no separate UPDATE is
        // needed. Guarded because the shared dev database replays every migration on API start.
        // One statement per Sql() call: a CREATE INDEX on a column added earlier in the same
        // batch fails at compile time, before the ALTER has run.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF COL_LENGTH('inventory.Products', 'ProductType') IS NULL
                    ALTER TABLE [inventory].[Products] ADD [ProductType] nvarchar(20) NOT NULL
                        CONSTRAINT [DF_Products_ProductType] DEFAULT 'STOCK_ITEM';
                """);
            migrationBuilder.Sql("""
                IF COL_LENGTH('inventory.Products', 'SupplyMethod') IS NULL
                    ALTER TABLE [inventory].[Products] ADD [SupplyMethod] nvarchar(20) NOT NULL
                        CONSTRAINT [DF_Products_SupplyMethod] DEFAULT 'PURCHASE';
                """);
            migrationBuilder.Sql("""
                IF COL_LENGTH('inventory.Products', 'IsSaleable') IS NULL
                    ALTER TABLE [inventory].[Products] ADD [IsSaleable] bit NOT NULL
                        CONSTRAINT [DF_Products_IsSaleable] DEFAULT 1;
                """);
            migrationBuilder.Sql("""
                IF COL_LENGTH('inventory.Products', 'IsPurchasable') IS NULL
                    ALTER TABLE [inventory].[Products] ADD [IsPurchasable] bit NOT NULL
                        CONSTRAINT [DF_Products_IsPurchasable] DEFAULT 1;
                """);
            migrationBuilder.Sql("""
                IF COL_LENGTH('inventory.Products', 'IsStockable') IS NULL
                    ALTER TABLE [inventory].[Products] ADD [IsStockable] bit NOT NULL
                        CONSTRAINT [DF_Products_IsStockable] DEFAULT 1;
                """);
            migrationBuilder.Sql("""
                IF COL_LENGTH('inventory.Products', 'IsManufacturable') IS NULL
                    ALTER TABLE [inventory].[Products] ADD [IsManufacturable] bit NOT NULL
                        CONSTRAINT [DF_Products_IsManufacturable] DEFAULT 0;
                """);
            migrationBuilder.Sql("""
                IF COL_LENGTH('inventory.Products', 'DefaultProductionWarehouseId') IS NULL
                    ALTER TABLE [inventory].[Products] ADD [DefaultProductionWarehouseId] int NULL;
                """);
            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes
                               WHERE name = 'IX_Products_DefaultProductionWarehouseId' AND object_id = OBJECT_ID('inventory.Products'))
                    CREATE INDEX [IX_Products_DefaultProductionWarehouseId] ON [inventory].[Products] ([DefaultProductionWarehouseId]);
                """);
            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes
                               WHERE name = 'IX_Products_OrganizationId_SupplyMethod' AND object_id = OBJECT_ID('inventory.Products'))
                    CREATE INDEX [IX_Products_OrganizationId_SupplyMethod] ON [inventory].[Products] ([OrganizationId], [SupplyMethod]);
                """);
            migrationBuilder.Sql("""
                IF OBJECT_ID('inventory.FK_Products_Warehouses_DefaultProductionWarehouseId', 'F') IS NULL
                    ALTER TABLE [inventory].[Products] ADD CONSTRAINT [FK_Products_Warehouses_DefaultProductionWarehouseId]
                        FOREIGN KEY ([DefaultProductionWarehouseId]) REFERENCES [inventory].[Warehouses] ([Id]) ON DELETE SET NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF OBJECT_ID('inventory.FK_Products_Warehouses_DefaultProductionWarehouseId', 'F') IS NOT NULL
                    ALTER TABLE [inventory].[Products] DROP CONSTRAINT [FK_Products_Warehouses_DefaultProductionWarehouseId];
                """);
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM sys.indexes
                           WHERE name = 'IX_Products_DefaultProductionWarehouseId' AND object_id = OBJECT_ID('inventory.Products'))
                    DROP INDEX [IX_Products_DefaultProductionWarehouseId] ON [inventory].[Products];
                """);
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM sys.indexes
                           WHERE name = 'IX_Products_OrganizationId_SupplyMethod' AND object_id = OBJECT_ID('inventory.Products'))
                    DROP INDEX [IX_Products_OrganizationId_SupplyMethod] ON [inventory].[Products];
                """);

            foreach (var (column, constraint) in new[]
            {
                ("DefaultProductionWarehouseId", (string?)null),
                ("IsManufacturable", "DF_Products_IsManufacturable"),
                ("IsStockable",      "DF_Products_IsStockable"),
                ("IsPurchasable",    "DF_Products_IsPurchasable"),
                ("IsSaleable",       "DF_Products_IsSaleable"),
                ("SupplyMethod",     "DF_Products_SupplyMethod"),
                ("ProductType",      "DF_Products_ProductType")
            })
            {
                if (constraint is not null)
                    migrationBuilder.Sql($"""
                        IF OBJECT_ID('inventory.{constraint}', 'D') IS NOT NULL
                            ALTER TABLE [inventory].[Products] DROP CONSTRAINT [{constraint}];
                        """);
                migrationBuilder.Sql($"""
                    IF COL_LENGTH('inventory.Products', '{column}') IS NOT NULL
                        ALTER TABLE [inventory].[Products] DROP COLUMN [{column}];
                    """);
            }
        }
    }
}

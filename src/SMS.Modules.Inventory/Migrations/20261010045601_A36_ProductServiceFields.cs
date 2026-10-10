using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Inventory.Migrations
{
    /// <inheritdoc />
    public partial class A36_ProductServiceFields : Migration
    {
        /// <inheritdoc />
        // A36 M1 (A36-P1-01, D-2) — service configuration on inventory.Products. Additive only: two nullable codes, a
        // nullable duration and two bits that default to 0 for every existing product (no service is configured yet).
        // Guarded, because every API start replays migrations on the drifted shared database.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF COL_LENGTH('inventory.Products', 'ServiceInvoicingPolicy') IS NULL
                    ALTER TABLE [inventory].[Products] ADD [ServiceInvoicingPolicy] nvarchar(30) NULL;
                IF COL_LENGTH('inventory.Products', 'ServiceBillingModel') IS NULL
                    ALTER TABLE [inventory].[Products] ADD [ServiceBillingModel] nvarchar(30) NULL;
                IF COL_LENGTH('inventory.Products', 'EstimatedDurationHours') IS NULL
                    ALTER TABLE [inventory].[Products] ADD [EstimatedDurationHours] decimal(18,4) NULL;
                IF COL_LENGTH('inventory.Products', 'HasServiceBom') IS NULL
                    ALTER TABLE [inventory].[Products] ADD [HasServiceBom] bit NOT NULL
                        CONSTRAINT [DF_Products_HasServiceBom] DEFAULT 0;
                IF COL_LENGTH('inventory.Products', 'IsSubcontractable') IS NULL
                    ALTER TABLE [inventory].[Products] ADD [IsSubcontractable] bit NOT NULL
                        CONSTRAINT [DF_Products_IsSubcontractable] DEFAULT 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var (column, constraint) in new[]
            {
                ("IsSubcontractable",      "DF_Products_IsSubcontractable"),
                ("HasServiceBom",          "DF_Products_HasServiceBom"),
                ("EstimatedDurationHours", (string)null),
                ("ServiceBillingModel",    (string)null),
                ("ServiceInvoicingPolicy", (string)null)
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

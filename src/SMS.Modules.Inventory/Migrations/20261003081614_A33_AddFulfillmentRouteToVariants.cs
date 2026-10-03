using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Inventory.Migrations
{
    /// <inheritdoc />
    public partial class A33_AddFulfillmentRouteToVariants : Migration
    {
        /// <inheritdoc />
        // A33 M2 (A33-PB-01) — each variant's default fulfillment route: a scalar uuid of a route in Logistics' own
        // DbContext, so no foreign key across contexts (validated in VariantFulfillmentRouteService). Additive and
        // nullable: every existing variant starts with no route and inherits the organization's default (D-6).
        // Guarded, because every API start replays migrations on the drifted shared database; one statement per
        // Sql() call, because a CREATE INDEX on a column added earlier in the same batch fails at compile time.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF COL_LENGTH('inventory.ProductVariants', 'FulfillmentRouteUuid') IS NULL
                    ALTER TABLE [inventory].[ProductVariants] ADD [FulfillmentRouteUuid] uniqueidentifier NULL;
                """);
            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes
                               WHERE name = 'IX_ProductVariants_OrganizationId_FulfillmentRouteUuid'
                                 AND object_id = OBJECT_ID('inventory.ProductVariants'))
                    CREATE INDEX [IX_ProductVariants_OrganizationId_FulfillmentRouteUuid]
                        ON [inventory].[ProductVariants] ([OrganizationId], [FulfillmentRouteUuid])
                        WHERE [FulfillmentRouteUuid] IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM sys.indexes
                           WHERE name = 'IX_ProductVariants_OrganizationId_FulfillmentRouteUuid'
                             AND object_id = OBJECT_ID('inventory.ProductVariants'))
                    DROP INDEX [IX_ProductVariants_OrganizationId_FulfillmentRouteUuid] ON [inventory].[ProductVariants];
                """);
            migrationBuilder.Sql("""
                IF COL_LENGTH('inventory.ProductVariants', 'FulfillmentRouteUuid') IS NOT NULL
                    ALTER TABLE [inventory].[ProductVariants] DROP COLUMN [FulfillmentRouteUuid];
                """);
        }
    }
}

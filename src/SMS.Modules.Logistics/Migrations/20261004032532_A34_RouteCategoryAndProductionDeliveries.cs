using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Logistics.Migrations
{
    /// <summary>
    /// A34 PA-01 / PE-04 (docs/route-classification/ADDENDUM-34-ANALYSIS.md §5) — additive and idempotent:
    /// <list type="bullet">
    /// <item><c>fulfillment_routes.RouteCategory nvarchar(20) NOT NULL DEFAULT 'STOCK'</c> (every existing route becomes a
    /// stock route, R-2) with <c>CK_fulfillment_routes_RouteCategory</c> (STOCK, MANUFACTURE and the reserved BUY,
    /// DROPSHIP — D-7);</item>
    /// <item><c>delivery_orders.ProductionOrderUuid uniqueidentifier NULL</c> + a filtered index (D-29).</item>
    /// </list>
    /// Every API start migrates the drifted shared database, so each statement is guarded (column / constraint / index
    /// existence) and a re-run on an already-migrated database is a no-op. One statement per <c>Sql()</c> call: a
    /// statement that names a column added earlier in this migration must compile after that column exists (A33
    /// precedent). No seed rows: the two MFG routes come from the C# seeder, its provisioning handler and the startup
    /// backfill (D-9).
    /// </summary>
    public partial class A34_RouteCategoryAndProductionDeliveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "IF COL_LENGTH(N'logistics.fulfillment_routes', N'RouteCategory') IS NULL " +
                "ALTER TABLE [logistics].[fulfillment_routes] ADD [RouteCategory] nvarchar(20) NOT NULL " +
                "CONSTRAINT [DF_fulfillment_routes_RouteCategory] DEFAULT N'STOCK';");

            migrationBuilder.Sql(
                "IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_fulfillment_routes_RouteCategory' " +
                "AND parent_object_id = OBJECT_ID(N'logistics.fulfillment_routes')) " +
                "ALTER TABLE [logistics].[fulfillment_routes] ADD CONSTRAINT [CK_fulfillment_routes_RouteCategory] " +
                "CHECK ([RouteCategory] IN ('STOCK','MANUFACTURE','BUY','DROPSHIP'));");

            migrationBuilder.Sql(
                "IF COL_LENGTH(N'logistics.delivery_orders', N'ProductionOrderUuid') IS NULL " +
                "ALTER TABLE [logistics].[delivery_orders] ADD [ProductionOrderUuid] uniqueidentifier NULL;");

            migrationBuilder.Sql(
                "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_delivery_orders_OrganizationId_ProductionOrderUuid' " +
                "AND object_id = OBJECT_ID(N'logistics.delivery_orders')) " +
                "CREATE INDEX [IX_delivery_orders_OrganizationId_ProductionOrderUuid] ON [logistics].[delivery_orders] " +
                "([OrganizationId], [ProductionOrderUuid]) WHERE [ProductionOrderUuid] IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_delivery_orders_OrganizationId_ProductionOrderUuid' " +
                "AND object_id = OBJECT_ID(N'logistics.delivery_orders')) " +
                "DROP INDEX [IX_delivery_orders_OrganizationId_ProductionOrderUuid] ON [logistics].[delivery_orders];");

            migrationBuilder.Sql(
                "IF COL_LENGTH(N'logistics.delivery_orders', N'ProductionOrderUuid') IS NOT NULL " +
                "ALTER TABLE [logistics].[delivery_orders] DROP COLUMN [ProductionOrderUuid];");

            migrationBuilder.Sql(
                "IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_fulfillment_routes_RouteCategory' " +
                "AND parent_object_id = OBJECT_ID(N'logistics.fulfillment_routes')) " +
                "ALTER TABLE [logistics].[fulfillment_routes] DROP CONSTRAINT [CK_fulfillment_routes_RouteCategory];");

            // The default constraint may carry another name on a drifted database: find it by column.
            migrationBuilder.Sql(
                "DECLARE @df sysname = (SELECT dc.name FROM sys.default_constraints dc " +
                "JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id " +
                "WHERE dc.parent_object_id = OBJECT_ID(N'logistics.fulfillment_routes') AND c.name = N'RouteCategory'); " +
                "IF @df IS NOT NULL EXEC(N'ALTER TABLE [logistics].[fulfillment_routes] DROP CONSTRAINT [' + @df + N']');");

            migrationBuilder.Sql(
                "IF COL_LENGTH(N'logistics.fulfillment_routes', N'RouteCategory') IS NOT NULL " +
                "ALTER TABLE [logistics].[fulfillment_routes] DROP COLUMN [RouteCategory];");
        }
    }
}

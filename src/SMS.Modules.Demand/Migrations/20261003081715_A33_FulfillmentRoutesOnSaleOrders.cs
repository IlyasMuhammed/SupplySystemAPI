using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Demand.Migrations
{
    /// <summary>
    /// A33 (Fulfillment Routes), DEM. Additive only:
    /// <list type="bullet">
    /// <item>sale_order_lines: FulfillmentRouteUuid (override while DRAFT, the D-16 snapshot after confirm),
    /// FulfillmentRouteCode and RouteSource (D-16), plus a filtered index for "is this route in use" (L-7);</item>
    /// <item>sale_orders: DeliveryCreationPendingSince (D-12 sweep selection, REV-01), plus a filtered index;</item>
    /// <item>sale_order_config: AutoCreateDeliveriesOnConfirm, default 1 (D-1) so existing organizations get it on.</item>
    /// </list>
    /// Every API start migrates the shared database, which has drifted from the migration history, so each step is
    /// guarded and the migration can run against a database that already has some or all of it.
    /// </summary>
    public partial class A33_FulfillmentRoutesOnSaleOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF COL_LENGTH(N'demand.sale_orders', N'DeliveryCreationPendingSince') IS NULL
    ALTER TABLE [demand].[sale_orders] ADD [DeliveryCreationPendingSince] datetime2 NULL;");

            migrationBuilder.Sql(@"
IF COL_LENGTH(N'demand.sale_order_lines', N'FulfillmentRouteCode') IS NULL
    ALTER TABLE [demand].[sale_order_lines] ADD [FulfillmentRouteCode] nvarchar(30) NULL;");

            migrationBuilder.Sql(@"
IF COL_LENGTH(N'demand.sale_order_lines', N'FulfillmentRouteUuid') IS NULL
    ALTER TABLE [demand].[sale_order_lines] ADD [FulfillmentRouteUuid] uniqueidentifier NULL;");

            migrationBuilder.Sql(@"
IF COL_LENGTH(N'demand.sale_order_lines', N'RouteSource') IS NULL
    ALTER TABLE [demand].[sale_order_lines] ADD [RouteSource] nvarchar(20) NULL;");

            migrationBuilder.Sql(@"
IF COL_LENGTH(N'demand.sale_order_config', N'AutoCreateDeliveriesOnConfirm') IS NULL
    ALTER TABLE [demand].[sale_order_config] ADD [AutoCreateDeliveriesOnConfirm] bit NOT NULL
        CONSTRAINT [DF_sale_order_config_AutoCreateDeliveriesOnConfirm] DEFAULT CAST(1 AS bit);");

            // Dynamic SQL: a filtered index naming a column added earlier in the same batch would not compile.
            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_sale_orders_DeliveryCreationPendingSince' AND object_id = OBJECT_ID(N'demand.sale_orders'))
    EXEC(N'CREATE INDEX [IX_sale_orders_DeliveryCreationPendingSince] ON [demand].[sale_orders] ([DeliveryCreationPendingSince]) WHERE [DeliveryCreationPendingSince] IS NOT NULL');");

            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_sale_order_lines_FulfillmentRouteUuid' AND object_id = OBJECT_ID(N'demand.sale_order_lines'))
    EXEC(N'CREATE INDEX [IX_sale_order_lines_FulfillmentRouteUuid] ON [demand].[sale_order_lines] ([FulfillmentRouteUuid]) WHERE [FulfillmentRouteUuid] IS NOT NULL');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_sale_orders_DeliveryCreationPendingSince' AND object_id = OBJECT_ID(N'demand.sale_orders'))
    DROP INDEX [IX_sale_orders_DeliveryCreationPendingSince] ON [demand].[sale_orders];
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_sale_order_lines_FulfillmentRouteUuid' AND object_id = OBJECT_ID(N'demand.sale_order_lines'))
    DROP INDEX [IX_sale_order_lines_FulfillmentRouteUuid] ON [demand].[sale_order_lines];");

            migrationBuilder.Sql(@"
IF COL_LENGTH(N'demand.sale_orders', N'DeliveryCreationPendingSince') IS NOT NULL
    ALTER TABLE [demand].[sale_orders] DROP COLUMN [DeliveryCreationPendingSince];
IF COL_LENGTH(N'demand.sale_order_lines', N'FulfillmentRouteCode') IS NOT NULL
    ALTER TABLE [demand].[sale_order_lines] DROP COLUMN [FulfillmentRouteCode];
IF COL_LENGTH(N'demand.sale_order_lines', N'FulfillmentRouteUuid') IS NOT NULL
    ALTER TABLE [demand].[sale_order_lines] DROP COLUMN [FulfillmentRouteUuid];
IF COL_LENGTH(N'demand.sale_order_lines', N'RouteSource') IS NOT NULL
    ALTER TABLE [demand].[sale_order_lines] DROP COLUMN [RouteSource];");

            // The default constraint goes first, whatever it is called (EF would name it differently from this migration).
            migrationBuilder.Sql(@"
DECLARE @df sysname = (
    SELECT dc.name FROM sys.default_constraints dc
    JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
    WHERE dc.parent_object_id = OBJECT_ID(N'demand.sale_order_config') AND c.name = N'AutoCreateDeliveriesOnConfirm');
IF @df IS NOT NULL EXEC(N'ALTER TABLE [demand].[sale_order_config] DROP CONSTRAINT [' + @df + N']');
IF COL_LENGTH(N'demand.sale_order_config', N'AutoCreateDeliveriesOnConfirm') IS NOT NULL
    ALTER TABLE [demand].[sale_order_config] DROP COLUMN [AutoCreateDeliveriesOnConfirm];");
        }
    }
}

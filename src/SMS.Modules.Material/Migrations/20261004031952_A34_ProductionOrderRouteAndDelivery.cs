using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Material.Migrations
{
    /// <summary>
    /// A34 PE-01 (D-18, D-20), MFG. Additive only, on material.production_orders:
    /// <list type="bullet">
    /// <item>FulfillmentRouteUuid: the sale order line's route, set only by the make-to-order path, plus a filtered index
    /// (D-8 "open production orders" usage, and what marks a PO as make-to-order);</item>
    /// <item>DeliveryOrderUuid + DeliveryNumber: the latest delivery created from the PO (C6);</item>
    /// <item>DeliveryCreationPendingSince: the C6 sweep's selection, plus a filtered index.</item>
    /// </list>
    /// <b>No backfill</b> (C-4): existing SALES_ORDER-sourced POs are A30 make-to-shortage and already have their A33
    /// deliveries; copying a route onto them would make each one create a second delivery at completion.
    /// Every API start migrates the shared database, which has drifted from the migration history, so each step is guarded
    /// and the migration can run against a database that already has some or all of it.
    /// </summary>
    public partial class A34_ProductionOrderRouteAndDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF COL_LENGTH(N'material.production_orders', N'FulfillmentRouteUuid') IS NULL
    ALTER TABLE [material].[production_orders] ADD [FulfillmentRouteUuid] uniqueidentifier NULL;");

            migrationBuilder.Sql(@"
IF COL_LENGTH(N'material.production_orders', N'DeliveryOrderUuid') IS NULL
    ALTER TABLE [material].[production_orders] ADD [DeliveryOrderUuid] uniqueidentifier NULL;");

            migrationBuilder.Sql(@"
IF COL_LENGTH(N'material.production_orders', N'DeliveryNumber') IS NULL
    ALTER TABLE [material].[production_orders] ADD [DeliveryNumber] nvarchar(50) NULL;");

            migrationBuilder.Sql(@"
IF COL_LENGTH(N'material.production_orders', N'DeliveryCreationPendingSince') IS NULL
    ALTER TABLE [material].[production_orders] ADD [DeliveryCreationPendingSince] datetime2 NULL;");

            // Dynamic SQL, one statement per Sql() call: a filtered index naming a column added earlier in the same batch
            // would not compile (A33 precedent).
            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_production_orders_FulfillmentRouteUuid' AND object_id = OBJECT_ID(N'material.production_orders'))
    EXEC(N'CREATE INDEX [IX_production_orders_FulfillmentRouteUuid] ON [material].[production_orders] ([FulfillmentRouteUuid]) WHERE [FulfillmentRouteUuid] IS NOT NULL');");

            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_production_orders_DeliveryCreationPendingSince' AND object_id = OBJECT_ID(N'material.production_orders'))
    EXEC(N'CREATE INDEX [IX_production_orders_DeliveryCreationPendingSince] ON [material].[production_orders] ([DeliveryCreationPendingSince]) WHERE [DeliveryCreationPendingSince] IS NOT NULL');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_production_orders_FulfillmentRouteUuid' AND object_id = OBJECT_ID(N'material.production_orders'))
    DROP INDEX [IX_production_orders_FulfillmentRouteUuid] ON [material].[production_orders];
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_production_orders_DeliveryCreationPendingSince' AND object_id = OBJECT_ID(N'material.production_orders'))
    DROP INDEX [IX_production_orders_DeliveryCreationPendingSince] ON [material].[production_orders];");

            migrationBuilder.Sql(@"
IF COL_LENGTH(N'material.production_orders', N'FulfillmentRouteUuid') IS NOT NULL
    ALTER TABLE [material].[production_orders] DROP COLUMN [FulfillmentRouteUuid];
IF COL_LENGTH(N'material.production_orders', N'DeliveryOrderUuid') IS NOT NULL
    ALTER TABLE [material].[production_orders] DROP COLUMN [DeliveryOrderUuid];
IF COL_LENGTH(N'material.production_orders', N'DeliveryNumber') IS NOT NULL
    ALTER TABLE [material].[production_orders] DROP COLUMN [DeliveryNumber];
IF COL_LENGTH(N'material.production_orders', N'DeliveryCreationPendingSince') IS NOT NULL
    ALTER TABLE [material].[production_orders] DROP COLUMN [DeliveryCreationPendingSince];");
        }
    }
}

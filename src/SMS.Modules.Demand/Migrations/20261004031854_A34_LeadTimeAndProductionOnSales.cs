using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Demand.Migrations
{
    /// <summary>
    /// A34 (Route Classification, Lead Time &amp; Production → Delivery), DEM, PC-01. Additive only, no data written:
    /// <list type="bullet">
    /// <item>sale_inquiry_lines, sale_quotation_lines, sale_order_lines: CalculatedLeadTimeDays, CalculatedDeliveryDate
    /// (date) and LeadTimeCalculatedAt (D-15). The effective date (manual ?? calculated) is derived in C#, not a
    /// computed column (C-13);</item>
    /// <item>sale_order_lines: ManualDeliveryDate (date), FulfillmentRouteCategory (the confirm snapshot) and
    /// ProductionShortfallQty (D-21);</item>
    /// <item>sale_orders: ProductionCreationPendingSince (D-17 sweep selection) plus a filtered index (REV-01 shape).</item>
    /// </list>
    /// MAKE_TO_ORDER is a new FulfillmentMode code; it fits the existing nvarchar(20). Every API start migrates the shared
    /// database, which has drifted from the migration history, so each step is guarded and the migration can run against
    /// a database that already has some or all of it.
    /// </summary>
    public partial class A34_LeadTimeAndProductionOnSales : Migration
    {
        private static readonly (string Table, string Column, string Type)[] Columns =
        [
            ("sale_inquiry_lines",   "CalculatedLeadTimeDays",         "int"),
            ("sale_inquiry_lines",   "CalculatedDeliveryDate",         "date"),
            ("sale_inquiry_lines",   "LeadTimeCalculatedAt",           "datetime2"),
            ("sale_quotation_lines", "CalculatedLeadTimeDays",         "int"),
            ("sale_quotation_lines", "CalculatedDeliveryDate",         "date"),
            ("sale_quotation_lines", "LeadTimeCalculatedAt",           "datetime2"),
            ("sale_order_lines",     "CalculatedLeadTimeDays",         "int"),
            ("sale_order_lines",     "CalculatedDeliveryDate",         "date"),
            ("sale_order_lines",     "LeadTimeCalculatedAt",           "datetime2"),
            ("sale_order_lines",     "ManualDeliveryDate",             "date"),
            ("sale_order_lines",     "FulfillmentRouteCategory",       "nvarchar(20)"),
            ("sale_order_lines",     "ProductionShortfallQty",         "decimal(18,4)"),
            ("sale_orders",          "ProductionCreationPendingSince", "datetime2")
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (table, column, type) in Columns)
                migrationBuilder.Sql($@"
IF COL_LENGTH(N'demand.{table}', N'{column}') IS NULL
    ALTER TABLE [demand].[{table}] ADD [{column}] {type} NULL;");

            // Its own statement, and dynamic SQL: a filtered index naming a column added earlier in the same batch would
            // not compile (A33 precedent).
            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_sale_orders_ProductionCreationPendingSince' AND object_id = OBJECT_ID(N'demand.sale_orders'))
    EXEC(N'CREATE INDEX [IX_sale_orders_ProductionCreationPendingSince] ON [demand].[sale_orders] ([ProductionCreationPendingSince]) WHERE [ProductionCreationPendingSince] IS NOT NULL');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_sale_orders_ProductionCreationPendingSince' AND object_id = OBJECT_ID(N'demand.sale_orders'))
    DROP INDEX [IX_sale_orders_ProductionCreationPendingSince] ON [demand].[sale_orders];");

            foreach (var (table, column, _) in Columns)
                migrationBuilder.Sql($@"
IF COL_LENGTH(N'demand.{table}', N'{column}') IS NOT NULL
    ALTER TABLE [demand].[{table}] DROP COLUMN [{column}];");
        }
    }
}

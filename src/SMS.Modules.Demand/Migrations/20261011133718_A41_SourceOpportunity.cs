using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Demand.Migrations
{
    /// <inheritdoc />
    public partial class A41_SourceOpportunity : Migration
    {
        /// <inheritdoc />
        // A41 M14 / D-13 — guarded and idempotent: every API start runs migrations against the shared database, which may
        // already hold any part of this (see the api-startup note in docs/crm/ADDENDUM-41-ANALYSIS.md).
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "sale_inquiries", "sale_quotations", "sale_orders" })
            {
                migrationBuilder.Sql($@"
IF COL_LENGTH('demand.{table}', 'SourceOpportunityUuid') IS NULL
    ALTER TABLE [demand].[{table}] ADD [SourceOpportunityUuid] uniqueidentifier NULL;");
                migrationBuilder.Sql($@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_{table}_SourceOpportunity' AND object_id = OBJECT_ID('demand.{table}'))
    CREATE INDEX [IX_{table}_SourceOpportunity] ON [demand].[{table}] ([OrganizationId], [SourceOpportunityUuid])
        WHERE [SourceOpportunityUuid] IS NOT NULL;");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_sale_quotations_SourceOpportunity",
                schema: "demand",
                table: "sale_quotations");

            migrationBuilder.DropIndex(
                name: "IX_sale_orders_SourceOpportunity",
                schema: "demand",
                table: "sale_orders");

            migrationBuilder.DropIndex(
                name: "IX_sale_inquiries_SourceOpportunity",
                schema: "demand",
                table: "sale_inquiries");

            migrationBuilder.DropColumn(
                name: "SourceOpportunityUuid",
                schema: "demand",
                table: "sale_quotations");

            migrationBuilder.DropColumn(
                name: "SourceOpportunityUuid",
                schema: "demand",
                table: "sale_orders");

            migrationBuilder.DropColumn(
                name: "SourceOpportunityUuid",
                schema: "demand",
                table: "sale_inquiries");
        }
    }
}

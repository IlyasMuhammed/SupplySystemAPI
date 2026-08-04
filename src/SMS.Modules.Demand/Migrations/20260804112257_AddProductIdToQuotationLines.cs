using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Demand.Migrations
{
    /// <inheritdoc />
    public partial class AddProductIdToQuotationLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ProductId",
                schema: "demand",
                table: "quotation_lines",
                type: "uniqueidentifier",
                nullable: true);

            // ProductId was never captured on QuotationLine before this migration — the frontend
            // was already sending it, but the request DTO/entity silently dropped it, so every
            // line that came from a PR/PO (the vast majority) has a recoverable ProductId sitting
            // on the source line. Backfill by joining back through whichever source UUID is set.
            migrationBuilder.Sql(@"
                UPDATE ql
                SET ql.ProductId = COALESCE(pl.ProductId, pol.ProductUuid)
                FROM demand.quotation_lines ql
                LEFT JOIN demand.pr_lines pl             ON pl.UUID  = ql.SourcePrLineUuid
                LEFT JOIN demand.purchase_order_lines pol ON pol.UUID = ql.SourcePoLineUuid
                WHERE ql.SourcePrLineUuid IS NOT NULL OR ql.SourcePoLineUuid IS NOT NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProductId",
                schema: "demand",
                table: "quotation_lines");
        }
    }
}

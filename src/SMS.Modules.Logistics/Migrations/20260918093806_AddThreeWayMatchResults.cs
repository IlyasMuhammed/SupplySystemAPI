using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Logistics.Migrations
{
    /// <inheritdoc />
    public partial class AddThreeWayMatchResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CarrierInvoiceId",
                schema: "logistics",
                table: "freight_accruals",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "InvoicedAmount",
                schema: "logistics",
                table: "freight_accruals",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MatchedAt",
                schema: "logistics",
                table: "freight_accruals",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "VarianceAmount",
                schema: "logistics",
                table: "freight_accruals",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VarianceNote",
                schema: "logistics",
                table: "freight_accruals",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VarianceReason",
                schema: "logistics",
                table: "freight_accruals",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_freight_accruals_CarrierInvoiceId",
                schema: "logistics",
                table: "freight_accruals",
                column: "CarrierInvoiceId");

            migrationBuilder.AddForeignKey(
                name: "FK_freight_accruals_carrier_invoices_CarrierInvoiceId",
                schema: "logistics",
                table: "freight_accruals",
                column: "CarrierInvoiceId",
                principalSchema: "logistics",
                principalTable: "carrier_invoices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_freight_accruals_carrier_invoices_CarrierInvoiceId",
                schema: "logistics",
                table: "freight_accruals");

            migrationBuilder.DropIndex(
                name: "IX_freight_accruals_CarrierInvoiceId",
                schema: "logistics",
                table: "freight_accruals");

            migrationBuilder.DropColumn(
                name: "CarrierInvoiceId",
                schema: "logistics",
                table: "freight_accruals");

            migrationBuilder.DropColumn(
                name: "InvoicedAmount",
                schema: "logistics",
                table: "freight_accruals");

            migrationBuilder.DropColumn(
                name: "MatchedAt",
                schema: "logistics",
                table: "freight_accruals");

            migrationBuilder.DropColumn(
                name: "VarianceAmount",
                schema: "logistics",
                table: "freight_accruals");

            migrationBuilder.DropColumn(
                name: "VarianceNote",
                schema: "logistics",
                table: "freight_accruals");

            migrationBuilder.DropColumn(
                name: "VarianceReason",
                schema: "logistics",
                table: "freight_accruals");
        }
    }
}

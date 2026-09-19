using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Logistics.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoiceLineMatching : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MatchMethod",
                schema: "logistics",
                table: "carrier_invoice_lines",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MatchNote",
                schema: "logistics",
                table: "carrier_invoice_lines",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MatchStatus",
                schema: "logistics",
                table: "carrier_invoice_lines",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "MatchedAt",
                schema: "logistics",
                table: "carrier_invoice_lines",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MatchedByUser",
                schema: "logistics",
                table: "carrier_invoice_lines",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MatchedConsignmentId",
                schema: "logistics",
                table: "carrier_invoice_lines",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_carrier_invoice_lines_MatchedConsignmentId",
                schema: "logistics",
                table: "carrier_invoice_lines",
                column: "MatchedConsignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_carrier_invoice_lines_OrganizationId_MatchStatus",
                schema: "logistics",
                table: "carrier_invoice_lines",
                columns: new[] { "OrganizationId", "MatchStatus" });

            migrationBuilder.AddForeignKey(
                name: "FK_carrier_invoice_lines_consignments_MatchedConsignmentId",
                schema: "logistics",
                table: "carrier_invoice_lines",
                column: "MatchedConsignmentId",
                principalSchema: "logistics",
                principalTable: "consignments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_carrier_invoice_lines_consignments_MatchedConsignmentId",
                schema: "logistics",
                table: "carrier_invoice_lines");

            migrationBuilder.DropIndex(
                name: "IX_carrier_invoice_lines_MatchedConsignmentId",
                schema: "logistics",
                table: "carrier_invoice_lines");

            migrationBuilder.DropIndex(
                name: "IX_carrier_invoice_lines_OrganizationId_MatchStatus",
                schema: "logistics",
                table: "carrier_invoice_lines");

            migrationBuilder.DropColumn(
                name: "MatchMethod",
                schema: "logistics",
                table: "carrier_invoice_lines");

            migrationBuilder.DropColumn(
                name: "MatchNote",
                schema: "logistics",
                table: "carrier_invoice_lines");

            migrationBuilder.DropColumn(
                name: "MatchStatus",
                schema: "logistics",
                table: "carrier_invoice_lines");

            migrationBuilder.DropColumn(
                name: "MatchedAt",
                schema: "logistics",
                table: "carrier_invoice_lines");

            migrationBuilder.DropColumn(
                name: "MatchedByUser",
                schema: "logistics",
                table: "carrier_invoice_lines");

            migrationBuilder.DropColumn(
                name: "MatchedConsignmentId",
                schema: "logistics",
                table: "carrier_invoice_lines");
        }
    }
}

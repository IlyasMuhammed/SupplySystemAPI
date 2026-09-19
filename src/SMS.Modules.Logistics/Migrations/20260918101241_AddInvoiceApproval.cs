using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Logistics.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoiceApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApprovalNote",
                schema: "logistics",
                table: "carrier_invoices",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ApprovedAmount",
                schema: "logistics",
                table: "carrier_invoices",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ApprovedAt",
                schema: "logistics",
                table: "carrier_invoices",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ApprovedBy",
                schema: "logistics",
                table: "carrier_invoices",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisputeOutcome",
                schema: "logistics",
                table: "carrier_invoices",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DisputeRaisedAt",
                schema: "logistics",
                table: "carrier_invoices",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisputeReason",
                schema: "logistics",
                table: "carrier_invoices",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisputeReference",
                schema: "logistics",
                table: "carrier_invoices",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisputeResolutionNote",
                schema: "logistics",
                table: "carrier_invoices",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DisputeResolvedAt",
                schema: "logistics",
                table: "carrier_invoices",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ExpectedCreditAmount",
                schema: "logistics",
                table: "carrier_invoices",
                type: "decimal(18,2)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApprovalNote",
                schema: "logistics",
                table: "carrier_invoices");

            migrationBuilder.DropColumn(
                name: "ApprovedAmount",
                schema: "logistics",
                table: "carrier_invoices");

            migrationBuilder.DropColumn(
                name: "ApprovedAt",
                schema: "logistics",
                table: "carrier_invoices");

            migrationBuilder.DropColumn(
                name: "ApprovedBy",
                schema: "logistics",
                table: "carrier_invoices");

            migrationBuilder.DropColumn(
                name: "DisputeOutcome",
                schema: "logistics",
                table: "carrier_invoices");

            migrationBuilder.DropColumn(
                name: "DisputeRaisedAt",
                schema: "logistics",
                table: "carrier_invoices");

            migrationBuilder.DropColumn(
                name: "DisputeReason",
                schema: "logistics",
                table: "carrier_invoices");

            migrationBuilder.DropColumn(
                name: "DisputeReference",
                schema: "logistics",
                table: "carrier_invoices");

            migrationBuilder.DropColumn(
                name: "DisputeResolutionNote",
                schema: "logistics",
                table: "carrier_invoices");

            migrationBuilder.DropColumn(
                name: "DisputeResolvedAt",
                schema: "logistics",
                table: "carrier_invoices");

            migrationBuilder.DropColumn(
                name: "ExpectedCreditAmount",
                schema: "logistics",
                table: "carrier_invoices");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Logistics.Migrations
{
    /// <inheritdoc />
    public partial class AddCarrierSupplierLinkAndPostedInvoice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SupplierId",
                schema: "logistics",
                table: "carriers",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PostedAt",
                schema: "logistics",
                table: "carrier_invoices",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PostedInvoiceNumber",
                schema: "logistics",
                table: "carrier_invoices",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PostedInvoiceUuid",
                schema: "logistics",
                table: "carrier_invoices",
                type: "uniqueidentifier",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SupplierId",
                schema: "logistics",
                table: "carriers");

            migrationBuilder.DropColumn(
                name: "PostedAt",
                schema: "logistics",
                table: "carrier_invoices");

            migrationBuilder.DropColumn(
                name: "PostedInvoiceNumber",
                schema: "logistics",
                table: "carrier_invoices");

            migrationBuilder.DropColumn(
                name: "PostedInvoiceUuid",
                schema: "logistics",
                table: "carrier_invoices");
        }
    }
}

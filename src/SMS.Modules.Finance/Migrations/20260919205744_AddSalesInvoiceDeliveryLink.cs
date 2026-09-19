using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Finance.Migrations
{
    /// <inheritdoc />
    public partial class AddSalesInvoiceDeliveryLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeliveryNumber",
                schema: "finance",
                table: "sales_invoices",
                type: "nvarchar(25)",
                maxLength: 25,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeliveryUuid",
                schema: "finance",
                table: "sales_invoices",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_invoices_OrganizationId_DeliveryUuid",
                schema: "finance",
                table: "sales_invoices",
                columns: new[] { "OrganizationId", "DeliveryUuid" },
                unique: true,
                filter: "[DeliveryUuid] IS NOT NULL AND [IsDelete] = 0 AND [Status] <> 'CANCELLED'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_sales_invoices_OrganizationId_DeliveryUuid",
                schema: "finance",
                table: "sales_invoices");

            migrationBuilder.DropColumn(
                name: "DeliveryNumber",
                schema: "finance",
                table: "sales_invoices");

            migrationBuilder.DropColumn(
                name: "DeliveryUuid",
                schema: "finance",
                table: "sales_invoices");
        }
    }
}

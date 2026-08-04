using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Warehouse.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationIdTenantScoping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_supplier_return_orders_ReturnNumber",
                schema: "warehouse",
                table: "supplier_return_orders");

            migrationBuilder.DropIndex(
                name: "IX_grns_GrnNumber",
                schema: "warehouse",
                table: "grns");

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "warehouse",
                table: "supplier_return_orders",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "warehouse",
                table: "supplier_return_order_lines",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "warehouse",
                table: "grns",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "warehouse",
                table: "grn_lines",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.CreateIndex(
                name: "IX_supplier_return_orders_OrganizationId",
                schema: "warehouse",
                table: "supplier_return_orders",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_return_orders_OrganizationId_ReturnNumber",
                schema: "warehouse",
                table: "supplier_return_orders",
                columns: new[] { "OrganizationId", "ReturnNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_return_order_lines_OrganizationId",
                schema: "warehouse",
                table: "supplier_return_order_lines",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_grns_OrganizationId",
                schema: "warehouse",
                table: "grns",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_grns_OrganizationId_GrnNumber",
                schema: "warehouse",
                table: "grns",
                columns: new[] { "OrganizationId", "GrnNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_grn_lines_OrganizationId",
                schema: "warehouse",
                table: "grn_lines",
                column: "OrganizationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_supplier_return_orders_OrganizationId",
                schema: "warehouse",
                table: "supplier_return_orders");

            migrationBuilder.DropIndex(
                name: "IX_supplier_return_orders_OrganizationId_ReturnNumber",
                schema: "warehouse",
                table: "supplier_return_orders");

            migrationBuilder.DropIndex(
                name: "IX_supplier_return_order_lines_OrganizationId",
                schema: "warehouse",
                table: "supplier_return_order_lines");

            migrationBuilder.DropIndex(
                name: "IX_grns_OrganizationId",
                schema: "warehouse",
                table: "grns");

            migrationBuilder.DropIndex(
                name: "IX_grns_OrganizationId_GrnNumber",
                schema: "warehouse",
                table: "grns");

            migrationBuilder.DropIndex(
                name: "IX_grn_lines_OrganizationId",
                schema: "warehouse",
                table: "grn_lines");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "warehouse",
                table: "supplier_return_orders");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "warehouse",
                table: "supplier_return_order_lines");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "warehouse",
                table: "grns");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "warehouse",
                table: "grn_lines");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_return_orders_ReturnNumber",
                schema: "warehouse",
                table: "supplier_return_orders",
                column: "ReturnNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_grns_GrnNumber",
                schema: "warehouse",
                table: "grns",
                column: "GrnNumber",
                unique: true);
        }
    }
}

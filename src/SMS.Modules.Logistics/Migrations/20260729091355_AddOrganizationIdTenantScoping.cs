using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Logistics.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationIdTenantScoping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_shipments_ShipmentNumber",
                schema: "logistics",
                table: "shipments");

            migrationBuilder.DropIndex(
                name: "IX_carriers_Code",
                schema: "logistics",
                table: "carriers");

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "logistics",
                table: "shipments",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "logistics",
                table: "carriers",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("84d0a96d-52d4-4375-9260-357b46fb9d9f"));

            migrationBuilder.CreateIndex(
                name: "IX_shipments_OrganizationId_ShipmentNumber",
                schema: "logistics",
                table: "shipments",
                columns: new[] { "OrganizationId", "ShipmentNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_carriers_OrganizationId_Code",
                schema: "logistics",
                table: "carriers",
                columns: new[] { "OrganizationId", "Code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_shipments_OrganizationId_ShipmentNumber",
                schema: "logistics",
                table: "shipments");

            migrationBuilder.DropIndex(
                name: "IX_carriers_OrganizationId_Code",
                schema: "logistics",
                table: "carriers");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "logistics",
                table: "shipments");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "logistics",
                table: "carriers");

            migrationBuilder.CreateIndex(
                name: "IX_shipments_ShipmentNumber",
                schema: "logistics",
                table: "shipments",
                column: "ShipmentNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_carriers_Code",
                schema: "logistics",
                table: "carriers",
                column: "Code",
                unique: true);
        }
    }
}

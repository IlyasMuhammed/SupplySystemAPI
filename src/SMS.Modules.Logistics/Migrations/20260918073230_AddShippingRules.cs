using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Logistics.Migrations
{
    /// <inheritdoc />
    public partial class AddShippingRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_carrier_services_OrganizationId_CarrierId_ServiceCode",
                schema: "logistics",
                table: "carrier_services");

            migrationBuilder.CreateTable(
                name: "shipping_rules",
                schema: "logistics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    MinChargeableWeightKg = table.Column<decimal>(type: "decimal(18,3)", nullable: true),
                    MaxChargeableWeightKg = table.Column<decimal>(type: "decimal(18,3)", nullable: true),
                    OriginCountryIso = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    OriginPostcodePrefix = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    DestinationCountryIso = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    DestinationPostcodePrefix = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    MinDeclaredValue = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    MaxDeclaredValue = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    AppliesToHazardous = table.Column<bool>(type: "bit", nullable: true),
                    AppliesToCod = table.Column<bool>(type: "bit", nullable: true),
                    CarrierId = table.Column<int>(type: "int", nullable: true),
                    ServiceCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Strategy = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    IsDelete = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<int>(type: "int", nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shipping_rules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_shipping_rules_carriers_CarrierId",
                        column: x => x.CarrierId,
                        principalSchema: "logistics",
                        principalTable: "carriers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_carrier_services_OrganizationId_CarrierId_ServiceCode",
                schema: "logistics",
                table: "carrier_services",
                columns: new[] { "OrganizationId", "CarrierId", "ServiceCode" },
                unique: true,
                filter: "[IsDelete] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_shipping_rules_CarrierId",
                schema: "logistics",
                table: "shipping_rules",
                column: "CarrierId");

            migrationBuilder.CreateIndex(
                name: "IX_shipping_rules_OrganizationId_Priority",
                schema: "logistics",
                table: "shipping_rules",
                columns: new[] { "OrganizationId", "Priority" },
                unique: true,
                filter: "[IsDelete] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_shipping_rules_UUID",
                schema: "logistics",
                table: "shipping_rules",
                column: "UUID",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "shipping_rules",
                schema: "logistics");

            migrationBuilder.DropIndex(
                name: "IX_carrier_services_OrganizationId_CarrierId_ServiceCode",
                schema: "logistics",
                table: "carrier_services");

            migrationBuilder.CreateIndex(
                name: "IX_carrier_services_OrganizationId_CarrierId_ServiceCode",
                schema: "logistics",
                table: "carrier_services",
                columns: new[] { "OrganizationId", "CarrierId", "ServiceCode" },
                unique: true);
        }
    }
}

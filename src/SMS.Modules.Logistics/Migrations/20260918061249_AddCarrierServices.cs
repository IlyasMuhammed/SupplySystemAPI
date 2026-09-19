using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Logistics.Migrations
{
    /// <inheritdoc />
    public partial class AddCarrierServices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "carrier_services",
                schema: "logistics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CarrierId = table.Column<int>(type: "int", nullable: false),
                    ServiceCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ServiceName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    DimDivisor = table.Column<int>(type: "int", nullable: true),
                    MinimumChargeableKg = table.Column<decimal>(type: "decimal(18,3)", nullable: true),
                    MaxWeightKgPerPackage = table.Column<decimal>(type: "decimal(18,3)", nullable: true),
                    MaxLengthCm = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    MaxLengthPlusGirthCm = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    SupportsCod = table.Column<bool>(type: "bit", nullable: false),
                    SupportsHazardous = table.Column<bool>(type: "bit", nullable: false),
                    TransitDays = table.Column<int>(type: "int", nullable: true),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    IsDelete = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<int>(type: "int", nullable: true),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_carrier_services", x => x.Id);
                    table.ForeignKey(
                        name: "FK_carrier_services_carriers_CarrierId",
                        column: x => x.CarrierId,
                        principalSchema: "logistics",
                        principalTable: "carriers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_carrier_services_CarrierId",
                schema: "logistics",
                table: "carrier_services",
                column: "CarrierId");

            migrationBuilder.CreateIndex(
                name: "IX_carrier_services_OrganizationId_CarrierId_IsDefault",
                schema: "logistics",
                table: "carrier_services",
                columns: new[] { "OrganizationId", "CarrierId", "IsDefault" });

            migrationBuilder.CreateIndex(
                name: "IX_carrier_services_OrganizationId_CarrierId_ServiceCode",
                schema: "logistics",
                table: "carrier_services",
                columns: new[] { "OrganizationId", "CarrierId", "ServiceCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_carrier_services_UUID",
                schema: "logistics",
                table: "carrier_services",
                column: "UUID",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "carrier_services",
                schema: "logistics");
        }
    }
}

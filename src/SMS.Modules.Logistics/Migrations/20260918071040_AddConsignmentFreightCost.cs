using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Logistics.Migrations
{
    /// <inheritdoc />
    public partial class AddConsignmentFreightCost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "FreightCost",
                schema: "logistics",
                table: "consignments",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FreightCurrency",
                schema: "logistics",
                table: "consignments",
                type: "nchar(3)",
                fixedLength: true,
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FreightRateNote",
                schema: "logistics",
                table: "consignments",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FreightRateSource",
                schema: "logistics",
                table: "consignments",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FreightRatedAt",
                schema: "logistics",
                table: "consignments",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RatedChargeableWeightKg",
                schema: "logistics",
                table: "consignments",
                type: "decimal(18,3)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RatedServiceCode",
                schema: "logistics",
                table: "consignments",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "consignment_charges",
                schema: "logistics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UUID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConsignmentId = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_consignment_charges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_consignment_charges_consignments_ConsignmentId",
                        column: x => x.ConsignmentId,
                        principalSchema: "logistics",
                        principalTable: "consignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_consignment_charges_ConsignmentId_Code",
                schema: "logistics",
                table: "consignment_charges",
                columns: new[] { "ConsignmentId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_consignment_charges_UUID",
                schema: "logistics",
                table: "consignment_charges",
                column: "UUID",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "consignment_charges",
                schema: "logistics");

            migrationBuilder.DropColumn(
                name: "FreightCost",
                schema: "logistics",
                table: "consignments");

            migrationBuilder.DropColumn(
                name: "FreightCurrency",
                schema: "logistics",
                table: "consignments");

            migrationBuilder.DropColumn(
                name: "FreightRateNote",
                schema: "logistics",
                table: "consignments");

            migrationBuilder.DropColumn(
                name: "FreightRateSource",
                schema: "logistics",
                table: "consignments");

            migrationBuilder.DropColumn(
                name: "FreightRatedAt",
                schema: "logistics",
                table: "consignments");

            migrationBuilder.DropColumn(
                name: "RatedChargeableWeightKg",
                schema: "logistics",
                table: "consignments");

            migrationBuilder.DropColumn(
                name: "RatedServiceCode",
                schema: "logistics",
                table: "consignments");
        }
    }
}

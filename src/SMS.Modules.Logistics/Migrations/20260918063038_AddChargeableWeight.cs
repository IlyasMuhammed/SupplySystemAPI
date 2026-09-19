using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Logistics.Migrations
{
    /// <inheritdoc />
    public partial class AddChargeableWeight : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ChargeableWeightBasis",
                schema: "logistics",
                table: "shipment_packages",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ChargeableWeightKg",
                schema: "logistics",
                table: "shipment_packages",
                type: "decimal(18,3)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DimWeightDivisor",
                schema: "logistics",
                table: "shipment_packages",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "WeightRatedAt",
                schema: "logistics",
                table: "shipment_packages",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "WeightRoundingKg",
                schema: "logistics",
                table: "carrier_services",
                type: "decimal(18,3)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChargeableWeightBasis",
                schema: "logistics",
                table: "shipment_packages");

            migrationBuilder.DropColumn(
                name: "ChargeableWeightKg",
                schema: "logistics",
                table: "shipment_packages");

            migrationBuilder.DropColumn(
                name: "DimWeightDivisor",
                schema: "logistics",
                table: "shipment_packages");

            migrationBuilder.DropColumn(
                name: "WeightRatedAt",
                schema: "logistics",
                table: "shipment_packages");

            migrationBuilder.DropColumn(
                name: "WeightRoundingKg",
                schema: "logistics",
                table: "carrier_services");
        }
    }
}

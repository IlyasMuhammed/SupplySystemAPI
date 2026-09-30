using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Warehouse.Migrations
{
    /// <inheritdoc />
    public partial class AddAllocationRunTrackingToGrn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AllocationRunAt",
                schema: "warehouse",
                table: "grns",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AllocationRunBy",
                schema: "warehouse",
                table: "grns",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllocationRunAt",
                schema: "warehouse",
                table: "grns");

            migrationBuilder.DropColumn(
                name: "AllocationRunBy",
                schema: "warehouse",
                table: "grns");
        }
    }
}

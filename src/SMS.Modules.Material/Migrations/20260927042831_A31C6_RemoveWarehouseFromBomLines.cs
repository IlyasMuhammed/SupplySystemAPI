using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Material.Migrations
{
    /// <inheritdoc />
    public partial class A31C6_RemoveWarehouseFromBomLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WarehouseUuid",
                schema: "material",
                table: "bill_of_materials");

            migrationBuilder.DropColumn(
                name: "WarehouseUuid",
                schema: "material",
                table: "bill_of_material_lines");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "WarehouseUuid",
                schema: "material",
                table: "bill_of_materials",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WarehouseUuid",
                schema: "material",
                table: "bill_of_material_lines",
                type: "uniqueidentifier",
                nullable: true);
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SMS.Modules.Suppliers.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierScoreSnapshotTrend : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ModifiedBy",
                schema: "suppliers",
                table: "SupplierScoreSnapshots",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ModifiedDate",
                schema: "suppliers",
                table: "SupplierScoreSnapshots",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Trend",
                schema: "suppliers",
                table: "SupplierScoreSnapshots",
                type: "nvarchar(12)",
                maxLength: 12,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ModifiedBy",
                schema: "suppliers",
                table: "SupplierScoreSnapshots");

            migrationBuilder.DropColumn(
                name: "ModifiedDate",
                schema: "suppliers",
                table: "SupplierScoreSnapshots");

            migrationBuilder.DropColumn(
                name: "Trend",
                schema: "suppliers",
                table: "SupplierScoreSnapshots");
        }
    }
}
